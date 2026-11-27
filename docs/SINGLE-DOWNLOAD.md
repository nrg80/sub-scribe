# Single download

A single download is one subtitle for one file, in one language and one variant, taken from one site
and written beside the video.

## The switches

`Download enabled` on the source card decides whether that site is asked at all. The target languages
are typed two-letter in the settings page and translated to the one internal form at the boundary.

## The order of a run

The run collects the candidate files, asks each switched-on site, picks one candidate, writes the file
and books the result. A file whose pairs are all settled is skipped with one line, and when nothing is
open any more the entry ends.

## Checks before the fetch

The language has to be carried by the site; the pair must not be settled already; a stored refusal
counts; and a site that answered with a quota error is not asked again in that run.

## Checks after the fetch

An archive is unpacked, then the text is judged: at least 100 bytes, the SRT has to parse and to run
forward, at least 30 cues, and the detected language has to be the wanted one. A wrong language is
refused here and never corrected, because the file would otherwise lie on disk under the wrong name.
The last cue must also fit the runtime.

## The name and the place

The subtitle is written next to the video as `<name>.<language>.<slot>.srt`, with the language in three
lower-case letters. Slots 01 to 49 are free places, and 51 to 99 keep an original that a correction is
working from. Such an original is locked against upload twice over: it is booked like any other
download, so its origin bars it from every site, and its content hash makes the uploader refuse it as a
duplicate. The
database then carries one row per site with the language, the slot and the origin.

## What a single download never does

It overwrites nothing, it never writes a language it did not verify, it never saves a duplicate, and it
never uploads what it downloaded.

## What the log says

`SKIP {File} — nothing open`, `{File} [{Lang}] — not available`,
`{File} [{Lang}] — download reject {Release}: {Reason}`, `{File} [{Lang}] — QA reject {Release}: {Reason}`.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M15, F-M16, F-M43, F-M44, F-M282, F-M316, F-M375,
F-M476, F-M504 and F-M790. Guarded by `endpoints`, `sources` and `statefile`.
