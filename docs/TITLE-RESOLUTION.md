# Title resolution — how a media file becomes a title identity

A media file arrives with a name, a Jellyfin item and sometimes stored provider identifiers, and none of
the three is an identity: the same film can sit under a translated name or a record copied from the wrong
title. Identity is the pair TMDb answers with — IMDb identifier, TMDb identifier, and film or series.

## The ladder

No single question asks what an identity is. TMDb ignores a year sent to the type-neutral search, a typed
search inherits whatever type Jellyfin guessed, and a name with no year cannot be filtered at all. The
ladder therefore descends from the identifier already held to the release name that can only be read. A
rung runs only when the one above it returned nothing (DownloadPipeline.cs:1453, 1458, 1497), and any rung
may be answered without a request, because every question passes the shelf first
(TmdbImdbResolver.cs:209-231). The upload path uses the same middle rungs and parses the name before it
asks (UploadPipeline.cs:1464-1485).

| Rung | The question | When it does not run |
| --- | --- | --- |
| the id test | does TMDb confirm this title at this year | the item carries no identifier, or no year is known |
| id to id | which IMDb identifier belongs to this TMDb identifier | no TMDb identifier is present |
| the type-neutral search | which title did TMDb hear in this name | an identifier is already resolved |
| the typed search | which film, or which series, of this name | the rung above it found something |
| the filename fallback | what does the release name itself state | the name yields no usable title, or nothing new |

## A stated identifier and a guessed one

An identifier TMDb states lives 365 days; one the plugin guessed lives 30 (TmdbCache.cs:62-65). Asked by
number, TMDb answers which identifier belongs to that number. Asked by name, the plugin has only guessed,
so the fact carries its title and year in its key and a different title reads as a miss
(TmdbCache.cs:574-589).

## When a title is closed, and when it opens again

Four failed attempts close a title, with pauses of two, four and eight seconds between them
(TitleLadder.cs:26, 66-70). Only a verdict closes it: a disturbance, or a title carried without a readable
identifier, closes the pair, never the title (TitleLadder.cs:100-106). Nothing schedules the reopening: the
row carries the moment it was written, and reopening is computed from that date plus seven days and an
offset of up to six hours from the lock key (TitleFailureStore.cs:164, 320-324). A failed probe writes a
new date (TitleFailureStore.cs:255-258).

## What the record holds, and what it never holds

The file row's key is our own identifier, never a title and never a Jellyfin id; beside it stand the
title's identifiers — IMDb, TMDb and one number per source that must create titles (Entities.cs:186-254).
The shelf holds one fact per question — kind, subject, value, size, date — plus a raw answer with a
shorter life (TmdbCache.cs:173-180). Never kept: a credential, because the shelf's key drops the access
parameter (TmdbCacheHandler.cs:63-85), and the media or subtitle bytes, only their hash and place.

## What it cannot decide

A title with no match keeps no identity: the download run logs `no id resolvable (TMDb ladder failed)` and
skips the file; the upload run counts a `not-found` and requeues the item (DownloadPipeline.cs:1577-1589,
UploadPipeline.cs:1586-1598). An ambiguous year is the second limit. Jellyfin's year is often the import
year, so the typed search repeats without it when the filter is empty, and the strict twin refuses a hit
with another year (TmdbImdbResolver.cs:326-330, 655-698).

## The requirements it needs

F-M66 keeps an unresolved item in the ladder, F-M190 makes the resolution type-free, F-M217 lets the file
name decide, F-M219 and F-M231 place the two search rungs and the identity test, F-M738 makes the pair
decide. The fallback is F-M346 to F-M348, the bracketed year F-M510, the stored answers F-M542 and
F-M775. The lock is F-M608, F-M610, F-M615, F-M618, F-M619 and F-M771; the shelf that never closes a title
is F-M777.
