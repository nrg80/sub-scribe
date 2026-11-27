# The runs

Everything the plugin does happens in a cycle, and one of five triggers starts it. A trigger names a
direction and a set of items; one lock decides who writes; the gates decide what happens to an item.

## The five triggers

The direction follows the trigger through `DirectionOf`: the two scheduled triggers carry one
direction each, the arrival triggers run both, and a recovery fire runs the direction it was armed
for. `arrival-upload` and `arrival-download` map to a direction too, but nothing starts them today.

| Trigger | Who starts it | Direction | What it reads |
| --- | --- | --- | --- |
| `scheduled-upload` | the coordinator's uploaded anchor, or the dashboard button | upload only | the whole selection |
| `scheduled-download` | the coordinator's download anchor, or the dashboard button | download only | the whole selection |
| `event` | the arrival watcher, after its debounce | both | the ids the collector gathered |
| `arrival-followup` | 90 seconds after the cycle that drained the collector | both | the ids that arrived during that cycle |
| recovery | a one-shot fire per direction, at a reset time plus a dice | the direction it was scheduled for | the whole selection |

## What an arrival covers

An arrival is a file that appeared while the plugin was watching: a finished episode or movie, a
subtitle dropped into a folder. It is the only path that knows what arrived. The watcher hands the
changed items to a collector, which waits until nothing more comes and turns them into one cycle in
both directions. Arrivals during that cycle are latched with
`Event during cycle — next seed round will include it`, and a follow-up fires 90 seconds later on
those ids.

## The run that covers everything

Full coverage belongs to the scheduled, manual and recovery runs: those three pass no id set, scan
every selected library, and catch what an arrival cannot — items that arrived while the plugin was
down, files whose earlier run was refused, every refetch that fell due. A button press and a
scheduled fire are the same run: the log line says `scheduled-upload` for both, measured on the host
by pressing the button. A recovery fire carries a reason (`quota`, `respaced`, `scanNN`).

## The bounds of an arrival

The ids travel with the cycle and bound it: the dispatcher passes them to the seeder (F-M233), which
skips every item outside the set (`SkipForArrivalScope`), and the libraries of the changed items are
the second bound. An empty collector does not run at all and logs `no arrivals collected — no run`.
The seeder feeds both pipelines: one pass over the selected libraries turns every movie and episode
on disk into a queue candidate, and the dispatcher merges that snapshot into the persisted queues,
where retry counters and tombstones survive. The gates are untouched: a narrow cycle still refuses a
file whose pair is already uploaded and still respects the source switches.

## The time owner

One class owns every due time: `ScribeSchedulerCoordinator`. The five Jellyfin tasks carry no
triggers of their own: `GetDefaultTriggers()` returns an empty list, so nothing fires beside the
coordinator's clock. Two owners of the same time produced double runs. The coordinator ticks every
30 seconds and queues a due job through `ITaskManager`, so it stays visible in the dashboard and can
still be started by hand. The anchors are diced once per installation from a cryptographic random
source and persisted in the plugin settings, so no restart re-dices them.

## One lock and its stale rules

One lock covers all six parts that write state: the seeder, the download run, the upload run, the
postprocessing run, the database refresh and the hash refresh. They all touch the same store, so one
mutual exclusion is what the design needs. Nobody waits: a caller that cannot take it gets `false`
and reschedules by `JobSpacingMinutes` (15 on the measured host). The earlier version polled a block
file every five seconds for up to ten minutes, produced 36 identical "already active" lines and a
three-and-a-half minute stall, and prevented no run. Inside one cycle seed, download and upload hand
over with a 20-second grace. The file `pipeline.block` in the plugin folder carries the holder as
`PID|UTC timestamp`, so a run that died without cleaning up is recognised instead of waited out.
Three rules take it over — the process is gone, or the stamp is older than six hours and the holder
is wedged, or the file is older than a day — and any leftover file is deleted at startup. The lock
fails open, so a malformed file never wedges the plugin.

## The markers that survive a restart

The slot markers live in two files beside the plugin database, `anchor-fires.json` and
`recovery-fires.json`, because the dedup test compares a marker against the current slot: kept in
memory only, every restart inside the same slot re-claimed it. Measured before the fix: 23 restarts
on one anchor day produced 22 OSHash fires against exactly one real anchor fire.

## Catch-up

A missed slot is caught up once: if the plugin was down when an anchor was due, the slot fires on
the first tick where it is past due and unconsumed. A refused slot keeps its refusal; the next
regular anchor picks the work up.

## The five Jellyfin tasks, and the jobs that are not cycles

The five tasks below are what an operator sees in the dashboard. None of them carries a trigger of its own;
the coordinator fires them, which is why a trigger list of `[]` is the rule and not an omission.

| Job | What it does | Default trigger |
| --- | --- | --- |
| `SubDL/TMDB — Subtitle Upload` | collects the libraries and sends embedded text tracks | none, coordinator |
| `SubDL/TMDB — Subtitle Download` | searches and fetches missing subtitles as loose files | none, coordinator |
| `SubDL Postprocessing` | moves stored upload ids to the published entry ids, pages to the watermark | none, manual or chained |
| `Sub Scribe — Database Refresh` | checks stored state against the filesystem, weekly anchor | none, coordinator |
| `Sub Scribe — OSHash Cache Refresh` | recomputes expired or fingerprint-changed hashes | none, coordinator |

Three jobs are not cycles and do not appear as a trigger. The postprocessing walks the stored upload ids down to
the published entry ids, pages to the last pass's watermark and has no trigger of its own: it runs from the
dashboard task or from `PostprocessUploads`. The database refresh is queued by the coordinator on its weekly anchor,
the OSHash refresh runs the same way, and both go through `AnchorJobGate`, so a busy lock defers them and a missed
slot is caught up rather than stacked.

The seeder is the producer in front of both pipelines. One pass over the selected libraries turns every movie and
episode with a media file on disk into a queue candidate, and the dispatcher merges that snapshot into the persisted
queues, where retry counters and tombstones survive. On the event path the seeder is handed the libraries of the
changed items and scans only those, growing back to all of them above the targeted-set limit. Ordering is last in,
first out by item creation time.

## What a run never does

It does not run two directions at once, fire a job twice for one slot, fire at a fixed clock time,
or keep a timer in the dashboard. It does not rescan in a loop either: a refusal returns the slot to
the next regular anchor. A trigger cannot widen an arrival cycle or narrow a scheduled one. No cycle
repairs a pair booked under an older language form; only a migration of the stored pairs fixes that.

## What it costs

Measured on the host: an upload run takes about 30 minutes, the weekly database refresh 4 minutes,
the hash refresh and the download run under a minute each. A full scheduled cycle spent 69 minutes
in its pack phase before the first of 1,477 items; an arrival cycle has in front of it what arrived,
eight episodes when eight episodes arrived. The 30-second tick and the deferral spacing cost nothing
measurable.

## Where the rules live

The requirements are F-M25, F-M47, F-M48, F-M65, F-M94h, F-M111, F-M116, F-M119, F-M152, F-M156,
F-M189, F-M233, F-M311, F-M331 and F-M445; the catalogue is [SPECIFICATION.md](SPECIFICATION.md).
Guarded by `endpoints` (T436 runs and triggers) and by the dashboard structure test.
