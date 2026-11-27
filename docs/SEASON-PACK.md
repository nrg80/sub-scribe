# Season pack upload

A season pack is one ZIP that holds every episode of a season in one language, using the same variant
for all of them. The plugin builds that ZIP from the files on your disk and hands it to a subtitle
site. This page describes how that works, in the order the plugin does it.

## Contents

- [1. What a pack is](#1-what-a-pack-is)
- [2. The switches](#2-the-switches)
- [3. Seven checks before anything is unpacked](#3-seven-checks-before-anything-is-unpacked)
- [4. What is checked after unpacking](#4-what-is-checked-after-unpacking)
- [5. How the archive is built and sent](#5-how-the-archive-is-built-and-sent)
- [6. What a pack never does](#6-what-a-pack-never-does)
- [7. What the log says](#7-what-the-log-says)
- [8. Where these rules live](#8-where-these-rules-live)

## 1. What a pack is

One pack carries exactly one season, one language and one variant. The variant means whether the
subtitle includes the hearing-impaired markers or not. So a season with 26 languages makes 26 packs,
not one big archive.

All members of a pack come from the same release, with the same quality and the same frame rate. A
season that mixes releases is not a pack, and the plugin never sends it as one. A pack is only ever
built from files that lie on your disk; the plugin never puts together a pack out of something it
downloaded.

## 2. The switches

Two switches decide, and they work independently of each other:

| What may go up | Switch | Where it sits |
|---|---|---|
| one episode on its own | `Upload enabled` | the source's own settings |
| the whole season as one ZIP | `Prefer season pack` (`preferSeasonZip`) | the source's card in the settings page |

Both switches may be on at the same time. A subtitle that already went up as a single may still go
into a pack afterwards, so the pack steps beside the single instead of replacing it.

A switch is only a permission: it says that something may go up, and the checks below decide whether it
really does. A site that never takes packs has no pack switch on its card at all.

## 3. Seven checks before anything is unpacked

These seven checks answer one question: can a pack be formed at all? They only read what the plugin
already knows, which is the folder on disk, the stream lists and the database. None of them starts
ffmpeg, so a refusal costs a few seconds instead of minutes.

1. **Is the season complete?** Every episode of that season has to lie on disk. When one is missing,
   the run waits instead of refusing for good.
2. **Does a site already have the pack?** The question is asked per site, and only sites with their
   pack switch on are counted here. When every one of them already has it, there is nothing to do.
3. **Is the old refusal still valid?** A stored "no pack" carries a signature: the number of episodes
   plus size and modification time of every file. The same signature means the same answer, so nothing
   is unpacked again.
4. **Is any site still open?** A site that refused this season stays closed as long as the signature
   holds. When no site is left open, the season is done for this run.
5. **Can every episode join?** The answer comes out of the database, per episode. A track that was
   refused because of its content stops the pack here: text that is too small, a track that failed a
   gate, a language the file does not carry. A track that was only *already uploaded* does not stop
   it.
6. **One release, one quality, one frame rate?** The run reads this from the file names and from the
   stream lists, without unpacking anything. Mixed values end the pack.
7. **Is there a text track of this language, and is one of them still open?** The run reads the stream
   list and asks the rejection records track by track. When every track of that language was already
   refused, nothing is unpacked.

If one of the seven answers no, the run writes the reason to the log and, when the reason concerns the
whole season, stores it together with the signature. If all seven answer yes, unpacking starts.

## 4. What is checked after unpacking

Unpacking is the expensive step, so it only starts after those seven checks agreed. The text that comes
out of the file then has to pass these gates. A failure still ends the pack for this run.

Every track of the wanted language is tried, one after another in file order, and each of them has to
pass all five gates:

- **Size.** Text under 2 KB is not a subtitle worth publishing.
- **Language.** The text has to speak the language of the pack. On the single path a tag that lies is
  corrected and the text goes up under the detected language. In a pack a correction counts as a
  refusal, because the archive carries one language and cannot claim another.
- **Cues.** Fewer than 30 cues means the track is not a full subtitle.
- **Timings.** They have to parse, they have to run forward, and each cue has to last between 0.1
  seconds and 10 minutes.
- **Sync.** The last cue must not end more than 5 minutes after the runtime, and the cues must not sit
  in less than a tenth of the runtime.

Two kinds of track never become a candidate. A forced track is not dialogue, so it is not offered and
is never unpacked for a pack. A loose subtitle file beside the video is no candidate either, because a
pack is built from what is inside the container.

Two more checks look at the season as a whole:

- **Equality.** All members must share one quality and one frame rate.
- **Origin.** Not one member may come from a download of any of our sources, whether it was downloaded
  directly or unpacked out of a downloaded pack. A single such member and no pack is built for that
  season, because such an archive would be a re-upload by construction.

## 5. How the archive is built and sent

The plugin writes the members under names the site can read, and it builds that name per site: each
site reads its own language code in the file name, because the sites do not spell every language the
same way. The archive itself is one ZIP over the season, in the language and the variant that the pack
carries.

Sending is an order to that site's engine, not a call past it. The engine answers what only the run
knows: which series title, which year and which season number apply. Depending on what the site
accepts, the engine hands over the finished ZIP or the finished texts as one set. When the answer
carries an id, the plugin stores it on the pack row; when it carries none, none is invented.

## 6. What a pack never does

- It never deletes anything at a site, because a pack is an addition and never a replacement.
- It never re-sends something that this site already holds.
- It never carries a subtitle that came out of a download.
- It never mixes releases, qualities or frame rates.
- It never carries a forced track and never a loose subtitle file.
- It never carries a language that the file does not speak.
- It never runs only because a switch is on; it runs because the seven checks agreed.

## 7. What the log says

Every step writes one line, and all of them stand at the normal level:

- `season pack candidate` says that the season is whole and that one release covers it.
- `season pack skipped before any extraction` says that one of the seven checks said no.
- `season pack filler` says which track was tried, which one did not qualify, and why.
- `season pack built` says that the archive exists.
- `season pack sent` says what became of the send, and so does the line
  `season pack — {Have} of {Rest} source(s) already hold the container`.

A pack that shows up in the statistics but never in the log was not formed.

## 8. Where these rules live

The rules themselves stand in [SPECIFICATION.md](SPECIFICATION.md), mainly under F-M372, F-M397,
F-M442, F-M443, F-M585, F-M600, F-M611, F-M647, F-M650, F-M687, F-M799 and F-M800. This page only tells
how they work together, and the wording follows [TERMS.md](TERMS.md).

Three suites guard the behaviour: `endpoints`, `seasonpack` and `packretry`. Their catalogue entries
stand in [TESTS.md](TESTS.md).
