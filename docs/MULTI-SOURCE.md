# Multi-source

Two upload sources work on the same pair, and a pair is (series, season, language, HI variant); how a pair is allocated to a slot stands in §8 of `CONTRACTS.md`. SubDL answers over an API, returns identifiers and knows single and season uploads. The tvst family is `tvsubtitles.net` for series and `moviesubtitles.org` for movies: one operator, no account, no identifier in the answer.

## The family and the order

The family is split in two, and TMDb with the plugin database decides which address a medium gets: series to tvsubtitles, movie to moviesubtitles, never the title and never the file name. The order setting applies to the family, not to the address, so "tvst first" means moviesubtitles first for a movie and tvsubtitles first for a series. One switch per direction controls it; the two address fields are data fields, not controls.

## What one source settles, the other leaves open

Each source keeps its own upload mark (`ScribeUploadId`, `TvSubtitlesUploadId`), so "uploaded somewhere" is no evidence. The duplicate check applies to the same source only: what SubDL already holds still goes to tvst. A rejection locks only the source that pronounced it, and a find is never uploaded, neither back to its own source nor to the other. The status names the open languages per source, not as a sum. A pack completes the pair only at its own source, so the season is done only when every source of the slot holds its own pack.

## The pack key carries its source

A source-blind sum once closed the season for both sources although the second held no pack, so the plan forms the intersection over the sources of the slot. The season line names them: `all N known pair(s) already up as a pack for subdl, tvsubtitles`. The pack key carries the source with it (`<imdb>|S<n>|<lang>|<hi|plain>|<source>`), and each source keeps its own identifier. tvst containers carry none, because the site returns none, and the evidence there is the line itself.

## Pack upload: collect once, send twice

Up to 12.2.0.8 two pack phases ran, first SubDL then tvst, and the members were collected twice and judged twice. Since 12.2.0.9 `FillAsync` builds one plan from the members, with both containers and both file name forms. `SendAsync` runs per source under `Task.WhenAll`: both start together, the second source with an offset of 0–5 % of the transfer rate, at 400 per hour up to 0,45 s. A failure of one source returns its result instead of throwing, so it does not drag the other down: each side keeps its own success mark and timestamp. One line per pair names both sides, `subdl: already up as a pack; tvst sent`, because two wordings for the same finding are a mistake.

## What the pair refuses before it starts

A stored rejection with the same season signature ends the pair before collecting and unpacking. A quota stop is no verdict: nothing is saved and the pair stays open. `RecordPack(..., source)` has no usable default for new calls, because a forgotten argument would silently write `subdl`; whoever asks "already packed" asks per source. A single upload follows the same pattern: one transfer, two sources, one offset, and the checks per source — switch, access data, gate — run before sending in chain order.

## Download: one source after the other

The download asks the sources one after another. `DownloadPipeline` runs `foreach (var quelle in quellenReihenfolge)` and searches a source before the next one is asked; there is no `Task.WhenAll` and no `Parallel.ForEach` here. The order is the only control, and it decides the inquiry order, not just the rank of the hits: a source with a switched-off switch is not searched at all.

## A silent source is not asked again

A source that stayed silent earlier in the run is not asked again, because every further item would cost the 90 s timeout with no prospect of an answer. When the main search hits the timeout, the next source is asked instead of ending the run. The results and the candidates per source are merged, not replaced: tvst displaces no SubDL hit, the ranking begins with the source order, then pack and score. A rejection from one source does not end the search of the other. An early exit after "first source empty" is the classic mistake: the log then says `no candidates on SubDL for [DE]` although tvst was never asked, and the evidence is the per-source line `[tvsubtitles-D] … — N candidate(s)`, not the summary line.

## What decides the media kind

The header of a file decides the media kind from the plugin database, TMDb-corrected, not from the item type. Only the header of the upload run takes the item type, because nothing is resolved there yet.

## What is still open

Whether the download should ask the sources at the same time is not decided. It would change the inquiry order and the timeout behaviour, because a silent source must not slow the other one down.

## One run, two stages

A run packs first and sends singles afterwards. The pack phase takes complete seasons, one ZIP per language and variant, to every source of the slot; the single phase takes everything else, one verdict per file and language, again per source. An arrival run does not pack, because the library belongs to the full run. tvst sits in the slot only while the full run runs and its upload switch is on.

## What the measurement says

1.331 video files and 113 complete seasons held 811 pairs, 540 of them suitable for tvst and 397 of those in seasons that were already fully packed. 25 went out, all to tvst and none to SubDL, which rejected every container as `Duplicate upload: identical file already published`. The remaining 143 stayed open, because their seasons were incomplete or carried mixed releases.

## Checks that hold this

| Check | File |
|---|---|
| source separation in the plan, intersection instead of sum | `scripts/tests/seasonpack/SeasonPackRun.cs` (10b) |
| "already packed" per source, source list in the line | `scripts/tests/packretry/PackRetryRun.cs` |
| one slot per pair, both sources, one line | `scripts/tests/seasonpack/SeasonPackRun.cs` (10) |
| mark per source, no re-up, no cross block | `scripts/tests/endpoints/EndpointsRun.cs` |
| comment and fields of the tvst payload | `scripts/tests/tvsubtitles/` |

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M482, F-M496, F-M504, F-M488, F-M489, F-M582, F-M584, F-M654, F-M571, F-M573, F-M570, F-M487, F-M642, F-M612, F-M647, F-M587, F-M648, F-M651, F-M650, F-M649, F-M477, F-M495, F-M592, F-M591, F-M429 and F-M424. Guarded by `endpoints`, `seasonpack` and `packretry`.
