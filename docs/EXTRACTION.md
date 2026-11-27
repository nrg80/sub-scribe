# Extracting the text out of a media file

The plugin keeps no subtitle text: what it sends is read out of the media container first. The store of
extracted tracks stands in [CACHING.md](CACHING.md).

## One pass, every text stream

One ffmpeg process reads a file and writes one `.srt` per text stream: `-map 0:s:N -c:s srt -f srt <output>`
repeated on one input. The old shape opened the container per stream — 83 reads of one gigabyte on an
83-stream episode, and the only items above ten minutes in a run with an 11 s median (F-M5).

## What ffmpeg is asked for

`0:s:N` counts the subtitle streams inside the container, bitmap and forced ones included, and never a loose
subtitle beside it, so the position comes from the unfiltered list (F-M658). The call carries no `?`: with
three streams, `-map 0:s:9?` wrote the first subtitle stream into position 9, exited 0 and said nothing.

## What comes back

Each output file is read back as text, and whitespace alone counts as no text for that stream. A read that a
cancellation interrupts is rethrown, not booked as an error per stream (F-M726). The folder holding the
outputs is deleted on every exit, after a success, a failure and an abort (F-M7).

## The form, and why it is the identity

One form comes first: UTF-8 without a byte order mark, LF line endings, no trailing whitespace, no cue of a
tenth of a second or shorter (F-M185). ffmpeg writes CRLF for some streams and LF for others, so the same
subtitle arrives in two byte shapes. The hash is the MD5 of exactly the bytes that go up (F-M186); the body
and the written file are the UTF-8 of that same string, so written and hashed bytes agree by construction, and
the fetched style would hash bytes that exist nowhere.

## What makes an extraction fail

Four things end a pass without text: no ffmpeg found, which the lookup reports and the system fails; a
position the file lacks, or one on video or audio; a container gone, locked or unreadable; a spawn or a
read-back that throws. An empty output under a clean exit is none of these: the stream says it has no text.

## A failed pass is not a refusal

The exit code decides what a null means. After exit 0 an empty stream is a verdict about content and costs no
second read; after any other exit it is a suspicion, asked again alone, and only what stays empty counts
(F-M659). Then it is parked as `extraction-failed`, retried after seven days if the file exists (F-M577).

## What the two flags do to the selection

A forced track is never asked for: it carries foreign-language scene lines, not the dialogue, so it leaves the
selection before the call, judged by the disposition and the title (F-M246, F-M367). A hearing-impaired track
stays in, holding its own place in the pair: an SDH text never counts as a duplicate (F-M71), and Jellyfin's
flag decides before the title (F-M254).

## What it costs

One read of the media container per file, whatever it carries: three languages cost one process, and the pack
filler keeps the texts for the rest of the run (F-M405, F-M441). In the measured run 449, 139 passes read 94
GB for 155 uploads, about 0.7 GB a pass, and not a byte of it crosses the network. The look-ahead runs the
next file's pass while the current one sends, never two ffmpeg at once (F-M596, F-M613).

## The requirements it needs

F-M5, F-M7, F-M71, F-M185, F-M186, F-M246, F-M254, F-M367, F-M405, F-M441, F-M577, F-M596, F-M613, F-M658,
F-M659 and F-M726. Guarded by `endpoints` and by the cases T287, T306 and T323.
