# Architecture

The plugin is one Jellyfin host process with one job: take the text subtitles that sit inside a library's media
files and put them on the subtitle sites, and bring back the subtitles a library is missing. Everything below is
arranged around that sentence — a direction that produces work, a boundary where a site sits, and a store that
remembers what was already decided. The rules themselves stand in `SPECIFICATION.md`; this paper says which part
does what and which part may talk to which.

## The parts

Eleven folders carry the code, and their names are the architecture. A part owns one kind of question and answers to
nobody above it.

| Part | What it owns | Where it is written down |
| --- | --- | --- |
| `ScheduledTasks` | the time, the triggers, the collection of arrived files, the five Jellyfin tasks | [RUNS.md](RUNS.md) |
| `Pipeline` | the two directions: download, upload, extraction, season containers, the budget | [SINGLE-DOWNLOAD.md](SINGLE-DOWNLOAD.md), [SINGLE-UPLOAD.md](SINGLE-UPLOAD.md), [SEASON-PACK-DOWNLOAD.md](SEASON-PACK-DOWNLOAD.md), [SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md), [EXTRACTION.md](EXTRACTION.md) |
| `Sources` | the boundary to every site, the source registry, the settings of a source, the language bridges | [CONTRACTS.md](CONTRACTS.md), [SUBDL.md](SUBDL.md), [TVSUBTITLES.md](TVSUBTITLES.md), [MOVIESUBTITLES.md](MOVIESUBTITLES.md) |
| `Qa` | the gates a subtitle passes and the alignment worker | [GATES.md](GATES.md), [AUTOSYNC.md](AUTOSYNC.md) |
| `Registry` | every store the pipeline reads or writes, the names on disk, the counters | [DATABASE.md](DATABASE.md), [NAMING.md](NAMING.md) |
| `Data` | the plugin database: schema, identifiers, migrations | [DATABASE.md](DATABASE.md) |
| `Api` | the HTTP end where the settings page and the dashboard read, and the disk stores that are not rows | [ENDPOINTS.md](ENDPOINTS.md), [CACHING.md](CACHING.md) |
| `Language` | the one translation between language codes | [LANGUAGE-CODES.md](LANGUAGE-CODES.md) |
| `Configuration` | the settings object and the change log | [GUI.md](GUI.md) |
| `Filters`, `Text` | the skip filter and the text folding used by the name and cue work | [NAMING.md](NAMING.md) |

Three files stand in the root and belong to no folder: `Plugin.cs` holds the instance and the start run,
`PluginServiceRegistrator.cs` wires everything into the host, and `LibraryScope.cs` answers the one question of
which library items are selected.

## The direction a run takes

A run begins with a trigger and ends with work in the store. The trigger wakes the dispatcher, the dispatcher asks
the seeder for candidates, the seeder writes them into the persisted queues, and the pipelines take one item at a
time out of a queue. Everything before the pipeline only collects; everything from the pipeline onward decides.

The download direction asks the sources for a language a file is missing, fetches the best candidate, moves it onto
its audio track, writes the file beside the media and books the result. The upload direction reads the text tracks
out of the media container, refuses or accepts each stream at the gates, builds a container per file or per season
and sends it, then books what each source answered. Both directions write into the same store and read the same
marks, which is why they can never run at the same time.

The store sits between the two directions and is the only thing that survives a restart. It holds what was decided
— a verdict per pair of content and language, a mark per source, a refusal with its signature — and never the media
itself. A cache sits beside it for everything that can be recomputed, and the difference between the two is the rule
that a cache may be deleted at any time while a verdict may not.

## The boundary, and the arrows

A pipeline knows the source contract and never a source. The contract has four methods — search, fetch, send and
report — and nothing else, and the concrete engine behind it is chosen by the registry from a name that is a data
value.
That is why a third site is a folder and a settings file and no migration, and why no pipeline file contains a site
name to branch on.

Arrows point one way. The pipeline calls the contract, the store and the gates; the contract calls its own HTTP
client; the store calls the database and the disks; the API layer reads all of it and is read by the settings page.
Nothing calls upward: a store never asks a pipeline what to do, an engine never writes a row, and the settings page
never decides anything — it writes settings and reads facts.

## What the architecture refuses

It refuses a fixed clock time, because one owner of due times is enough and two owners double-fire. It refuses a
table or a column per source, because a source is a value and a fourth site must cost no migration. It refuses a
cache that carries a decision, because a cleared cache would then change what the plugin believes. It refuses a
worker that reports another worker's result, because a lit lamp that means "something is running" says nothing. And
it refuses an invented number in a refusal: a gate that cannot measure says so, and a refusal names its cause.

## Where the rest is written

The specification states the rules, the test catalogue names how each one is proven, the style sheet says how a
paper is written, and the glossary fixes one word per concept. Each functional group then has its own paper, and
this page is only the map: [TERMS.md](TERMS.md), [STYLE.md](STYLE.md), [SPECIFICATION.md](SPECIFICATION.md),
[TESTS.md](TESTS.md).

[SPECIFICATION.md](SPECIFICATION.md), §1, §2, §3, §4, §14, §21; F-M194, F-M624, F-M668, F-M669, F-M671, F-M682, F-M683.
Guarded by `coverage` (who may read what the store holds), `storex` (the tables and their keys), `endpoints` (the
HTTP end) and `gui-structure` (the page reads and never writes).
