#!/usr/bin/env python3
"""Generates tests/fixtures/site/slow/: 21 pages (index + p01..p20) of ~6 KB each.

Served through nginx with `limit_rate 1k`, each page takes about six seconds to download, so a crawl of
the whole set takes long enough to kill the worker in the middle (crash-recovery check E5-live).

Link structure (depth 5 with the default limits): index -> p01..p04; p(n) -> p(n+4) and p(n+5) when they exist.
Every page also links back to index (cycle) and to one external URL.
"""
from __future__ import annotations

import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1] / "tests" / "fixtures" / "site" / "slow"
PAGES = 20
PADDING_BYTES = 6000

FILLER = "<p>" + ("The quick brown fox jumps over the lazy dog. " * 40).strip() + "</p>\n"


def page(title: str, links: list[tuple[str, str]]) -> str:
    items = "\n".join(f'    <li><a href="{href}">{text}</a></li>' for href, text in links)
    body = f"""<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Slow fixture – {title}</title></head>
<body>
  <h1>{title}</h1>
  <ul>
{items}
  </ul>
"""
    while len(body.encode()) < PADDING_BYTES:
        body += FILLER
    return body + "</body>\n</html>\n"


def main() -> None:
    ROOT.mkdir(parents=True, exist_ok=True)
    index_links = [(f"p{i:02d}.html", f"Page {i}") for i in range(1, 5)]
    index_links.append(("https://external.example.org/slow", "External"))
    (ROOT / "index.html").write_text(page("index", index_links), encoding="utf-8")

    for n in range(1, PAGES + 1):
        links = [(f"p{m:02d}.html", f"Page {m}") for m in (n + 4, n + 5) if m <= PAGES]
        links.append(("/slow/", "Back to slow index (cycle)"))
        links.append(("https://external.example.org/slow", "External"))
        (ROOT / f"p{n:02d}.html").write_text(page(f"p{n:02d}", links), encoding="utf-8")

    print(f"wrote {PAGES + 1} pages to {ROOT}")


if __name__ == "__main__":
    main()
