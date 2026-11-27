# The contracts — where the boundary runs

A source answers through three contracts, and there is no fourth: a new task is assigned to one of the three or it is not built. The job contract carries four jobs, the account-ledger contract three tasks, the inquiry contract two tasks. Every source may fulfil one, two or all three; the job contract is mandatory and the other two are voluntary. Whoever does not fulfil one is absent from its list instead of returning an empty obligatory answer, and no contract presupposes another. Two upload sources work on the same pair, so this paper also says what one source settles, what the other leaves open, and how download and upload treat both.

## 1. Terms

The vocabulary is [TERMS.md](TERMS.md) — source, pipeline, plugin database, pack, handle, verdict, capability, light, inquiry — and it applies here too. Three words are used in this document only. The three contracts are the job contract, the account-ledger contract and the inquiry contract; a boundary rule is one of the seven in §6; an allocation assigns a question to exactly one contract in §8.

## 2. Scope

The job contract covers search, fetch, send and report. The account-ledger contract lists our own submissions, lists published submissions and withdraws one. The inquiry contract says how the source stands and how much quota is left today. The rules themselves live in `SPECIFICATION.md` — §22 for one contract per source, §§3 and 4 for the two directions, §10 for quota and timing — and this document states where the boundary runs.

## 3. The job contract — the work on one subtitle

| Job | Question | Answer |
| --- | --- | --- |
| Search | Which subtitles do you have for this title? | Candidates with an opaque handle, language, release name, pack membership |
| Fetch | Give me this subtitle. | Bytes, file name, foreign identifier, content hash |
| Send | Take this subtitle (single or as a pack). | accepted · rejected · busy · failed |
| Report | This entry is broken. | accepted · rejected · busy · failed |

The send job carries the title question with it: if the source lacks its own title record, the job may require it to look the title up on its site and create it. The answer reports what happened — ready, created, created without a readable identifier, already carries the title without an identifier, not findable, not askable, no verdict — and names the identifier if there is one. A dry run creates nothing and decides nothing, so a missing title there is "not askable" and not a no. As long as the title does not stand, the send job sends nothing, which is why the pipeline may repeat it without a second upload. The job contract knows no account: listing and withdrawing are not part of it, not even as a fifth job.

## 4. The account-ledger contract — the account's own record

Only sources with their own account fulfil it; a source without an account is absent from the list instead of answering "I cannot do that". A published submission carries two handles, one to withdraw it and one of its published entry, and the two number spaces differ; an open submission carries only the first. The state is coarser than any source: open, accepted, rejected, the last with the source's plain text alongside, and states only one side knows do not cross the boundary. Withdrawing has the outcome of sending, four outcomes and no second vocabulary. The contract answers no reachability and no quota question.

## 5. The inquiry contract — how the source stands

Every source fulfils it, including one without an account, because whether it is reachable is something it knows best itself. The source sets its own lights: key, label, health state and plain text come from it, the caller knows no source by name and only paints what comes back. Quota is read live, not cached, and the run tells a short-term throttle apart from the daily limit by it. "I keep no quota" is a valid answer: no invented numbers, no silence.

## 6. Shared boundary rules

1. No contract knows the plugin database: no database, no directory, none of our identifier shapes, no row of our storage.
2. What is handed in is handed in: network connection, archive cache, log and settings come from outside, and no engine opens its own network connection.
3. Only neutral data crosses the boundary: language in one uniform spelling, timestamps in UTC, the source's plain text and opaque handles, no addresses, no access data, no foreign identifier shape.
4. The source name is a data value, not a switch: it stands in the plugin database, in the origin line and on the tile, and nothing is decided by it.
5. Capabilities replace name comparisons: whether a source can upload, report, accept packs, create a title or keep a quota, its capabilities say, not its name.
6. A contract is a promise, not an obligation to be complete: whoever cannot do a task does not fulfil the contract and does not pretend it could.
7. Which language a source accepts, the source says, in both directions and with the information whether it states supported languages; "I keep no list" is its own answer and not an empty list, which would mean "I can do nothing".

## 7. Who writes to the plugin database

The pipeline writes, the source never: stored identifiers, marks, locks and counters belong to the pipeline. What a source returns is a proposal with evidence — an identifier, a state, a plain text — and only the pipeline decides whether and where it enters the plugin database. A lock is pipeline knowledge: a title stays closed after four failed attempts, for seven days, then open for exactly one probe, and the source never sees any of it. Each upload source keeps its own upload mark (`ScribeUploadId`, `TvSubtitlesUploadId`), so "uploaded somewhere" is no evidence.

## 8. Allocation: which question belongs where

| Question | Responsible | Never responsible |
| --- | --- | --- |
| Which subtitles do you have for this title? | Job contract (search) | Account ledger, inquiry answer |
| Take this subtitle | Job contract (send), including title creation | Account ledger |
| What else lies with my account? | Account ledger | Job contract |
| Withdraw this entry | Account ledger | Job contract |
| Are you reachable, which light applies? | Self-report | Job contract |
| How much is left today? | Self-report (live) | Account ledger, job contract |
| What is your name in one line? | Capabilities | all three contracts |
| What happened to the title? | Job contract (answer of the send) | Account ledger |
| How many pages has my account? | Account ledger (depth of the question) | Self-report |

## 9. Work that lives in no contract

Creating a title is no job of its own but a field of sending. Quota is not account bookkeeping but an inquiry answer. Diagnosis on the account pages through one's own submissions with the account ledger at a page depth of one, and invents no path of its own. Status lights use the inquiry answer and open no connection of their own to the source. A source's short name is a capability, not a condition in the pipeline. A site's language mapping is a data value of the source and no matter of fetching, while the one neutral spelling of the plugin database stays above the sources.

## 10. The family and the address

The tvst family is `tvsubtitles.net` for series and `moviesubtitles.org` for movies: one operator, no account, no identifier in the answer, while SubDL answers over an API and knows single and season uploads. TMDb with the plugin database decides which address a medium gets: series to tvsubtitles, movie to moviesubtitles, never the title and never the file name. The order setting applies to the family, not to the address, so "tvst first" means moviesubtitles first for a movie and tvsubtitles first for a series. One switch per direction controls it; the two address fields are data fields, not controls. The header of a file decides the media kind from the plugin database, TMDb-corrected, not from the item type; only the header of the upload run takes the item type, because nothing is resolved there yet.

## 11. What one source settles, the other leaves open

The duplicate check applies to the same source only: what SubDL already holds still goes to tvst. A rejection locks only the source that pronounced it, and a find is never uploaded, neither back to its own source nor to the other. The status names the open languages per source, not as a sum. A pack completes the pair only at its own source, so the season is done only when every source of the slot holds its own pack. A source-blind sum once closed the season for both sources although the second held no pack, so the plan forms the intersection over the sources of the slot. The season line names them: `all N known pair(s) already up as a pack for subdl, tvsubtitles`. The pack key carries the source with it (`<imdb>|S<n>|<lang>|<hi|plain>|<source>`), and each source keeps its own identifier; tvst containers carry none, because the site returns none, and the evidence there is the line itself.

## 12. Pack upload: collect once, send twice

Up to 12.2.0.8 two pack phases ran, first SubDL then tvst, and the members were collected twice and judged twice. Since 12.2.0.9 `FillAsync` builds one plan from the members, with both containers and both file name forms. `SendAsync` runs per source under `Task.WhenAll`: both start together, the second source with an offset of 0–5 % of the transfer rate, at 400 per hour up to 0,45 s. A failure of one source returns its result instead of throwing, so it does not drag the other down: each side keeps its own success mark and timestamp. One line per pair names both sides, `subdl: already up as a pack; tvst sent`, because two wordings for the same finding are a mistake.

## 13. What the pair refuses before it starts

A stored rejection with the same season signature ends the pair before collecting and unpacking. A quota stop is no verdict: nothing is saved and the pair stays open. `RecordPack(..., source)` has no usable default for new calls, because a forgotten argument would silently write `subdl`; whoever asks "already packed" asks per source. A single upload follows the same pattern: one transfer, two sources, one offset, and the checks per source — switch, access data, gate — run before sending in chain order.

## 14. Download: one source after the other

The download asks the sources one after another. `DownloadPipeline` runs `foreach (var quelle in quellenReihenfolge)` and searches a source before the next one is asked; there is no `Task.WhenAll` and no `Parallel.ForEach` here. The order is the only control, and it decides the inquiry order, not just the rank of the hits: a source with a switched-off switch is not searched at all. A source that stayed silent earlier in the run is not asked again, because every further item would cost the 90 s timeout with no prospect of an answer. When the main search hits the timeout, the next source is asked instead of ending the run. The results and the candidates per source are merged, not replaced: tvst displaces no SubDL hit, the ranking begins with the source order, then pack and score. A rejection from one source does not end the search of the other. An early exit after "first source empty" is the classic mistake: the log then says `no candidates on SubDL for [DE]` although tvst was never asked, and the evidence is the per-source line `[tvsubtitles-D] … — N candidate(s)`, not the summary line.

## 15. What is still open

Whether the download should ask the sources at the same time is not decided. It would change the inquiry order and the timeout behaviour, because a silent source must not slow the other one down.

## 16. One run, two stages

A run packs first and sends singles afterwards. The pack phase takes complete seasons, one ZIP per language and variant, to every source of the slot; the single phase takes everything else, one verdict per file and language, again per source. An arrival run does not pack, because the library belongs to the full run. tvst sits in the slot only while the full run runs and its upload switch is on.

## 17. What the measurement says

1.331 video files and 113 complete seasons held 811 pairs, 540 of them suitable for tvst and 397 of those in seasons that were already fully packed. 25 went out, all to tvst and none to SubDL, which rejected every container as `Duplicate upload: identical file already published`. The remaining 143 stayed open, because their seasons were incomplete or carried mixed releases.

## 18. Checks that hold the boundary

| Check | What it holds |
| --- | --- |
| F-M682 | no file of a source names the plugin database or the pipeline, no file of the pipeline names a source, every source is built explicitly and not looked up |
| T392 | the account-ledger contract carries exactly its three tasks, stands next to the four jobs, and only the source with an account fulfils it |
| T393 | the inquiry contract carries exactly its tasks and every source fulfils it; outside the source no file reads an access datum from the general configuration |
| T394 | the pipeline names no source: short name from the capabilities, lists from the account ledger, quota from the inquiry answer |
| T395 | the send job carries the title question; both sides that create titles do so without our plugin database and without a verdict in a dry run |
| T396 | every source yields its language answer, the two sites with supported languages and the third open without a list; the return path delivers the plugin database's spelling |
| `scripts/tests/endpoints/EndpointsRun.cs` | mark per source, no re-up, no cross block |
| `scripts/tests/seasonpack/SeasonPackRun.cs` (10b) | source separation in the plan, intersection instead of sum |
| `scripts/tests/seasonpack/SeasonPackRun.cs` (10) | one slot per pair, both sources, one line |
| `scripts/tests/packretry/PackRetryRun.cs` | "already packed" per source, source list in the line |
| `scripts/tests/tvsubtitles/` | comment and fields of the tvst payload |

## 19. Where the rules live

The rules live in [SPECIFICATION.md](SPECIFICATION.md), §22 with §§3, 4 and 10. The numbers are F-M238, F-M424, F-M429, F-M435, F-M477, F-M482, F-M487, F-M488, F-M489, F-M495, F-M496, F-M504, F-M508, F-M557, F-M570, F-M571, F-M573, F-M582, F-M584, F-M587, F-M591, F-M592, F-M608, F-M612, F-M642, F-M647, F-M648, F-M649, F-M650, F-M651, F-M654, F-M682, F-M685, F-M729, F-M730, F-M731, F-M733, F-M734, F-M735. F-M771 closes the list. Guarded by the checks above and by `endpoints`, `seasonpack` and `packretry`.
