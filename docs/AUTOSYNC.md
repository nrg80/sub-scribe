# Auto-Sync — moving a subtitle onto its audio

*Derived document: every rule carries its `F-M` tag and the constant it is written with. `§4.3` of
`REQUIREMENTS.md` states the rules normatively; this paper explains them, names the constants and
lists what the code actually refuses. The words are those of `TERMS.md`.*

## Contents

- [1. What it is](#1-what-it-is)
- [2. Which track the audio comes from](#2-which-track-the-audio-comes-from)
- [3. What is measured](#3-what-is-measured)
- [4. Which cues may vote — the speech rule](#4-which-cues-may-vote--the-speech-rule)
- [5. The fit](#5-the-fit)
- [6. When a correction counts as proven](#6-when-a-correction-counts-as-proven)
- [7. Gap and order rules between cues](#7-gap-and-order-rules-between-cues)
- [8. A constant offset and a moving one](#8-a-constant-offset-and-a-moving-one)
- [9. The order of operations, and what the plugin database is told](#9-the-order-of-operations-and-what-the-plugin-database-is-told)
- [10. The reserved block 51–99](#10-the-reserved-block-5199)
- [11. One section, one switch](#11-one-section-one-switch)
- [12. The worker and its six statuses](#12-the-worker-and-its-six-statuses)
- [13. Where it refuses](#13-where-it-refuses)
- [14. What it cannot do](#14-what-it-cannot-do)
- [15. The checks](#15-the-checks)

## 1. What it is

A downloaded subtitle is moved onto the audio track it belongs to (F-M296). Two halves must not be
confused, because one is a choice about **reading** and the other is a change to the **file**:

- **which** audio track is read — decided by language, never by stream order (§2);
- **whether** the file is then moved — decided by a fit over the audio, and only written when the
  move is statistically proven (§5, §6).

Every number the fit produces is a **measurement of the audio**, never of another subtitle: a
reference subtitle that itself drifts would make the result look better than it is (F-M305).

## 2. Which track the audio comes from

**The track is chosen by LANGUAGE, not by stream order** (F-M297). Priority:

1. a track whose language equals the subtitle's language,
2. an English track,
3. the first track without a language tag,
4. otherwise `FallbackPosition` — the first audio stream (`0:a:0`).

The implementation carries an **explicit ISO map**: a subtitle name holds two letters (`en`, `de`), a
container tag holds three (`eng`, `deu`), and comparing the two sets directly never matches. A zero
result from such a comparison is a defect in the comparison, not a finding — it is root-caused, never
reported (F-M297).

## 3. What is measured

The audio is decoded to a **mono signal at `SampleRate = 16 000` Hz, band-passed 300–3400 Hz**, and cut
into frames of `FrameSec = 0.020` s (F-M307). Per frame the level is `20·log10(sqrt(mean(v²)))`; the
stream is read in chunks and the samples are never held in memory.

The levels are clipped at the file's **10th and 90th percentiles**, so one loud effect or one silent
stretch cannot set the scale. A cue at a candidate shift scores the mean level over its shifted window:
**off audio → score 0 and the cue is excluded** (F-M307).

## 4. Which cues may vote — the speech rule

**Only dialogue lines are fitted** (F-M360). A cue that describes sound is still **moved** by its
segment's shift — it belongs to the same time frame as the dialogue around it — but it **may not vote**
on where a step goes, and it may not inflate the segment charge `Z·σ·√(cues)`:

- a line containing **`♪` or `♫`** is music, not speech;
- a cue that is **nothing but bracket groups** — `[door slams]`, `{phone rings}`, `(crowd shouting)` —
  is not speech, **in lower case too**, which is how embedded SDH writes them (F-M361);
- a **bare capitalised sound word** (`GUNSHOT`) — all capitals, no lower-case letter at all — is not
  speech.

**A letter outside the groups means dialogue, and the cue stays speech.** `(SCOFFS) Okay, so now
what?` and `[Shirley] Hey!` are dialogue; a rule that discards everything with a leading bracket throws
such lines away.

Implemented once in `DriftGate.IsSpeechCue`, weight `1.0` for speech and `0.0` for everything else; the
weight travels with the cue through `ParseCuesWeighted`.

## 5. The fit

The offset is a **piecewise-constant function of time**, fitted by exact dynamic programming over
`BlockCues = 40` cues (F-M307). The constants, and what each one is for:

- `MaxOffsetSec = 20.0` — the search range of a single offset, both directions.
- `StateSec = 0.25` — the step of the offset state space inside the DP.
- `SlackSec = 0.30` — the tolerance inside the score.
- `Z = 1.0` — the charge per segment: `Z·σ·√(cues)`. It shapes the segments; it does not decide
  whether anything is written.
- `MinSegFrac = 0.12` — a segment must cover at least this fraction of the file.
- `MinSegFloor = 40` and `MinFitCues = 40` — a segment, and the file, must carry at least this many
  cues; below that there is nothing to fit.
- `RiseWindowFrames = 10`, `VetoLevelGain = 0.10`, `VetoOnsetFloor = 0.01` — the veto that removes a
  candidate offset whose rise is not a speech onset.
- `EdgeMarginSec = 2.0` — the margin kept at the file's edges.

The detector that decides whether an offset **moves** works on the same frame levels: offset grid
`−8.0 … +18.0` s in steps of `0.1` s, `Eps = 0.30`, `LamStart = 0.25`, `LamEnd = 0.60`,
`WEnd = 0.55`, `BayesFactorThreshold = 3.0`, `MinStepSec = 0.8`, `MinBoundaryGapSec = 120.0` (two
boundaries are never closer than two minutes).

## 6. When a correction counts as proven

Three conditions, and **all three** must hold before a byte is written (F-M307, F-M320):

1. **Significance.** The moved cues must gain over `DeployZ = 2.0` standard errors — a **paired test
   over the MOVED cues only**. A fit that improves nothing has a gain of zero and fails here; the
   cause is recorded as `NotSignificant`.
2. **Not worse.** The file as a whole must not score worse after the correction.
3. **The floor.** The largest applied shift must be at least `MinShiftSec = 0.7` s. Below that the
   measurement floor of this material and a small real offset are the same number, and the feature
   would move files that were already in sync.

`DeployZ` is a **separate constant from `Z`**: `Z` is the charge inside the fit and shapes the
segments, `DeployZ` only decides whether a result is written. Raising one must not silently move the
other.

A refusal that carries no proven gain is **not** a failure: the file as downloaded is the best version
of that language (F-M321). Its own refusal text lists the cause — `below floor`, `not significant`,
`significantly worse, untreated is better` — and never a value the revert has already zeroed.

## 7. Gap and order rules between cues

The correction may not push cues through each other, and it may not close a gap that a player would
render as stacked text.

- **Order guard against the previous cue's END** (F-M296). The staircase **adds** its shift, so the
  bound is a **lower** one: `eff[k] ≥ eff[k−1] − gap + MinGapSec` with `MinGapSec = 0.04` s
  (`SubtitleSync.MinGapSec`).
- **A gap that is already below the floor must not be shrunk further.** Then the previous cue's shift
  is carried forward unchanged (`eff[k] = eff[k−1]`). Demanding the floor everywhere would make the
  guard fire on nearly every cue and flatten the staircase into one constant shift — which looks
  correct in the step heights and is not.
- **Never below zero.** A shift that would push a cue below zero is applied as measured and the **cue
  is clamped to 0**; the fit decides the offset, the clamp only stops a cue running off the front
  (F-M296).
- **Text is not touched.** Times only, text byte-for-byte, cue count and cue order unchanged.
- **Speech islands and gap floor.** The gate that finds speech on the audio treats a run of speech as
  an island only if it lasts at least `MinIslandSec = 0.15` s and starts at least
  `MinGapSec = 0.25` s after the previous island's end (`DriftGate`). Two different constants, two
  different questions: `0.04` s guards the cues in the file, `0.25`/`0.15` s finds speech in the
  audio.

## 8. A constant offset and a moving one

- **Constant** — the correction is **minus** the measured value: `srt_time − measured = synced`. The
  sign is measured, not assumed; a plus instead of a minus doubles the error instead of removing it.
- **Moving** — the offset is repaired **segment by segment**: each cue is shifted by the offset of its
  own segment, and the boundaries are the ones the drift gate already found (F-M300). A staircase has
  one parameter per segment and therefore fits anything; it is not trusted because it fits, it is
  trusted because a shuffled ordering of the same cues fits far worse.
- **One correction for a moving offset.** There is no second route: the audio is the only reference,
  and the segments are the repair (F-M300).
- **A boundary is measured, never computed.** The steps of a re-cut do not sit on a fixed grid; a grid
  assumption would put a step where no cut is.

## 9. The order of operations, and what the plugin database is told

1. fetch the candidate bytes,
2. **sync** — measure, and move if the move is proven,
3. **normalize** (idempotent, F-M185) — the corrected file is written canonical (UTF-8, no BOM, LF),
   so written bytes and hashed bytes are identical by construction,
4. **write two files** — the corrected `<base>.<lang>.srt` **and** the untouched original as the
   one-entry archive `<base>.<lang>.srt.unsynced.zip` (F-M306),
5. **register the hash of the CORRECTED subtitle.**

The hash describes the file that lies on disk and later goes up, so the duplicate guards keep seeing
the truth: **a correction must never lift a duplicate guard** (F-M296).

**Why the name sits AFTER `.srt`.** The loose-subtitle listing matches `<base>*.srt` (F-M251); a name
ending in `.srt.unsynced.zip` is ignored by it, while the swapped `<base>.<lang>.unsynced.srt` would be
read as a subtitle whose language is `unsynced`.

## 10. The reserved block 51–99

Slots **51–99 are reserved for kept originals**, corrected files stop at 49 (F-M315). The slot encodes
the kind (F-M317):

- **aligned** — a correction was applied → `01` upward;
- **unprocessed** — the fit found the file wrong (shift beyond the limit, first cue negative, too few
  cues) → the reserved range `51` upward;
- **`no proven gain`** is neither: `01` **and** a byte-identical copy at `51` (F-M321).

The slot is always written, always two digits, and it is the track order (F-M316). Originals in the
reserved block are **locked against upload** (F-M452): a version that passed significance closes a
language pair, a rejected original does not.

## 11. One section, one switch

The correction sections are **one** section and they carry **one** switch, the automatic sync check,
governing **every route that moves the file** (F-M303, F-M304). The page offers no switch for
something that is simply right; the download tab reads in the order the cycle works — continue after
daily limit, follow-up rounds, then Auto-Sync (F-M329).

## 12. The worker and its six statuses

The auto-sync is a **worker of its own**: it is handed the subtitle and hands back a status and its
output; the download run keeps writing the files and counting (F-M323). Six per-file statuses, not the
per-run vocabulary of F-M268:

- `ok` — moved;
- `already-good` — measured and declined: `no proven gain`, so the file as downloaded is the best
  version of that language (F-M321). The walk ends here;
- `disabled` — the switch is off;
- `failed` — the worker was **unable to measure** (as opposed to measuring and declining);
- `refused-file` — a verdict on **this file**: another candidate may well align, so the hunt stays
  alive (a shift beyond the limit, too few cues);
- `refused-audio` — a verdict on the **audio or the tooling**: every candidate would be refused
  identically, so hunting on would spend quota for the same answer.

The split is made by the worker, not re-derived by the caller from the reason text
(`RefusalMeansAlreadyGood`, `RefusalIsCandidateSpecific`, `IsMeasurementFailure`).

Its status is its own row in the Workers list — `Auto-Sync` (`AutoSyncWorker.Name`) — and it reports
only its own result; no row borrows a neighbour's sentence (F-M322, F-M328). The statistics count
**only alignments that really happened**, and every number in an Auto-Sync log line is written
culture-invariantly, separated by a point (F-M339).

## 13. Where it refuses

Each refusal names its cause, and a refusal may not replace a fitted value:

- **largest shift below `MinShiftSec`** ⇒ `already-good` — `no proven gain (below floor)`, the file
  stays as it is (F-M321);
- **a shift beyond `MaxShiftSec`, too few cues** ⇒ `refused-file`; the verdict is about this file;
- **no ffmpeg, unreadable audio, no speech, an empty frame set** ⇒ `refused-audio`; every candidate
  would be refused identically;
- **drift reported without segment offsets** ⇒ nothing is written; the segment list is what the
  staircase needs;
- **a cue below zero** ⇒ **not** a refusal: the shift is applied and the cue clamped (F-M296).

## 14. What it cannot do

- **It cannot tell a good correction from a bad one without the audio.** No reference-free signal
  separates them: every candidate computed from the run itself overlaps between the files it helps and
  the files it spoils, so no gate is built on them (F-M305, F-M300).
- **It cannot repair a boundary it placed wrongly.** A step placed through a region that was already
  correct moves that region off the audio; the result is therefore read **per segment**, never as a
  file-wide total.
- **It cannot use a subtitle as a yardstick.** That makes the measurement circular.
- **It cannot work without a decoder.** No ffmpeg is a refusal, not an estimate.

## 15. The checks

What pins the rules above, by suite:

- `fit`, `speechfit`, `aifilter` — the fit and the speech rule (§5, §4);
- `staircase` — the segment repair (§8);
- `framerate` — a re-cut is not a framerate error;
- `autosync` — the worker and its statuses (§12);
- `culture` — the culture-invariant numbers (§12).

And the tests that state a single rule in one line: **T111** the sign, **T114** the staircase,
**T121(a)** off-audio cues excluded, **T131** the repetition with the QA retry limit, **T148** the
Auto-Sync row, **T149** the numbers with a point, **T199** the dry run against the queue.
