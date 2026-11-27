# Season Pack — how one is formed, and when it may be formed

**Owner: the container built.** One season, one language, one variant, one release — packed into one ZIP by
the plugin and handed to a source. This paper states the whole way: which two switches decide, which
checks run before the first extraction, which run after it, how the container is built, and what a pack
never does.

*Derived document: the rules are stated normatively in `SPECIFICATION.md` (F-M372, F-M397, F-M429,
F-M431, F-M442, F-M443, F-M487, F-M585, F-M600, F-M611, F-M647, F-M650, F-M687, F-M800). This paper
describes how those rules work together. The words are those of `TERMS.md`.*

## Contents

- [1. What this is about](#1-what-this-is-about)
- [2. Two switches, and nothing else](#2-two-switches-and-nothing-else)
- [3. May a pack be formed — the seven checks without extraction](#3-may-a-pack-be-formed--the-seven-checks-without-extraction)
- [4. What is still checked after the extraction](#4-what-is-still-checked-after-the-extraction)
- [5. How the container is built and sent](#5-how-the-container-is-built-and-sent)
- [6. What a pack never does](#6-what-a-pack-never-does)
- [7. The record, and what a new attempt needs](#7-the-record-and-what-a-new-attempt-needs)
- [8. What the log says](#8-what-the-log-says)
- [9. The checks](#9-the-checks)

## 1. What this is about

A source sells a season twice: as **single uploads**, one episode at a time, and as a **season pack**,
one ZIP that carries every episode of the season in one language. The second is worth more to the
source, so the plugin offers it whenever it may.

A pack is only ever formed from files that lie on disk and from text that lies inside those files. It
is never assembled out of anything the plugin downloaded, and it is never assembled out of a mixed set
of releases. Both sentences are checked, not assumed.

The unit of the whole paper is the **slot**: one season, one language, one variant, one source. A pair
is the same slot for both sources at once — the container is built once and offered to each source
that wants it (F-M647).

## 2. Two switches, and nothing else

Two switches decide, and they are independent:

| Direction | Switch | Where | Effect |
|---|---|---|---|
| single upload | `uploadEnabled` | the source's own settings | the pair may go up as a single |
| season pack | `Prefer season pack` (`preferSeasonZip`) | the source's card in the settings page | the pair may go up as a pack |

Four sentences follow from that, and they are the whole policy:

- **Single when the upload switch is on.** Nothing else is needed for a single.
- **Pack when the pack switch is on.** Nothing else is needed for a pack.
- **Both may be on.** A single that is already up does not stop the pack, and a pack does not stop a
  single. The pack steps beside the single (F-M585).
- **Neither switch is a licence to overrule a check.** The switches say *may*, the checks say *whether*.

A source that carries no packs has no pack switch at all — its card offers none, and the run asks its
capability instead of a switch (F-M687). The **slot** of a pair is exactly the list of sources whose pack
switch is on, so a switched-off source never waits for a container and never holds one off. Upload and download are separate directions: the switch of one
direction never enables the other.

## 3. May a pack be formed — the seven checks without extraction

The question *can a pack be formed* is answered **before any extraction**. Each check reads what the
plugin already knows: the folder, the stream lists, the database. None of them calls ffmpeg, so a
refusal costs seconds.

1. **Is the season whole?** Every episode of the season lies on disk. A missing episode means wait, not
   refuse (F-M372).
2. **Does a source already hold the container?** Asked per source, and only the sources that are cleared
   for pack upload stand in the slot — a source with its pack switch off is not one of them. When every
   source of the slot has it, there is nothing to do (F-M647, F-M682).
3. **Is the refusal still valid?** The stored verdict carries a signature — episode count plus size and
   modification time of every file. Same signature, same verdict: nothing is extracted (F-M611,
   F-M650).
4. **Is any source still open?** A source that refused this season stays closed while the signature
   holds. When no source is left open, the slot is done (F-M644, F-M650).
5. **Can every episode join?** Asked per episode out of the store. A track that is refused for a reason
   of content — too small, a failed gate, a language the file does not carry — stops the pack here.
   A track that is only *already uploaded* does not stop it (F-M600, F-M585).
6. **One source, one quality, one frame rate?** Read from the path and the stream list. A season of
   mixed releases is not a pack and is never sent as one (F-M442).
7. **Is there a text track of this language at all, and is one of them still open?** The stream list is
   read by id, and the rejection store is asked per track. When every track of the language is already
   refused, nothing is extracted (F-M443, F-M468).

A **no** at any of these seven ends the slot for this run. The reason is written to the log and, where
it belongs to the season, stored with the signature. A **yes** at all seven leads to the extraction.

## 4. What is still checked after the extraction

Extraction is the expensive step, so it is only taken when the seven checks above agreed. After it the
text itself is judged, and here a **no** still ends the slot.

Per track, in file order, every track of the requested language is tried (F-M431):

- **Size.** Under the floor the text is not a subtitle worth publishing.
- **Language.** The detected language of the text must be the language of the slot. On the single path
  a lying tag is corrected and the text goes up under the detected language (F-M375). In a pack a
  correction is a refusal, because a container carries one language and cannot claim another.
- **Cues.** Too few cues means the track is not a full subtitle.
- **Timings.** The timings must parse, must be monotonic, and their durations must stay in a plausible
  band.
- **Sync.** The cue span must fit the runtime, and the cues must not sit in a small corner of it.

Forced tracks are no candidate at all: they are not dialogue, they are not offered, and they are never
extracted for a pack. Loose subtitles beside the file are no candidate either — a pack is built from
the container.

Per season, twice:

- **Equality.** All members must share one quality and one frame rate (F-M442).
- **Origin.** Not one member may come from a download of any of our sources — downloaded or unpacked
  out of a downloaded pack. One such member and no pack is built for that season, because such a
  container would be a re-upload by construction (F-M397, F-M487).

## 5. How the container is built and sent

The members are written under names the source can read, and the name is built **per source**: each
source reads its own language code in the file name (F-M799). The archive itself is one ZIP over the
season, in the slot's language and variant.

Sending is an order to the source's engine, not a call past it (F-M687). The engine answers the
question that only the run can answer: which series title, which year, which season number. Two forms
exist, and the capability of the source decides which one is used:

- the **prebuilt container** — the ZIP is handed over as it is,
- the **members** — the finished texts are handed over as one set.

An answer that carries an id is stored on the pack row. An answer without one is not invented.

## 6. What a pack never does

- It never deletes anything at a source. A pack is an addition, never a replacement.
- It never re-sends a pair that this source already holds (F-M647).
- It never carries a subtitle that came out of a download (F-M397).
- It never mixes two releases, two qualities or two frame rates (F-M442).
- It never carries a forced track, and never a loose sidecar.
- It never carries a language the file does not speak (F-M375).
- It never runs because a switch is on — it runs because the seven checks agreed.

## 7. The record, and what a new attempt needs

The pack row carries title ids, season, language, variant, release, the members, the source ids and a
signature. A refusal is stored the same way, with the reason and the same signature.

That signature is what makes the second run cheap: as long as the files keep their size and their
modification time, the verdict holds and nothing is unpacked. A new attempt therefore needs a changed
file, a changed member set, or a cleared verdict. Deleting rows is the last way, not the first: it
throws away what the run has already learned and pays the extraction again.

## 8. What the log says

One line per step, all of them at the normal level:

- `season pack candidate` — the season is whole, and one release covers it.
- `season pack skipped before any extraction` — one of the seven checks said no.
- `season pack filler` — which track was tried, which did not qualify, and why.
- `season pack built` — the container exists.
- `season pack sent`, `season pack — {Have} of {Rest} source(s) already hold the container` — what
  became of the send.

A pack that appears in the statistics but never in the log is not a pack that was formed.

## 9. The checks

`endpoints` and `seasonpack` guard this paper: T445 holds that a pack asks its lock with the pack in
mind, and T446 holds that this document, the specification entry and the switch names agree. The
catalogue entry of every test stands in `TESTS.md`.
