# Season pack caching

A season archive is one ZIP that carries many subtitles of one season, and a source hands it out once.
Inside one run the archive serves several episodes, and across runs it serves them again as long as it
lies on disk, which is the whole point of the cache: one fetch, many files. Two kinds of archive travel
this way — the tvsubtitles season ZIP of one series, one season and one language, and the SubDL archive
of one subtitle page, which the API hands out as a ZIP. The rules are stated normatively in
`SPECIFICATION.md` (F-M370, F-M663, F-M665, F-M719, F-M766, F-M767, F-M768), and the words are those of
`TERMS.md`.

## One cache, many key spaces

There is exactly one cache, and it belongs to no source: it lives in the plugin's data folder under
`archive-cache`, and every engine writes into it. Which source delivered a file stands in the **key**,
not in a folder: `tvst.<series>.<season>.<language>` for the tvsubtitles season ZIP, the page address for
a SubDL archive. The same number in two sources therefore produces two files that never collide, and a
hit in one key space is never served for the other.

## The way of an archive

Every lookup walks three steps in this order. The disk is asked first: a stored archive is read and
unpacked, and no request leaves the host. Only a miss reaches the source, with one fetch, and a successful
fetch is written to disk at once. The unpack is per call: the archive bytes live while they are read and
are free afterwards, and nothing is kept for the rest of the run. The second episode of a season therefore
costs a disk read, not a request, and the next run starts on a warm disk.

## Nothing is remembered in the run

There is no memo of the season archive inside the client: neither the bytes nor the list of entries
survives a call. An earlier version kept the unpacked subtitles of every season it had touched for the
whole run, which was several tens of megabytes on a full run over a hundred seasons and contradicted the
rule that the archive lies on disk. A first step kept only the names; then the memo was dropped
entirely. One consequence is named on purpose: a season whose archive is **missing or unreadable** is
asked again on the next call, while a stored archive is still served from disk, so the cost only appears
where there is nothing to keep.

## Retention and ceiling

Two switches in the Download tab rule the whole cache, and both belong to the cache, not to a source.
**days** counts retention from the last use, and `0` switches the cache off. **megabytes** is the
ceiling over everything stored, and `0` switches the cache off there too. The default is `7` days and
`256` MB. A run starts by
trimming — first everything older than the retention, then the oldest entries until the ceiling is met —
and a reset of the plugin database purges the cache with it, because a reset means "forget everything".

## What decides whether an archive is used

Three switches stand in front of the archive, and none of them sits in the endpoint. **Packs** on the
source card decides whether the source takes part in packs at all, for the upload and for the download
(F-M719). **Prefer season pack** narrows that to the download: an archive is only taken first when it
stands open (F-M693). The **run type** decides whether packs are built at all — an arrival run never asks,
a full, refetch or manual run does (F-M680, F-M764).

## What the cache says

The cache reports every operation at the plugin's Debug mode, and the run hands it its logger and its log
mode, so the lines appear exactly when the plugin protocolls at Debug:

| Operation | The line names |
| --- | --- |
| switching on | folder, retention, size |
| a hit | key, bytes, age, state of the store |
| a miss | key and the reason — no file, expired and removed, unreadable |
| a store | key, bytes, state of the store |
| a refused store | key and the reason — cache off, no key, empty body, above the ceiling, not a ZIP |
| the trim | how many expired, how many evicted, state against the ceiling |
| the purge | how many archives, from which folder |

Every line starts with `[Sub Scribe-Cache]`, so one search finds all of them.

## What is never stored

A cache must not become a dump, so three things never reach the disk. A **failure**: only a successful
fetch is written, and a broken or missing archive costs a new attempt on the next call instead of being
remembered. A **plain body**: only containers are kept, because a single subtitle under a page address
would be served to the wrong caller. An **oversized body**: anything above the per-archive ceiling stays
where it came from.

## The checks

The suite `archivecache` covers the cache itself — a hit inside a run, a hit across runs, the switches at
`0`, the retention, the ceiling, the purge and every log line (F-M767). The suite `tvsubtitles` covers the
season ZIP as step one: one fetch for five episodes, a member read from disk with no request, and what
happens when the archive is broken (F-M663, F-M665, F-M766). The suite `endpoints` covers the switches of
the source cards and the fact that the upload endpoint carries no pack switch of its own (F-M719,
F-M765).
