# Data Model — the plugin database

**Owner: the shape.** The tables, their keys and what each one carries; the normative rules stand in §14 of `REQUIREMENTS.md`.

*Derived document: the rules are stated normatively in `§14` of `REQUIREMENTS.md`; this paper describes
the shape they produce — the tables, their keys, what each one carries and what it never carries. The
words are those of `TERMS.md`. Where a table name and a level name are the same word, that is the rule,
not a coincidence (F-M671).*

## Contents

- [1. What it is](#1-what-it-is)
- [2. The four levels](#2-the-four-levels)
- [3. The tables at a glance](#3-the-tables-at-a-glance)
- [4. Keys and identifiers](#4-keys-and-identifiers)
- [5. The membership table](#5-the-membership-table)
- [6. The source level](#6-the-source-level)
- [7. Verdicts, refusals and evidence](#7-verdicts-refusals-and-evidence)
- [8. The mark matrix](#8-the-mark-matrix)
- [9. Caches](#9-caches)
- [10. Who writes what](#10-who-writes-what)
- [11. Takeover and migration](#11-takeover-and-migration)
- [12. What the plugin database never holds](#12-what-the-plugin-database-never-holds)
- [13. The checks](#13-the-checks)

## 1. What it is

The plugin database is our own store of rows — identifiers, marks, locks, counters — and the only
place where the plugin's knowledge survives a restart (F-M194). It is not a cache of Jellyfin's library
and not a copy of any source's catalogue: it holds what **we** decided and what **we** saw.

## 2. The four levels

The **table name is the level**, and every identifier says which level it belongs to (F-M671, F-M676):

- **`pack`** — a whole season's subtitles for one language, as one container. Identifier `[xxxx]`.
- **`file`** — one media file. Identifier `#yyyyy`.
- **`track`** — one subtitle stream **inside** a media file. Identifier `#yyyyy#zzz` — the file stands
  in the identifier (F-M676).
- **`sidecar`** — a loose subtitle beside the media file. Identifier `#yyyyy&zzz` — again with the file
  in it (F-M674).

Two prohibitions follow from that and are worth naming because they were defects before: **`file` and
`track` carry no pack identifier** — the membership lives in its own table (F-M673, F-M677) — and **no
column is called `Id`** while meaning something else.

## 3. The tables at a glance

Fourteen tables exist. Nine belong to the model proper, five are small side tables created by
migrations.

**The entity level**
- `pack` — 16 columns: `PackId`, `SeasonKey`, `Source`, title identifiers, `Season`, `Language`,
  `HearingImpaired`, `ReleaseName`, `Signature`, `MtimeUnix`, `EpisodeCount`, `Episodes`,
  `AbsorbedIds`, `Created`, `Updated`.
- `file` — 22 columns: `FileId`, `OsHash`, `FileName`, `Size`, `MtimeUnix`, `Path`, `JellyfinItemId`,
  the per-source title identifiers, `IsSeries`, `Season`, `Episode`, the search bookkeeping
  (`LastSearchUtc`, `LastSearchLanguages`, `LastSearchEmptyUtc`), `Created`, `LastSeen`. **No language,
  no hearing-impaired, no pack identifier** — a file has no subtitle property.
- `track` — 16 columns: `TrackId`, `TrackNumber`, `MediaHash`, `SubPos`, `Language`,
  `HearingImpaired`, `Forced`, `ContentHash`, `Status`, `Reason`, `StatusAt`, `Origin`, the
  detection fields, `Created`, `LastSeen`.
- `sidecar` — 20 columns: `TrackId`, `TrackNumber`, `MediaHash`, `ContentHash`, `FileName`, `Path`,
  `Language`, `HearingImpaired`, `Forced`, `Status`, `Reason`, `StatusAt`, `Origin`, title
  identifiers, `IsSeries`, `Season`, `Episode`, `Created`, `LastSeen`.

**The membership**
- `pack_track` — `PackId`, `TrackId`, `Added`; a foreign key on both sides.

**The source level**
- `upload` — `SubjectId`, `Source`, `UploadedAt`, `RefusedAt`, `RefusedReason`, `RefusalSignature`,
  `Fingerprint`, `MtimeUnix`, `Written`.
- `reference` — `SubjectId`, `Source`, `DownloadSourceId`, `UploadedId`, `ItemId`, `SdId`, `Url`,
  `Written` (F-M672).
- `badapple` — `SubjectId`, `Source`, `SourceId`, `Member`, `Language`, `Reason`, `Finding`, `NId`,
  `ReleaseName`, `Kind`, `ReportedAt`, `ReportState`, `Written`.
- `source_matrix` — 20 columns: `Id`, `MediaHash`, the title identifiers, `Source`, `Kind`,
  `Language`, `Season`, `Episode`, `SubPos`, `HearingImpaired`, `ClosedAt`, `Fingerprint`,
  `MtimeUnix`, `SourceId`, `UploadId`, the refusal triple, `Written` (F-M650, F-M668, F-M669).

**The side tables**
- `tmdb_answers` — `Id`, `Value`: the answers a title question already got.
- `aired_seasons` — `Id`, `Value`: how many seasons a series has aired.
- `title_failures` — `Id`, `Attempts`, `Closed`, `Reason`: the title lock (F-M198).
- `source_refusals` — `Id`, `Signature`, `Reason`: a refusal that a changed content no longer matches.
- `pack_seasons` — `Id`, `Signature`, `Verdict`, `Reason`: the per-season refusal, keyed on the
  fingerprint of the content.

## 4. Keys and identifiers

- **The key is the identifier.** `FileId` is the text `#yyyyy` itself, not a number pointing at one
  (F-M676); the same for `TrackId` (`#yyyyy#zzz`) and for a loose subtitle (`#yyyyy&zzz`, F-M674).
- **The number inside is one range per file across `track` and `sidecar`**, assigned at first sight,
  never re-assigned, never reused. The ffmpeg position (`0:s:N`) is derived from it at runtime and is
  never stored as the identity.
- **One unique property per file:** `OsHash` has a unique index (`IX_file_OsHash`), and it is checked
  against path and file name — a media file that was re-hashed is the same row, a different file with
  the same name is not.
- **The content hash is the second key space**, on `track` and `sidecar` (`ContentHash`): it describes
  the bytes and therefore the duplicate guards, not the place.

## 5. The membership table

`pack_track` holds which tracks belong to which pack, with the timestamp of the joining. That is why
`file` and `track` carry no pack identifier (F-M673, F-M677): membership is a relation, not a column,
and a pack that is purged takes its rows with it while the tracks stay where they are.

## 6. The source level

Everything that belongs to **one** source lives in its own table, never in a column of the entity level
— that is what makes a third source possible without touching the entity tables (F-M668, F-M669):

- `upload` says what a source answered about one subtitle: the time it went up, or the refusal with
  its reason, its signature and the fingerprint of the content.
- `reference` keeps the foreign identifiers apart: the source's own download identifier, our upload
  identifier, the item identifier, `SdId`, a URL (F-M672).
- `badapple` keeps a bad download together with the foreign identifier **and the member** — the file
  inside the pack that was bad (F-M679).
- `source_matrix` is the mark matrix: one row per (media, source, kind, language), carrying the
  verdict, the signature and the time.

## 7. Verdicts, refusals and evidence

- A **verdict** about a pair is a fact of the row that carries the subtitle — `Status`, `Reason`,
  `StatusAt` on `track` or `sidecar`.
- A **refusal** by a source is remembered so that the same content is not offered twice: the
  fingerprint of what was refused decides whether a changed content may try again (`upload`. Refusal
  triple, `pack_seasons`, `source_refusals`).
- A **lock** closes a title after repeated failure (`title_failures`); it is the only row whose
  `Closed` flag makes the pipeline stop asking.
- **Evidence beats a refusal and a refusal beats evidence**: a success clears the refusal, a refusal
  clears the success — the two live in the same row and exclude each other.

## 8. The mark matrix

`source_matrix` is the one place the marks of all sources stand side by side (F-M667, F-M668). One row
per media, source, kind and language — no per-source table, no per-source column. Its `Fingerprint`
and the refusal triple give the matrix the same memory the `upload` table has for a single submission,
and the matrix is adopted once at the first start (F-M667).

## 9. Caches

`tmdb_answers` and `aired_seasons` are answers we already paid for: the title question and the aired
count of a season. They are caches in the strict sense — deletable without losing a decision — and
they exist so a run does not ask the same question twice.

## 10. Who writes what

The pipeline writes; an engine never does (see `CONTRACTS.md` §7 for the boundary and the full list).
The short form: the seeder writes `file` and `sidecar` observations, a download writes `track`,
`sidecar` and `reference`, an upload writes `upload` and `source_matrix`, the database refresh
reconciles and removes, and the reporting keeps the counters in their single status row (F-M207).

## 11. Takeover and migration

- **The ascent runs in ONE transaction** after a timestamped backup of the file next to it: the old
  plugin database is read, the current tables are filled, the old tables and columns are dropped.
  There is no half-migrated state to resume from.
- **A build without a takeover path creates an empty plugin database** and reads the predecessor
  neither whole nor in part (F-M639) — a deliberate trade: an empty database is honest, a half-read
  one is not.
- **The file itself lives in the plugin data folder**, next to the configuration; the backups stay in
  the files area with their timestamp in the name.

## 12. What the plugin database never holds

- **No credentials.** A source's access data lives in its own settings file, never in a row.
- **No media, no subtitle bytes.** Only their hashes, their names and their places.
- **No library inventory.** The Jellyfin item identifier is a pointer, not a copy of the library.
- **No statistics of a dry run.** A dry run creates nothing and decides nothing (F-M247).

## 13. The checks

The suites that hold this shape: `storex` (the tables and their keys), `statefile` (the file and its
takeover), `coverage` (what a reader may conclude from the rows), `matrix` (`source_matrix`) and
`hipool`. The normative rules are `F-M194`…`F-M207`, `F-M282`…`F-M285` and `F-M639`…`F-M680` in
`REQUIREMENTS.md` §14.
