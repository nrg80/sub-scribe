# Architecture — two trees and what they share

Every piece of work in this plugin can be located by three questions. **Which run is it** — manual, scheduled or
arrival? **Which direction** — download or upload? **Which unit** — one file or a whole season? The two graphs below
answer those questions for each direction, and everything outside them is shared by all four leaves.

## The download tree

```
                       DOWNLOAD
                          │
   manuell ───────────────┼─────────────── Zeitplan ────────────── Ankunft
   (ganze Auswahl)        │                (ganze Auswahl)          (nur die angekommenen IDs)
                          ▼
            Seeder füllt die Download-Liste der Warteschlange
                          │
        ┌─────────────────┴─────────────────┐
        ▼                                   ▼
     EINZELN                            STAFELPACK
  eine Sprache, eine Datei          ein Archiv für die Staffel
        │                                   │
  Kandidat suchen, holen             Staffel-ZIP zuerst, dann die Folgeseite
        │                                   │
  auf die Tonspur legen              Mitglieder entpacken und ablegen
        │                                   │
        └─────────────────┬─────────────────┘
                          ▼
        Tore: Größe, Sprache, Cues, SRT, Sync, Herkunft
                          ▼
        Bestand: Urteil je (Inhalt, Sprache) ──► Datenbank
```

## The upload tree

```
                        UPLOAD
                          │
   manuell ───────────────┼─────────────── Zeitplan ────────────── Ankunft
   (ganze Auswahl)        │                (ganze Auswahl)          (nur die angekommenen IDs)
                          ▼
            Seeder füllt die Upload-Liste der Warteschlange
                          │
        ┌─────────────────┴─────────────────┐
        ▼                                   ▼
     EINZELN                            STAFELPACK
  die Spuren EINER Datei          eine Sprache, alle Folgen
        │                                   │
  Textspuren in einem Lauf           Vorprüfungen ohne Extraktion:
        │                            Staffel vollständig, je Quelle offen,
  Tore je Spur                      Urteils-Signatur, eine Sprache
        │                                   │
  Container aus den Spuren           Füllen aus dem Speicher,
  bauen und senden                   Lücken einzeln extrahieren
        │                                   │
        └─────────────────┬─────────────────┘
                          ▼
        Bestand: Buchung je Quelle ──► Datenbank, Pack-Urteil je Staffel
```

## The four leaves, and the paper that owns them

| Run | Direction | Unit | Paper |
| --- | --- | --- | --- |
| any | download | one language of one file | [SINGLE-DOWNLOAD.md](SINGLE-DOWNLOAD.md) |
| any | download | one season archive | [SEASON-PACK-DOWNLOAD.md](SEASON-PACK-DOWNLOAD.md) |
| any | upload | the tracks of one file | [SINGLE-UPLOAD.md](SINGLE-UPLOAD.md) |
| any | upload | one language of one season | [SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md) |

The run type never changes what a leaf does; it changes only how much arrives at the leaf. A manual run and a
scheduled run hand over the whole selection, an arrival run hands over the ids that arrived, and the leaf cannot
tell the difference because the narrowing happened before it.

## What all four leaves share

The **coordinator** owns every due time, so no leaf starts itself; the five Jellyfin tasks carry empty trigger lists
on purpose. The **dispatcher** turns a trigger into a cycle and hands the scope down. The **seeder** turns libraries
into candidates and decides nothing else. All of that is told in [RUNS.md](RUNS.md).

The **queue** is one file with one list per direction, filled by the seeder and drained by the pipelines, with retry
counters and tombstones. It is the only thing between collecting and deciding: [QUEUE.md](QUEUE.md).

The **gates** are the single place that refuses, and they are shared by both directions: [GATES.md](GATES.md). The
**source registry** turns a source name into an engine and wraps it in a counter, so both directions talk to every
site the same way: [CONTRACTS.md](CONTRACTS.md).

The **store** keeps what was decided and survives a restart; a **cache** keeps what can be recomputed and may be
emptied at any time. Where a new value belongs is decided by that one distinction: [DATABASE.md](DATABASE.md) and
[CACHING.md](CACHING.md).

The **worker rows** report what a worker did, painted by the page; neither of them decides anything:
[GUI.md](GUI.md). And one **lock** covers every part that writes, which is why no two leaves ever run at the same
time.

## Finding your way from a symptom

| If you see … | It belongs to | Paper |
| --- | --- | --- |
| a run that scans 1.477 items after a file arrived | the run type, not the leaf | [RUNS.md](RUNS.md) |
| a pair that is sent twice | the pair lock in the store | [SINGLE-UPLOAD.md](SINGLE-UPLOAD.md) |
| a language that never reaches a site | the gates or the source's language list | [GATES.md](GATES.md), the site's paper |
| a season that never becomes a pack | the pre-checks, before any extraction | [SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md) |
| a subtitle that sits wrong on the audio | Auto-Sync, after the fetch | [AUTOSYNC.md](AUTOSYNC.md) |
| a row that stays yellow | the worker row and its deferral | [GUI.md](GUI.md) |
| a name nobody can parse back | `SidecarNaming` and its writers | [NAMING.md](NAMING.md) |

[SPECIFICATION.md](SPECIFICATION.md), §2, §3, §4, §14, §21; F-M233, F-M624, F-M668, F-M669, F-M682, F-M683, F-M801.
Guarded by `coverage`, `storex`, `endpoints` and `gui-structure`.
