# Season pack upload

A pack is one ZIP that carries every episode of one season, in one language and one variant. The plugin
builds it from the files on disk and hands it to a site.

## The switches

`Upload enabled` on the source card lets a single go up, and `Packs` puts the source into the pack slot,
which is what allows a season to go up as one ZIP. The two are independent: a subtitle that is already up
as a single may go into a pack afterwards. `Prefer season pack` (`preferSeasonZip`) is not an upload
switch, it belongs to the download side and makes a run ask for an archive before a single.
A switch is only a permission, and the checks below decide whether it happens.

## Checks before anything is unpacked

Seven checks, and none of them starts ffmpeg. Is the season complete on disk? Does a counted site
already hold the pack, where only sites with the pack switch on are counted? Does a stored refusal
still hold for the same files, with the signature being the episode count plus size and modification
time? Is any site still open? Can every episode join, where a content refusal stops the pack and an
already uploaded track does not? Is it one release with one quality and one frame rate? And is there a
text track of that language that was not already refused? Any no ends the run and stores the reason
with the signature.

## Checks after unpacking

Every track of the language is tried in file order. The gates are a 2 KB floor and at least 30 cues.
Timings must parse and run forward, and every cue must last between 0.1 seconds and 10 minutes. The
last cue may not end more than 5 minutes after the runtime, and the cues must cover at least a tenth of
it. The text also has to speak the language, and in a pack a correction is a refusal. Then the season as
a whole: all members share one quality and one frame rate, and not one member comes from a download of
any source. Forced tracks and loose subtitle files never become a candidate.

## How it is built and sent

Member names are built per site, because each site reads its own language code in the file name. The
archive is one ZIP per season, language and variant. Sending is an order to that site's engine, which
hands over the ZIP or the finished texts; an id in the answer is stored, and none is invented.

## What a pack never does

It deletes nothing at a site, re-sends nothing that a site already holds, and carries no downloaded
text, no mixed release, no forced track, no loose file and no language the file does not speak. It also
never runs just because a switch is on.

## What the log says

`season pack candidate`, `season pack skipped before any extraction`, `season pack filler`,
`season pack built`, `season pack sent`.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M372, F-M397, F-M442, F-M443, F-M585, F-M600, F-M611,
F-M647, F-M650, F-M687, F-M799 and F-M800. Guarded by `endpoints`, `seasonpack` and `packretry`.
