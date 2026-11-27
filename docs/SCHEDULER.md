# The scheduler

One class owns every due time: `ScribeSchedulerCoordinator`. The five Jellyfin tasks carry no triggers of
their own — `GetDefaultTriggers()` returns an empty list in all of them — so nothing fires on a Jellyfin
timer beside the coordinator's clock. That decision was taken because two owners of the same time produced
double runs, and because a Jellyfin timer cannot know that another job holds the lock.

The coordinator ticks every 30 seconds. When a job is due it is queued through `ITaskManager`, so it stays
visible in the dashboard and can still be started by hand; the event dispatcher executes it. The tick
itself does no work.

The anchors — the times at which the regular refetch runs are due — are diced once per installation from a
cryptographic random source and then persisted in the plugin settings. Two installations therefore never
start at the same minute, and a restart does not re-dice. This replaces every fixed clock time the plugin
ever had.

A due job takes the global run lock. If the lock is busy, the job is not dropped and not queued twice: it
defers by `JobSpacingMinutes` (15 on the measured host) and tries again. Nothing overlaps, nothing is lost,
and the deferral carries its reason so the dashboard shows why a run is waiting.

The slot markers live in two files beside the plugin database, `anchor-fires.json` and `recovery-fires.json`.
They have to, because the dedup test compares a marker against the current slot: kept in memory only, every
Jellyfin restart inside the same slot re-claimed it. Measured before the fix: 23 restarts on one anchor day
produced 22 OSHash fires against exactly one real anchor fire.

A missed slot is caught up once. If the plugin was down when an anchor was due, the slot fires on the first
tick where it is past due and unconsumed, and the persisted marker keeps it from stacking or replaying
across a restart. A slot that was refused keeps its refusal; the next regular anchor picks the work up
again.

| Job | What it does | Default trigger |
| --- | --- | --- |
| `SubDL/TMDB — Subtitle Upload` | collects the libraries and sends embedded text tracks | none, coordinator |
| `SubDL/TMDB — Subtitle Download` | searches and fetches missing subtitles as loose files | none, coordinator |
| `SubDL Postprocessing` | moves stored upload ids to the published entry ids, pages to the watermark | none, manual or chained |
| `Sub Scribe — Database Refresh` | checks stored state against the filesystem, weekly anchor | none, coordinator |
| `Sub Scribe — OSHash Cache Refresh` | recomputes expired or fingerprint-changed hashes | none, coordinator |

The seeder is the producer in front of both pipelines. One pass over the selected libraries turns every
movie and episode with a media file on disk into a queue candidate; the dispatcher merges that snapshot into
the persisted queues, where retry counters and tombstones survive. On the event path the seeder is handed
the libraries of the changed items and scans only those, growing back to "all" above the targeted-set limit.
Ordering is last in, first out by item creation time.

What it does not do: it does not run two directions at once, it does not fire a job twice for one slot, it
does not fire at a fixed clock time, and it does not keep a timer in the dashboard. It also does not rescan
in a tight loop — a refusal returns the slot to the next regular anchor instead of retrying in minutes.

What it costs, measured on the host: an upload run takes about 30 minutes, the weekly database refresh 4
minutes, the hash refresh and the download run under a minute each. The 30-second tick and the deferral
spacing cost nothing measurable; the jobs themselves dominate.

[SPECIFICATION.md](SPECIFICATION.md), F-M25, F-M48, F-M65, F-M111, F-M116, F-M119, F-M152, F-M156, F-M445.
Guarded by `endpoints` (T436 runs and triggers) and by the dashboard structure test.
