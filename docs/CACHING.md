# Caching

Three things are kept between runs so that nothing is paid for twice: the answers TMDb gave, the subtitle texts
that were cut out of a media file, and the archives the sources handed out. Two stores carry them, and neither store
carries a decision — anything in them may be deleted, and the plugin then simply asks again. The normative rules
stand in §7 and §14 of `SPECIFICATION.md`; the words are those of `TERMS.md`.

## What a store is, and what it is not

A store has a key, a retention counted from the last use and a ceiling; a retention of `0` or a ceiling of `0`
switches it off. Every lookup is answered by the store first and by the world second, and every write happens only
after the world answered successfully. Nothing unsuccessful is ever written: a failed fetch, a failed extraction or
an empty body leaves no entry, so a transient fault cannot poison a store and the next run is free to try again.

## The TMDb shelf

Jellyfin, TMDb and the sources count differently, and every conversation between them needs an identifier, so the
plugin asks TMDb constantly. Measured over one day: 1.221 requests against 305 different addresses, of which 916
were repeats, with one series asked twenty-one times and one film nineteen times, sometimes twice within a second.
Before the shelf existed, a single day of restarts was enough to make 283 of 555 requests repeats.

### Facts first, raw answers as a net

The shelf keeps two kinds of row. A **fact** is the answer to a question in the plugin's own terms — which TMDb
identifier a title has — and it is keyed by the question and by whether the question meant a series. A **raw
answer** is the whole response as TMDb sent it, keyed by the address with the credential stripped. The fact is what
a later run reads; the raw answer is the net for addresses no call site knows about today, because no future call
site can forget a shelf that sits in the HTTP handler. Facts are also mirrored in a capped memory map, so a run over
a season does not read the disk for every episode; raw answers are never held in memory.

### Its own file

The shelf lives in its own file beside the plugin database, and the reason is that a ceiling has to be a fact.
Deleting rows from SQLite does not shrink the file they live in, so the ceiling would be a calculation while the
file stays large; in its own file the ceiling is the file, and a trim can be followed by a real shrink. Clearing is
then a delete, because a switch at `0` means keep nothing, which is a file to remove. Both kinds carry a ceiling of
16 MB, measured on the host at 166 facts and 235 raw answers inside a 5 MB file.

### Two lifetimes, and what a stored answer may not do

An answer that named an identifier lives 180 days, one that found nothing lives 7 days, because a title that does
not exist yet may exist next month. The shelf also never closes a title. The ladder compares how often the shelf
answered before and after its attempts. A run in which only the shelf spoke counts the attempt but does not close the
title, because an old answer is not a verdict about today.

### Where it stops

One rule is deliberately not built. Facts are still asked with a language in the key and requests still append
blocks, so the shelf is smaller today than this paper wants it; the rule waits for the call sites. One address also
slips past the handler, because the status page builds its own HTTP client and asks for the TMDb configuration every
time.

## The archive store

One folder in the plugin's data area, `archive-cache`, keeps both directions: the packs the sources handed out and
the track texts that were cut out for an upload. The key carries the direction and the source — `tvst.<series>.
<season>.<language>` for a season ZIP, the page address for a SubDL archive, a track key for an extracted text —
so two entries never answer for each other. One ceiling covers both, which is intended: entries that keep being used
stay, the ones nobody touches make room. Measured on the host: 3.336 entries holding 79,8 MB against a ceiling of
512 MB.

### The way of an archive

Every lookup walks the same three steps. The disk is asked first, and a stored archive is read and unpacked with no
request leaving the host. Only a miss reaches the source, with one fetch, and a successful fetch is written at once.
The unpack is per call: the archive bytes live while they are read and are free afterwards, and nothing about them
survives the call. The second episode of a season therefore costs a disk read, not a request, and the next run starts
on a warm disk.

### One track, one ZIP, unpacked on access

An extracted text is stored as a ZIP with a single member under a fixed name, written right after the extraction
succeeded. Reading opens the ZIP and takes the one member into memory, which is a few milliseconds and no disk read
beyond the entry itself. The compressed form is also the smaller one: a subtitle of this library measures between
6 KB and 200 KB as text with a median around 39 KB, and text compresses well. Thousands of entries therefore fit
into the shared ceiling while the media files themselves never come near it.

### Byte-identical, or it does not count

What a source receives on a hit has to be what a first run would have sent, so the ZIP is built deterministically —
same inner name, same timestamp, same method — and a hit and a fresh extraction differ in nothing but the CPU they
spent. Integrity needs no bookkeeping of its own: a ZIP carries a checksum per member, so a read that comes back
changed is caught by the unpack itself and treated as a miss. A damaged entry is removed, never repaired.

### Containers are built from the stored texts

The store holds single texts, and every bigger thing is assembled from them when it is needed. A run that sends
several tracks of one media file builds its upload container from the stored entries of that file; a pack is
assembled across the entries of its episodes, one track after another. Episodes without an entry are extracted
first, nothing is invented and no empty member is written, so a container never carries a wrong subtitle and a gap
never blocks it.

### The name of an entry, without hashing the file

The name comes from values one look at the file already gives: the identifier of the item in the library, the
number of the track, the size of the container, when it was last written and when it was last changed. No hash is
computed for the store, because a content hash costs a full read of the container — exactly the work the store
exists to avoid — and paying it before the lookup would make every hit as expensive as a miss. The track number is
safe to use because it is drawn from the circle of one file: every track gets the next free number when it is first
seen, and a number is never handed out twice. The ffmpeg index is a different number, counted at run time and never
stored.

### What is never stored

Three things never reach the disk. A failure: only a successful fetch or extraction is written. A plain body: only
containers and extracted texts are kept, never a single subtitle under a page address, which would be served to the
wrong caller. An oversized body: anything above the per-entry ceiling stays where it came from.

### Retention, ceiling, and where the limits are

Retention counts days from the last use and a hit moves the entry to now, so a track that keeps being sent does not
age out while dead entries sit still. The defaults are 7 days and 256 MB; both switches belong to the cache and not
to a source. A run starts by trimming, first everything older than the retention and then the oldest entries until
the ceiling is met, and a reset of the plugin database purges the cache with it, because a reset means forget
everything. One stale case stays: a replaced file with the same size and the same modification time, which the
change time closes because it cannot be set back from outside. A remuxed container changes its size and often the
order of its streams, so the name no longer matches and the entry is a miss — the correct answer, not a defect.

## What the cache says

The archive store reports every operation when the plugin logs at Debug:

| Operation | The line names |
| --- | --- |
| switching on | folder, retention, ceiling |
| a hit | key, bytes, age, state of the store |
| a miss | key and the reason — no file, expired and removed, unreadable |
| a store | key, bytes, state of the store |
| a refused store | key and the reason — cache off, no key, empty body, above the ceiling, not a ZIP |
| the trim | how many expired, how many evicted, state against the ceiling |

Every line of it starts with `[Sub Scribe-Cache]`, so one search finds all of them. The shelf logs in the same
fashion, one line per hit, miss and store, with the kind of the fact and the lifetime in days.

## The checks

The suite `archivecache` covers the store itself — a hit inside a run, a hit across runs, the switches at `0`, the
retention, the ceiling, the purge and every log line. The suite `tvsubtitles` covers the season ZIP as the first
step: one fetch for five episodes, a member read from disk with no request, and what happens when an archive is
broken. The suite `endpoints` covers the switches on the source cards and the fact that no upload endpoint carries a
pack switch of its own.

[SPECIFICATION.md](SPECIFICATION.md), F-M370, F-M542, F-M555, F-M663, F-M665, F-M719, F-M763, F-M766, F-M767,
F-M768, F-M772, F-M773, F-M775, F-M776, F-M777, F-M778. F-M774 is deliberately not built.
