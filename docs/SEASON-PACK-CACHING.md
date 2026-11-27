# Season Pack Caching — what is kept, where and what it says

How a season archive travels from a source to the run: the one
cache on disk, the key spaces per source, the read, the fetch, the unpack — and the log lines that
make every step visible.

This paper is derived from the rules in the rules are stated normatively in `SPECIFICATION.md` (F-M370, F-M663, F-M665,
F-M719, F-M766, F-M767, F-M768). This paper describes how those rules work together: what is kept,
where it lies, who reads it and what the cache says while doing it. The words are those of
`TERMS.md`.*

## Contents

- [1. What this is about](#1-what-this-is-about)
- [2. One cache, many key spaces](#2-one-cache-many-key-spaces)
- [3. The way of an archive](#3-the-way-of-an-archive)
- [4. Nothing is remembered in the run](#4-nothing-is-remembered-in-the-run)
- [5. Retention and ceiling](#5-retention-and-ceiling)
- [6. What decides whether an archive is used](#6-what-decides-whether-an-archive-is-used)
- [7. What the cache says](#7-what-the-cache-says)
- [8. What is never stored](#8-what-is-never-stored)
- [9. The checks](#9-the-checks)

## 1. What this is about

A season archive is one ZIP that carries many subtitles of one season. A source hands it out once.
Inside one run the archive serves several episodes, and across runs it serves them again — as long as
it lies on disk. That is the whole point of the cache: **one fetch, many files**.

Two kinds of archives travel this way:

- the **tvsubtitles season ZIP** of one series, one season and one language,
- the **SubDL archive** of one subtitle page, which the API hands out as a ZIP.

## 2. One cache, many key spaces

There is exactly one cache, and it belongs to no source. It lives in the plugin's data folder under
`archive-cache`, and every engine writes into it.

Which source delivered a file stands in the **key**, not in a folder:

- `tvst.<series>.<season>.<language>` for the tvsubtitles season ZIP,
- the page address for a SubDL archive.

The same number in two sources therefore produces two files that never collide. A hit in one key
space is never served for the other.

## 3. The way of an archive

Every lookup walks the same three steps, in this order:

1. **The disk is asked first.** A stored archive is read and unpacked. No request leaves the host.
2. **Only a miss reaches the source.** One fetch, and a successful fetch is written to disk at once.
3. **The unpack is per call.** The archive bytes live while they are read; afterwards they are free
   again. Nothing is kept for the rest of the run.

So the second episode of a season costs a disk read, not a request. The same holds for the next run,
which starts on a warm disk.

## 4. Nothing is remembered in the run

There is no memo of the season archive inside the client. Neither the bytes nor the list of entries
survives a call.

An earlier version kept the unpacked subtitles of every season it had touched for the whole run. That
was several tens of megabytes on a full run over a hundred seasons, and it contradicted the rule that
the archive lies on disk. A first step kept only the names; then the memo was dropped entirely.

One consequence is named on purpose: a season whose archive is **missing or unreadable** is asked
again on the next call. A stored archive is still served from disk, so the cost only appears where
there is nothing to keep.

## 5. Retention and ceiling

Two switches in the Download tab rule the whole cache, and both belong to the cache, not to a source:

- **days** — retention counted from the last use. `0` switches the cache off.
- **megabytes** — the ceiling over everything stored. `0` switches the cache off. The default is
  `7` days and `256` MB.

A run starts by trimming: first everything that is older than the retention, then the oldest entries
until the ceiling is met. A resetting of the plugin database purges the cache with it, because a reset
means "forget everything".

## 6. What decides whether an archive is used

Three switches stand in front of the archive, and none of them sits in the endpoint:

- **Packs** on the source card decides whether the source takes part in packs at all — for the upload
  and for the download (F-M719).
- **Prefer season pack** on the source card narrows that to the download: an archive is only taken
  first when it stands open (F-M693).
- The **run type** decides whether packs are built at all. An arrival run never asks; a full, refetch
  or manual run does (F-M680, F-M764).

## 7. What the cache says

The cache reports every operation at the plugin's Debug mode. The run hands it its logger and its log
mode, so the lines appear exactly when the plugin protocolled at Debug.

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

## 8. What is never stored

A cache must not become a dump, so three things never reach the disk:

- **a failure.** Only a successful fetch is written. A broken or missing archive costs a new attempt
  on the next call instead of being remembered.
- **a plain body.** Only containers are kept. A single subtitle under a page address would be served
  to the wrong caller.
- **an oversized body.** Anything above the per-archive ceiling stays where it came from.

## 9. The checks

- `archivecache` — the cache itself: a hit inside a run, a hit across runs, the switches at `0`, the
  retention, the ceiling, the purge, and every log line (F-M767).
- `tvsubtitles` — the season ZIP as step one: one fetch for five episodes, a member read from disk
  with no request, and what happens when the archive is broken (F-M663, F-M665, F-M766).
- `endpoints` — the switches of the source cards and the fact that the upload endpoint carries no pack
  switch of its own (F-M719, F-M765).
