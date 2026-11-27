# Data Model — the plugin database

The tables, their keys and what each one carries; the normative rules stand in §14 of `SPECIFICATION.md`.

This paper is derived from the rules in the rules are stated normatively in `§14` of `SPECIFICATION.md`. This paper describes
the shape those rules produce: the tables, their keys, what each one carries and what it never carries.
The words are those of `TERMS.md`. Where a table name and a level name are the same word, that is the
rule, not a coincidence (F-M671).*

## Contents

- [1. What it is](#1-what-it-is)
- [2. The four levels](#2-the-four-levels)
- [3. The tables at a glance](#3-the-tables-at-a-glance)
- [4. Keys and identifiers](#4-keys-and-identifiers)
- [5. The membership table](#5-the-membership-table)
- [6. The source level](#6-the-source-level)
- [7. Adding another source](#7-adding-another-source)
- [8. Verdicts, refusals and evidence](#8-verdicts-refusals-and-evidence)
- [9. The mark matrix](#9-the-mark-matrix)
- [10. Caches](#10-caches)
- [11. Who writes what](#11-who-writes-what)
- [12. Migration — how the ascent runs](#12-migration-how-the-ascent-runs)
- [13. A file from another version, and the empty case](#13-a-file-from-another-version-and-the-empty-case)
- [14. What the plugin database never holds](#14-what-the-plugin-database-never-holds)
- [15. The checks](#15-the-checks)

## 1. What it is

The plugin database is our own store of rows: identifiers, marks, locks, counters — and the only place
where the plugin's knowledge survives a restart (F-M194). It is not a cache of Jellyfin's library and
not a copy of any source's catalogue: it holds what **we** decided and what **we** saw.

## 2. The four levels

The **table name is the level**, and every identifier says which level it belongs to (F-M671, F-M676):

- **`pack`**: a whole season's subtitles for one language, as one container. Identifier `[xxxx]`.
- **`file`**: one media file. Identifier `#yyyyy`.
- **`track`**: one subtitle stream **inside** a media file. Identifier `#yyyyy#zzz`, where the file
  stands in the identifier (F-M676).
- **`sidecar`**: a loose subtitle beside the media file. Identifier `#yyyyy&zzz`, again with the file
  in it (F-M674).

Two prohibitions follow from that, and they are worth naming because they were defects before.
**file and track carry no pack identifier**, because the membership lives in its own table (F-M673,
F-M677), and **no column is called `Id`** while meaning something else.

## 3. The tables at a glance

Fourteen tables exist. Nine belong to the model proper, five are small side tables that migrations
create.

**The entity level**
- `pack` has 16 columns: `PackId`, `SeasonKey`, `Source`, title identifiers, `Season`, `Language`,
  `HearingImpaired`, `ReleaseName`, `Signature`, `MtimeUnix`, `EpisodeCount`, `Episodes`,
  `AbsorbedIds`, `Created`, `Updated`.
- `file` has 22 columns: `FileId`, `OsHash`, `FileName`, `Size`, `MtimeUnix`, `Path`, `JellyfinItemId`,
  the per-source title identifiers, `IsSeries`, `Season`, `Episode`, the search bookkeeping
  (`LastSearchUtc`, `LastSearchLanguages`, `LastSearchEmptyUtc`), `Created`, `LastSeen`. **No language,
  no hearing-impaired, no pack identifier**, because a file has no subtitle property.
- `track` has 16 columns: `TrackId`, `TrackNumber`, `MediaHash`, `SubPos`, `Language`,
  `HearingImpaired`, `Forced`, `ContentHash`, `Status`, `Reason`, `StatusAt`, `Origin`, the
  detection fields, `Created`, `LastSeen`.
- `sidecar` has 20 columns: `TrackId`, `TrackNumber`, `MediaHash`, `ContentHash`, `FileName`, `Path`,
  `Language`, `HearingImpaired`, `Forced`, `Status`, `Reason`, `StatusAt`, `Origin`, title
  identifiers, `IsSeries`, `Season`, `Episode`, `Created`, `LastSeen`.

**The membership**
- `pack_track` holds `PackId`, `TrackId` and `Added`, with a foreign key on both sides.

**The source level**
- `upload` has `SubjectId`, `Source`, `UploadedAt`, `RefusedAt`, `RefusedReason`, `RefusalSignature`,
  `Fingerprint`, `MtimeUnix`, `Written`.
- `reference` has `SubjectId`, `Source`, `DownloadSourceId`, `UploadedId`, `ItemId`, `SdId`, `Url`,
  `Written` (F-M672).
- `badapple` has `SubjectId`, `Source`, `SourceId`, `Member`, `Language`, `Reason`, `Finding`, `NId`,
  `ReleaseName`, `Kind`, `ReportedAt`, `ReportState`, `Written`.
- `source_matrix` has 20 columns: `Id`, `MediaHash`, the title identifiers, `Source`, `Kind`,
  `Language`, `Season`, `Episode`, `SubPos`, `HearingImpaired`, `ClosedAt`, `Fingerprint`,
  `MtimeUnix`, `SourceId`, `UploadId`, the refusal triple, `Written` (F-M650, F-M668, F-M669).

**The side tables**
- `tmdb_answers` has `Id` and `Value`: the answers a title question already got.
- `aired_seasons` has `Id` and `Value`: how many seasons a series has aired.
- `title_failures` has `Id`, `Attempts`, `Closed` and `Reason`: the title lock (F-M198). Its write date is the anchor of the pause (F-M771) — the moment the title opens for its one probe is computed from it, so no column is added for a date that can be computed.
- `source_refusals` has `Id`, `Signature` and `Reason`: a refusal that a changed content no longer matches.
- `pack_seasons` has `Id`, `Signature`, `Verdict` and `Reason`: the per-season refusal, keyed on the
  fingerprint of the content.

## 4. Keys and identifiers

- **The key is the identifier.** `FileId` is the text `#yyyyy` itself, not a number pointing at one
  (F-M676); the same for `TrackId` (`#yyyyy#zzz`) and for a loose subtitle (`#yyyyy&zzz`, F-M674).
- **The number inside is one range per file across `track` and `sidecar`**: the plugin assigns it at
  first sight and never re-assigns or reuses it. It derives the ffmpeg position (`0:s:N`) from it at
  runtime and never stores it as the identity.
- **One unique property per file:** `OsHash` has a unique index (`IX_file_OsHash`), and the plugin
  checks it against path and file name: a media file that was re-hashed is the same row, a different
  file with the same name is not.
- **The content hash is the second key space**, on track and sidecar (`ContentHash`): it describes
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
- `badapple` keeps a bad download together with the foreign identifier **and the member**, the file
  inside the pack that was bad (F-M679).
- `source_matrix` is the mark matrix: one row per (media, source, kind, language), carrying the
  verdict, the signature and the time.

## 7. Adding another source

**A source is a value, never a schema object.** That is the whole rule, and it is what makes a third
source possible (F-M668, F-M669): there is no table per source, no column per source, and nothing in
the model knows a source by name.

What a new source brings, and where it appears without any schema change:

- its **settings file of its own** and its entry in the catalogue, both outside the database
  (F-M683);
- rows in **`source_matrix`** as soon as it has marks: one row per (media, source, kind, language), and
  for a track also per **position**, because `file` and `pack` carry no position (F-M669);
- rows in **`upload`** when it answers a submission, with the refusal triple when it refuses;
- rows in **`reference`** for its own identifiers: the download identifier, our upload identifier, the
  item identifier (F-M672);
- rows in **`badapple`** when a download from it turned out bad, with the foreign identifier and the
  member inside the pack (F-M679).

**The test of the model:** if attaching a source ever needs a migration or a schema version bump, the
model is wrong and the new column is the defect. A source that is withdrawn keeps its rows — they are
history — but nothing reads them any more, and its marks are never read as another source's.

## 8. Verdicts, refusals and evidence

- A **verdict** about a pair is a fact of the row that carries the subtitle: `Status`, `Reason`,
  `StatusAt` on `track` or `sidecar`.
- The plugin remembers a **refusal** by a source so that the same content is not offered twice: the
  fingerprint of what was refused decides whether a changed content may try again (`upload`, the
  refusal triple, `pack_seasons`, `source_refusals`).
- A **lock** closes a title after repeated failure (`title_failures`); it is the only row whose
  `Closed` flag makes the pipeline stop asking — and the only one that stops asking only for a while, because the pause is read from its write date (F-M771).
- **Evidence beats a refusal and a refusal beats evidence**: a success clears the refusal, a refusal
  clears the success — the two live in the same row and exclude each other.

## 9. The mark matrix

`source_matrix` is the one place the marks of all sources stand side by side (F-M667, F-M668). One row
per media, source, kind and language — no per-source table, no per-source column. Its `Fingerprint`
and the refusal triple give the matrix the same memory the `upload` table has for a single submission,
and the build adopts the matrix once at the first start (F-M667).

## 10. Caches

`tmdb_answers` and `aired_seasons` are answers we already paid for: the title question and the aired
count of a season. They are caches in the strict sense — deletable without losing a decision — and
they exist so a run does not ask the same question twice.

## 11. Who writes what

The pipeline writes; an engine never does (see `CONTRACTS.md` §7 for the boundary and the full list).
The short form: the seeder writes `file` and `sidecar` observations, a download writes `track`,
sidecar and `reference`, an upload writes `upload` and `source_matrix`, the database refresh
reconciles and removes, and the reporting keeps the counters in their single status row (F-M207).

## 12. Migration — how the ascent runs

**The version stands in one row.** `meta` with the identifier `db` carries `SchemaVersion`; this build
writes **24**. Every start compares that marker with what the build understands, and there are exactly
four cases:

- **no marker** (a file that did not exist) → the plugin initialises the file at the current version
  and writes one Normal line that says so;
- **marker older than this build** → the ascent runs, then raises the marker; the build **ignores
  records of another schema version rather than rewriting them**;
- **marker newer than this build** → **the plugin disables writing for the whole session**, with an
  error that says so and asks the operator to update. An older build never damages a newer file;
- **marker unreadable** → the check logs a warning and lets the plugin start. A broken compatibility
  check must not keep the plugin from running.

**The ascent itself** (F-M668, F-M670):

1. **The ascent takes a timestamped copy of the file first** — that copy is the only place material
   survives that cannot be placed (see below).
2. **Everything is read before the transaction**, and the write happens inside **one** transaction:
   there is no half-migrated state to resume from.
3. **The condition is the stock, not the marker.** As long as any legacy wide mark column, the old
   `source_marks` table or a legacy `SubdlId` column stands anywhere, there is something to take over.
   When all of them are gone the run does nothing — it is idempotent, so the next start repeats an
   aborted ascent instead of remembering it as finished.
4. **The ascent only narrows.** Wide columns become rows of the matrix; nothing is invented, and no
   value is guessed into a row.
5. **Rows without an anchor**, meaning no media hash and no owner row, cannot be placed in the current
   model. They live on **in the backup only**, and the ascent says so openly rather than patching them
   into a row (F-M670).

**A new build creates the tables it adds** on a file that predates them, once, on its first start
(the caches are the example) — a missing table is not an ascent, it is a creation.

## 13. A file from another version, and the empty case

- **Downgrade.** A file written by a newer build is read-only for this session. The log carries an
  error naming both numbers; the build writes no row and touches no table.
- **A build without a takeover path creates an empty plugin database** and reads the predecessor
  neither whole nor in part (F-M639). That is a deliberate trade: an empty database is honest, a
  half-read one is not.
- **Backups** carry their timestamp in the name and live in the files area; a migration never deletes
  the copy it took.

## 14. What the plugin database never holds

- **No credentials.** A source's access data lives in its own settings file, never in a row.
- **No media, no subtitle bytes.** Only their hashes, their names and their places.
- **No library inventory.** The Jellyfin item identifier is a pointer, not a copy of the library.
- **No statistics of a dry run.** A dry run creates nothing and decides nothing (F-M247).

## 15. The checks

The suites that hold this shape: `storex` (the tables and their keys), `statefile` (the file and its
takeover), `coverage` (what a reader may conclude from the rows), `matrix` (`source_matrix`) and
`hipool`. The normative rules are `F-M194`…`F-M207`, `F-M282`…`F-M285` and `F-M639`…`F-M680` in
`SPECIFICATION.md` §14.
