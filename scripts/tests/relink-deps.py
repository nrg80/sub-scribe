#!/usr/bin/env python3
"""Dedupliziert die Abhaengigkeiten der Testprojekte per Hardlink.

Jedes Testprojekt kopiert beim Bauen den ganzen Abhaengigkeitsberg in sein eigenes
bin/ (ICU4N 13 MB, EF Core, SQLite-Natives je Plattform) — 40-mal dieselben Bytes.
Dieses Skript sucht byte-gleiche Dateien und ersetzt die Dubletten durch Hardlinks
auf die erste Fundstelle: gleiche Inode, kein zusaetzlicher Plattenplatz, die
Verzeichnisse bleiben unveraendert.

Aufruf:  python3 scripts/tests/relink-deps.py [--dry] [<repo>]
"""
import hashlib
import os
import sys


def digest(path, size):
    h = hashlib.md5()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            h.update(chunk)
    return (size, h.hexdigest())


def main(argv):
    dry = "--dry" in argv
    args = [a for a in argv if not a.startswith("--")]
    repo = args[0] if args else os.path.abspath(
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    tests = os.path.join(repo, "scripts", "tests")

    canonical = {}
    linked = 0
    saved = 0
    seen_inodes = set()

    for suite in sorted(os.listdir(tests)):
        bindir = os.path.join(tests, suite, "bin")
        if not os.path.isdir(bindir):
            continue
        for root, _dirs, files in os.walk(bindir):
            for name in sorted(files):
                path = os.path.join(root, name)
                if os.path.islink(path):
                    continue
                try:
                    st = os.stat(path)
                except OSError:
                    continue
                if st.st_size < 4096:          # kleine Dateien lohnen nicht
                    continue
                if st.st_ino in seen_inodes:   # schon ein Link auf die Kanone
                    continue
                key = (st.st_size, os.path.basename(path))
                md5 = digest(path, st.st_size)[1]
                key = (st.st_size, md5)
                first = canonical.get(key)
                if first is None:
                    canonical[key] = path
                    seen_inodes.add(st.st_ino)
                    continue
                if dry:
                    linked += 1
                    saved += st.st_size
                    continue
                try:
                    os.unlink(path)
                    os.link(first, path)
                    linked += 1
                    saved += st.st_size
                except OSError as exc:
                    print(f"  nicht verlinkt: {path} ({exc})")

    print(f"{linked} Datei(en) verlinkt, {saved / (1024 * 1024):.1f} MB gespart"
          + ("  [dry]" if dry else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
