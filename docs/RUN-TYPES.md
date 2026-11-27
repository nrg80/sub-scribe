# The run types

Everything the plugin does happens in a cycle, and every cycle is started by one of five triggers. The trigger
decides two things and nothing else: which direction runs, and which items are read. What happens to an item is
decided further down by the gates, never by the trigger.

| Trigger | Who starts it | Direction | What it reads |
| --- | --- | --- | --- |
| `scheduled-upload` | the coordinator's uploaded anchor, or the dashboard button | upload only | the whole selection |
| `scheduled-download` | the coordinator's download anchor, or the dashboard button | download only | the whole selection |
| `event` | the arrival watcher, after its debounce | both | the ids the collector gathered |
| `arrival-followup` | 90 seconds after the cycle that drained the collector | both | the ids that arrived during that cycle |
| recovery | a one-shot fire per direction, at a reset time plus a dice | the direction it was scheduled for | the whole selection |

A scheduled run and a button press are the same run. Jellyfin starts the task, the task starts the cycle, and the
log line says `scheduled-upload` in both cases — measured on the host by pressing the button and reading the next
line. That is deliberate: `F-M233` reserves full coverage for the scheduled and the manual path, and the operator
who presses the button wants the net, not a fragment.

A recovery run sees everything, and that is on purpose. A recovery fire is not attached to an item: it is a
one-shot retry for one direction, scheduled when a quota was spent or a job had to be moved, and it carries a
reason (`quota`, `respaced`, `scanNN`). It cannot know which items were open when it was scheduled, so it seeds
the selection like any scheduled run. When nothing is open, the seed finds nothing and the cycle is a cheap
no-op; when the quota has come back, the open work is waiting in the store and gets its run.

The direction follows the trigger through `DirectionOf`, and the reason is the only input. The two scheduled
triggers carry one direction each. The arrival triggers run both, because an arrived episode usually needs a
subtitle and may carry text worth uploading. Everything else runs both. Two more names, `arrival-upload` and
`arrival-download`, are mapped to a direction as well, but nothing starts them today — they are dead branches in
that switch, kept as mapping and worth naming rather than rediscovering.

Three jobs are not cycles and are not in the table. The postprocessing walks the stored upload ids down to the
published entry ids, pages to the last pass's watermark and has no trigger of its own: it runs from the dashboard
task or from `PostprocessUploads`. The database refresh is queued by the coordinator on its weekly anchor. The
OSHash refresh runs the same way on its own weekly anchor. Both weekly jobs go through `AnchorJobGate`, so a busy
lock defers them and a missed slot is caught up rather than stacked.

What a trigger cannot do: it cannot widen an arrival cycle, it cannot narrow a scheduled one, and it cannot
change what the gates decide. The scope says which items are read. The switches, the QA gates and the pair lock
say what may happen to them.

[SPECIFICATION.md](SPECIFICATION.md), F-M47, F-M48, F-M65, F-M94h, F-M116, F-M119, F-M152, F-M156, F-M233, F-M331.
Guarded by `endpoints` (T436 runs and triggers) and the dashboard structure test.
