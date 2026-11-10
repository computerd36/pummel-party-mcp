#!/usr/bin/env python3
"""
wiki2md - convert the exported Pummel Party Mod Editor wiki (MediaWiki markup)
into agent-friendly GitHub Markdown.

    python scripts/wiki2md.py [--src docs/wiki] [--out docs/md]

Design goals (vs. a generic pandoc conversion):
  * Every page starts with `# Title`, then `Categories:` / `Aliases:` lines.
  * Internal links -> relative `Page.md` links; redirects resolved; links to
    pages that don't exist are rendered as plain text.
  * Images (not available locally) -> `*[Image: caption]*` on their own line.
  * Transclusions `{{:Page}}` are inlined so each file is self-contained.
  * Redirect pages are not emitted (they become aliases of their target).
  * Category pages get a generated member list (MediaWiki builds these
    dynamically, so the export has none).
  * Wiki tables -> clean GFM tables (no empty rows, no style attributes);
    indented sub-property rows are annotated with their parent property.
  * Layout-only tables (no header row) -> nested bullet lists.
  * INDEX.md lists every page with a one-line summary, grouped by category.

Only reads from --src; deletes/rewrites *.md in --out.
"""
from __future__ import annotations

import argparse
import re
import sys
from collections import defaultdict
from pathlib import Path

# --------------------------------------------------------------------------
# Titles, stems, link resolution
# --------------------------------------------------------------------------


def norm_title(title: str) -> str:
    t = re.sub(r"[\s_]+", " ", title).strip()
    return t[:1].upper() + t[1:] if t else t


def title_to_stem(title: str) -> str:
    return norm_title(title).replace(" ", "_").replace("/", "_").replace(":", "_")


def gfm_anchor(text: str) -> str:
    a = text.strip().lower()
    a = re.sub(r"[^\w\- ]", "", a)
    return a.replace(" ", "-")


class Wiki:
    def __init__(self, src: Path):
        self.src = src
        self.raw: dict[str, str] = {}  # stem -> wikitext
        self.titles: dict[str, str] = {}  # stem -> display title
        self.redirects: dict[str, tuple[str, str]] = {}  # stem -> (stem, anchor)
        self.warnings: list[str] = []

        all_file = src / "_ALL.wiki"
        if all_file.exists():
            for m in re.finditer(r"^===== PAGE: (.+?) =====\s*$",
                                 all_file.read_text(encoding="utf-8"), re.M):
                self.titles[title_to_stem(m.group(1))] = norm_title(m.group(1))

        for f in sorted(src.glob("*.wiki")):
            if f.name == "_ALL.wiki":
                continue
            text = f.read_text(encoding="utf-8").replace("\r\n", "\n")
            self.raw[f.stem] = text
            self.titles.setdefault(f.stem, f.stem.replace("_", " "))
            m = re.match(r"\s*#REDIRECT\s*\[\[([^\]|]+)", text, re.I)
            if m:
                tgt, _, anchor = m.group(1).partition("#")
                self.redirects[f.stem] = (title_to_stem(tgt.lstrip(":")), anchor)

    def resolve(self, target: str) -> tuple[str, str] | None:
        """Wiki link target -> (stem, anchor) of an existing, non-redirect page."""
        target = target.strip().lstrip(":")
        page, _, anchor = target.partition("#")
        if not page:  # same-page anchor
            return ("", anchor)
        stem = title_to_stem(page)
        seen = set()
        while stem in self.redirects and stem not in seen:
            seen.add(stem)
            stem, ranchor = self.redirects[stem]
            anchor = anchor or ranchor
        if stem in self.raw and stem not in self.redirects:
            return (stem, anchor)
        return None

    def link(self, target: str) -> str | None:
        r = self.resolve(target)
        if r is None:
            return None
        stem, anchor = r
        href = f"{stem}.md" if stem else ""
        if anchor:
            href += "#" + gfm_anchor(anchor)
        return href


# --------------------------------------------------------------------------
# Inline conversion
# --------------------------------------------------------------------------

IMAGE_OPTS = re.compile(
    r"^(thumb|thumbnail|frame|framed|frameless|border|right|left|center|centre|none|"
    r"upright(=.*)?|\d*x?\d+px|(alt|link|page|class|lang)=.*)$", re.I)


def split_top(s: str, sep: str = "|") -> list[str]:
    """Split on sep, ignoring separators nested inside [[...]] or {{...}}."""
    parts, depth, cur, i = [], 0, [], 0
    while i < len(s):
        two = s[i:i + 2]
        if two in ("[[", "{{"):
            depth += 1; cur.append(two); i += 2; continue
        if two in ("]]", "}}") and depth:
            depth -= 1; cur.append(two); i += 2; continue
        if s.startswith(sep, i) and depth == 0:
            parts.append("".join(cur)); cur = []; i += len(sep); continue
        cur.append(s[i]); i += 1
    parts.append("".join(cur))
    return parts


def find_close(s: str, start: int) -> int:
    """Index of the ']]' matching the '[[' at s[start]; -1 if unbalanced."""
    depth, i = 0, start
    while i < len(s) - 1:
        if s.startswith("[[", i):
            depth += 1; i += 2; continue
        if s.startswith("]]", i):
            depth -= 1
            if depth == 0:
                return i
            i += 2; continue
        i += 1
    return -1


class Inline:
    """Converts one chunk of inline wikitext. ctx: 'para' | 'cell' | 'list'."""

    def __init__(self, wiki: Wiki, page: "PageState"):
        self.wiki, self.page = wiki, page
        self.tokens: list[str] = []

    def tok(self, s: str) -> str:
        self.tokens.append(s)
        return f"\x00{len(self.tokens) - 1}\x00"

    def restore(self, s: str) -> str:
        for _ in range(10):
            new = re.sub(r"\x00(\d+)\x00", lambda m: self.tokens[int(m.group(1))], s)
            if new == s:
                break
            s = new
        return s

    @staticmethod
    def code(s: str) -> str:
        s = s.replace("'''", "").replace("''", "")
        fence = "``" if "`" in s else "`"
        return f"{fence}{s}{fence}"

    def convert(self, s: str, ctx: str = "para") -> str:
        # 1. literal / code spans
        def nowiki(m):
            body = m.group(1)
            return self.tok(self.code(body) if re.search(r"[<>\[\]{}'*]", body) else body)
        s = re.sub(r"<nowiki>(.*?)</nowiki>", nowiki, s, flags=re.S)
        s = re.sub(r"<code>(.*?)</code>", lambda m: self.tok(self.code(m.group(1))), s, flags=re.S)
        # Unity rich-text tags shown as literal syntax on the wiki
        s = re.sub(r"</?(?:color|size)(?:=[^>]*)?>", lambda m: self.tok(self.code(m.group(0))), s)

        # 2. HTML-ish tags
        s = re.sub(r'<dfn\s+title="([^"]*)"\s*>(.*?)</dfn>', r"\2 (\1)", s, flags=re.S)
        s = re.sub(r"</?(?:span|u|div|dfn|small|big|center|font)\b[^>]*>", "", s)
        s = re.sub(r"</?b>", "'''", s)
        s = re.sub(r"</?i>", "''", s)
        s = re.sub(r"<br\s+clear=[^>]*>", "", s)
        s = re.sub(r"<br\s*/?>", "\x00BR\x00", s)

        # 3. wiki links (bracket-matched so File captions may contain links)
        s = self.wikilinks(s, ctx)

        # 4. external links
        def ext(m):
            url, text = m.group(1), (m.group(2) or "").strip()
            return self.tok(f"[{self.emph(text)}]({url})" if text else f"<{url}>")
        s = re.sub(r"\[(https?://[^\s\]]+)(?:\s+([^\]]*))?\]", ext, s)

        # 5. bold / italic
        s = self.emph(s)

        s = self.restore(s)
        br = "<br>" if ctx == "cell" else ("\n" if ctx == "para" else " ")
        s = s.replace("\x00BR\x00", br)
        s = re.sub(r"[ \t]{2,}", " ", s)
        return s

    @staticmethod
    def emph(s: str) -> str:
        s = re.sub(r"'''''(.+?)'''''", r"***\1***", s)
        s = re.sub(r"'''(.+?)'''", r"**\1**", s)
        s = re.sub(r"''(.+?)''", r"*\1*", s)
        s = s.replace("'''", "").replace("''", "")  # unbalanced leftovers
        return s

    def wikilinks(self, s: str, ctx: str) -> str:
        out, i = [], 0
        while True:
            j = s.find("[[", i)
            if j < 0:
                out.append(s[i:]); break
            k = find_close(s, j)
            if k < 0:
                out.append(s[i:]); break
            out.append(s[i:j])
            inner = s[j + 2:k]
            i = k + 2
            # link trail: [[Gameobject]]s -> "Gameobjects"
            trail = ""
            m = re.match(r"[a-z]+", s[i:])
            if m and not re.match(r"\s*(File|Image|Category):", inner, re.I):
                trail = m.group(0); i += len(trail)
            out.append(self.one_link(inner, trail, ctx))
        return "".join(out)

    def one_link(self, inner: str, trail: str, ctx: str) -> str:
        parts = split_top(inner)
        target = parts[0].strip()
        low = target.lower()
        if low.startswith(("file:", "image:")):
            fname = target.split(":", 1)[1].strip()
            caption = ""
            for p in parts[1:]:
                if not IMAGE_OPTS.match(p.strip()):
                    caption = p.strip()
            caption = Inline(self.wiki, self.page).convert(caption, "list") if caption else fname
            img = self.tok(f"*[Image: {caption.rstrip()}]*")
            return f"\n\n{img}\n\n" if ctx == "para" else img
        if low.startswith("category:"):
            self.page.categories.append(norm_title(target.split(":", 1)[1]))
            return ""
        text = parts[1].strip() if len(parts) > 1 and parts[1].strip() else target.lstrip(":")
        text = self.emph(text + trail)
        href = self.wiki.link(target)
        if href is None:
            self.page.missing_links.add(target)
            return self.tok(text)
        return self.tok(f"[{text}]({href})")


# --------------------------------------------------------------------------
# Block conversion
# --------------------------------------------------------------------------


class PageState:
    def __init__(self):
        self.categories: list[str] = []
        self.missing_links: set[str] = set()


def expand_transclusions(wiki: Wiki, text: str, depth: int = 0) -> str:
    def repl(m):
        name = m.group(1).strip()
        stem = title_to_stem(name)
        if depth < 5 and stem in wiki.raw:
            body = re.sub(r"\[\[Category:[^\]]*\]\]", "", wiki.raw[stem])
            return expand_transclusions(wiki, body.strip(), depth + 1)
        wiki.warnings.append(f"unresolved transclusion {{{{:{name}}}}}")
        return ""
    text = re.sub(r"\{\{:([^{}|]+)\}\}", repl, text)

    def other(m):
        wiki.warnings.append(f"dropped template {m.group(0)[:60]}")
        return ""
    return re.sub(r"\{\{[^{}]*\}\}", other, text)


def galleries_to_lists(text: str) -> str:
    def repl(m):
        lines = []
        for ln in m.group(1).strip().splitlines():
            ln = ln.strip()
            if not ln:
                continue
            if not re.match(r"(File|Image):", ln, re.I):
                ln = "File:" + ln
            lines.append(f"* [[{ln}]]")
        return "\n" + "\n".join(lines) + "\n"
    return re.sub(r"<gallery[^>]*>(.*?)</gallery>", repl, text, flags=re.S)


def strip_markup(md: str) -> str:
    """Markdown -> plain text (for summaries / parent names)."""
    s = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", md)
    s = re.sub(r"[*`]", "", s)
    return re.sub(r"\s+", " ", s).strip()


class Table:
    def __init__(self, conv: "Converter"):
        self.conv = conv

    @staticmethod
    def split_attr(cell: str) -> tuple[str, str]:
        parts = split_top(cell, "|")
        if len(parts) >= 2 and re.search(r'\w+\s*=\s*"', parts[0]) and "[[" not in parts[0]:
            return parts[0], "|".join(parts[1:])
        return "", cell

    def parse(self, lines: list[str]):
        caption, rows, cur = "", [], None

        def new_row():
            nonlocal cur
            cur = {"cells": [], "header": []}
            rows.append(cur)

        for ln in lines[1:]:  # skip "{|"
            s = ln.strip()
            if s.startswith("|}"):
                break
            if s.startswith("|+"):
                caption = s[2:].strip(); continue
            if s.startswith("|-"):
                new_row(); continue
            if cur is None:
                new_row()
            if s.startswith("!"):
                for c in re.split(r"!!|\|\|", s[1:]):
                    attr, body = self.split_attr(c)
                    cur["cells"].append({"attr": attr, "text": body.strip()})
                    cur["header"].append(True)
            elif s.startswith("|"):
                for c in split_top(s[1:], "||"):
                    attr, body = self.split_attr(c)
                    cur["cells"].append({"attr": attr, "text": body.strip()})
                    cur["header"].append(False)
            elif cur["cells"]:
                cur["cells"][-1]["text"] += "\n" + ln
        rows = [r for r in rows if any(c["text"].strip() for c in r["cells"])]
        return caption, rows

    def cell_md(self, text: str) -> str:
        lines = []
        for ln in text.split("\n"):
            m = re.match(r"^([*#]+)\s*(.*)", ln.strip())
            lines.append(("• " + m.group(2)) if m else ln.strip())
        s = self.conv.inline("\x00BR\x00".join(l for l in lines if l), "cell")
        s = s.replace("\x00BR\x00", "<br>")
        s = re.sub(r"(<br>\s*)+$", "", s.strip())
        return s.replace("|", "\\|")

    @staticmethod
    def indent_of(cell) -> int:
        m = re.search(r"padding-left:\s*([\d.]+)", cell["attr"])
        return int(float(m.group(1)) * 10) if m else 0

    def render(self, lines: list[str]) -> str:
        caption, rows = self.parse(lines)
        if not rows:
            return ""
        out = []
        if caption:
            out.append(f"**{strip_markup(self.conv.inline(caption, 'list'))}**\n")
        if all(rows[0]["header"]):
            head = [self.cell_md(c["text"]) for c in rows[0]["cells"]]
            n = len(head)
            out.append("| " + " | ".join(head) + " |")
            out.append("|" + "---|" * n)
            parents: list[tuple[int, str]] = []
            for r in rows[1:]:
                cells = [self.cell_md(c["text"]) for c in r["cells"]]
                ind = self.indent_of(r["cells"][0]) if r["cells"] else 0
                while parents and parents[-1][0] >= ind:
                    parents.pop()
                name = strip_markup(cells[0]) if cells else ""
                if ind and parents and cells:
                    cells[0] += f" *(under {parents[-1][1]})*"
                parents.append((ind, name))
                if len(cells) > n:
                    cells = cells[:n - 1] + ["<br>".join(cells[n - 1:])]
                cells += [""] * (n - len(cells))
                out.append("| " + " | ".join(cells) + " |")
        else:
            # layout table -> nested bullet list
            for r in rows:
                cells = [self.cell_md(c["text"]).replace("\\|", "|") for c in r["cells"]]
                cells = [c for c in cells if c]
                if not cells:
                    continue
                ind = 1 if self.indent_of(r["cells"][0]) else 0
                if r["header"] and all(r["header"]):
                    cells = [f"**{strip_markup(c)}**" for c in cells]
                line = cells[0] if len(cells) == 1 else f"{cells[0]}: " + " — ".join(cells[1:])
                out.append("  " * ind + "- " + line)
        return "\n".join(out)


class Converter:
    def __init__(self, wiki: Wiki, stem: str):
        self.wiki, self.stem = wiki, stem
        self.page = PageState()

    def inline(self, s: str, ctx: str = "para") -> str:
        return Inline(self.wiki, self.page).convert(s, ctx)

    def convert(self) -> str:
        text = self.wiki.raw[self.stem]
        text = expand_transclusions(self.wiki, text)
        text = galleries_to_lists(text)
        text = re.sub(r"__[A-Z]+__", "", text)
        text = re.sub(r"</?div[^>]*>", "", text)
        text = re.sub(r"^\s*<br\s+clear=[^>]*>\s*$", "", text, flags=re.M)
        text = re.sub(r"<hr\s*/?>", "\n----\n", text)

        lines = text.split("\n")
        out: list[str] = []
        para: list[str] = []
        counters: dict[int, int] = {}
        list_widths: list[int] = []

        def flush_para():
            if para:
                out.append(self.inline("\n".join(para), "para"))
                out.append("")
                para.clear()

        def end_list():
            if counters or list_widths:
                counters.clear(); list_widths.clear()
                out.append("")

        i = 0
        while i < len(lines):
            ln = lines[i]
            s = ln.strip()

            if s.startswith("{|"):
                flush_para(); end_list()
                j = i
                while j < len(lines) and not lines[j].strip().startswith("|}"):
                    j += 1
                out.append(Table(self).render(lines[i:j + 1]))
                out.append("")
                i = j + 1
                continue

            m = re.match(r"^(={1,6})\s*(.+?)\s*\1\s*$", s)
            if m:
                flush_para(); end_list()
                level = min(len(m.group(1)), 6)
                out.append("#" * level + " " + strip_markup(self.inline(m.group(2), "list")))
                out.append("")
                i += 1
                continue

            m = re.match(r"^([*#:;]+)\s*(.*)$", ln)
            if m and not s.startswith("#REDIRECT"):
                flush_para()
                prefix, body = m.group(1), m.group(2)
                depth = len(prefix)
                for d in list(counters):
                    if d > depth:
                        del counters[d]
                del list_widths[depth - 1:]
                while len(list_widths) < depth - 1:
                    list_widths.append(2)
                indent = " " * sum(list_widths)
                kind = prefix[-1]
                if kind == "#":
                    counters[depth] = counters.get(depth, 0) + 1
                    marker = f"{counters[depth]}. "
                elif kind == "*":
                    counters.setdefault(depth, 0)
                    marker = "- "
                else:  # ':' / ';' -> indented continuation
                    counters.setdefault(depth, 0)
                    marker = ""
                list_widths.append(len(marker) or 2)
                content = self.inline(body, "list").strip()
                if kind == ";" and content:
                    content = f"**{content}**"
                out.append(indent + marker + content)
                i += 1
                continue

            if s in ("----",) or re.fullmatch(r"-{4,}", s):
                flush_para(); end_list()
                out.append("---"); out.append("")
                i += 1
                continue

            if ln.startswith(" ") and s:
                flush_para(); end_list()
                block = []
                while i < len(lines) and lines[i].startswith(" ") and lines[i].strip():
                    block.append(lines[i][1:])
                    i += 1
                code = "\n".join(block)
                code = re.sub(r"<nowiki>(.*?)</nowiki>", r"\1", code, flags=re.S)
                code = re.sub(r"<nowiki>|</nowiki>", "", code)
                code = "\n".join(l.rstrip() for l in code.split("\n") if l.strip())
                out.append("```\n" + code + "\n```")
                out.append("")
                continue

            if not s:
                flush_para(); end_list()
                i += 1
                continue

            end_list()
            para.append(ln)
            i += 1

        flush_para()
        md = "\n".join(out)
        md = self.cleanup(md)
        # de-dup categories, keep order
        seen = []
        for c in self.page.categories:
            if c not in seen:
                seen.append(c)
        self.page.categories = seen
        return md

    @staticmethod
    def cleanup(md: str) -> str:
        lines = [l.rstrip() for l in md.split("\n")]
        # drop lines that are only punctuation left over from split images
        lines = [l for l in lines if not re.fullmatch(r"\s*[.,;:]\s*", l)]
        lines = [l.lstrip() if not l.startswith((" -", "  ", "```")) else l for l in lines]
        # blank line between a text line and a list that starts right after it
        is_item = lambda l: bool(re.match(r"\s*(-|\d+\.)\s", l))
        spaced = []
        for l in lines:
            if spaced and is_item(l) and spaced[-1].strip() and not is_item(spaced[-1]) \
                    and not spaced[-1].startswith(("```", "|")):
                spaced.append("")
            spaced.append(l)
        md = "\n".join(spaced)
        md = re.sub(r"\n{3,}", "\n\n", md)
        return md.strip() + "\n"


# --------------------------------------------------------------------------
# Output
# --------------------------------------------------------------------------


def summary(md_body: str) -> str:
    for block in md_body.split("\n\n"):
        b = block.strip()
        if not b or b.startswith(("#", "|", "*[Image", "-", "```", "**")) or re.match(r"\d+\.", b):
            continue
        text = strip_markup(b)
        m = re.match(r"(.{20,}?[.!?])(\s|$)", text)
        text = m.group(1) if m else text
        return text if len(text) <= 220 else text[:217].rstrip() + "..."
    return ""


def write(path: Path, text: str) -> None:
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def cat_link(cat: str) -> str:
    return f"[{cat}](Category_{title_to_stem(cat)}.md)"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    root = Path(__file__).resolve().parent.parent
    ap.add_argument("--src", type=Path, default=root / "docs" / "wiki")
    ap.add_argument("--out", type=Path, default=root / "docs" / "md")
    args = ap.parse_args()

    wiki = Wiki(args.src)
    args.out.mkdir(parents=True, exist_ok=True)
    for old in args.out.glob("*.md"):
        old.unlink()

    aliases: dict[str, list[str]] = defaultdict(list)
    for stem in wiki.redirects:
        r = wiki.resolve(wiki.titles[stem])
        if r:
            aliases[r[0]].append(wiki.titles[stem])

    pages: dict[str, dict] = {}
    for stem in wiki.raw:
        if stem in wiki.redirects:
            continue
        conv = Converter(wiki, stem)
        body = conv.convert()
        pages[stem] = {"title": wiki.titles[stem], "body": body,
                       "cats": conv.page.categories, "missing": conv.page.missing_links}

    members: dict[str, list[str]] = defaultdict(list)
    for stem, p in pages.items():
        for c in p["cats"]:
            members[c].append(stem)

    for stem, p in pages.items():
        p["summary"] = summary(p["body"])

    for stem, p in pages.items():
        head = [f"# {p['title']}", ""]
        meta = []
        if p["cats"]:
            meta.append("Categories: " + ", ".join(cat_link(c) for c in p["cats"]))
        if aliases.get(stem):
            meta.append("Aliases: " + ", ".join(sorted(aliases[stem])))
        if meta:
            head += ["  \n".join(meta), ""]
        body = p["body"]
        if stem.startswith("Category_"):
            cat = p["title"].split(":", 1)[1]
            subcats = sorted(s for s in members.get(cat, []) if s.startswith("Category_"))
            mems = sorted((s for s in members.get(cat, []) if not s.startswith("Category_")),
                          key=lambda s: pages[s]["title"])
            if subcats:
                body += "\n## Subcategories\n\n" + "\n".join(
                    f"- [{pages[s]['title'].split(':', 1)[1]}]({s}.md)" for s in subcats) + "\n"
            if mems:
                body += "\n## Pages in this category\n\n" + "\n".join(
                    f"- [{pages[s]['title']}]({s}.md)" + (f" — {pages[s]['summary']}" if pages[s]['summary'] else "")
                    for s in mems) + "\n"
        write(args.out / f"{stem}.md", "\n".join(head) + "\n" + body)

    # ---- INDEX.md
    cat_titles = {c for c in members}
    parent_of = {c: [x for x in pages.get("Category_" + title_to_stem(c), {}).get("cats", [])]
                 for c in cat_titles}

    def leaf_cats(cats):
        return [c for c in cats if not any(c in parent_of.get(o, []) for o in cats if o != c)]

    def guess_group(title: str) -> str:
        """Index bucket for pages the wiki left uncategorized (index only)."""
        if title.endswith("Component") or title == "Rendering Components":
            return "Components"
        if title.endswith("Action"):
            return "Actions"
        if title.endswith("Asset") or title.startswith("Importing "):
            return "Assets"
        if "Shortcuts" in title:
            return "Shortcuts"
        if title.startswith(("Your First", "Tutorial")):
            return "Tutorials"
        return "General & concepts"

    groups: dict[str, list[str]] = defaultdict(list)
    for stem, p in pages.items():
        if stem.startswith(("Category_", "Template_")):
            continue
        for c in (leaf_cats(p["cats"]) or [guess_group(p["title"])]):
            groups[c].append(stem)

    order = ["General & concepts", "Tutorials", "Mod Editor Windows", "Shortcuts",
             "Gameplay Components", "Rendering Components", "Audio Components",
             "Physics Components", "Collider Component", "Components", "Actions", "Assets"]
    keys = [k for k in order if k in groups] + sorted(k for k in groups if k not in order)
    real_cats = {pages[s]["title"].split(":", 1)[1] for s in pages if s.startswith("Category_")}

    idx = ["# Pummel Party Mod Editor Docs — Index", "",
           "Generated by `scripts/wiki2md.py` from `docs/wiki/*.wiki` — do not edit by hand; "
           "re-run the script instead.", "",
           "One file per wiki page. Each starts with `# Title` and a `Categories:` line. "
           "Images are not included; `*[Image: ...]*` marks where a screenshot was, with its caption.",
           "", "Start here: [Main Page](Main_Page.md)", ""]
    for k in keys:
        heading = cat_link(k) if k in real_cats else k
        idx += [f"## {heading}", ""]
        for s in sorted(set(groups[k]), key=lambda s: pages[s]["title"]):
            line = f"- [{pages[s]['title']}]({s}.md)"
            if pages[s]["summary"]:
                line += f" — {pages[s]['summary']}"
            idx.append(line)
        idx.append("")
    if aliases:
        idx += ["## Aliases (redirects)", ""]
        for stem, names in sorted(aliases.items()):
            for n in sorted(names):
                idx.append(f"- {n} → [{pages[stem]['title']}]({stem}.md)")
        idx.append("")
    write(args.out / "INDEX.md", "\n".join(idx))

    missing = sorted({m for p in pages.values() for m in p["missing"]})
    print(f"wrote {len(pages)} pages + INDEX.md to {args.out}")
    print(f"redirects folded into aliases: {len(wiki.redirects)}")
    if missing:
        print("links to pages not in export (rendered as plain text):", ", ".join(missing))
    for w in sorted(set(wiki.warnings)):
        print("warning:", w)
    return 0


if __name__ == "__main__":
    sys.exit(main())
