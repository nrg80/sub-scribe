# The queue

The queue is two lists, one per direction, and one file that holds both: `cycle-queues.json` in the plugin's data
folder. The dispatcher writes it through a temporary file and reads it back on start, so a restart continues with what
was open. No run may work an item that does not stand in it (F-M111, F-M244).

## The item and its three states

An entry carries the item id, the path of the media file, a name for the log, the retry counter, the state, the
sources that queued it and two stamps. Nothing else: no language, no verdict, no pack, no season.

| State | What it means | What happens to it |
| --- | --- | --- |
| `Queued` | waiting for a run | stays until a run decides |
| `Done` | finished, or the run had nothing to report | removed when that run ends |
| `Purged` | three real failures in a row | removed too, the id kept in a second file |

## The order

The queue is not worked in the order it was written. The seeder sorts its candidates newest first, items without an
IMDb or TMDb id last, and that stamp does not reach the snapshot, whose copy carries only id, path and name. A run
sorts the selection itself, newest first (F-M59).

## What a failure leaves behind

Only a real failure counts; an item a source does not have keeps its number, because the refetch interval owns that
case. At three it turns `Purged`, leaves the file when the run ends, and its id goes to `purged-items.json`, skipped
by every merge. A stopped run or a dry run leaves the rest open, and no clock prunes the list (F-M22, F-M131).

## What an item says about sources

Only the upload entry names sources; the download question is answered from the tracks of the file. The names are
stored as text and stand at the item, because the queue is merged across scans and a map beside it knows only the last
one (F-M549). A merge takes the fresh answer and leaves the names when it is empty (F-M552, F-M546).

## Who fills it

The seeder fills the queue once per leg of a cycle. It fills only the direction the cycle covers: download while that
switch is on and a source may still fetch, upload only for sources with open work, and on an arrival cycle only the
ids that arrived (F-M233, F-M331). The queue grows from files with at least one open stamp (F-M557, F-M563).

## Who drains it

The dispatcher hands the `Queued` ids, narrowed to the arrivals on an arrival cycle, to the pipeline as its filter and
notes each counter at the start. The pipeline skips items outside it and reports one outcome per item. At the end the
dispatcher marks every filtered item whose counter did not move as `Done` and removes the rest.

## When a direction is switched off

A direction that is off fills nothing and starts no run, and its entries stay for the cycle after it comes back; a
spent allowance does the same, items left due. Nothing leaves the queue because a switch was turned off (F-M331).

## What the queue never holds

No folder, no series, no season, no virtual item — the scan asks only for movies and episodes. No item whose file was
missing, none outside the selected libraries, no id purged before. No finished work, no pack, no language, no verdict.

[SPECIFICATION.md](SPECIFICATION.md), F-M22, F-M59, F-M111, F-M131, F-M233, F-M244, F-M331, F-M546, F-M549, F-M552,
F-M557, F-M563.
