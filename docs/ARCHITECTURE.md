# Architecture — the map

One plugin, four bands. The band on top decides what work exists, the band below it talks to the world, the third
keeps what was decided, and the last one shows it. Nothing skips a band: a decision never reaches a website without
passing the gates, and a website never writes into the memory.

```
WHO DECIDES

   Zeit (Koordinator) ──► Dispatcher ──► Seeder ──► Warteschlange
                              ▲                         │
   Ankunft (Wächter) ────────┘                         ▼
                                        ┌──────────────┴──────────────┐
                                        ▼                             ▼
                                 Download-Pipeline            Upload-Pipeline
                                        │                             │
                                        └──────────►  Tore  ◄──────────┘
THE WORLD                                            │
                                                     ▼
                                   Quellen-Registry ──► Engine SubDL
                                                     ──► Engine tvst
                                                     ──► Engine mst
WHAT REMAINS                                         │
                                                     ▼
                    Bestand: Urteile, Marken, Signaturen ──► Datenbank
                    Speicher: TMDb-Antworten, Texte, Archive ──► Platte
WHO SHOWS IT                                         │
                                                     ▼
                             Arbeiterzeilen ──► Seite (liest, entscheidet nichts)
```

## The band that decides

The **coordinator** owns every due time. The five Jellyfin tasks carry empty trigger lists, so nothing fires beside
its clock; a due job is queued through the task manager so it stays visible in the dashboard and can still be
started by hand.

The **watcher** feeds the dispatcher when a file appears. It does not start work; it hands over the changed items,
and the collector waits until nothing more comes. The **dispatcher** turns that into a cycle: it asks the seeder,
runs both directions, and reports the cycle line under the worker list.

The **seeder** is the producer and only the producer. It walks the selected libraries and turns every movie and
episode with a media file on disk into a queue candidate; it writes no verdict and sends nothing. The **queue** is
one file with two lists, one per direction, ordered last in first out by item creation time, with retry counters and
tombstones that survive a restart.

The two **pipelines** are where a decision starts. Download asks for a missing language, picks a candidate, moves it
onto the audio, writes the file and books the answer. Upload reads the text tracks out of the container, weighs each
stream, builds a container and sends it. Neither knows a website by name, and neither writes without the lock.

The **gates** are the only place that refuses. They receive text and a track, never a promise, and their refusal
names its cause. **Auto-Sync** is a worker of its own beside them: it is handed a subtitle, measures it against the
audio and hands back a status.

## The band with the world

The **registry** turns a source name into an engine and wraps it in an auditing decorator, so every site is counted
at the same place. The three **engines** answer four things and nothing else: search, fetch, send, report. Each has
its own settings file, its own language bridge and its own idea of what a daily allowance means.

## The band that remains

The **store** holds what was decided and is the only thing that survives a restart: a verdict per pair of content
and language, a mark per source, a refusal with the signature of what was refused. It writes through the
**database**, and its keys are identifiers, not row numbers.

A **cache** holds what can be recomputed: TMDb answers, extracted texts, downloaded archives. It may be emptied at
any time and the plugin only pays again. That is the whole difference from the store, and it decides where a new
value belongs.

## The band that shows

The **worker rows** are written by the workers themselves, one row per worker, and each row reports only its own
result. The **page** reads facts and rows and never decides anything; the API end that serves it is read-only for
everything the plugin believes.

## If you want to change something, touch this

| You want to … | Touch | Read first |
| --- | --- | --- |
| change when a run happens | the coordinator's anchors and the job spacing | [RUNS.md](RUNS.md) |
| change what an arrival covers | the dispatcher's scope and the seeder's filter | [RUNS.md](RUNS.md) |
| add or change a refusal | the gates, and the reason string where it is raised | [GATES.md](GATES.md) |
| add a fourth site | a folder under `Sources`, a settings file, a language bridge | [CONTRACTS.md](CONTRACTS.md), §21 of the specification |
| change a file name | `SidecarNaming` and every writer that builds one | [NAMING.md](NAMING.md) |
| change what a run says | the line's wording and the debug level it needs | [LOGGING.md](LOGGING.md) |
| add a stored value | the store if it is a decision, a cache if it can be recomputed | [DATABASE.md](DATABASE.md), [CACHING.md](CACHING.md) |
| change a column or a key | the schema, the migration, and every stored key of that form | [DATABASE.md](DATABASE.md) |
| change the page | the settings object, the HTML, the structure test | [GUI.md](GUI.md) |
| change a quota stop | the quota snapshot and the stop decision | [QUOTA.md](QUOTA.md) |
| change how many tracks are extracted | the extraction pass and its look-ahead | [EXTRACTION.md](EXTRACTION.md) |
| change the queue's behaviour | the item states and the retry ceiling | [QUEUE.md](QUEUE.md) |
| change how a subtitle is aligned | the drift gate, the fit and the worker | [AUTOSYNC.md](AUTOSYNC.md) |
| change a title lookup | the ladder and its pause | [TITLE-RESOLUTION.md](TITLE-RESOLUTION.md) |

## Three rules that hold the whole map

One writer at a time: the lock covers everything that writes, because both directions and all the background work
share one store. One decision per place: a store never decides, a page never writes, an engine never books. And one
arrow direction: a pipeline may call the contract, the store and the gates; nothing calls back up into a pipeline.

[SPECIFICATION.md](SPECIFICATION.md), §1, §2, §3, §4, §14, §21; F-M94h, F-M194, F-M233, F-M624, F-M668, F-M669,
F-M682, F-M683. Guarded by `coverage`, `storex`, `endpoints` and `gui-structure`.
