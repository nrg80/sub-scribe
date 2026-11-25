#!/usr/bin/env python3
"""Readability at human scale — measured, not felt.

Criterion (per document, prose only):

  * no sentence longer than 45 words
  * no paragraph longer than 200 words
  * no list block longer than 400 words (a table row and a bullet are their own block)

What counts as prose: everything except code blocks, headings, link lists (a Contents block),
table rows and the derived/owner header lines. A bullet is measured on its own, because a reader
takes it on its own.

Usage:
  check.py <tree-or-file> [...]      # reports, exit 1 when a limit is broken
  check.py --summary <tree> [...]    # one line per document
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

SENTENCE_WORDS = 45
PARAGRAPH_WORDS = 200
LIST_WORDS = 400

WORD = re.compile(r"[A-Za-z0-9][A-Za-z0-9'\-/.:]*")


def documents(paths: list[str]) -> list[Path]:
    out: list[Path] = []
    for raw in paths:
        p = Path(raw)
        if p.is_file():
            out.append(p)
            continue
        out.extend(sorted(p.glob("docs/*.md")))
        out.extend(sorted(p.glob("docs/engines/*.md")))
        out.extend(sorted(p.glob("docs/*/*.md")))
        if (p / "README.md").exists():
            out.append(p / "README.md")
    seen: set[Path] = set()
    uniq: list[Path] = []
    for p in out:
        if p not in seen:
            seen.add(p)
            uniq.append(p)
    return uniq


def blocks(lines: list[str]) -> list[tuple[int, int, str, str]]:
    """(first line, last line, kind, text) — kind is 'prose', 'bullet' or 'row'."""
    out: list[tuple[int, int, str, str]] = []
    in_code = False
    in_toc = False
    para: list[str] = []
    start = 0

    def flush(end: int) -> None:
        nonlocal para
        if para:
            out.append((start, end, "prose", " ".join(x.strip() for x in para)))
            para = []

    for i, line in enumerate(lines, start=1):
        if line.strip().startswith("```"):
            flush(i - 1)
            in_code = not in_code
            continue
        if in_code:
            continue
        stripped = line.strip()
        if stripped.startswith("#"):
            flush(i - 1)
            in_toc = stripped.startswith("## Contents")
            continue
        if not stripped:
            flush(i - 1)
            continue
        if stripped.startswith("- [") or stripped.startswith("  - ["):
            continue  # Contents entry, not prose
        if in_toc:
            continue
        if stripped.startswith("|"):
            flush(i - 1)
            out.append((i, i, "row", stripped))
            continue
        if stripped.startswith(("- ", "* ", "1. ")) or re.match(r"^\d+\.\s", stripped):
            flush(i - 1)
            out.append((i, i, "bullet", stripped))
            continue
        if re.match(r"^\*\*(?:F-M|T\d|NF-|A-)", stripped):
            # A definition entry is read on its own, like a bullet — not merged with its neighbours.
            flush(i - 1)
            out.append((i, i, "entry", stripped))
            continue
        if stripped.startswith(("*Derived", "**Owner:", "*Paper:")):
            flush(i - 1)
            out.append((i, i, "header", stripped))
            continue
        if not para:
            start = i
        para.append(line)
    flush(len(lines))
    return out


def sentences(text: str) -> list[str]:
    text = re.sub(r"`[^`]*`", "X", text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"\1", text)
    return [s for s in re.split(r"(?<=[.!?])\s+", text) if s.strip()]


def words(text: str) -> int:
    return len(WORD.findall(re.sub(r"`[^`]*`", "X", text)))


def measure(path: Path) -> dict:
    lines = path.read_text(encoding="utf-8").split("\n")
    long_sentences: list[tuple[int, int, int, str]] = []
    long_paragraphs: list[tuple[int, int, int]] = []
    long_lists: list[tuple[int, int, int]] = []
    worst = 0
    total = 0
    for first, last, kind, text in blocks(lines):
        w = words(text)
        worst = max(worst, w)
        for s in sentences(text):
            total += 1
            sw = words(s)
            if sw > SENTENCE_WORDS and kind in ("prose", "bullet", "header", "entry"):
                long_sentences.append((first, last, sw, s.strip()))
        if kind == "prose" and w > PARAGRAPH_WORDS:
            long_paragraphs.append((first, last, w))
        if kind in ("bullet", "row", "entry") and w > LIST_WORDS:
            long_lists.append((first, last, w))
    return {
        "path": path,
        "lines": len(lines),
        "sentences": total,
        "long_sentences": long_sentences,
        "long_paragraphs": long_paragraphs,
        "long_lists": long_lists,
        "largest": worst,
    }


def main(argv: list[str]) -> int:
    summary = "--summary" in argv
    paths = [a for a in argv[1:] if not a.startswith("--")] or ["."]
    bad = 0
    for path in documents(paths):
        m = measure(path)
        n = len(m["long_sentences"]) + len(m["long_paragraphs"]) + len(m["long_lists"])
        bad += n
        if summary:
            print(
                f"{m['path'].name:26} {m['lines']:5} lines  {m['sentences']:5} sentences  "
                f">45W {len(m['long_sentences']):4}  >200W para {len(m['long_paragraphs']):3}  "
                f"largest {m['largest']:4}"
            )
            continue
        for first, last, sw, s in m["long_sentences"]:
            print(f"{m['path']}:{first}-{last} [sentence {sw}W] {s[:120]}")
        for first, last, w in m["long_paragraphs"]:
            print(f"{m['path']}:{first}-{last} [paragraph {w}W]")
        for first, last, w in m["long_lists"]:
            print(f"{m['path']}:{first}-{last} [list {w}W]")
    if not summary:
        print(f">>> READABILITY: {'OK' if bad == 0 else str(bad) + ' finding(s)'}")
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
