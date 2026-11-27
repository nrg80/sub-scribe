# Upload Extraction Cache — one pass per track, not per run

A track has to leave the media container before it can be
sent anywhere. Today that extraction is repeated for every run, and what came out of it is thrown away
when the run ends. This paper describes keeping it, under the rules the download cache already follows.

**Status: built in 12.3.8.76.** The operator approved the shape on 2026-11-27: one store
shared with the download cache, retention and ceiling included, one ZIP per track, a hit counts like a
fresh extraction, and containers assembled from the stored single subtitles. Every rule below names the
requirement it would become (§11); that number is deliberately not named here — see §11
first, so the proposals start one number higher. Nothing here is released, and the papers of the built system stay
untouched until the first line of it exists.

*The words are those of `TERMS.md`. A **pack** is the season container of one language. A container that
carries several tracks of ONE media file is not a pack; this paper calls it the **upload container** of
that file.*

## Contents

- [1. What this is about](#1-what-this-is-about)
- [2. The same store, the second direction](#2-the-same-store-the-second-direction)
- [3. Naming a track without hashing the file](#3-naming-a-track-without-hashing-the-file)
- [4. Why the track number is a safe name](#4-why-the-track-number-is-a-safe-name)
- [5. One track, one ZIP, unpacked on access](#5-one-track-one-zip-unpacked-on-access)
- [6. Byte-identical, or it does not count](#6-byte-identical-or-it-does-not-count)
- [7. Containers are assembled from the stored subtitles](#7-containers-are-assembled-from-the-stored-subtitles)
- [8. Counting and refusals](#8-counting-and-refusals)
- [9. Retention, ceiling and what it saves](#9-retention-ceiling-and-what-it-saves)
- [10. Where the limits are](#10-where-the-limits-are)
- [11. The requirements it needs](#11-the-requirements-it-needs)

## 1. What this is about

An upload run wants a subtitle as bytes on its way out. The subtitle sits as a track inside a media
container, so the run takes it out with ffmpeg, writes the text into a folder that belongs to the run,
builds its container for the source, and drops the texts when the run finishes.

The next run over the same media file does all of it again. That is a full read of the container and a
full ffmpeg pass, for bytes that were already produced once and have not changed since.

What is expensive is not the unpacking and not the compression. It is reading the media container from
disk. On a host whose disk stalls longer than its memory ever does, that is the cost worth removing.

## 2. The same store, the second direction

There is one store, and it keeps to the rules of the download cache without exception:

- one folder in the plugin's data area, `archive-cache`,
- one retention, counted in days from the last use,
- one ceiling in megabytes over everything stored,
- one ceiling per entry,
- one log prefix.

Uploads get their own **key space** inside it, so a stored subtitle and a stored archive never answer
for each other. Sharing the ceiling is intended: the entries that keep being used stay, and the ones
nobody touches make room. A season being uploaded can therefore push out a download pack that has not
served anything for a while. That is the sorting the retention already asks for, and it is not a fault.

## 3. Naming a track without hashing the file

The name of an entry is built from values that one look at the file already gives:

- the identifier of the item in the library,
- the number of the track,
- the size of the media container,
- when it was last written,
- when it was last changed.

**No hash is ever computed for the store.** A content hash costs a full read of the container, which is
exactly the work the store exists to avoid; paying it before the lookup would make every hit as
expensive as a miss. Where an open-subtitles hash for that item already lies in the plugin database, it
may join the name as an extra witness. It is never a reason to read the file.

## 4. Why the track number is a safe name

Track numbers come from the circle of one file (F-M671). Every track of a media container gets the next
free number when it is first seen, whether it is embedded or lies loose beside the file, and a number is
never handed out twice. A vanished track leaves a gap; the next one takes the number above it.

The ffmpeg index is not that number and is never stored. It is counted at run time as the number of
embedded tracks carrying a smaller number, so a loose subtitle can never shift an embedded track's
index, and a loose subtitle can never collide with an embedded track in the store either. The number is
stable for as long as the container is, which is exactly as long as its entry is useful.

## 5. One track, one ZIP, unpacked on access

An entry holds **one track of one media file**: a ZIP with a single subtitle inside it under a fixed
name. Writing happens once, right after a successful extraction.

Reading means opening the ZIP and taking the one member into memory. That is a few milliseconds of work
and no disk read beyond the entry itself. The unpacked text is never written to disk and nothing about it
survives the call: the store is on disk, the run keeps no memo (F-M768, F-M766).

The compressed form is also the smaller one. A subtitle of the sort this library carries measures
between 6 KB and 200 KB as text with a median around 39 KB, and text compresses well. Thousands of
entries fit into the shared ceiling, while the media files themselves never come near it.

## 6. Byte-identical, or it does not count

What a source receives on a hit has to be what a first run would have sent. The ZIP is therefore built
deterministically: the same inner name, the same timestamp, the same method. A hit and a fresh
extraction differ in nothing but the CPU they spent.

Integrity needs no bookkeeping of its own. A ZIP carries a checksum per member, so a read that comes
back changed is detected by the unpack itself and treated as a miss. A damaged entry is removed, not
repaired.

## 7. Containers are assembled from the stored subtitles

The store holds single subtitles; every bigger thing is built from them at the moment it is needed.

- **The upload container of one media file.** A run that sends several tracks of the same file builds
  its container from the stored entries of that file. Missing tracks are extracted as before.
- **A pack.** A season pack is assembled across the entries of its episodes, one track after another.
  Episodes without an entry are extracted first; nothing is invented and no empty member is written.

A container therefore never carries a wrong subtitle, and a gap never blocks it: the gap is simply
filled the old way.

## 8. Counting and refusals

A hit is not a shortcut in the report. It counts exactly like a fresh extraction: one track, the same
counters, the same statistics per source (F-M763).

The order of the questions stays as it is. The verdict of a pair may refuse a track before anything is
extracted (F-M443); the store is only asked for tracks that survived that decision. A refused track is
not extracted, so it is never stored either.

Nothing unsuccessful is ever written. A failed ffmpeg pass leaves no entry, so a transient fault cannot
poison the store and a later run is free to try again.

## 9. Retention, ceiling and what it saves

Retention counts from the last **use**, as it does for archives: a hit moves the entry to now, so a track
that keeps being sent does not age out while dead entries sit still. When the ceiling is exceeded, the
oldest write goes first.

What a hit saves is one full read of the media container and one ffmpeg pass. On a 2 GB container over a
slow disk that is the difference between a stall and a file read of a few kilobytes. It costs no
network, because the subtitle never left the host.

## 10. Where the limits are

- **A replaced file with the same size and the same modification time** is the one stale case. The
  change time closes it, because it cannot be set back from outside.
- **A remuxed container** changes the size, and often the order of its streams. The name no longer
  matches, so the entry is a miss. That is the correct answer, not a defect.
- **A deleted media file** leaves entries that only the retention removes.
- **A reset of the plugin database** purges the store with it, because a reset means "forget
  everything".

## 11. The requirements it needs

These are proposals and carry section marks, not requirement numbers. A number is handed out when the
code that carries the rule exists; until then it belongs to whoever builds first. This paper has already
been overtaken twice — F-M771 to F-M776 went to the lock pause and to the TMDb shelf while the paper sat
here — so it stops naming numbers: §11.1 to §11.6 are the placeholders, and the numbers come with the build.

- **§11.1 — the extracted track is kept.** A successfully extracted track is stored under the shared
  cache folder, in its own key space, under the retention and ceiling of the download cache.
- **§11.2 — the name is built without a hash.** The entry is named from the item identifier, the track
  number, the size, the last write and the change time of the media container. No hash is computed for
  the store; a stored hash may only be used when it already exists.
- **§11.3 — one track, one ZIP, unpacked on access.** An entry holds exactly one subtitle under a fixed
  name. It is unpacked into memory when read, and nothing is kept afterwards.
- **§11.4 — a hit is byte-identical.** The entry is written deterministically, so a hit and a fresh
  extraction differ only in the work spent. A member whose checksum fails is removed and counts as a
  miss.
- **§11.5 — containers are assembled, not re-extracted.** The upload container of a media file, and a
  pack over its episodes, are assembled from the stored subtitles; tracks without an entry are
  extracted as usual.
- **§11.6 — a hit counts as an extraction.** The counters and the statistics count a hit exactly like
  a fresh pass, and the verdict of a pair is asked before the store.

Once the first of these is built, this paper moves its status line and the rules take the place they
belong to: the normative text in `SPECIFICATION.md`, the checks in `TESTS.md`, and the log lines in the
table of `SEASON-PACK-CACHING.md`.
