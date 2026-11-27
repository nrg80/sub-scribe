# Single upload

A single upload is one file that the plugin sends to one subtitle site. It carries the text of one
language in one variant and belongs to one source. The plugin screens it before it leaves and judges
the text again afterwards, and every one of those steps has a rule behind it. This page follows a
single upload from the track list to the mark in the plugin database, in the order the plugin walks it.

## Contents

- [1. What a single upload is](#1-what-a-single-upload-is)
- [2. The switches](#2-the-switches)
- [3. What is checked before anything is unpacked](#3-what-is-checked-before-anything-is-unpacked)
- [4. What is checked after unpacking](#4-what-is-checked-after-unpacking)
- [5. What is sent and what comes back](#5-what-is-sent-and-what-comes-back)
- [6. What a single upload never does](#6-what-a-single-upload-never-does)
- [7. What the log says](#7-what-the-log-says)
- [8. Where these rules live](#8-where-these-rules-live)

## 1. What a single upload is

A single carries one file, one language, one variant and one source, and nothing else belongs to it.
The file is a film or exactly one episode of a series. The variant says whether the text carries the
hearing-impaired markers, and plain and SDH are two uploads, never one. The variant is part of the
identity on both sides of the plugin database, so a plain upload never blocks an SDH upload of the
same text (F-M489).

One media file usually holds several of these at once, because every text track inside it is decided
on its own, and a loose subtitle may lie beside it as well. No single ever mixes two languages, two
episodes or two variants.

Only text can go up, so only two kinds of thing come into question. A text track inside the container
is one, and it is counted by its position among all subtitle streams, bitmap tracks included, because
that is the number the extraction pass addresses. A loose subtitle beside the video is the other, and
its language comes from its file name, or from the text itself when the name carries no language. A
bitmap track is never one, because there is no text to read, to hash or to judge.

A forced track never goes up either. An embedded forced stream is dropped by the one predicate that
asks for dialogue, and a loose forced subtitle is written to the log as seen but never sent (F-M284).

Two sources over the same text are two attempts, not one. Both are asked in the same run, and the
waiting time of a track is paid once, not once per source (F-M587).

## 2. The switches

One switch decides about singles and another decides about packs. Both belong to the source and not
to the file, and they work independently of each other.

| What may go up | Switch | Where it sits |
|---|---|---|
| one episode on its own | `Upload enabled` (`uploadEnabled`) | the source's own settings |
| the whole season as one ZIP | `Prefer season pack` (`preferSeasonZip`) | the source's card in the settings page |

Both may be on at the same time, and neither one narrows the other (F-M585). A run builds its packs
first and works the singles afterwards, and a position that a pack of this run already carries still
goes up as a single beside it (F-M429, F-M585). A pack never holds a single back, and a single that is
already up never keeps a text out of a pack.

A switch is only a permission: it says that something may go up, and the checks below decide whether
it really does. A source that is switched off is not asked at all, and the log says so instead of
staying silent. A source whose access data is missing falls out of the chain, and the other source
keeps going. A dry run reads the switch and nothing else, because it makes no call and needs no access
data.

## 3. What is checked before anything is unpacked

These checks answer one question: is there anything to send at all? They read the plugin database, the
stream list and the language tables, and none of them starts ffmpeg, so a refusal costs a second
instead of minutes. The run asks them in the order given here.

1. **Is the file done for every source?** The media row carries a completion stamp per source, and one
   look at it ends the file, provided that no loose subtitle beside it still needs a source
   (F-M576, F-M556).
2. **Is the item settled already?** Every text track has to hold its end state and no source may want a
   pair any more. When both hold, the file ends before the one extraction pass (F-M539).
3. **Was every text track rejected earlier?** A discarded track can no longer be uploaded by anyone, so
   the file ends here as well (F-M541).
4. **Is a track parked after a failed extraction?** The reason sits on the track and counts for seven
   days, and after that the track gets its next attempt without anyone deleting a row (F-M577).
5. **Did this source queue the item?** The queue is kept per source, so a source that never wanted the
   file does not hold it open (F-M546).
6. **Does any source still want this pair?** That question reads the switch, the media type and the
   proof only. When the answer is no, the log names it and no verdict is written (F-M581). The stored
   marks answer the same question per source: a pair that this source already holds is a self-echo and
   is not sent twice, while a text that one source holds and the other still misses goes on
   (F-M489, F-M500).
7. **Does the site carry this language at all?** A source that does not carry it cannot want it, so the
   pair is closed for it with a language refusal (F-M586).

A no at any of these ends the file for this run. Everything that survives is read in one extraction
pass over all text tracks of the file, and that pass is the expensive step.

## 4. What is checked after unpacking

Once the text exists, it has to pass these gates, in the order in which the code asks them. None of
them costs a second read of the container.

The text is first brought into one canonical form with normalised line endings, so the same text
always hashes to the same value. Then the language of the tag is settled: a tag without a language is
resolved by detection on the text, and a tag that the table cannot translate is stored as a rejection
rather than guessed.

- **Size.** Text under 2 KB is not a subtitle worth publishing, and the floor is hard and always on.
- **Language.** The detected language is compared with the language of the single. A tag that lies is
  corrected here, so the text goes up under the detected language and the log names the tag that was
  wrong (F-M375).
- **Cues.** Fewer than 30 cues means the track is not a full subtitle.
- **Timings.** They have to parse, they have to run forward, and every cue has to last between 0.1
  seconds and 10 minutes.
- **Sync.** The last cue must not end more than 5 minutes after the runtime, and the cues must not sit
  in less than a tenth of the runtime.

The two duplicate verdicts, already there at this source and already known content, are not quality
judgements. They are stored on their own and never count as a failed gate.

## 5. What is sent and what comes back

The plugin builds the name per source, because the sites do not spell every language the same way:
SubDL reads `CS` where the series site reads `cz`, and `PT` where the film site writes `br`
(F-M799, F-M735). A series name reads `<imdb>.S01E02.<content hash>.CS.srt`, and a film name carries no
episode number.

The request carries the bytes of the text, that name, the language in ISO form, the variant flag, the
release name, the quality, the frame rate and the target. The target says whether this is a series or
a film, and it names the two identifiers together with season and episode. No request carries a pack.

The answer ends in one of four cases: accepted, refused, busy or no answer. Only the first two are
verdicts. A busy answer names its wait and writes nothing, while a connection fault or a timeout is
logged and leaves the pair open for the next run (F-M609). The checks per source still run before
dispatch and in chain order, and the two sources are then started together, the second with a small
offset so that the two writes do not land at the same moment (F-M587).

An answer may carry the source's own identifier for the submission. SubDL's single answer usually
names none, so the upload is remembered without an id and the id is read back once at the end of the
run (F-M588). A site that names none at all gets a mark of our own, so an upload without an identifier
stays distinguishable from one that never happened.

Afterwards the row of the pair stores the state, the content hash, the identifier and the moment, and
it stores all of them per source. Two sources that both accepted therefore leave two marks, not one
shared mark (F-M489). A substantive no is remembered per track, per source and per content, so the
same text is not offered again (F-M605), and a run that delivers to two sources counts two deliveries
(F-M763).

## 6. What a single upload never does

- It never deletes anything at a source, because a single is an addition and never a replacement.
- It never sends the same pair to the same source twice (F-M489).
- It never sends a text of known origin, so a subtitle that came out of a download goes nowhere
  (F-M487).
- It never sends a language that the source does not carry (F-M586).
- It never invents an identifier. A mark of our own says that the upload is ours, and a missing
  identifier stays missing.
- It never carries a forced track, and it never carries a bitmap track.
- It never runs because a switch is on; it runs because the checks agreed.
- It does **not** hold a position back because a pack carries it. A pack member goes up as a single
  beside the pack, just as the pack runs beside a single that is already up (F-M585).

## 7. What the log says

The run writes one line per step, in the order in which it takes them, and all of them stand at the
normal level.

- `SKIP {File} — stamped complete for every source, no stream read (F-M576)`
- `SKIP {File} — item settled before extraction (every text stream has its end state, no source still wants a pair, F-M539)`
- `SKIP {File} — every text stream was rejected earlier, nothing can be uploaded (F-M541)`
- `extraction: {Count} text stream(s) in ONE ffmpeg pass for {File} (exit {Exit}){Note}`
- `SKIP {File} s{Pos} [{Lang}] — no source asks for this pair right now, no verdict written (F-M581)`
- `LANGUAGE {File} — tagged {Tag}, detected {Lang}; uploaded under {Lang} (F-M375)`
- `{File} [{Lang}] — carried by the pack built in this run and sent as a single beside it (F-M585, the pack row lists this episode: {Covered}).`
- `UPLOADED {File} [{Lang}] → {Source}`
- `upload saved for {File} [{Lang}] — the SubDL id is read back once at the end of the run (F-M588).`
- `NO ANSWER {File} [{Lang}] → {Source}: {Reason} — no verdict, the pair stays open (F-M609)`

A single that appears in the statistics but never in the log is not a single that was sent.

## 8. Where these rules live

The rules themselves stand in [SPECIFICATION.md](SPECIFICATION.md), under F-M284, F-M375, F-M429,
F-M487, F-M489, F-M500, F-M539, F-M541, F-M546, F-M556, F-M576, F-M577, F-M581, F-M585, F-M586,
F-M587, F-M588, F-M605, F-M609, F-M735, F-M763 and F-M799. This page only tells how they work
together, and its wording follows [TERMS.md](TERMS.md).

Four suites guard the behaviour. Most of it sits in `endpoints`. T286 holds the cheap head exit and
T287 the parked failure. T290 holds the stored verdict, T291 the pair question, and T292 and T294 the
verdict per source. T295 holds the pack beside the single, T296 the language that a source does not
carry, and T297 the two sends in one pass. T298 holds the id that is read back once, T299 the pause
on the track, and T300 the mark that is written at once. The name per source sits in `uploadname` as
T444, the remembered no in `dupskip` as T391, and the hygiene rule that this page obeys in `spec-doc`
as T403. The catalogue entry of every test stands in [TESTS.md](TESTS.md).
