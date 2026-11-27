# Architecture — three zooms

The plugin can be read at three depths, and each depth answers one question. **What starts a run?** **Which direction
does it go?** **What unit does it work on?** The three diagrams are the same system, zoomed in once more each time.

## Zoom 1 — the trigger, and nothing else

```
Auslöser
   │   manuell · Zeitplan · Ankunft
   ▼
Dispatcher
   │
   ▼
Seeder
   │
   ▼
Warteschlange
```

A run exists because something asked for it: a person pressed a button, a slot fell due, or a file appeared. The
dispatcher turns that into a cycle, the seeder turns libraries into candidates, and the queue holds them. Nothing in
this zoom decides anything about a subtitle. The whole of it is [RUNS.md](RUNS.md) and [QUEUE.md](QUEUE.md).

The three triggers differ in one way only: a manual or scheduled run looks at the whole selection, an arrival run
looks at the ids that arrived. That is why the trigger does not appear again in the next two zooms.

## Zoom 2 — the direction

```
Warteschlange
   │        │
   ▼        ▼
Download  Upload
```

The queue has one list per direction, and a pipeline drains its own list. Download fetches and places a subtitle,
upload reads and sends one. They share the store, the gates and the lock, and they never run at the same time
because both write into the same store.

## Zoom 3 — the unit

```
Download            Upload
   │                   │
 ┌─┴──┐            ┌───┴──┐
 ▼    ▼            ▼      ▼
Einzel Pack      Einzel  Pack
```

Inside a direction there are exactly two units. **Einzel** works on one file: one language to fetch, or the text
tracks of one file to send. **Pack** works on a whole season: one archive to unpack, or one container per language
carrying all episodes. That is the last branch — deeper there is only the leaf itself.

| Direction | Unit | What it works on | Paper |
| --- | --- | --- | --- |
| Download | Einzel | one language of one media file | [SINGLE-DOWNLOAD.md](SINGLE-DOWNLOAD.md) |
| Download | Pack | one season archive of a site | [SEASON-PACK-DOWNLOAD.md](SEASON-PACK-DOWNLOAD.md) |
| Upload | Einzel | the text tracks of one media file | [SINGLE-UPLOAD.md](SINGLE-UPLOAD.md) |
| Upload | Pack | one language across one season | [SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md) |

## What is the same in all three zooms

The **lock**: one mutex over every part that writes, so no two leaves of the tree ever run together. The **store**:
verdicts, marks and refusals, the only thing that survives a restart. The **cache**: texts, archives and TMDb
answers, deletable at any time. The **sources**: a registry that turns a name into an engine, asked with four
methods, never by name inside a pipeline. The **worker rows**: each worker reports only its own result, and the page
only reads. None of these belongs to a zoom because all of them are used by every zoom.

## From a symptom to the right place

| If you see … | It belongs to | Paper |
| --- | --- | --- |
| a run that examines the whole library after one arrival | zoom 1, the run's scope | [RUNS.md](RUNS.md) |
| a pair sent twice | the store's pair lock | [SINGLE-UPLOAD.md](SINGLE-UPLOAD.md) |
| a language that reaches no site | the gates or the site's language list | [GATES.md](GATES.md) |
| a season that never becomes a pack | zoom 3, the pre-checks before any extraction | [SEASON-PACK-UPLOAD.md](SEASON-PACK-UPLOAD.md) |
| a subtitle sitting wrong on the audio | the Auto-Sync worker after the fetch | [AUTOSYNC.md](AUTOSYNC.md) |
| a worker row that stays yellow | the row and its deferral | [GUI.md](GUI.md) |
| a name that cannot be parsed back | `SidecarNaming` and its writers | [NAMING.md](NAMING.md) |

[SPECIFICATION.md](SPECIFICATION.md), §2, §3, §4, §14, §21; F-M233, F-M624, F-M668, F-M669, F-M682, F-M683, F-M801.
Guarded by `coverage`, `storex`, `endpoints` and `gui-structure`.
