# Auto-Sync — moving a subtitle onto its audio

A downloaded subtitle is moved onto the audio track it belongs to (F-M296). Two halves stay apart: **which** track is read is decided by language and never by stream order, and **whether** the file is moved is decided by a fit over the audio and written only when the move is proven. Every number the fit produces measures the audio, never another subtitle, because a reference subtitle that itself drifts would make the result look better than it is (F-M305). The words are those of `TERMS.md`, and §4.3 of `SPECIFICATION.md` states the rules normatively.

## The track comes from language

The track is chosen by language, not by stream order (F-M297): a track whose language equals the subtitle's language, an English track, the first track without a language tag, otherwise `FallbackPosition`, the first audio stream (`0:a:0`). An explicit ISO map bridges the two spellings, because a subtitle name holds two letters (`en`, `de`) and a container tag holds three (`eng`, `deu`), and comparing the two sets directly never matches. A zero result from that comparison is a defect in the comparison, not a finding, so it is root-caused and never reported (F-M297).

## What is measured

The audio is decoded to a mono signal at `SampleRate = 16 000` Hz, band-passed 300–3400 Hz and cut into frames of `FrameSec = 0.020` s (F-M307). The level of a frame is `20·log10(sqrt(mean(v²)))`, the stream is read in chunks and the samples are never held in memory. The levels are clipped at the 10th and 90th percentiles of the file, so one loud effect or one silent stretch cannot set the scale. A cue at a candidate shift scores the mean level over its shifted window, and a cue that lands off the audio scores 0 and is excluded (F-M307).

## The speech rule

Only dialogue lines are fitted (F-M360). A cue that describes sound is still moved by its segment's shift, because it belongs to the same time frame as the dialogue around it, but it may not vote on where a step goes and may not inflate the segment charge `Z·σ·√(cues)`. Three shapes are not speech. A line containing `♪` or `♫` is music and not speech. A cue of nothing but bracket groups is not speech either, such as `[door slams]`, `{phone rings}` or `(crowd shouting)`, in lower case too, which is how embedded SDH writes them (F-M361). A bare capitalised sound word such as `GUNSHOT`, all capitals and no lower-case letter at all, is not speech. A letter outside the groups means dialogue and the cue stays speech: `(SCOFFS) Okay, so now what?` and `[Shirley] Hey!` are dialogue, and a rule that discards everything with a leading bracket throws such lines away. The gate is `DriftGate.IsSpeechCue`, weighted `1.0` for speech and `0.0` for everything else, and `ParseCuesWeighted` carries the weight with the cue.

## The fit

The offset is a piecewise-constant function of time, fitted by exact dynamic programming over `BlockCues = 40` cues (F-M307). A second detector on the same frame levels decides whether an offset moves.

| Constant | What it is for |
| --- | --- |
| `MaxOffsetSec = 20.0` | the search range of a single offset, both directions |
| `StateSec = 0.25` | the step of the offset state space inside the DP |
| `SlackSec = 0.30` | the tolerance inside the score |
| `Z = 1.0` | the charge per segment, `Z·σ·√(cues)`; it shapes the segments and does not decide whether anything is written |
| `MinSegFrac = 0.12` | a segment must cover at least this fraction of the file |
| `MinSegFloor = 40`, `MinFitCues = 40` | a segment, and the file, must carry at least this many cues; below that there is nothing to fit |
| `RiseWindowFrames = 10`, `VetoLevelGain = 0.10`, `VetoOnsetFloor = 0.01` | the veto that removes a candidate offset whose rise is not a speech onset |
| `EdgeMarginSec = 2.0` | the margin kept at the file's edges |
| offset grid `−8.0 … +18.0` s, step 0.1 s | the search of the detector that decides whether an offset moves, with `Eps = 0.30`, `LamStart = 0.25`, `LamEnd = 0.60`, `WEnd = 0.55`, `BayesFactorThreshold = 3.0`, `MinStepSec = 0.8` and `MinBoundaryGapSec = 120.0`; two boundaries are never closer than two minutes |

## When a correction counts as proven

All three conditions must hold before a byte is written (F-M307, F-M320). The moved cues must gain over `DeployZ = 2.0` standard errors in a paired test over the moved cues only. A fit that improves nothing has a gain of zero and is recorded as `NotSignificant`. The file as a whole must not score worse after the correction. And the largest applied shift must be at least `MinShiftSec = 0.7` s, because below that the measurement floor of this material and a small real offset are the same number. `DeployZ` is a constant of its own: `Z` is the charge inside the fit and shapes the segments, `DeployZ` only decides whether a result is written, and raising one must not move the other. A refusal that carries no proven gain is not a failure, because the file as downloaded is the best version of that language (F-M321); its text names the cause — `below floor`, `not significant`, `significantly worse, untreated is better` — and never a value that the revert already zeroed.

## Gap and order rules between cues

The correction may not push cues through each other and may not close a gap that a player would render as stacked text. The guard runs against the previous cue's end (F-M296), and because the staircase adds its shift the bound is a lower one: `eff[k] ≥ eff[k−1] − gap + MinGapSec` with `MinGapSec = 0.04` s (`SubtitleSync.MinGapSec`). A gap already below the floor must not shrink further, so the previous cue's shift is carried forward unchanged (`eff[k] = eff[k−1]`), because demanding the floor everywhere would fire on nearly every cue and flatten the staircase into one constant shift. A shift that would push a cue below zero is applied as measured and the cue is clamped to 0 (F-M296). Times move, text stays byte-for-byte, and cue count and cue order stay as they were. The gate that finds speech on the audio calls a run of speech an island only if it lasts at least `MinIslandSec = 0.15` s and starts at least `MinGapSec = 0.25` s after the previous island's end (`DriftGate`). Those are two constants for two questions: 0.04 s guards the cues in the file, and 0.25 and 0.15 s find speech in the audio.

## A constant offset and a moving one

A constant correction is minus the measured value (`srt_time − measured = synced`), and the sign is measured rather than assumed, because a plus instead of a minus doubles the error instead of removing it. A moving offset is repaired segment by segment: each cue takes the offset of its own segment, and the boundaries are the ones the drift gate already found (F-M300). A staircase has one parameter per segment and therefore fits anything; it is trusted not because it fits but because a shuffled ordering of the same cues fits far worse. There is no second route for a moving offset, because the audio is the only reference and the segments are the repair (F-M300). A boundary is measured, never computed, because the steps of a re-cut do not sit on a fixed grid.

## The order of operations and the database

A run fetches the candidate bytes, syncs them, normalizes them, writes two files and registers one hash. Normalizing is idempotent (F-M185) and writes the corrected file canonical — UTF-8, no BOM, LF — so written bytes and hashed bytes are identical by construction. The two files are the corrected `<base>.<lang>.srt` and the untouched original as the one-entry archive `<base>.<lang>.srt.unsynced.zip` (F-M306). The registered hash belongs to the corrected subtitle, because it describes the file on disk that later goes up, and a correction must never lift a duplicate guard (F-M296). The name sits after `.srt` because the loose-subtitle listing matches `<base>*.srt` (F-M251): `.srt.unsynced.zip` is ignored by it, while a swapped `<base>.<lang>.unsynced.srt` would be read as a subtitle whose language is `unsynced`.

## The reserved block 51–99

Slots 51–99 are reserved for kept originals and corrected files stop at 49 (F-M315). The slot encodes the kind (F-M317): `01` upward for an aligned file, `51` upward for an unprocessed one, which is a shift beyond the limit, a first cue below zero or too few cues. `no proven gain` is neither, so it keeps `01` and a byte-identical copy at `51` (F-M321). The slot is always written, always two digits, and it is the track order (F-M316). Originals in the reserved block are locked against upload (F-M452): a version that passed significance closes a language pair, a rejected original does not.

## One section, one switch

The correction sections are one section with one switch, the automatic sync check, and it governs every route that moves the file (F-M303, F-M304). The page offers no switch for something that is simply right, and the download tab reads in the order the cycle works: continue after daily limit, follow-up rounds, then Auto-Sync (F-M329).

## The worker and its six statuses

The auto-sync is a worker of its own: it is handed the subtitle and hands back a status and its output, while the download run keeps writing the files and counting (F-M323). Six per-file statuses replace the per-run vocabulary of F-M268. `ok` means moved. `already-good` means measured and declined, so the file as downloaded is the best version of that language and the walk ends here (F-M321). `disabled` means the switch is off, and `failed` means the worker was unable to measure. `refused-file` is a verdict on this file, so another candidate may align and the hunt stays alive. `refused-audio` is a verdict on the audio or the tooling, so every candidate would be refused identically. The worker makes that split itself through `RefusalMeansAlreadyGood`, `RefusalIsCandidateSpecific` and `IsMeasurementFailure`, and the caller never re-derives it from the reason text. Its status is its own row in the Workers list, `Auto-Sync` (`AutoSyncWorker.Name`), and it reports only its own result (F-M322, F-M328). The statistics count only alignments that really happened, and every number in an Auto-Sync log line is written culture-invariantly with a point (F-M339).

## Where it refuses

Each refusal names its cause, and no refusal replaces a fitted value. A largest shift below `MinShiftSec` gives `already-good` with `no proven gain (below floor)`, and the file stays as it is (F-M321). A shift beyond `MaxShiftSec` or too few cues gives `refused-file`. No ffmpeg, unreadable audio, no speech or an empty frame set gives `refused-audio`. Drift reported without segment offsets writes nothing, because the segment list is what the staircase needs. A cue below zero is not a refusal: the shift is applied and the cue clamped (F-M296).

## What it cannot do

Nothing without the audio separates a good correction from a bad one, because every candidate computed from the run itself overlaps between the files it helps and the files it spoils (F-M305, F-M300). It cannot repair a boundary it placed wrongly, because a step through a region that was already correct moves that region off the audio, so the result is read per segment and never as a file-wide total. It cannot use a subtitle as a yardstick, because that makes the measurement circular, and it cannot work without a decoder: no ffmpeg is a refusal, not an estimate.

## Where the rules live

§4.3 of `SPECIFICATION.md` carries the normative text, with F-M185, F-M251, F-M296, F-M297, F-M300, F-M303, F-M304, F-M305, F-M306, F-M307, F-M315, F-M316, F-M317, F-M320, F-M321, F-M322, F-M323, F-M328, F-M329, F-M339, F-M360, F-M361, F-M452 and the per-run vocabulary of F-M268. Guarded by `fit`, `speechfit` and `aifilter` (the fit and the speech rule), `staircase` (the segment repair), `framerate` (a re-cut is not a framerate error), `autosync` (the worker) and `culture` (the invariant numbers). One rule in one line: T111 the sign, T114 the staircase, T121(a) off-audio cues excluded, T131 the repetition with the QA retry limit, T148 the Auto-Sync row, T149 the numbers with a point, T199 the dry run against the queue.
