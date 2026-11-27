#!/usr/bin/env python3
"""Erzeugt die Sprachtafel aus der amtlichen ISO-639-2-Liste (F-M797).

Quelle: Library of Congress, Registrierungsstelle fuer ISO 639-2,
        https://www.loc.gov/standards/iso639-2/ISO-639-2_utf-8.txt
Spalten: 639-2/B | 639-2/T (nur wenn abweichend) | 639-1 | Englisch | Franzoesisch

Die Datei liegt zwischengespeichert unter scripts/data/iso-639-2.txt, damit der
Bau ohne Netz reproduzierbar bleibt. Aufruf:

    python3 scripts/tools/gen-language-table.py <ziel.cs> [--fetch]

Ohne --fetch wird nur die zwischengespeicherte Datei gelesen.
"""
import pathlib
import sys
import urllib.request

QUELLE = "https://www.loc.gov/standards/iso639-2/ISO-639-2_utf-8.txt"
HIER = pathlib.Path(__file__).resolve().parent
DATEN = HIER.parent / "data" / "iso-639-2.txt"


def hole() -> str:
    req = urllib.request.Request(
        QUELLE,
        headers={
            "User-Agent": "Mozilla/5.0 (X11; Linux aarch64) Chrome/126 Safari/537.36",
            "Accept": "*/*",
        },
    )
    text = urllib.request.urlopen(req, timeout=120).read().decode("utf-8", errors="replace")
    DATEN.parent.mkdir(parents=True, exist_ok=True)
    DATEN.write_text(text, encoding="utf-8")
    return text


def lies() -> str:
    if "--fetch" in sys.argv or not DATEN.exists():
        return hole()
    return DATEN.read_text(encoding="utf-8")


def main() -> int:
    ziel = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else None
    if ziel is None:
        print("Aufruf: gen-language-table.py <ziel.cs> [--fetch]")
        return 2

    zeilen = []
    for roh in lies().replace("\ufeff", "").splitlines():
        f = roh.split("|")
        if len(f) < 4:
            continue
        b, t, eins, englisch = f[0].strip(), f[1].strip(), f[2].strip(), f[3].strip()
        if len(b) != 3:
            continue
        zeilen.append((b, t or b, eins, englisch.replace('"', "'")))

    zeilen.sort(key=lambda z: z[0])
    mit_eins = sum(1 for z in zeilen if z[2])
    abweichend = sum(1 for z in zeilen if z[1] != z[0])

    kopf = f'''// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// ERZEUGT — nicht von Hand bearbeiten. Erzeuger: scripts/tools/gen-language-table.py
// Quelle: Library of Congress, Registrierungsstelle fuer ISO 639-2
//         {QUELLE}
// Stand der Liste: {len(zeilen)} Kennzeichen, davon {mit_eins} mit zweistelligem Pendant und
// {abweichend} mit abweichender terminologischer Form (B != T).
//
// F-M797: die Uebersetzung zwischen den Normen wird nicht getippt, sie wird aus der amtlichen
// Liste erzeugt. Jede Zeile traegt beide dreistelligen Formen, den Zweisteller (leer, wenn die
// Sprache keinen hat) und den englischen Namen.

namespace Jellyfin.Plugin.SubScribe.Language;

/// <summary>
/// Die amtliche ISO-639-2-Liste als Daten (F-M797).
/// </summary>
public static class LanguageTable
{{
    /// <summary>
    /// Eine Zeile der amtlichen Liste.
    /// </summary>
    /// <param name="Bibliographic">Die bibliografische Form, z. B. <c>ger</c>.</param>
    /// <param name="Terminologic">Die terminologische Form, z. B. <c>deu</c>.</param>
    /// <param name="TwoLetter">Der zweistellige Code, leer wenn die Sprache keinen hat.</param>
    /// <param name="English">Der englische Name, z. B. <c>German</c>.</param>
    public readonly record struct Row(string Bibliographic, string Terminologic, string TwoLetter, string English);

    /// <summary>
    /// Alle Zeilen der amtlichen Liste, nach bibliografischer Form sortiert.
    /// </summary>
    public static readonly Row[] Rows =
    {{
'''
    rumpf = "".join(
        f'        new("{b}", "{t}", "{eins}", "{englisch}"),\n' for b, t, eins, englisch in zeilen
    )
    fuss = '''    };
}
'''
    ziel.write_text(kopf + rumpf + fuss, encoding="utf-8")
    print(f"{ziel}: {len(zeilen)} Zeilen, {mit_eins} mit Zweisteller, {abweichend} B!=T")
    return 0


if __name__ == "__main__":
    sys.exit(main())
