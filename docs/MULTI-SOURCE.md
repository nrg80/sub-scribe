# Multi-Source Architecture — SubDL and the tvst family

As of 20.11.2026, plugin 12.2.0.14. This sheet pulls together what is
spread across many numbers in [`REQUIREMENTS.md`](REQUIREMENTS.md): how the sources
relate to one another, what „done" means, and how download and upload behave
with several sources. It is the one place where the source question can be read as a whole.

## 1. Two sources, not two truths

There are **two** upload sources:

- **SubDL** — API, returns identifiers, knows single and season uploads.
- **the tvst family** — `tvsubtitles.net` (series) and `moviesubtitles.org` (movies), the same
  operator, no account, no identifier in the answer. The family is *internally* split in two; which
  address a medium gets is decided by **TMDb/the plugin database** (series → tvsubtitles, movie →
  moviesubtitles), never the title and never the file name.

The order setting applies to the **family**, not to the individual address: „tvst first"
means moviesubtitles first for a movie, tvsubtitles first for a series. And the interface has
**one switch per direction** — the two address fields are data fields, not controls
(F-M482, F-M496, F-M504).

## 2. The principle: what is done at one source is open at the other

This is the rule from which everything else follows, and it has been broken several times:

| Fact | Rule | Number |
|---|---|---|
| Uploaded | its own mark per source (`ScribeUploadId`, `TvSubtitlesUploadId`) — „uploaded somewhere" is no evidence | F-M488, F-M489 |
| Duplicate | applies only to **the same** source: what SubDL has still goes to tvst | F-M582 |
| Rejection | locks only the source that pronounced it | F-M584 |
| Season pack | a pack completes the pair only at **its own** source; the season is done only when **every** source of the slot has its own pack | **F-M654**, F-M571, F-M573 |
| Status | names open languages per source, not as a sum | F-M570 |
| Origin | a find is never uploaded — neither back to its own source nor to the other | F-M487 |

**The pack case has been the most expensive one.** Up to 12.2.0.13 the plan read the source-blind sum
`PacksBySeason()`; a SubDL pack ended the season for tvsubtitles as well. Measured on 20.11.2026:
**36 seasons with 177 pairs** ended early with „already up as a pack", and tvsubtitles had for
those seasons **0** packs. Since 12.2.0.14 the plan forms the **intersection** over the sources of the
slot (`PacksOfAllSlotSources`), and the season line names the sources:
`all N known pair(s) already up as a pack for subdl, tvsubtitles`.

The pack key carries the source with it (`<imdb>|S<n>|<lang>|<hi|plain>|<source>`), each source keeps
its own identifier (F-M642). tvst containers carry **no** identifier, because the site returns
none (F-M612) — the evidence there is the line itself.

## 3. Upload: one slot per pair, two sources at the same time

A **pair** is (series, season, language, HI variant). Up to 12.2.0.8 two separate
pack phases ran — first SubDL, then tvst —, and the members were collected twice and
judged twice. Since 12.2.0.9 the following applies (F-M647):

1. **Collect once, judge once.** `FillAsync` builds from the members *one* plan with both
   containers and both file name forms.
2. **Send per source, at the same time.** `SendAsync` runs per source, started via `Task.WhenAll`,
   the second source with an **offset of 0–5 %** of the transfer rate (at 400/h up to 0,45 s) instead of a simultaneous start
   (F-M587). Measured in operation: 270–429 ms offset.
3. **A failure of one source does not drag the other down** — the result is returned, not
   thrown; per source its own success mark and its own timestamp.
4. **One line per pair** (F-M648, F-M651), with both sides: `"subdl: already up as a pack; tvst
   sent"`. Two wordings for the same finding are a mistake, not a more precise statement.
5. **Do not extract anything that no source wants any more** (F-M650): if a rejection with the same
   season signature is in the plugin database, the pair ends before collecting and unpacking.
6. **A quota stop is no verdict** (F-M649): nothing is saved, the pair stays open.
7. **Source control stays mandatory.** Whoever asks „already packed" asks per source; whoever
   writes a line names the source **explicitly** — `RecordPack(..., source)` has no
   usable default value for new calls, otherwise a forgotten argument silently writes
   `subdl`.

For single uploads the same pattern applies: one transfer, two sources, one offset (F-M587); the
checks per source (switch, access data, gate) run **before** sending, in chain order.

## 4. Download: one after another, in order — the opposite of upload

**The download asks the sources ONE AFTER ANOTHER, not at the same time.** The configured order is
data, not a special case: `DownloadPipeline` runs `foreach (var quelle in quellenReihenfolge)`, and
within a source it searches before the next one is asked. There is **no**
`Task.WhenAll` and no `Parallel.ForEach` there — unlike upload, where the two send paths
run at the same time. Whoever expects „parallel download" does not find it today; the order is
the only control.

Why this is so and what depends on it:

- The order determines the **inquiry order**, not only the rank of the hits. A source
  with a switched-off switch is not searched at all (F-M477, F-M495).
- A source that has already stayed silent in this run is **not asked again** (F-M592):
  otherwise every further item costs the 90 s timeout with no prospect of an answer.
- If the main search (SubDL) hits the timeout, the next source is asked instead of ending
  the run (F-M591).
- The result per source is **merged**, not replaced: the ranking begins with the
  source order, then pack and score.
- The candidates are **merged**: tvst no longer displaces SubDL, the ranking begins
  with the source order, then pack and score.
- **A rejection from one source does not end the search of the other** — otherwise the second
  source would never get its turn.
- An early exit after „first source empty" is the classic mistake: then the log says
  `no candidates on SubDL for [DE]`, although tvst was never asked. The evidence is the per-source line
  (`[tvsubtitles-D] … — N candidate(s)`), not the summary line.
- The header of a file decides the media kind **from the plugin database** (TMDb-corrected), not from the
  item type; only the file header of the upload run takes the item type, because nothing is resolved there yet.

**Open, not built:** whether the download should ask the sources at the same time. That would be a change
to the inquiry order and to the timeout behaviour (a silent source must not
slow the other down), not a description of the current state — and it is not decided.

## 5. What this means in operation

One run, two stages — the pack phase first, then the single phase (F-M429):

- **Pack phase**: complete seasons, one zip per (language, variant), to **every** source of the slot.
- **Single phase**: everything else, one verdict per file and language, again per source.
- An arrival run does not pack; the library belongs to the full run (F-M424). tvst is in the slot only
  when the full run is running **and** its upload switch is on (F-M495).

Measurements from 20.11.2026 as the benchmark: 1.331 video files, 113 complete seasons, 811 pairs in the
file system, 540 suitable for tvst — of those 397 in seasons that were already fully packed,
25 sent, 143 open (incomplete seasons or mixed releases). Of the 25, all went to
tvst, none to SubDL (SubDL rejected every container as `Duplicate upload: identical file already
published`).

## 6. Checks that hold this architecture in place

| Check | File |
|---|---|
| Source separation in the plan, intersection instead of sum | `scripts/tests/seasonpack/SeasonPackRun.cs` (10b) |
| „already packed" per source, source list in the line | `scripts/tests/packretry/PackRetryRun.cs` |
| one slot per pair, both sources, one line | `scripts/tests/seasonpack/SeasonPackRun.cs` (10) |
| mark per source, no reup, no cross block | `scripts/tests/endpoints/EndpointsRun.cs` |
| Comment and fields of the tvst payload | `scripts/tests/tvsubtitles/` |
