"""
Dump the Pummel Party Mod Documentation wiki (MediaWiki) to local files.

Usage:
    pip install requests
    python scripts/dump_pummel_wiki.py
    python scripts/wiki2md.py          # then convert to agent-friendly Markdown

Output:
    docs/wiki/<Page_Title>.wiki   - raw wikitext, one file per page
    docs/wiki/_ALL.wiki           - everything concatenated (optional single file)
"""
import pathlib
import re
import time

import requests

API = "https://workshop.pummelparty.com/workshop.pummelparty/api.php"
OUT = pathlib.Path(__file__).resolve().parent.parent / "docs" / "wiki"
NAMESPACES = [0, 14, 10]  # main pages, categories, templates
DELAY = 0.5  # be polite to the server

session = requests.Session()
session.headers["User-Agent"] = "pummel-mcp-docs-dump/0.1 (personal, offline docs)"


def api(**params):
    params.update(format="json", formatversion=2)
    r = session.get(API, params=params, timeout=30)
    r.raise_for_status()
    time.sleep(DELAY)
    return r.json()


def all_titles(ns):
    cont = {}
    while True:
        data = api(action="query", list="allpages", apnamespace=ns, aplimit="max", **cont)
        for p in data["query"]["allpages"]:
            yield p["title"]
        if "continue" not in data:
            break
        cont = data["continue"]


def fetch_wikitext(titles):
    data = api(
        action="query",
        prop="revisions",
        rvprop="content",
        rvslots="main",
        titles="|".join(titles),
    )
    for page in data["query"]["pages"]:
        revs = page.get("revisions")
        if revs:
            yield page["title"], revs[0]["slots"]["main"]["content"]


def safe_name(title):
    return re.sub(r'[<>:"/\\|?*]', "_", title).replace(" ", "_")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    titles = [t for ns in NAMESPACES for t in all_titles(ns)]
    print(f"Found {len(titles)} pages")

    combined = []
    for i in range(0, len(titles), 50):  # API allows 50 titles per request
        for title, text in fetch_wikitext(titles[i : i + 50]):
            (OUT / f"{safe_name(title)}.wiki").write_text(text, encoding="utf-8")
            combined.append(f"\n\n===== PAGE: {title} =====\n\n{text}")
        print(f"  {min(i + 50, len(titles))}/{len(titles)}")

    (OUT / "_ALL.wiki").write_text("".join(combined), encoding="utf-8")
    print(f"Done -> {OUT.resolve()}")


if __name__ == "__main__":
    main()