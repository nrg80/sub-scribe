# Upload extraction cache — one pass per track, not per run

An upload run wants a subtitle as bytes on its way out, and the subtitle sits as a track inside a media container. So the run takes it out with ffmpeg, writes the text into a folder that belongs to the run, builds its container for the source and drops the texts when the run finishes. The next run over the same media file does all of it again, as a full read of the container and a full ffmpeg pass, for bytes that were already produced once and have not changed since. What is expensive is not the unpacking and not the compression: it is reading the media container from disk, and on a host whose disk stalls longer than its memory ever does that is the cost worth removing.

## What is built

**Built in 12.3.8.76.** The operator approved the shape on 2026-11-27: one store shared with the download cache, retention and ceiling included, one ZIP per track, a hit counting like a fresh extraction, and containers assembled from the stored single subtitles. Nothing here is released, and the papers of the built system stay untouched until the first line of it exists.

## The same store, the second direction

There is one store, and it keeps to the rules of the download cache without exception. It has one folder in the plugin's data area, `archive-cache`, one retention counted in days from the last use, one ceiling in megabytes over everything stored, one ceiling per entry and one log prefix. Uploads get their own key space inside it, so a stored subtitle and a stored archive never answer for each other. Sharing the ceiling is intended, because the entries that keep being used stay and the ones nobody touches make room. A season being uploaded can therefore push out a download pack that has not served anything for a while, which is the sorting the retention already asks for and not a fault.

## Naming a track without hashing the file

The name of an entry comes from values that one look at the file already gives. They are the identifier of the item in the library, the number of the track, the size of the media container, when it was last written and when it was last changed. No hash is ever computed for the store, because a content hash costs a full read of the container, which is exactly the work the store exists to avoid, and paying it before the lookup would make every hit as expensive as a miss. Where an open-subtitles hash for that item already lies in the plugin database, it may join the name as an extra witness, but it is never a reason to read the file.

## Why the track number is a safe name

Track numbers come from the circle of one file (F-M671): every track of a media container gets the next free number when it is first seen, whether it is embedded or lies loose beside the file, and a number is never handed out twice. A vanished track leaves a gap, and the next one takes the number above it. The ffmpeg index is not that number and is never stored. It is counted at run time as the number of embedded tracks carrying a smaller number, so a loose subtitle can never shift an embedded track's index or collide with one in the store. The number is stable for as long as the container is, which is as long as its entry is useful.

## One track, one ZIP, unpacked on access

An entry holds one track of one media file: a ZIP with a single subtitle inside it under a fixed name, written once right after a successful extraction. Reading opens the ZIP and takes the one member into memory, which is a few milliseconds of work and no disk read beyond the entry itself. The unpacked text is never written to disk and nothing about it survives the call, so the store is on disk and the run keeps no memo (F-M768, F-M766). The compressed form is also the smaller one. A subtitle of the sort this library carries measures between 6 KB and 200 KB as text, with a median around 39 KB, and text compresses well. Thousands of entries therefore fit into the shared ceiling, while the media files themselves never come near it.

## Byte-identical, or it does not count

What a source receives on a hit has to be what a first run would have sent, so the ZIP is built deterministically with the same inner name, the same timestamp and the same method. A hit and a fresh extraction differ in nothing but the CPU they spent. Integrity needs no bookkeeping of its own: a ZIP carries a checksum per member, so a read that comes back changed is detected by the unpack itself and treated as a miss. A damaged entry is removed, not repaired.

## Containers are assembled from the stored subtitles

The store holds single subtitles, and every bigger thing is built from them at the moment it is needed. The words are those of `TERMS.md`: a **pack** is the season container of one language, and a container that carries several tracks of one media file is not a pack but the **upload container** of that file. A run that sends several tracks of the same media file builds its upload container from the stored entries of that file, and missing tracks are extracted as before. A pack is assembled across the entries of its episodes, one track after another, with episodes that carry no entry extracted first, nothing invented and no empty member written. A container therefore never carries a wrong subtitle, and a gap never blocks it because the gap is filled the old way.

## Counting and refusals

A hit is not a shortcut in the report: it counts exactly like a fresh extraction, as one track with the same counters and the same statistics per source (F-M763). The order of the questions stays as it is, and the verdict of a pair may refuse a track before anything is extracted (F-M443), so the store is only asked for tracks that survived that decision. A refused track is not extracted and therefore never stored. Nothing unsuccessful is ever written, so a failed ffmpeg pass leaves no entry, a transient fault cannot poison the store and a later run is free to try again.

## Retention, ceiling and what it saves

Retention counts from the last use, as it does for archives, and a hit moves the entry to now, so a track that keeps being sent does not age out while dead entries sit still. When the ceiling is exceeded, the oldest write goes first. What a hit saves is one full read of the media container and one ffmpeg pass. On a 2 GB container over a slow disk that is the difference between a stall and a read of a few kilobytes. It costs no network, because the subtitle never left the host.

## Where the limits are

One stale case stays: a replaced file with the same size and the same modification time, which the change time closes because it cannot be set back from outside. A remuxed container changes the size and often the order of its streams, so the name no longer matches and the entry is a miss, which is the correct answer and not a defect. A deleted media file leaves entries that only the retention removes, and a reset of the plugin database purges the store with it, because a reset means "forget everything".

## The requirements it needs

These are proposals and carry section marks, because a number is handed out only when the code that carries the rule exists; until then it belongs to whoever builds first. §11.1 to §11.6 are the placeholders, and the numbers come with the build. §11.1 keeps the extracted track in the shared store. §11.2 builds the name without a hash. §11.3 holds one track per ZIP and unpacks it on access. §11.4 writes deterministically, so a hit is byte-identical. §11.5 assembles containers instead of re-extracting them. §11.6 counts a hit as an extraction, and asks the verdict of a pair before the store. This paper was overtaken twice, because F-M771 to F-M776 went to the lock pause and to the TMDb shelf while it sat here. Once the first of these is built, the rules take their place in `SPECIFICATION.md`, in `TESTS.md` and in the log table of `SEASON-PACK-CACHING.md`.
