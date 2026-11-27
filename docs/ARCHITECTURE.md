# Architecture — who decides, who may ask whom

This paper does not list folders; it answers the four questions a new contributor gets wrong at first. **Who owns
the time?** **Who owns the decision about a subtitle?** **Who owns the memory?** And **which arrow may point which
way?** Everything else in the plugin follows from those four answers, and every one of them was paid for with a
measured defect.

## Decision one — one owner of the time

Nothing in the plugin starts itself. The five Jellyfin tasks carry empty trigger lists, a coordinator owns every due
time, and it queues a task when a slot falls due. The price is that a missed slot must be caught up and that a job
which cannot take the lock must reschedule itself; the gain is that two owners of one time can never double-fire.

## Decision two — one boundary, and the source is a value

The pipeline knows four methods — search, fetch, send, report — and never a website. The name of a source is data
that arrives from the registry, an engine is constructed behind that name, and no pipeline file branches on a site.
The price is that anything a site needs must fit those four answers; the gain is that a fourth site is a folder and
a settings file, with no schema migration and no change to a pipeline.

The registry does not hand out the raw engine. It wraps it in an auditing decorator that records what a source
answered, so the counters and the report are produced at the same place for every site.

## Decision three — one memory, two directions

Both directions read and write the same store, so they may never run at the same time. One lock covers the seeder,
both pipelines, the postprocessing, the database refresh and the hash refresh; whoever cannot take it gets a refusal
and comes back after the job spacing. The price is that a slow upload delays a download; the gain is that the store
has exactly one writer at any moment, which is why a verdict cannot be written twice for the same pair by accident.

## Decision four — a cache is not a memory

The store holds decisions: a verdict about a pair of content and language, a mark per source, a refusal with its
signature. A cache holds things that can be recomputed: TMDb answers, extracted texts, downloaded archives. The two
live in different places on purpose — a cache is deletable at any time and the plugin only pays again, while a
verdict that vanished would change what the plugin believes. That single distinction decides where a new value
belongs.

## The path of one run

```
   time ─────────────────────────────► coordinator (owns every due time)
                                             │ queues a Jellyfin task
                                             ▼
   watcher ──► dispatcher ──► seeder ──► queues (one file, two lists)
                   │                          │
                   │                ┌─────────┴──────────┐
                   │                ▼                    ▼
                   │        download pipeline      upload pipeline
                   │                │                    │
                   │                └──► gates ◄──────────┘
                   │                        │
                   │                source registry
                   │                 │            │
                   │           engine            engine      (one per site)
                   │                 │            │
                   └────────────► worker rows ──► page (reads, never decides)
                                        │
        both pipelines ──► registry (verdicts, marks) ──► database
                        └► caches (disk) — deletable at any time
```

A run collects before it decides. The dispatcher and the seeder only gather candidates; the decision starts in the
pipeline, passes the gates, and ends in a store write. That is why an arrival run can be narrowed to the arrived
items without changing any rule: narrowing removes candidates, never decisions.

## The seams, and what never crosses them

| Seam | What crosses | What never crosses |
| --- | --- | --- |
| dispatcher → pipeline | a direction, an item, a scope | a source, a verdict, a container |
| pipeline → contract | a search request, a fetch request, a send request | a site name, a table name, a row |
| contract → engine | the site's own parameters and answers | our identifiers, our verdicts |
| pipeline → gates | subtitle text and its track | a promise about the audio |
| pipeline → store | a verdict with its signature and time | media bytes, credentials |
| store → database | rows whose keys are identifiers | a decision the store has not made |
| api → page | facts and rows, read only | a decision, a write, a switch |

## The truth about the folders

The folders are topics, not layers. A dependency graph built from `using` lines shows cycles, and the cause is a
handful of cross-cutting types rather than careless design: `LogUtil` sits in `Pipeline` and is called from eight
files in `ScheduledTasks`, seven in `Api`, four in `Registry` and one in `Data`. What is a real layer is the type,
not the folder — the engine contract, the store interfaces and the gate entry points. A new file belongs where its
topic lives, and the arrows above are what it must keep.

## What a new feature has to touch

Five steps, in this order: name the decision and put it in the pipeline or the gates, never in a store. Write the
requirement in `SPECIFICATION.md` and the test in `TESTS.md` before the code. Put a new value in the store if it is
a decision and in a cache if it can be recomputed. Take the lock if the run writes anything. And let the worker row
report only its own result.

## Where the rest is written

The parts themselves each have a paper. The time and the triggers stand in [RUNS.md](RUNS.md), the queue in
[QUEUE.md](QUEUE.md). The two directions are told four times, once per way: [SINGLE-DOWNLOAD.md](SINGLE-DOWNLOAD.md),
[SINGLE-UPLOAD.md](SINGLE-UPLOAD.md), [SEASON-PACK-DOWNLOAD.md](SEASON-PACK-DOWNLOAD.md),
[SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md). The gates stand in [GATES.md](GATES.md), the boundary in
[CONTRACTS.md](CONTRACTS.md), and each site has one paper of its own. The two kinds of memory are
[DATABASE.md](DATABASE.md) and [CACHING.md](CACHING.md), the page is [GUI.md](GUI.md), and what a run says while it
works is [LOGGING.md](LOGGING.md).

[SPECIFICATION.md](SPECIFICATION.md), §1, §2, §3, §4, §14, §21; F-M94h, F-M194, F-M233, F-M624, F-M668, F-M669,
F-M682, F-M683. Guarded by `coverage`, `storex`, `endpoints` and `gui-structure`.
