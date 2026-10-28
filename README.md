# Crawler — job-based web crawler

A small, production-minded web crawler: submit a URL, a worker crawls it breadth-first over RabbitMQ, results (pages, parent→child edges, Domain Link Ratio per page) land in PostgreSQL, and a React UI shows progress, the page tree and job history.

- **Crawl API** — ASP.NET Core 8 minimal API: creates jobs, serves status/tree/history, records cancellations, publishes work through a transactional outbox.
- **Crawl Worker** — .NET 8 hosted services: consumes jobs with manual acks, claims them with an expiring lease, crawls, persists per page in one transaction, retries, dead-letters poison messages, recovers expired leases.
- **PostgreSQL 16**, **RabbitMQ 3.13**, **React 18 + Vite + TanStack Query** served by nginx, plus a deterministic **fixture site** to crawl safely.

Planning documents written before the code: [docs/requirements.md](docs/requirements.md), [docs/architecture.md](docs/architecture.md), [docs/contracts.md](docs/contracts.md), [docs/test-plan.md](docs/test-plan.md), [docs/decisions.md](docs/decisions.md), [docs/implementation-plan.md](docs/implementation-plan.md).

## 1. Architecture

```
 Browser ── React (nginx :3000) ──/api──▶ Crawl API (:8080) ──tx──▶ PostgreSQL ◀──tx── Crawl Worker (:8081 /health)
                                               │ outbox publisher (confirms)                 ▲
                                               ▼                                             │ consume, manual ack, prefetch
                                          RabbitMQ  exchange `crawl` ──▶ queue `crawl.jobs` ──┘
                                                      `crawl.jobs.retry` (TTL → back to crawl.jobs)
                                                      `crawl.dlx` ──▶ `crawl.jobs.dlq`
```

Code layout (one shared assembly, layered by folder — see decision D1):

```
src/Crawler.Core/Domain          entities, JobStateMachine, UrlNormalizer, DomainLinkRatio, UrlSafety (no EF/HTTP/MQ)
src/Crawler.Core/Application     DTOs (API contract), CrawlJobMessage, options, interfaces
src/Crawler.Core/Infrastructure  EF Core DbContext + migrations, JobStateStore (conditional updates), RabbitMQ, outbox, crawling (fetcher, link extractor)
src/Crawler.Api                  endpoints, query/command services, tree builder, startup migration
src/Crawler.Worker               CrawlEngine (BFS), JobConsumer, JobMessageHandler, LeaseRecoveryService, health
frontend/                        React app
tests/                           Core unit tests, API integration tests, Worker integration tests, HTML fixtures
deploy/                          Dockerfiles, nginx config
```

## 2. Technology stack and rationale

| Choice | Why |
|--------|-----|
| .NET 8 minimal APIs + hosted services | Required by the assignment; minimal APIs keep endpoints thin and testable with `WebApplicationFactory`. |
| EF Core 8 + Npgsql, migrations committed | Schema as code; `ExecuteUpdate` gives single-statement conditional transitions. Snake-case naming via `EFCore.NamingConventions`. |
| RabbitMQ.Client 7 | Async API with publisher confirms; durable queues, DLX and TTL-based retry are broker-native. |
| AngleSharp | Robust HTML5 parsing of malformed markup; `<base href>` handling. |
| TanStack Query | Polling with automatic stop on terminal states, cache invalidation, no hand-rolled intervals. |
| Testcontainers | Integration tests run against real PostgreSQL/RabbitMQ locally and in CI instead of mocks. |

Deliberately not used: Kafka, Redis, MediatR, Polly, a UI framework — none would change the outcome at this size.

## 3. Quick start

Prerequisites: Docker Desktop (or Docker Engine + Compose v2).

```bash
docker compose up --build
```

| Service | URL |
|---------|-----|
| UI | http://localhost:3000 |
| API | http://localhost:8080 (health: `/health/live`, `/health/ready`) |
| Worker health | http://localhost:8081/health/ready |
| RabbitMQ management | http://localhost:15672 (guest / guest) |
| Fixture site (safe crawl target) | http://localhost:8090 — submit **`http://fixture-site/`** in the UI (that is its name on the Docker network); `http://fixture-site/slow/` is a rate-limited 21-page variant that takes about a minute to crawl |

Migrations run automatically when the API starts. Stop with `docker compose down`; reset everything including the database volume with `docker compose down -v`. Override ports/credentials by copying `env.example` to `.env` (the repository's tooling forbids committing `.env*` files, hence the name).

Local development without containers for the apps:

```bash
docker compose up postgres rabbitmq fixture-site      # infrastructure only
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"   # if the SDK is a per-user install
dotnet run --project src/Crawler.Api                   # http://localhost:8080
dotnet run --project src/Crawler.Worker                # http://localhost:8081
cd frontend && npm ci && npm run dev                   # http://localhost:5173, proxies /api to :8080
```

In `Development`, `localhost` and `127.0.0.1` are trusted crawl targets so you can crawl `http://localhost:8090/`.

End-to-end smoke test against the running stack (crawl, duplicate delivery, DLQ, cancellation, health):

```bash
python scripts/e2e_smoke.py
```

### Configuration

All settings live in `appsettings.json` and can be overridden with environment variables (`Section__Key`). Shared keys are listed in [docs/contracts.md](docs/contracts.md) §5 (`ConnectionStrings:Postgres`, `RabbitMq:*`, `Crawler:Limits|Http|Security|Worker|Outbox`). API-only keys: `Database:MigrateOnStartup` (default true), `Database:MigrationMaxAttempts` (30) / `MigrationRetryDelaySeconds` (2), `Outbox:Enabled` (true; tests disable the publisher), `Cors:AllowedOrigins` (defaults to the Vite and nginx origins).

## 4. API

Full contract with field lists: [docs/contracts.md](docs/contracts.md). Errors are RFC 7807 `application/problem+json`.

```bash
# create (202 + Location)
curl -s -X POST localhost:8080/api/jobs -H 'content-type: application/json'      -d '{"url":"http://fixture-site/","maxDepth":2}'
# → {"jobId":"3f2c..."}

curl -s localhost:8080/api/jobs/3f2c...            # status, counters, progressPercent
curl -s localhost:8080/api/jobs/3f2c.../tree       # spanning tree with ratio per page
curl -s 'localhost:8080/api/jobs?page=1&pageSize=20'
curl -s -X POST localhost:8080/api/jobs/3f2c.../cancel   # 202 requested / 200 already canceled / 409 terminal / 404
```

`maxDepth` defaults to 2 (max 5); the optional `maxPages` defaults to 200 (max 1000). Both bounds are server configuration (`Crawler:Limits`).

## 5. Job lifecycle

```
Pending ──claim──▶ Running ──frontier empty──▶ Completed
   │                  │  ├──cancellation observed──▶ Canceled
   │                  │  └──unrecoverable / retries exhausted──▶ Failed
   ├──cancel──▶ Canceled              Running ──lease expired──▶ Running (re-claimed by another worker)
   └──poison message──▶ Failed
```

Every transition is one conditional `UPDATE ... WHERE status = <expected> [AND lease_owner = <me>]` (`JobStateStore`). Terminal states therefore cannot be overwritten, two workers cannot own a job at once, and a cancellation that races a completion loses cleanly (the flag stays recorded, the status stays `Completed`).

- **Completed** — the crawl finished within its limits, even if some pages failed (`failedPages > 0`) or the root page itself could not be fetched.
- **Failed** — the job could not be executed (message retries exhausted, invalid job). Page-level HTTP errors never fail a job.
- **Canceled** — a Pending job is canceled immediately by the API; a Running job is canceled by the worker at the next page boundary. Cancellation is idempotent.
- **Truncated** — reported separately from status: the crawl hit `maxPages` while in-scope pages remained.

## 6. Data model and indexes

Tables (`snake_case`, see migration `InitialCreate`):

| Table | Purpose | Key constraints / indexes |
|-------|---------|---------------------------|
| `crawl_jobs` | job state, limits, counters, lease, cancellation flag | `ix (created_at desc, id desc)` history paging; `ix (status, lease_expires_at)` lease recovery sweep |
| `crawl_pages` | one row per page that received a crawl slot | **`unique (job_id, normalized_url)`** idempotency; `ix (job_id, discovery_order)` tree + frontier resume; FK `parent_page_id` for the first-discovery spanning tree |
| `crawl_edges` | every discovered link, including external and not-crawled targets | **`unique (job_id, source_page_id, target_normalized_url)`** with `occurrence_count`; `ix (job_id, target_page_id)` |
| `outbox_messages` | transactional outbox for job messages | partial `ix (created_at) where published_at is null` |

Performance notes: all reads are `AsNoTracking` projections; history uses one `COUNT` + one keyset-friendly ordered page; the tree is one query per job (≤ `MaxAllowedPages` rows) built iteratively in memory; the worker inserts a page's children and edges in one batched `SaveChanges`; counters are maintained incrementally (`discovered += n, processed += 1`) instead of being recounted. Foreign keys cascade from job to pages/edges.

## 7. Domain Link Ratio

For each fetched page: `ratio = internal / total`, where *total* is the number of eligible outgoing link occurrences (`<a href>` resolving to an absolute `http`/`https` URL; `mailto:`, `tel:`, `javascript:` and empty hrefs are ignored) and *internal* are those whose host equals the job's starting host exactly (subdomains count as external; the port is ignored). Duplicate occurrences count separately. Zero eligible links → `0`. Stored as a double computed by one division (deterministic), together with both counts. Implemented in `Domain/DomainLinkRatio.cs`, independent of crawling and persistence.

## 8. Crawling and URL normalization

- BFS from the root (depth 0). Pages at `maxDepth` are fetched (so they get a ratio) but their links are not expanded. The root counts toward `maxPages`.
- Only pages that receive a slot (`discoveredPages < maxPages`) become page rows; a link to an in-scope page that gets no slot marks the job `truncated`. Edges to external, out-of-depth or truncated targets are still persisted (`target_page_id` null).
- Same-host `http(s)` pages only. External links are counted and stored as edges, never fetched. Redirects to another host are not followed (page marked `Skipped`).
- Within a level, fetches run concurrently (`Crawler:Http:Concurrency`, default 4) but results are applied in page order, so the tree and parent selection are deterministic (decision D4).
- Normalization (`Domain/UrlNormalizer.cs`): resolve relative URLs against `<base href>` or the document URL, drop fragments, lower-case scheme and host, drop default ports, empty path → `/`. Path case, query order, trailing slashes and all query parameters are preserved. Unsupported schemes are rejected.
- HTML only: the fetcher checks `Content-Type` before reading the body, caps bodies at 2 MB, uses a 10 s timeout per attempt, retries transient failures 3× with exponential backoff, follows at most 5 same-host redirects.
- robots.txt (RFC 9309, `Crawler:Http:RespectRobotsTxt`, default on): fetched once per job from the starting host through the same safe pipeline; the group for `CrawlerAssignmentBot` is used if present, else `*`; longest-match precedence with `Allow` winning ties, `*` and `$` supported, body capped at 500 KiB. A 4xx or missing file allows everything; a 5xx or unreachable file disallows everything for that job. Disallowed pages keep their slot and appear in the tree as `Skipped` with the reason, so the exclusion is visible rather than silent.

## 9. Message schema

Exchange `crawl` (direct) → queue `crawl.jobs` (durable, persistent messages).

```json
{ "schemaVersion": 1, "messageId": "guid", "jobId": "guid", "correlationId": "guid", "createdAt": "2026-10-28T18:00:00Z" }
```

AMQP properties: `message_id`, `correlation_id`, `content_type: application/json`, `delivery_mode: 2`, header `x-attempt` (1-based). The message carries identity only; crawl parameters and all mutable state live in the job row, so a stale or duplicated message can never apply stale parameters.

## 10. Idempotency strategy

Several layers, each sufficient on its own for a different failure:

1. **Atomic claim with lease** — `UPDATE crawl_jobs SET status='Running', lease_owner=me, lease_expires_at=now()+60s WHERE id=? AND (status='Pending' OR (status='Running' AND (lease_expires_at < now() OR lease_owner = me)))`. A duplicate message for a job that is terminal is acknowledged and dropped; for a job actively owned by another worker it is acknowledged too, because recovery does not depend on redelivery (next point).
2. **Resume from the durable frontier** — each processed page commits atomically with its new child rows, edges and the counter update. On re-claim the worker reloads the job's pages; `Discovered` rows are the frontier, everything else is skipped. No page is fetched twice, no counter is double-applied (decision D3).
3. **Owner-conditional writes** — the per-page counter update is `WHERE lease_owner = me AND status = 'Running'`; if it affects 0 rows the transaction is rolled back and the worker stops (`LeaseLostException`), so a worker that lost its lease during a GC pause cannot corrupt state.
4. **Database constraints** — `unique (job_id, normalized_url)` and `unique (job_id, source_page_id, target_normalized_url)` are the last line of defense.

## 11. Retry policy and DLQ

**Page-level (never fails the job):** timeouts, connection errors, HTTP 408/429/500/502/503/504 are transient → up to 3 attempts with exponential backoff (500 ms, 1 s, 2 s); then the page is `Failed`. 404 and other 4xx are permanent → `Failed` immediately. Non-HTML content, external redirects, oversized bodies, blocked destinations → `Skipped`.

**Message-level:** an exception while handling a message (database or broker unavailable, unexpected error) is transient → the worker publishes a copy to `crawl.jobs.retry` with `x-attempt + 1` and acknowledges the original. The retry queue has a fixed TTL (`Crawler:Worker:RetryDelaySeconds`, default 10 s) and dead-letters back to `crawl.jobs`. After `MaxMessageAttempts` (default 5) the job is marked `Failed` with a reason and the message is rejected without requeue → `crawl.dlx` → `crawl.jobs.dlq`.

**Straight to the DLQ (poison):** unparseable JSON, wrong `schemaVersion`, empty `jobId`, unknown job. Nothing is ever acknowledged before the state that makes redelivery safe has been committed.

**Database-enforced cap:** every successful claim increments `crawl_jobs.attempts`. A claimable job (Pending, or Running with an expired lease) whose attempts already reached `MaxMessageAttempts` is marked `Failed` and its message dead-lettered before any new claim, so crashes, broker redeliveries and sweeper republishes, which all bypass the `x-attempt` header, still terminate (decision D16). If the consumer cannot even settle a message (for example the retry publish fails) it waits 5 s and returns the message to the queue; the same cap bounds that path.

## 12. Crash recovery

1. Worker claims the job (lease 60 s, renewed by a heartbeat every 15 s while the crawl runs) and starts persisting pages.
2. Worker dies mid-crawl. RabbitMQ redelivers the unacked message immediately; another worker sees an unexpired lease and acknowledges it (the job is still owned).
3. `LeaseRecoveryService` in every worker scans `status='Running' AND lease_expires_at < now()` every 30 s and republishes a job message for each expired lease.
4. The new owner claims the job (`Running → Running`, `attempts + 1`) and resumes from the frontier (section 10). Final counts equal those of an uninterrupted run.

Recovery latency is bounded by the lease duration plus the sweep interval (≤ 90 s with defaults). The behaviour is covered by integration test W6 (`tests/Crawler.Worker.Tests`) and exercised live by `scripts/e2e_smoke.py --with-crash`, which kills the worker container while it crawls the rate-limited `/slow/` fixture.

## 13. Cancellation

`POST /api/jobs/{id}/cancel` sets `cancellation_requested`; a Pending job becomes `Canceled` at once (there is no worker to observe the flag). A Running worker checks the flag between pages and performs `Running → Canceled` (owner-conditional). Completion that races a cancel wins; a Completed/Failed job returns 409. Repeating a cancel is idempotent (202 while running, 200 once canceled). Remaining `Discovered` pages stay in the tree, clearly marked as not crawled.

## 14. Tests

```bash
dotnet test Crawler.sln --nologo          # unit + integration (integration tests start PostgreSQL/RabbitMQ via Testcontainers)
dotnet test tests/Crawler.Core.Tests      # pure unit tests only, no Docker needed
cd frontend && npm test -- --run
python scripts/e2e_smoke.py --with-recovery --with-crash   # against `docker compose up`
cd frontend && npm run test:e2e           # Playwright, against `docker compose up`
```

| Project | What it covers |
|---------|----------------|
| `Crawler.Core.Tests` | URL normalization (N1–N20), Domain Link Ratio (R1–R10), state machine, progress formula, SSRF address classification, options consistency, robots.txt parser and matcher (41 cases) |
| `Crawler.Api.Tests` | endpoints A1–A13 with a real PostgreSQL container, `JobStateStore` transitions S3–S12 incl. concurrent claims, tree builder, outbox publisher against RabbitMQ |
| `Crawler.Worker.Tests` | link extraction (L1–L7), HTTP fetcher behaviour (retries, redirects, content type, size cap, SSRF), BFS engine B1–B13 against PostgreSQL with the fixture graph, message handler W1–W6 (duplicate delivery, DLQ decisions, retry publishing, retry exhaustion, database attempt cap, lease expiry + recovery), robots.txt crawls (blocked page skipped and never requested, 503 → all skipped, 404 → all fetched, opt-out), RabbitMQ round trip |
| `frontend` (vitest) | tree utilities, formatting, TreeView rendering, mock client lifecycle |
| `frontend/e2e` (Playwright, Chromium, against the running stack) | start-crawl validation and full crawl of the fixture site with tree assertions, history ordering and navigation, cancel of a running slow crawl, 400 px layout without horizontal overflow |

Expected values were fixed in [docs/test-plan.md](docs/test-plan.md) before implementation.

## 15. Observability

Structured JSON console logs (`Microsoft.Extensions.Logging` JSON formatter, scopes enabled) outside Development; every job operation runs in a scope with `JobId`, `CorrelationId` and, in the worker, `MessageId` and `Attempt`. Logged events: job created/published, message received, claim result, level progress, page outcomes at Debug, truncation, completion/cancel/failure with counters, retries with classification, dead-lettering, lease expiry and recovery. Health: API `/health/live` + `/health/ready` (PostgreSQL + RabbitMQ), Worker the same on :8081; Docker health checks gate start-up order.

## 16. Security assumptions

Users submit arbitrary URLs, so the crawler defends against SSRF:

- Only `http`/`https`. The API rejects loopback names and non-public IP literals (RFC 1918, link-local/cloud-metadata `169.254.0.0/16`, CGNAT, multicast, IPv6 ULA/link-local, IPv4-mapped forms, documentation ranges) at submission.
- The worker resolves DNS itself inside `SocketsHttpHandler.ConnectCallback`, validates **every** resolved address and connects only to a validated one — the connection cannot be redirected to a private address by DNS rebinding after validation.
- Redirects are followed manually; each `Location` is normalized and validated and must stay on the starting host.
- No cookies, no credentials, descriptive `User-Agent`, 10 s timeouts, 2 MB body cap, bounded concurrency, HTML only, no script execution.
- IPv6 uses an allow-list: only global unicast `2000::/3` minus documentation/ORCHID/benchmarking ranges is public, and 6to4 addresses are judged by the IPv4 they embed.
- Credentials embedded in URLs (`user:pw@host`) are stripped during normalization and never stored, displayed or sent.
- Error text returned by the API is generic ("resolves to a non-public address", exception type only); details stay in logs so the crawler cannot be used to map internal DNS.
- `Crawler:Security:TrustedHosts` (`fixture-site` in Docker, `localhost`/`127.0.0.1` in Development) is the explicit allow-list for local fixtures; it is empty by default.

## 17. Known limitations

- Subdomains are treated as external hosts; `www.example.com` and `example.com` are different starting domains.
- A same-host redirect target is attributed to the original URL; if the target is also linked directly it is crawled again under its own URL.
- A cancel request that arrives while the last page is being persisted is recorded (`cancellationRequested: true`) but the job finishes `Completed`; completion wins the race by design.
- Pages beyond `maxPages` are represented only as edges, not as tree nodes.
- Message-level retry uses a fixed delay (one TTL queue); changing `RetryDelaySeconds` requires deleting `crawl.jobs.retry` because RabbitMQ queue arguments are immutable.
- Lease recovery after a crash takes up to lease + sweep interval (≤ 90 s with defaults) and requires at least one live worker. A sweeper republishes an expired lease at most once per 5 minutes per process.
- No authentication on the API; it is a local assignment deployment (an API-key scheme was built and removed again at the owner's request, decision D25).

## 18. Engineering trade-offs, priorities and next steps

**Implemented first:** the shared core (schema, migration, conditional state transitions, URL normalization, ratio) with its unit tests, because every other component depends on those contracts and the rubric weights correctness and edge cases highest. Then API, worker and UI were built in parallel against frozen contracts, followed by Docker Compose integration and reliability checks.

**Trade-offs:** one shared assembly instead of a project per layer; level-synchronous BFS (deterministic trees) instead of maximal throughput; resume-from-frontier instead of replay (more state handling, exact counters); fixed-TTL retry queue instead of exponential message backoff; the worker hosts a tiny Kestrel endpoint purely for health.

**Cut:** SSE/SignalR progress (polling is accepted by the assignment and simpler) and authentication (both were built in a second round and removed again at the owner's request, D25); per-domain politeness delays. **Added in the second round once the core was verified:** robots.txt and Playwright browser tests.

**Next with more time:** keyset pagination for history; `repeated references` in the tree API (cross-links to already-shown nodes); exponential message backoff with multiple TTL queues or a delayed-exchange plugin; OpenTelemetry traces/metrics; per-host rate limiting; configurable subdomain policy; a `pages`/`edges` listing endpoint with pagination; authentication (OIDC/SSO) and an SSE progress stream if the deployment ever leaves the local machine.

## 19. Verification status

What was executed on 2026-10-28 before submission (Windows 11 host, Docker Desktop 29):

| Check | Result |
|-------|--------|
| `dotnet build Crawler.sln` | succeeded, 0 warnings |
| `dotnet test Crawler.sln` | 278 passed, 0 failed (Core 156, Worker 66, API 56; Worker/API use PostgreSQL and RabbitMQ Testcontainers) |
| `npm run build`, `npm test -- --run` | build clean; 25 tests passed |
| `npm run test:e2e` (Playwright) | 8 tests passed against the Compose stack |
| `docker compose up --build` | all six services healthy; migrations applied by the API |
| `python scripts/e2e_smoke.py --with-recovery --with-crash` | fixture crawl (5 pages, 1 failed, root ratio 0.75, deterministic parents), duplicate delivery leaves rows and counters unchanged, invalid JSON and unknown job land in `crawl.jobs.dlq`, cancel matrix (202/200/409/404), canceled job not revived by its queued message, validation (loopback literal, ftp, depth 9 → 400), history ordering, health endpoints, expired-lease recovery by the sweeper, worker container killed mid-crawl of the slow fixture and the job resumed to 21/21/0 |
| UI against the real API (Chrome, 1400 px and 400 px) | start → details with live polling and tree → history; no console errors |
| Independent code review (read-only agent) | 10 findings; 9 fixed before submission (decisions D16–D20), 1 documented as a known limitation (cancel vs. completion race) |
| GitHub Actions workflow | written and validated locally (`docker compose config`); not executed on GitHub from this machine |

Crash recovery is exercised three ways: integration test W6 (crash → expired lease → republish → resume with identical counts), the live expired-lease scenario, and the live kill of the worker container mid-crawl.

## 20. Submission summary

See the final section of this README's companion, [docs/implementation-plan.md](docs/implementation-plan.md), for the timeline. In short: a working API → RabbitMQ → Worker → PostgreSQL → React pipeline with deterministic BFS crawling, exact Domain Link Ratios, idempotent at-least-once processing (lease + frontier resume + constraints), bounded retries with a dead-letter queue, a transactional outbox, lease-based crash recovery, idempotent cancellation, SSRF protection, structured logs, health checks, Docker Compose, CI, and specification-first tests at unit, integration and end-to-end level.
