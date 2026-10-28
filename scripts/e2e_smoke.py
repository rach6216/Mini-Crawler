#!/usr/bin/env python3
"""End-to-end smoke test against a running `docker compose up` stack.

Covers test-plan items E1 (crawl of the fixture site), E2 (duplicate delivery), E3 (poison message
to DLQ), E4 (cancellation of a Pending job and terminal-state protection) and E6 (health endpoints). Uses
only the standard library so it runs anywhere Python 3 is available.

    python scripts/e2e_smoke.py
"""
from __future__ import annotations

import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = os.environ.get("CRAWLER_API", "http://localhost:8080")
WORKER = os.environ.get("CRAWLER_WORKER", "http://localhost:8081")
RABBIT = os.environ.get("CRAWLER_RABBIT", "http://localhost:15672")
RABBIT_AUTH = base64.b64encode(
    f"{os.environ.get('RABBITMQ_USER', 'guest')}:{os.environ.get('RABBITMQ_PASSWORD', 'guest')}".encode()
).decode()
TARGET = os.environ.get("CRAWLER_TARGET", "http://fixture-site/")
COMPOSE = os.environ.get("CRAWLER_COMPOSE", "docker compose").split()

failures: list[str] = []


def check(condition: bool, message: str) -> None:
    status = "PASS" if condition else "FAIL"
    print(f"[{status}] {message}")
    if not condition:
        failures.append(message)


def http(
    method: str, url: str, body: dict | str | None = None, authorization: str | None = None
) -> tuple[int, dict | str | None]:
    data = None
    headers = {"Accept": "application/json"}
    if body is not None:
        data = (json.dumps(body) if isinstance(body, dict) else body).encode()
        headers["Content-Type"] = "application/json"
    if authorization:
        headers["Authorization"] = authorization
    request = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            raw = response.read().decode()
            try:
                return response.status, (json.loads(raw) if raw else None)
            except json.JSONDecodeError:
                return response.status, raw
    except urllib.error.HTTPError as error:
        raw = error.read().decode()
        try:
            return error.code, json.loads(raw)
        except json.JSONDecodeError:
            return error.code, raw


def wait_terminal(job_id: str, timeout_seconds: float = 60) -> dict:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        _, job = http("GET", f"{API}/api/jobs/{job_id}")
        assert isinstance(job, dict)
        if job["status"] in {"Completed", "Failed", "Canceled"}:
            return job
        time.sleep(0.5)
    raise TimeoutError(f"job {job_id} did not reach a terminal state within {timeout_seconds}s")


def publish_raw(payload: str, routing_key: str = "crawl.jobs") -> None:
    body = {
        "properties": {"delivery_mode": 2, "content_type": "application/json"},
        "routing_key": routing_key,
        "payload": payload,
        "payload_encoding": "string",
    }
    status, result = http("POST", f"{RABBIT}/api/exchanges/%2F/crawl/publish", body, f"Basic {RABBIT_AUTH}")
    assert status == 200 and isinstance(result, dict) and result.get("routed"), f"publish failed: {status} {result}"


def queue_depth(queue: str) -> int:
    _, info = http("GET", f"{RABBIT}/api/queues/%2F/{queue}", authorization=f"Basic {RABBIT_AUTH}")
    assert isinstance(info, dict)
    return int(info.get("messages", 0))


def queue_has_messages(queue: str) -> bool:
    """Live check: peeks one message (requeued) instead of reading the lagging management statistics."""
    body = {"count": 1, "ackmode": "reject_requeue_true", "encoding": "auto"}
    status, result = http("POST", f"{RABBIT}/api/queues/%2F/{queue}/get", body, f"Basic {RABBIT_AUTH}")
    assert status == 200 and isinstance(result, list), f"peek failed: {status} {result}"
    return len(result) > 0


def drain_queue(queue: str) -> bool:
    """Removes every message with destructive gets (a purge right after a peek is refused with 406)."""
    for _ in range(20):
        status, result = http("POST", f"{RABBIT}/api/queues/%2F/{queue}/get",
                              {"count": 50, "ackmode": "ack_requeue_false", "encoding": "auto"}, f"Basic {RABBIT_AUTH}")
        assert status == 200 and isinstance(result, list), f"drain failed: {status} {result}"
        if not result:
            return True
    return False


def wait_queue_empty(queue: str, timeout_seconds: float) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if not queue_has_messages(queue):
            return True
        time.sleep(1)
    return False


def find_node(node: dict, suffix: str) -> dict | None:
    stack = [node]
    while stack:
        current = stack.pop()
        if current["url"].endswith(suffix):
            return current
        stack.extend(current["children"])
    return None


def e6_health() -> None:
    for name, base in (("api", API), ("worker", WORKER)):
        for probe in ("live", "ready"):
            status, _ = http("GET", f"{base}/health/{probe}")
            check(status == 200, f"E6 {name} /health/{probe} -> {status}")


def e1_fixture_crawl() -> dict:
    status, created = http("POST", f"{API}/api/jobs", {"url": TARGET, "maxDepth": 2})
    check(status == 202 and isinstance(created, dict) and "jobId" in created, f"E1 create job -> {status} {created}")
    job = wait_terminal(created["jobId"])  # type: ignore[index]
    check(job["status"] == "Completed", f"E1 status Completed (got {job['status']}, reason={job.get('failureReason')})")
    check(job["discoveredPages"] == 5, f"E1 discoveredPages == 5 (got {job['discoveredPages']})")
    check(job["processedPages"] == 5, f"E1 processedPages == 5 (got {job['processedPages']})")
    check(job["failedPages"] == 1, f"E1 failedPages == 1 (got {job['failedPages']})")
    check(job["truncated"] is False, "E1 truncated is false")
    check(job["progressPercent"] == 100, f"E1 progressPercent == 100 (got {job['progressPercent']})")

    _, tree = http("GET", f"{API}/api/jobs/{job['jobId']}/tree")
    assert isinstance(tree, dict)
    root = tree["root"]
    check(tree["totalNodes"] == 5, f"E1 tree totalNodes == 5 (got {tree['totalNodes']})")
    check(root["depth"] == 0 and root["fetchStatus"] == "Fetched", "E1 root fetched at depth 0")
    check(abs(root["domainLinkRatio"] - 0.75) < 1e-9, f"E1 root ratio 0.75 (got {root['domainLinkRatio']})")
    check(root["outgoingLinkCount"] == 4 and root["internalLinkCount"] == 3, "E1 root counts 4 total / 3 internal")
    child_urls = sorted(c["url"] for c in root["children"])
    check(child_urls == sorted([TARGET + "a.html", TARGET + "b.html"]), f"E1 root children a.html,b.html (got {child_urls})")
    c_node = find_node(root, "/c.html")
    d_node = find_node(root, "/d.html")
    check(c_node is not None and c_node["depth"] == 2, "E1 c.html present at depth 2")
    a_node = find_node(root, "/a.html")
    check(a_node is not None and any(ch["url"].endswith("/c.html") for ch in a_node["children"]), "E1 c.html parented by a.html (first discovery)")
    check(d_node is not None and d_node["fetchStatus"] == "Failed" and d_node["httpStatusCode"] == 404, "E1 d.html Failed with 404")
    return job


def e2_duplicate_delivery(job: dict) -> None:
    before = (job["discoveredPages"], job["processedPages"], job["failedPages"], job["status"])
    message = {
        "schemaVersion": 1,
        "messageId": "11111111-1111-1111-1111-111111111111",
        "jobId": job["jobId"],
        "correlationId": "22222222-2222-2222-2222-222222222222",
        "createdAt": "2026-01-01T00:00:00Z",
    }
    publish_raw(json.dumps(message))
    time.sleep(3)
    _, after_job = http("GET", f"{API}/api/jobs/{job['jobId']}")
    assert isinstance(after_job, dict)
    after = (after_job["discoveredPages"], after_job["processedPages"], after_job["failedPages"], after_job["status"])
    check(before == after, f"E2 duplicate delivery left job unchanged ({before} -> {after})")
    _, tree = http("GET", f"{API}/api/jobs/{job['jobId']}/tree")
    assert isinstance(tree, dict)
    check(tree["totalNodes"] == 5, f"E2 no duplicate page rows (totalNodes {tree['totalNodes']})")


def e3_poison_to_dlq() -> None:
    before = queue_depth("crawl.jobs.dlq")
    publish_raw("this is not json")
    publish_raw(json.dumps({"schemaVersion": 1, "messageId": "33333333-3333-3333-3333-333333333333",
                            "jobId": "44444444-4444-4444-4444-444444444444",
                            "correlationId": "55555555-5555-5555-5555-555555555555",
                            "createdAt": "2026-01-01T00:00:00Z"}))
    deadline = time.time() + 20
    after = before
    while time.time() < deadline:
        after = queue_depth("crawl.jobs.dlq")
        if after >= before + 2:
            break
        time.sleep(1)
    check(after >= before + 2, f"E3 invalid JSON and unknown job routed to DLQ (depth {before} -> {after})")


def e4_cancel_pending_and_terminal_protection() -> None:
    subprocess.run([*COMPOSE, "stop", "worker"], check=True, capture_output=True)
    try:
        status, created = http("POST", f"{API}/api/jobs", {"url": TARGET, "maxDepth": 1})
        assert status == 202 and isinstance(created, dict)
        job_id = created["jobId"]
        status, body = http("POST", f"{API}/api/jobs/{job_id}/cancel")
        check(status == 202 and isinstance(body, dict) and body["status"] == "Canceled", f"E4 cancel Pending -> 202 Canceled (got {status} {body})")
        status, body = http("POST", f"{API}/api/jobs/{job_id}/cancel")
        check(status == 200, f"E4 repeated cancel is idempotent -> 200 (got {status})")
    finally:
        subprocess.run([*COMPOSE, "start", "worker"], check=True, capture_output=True)

    # The queued message for the canceled job must be acknowledged without reviving the job.
    time.sleep(8)
    _, job = http("GET", f"{API}/api/jobs/{job_id}")
    assert isinstance(job, dict)
    check(job["status"] == "Canceled" and job["processedPages"] == 0, f"E4 canceled job untouched after worker restart ({job['status']}, processed {job['processedPages']})")
    check(wait_queue_empty("crawl.jobs", 20), "E4 main queue drained (message acked)")

    status, completed = http("POST", f"{API}/api/jobs", {"url": TARGET, "maxDepth": 0})
    assert status == 202 and isinstance(completed, dict)
    wait_terminal(completed["jobId"])
    status, body = http("POST", f"{API}/api/jobs/{completed['jobId']}/cancel")
    check(status == 409, f"E4 cancel Completed -> 409 (got {status})")
    status, _ = http("POST", f"{API}/api/jobs/00000000-0000-0000-0000-000000000000/cancel")
    check(status == 404, f"E4 cancel unknown -> 404 (got {status})")


def e5_lease_recovery() -> None:
    """Simulates a worker that died mid-job: Running status, expired lease, no message in the queue.

    Only the LeaseRecoveryService can rescue the job. Requires `docker compose exec postgres psql`.
    """
    subprocess.run([*COMPOSE, "stop", "worker"], check=True, capture_output=True)
    try:
        check(wait_queue_empty("crawl.jobs", 20), "E5 queue empty before the scenario starts")
        status, created = http("POST", f"{API}/api/jobs", {"url": TARGET, "maxDepth": 2})
        assert status == 202 and isinstance(created, dict)
        job_id = created["jobId"]
        # Wait until the outbox has published, then purge so no queued message can resume the job.
        deadline = time.time() + 15
        while time.time() < deadline and not queue_has_messages("crawl.jobs"):
            time.sleep(0.5)
        check(drain_queue("crawl.jobs"), "E5 main queue drained")
        sql = ("UPDATE crawl_jobs SET status='Running', lease_owner='dead-worker', "
               f"lease_expires_at=now() - interval '1 minute', started_at=now() WHERE id='{job_id}';")
        result = subprocess.run([*COMPOSE, "exec", "-T", "postgres", "psql", "-U", os.environ.get("POSTGRES_USER", "crawler"),
                                 "-d", os.environ.get("POSTGRES_DB", "crawler"), "-c", sql], capture_output=True, text=True)
        check(result.returncode == 0 and "UPDATE 1" in result.stdout, f"E5 job marked Running with an expired lease ({result.stdout.strip()} {result.stderr.strip()})")
    finally:
        subprocess.run([*COMPOSE, "start", "worker"], check=True, capture_output=True)

    job = wait_terminal(job_id, timeout_seconds=90)
    check(job["status"] == "Completed", f"E5 recovered job Completed (got {job['status']})")
    check((job["discoveredPages"], job["processedPages"], job["failedPages"]) == (5, 5, 1),
          f"E5 recovered job counts 5/5/1 (got {job['discoveredPages']}/{job['processedPages']}/{job['failedPages']})")
    logs = subprocess.run([*COMPOSE, "logs", "worker", "--tail", "200"], capture_output=True, text=True).stdout
    check(f"Lease expired for job {job_id}" in logs, "E5 sweeper logged the expired lease and republished")


def e5_live_crash_mid_crawl() -> None:
    """Kills the worker container while it is crawling the rate-limited /slow/ fixture, then restarts it.

    Expected: the job resumes after the lease expires (lease + sweep ≤ ~90 s) and completes with the same
    page set as an uninterrupted crawl (21 pages, 0 failed), with no page fetched twice after the resume.
    """
    status, created = http("POST", f"{API}/api/jobs", {"url": TARGET + "slow/", "maxDepth": 5})
    assert status == 202 and isinstance(created, dict)
    job_id = created["jobId"]

    deadline = time.time() + 120
    processed_before_kill = 0
    while time.time() < deadline:
        _, job = http("GET", f"{API}/api/jobs/{job_id}")
        assert isinstance(job, dict)
        processed_before_kill = job["processedPages"]
        if job["status"] == "Running" and 3 <= processed_before_kill < 15:
            break
        time.sleep(1)
    check(3 <= processed_before_kill < 15, f"E5-live worker killed mid-crawl after {processed_before_kill} pages")

    subprocess.run([*COMPOSE, "kill", "worker"], check=True, capture_output=True)
    _, killed = http("GET", f"{API}/api/jobs/{job_id}")
    assert isinstance(killed, dict)
    check(killed["status"] == "Running", f"E5-live job still Running right after the kill ({killed['status']})")
    subprocess.run([*COMPOSE, "start", "worker"], check=True, capture_output=True)

    started = time.time()
    job = wait_terminal(job_id, timeout_seconds=240)
    elapsed = time.time() - started
    check(job["status"] == "Completed", f"E5-live job Completed after restart (got {job['status']}, {elapsed:.0f}s)")
    check((job["discoveredPages"], job["processedPages"], job["failedPages"]) == (21, 21, 0),
          f"E5-live counts 21/21/0 (got {job['discoveredPages']}/{job['processedPages']}/{job['failedPages']})")
    _, tree = http("GET", f"{API}/api/jobs/{job_id}/tree")
    assert isinstance(tree, dict)
    check(tree["totalNodes"] == 21, f"E5-live tree has 21 nodes, no duplicates (got {tree['totalNodes']})")
    logs = subprocess.run([*COMPOSE, "logs", "worker", "--tail", "400"], capture_output=True, text=True).stdout
    check(f"Lease expired for job {job_id}" in logs or "known pages" in logs, "E5-live worker logged the resume")


def validation_and_history() -> None:
    status, body = http("POST", f"{API}/api/jobs", {"url": "http://127.0.0.1/"})
    check(status == 400, f"A4 loopback literal rejected -> 400 (got {status})")
    status, body = http("POST", f"{API}/api/jobs", {"url": "ftp://example.com/"})
    check(status == 400, f"A2 ftp rejected -> 400 (got {status})")
    status, body = http("POST", f"{API}/api/jobs", {"url": TARGET, "maxDepth": 9})
    check(status == 400, f"A3 maxDepth 9 rejected -> 400 (got {status})")
    status, page = http("GET", f"{API}/api/jobs?page=1&pageSize=5")
    assert isinstance(page, dict)
    check(status == 200 and page["pageSize"] == 5 and len(page["items"]) <= 5 and page["totalCount"] >= 1, "A7 history page shape")
    created = [item["createdAt"] for item in page["items"]]
    check(created == sorted(created, reverse=True), "A7 history ordered newest first")


def main() -> int:
    print(f"API={API} WORKER={WORKER} RABBIT={RABBIT} TARGET={TARGET}")
    e6_health()
    job = e1_fixture_crawl()
    e2_duplicate_delivery(job)
    e3_poison_to_dlq()
    validation_and_history()
    e4_cancel_pending_and_terminal_protection()
    if "--with-recovery" in sys.argv:
        e5_lease_recovery()
    if "--with-crash" in sys.argv:
        e5_live_crash_mid_crawl()
    print()
    if failures:
        print(f"{len(failures)} check(s) FAILED:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print("All end-to-end checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
