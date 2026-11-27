# The worker rows

Seven rows stand in the Workers block of the General tab, one per worker: the display name, the moment the run began, and one outcome word with its colour. The answer behind them also carries the run's one-line detail, which the row itself never prints. The rows are read from the plugin database, not from memory, so a Jellyfin restart neither resets them nor turns a run of two minutes ago into "never run" (F-M621).

## The seven workers

| Row | What it owns | Who writes it |
| --- | --- | --- |
| `Seeder` | the scan of the selected libraries and the queue it fills | the event dispatcher, before each direction runs |
| `Download` | the download run's own work | the download run, from the moment its queue is prepared |
| `Auto-Sync` | the alignment of the fetched files onto their audio | the download run that feeds it |
| `Upload` | the upload run's own work | the upload run, from the moment its queue is prepared |
| `Upl. Postproc.` | moving stored upload ids to the published entry ids | the postprocessing task |
| `Database` | checking the stored state against the filesystem | the database refresh task |
| `OSHash` | recomputing expired or changed hashes | the hash refresh task |

The order and the names live in one list in the registry, so the section is complete before any worker has run, and a rename reaches rows written by an older build without a migration. A stored row whose key is no longer in that list is dropped, because it would be a worker this build does not run.

## One row, one result

The rule is one sentence from the operator: every worker reports only itself (F-M322, F-M328). The download row says what the download did, the upload row what the upload did, and the seeder's counts stay on the seeder's row. A single green fallback used to hand the seeder's sentence to every direction row, so two rows carried the identical text while neither said what its own direction had done. A direction row is now written by that direction's own run, start to finish, and the waiting task leaves it alone.

## Who marks a row and who clears it

A row turns `run` when its own work begins, not when the cycle does: the seeder just before its scan, a direction once its queue is prepared, the alignment when a download run with its switch on opens it. The end comes from the same owner. `Finish` writes the end stamp and the outcome and keeps the start stamp it finds, so the duration shown is the run's real one and not the moment of recording. The start also writes whether the run is dry, so the grey `dry` stands beside the word while the run is still going and not only after it ended (F-M401). A cancellation is the one exception: it stamps both ends with the moment, because a stop has a moment and not a run. It reads `cancel` in grey, the same grey as `skip`, because nothing went wrong in either case (F-M353). Every write is wrapped, so a failing record never breaks the run it describes.

## A dead process does not leave a light on

Only `Finish` and the cancellation close a row, and both need the living process. A restart in mid-run therefore left `run` behind with no end, and the stop button then answered that no cycle was active while the row claimed one. The start sweeps every open row, writes the moment it was found as the end, `failed` as the outcome and `process ended mid-run (closed at the next start)` as the detail. It is deliberately not a cancellation, because nobody pressed a button (F-M621, F-M293).

## A direction waits for its own turn

A direction must never light up while the seeder is still scanning, because the light means work in flight and the scan is the seeder's work. The measured incident: the cycle started at 16:16, the seeder scanned until 17:12, and the page read `ok` — the waiting task had reported success for a cycle whose scan was still running. The same shape appeared once as two blue directions beside a scanning seeder, which is why the waiting tasks now mark nothing at all (F-M289). A full scan of 748 items took 56 minutes on the measured host, and that hour belongs to the seeder's row and to no other.

## A pending fire is a state, not a verdict

A recovery fire is one shot per direction, armed whenever that direction has to come back later — a quota stop, an overload, a busy lock, a library scan in the way — and it survives a restart in its own file (F-M65, F-M116). While one is pending, that worker's row reads `defer` in yellow whichever outcome it stored, including `failed`, because the retry is what happens next; only `run` is stronger and stays untouched (F-M436). The moment and the cause are printed on the line under the list, as the worker's name, the server's own wording and `next attempt` with day and time. The armed postprocessing has its own line there too, so its row never sits yellow for an order that every upload run arms again (F-M445). An elapsed deadline is not a pending fire, so row and line both fall back to the record (F-M368).

## A deferral that has no row

A job that meets a busy run lock does not wait: it returns at once and reschedules in job-spacing steps (F-M152). The two maintenance rows write their own word for it, `deferred` with `lock busy — rescheduled`. The cycle itself owns no row, so its deferral carries its own fields — the due moment, the reason and the attempt against the limit — and the page prints them on the cycle's own line below the list (F-M623). The reason belongs to the moment and never to the last run, so a fresh deferral cannot wear the previous run's cause (F-M475).

## What the rows never claim

A row is the last run and not a history: one row per worker key, overwritten on every run, with no prune path. A worker that has never run reads `never` with an empty start, so no fresh date stands next to a run that did not happen. A direction that did nothing gets no row at all — a switched-off direction, an allowance already spent, or a cycle that ended before that direction began — and the log carries the line instead (F-M365, F-M366). A row never borrows another worker's sentence, and a pending retry never becomes a verdict about the run behind it.

## Where the rules live

The endpoint is `Plugins/SubdlSync/WorkerRuns` and the block on the page is the Workers list; the registry holds the seven keys, the outcome words and the single write path. The requirements are F-M65, F-M116, F-M152, F-M268, F-M289, F-M293, F-M294, F-M322, F-M328, F-M340, F-M353, F-M359, F-M365, F-M366, F-M368, F-M401, F-M436, F-M445, F-M475, F-M621 and F-M623. The due times behind the fires stand in [SCHEDULER.md](SCHEDULER.md), the six statuses of the alignment in [AUTOSYNC.md](AUTOSYNC.md), and the page around the list in [GUI.md](GUI.md).
