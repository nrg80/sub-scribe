# Single upload

A single upload is one subtitle of one file, in one language and one variant, going up to one site on
its own.

## Which file counts

A text track inside the media file counts, and so does a loose subtitle beside it. Bitmap tracks never
count, because they carry no text, and a forced track never goes up at all.

## The switches

`Upload enabled` on the source card lets a single go up. The pack switch is separate and does not
depend on it, and both may be on at once. A switch is only a permission, and the checks below decide.

## Checks before anything is unpacked

None of these starts ffmpeg. Is the file already settled for every source? Was this track refused
before? Does a loose subtitle beside it still need work? Is the text a duplicate of something the site
already has? And is the language one that this site carries at all?

## Checks after unpacking

The text has to clear 2 KB and to speak the language of the slot, and it needs at least 30 cues. Its
timings have to parse and run forward, with every cue between 0.1 seconds and 10 minutes. Its last cue
must not end more than 5 minutes after the runtime. A tag that lies is corrected here, and the text goes
up under the language that was detected.

## What is sent, and what comes back

The file name is built per site, because each site reads its own language code in the name. The request
carries the text and the language the site expects; the answer either carries an id, which is stored,
or it does not, and then none is invented. What is stored is the track, its language, the site and the
id.

## What a single upload never does

It deletes nothing, it never invents an id, and it never sends a forced track. A subtitle that was
downloaded from any site never goes up at all: not back to where it came from, and not to any other
site either. The origin alone decides that, once per file, before any site is looked at. What a site
already holds of our own texts is a second and separate question, read from that site's own submission
id rather than from a shared flag, and it holds for an embedded track and for a loose sidecar alike.

A corrected copy inherits that lock from the original it was formed from (F-M806). The copy beside
the kept original (slots 51–99) carries different bytes, so the content hash alone cannot recognize
it; the reserved block proves the file was fetched, and the origin is carried over to the copy's own
hash — once written, every later run asks the content and nothing else.

## What the log says

`SKIP {File} — item settled before extraction`, `SKIP {File} — every text stream was rejected earlier`,
`LANGUAGE {File} — tagged {Tag}, detected {Lang}`, `UPLOADED {File} [{Lang}] → {Source}`, `origin:subdl — downloaded there, never uploaded`.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M284, F-M375, F-M429, F-M539, F-M556, F-M576, F-M581,
F-M585, F-M588, F-M605 and F-M799. Guarded by `endpoints`, `sources` and `packretry`.
