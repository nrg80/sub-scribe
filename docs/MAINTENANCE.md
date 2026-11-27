# Maintenance — the four jobs that need no run

Four jobs touch what the plugin stores without a subtitle being decided: the upload postprocessing, the database
refresh, the OSHash refresh, and the reset with its five scopes. None of them sends, fetches or judges a subtitle.
They match, verify, compact and forget, and all four take the same global lock as the two directions, so none of them
ever overlaps a run.

| Job | When it runs | What it changes | What it costs |
| --- | --- | --- | --- |
| Upload postprocessing | its own cadence setting or a manual start | stored upload ids, rejected entries, the watermark | one source call per open upload |
| Database refresh | its own weekly anchor or a manual start | verdicts, marks, dead rows, then the file itself | minutes of lock, one backup file |
| OSHash refresh | its own weekly anchor or a manual start | hash entries of changed or expired files | one stat per entry, one read per recompute |
| Reset | only by hand, never on a schedule | everything a scope names, after a backup | the state of that scope, plus recomputation |

## Upload postprocessing

The postprocessing waits until a run has finished, by design: the upload path does not search the site for
duplicates before it uploads, and a duplicate that slipped through is accepted and cleaned up here (`F-M184`). It
walks the stored upload ids of the open files, asks the source what became of them, deletes the entries the source
rejected and marks them as a remote duplicate, and pages until the watermark is reached (`F-M17y`).

It carries its own cadence setting and its own manual start button, and the scheduled binding applies to the
scheduled run only — a manual run always works (`F-M210`, `F-M210a`, `F-M291`). The button on the page starts the
Jellyfin task, not the HTTP route, because only the task writes the worker row (`F-M332`). When the lock is busy the
job reschedules at the job spacing instead of waiting; after sixteen attempts it gives up with a single warning
(`F-M176`, `F-M205`). Its line at the end reports the four counts it worked through:
`Upload postprocessing DONE in 10s: … matched rejected … deleted+remote-duplicate, … unmatched`.

## Database refresh

The refresh runs one fixed order in one run under the lock and reports what it changed (`F-M274`). It probes the item
set and drops rows whose item is gone. Then it checks the file side, the embedded side and the pack side. The
compaction of the file is the last step.

Every step shares the same fail-safe: a root that cannot be listed, an item that cannot be resolved, an empty
stream list, means *unknown*, and unknown is never deleted (`F-M234`, `F-M258`). A stored language set that covers
the configured one counts as complete, so removing a language does not invalidate every mark. Verdicts without
evidence are discarded and rebuilt by the next run (`F-M583`); a row that carries a verdict or a detection attempt
is kept. Pack rows whose pack identifier no longer stands on any subtitle row are removed (`F-M404`).

The compaction is measured as the whole footprint, data file plus journal, before and after, with the journal folded
in first, and `ANALYZE` runs as its own guarded step (`F-M214`, `F-M342`). A rebuild that cannot release the pages is
replaced by a rebuild from the file's own rows, counted against the original, and swapped by rename; the previous
file survives as exactly one timestamped backup (`F-M237`). A failed compaction never fails the task and never leaves
the file dead: it is probed with a real query first (`F-M236`).

## OSHash refresh

The refresh works on a snapshot and decides per entry from the file itself (`F-M275`). Every entry is stat-ed for
size and modification time; a fingerprint mismatch recomputes the hash, an unchanged fingerprint recomputes it only
once its trust window has passed. A missing or unreadable file keeps its entry and counts as skipped, the cache is
flushed at the end, and the line reports entries, changed, missing and skipped.

Its anchor is its own, diced once per installation and weekly, so it never competes with the database refresh
(`F-M119`). The value it writes is held against an independent implementation of the published algorithm, not
against itself (`F-M279`).

## The reset, and its five scopes

The reset is the only way to make the plugin forget on purpose. It never runs on a schedule, it requires
`confirm=true`, and it copies the database to a timestamped backup before it touches anything — with the journal
folded in first, because the copy of a main file alone misses every write since the last checkpoint (`F-M90`,
`F-M181`).

| Scope | What is forgotten | `dry=true` |
| --- | --- | --- |
| `all` | the database file and its side files, the archive cache, the TMDb shelf, the counters | no |
| `upload` | the embedded-track rows and every file's upload-complete marker | no |
| `download` | downloaded sidecars, burned candidates, QA counters, search stamps | no |
| `pack` | the stored season verdicts, all of them or one series with `imdb` and `season` | no |
| `season` | one season completely: rows, tracks, sidecars, marks, references, containers, verdicts, aired episodes, refusals, title exhaustion | yes |

**`dry=true` counts for `season` and for nothing else.** On `pack` and on `all` the flag is accepted, counted and
then ignored: the rows are deleted. A reset must never be planned as a dry run in the hope that it only reports.

A reset starts nothing. It does not scan the library, does not send and does not re-upload; the next run decides
again from what is left. An `all` reset is the expensive one, because it also empties the archive cache and the TMDb
shelf, so the next run pays for every extraction and every lookup once more (`F-M427`, `F-M772`).

Two routes keep the backups reachable: the list route names the stamps present next to the data file, and the
restore route takes one of them back under `confirm=true`.

## What this paper does not cover

A run is described in [RUNS.md](RUNS.md); the store and its migrations in [DATABASE.md](DATABASE.md); the two kinds
of cache in [CACHING.md](CACHING.md); the counters and the worker rows in [GUI.md](GUI.md).

[SPECIFICATION.md](SPECIFICATION.md), §5, §6, §7; F-M90, F-M94, F-M119, F-M176, F-M181, F-M184, F-M205, F-M210,
F-M214, F-M234, F-M236, F-M237, F-M258, F-M274, F-M275, F-M291, F-M332, F-M342, F-M404, F-M427, F-M583, F-M739,
F-M740, F-M772. Guarded by `storex`, `statefile`, `workfiles` and `endpoints`.
