# CLAUDE.md — Crawler

Job-based web crawler: ASP.NET Core API + .NET Worker + PostgreSQL + RabbitMQ + React. Assignment: `Home_Assignment_Full-Stack_Developer.pdf`. Planning notes (`docs/`: requirements, architecture, contracts, test plan, decision log, implementation plan) are local-only and gitignored; a fresh clone does not have them. The frozen contract is also expressed in code: the DTO records, `CrawlJobMessage`, options classes and the EF model in `src/Crawler.Core`.

## Layout
```
Crawler.sln
src/Crawler.Core/        shared: Domain/ (pure), Application/ (DTOs, options, interfaces, message), Infrastructure/ (EF, RabbitMQ, crawling, outbox)
src/Crawler.Api/         ASP.NET Core minimal API (port 8080)
src/Crawler.Worker/      hosted services + health endpoint (port 8081)
tests/Crawler.Core.Tests/      unit tests for Domain + pure application logic
tests/Crawler.Api.Tests/       API integration tests (WebApplicationFactory + PostgreSQL)
tests/Crawler.Worker.Tests/    crawl engine, fetcher, consumer tests; tests/fixtures/ HTML fixtures
frontend/                Vite + React + TS
deploy/                  Dockerfiles, nginx config, fixture-site
docker-compose.yml, .env.example, .github/workflows/ci.yml
```

## Stack
.NET 8 (SDK 8.0.427), EF Core 8.0.11 + Npgsql, RabbitMQ.Client 7.2.2 (async API, publisher confirms), AngleSharp 1.8.6, xUnit 2.9.3, Testcontainers 4.16.0. Frontend: React 18, TypeScript, Vite, React Router 6, TanStack Query 5, plain CSS with tokens.

## Commands (Windows, Git Bash)
The SDK is installed per-user. Prefix every dotnet command with:
```
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
```
- Build: `dotnet build Crawler.sln -c Release --nologo`
- Unit tests: `dotnet test tests/Crawler.Core.Tests --nologo`
- All tests: `dotnet test Crawler.sln --nologo` (integration tests start PostgreSQL/RabbitMQ through Testcontainers, or use `CRAWLER_TEST_PG` / `CRAWLER_TEST_RABBITMQ` connection settings when set)
- Add migration: `dotnet ef migrations add <Name> --project src/Crawler.Core --startup-project src/Crawler.Api --output-dir Infrastructure/Persistence/Migrations`
- Frontend: `cd frontend && npm ci && npm run dev` / `npm run build` / `npm test`
- Full stack: `docker compose up --build` → UI http://localhost:3000, API http://localhost:8080, RabbitMQ UI http://localhost:15672 (guest/guest), fixture site http://localhost:8090
- End-to-end: `python scripts/e2e_smoke.py --with-recovery --with-crash`; browser tests: `cd frontend && npm run test:e2e` (Playwright, against the running stack)

## Conventions
- C#: file-scoped namespaces, `sealed` classes by default, records for DTOs/messages, nullable enabled, warnings as errors in Core. Async all the way with `CancellationToken` as the last parameter.
- `Domain/` must not reference EF Core, HttpClient, RabbitMQ or ASP.NET. Business rules (`UrlNormalizer`, `DomainLinkRatio`, `JobStateMachine`) are pure and unit-tested.
- Options classes are bound with `ValidateDataAnnotations().ValidateOnStart()`; no magic numbers in services — read from options or named constants.
- Structured logging only: `_logger.LogInformation("Job {JobId} completed with {ProcessedPages} pages", ...)`. Wrap job work in `BeginScope` with `JobId`, `CorrelationId`, `MessageId`. No per-link logs at Information level.
- Every state transition is a conditional UPDATE; never load-modify-save a job status.
- Never ack a RabbitMQ message before the durable state that makes re-delivery safe has been committed.
- Comments explain *why*. No narration, no filler XML docs.
- Frontend: function components, hooks, TanStack Query for all server state, no `any`, CSS variables from `src/styles/tokens.css`.

## Agent responsibilities (Phase B)
Agents work with disjoint file ownership (API / Worker / Frontend / orchestrator for deploy, CI, README). Shared contracts, the C# types in `src/Crawler.Core/Application/` and `src/Crawler.Core/Domain/`, are read-only for agents.

## Testing expectations
Expected inputs and outputs are fixed before implementation (test ids such as N1, R4, B6, W5 in test names refer to that plan). Do not alter expected values to make tests pass. Integration tests that need infrastructure must use the real thing (Testcontainers or a provided connection) and be clearly skipped — never mocked and reported as verified — when it is unavailable.

## Integration rules
- Build must stay green; run `dotnet build Crawler.sln` before handing work back.
- Run the tests you own before reporting. Report what passed, what failed and what was not run.
- No new NuGet/npm dependencies beyond those listed in the stack without a reason in the final report.

## Critical invariants
1. One page row per `(job_id, normalized_url)`; one edge row per `(job_id, source_page_id, target_normalized_url)`.
2. `discoveredPages == count(pages)`, `processedPages == count(pages with status != Discovered)`, `failedPages == count(status == Failed)` at every transaction boundary.
3. Terminal statuses are never overwritten.
4. Worker writes for a job succeed only while `lease_owner` matches.
5. Only http(s) URLs whose resolved addresses are public (or whose host is in `TrustedHosts`) are ever connected to.
6. `maxPages` is never exceeded; links from depth `maxDepth` pages are recorded as edges but never enqueued.

## Definition of Done
Builds and tests green locally and in CI; `docker compose up --build` from a clean checkout serves the UI, API, worker, fixture site; a crawl of the fixture site completes with the expected counts; duplicate delivery, DLQ, cancellation and lease recovery have been exercised; README describes actual behavior and limitations.
