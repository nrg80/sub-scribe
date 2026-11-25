#!/usr/bin/env python3
"""Check that published documentation carries no credentials, addresses or operator paths.

The same eight patterns as NF-10 in the requirements, plus an explicit allowlist for the cases that
are public by nature and would otherwise be sorted by hand on every run:

  * a hex string inside a public store or content URL — Roku channel-store identifiers and GitHub
    raw-content hashes are part of a public link, not a secret;
  * an e-mail address of a project, not of a person — an upstream code of conduct names its own
    contact address, and that address belongs to the project.

Usage:  python3 check.py <root> [<root> ...]
Exit:   0 = no finding, 1 = at least one finding.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

DOC_SUFFIXES = {".md", ".markdown", ".txt", ".rst"}

# Not documentation: build output and dependency trees are not published with the repository.
SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".venv", "venv", "__pycache__", "packages"}

# Public links: a hex string there is an identifier of a public place, not a secret.
PUBLIC_URL = re.compile(
    r"(?:channelstore\.roku\.com|raw\.githubusercontent\.com|githubusercontent\.com/"
    r"|user-images\.githubusercontent\.com|objects\.githubusercontent\.com)",
    re.I,
)

# Project addresses (not personal). Each entry names why it is allowed.
ALLOWED_ADDRESSES = {
    "awesome-jellyfin@d2a.io": "contact address of the upstream project, named in its code of conduct",
}

PATTERNS: list[tuple[str, re.Pattern[str]]] = [
    (
        "credential value",
        re.compile(
            r"(?i)\b(?:api[_-]?key|apikey|token|passw(?:or)?d|secret)\s*[=:]\s*"
            r"(?!(?:set|unset|empty|none|required|configured|present|absent)\b)"
            r"(?!\{|<|\"|'|\.\.\.|…|TODO|X{3})[A-Za-z0-9][A-Za-z0-9_.-]{7,}"
        ),
    ),
    (
        "provider key shape",
        re.compile(
            r"\b(?:ghp_[A-Za-z0-9]{20,}|gho_[A-Za-z0-9]{20,}|ghs_[A-Za-z0-9]{20,}"
            r"|ghu_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{16,}"
            r"|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})"
        ),
    ),
    ("hex string (32+)", re.compile(r"\b[A-Fa-f0-9]{32,}\b")),
    ("e-mail address", re.compile(r"\b[\w.+-]+@(?!example\.(?:org|com|net|invalid))[\w-]+\.[\w.]{2,}")),
    ("private host address", re.compile(r"\b(?:192\.168|10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b")),
    ("operator path", re.compile(r"(?:/opt/data|/data/movies|/mnt/|/home/|[A-Za-z]:\\Users)")),
    ("IBAN", re.compile(r"\b[A-Z]{2}\d{2}(?:\s?\d{4}){3,}\b")),
    ("JSON web token", re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")),
]

SECRETS_FILE = re.compile(r"\.secrets\.json|(?:^|/)\.env(?:$|\.)")


def allowed(kind: str, match: re.Match[str], line: str) -> bool:
    """True when the hit is public by nature, so it is not a finding."""
    text = match.group(0)
    if kind in ("hex string (32+)", "credential value") and PUBLIC_URL.search(line):
        return True
    if kind == "e-mail address":
        # a sentence may end right after the address, so the trailing punctuation is not part of it
        if text.lower().rstrip(".,;:)]}>") in ALLOWED_ADDRESSES:
            return True
    return False


def check_root(root: Path) -> list[str]:
    findings: list[str] = []
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.suffix.lower() not in DOC_SUFFIXES:
            continue
        if SKIP_DIRS.intersection(path.parts):
            continue
        rel = path.relative_to(root).as_posix()
        if SECRETS_FILE.search(rel):
            findings.append(f"{rel}: a secrets file is published")
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            for kind, pattern in PATTERNS:
                for match in pattern.finditer(line):
                    if allowed(kind, match, line):
                        continue
                    findings.append(f"{rel}:{number}: {kind} — {match.group(0)[:40]}")
    return findings


def main(argv: list[str]) -> int:
    roots = [Path(a) for a in argv[1:]] or [Path.cwd()]
    findings: list[str] = []
    for root in roots:
        if not root.is_dir():
            print(f"not a directory: {root}")
            return 2
        findings.extend(check_root(root))
    if findings:
        for finding in findings[:40]:
            print("  FAIL " + finding)
        if len(findings) > 40:
            print(f"  … {len(findings) - 40} further finding(s)")
        print(f">>> DOC-HYGIENE FAIL: {len(findings)} finding(s) in {len(roots)} tree(s)")
        return 1
    print(f">>> DOC-HYGIENE OK: {len(roots)} tree(s), no credential, no address, no operator path")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
