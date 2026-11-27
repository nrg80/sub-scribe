# Single upload — how one is formed, and when it may go up

**Owner: the one file sent.** One text track of one language, or one loose subtitle
beside the file, for one source: screened, gated, named and sent. This paper states the whole way —
which switch lets it out, which checks run before the text is read, which judge the text, what the
request carries, what the answer returns, and what a single upload never does.

*Derived document: the rules are stated normatively in `SPECIFICATION.md` (F-M284, F-M375, F-M429,
F-M487, F-M489, F-M500, F-M539, F-M541, F-M546, F-M556, F-M576, F-M577, F-M581, F-M585, F-M586,
F-M587, F-M588, F-M605, F-M609, F-M763, F-M799). This paper describes how those rules work together.
The words are those of `TERMS.md`.*

## Contents

- [1. What this is about](#1-what-this-is-about)
- [2. What a single upload is](#2-what-a-single-upload-is)
- [3. The one switch that lets it out](#3-the-one-switch-that-lets-it-out)
- [4. Which file is upload material at all](#4-which-file-is-upload-material-at-all)
- [5. What is settled before any text is read](#5-what-is-settled-before-any-text-is-read)
- [6. What is judged once the text exists](#6-what-is-judged-once-the-text-exists)
- [7. What is sent, and what comes back](#7-what-is-sent-and-what-comes-back)
- [8. What a single upload never does](#8-what-a-single-upload-never-does)
- [9. What the log says](#9-what-the-log-says)
- [10. The checks](#10-the-checks)

## 1. What this is about

A source takes a subtitle in two shapes: as a **single upload**, one file at a time, and as a **pack**,
one container over a whole season. The pack has its own paper (`SEASON-PACK.md`). This one follows a
single from the track list to the mark in the plugin database.

The unit of the whole paper is the **slot**: one file, one language, one variant, one source. A media
file usually holds several slots at once, one per text track, and every one of them is decided on its
own. A slot never mixes two languages, two episodes or two variants.

Two sources mean two slots over the same text, and the run asks both in one pass. The checks per source
run in chain order before anything is sent, and one transfer slot pays for the track once, not once per
source (F-M587).

## 2. What a single upload is

A single consists of exactly five statements:

| Part | What it names |
|---|---|
| one file | a film, or exactly one episode of a series |
| one language | the ISO code the text speaks |
| one variant | plain, or hearing-impaired (SDH) — they are two slots, never one |
| one source | the site the slot belongs to; another source is another slot |
| one text | a text track inside the container, or one loose subtitle beside it |

Nothing else belongs to it. It carries no second language, no second episode and no container. The
variant is part of the identity on both sides of the plugin database, so a normal upload never blocks
an SDH upload of the same track (F-M489).

## 3. The one switch that lets it out

One switch decides, and it belongs to the source, not to the file:

| Direction | Switch | Where | Effect |
|---|---|---|---|
| single upload | `uploadEnabled` | the source's own settings | the slot may go up as a single |
| season pack | `Prefer season pack` | the source's card in the settings page | the slot may go up as a pack |

The two switches are independent, and neither narrows the other (F-M585). Three sentences follow:

- **A single goes up when the upload switch of its source is on.** Nothing else is needed for a single.
- **A pack does not stop a single.** A run builds its packs first and then works the singles, and a
  position a pack of this run already carries still goes up as a single beside it (F-M429, F-M585).
- **Neither switch is a licence to overrule a check.** The switch says *may*, the checks say *whether*.

A source that is switched off is not asked at all, and the line says so instead of staying silent. A
source whose access data is missing falls out of the chain as well, and the other source keeps going.
A run in dry mode asks only the switch, because it makes no call and needs no access data.

## 4. Which file is upload material at all

Only text can be uploaded, so only text is a candidate:

- **A text track inside the media container.** It is counted by its position among all subtitle
  streams, bitmap tracks included, because that is the number an extraction pass addresses.
- **A loose subtitle beside the file.** Its language comes from its name; a name without a language
  token is detected from its own text.
- **A bitmap track is no candidate.** There is no text to read, to hash or to judge, so it never
  becomes a slot.

A **forced** track is no candidate either, and that holds on both sides: an embedded forced stream is
dropped by the one predicate that asks for dialogue, and a loose forced subtitle is named in the log
as observed but never uploaded (F-M284).

## 5. What is settled before any text is read

The questions below cost no extraction: a database look, a stream list, a language table. In this
order:

1. **Is every source already done with this file?** The media row carries a completion stamp per
   source, and one look at it ends the file — provided no loose subtitle beside it still needs a
   source (F-M576, F-M556).
2. **Does every text track have its end state, and does no source want a pair any more?** Then the
   file ends before the extraction pass (F-M539).
3. **Is every text track already rejected?** A discarded track can no longer be uploaded by anyone,
   so the file ends here too (F-M541).
4. **Is the track parked after an extraction failure?** The reason is stored on the track and counts
   for seven days; after that the track gets its next attempt without anyone deleting a row (F-M577).
5. **Did this source queue this item?** The queue is kept per source, so a source that never wanted
   the file does not hold it open (F-M546).
6. **Does any source still want this pair?** The question reads the proof only — the stored verdict
   per source and the mark of this position — and it says *no source wants it* only when that is true
   (F-M581).
7. **Does the source carry this language at all?** A source that does not carry it is not one that
   still wants it, and the pair is closed for it with a language refusal (F-M586).

A **no** at any of the seven ends the file for this run, and the log names the question that said it.

What survives all seven is read in **one extraction pass over all text tracks** of the file. That pass
is the expensive step, so nothing below it is allowed to run before it.

## 6. What is judged once the text exists

The per-track questions come next, in this order, and every one of them costs no second read of the
container:

- **The canonical form.** Line endings are normalised before hashing, so the same text always hashes
  to the same value and two payloads of one text cannot differ.
- **The language of the tag.** A tag without a language is resolved by detection on the text; a tag
  the table cannot translate is a stored rejection, not a guess.
- **The pair, per position.** A pair one source already holds is a self-echo for that source only, and
  the position is recorded as rejected-by-self-echo rather than uploaded twice (F-M489).
- **A stored reject for this position.** A text that is demonstrably up settles the position instead
  of being rejected again; a text that is up at one source and open at the other goes on to the chain
  (F-M500).

Then the content gates, in the order the code asks them:

1. **Size.** Below the 2 KB floor the text is not a subtitle worth publishing. The floor is hard and
   always on.
2. **Language.** With the check switched on, the detected language of the text is compared with the
   language of the slot. A tag that lies is corrected here and the text goes up under the detected
   language (F-M375).
3. **Cues.** Fewer than 30 cues means the track is not a full subtitle.
4. **Timings.** The timings must parse, must rise monotonically, and every cue must stay between
   0.1 s and 10 min.
5. **Sync.** The cue span must not reach more than five minutes past the runtime, and the cues must
   not sit in less than a tenth of it.

The two duplicate verdicts — already up at this source, already known content — are not quality
judgements. They are stored separately and never count as a gate failure.

## 7. What is sent, and what comes back

The name is built **per source**: each source reads its own language code in the file name
(F-M799, F-M735). A series name reads `<imdb>.S01E02.<content hash>.CS.srt` for SubDL, and the same
text goes to the series site as `…CZ.srt`. A film name carries no episode number.

The request carries the bytes of the text, that name, the language in ISO form, the variant flag, the
release name, the quality, the frame rate and the target — series or film, both identifiers, season
and episode. It carries no pack.

The answer has exactly four outcomes: **accepted**, **rejected**, **busy**, **failed**. Only the first
two are verdicts. A busy answer carries its wait and writes nothing; a failure is logged and the pair
stays open for the next run (F-M609).

An answer may carry the source's own identifier for the submission. SubDL's single answer usually
names none, so the upload is remembered and its identifier is read back once at the end of the run
(F-M588). A site that names none at all gets a mark of our own instead, so an upload without an
identifier is still distinguishable from one that never happened.

Stored afterwards, on the row of the pair: the state, the content hash, the identifier and the moment
per source. Two sources that both accepted produce two marks, not one shared mark (F-M489). A
substantive no is remembered as well, per track, source and content, so the same text is not offered
again (F-M605). A run that delivers to two sources counts two deliveries (F-M763).

## 8. What a single upload never does

- It never deletes anything at a source. A single is an addition, never a replacement.
- It never sends the same pair to the same source twice (F-M489).
- It never sends a text of known origin. A subtitle that came out of a download goes nowhere (F-M487).
- It never sends a language the source does not carry (F-M586).
- It never invents an identifier. A mark of our own says it is ours, and a missing one stays missing.
- It never carries a forced track, and never a bitmap track.
- It never runs because a switch is on — it runs because the checks agreed.
- It does **not** hold a position back because a pack carries it. A pack member goes up as a single
  beside the pack, just as the pack runs beside a single that is already up (F-M585).

## 9. What the log says

One line per step, in the order the run takes them:

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

## 10. The checks

`endpoints` guards most of this paper. T286 holds the cheap head exit, T287 the parked extraction
failure, T290 the stored verdict, T291 the pair question on the proof. T292 and T294 hold the verdict
per source, T295 that pack and single no longer exclude each other. T296 covers the language a source
does not carry, T297 the two sends in one pass, T298 the identifier read back once. T299 holds the
pause on the track and T300 the mark written immediately (F-M576, F-M577, F-M581, F-M585, F-M586,
F-M588). T444 covers the name per source (F-M799). `dupskip` holds T391, the remembered no; `spec-doc`
holds T403, the hygiene rule this paper obeys. The name itself is checked by
`scripts/tests/uploadname/check.py`, the catalogue entry of every test stands in `TESTS.md`.
