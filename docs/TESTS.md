# Test Library — every requirement carries a test

*Derived document: the rule stands in §19 of `SPECIFICATION.md`: a functional requirement carries at
least one automated test, a unit test where possible and an integration test where the behaviour needs
one. This paper is the catalogue itself: one entry per test, grouped by the chapter of the
specification the entry proves. Every entry is verbatim, because its number is its identity and a
number is never reused.*

**Owner: the catalogue.** The tests and what each one proves; the rule that every requirement needs one
stands in §19 of `SPECIFICATION.md`, and the binding of a test to its suite in `scripts/tests/counts.json`.

## Contents

- [1. Tests of the seeder (§2)](#1-tests-of-the-seeder-2) — 5 tests
- [2. Tests of the upload pipeline (§3)](#2-tests-of-the-upload-pipeline-3) — 28 tests
- [3. Tests of the download pipeline (§4)](#3-tests-of-the-download-pipeline-4) — 35 tests
- [4. Tests of upload postprocessing (§5)](#4-tests-of-upload-postprocessing-5) — 7 tests
- [5. Tests of the database refresh (§6)](#5-tests-of-the-database-refresh-6) — 5 tests
- [6. Tests of the rules shared by both directions (§8)](#6-tests-of-the-rules-shared-by-both-directions-8) — 2 tests
- [7. Tests of the API, the identifiers and the credentials (§9)](#7-tests-of-the-api-the-identifiers-and-the-credentials-9) — 31 tests
- [8. Tests of the scheduler, the quota and the timing (§10)](#8-tests-of-the-scheduler-the-quota-and-the-timing-10) — 17 tests
- [9. Tests of the content registry and identity (§11)](#9-tests-of-the-content-registry-and-identity-11) — 11 tests
- [10. Tests of the library scope and the skip filters (§12)](#10-tests-of-the-library-scope-and-the-skip-filters-12) — 3 tests
- [11. Tests of the configuration and the settings page (§13)](#11-tests-of-the-configuration-and-the-settings-page-13) — 34 tests
- [12. Tests of the data model and persistence (§14)](#12-tests-of-the-data-model-and-persistence-14) — 75 tests
- [13. Tests of logging, status and transparency (§15)](#13-tests-of-logging-status-and-transparency-15) — 19 tests
- [14. Tests of the test discipline itself (§19)](#14-tests-of-the-test-discipline-itself-19) — 34 tests
- [15. Tests of the source contracts (§22)](#15-tests-of-the-source-contracts-22) — 12 tests
- [16. Tests without a named requirement](#16-tests-without-a-named-requirement) — 92 tests

**404 tests** in 16 chapters. The number is the identity: a test keeps its
number when it moves to another chapter, and a number is never reused.

## 1. Tests of the seeder (§2)

**T48:** An on-arrival run works exactly the items that arrived (F-M233)

**T61:** The pipelines execute the queue item's work order (F-M244)

**T76:** The scan records loose subtitles as observations (F-M259)

**T81:** A dry run suppresses the media container rewrite for both directions (F-M263)

**T88:** An unlabelled loose subtitle is detected and renamed, and a taken combination takes the next slot (F-M278)

## 2. Tests of the upload pipeline (§3)

**T56:** loose subtitle names resolve through one reader, never through the language mapper (F-M239)

**T68:** A bare episode marker makes the item a series (F-M249, part a)

**T69:** The bare-marker guard rejects release-group suffixes, and the date rule needs text after the date (F-M249, parts a and b)

**T70:** The parser's four new shapes hold, and the library diff is the acceptance (F-M249/F-M250)

**T82:** A media file is rewritten at most once (F-M264)

**T84:** The sweep removes observations but never a verdict (F-M266)

**T166:** A complete season is packable, no matter how much of it sits in the queue (F-M394).

**T167:** Filler material must be uploaded (F-M395).

**T168:** No second pack with the same members, and dead pack rows drop out (F-M396).

**T169:** An `.srt` of SubDL provenance kills the pack (F-M397).

**T170:** Only the `packs` row substantiates a pack (F-M399).

**T171:** The language row establishes the finding (F-M400).

**T172:** "run dry" and one spelling for the mode (F-M401–F-M403).

**T174:** Single upload first, then the season pack. And in the arrival no pack at all (F-M418, F-M420).

**T286:** The cheap header exit (F-M576, F-M88c): the exit stands **before** the stream fetch, asks the mark per source (`IsCompleteFor(earlyHash, quelle, null)`) and `NewestCompletionStamp` delivers the most recent stamp for the loose subtitle question. **1 check** in `endpoints` (245 → 246).

**T287:** The parking of an extraction failure (F-M577): the reason `extraction-failed` stands in the existing plugin database, and the park question (`ExtractRejectIsStale`) takes the track up again after seven days. The **existence check stands before** the writing of the rejection: a missing file is not parked. **2 checks** in `endpoints` (246 → 248).

**T290:** A stored verdict closes the pair (F-M580): `PairIsDecided` asks for a `duplicate-content` verdict on the embedded **or** the loose subtitle row of the pair. The pair question asks this question **before** the source gate, and a stored ruling does not count the counter again. **2 checks** in `endpoints` (250 → 252).

**T291:** The pair question checks only the evidence (F-M581): between `AnyUploadSourceStillWants` and the next member stands neither `QueuedForSource(` nor `SiteLanguage(`. `IrgendeineQuelleHat` exists as its own check piece, and the self-echo branch writes only with evidence: without evidence the line there reads "no source asks for this pair right now, no verdict written". **2 checks** in `endpoints` (252 → 254).

**T292:** "Duplicate" applies per source (F-M582): `SettleDuplicateContent` sends the candidate without evidence to the chain (`DueForAnotherSource`) as long as one source may take it, and `PairIsDecided` closes only on known origin. **2 checks** in `endpoints` (254 → 256).

**T294:** "Duplicate" applies per source in the pack builder (F-M584): `SpurIstGesperrt` asks the pair question per source on `duplicate-self-echo`, and the pack filler uses it instead of the source-neutral rejection. The header exit F-M541 applies only without an open pair. **2 checks** in `endpoints` (258 → 260).

**T295:** Pack and single no longer exclude each other (F-M585): no `deletePublishedSubtitle` in the pack path, the member rows no longer get a pack id, and the single path skips no pack position. `CoveredByPack` reads the episode list of the `packs` row. **3 checks** in `endpoints` (260 → 263).

**T296:** A source without this language does not count as open (F-M586): `NimmtSprache` as the only place for language × source, the filtering in `AnyUploadSourceStillWants` and `SettleDuplicateContent`, the reason `unsupported-language` and the closing in `PairIsDecided`. **3 checks** in `endpoints`.

**T297:** Sources simultaneously instead of in sequence (F-M587): the chain collects the responsible sources (`zustaendig.Add`) and starts them via `Task.WhenAll` with 0–5 % offset (`versatzMs` from `TransferPauseMs`). It returns one failure per source instead of throwing it, and reads `mySubtitles` only with `maxPages: 1`. **3 checks** in `endpoints` (266 → 269).

**T298:** The SubDL id once at the run end (F-M588): the upload remembers the track instead of resolving it immediately. `ResolveDeferredUploadIdsAsync` fetches **one** list call, enters the ids via `SetDeferredSubdlId`, uses each entry exactly once (creation-time order) and pulls the published list once for what was released. **3 checks** in `endpoints` (269 → 272).

**T299:** The pause hangs on the track (F-M589): the flag is set per candidate, the file flag no longer carries a pause, and it stays at exactly one transfer pause in the path. **3 checks** in `endpoints` (272 → 275). The F-M547 check now asks the track flag.

**T300:** Note immediately and timeout per source (F-M590): the mark is written immediately after the source's answer. A timeout without a set `ct` becomes a failure of that source instead of a run abort, and only three tracks without an answer from any source end the run. **3 checks** in `endpoints` (275 → 278).

**T301:** Timeout per source also in the download (F-M591): the movie path is guarded, and search attempt and main search as well. A real cancellation flies on, and only three items without an answer from any source end the run. **3 checks** in `endpoints` (278 → 281).

**T302:** File fetch and reports guarded against timeout, dead sources stay out (F-M592): the fetch falls back to the next candidate. `MerkeQuelleTot` carries the failed sources for the rest of the run; without a remaining source the run ends, and open reports are deferred. **3 checks** in `endpoints` (281 → 284).

## 3. Tests of the download pipeline (§4)

**T15:** UTF-16 downloaded bytes decode to the same content hash as the uploaded file (F-M187)

**T35:** A mis-typed episode is searched by its parsed name and receives its own pack entry (F-M217, F-M215)

**T58:** The HI variant comes from its own search (F-M241)

**T59:** One corrected file per language: the walk stops at the first candidate whose correction proves itself, and an unprocessed file never ends it (F-M319/F-M318)

**T71:** One name parser, and a bracketed year leaves no bracket in the title (F-M251/F-M252)

**T77:** The fetched file's own HI flag decides its name and its record (F-M260)

**T98:** The walk's candidate limit and the search's early-stop threshold are the SAME number, derived from the ONE correction-attempts setting (F-M50): with that setting at 3 both are 3, at 2 both are 2, at `0` both stay unlimited.

**T108:** The drift detector, driven with synthetic material, reports NO drift for cues that track the speech at a constant offset. It recovers planted steps of known size and time at 20 and 30 min, with a span matching the planted total, and reports drift for a ramped offset. The factored two-offset marginal used in the port equals the explicit outer log-sum-exp: worst deviation < 1e-6 over 20 random trials (F-M295)

**T109:** End-to-end on a real episode: the plain subtitle comes back steady while the hearing-impaired variant of the same episode comes back drifting with a span and boundaries named, and the verdict states a span rather than a correction value (F-M295)

**T110:** The auto-sync's two file-level halves, without audio. The audio-track rule picks (1) the track in the subtitle's language, (2) English, (3) the first untagged track, with the 2-vs-3-letter codes (`en` against `eng`, `de` against `deu`/`ger`, `zh` against `zho`/`chi`) resolving as equal and `und`/null resolving as "no language". A planted constant shift moves every timestamp by exactly that amount while text, cue count and cue order stay unchanged. A shift that would push the first cue below zero is APPLIED and that cue is CLAMPED to 0, with every later cue still carrying the full measured shift. The corrected file is written CANONICAL (UTF-8, no BOM, LF) — asserted on the real writer, so a reintroduced re-encode to the fetched byte style fails here — while the archived original keeps its own byte style. And the kept name `<base>.<lang>.srt.unsynced.zip` does NOT match the loose subtitle listing pattern of the name stem with `.srt`, while the swapped order does (F-M296, F-M306)

**T118:** The kept original, driven through the real planner and read back through the real name parser. The first original takes slot **51** beside the corrected file. The name **matches** the loose subtitle listing — that match is the point, since it is how the file becomes a selectable track — and parses as the corrected file's own language with `hi=false` and `forced=false`. With 51 taken, the next original takes **52**, and the numbering stays inside 51–99. When all 49 reserved slots are taken, **no name is returned**, so nothing can overwrite an original already kept; and **every corrected slot taken returns the plain name**, never a reserved one: the collision F-M315 exists to prevent.

**T120:** Real material is scored against the **audio**, never against another subtitle (F-M305), and the run is repeated on its own output to assert idempotence in the same test: the timeline must be unchanged on the second pass.

**T121:** The two defects of F-M307 are regression cases and must fail loudly if they return. **(a)** A shifted cue window that falls outside the audio scores 0 and is **excluded**: the fit must never return a boundary-runner shift on cues that were not moved (the clipped behaviour returned shifts at the search limit). **(b)** The before/after report is scored with the **real cue ends**, asserted against a known value: a report over one frame at the cue start silently makes the deploy rule revert good files.

**T128:** The reserved block is exactly **90–99**: the reservation test is true at 90 and 99 and false at 89 and 1. The target-name search runs **downwards**: 99 first, then 98. It returns **null** when all ten are taken, never a name that would overwrite. The target name for corrected files never hands out a reserved slot: with slots 1–89 taken it returns slot 1 (`<base>.<lang>.01.srt`, F-M316) so the caller's own existence check refuses the move.

**T175:** The account filter of the download search — account identifier, no mis-grab on foreign uploaders, exclusion before the ranking and the switch including its label (F-M449).

**T409:** The runtime window (F-M43), in `reporting`. **7 checks**. The window is 12 % of the runtime when that is larger than the configured floor, and never smaller than it. The two reported cases of the run (88 % and 91 % of their film) now sit inside it. A genuinely cut subtitle is still rejected, and a missing runtime with a switched-off check passes everything. The gate asks the window, and the verdict names the one that was applied.

**T176:** The report of an observed defect — wording without justification, category per finding, name of the affected file in both tracks, sender line with version and account name. Report in the same run, deferral instead of discarding, locked candidate after the report, switch and its position as the second-to-last entry (F-M450). **4 checks on the file naming.**

**T177:** What a file on disk proves — the reserved original block (51–99) closes no pair, an aligned `01` does, a deleted `01` opens it again, an unnumbered name still counts, and the slot reader reads what the writer writes (F-M452). **12 checks.**

**T179:** Which name carries an episode — `6.10 …` in a pack is E10, `5.10 …` is never season 6, `DDP5.1` is no E1, `S06.1080p` is no episode, and `SxxExx` works unchanged beside it (F-M454). **6 checks.**

**T180:** An empty fetch is no verdict — the refetch after ten seconds, exactly once per candidate, reported only on the second empty fetch. A member is a file and thus reportable, a pack is not; and the accepted report reaches the statistics row, a dry run does not (F-M455). **6 checks.**

**T181:** Decorative letters and verdict per attempt — `𝐒𝟎𝐄𝟏𝟎` and fullwidth are read, the written `S00` stays season 0, a deleted verdict carries no status, and only a real 200 counts as empty answer (F-M456/F-M457). **7 checks.**

**T182:** No statistic name is longer than the longest row the table already carries — the name is shortened, never the number (F-M458). **1 check.**

**T183:** The statistics spans the field and pushes the number to its right edge, so that it ends on the same vertical as the status columns (F-M459). **1 check.**

**T184:** The SDH path reports its measurements — structure and runtime verdict become findings, what was not delivered does not, the empty fetch is repeated once, and the finding is set immediately instead of at run end (F-M460). **6 checks.**

**T185:** The v1 fetch path writes status, error name and length of its own answer — a reject names the reason of this inquiry, not that of the previous one (F-M461). **1 check.**

**T186:** The finding of the main path is set where it was measured — before continuing to the next candidate, not behind "nothing stored" (F-M462). **1 check.**

**T187:** The chosen file name decides in the archive — it resolves an entry without episode name, a non-existent name stays null, and the episode search comes before the reload (F-M463). **7 checks.**

**T188:** A fetch carries its verdict, a resolution happens once — no client state, a common resolution order, archive via the path of the file list (F-M464). **9 checks.**

**T189:** The upload follow-up hangs on the upload, the season memo on the fingerprint (F-M465, F-M466). **7 checks.**

**T190:** The series is resolved via its id — without a loaded series, without lookup, and with precedence of the loaded series (F-M467). **4 checks.**

**T191:** The track list is read via the medium's id, the object list remains the fallback (F-M468). **2 checks.**

**T192:** The group choice is binding: split season without coverage, covering group, nameless group (F-M469). **5 checks.**

**T193:** The follow-up stands once on the page; the status line stays silent about it as long as it is healthy, and names it in the failure case (F-M470). **3 checks.**

**T288:** No candidate cap per language (F-M578): `PluginConfiguration` carries no field `DownloadMaxCandidatesPerLanguage`, and the parameter `maxCandidatesPerLanguage` occurs **only** in `ScribeApiClient.cs`. No call site sets it. **1 check** in `endpoints` (248 → 249).

**T289:** No IMDb mandatory switch (F-M579): `PluginConfiguration` carries no field `DownloadRequireImdb`, and the download pipeline fetches the missing IMDb id via `ResolveImdbAsync` as soon as TMDb is configured. **1 check** in `endpoints` (249 → 250).

## 4. Tests of upload postprocessing (§5)

**T8:** A duplicate release is uploaded, then removed by postprocessing and marked `duplicate-remote` (F-M184)

**T24:** Sixteen reschedules are accepted, the seventeenth is refused once (F-M205)

**T30:** Refresh, OSHash, postprocessing and the refetch cycle each fire on their own setting (F-M210/F-M210a/F-M211)

**T31:** A fire that comes due before the tasks are registered is not consumed (F-M212)

**T38:** Log lines carry the new namespace while the data folder keeps the assembly name (F-M223)

**T42:** Every task the plugin registers reports the Sub Scribe category (F-M227)

**T97:** A dry run leaves no stored verdict in either direction - the refetch stamp, the registry upserts, the file-missing deletion and the cycle-queue removal are all held back, while the worker-run record is still written (F-M22, F-M287).

## 5. Tests of the database refresh (§6)

**T33:** Compaction covers data file plus journal (F-M214)

**T51:** A failed compaction leaves the database answering (F-M236)

**T54:** A rebuild that cannot release the pages is replaced by one built from the rows (F-M237)

**T75:** The refresh drops embedded rows whose stream is gone (F-M258)

**T293:** Verdicts without evidence (F-M583): `ClearVerdictsWithoutEvidence` checks both reasons, demands missing marks and unknown origin, deletes embedded **and** loose subtitle rows, and the database refresh calls it in its run. **2 checks** in `endpoints` (256 → 258).

## 6. Tests of the rules shared by both directions (§8)

**T63:** N text streams cost exactly one ffmpeg call, and an unknown index fails the pass (F-M5)

**T89:** An untagged track is resolved and the found language written back (F-M261)

## 7. Tests of the API, the identifiers and the credentials (§9)

**T25:** A respaced fire never claims a quota reset (F-M206)

**T27:** The reschedule budget resets at the UTC day roll-over (F-M208)

**T36:** A title that is both film and series resolves by year, type-neutral first (F-M219)

**T46:** Jellyfin's ids are corrected by title and year, counted over both directions (F-M231)

**T47:** A server-error login is classified as transient; wrong credentials fail closed at once (F-M232)

**T50:** Three transport timeouts are retried, a user stop is never (F-M235)

**T156:** The upload reports the frame rate code of the item's video (F-M350).

**T210:** Order and the notion of success. The order is a setting defaulting to tvsubtitles.net first, and the GUI offers it under Downloads and stores the choice. Both endpoints are asked and their candidates **merged** instead of replaced (F-M494). A kept original version counts in auto-sync not as a success but as `DownloadedWithoutSignificance`. **4 checks**, all in the suite `endpoints`.

**T213:** Both endpoints are asked, the second gets its own attempt (F-M494). The ranking leads with the source in the configured order, before pack and score. An abort of the correction search is withdrawn as long as a candidate of the other source is still untried, together with that source's reject counter. And the fetch path checks the candidate source's switch immediately before the fetch, through **one** helper (`SourceAllowed`) that the main pass and the SDH variant share. **3 checks**, Suite `endpoints`.

**T220:** The upload queue asks per source instead of source-blind (F-M499). The seeder's upload branch uses the per-source readers (`ArePositionsTerminalFor`, `CountKnownPairsFor`, `IsSidecarUploadedTo`) and **no** longer the blind ones (`ArePositionsTerminal`, `CountKnownPairs`, `IsSidecarUploaded`). The helper runs only over sources that are switched on **and** configured: their mark is read by **one** rule (`IsUploadedTo`). **2 checks**, Suite `endpoints`.

**T221:** The **head** of the upload run asks per source too (F-M499). The pipeline reads the file mark only via `IsSubtitlesUploadedFor(…, provider)`, and it skips a medium only once **none** of the switched-on **and** configured sources (`UploadSources()`) is open. It no longer calls the blind `IsSubtitlesUploaded` there; the per-source counterpart of the mark filters via `IsEmbedTerminalFor` and thus via the same rule as the upload itself. **2 checks**, Suite `endpoints`.

**T222:** The settle verdict "already uploaded" asks per source (F-M500). The source evidence comes from `IsContentUploadedFor(…, provider)` (the same marks as `IsUploadedTo`), and it is put at **all four** places of the upload path through ONE rule (`SettleDuplicateContent`); `RecordSkippedContent` writes the mark only for the source that holds the content.

The rebuild is driven against a throwaway plugin database. The same content at SubDL does **not** settle tvsubtitles, and twice at the same source stays blocked (and writes no rejection). Without source evidence the rejection stays source-neutral. The provenance gate holds in all three cases. Unknown or external provenance may go to **both** sources (chain order, own mark per source, both marks side by side on one line), while `origin=subdl` goes up **nowhere** (`origin:subdl — downloaded there, never uploaded`), `origin=tvsubtitles` just as little. And a track whose content is known as a download is settled for **both** sources (negative control: without known provenance it is not). **10 checks**, Suite `endpoints`.

**T223:** The tvst series number stands in the plugin database and a missing series is created with the TMDb title (F-M501): the comparison is strict (case and whitespace do not count, `&` = `and` = `und`, **no** substring and **no** article trimming). A set `TvstShowId` prevents search **and** creation (null inquiries). A hit writes the number into the medium without creating; without a hit **exactly once** is created (name/year/season count from TMDb), then searched **again** and only then written.

The season count is limited to 1..22 (30 → 22, no indication → 1) and `BuildAddShow` rejects 23. A failed creation writes nothing, does not block the chain (`Skipped`) and is **not** repeated in the same run. The dry run creates nothing, and an unclear series list is inquired once and then not written.

The endpoint does not search with number in the ticket **before** the upload (the one list afterwards is the id read-back, F-M488). And the suite runs the migration itself on a file from schema 12: the column is pulled on opening, the stock row remains readable, the marker then stands at 13. **16 checks**, suite `endpoints`.

**T224:** The language check of the tvst upload runs through the tvst-specific bridge (F-M502). The page's four deviations are mapped in **both** spellings (`el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`, `zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp`). The other page languages come from their ISO-639-2 forms (`deu`/`ger`, `fra`/`fre`, `spa`, `pob`, `ukr`, `nld`/`dut`, `ron`/`rum` …). The page's own codes stay equal to themselves, and upper/lower case and whitespace do not count. Region and script fall back to the base code (`zh-Hans` → `cn`, `ja-JP` → `jp`, `es-419` → `es`), **except** `pt-BR`/`pt_BR`, which is Brazilian Portuguese (`br`), while `pt` and `pt-PT` stay at `pt`.

An unknown code yields `null` instead of a guess, and `BuildUpload` refuses it with **one** line naming the page's codes (an ISO code, by contrast, is **no** longer refused). The `lang` field then carries the page code, and `LanguageName`/`Iso3` come from the table. The upload's filename keeps its ISO3 ending (`… 2x24.gre.srt`) in the multipart body. **9 checks**, all in the suite `tvsubtitles`.

**T226:** The sources are **asked** in the configured order (not merely sorted), an empty first source no longer ends the operation, and the sources are a list instead of a pair (F-M504).

The question order names **every** provider of the enum and puts the configured source first (both settings, `Leading` included). The number of sources asked is the number of **enabled** ones. Next, **the sequence is rebuilt from the same building blocks the run uses** (`SourceOrder.ForOrder` plus `SourceOrder.MainSearch`, the constant the pipeline branches on) and compared. With `TvSubtitlesFirst` the tvst query stands **before** the SubDL search, with `ScribeFirst` **behind** it, each as the first member of the list.

On the source text the SubDL call (`RunV2SearchAsync`) sits in the local function that the branch "this source is the main search" calls, and **no** call stands behind the loop. In the loop body there is **no** `break` and no exit line, and both branches (main search, additional source) are present. Behind it stands **no** `ForeignCandidatesAsync`. No source is skipped, no premature exit. The exit on an empty answer stands **behind** all questions (also behind the SubDL search).

The run builds its list from `SourceOrder.ForOrder(DownloadOrder)` **and** the switch **per source** (no `zusatzQuellen` residual list any more, no hard-wired provider). If all sources asked are empty, it stays with the previous result (`NotAvailable`, `SkippedNoCandidates`, return), and the line names the sources asked instead of "SubDL" alone.

`ForeignCandidatesAsync` has **exactly one** call site (no double fetch per source and language: the language loop only reads the dictionary). Search as well as fetch take the endpoint of **every** source via `EndpointFor` from the catalogue (no `_tvDownload` any more). **10 checks**, all in the suite `endpoints`.

**T227:** The tvst upload's release field is the release group, not the filename (F-M505). For the real library name `The Middle.S01E01.MULTI.1080p.WEB-DL.H265-FTMVHD`, `SeasonPack.ReleaseGroup` yields the group `FTMVHD`: the name with dots stays the name, the group hangs behind the hyphen.

On the source text the tvst ticket (from `new Api.Endpoints.UploadTicket(` to `TvstShowId: show.ShowId);`) carries `SeasonPack.ReleaseGroup(releaseName) ?? string.Empty` and **no** `GetFileNameWithoutExtension`. The page cuts a release at the hyphen: a filename would arrive there as `The Middle.S01E21.MULTI.1080p.WEB-DL` and the group would be lost. An empty field is permitted and the majority on the page. **2 checks**, all in the suite `seasonpack`.

**T228:** The third source's client is drivable against the recorded pages and keeps its own tables (F-M506): 13 language codes with display name and file code, of which **none** silently becomes English.

The ISO forms of the 13 are mapped (`eng spa fra/fre deu/ger pob rus uk/ukr ita el/ell/gre ara hun pol tur`), `pt-BR`/`pt_BR` is `br`, and `pt`, `por`, `nl`/`dut`, `sv`, `da`, `fi`, `ko`, `zh`/`zho`/`chi`, `ja`/`jpn`, `bg`, `cs`/`ces`/`cze`, `ro`/`rum`, `xx`, empty and `null` yield **`null`**. The rip table has the selection field's nine values in its order and is **not** tvst's (there `HDTV`/`HEVC`/`DVDSCR`, here `WEB-DL`/`WEBRip`/`BrRip`/`HDRip`); `WEB-DL` stays `WEB-DL`, `BluRay.x265` becomes `BluRay`, but `x265`/`HEVC`/`H.265` **alone** yield the empty value (the page knows no codec), and `HDTV` likewise. The resolver finds `Dune` (1984) and `Dune: Part One` (2021) via the TMDb title, yields nothing on a **wrong year**, finds the inverted spelling (`The Opposite of Sex` → `Opposite of Sex, The`), makes **no** partial hit and does **not** guess with two identical candidates.

All three list views are parsed (film list 487 rows, upload list 492, search 5 despite HTTP 500) including a repeated year in the name. The film page with 54 entries/12 sections and 51/13 (language from the flag, section heading as counter-check, free rip values stay), the doubled year bracket (`O Beautiful (2002) (2002)`), the film **without** entries. The entry page (rip, release, filename, `parts`) and the junk page of an unknown id (**`null`**). The download follows the **relative** 302, keeps brackets (`ru(2).zip`), takes the file with the language code out of the zip and flags multi-parters, refuses `files/` and returns bare `.srt` bytes unchanged. The upload goes in the form's order (`tvid lang rip release author UFile UFile2 UFile3 comments go`) with `go=1`, without `author` when empty, with the **release group** instead of the filename, with identifiers and client in `comments` and with `UFile2`/`UFile3` only when filled.

`tvid=0`, an unknown language, a rip outside the table, missing ids, an empty file, a wrong extension and a third extra are refused **without** a single request. The dry run builds the body and sends nothing, the live run posts multipart to `add.php` with the form as referer. **84 checks**, all in the suite `moviesubtitles`.

**Stage 2b (F-M508):** the fourth half drives the UPLOAD ENDPOINT against a counting handler. A ticket with season/episode, one with a season pack, a foreign language and a missing number without a year are refused without any request arising. The dry run reports the body and sends nothing. The live path (against the handler, not against the page) sends `tvid lang rip release UFile comments go` in the form's order, leaves `author` out, carries the group `FTMVHD` in `release` instead of the filename. It reports 2xx as "accepted, id unknown" (`Id = null`) and a 500 as a refusal with the page's text.

**T229:** The third source is a provider, and the media kind decides which page is asked (F-M507). Without a set switch the moviesubtitles **download** is on and the **upload** off; a stored `false` stays off and a `true` beats the legacy switch (which does not answer for this source).

`SourceOrder.AppliesTo` says SubDL **both**, tvsubtitles.net **series only**, moviesubtitles.org **film only**. The question order built from it names `tvst, SubDL` for a series and `SubDL, moviesubtitles` for a film. The respectively other page stands in **neither** of the two orders. A switched-off switch leaves exactly SubDL over. `moviesubtitles` is a provenance like the other two: name/parse round trip, a **known** provenance locks the upload at all three targets, unknown stays permitted.

The source has **no upload path**. `HasUploadPath` is false for it, the upload's chain order is cut on it and does not even name it as a dead entry (source binding to `.Where(Api.Endpoints.SubtitleEndpointCatalog.HasUploadPath)`).

The run cuts its list by media kind and demands season and episode for series. It fetches the TMDb record for films (`MovieInfoAsync`), resolves the film number via the plugin database (`MsubMovieId`, the second time **read** instead of searched, no second network access) and passes title, year and number. The film page's endpoint fetches the film page **once** per film and run (two languages, one GET, language from the flag). It denies a series (season/episode set) as well as a language without a page code **without a single call**.

And the migration is driven on a file from **schema 13**: the three columns of both subtitle tables and `MsubMovieId` are pulled along on open, the existing row stays readable unchanged (`MsubMovieId` = `null`), the marker then stands at **14**. **14 checks**, all in the suite `endpoints`; the default of the two switches is recorded by the same suite `offstate`.

**T230:** The film address's upload path is built, wired and locked (F-M508), and the order arranges two sources instead of three addresses (F-M509).

Checked on the source text and on the plugin database. The chain sends per source via `SendToSourceAsync` (SubDL, series address, film address), collects one result per source and writes per source **its** mark and **its** timestamp (`MarkOf` returns `(null, null)` for a source that did not take). The film address gets `Season: 0`, `Episode: 0`, `MsubMovieId` from the resolution result and `MovieYear` from TMDb, refuses a series ticket (`film-source-films-only`) and is built with the run's dry-run switch; **no** switch is read directly in the chain (only via `SourceAllowed`). The gate knows the third address (`MovieSubtitles`, `AllowedCount`, `already-uploaded:subdl+tvsubtitles+moviesubtitles`) and the provenance locks it along with the others. A mark in the film address's columns holds **only** for it (neither for SubDL nor for tvsubtitles.net), and `RecordSkippedContent` sets the timestamp for it alone.

On the order: `DownloadSourceOrder` has **two** values, `ForMedia` names `tvst, SubDL` for a series and `moviesubtitles, SubDL` for a film (reversed with "SubDL first"), so `tvst zuerst` leads the film address **before** the SubDL search. And the upload chain contains the film address only for films: by default its switch stays off. **8 checks**, all in the suite `endpoints`.

**T231:** The log lines of the first film run name the address that was asked and the source that won (F-M510): `SourceOrder.FamilyPage` yields tvsubtitles.net for a series and moviesubtitles.org for a film, and `Provenance.Name` turns that into the labels `tvsubtitles`/`moviesubtitles`.

The summary line's label is **no** fixed text any more (no `tvsubtitles-D` literal in the pipeline's source text, the placeholder `{Src}` instead). The address stands as the **first** argument before file, language and count, and the line's statement stays (candidates per source asked from `quellenReihenfolge`, plus the question order). The label is exactly the address that `ForMedia` puts in first place for this media kind. The DRY-RUN line carries the provenance as the first field of the bracket (`({Source}, score {Score}, hi={Hi})`), which comes from `cand.Provider` and stands in the **same** call; the old form without provenance is gone, `score` and `hi` keep their place and order.

The shared lines of both directions name the source from the list (`Provenance.Name(quelle)` in the count and in the upload dry run). While a fixed label stands only in the **own** endpoint branch (the film address in its film branch, no `tvsubtitles` line in the download's film path). **8 checks**, all in the suite `endpoints`.

**T232:** A trailing bracketed year is split off before the TMDb title comparison — and only there (F-M510). `ComparisonTitle` turns `Oppenheimer (2023)` into an `Oppenheimer`, `The Office (2019–2023)` (and the spelling with `-`) into a `The Office` and `Dune [2023]` into a `Dune`. `(Ghost) Riders In the Sky (American Outlaws_ Live …)` stays **unchanged** (only a **trailing** bracketed year counts, a bracketed title is not cut up), and `Blade Runner 2049` likewise (year in the title, no bracket). The doubling `O Beautiful (2002) (2002)` loses exactly ONE year.

Against a handler that imitates the measured TMDb answer (the plain title hits, the bracketed text does not), the **strict** title+year test finds the film (tmdb 872585, tt15398776, not a series). The search request carried `query=Oppenheimer` and the year **never** went out (exactly one search, no second attempt). And the measured plugin database state (Jellyfin's wrong `tmdb 1422`) is **corrected** instead of tolerated. **9 checks**, all in the suite `moviesubtitles`.

**T233:** A raw quality from the release name passes through the film endpoint and lands as the page's rip value (F-M511). Six cases — `web`, `1080p BluRay x264`, `WEB-DL`, `HDTV`, `x265` and empty — go through `MovieSubtitlesUploadEndpoint` as a ticket. Each is **accepted** (`Ok`, dry run, no refusal reason, the measured `Ok=false` case is gone). And the payload of each raw value is **byte-identical** to the payload of its mapped value (`web`=`WEB`, `1080p BluRay x264`=`BluRay`, `WEB-DL`=`WEB-DL`, `HDTV`=empty, `x265`=empty, empty=empty): the dry run shows the same body that goes in the live case.

Against a counting handler the live path substantiates the body: `name="rip"` carries `WEB` for `web` (and no `WEB-…`), the **empty** value for `HDTV`, `WEB-DL` for `WEB-DL`. The length of the body sent is the length the dry run named for the same ticket, and `TvSubtitlesClient.RipFromQuality("WEB-DL")` stays `WEB`: the foreign mapping is not borrowed. **5 checks**, all in the suite `moviesubtitles`.

**T234:** The queuer takes the media kind from the plugin database, and Jellyfin's type is the fallback (F-M512). On a real plugin database file `ContentHashRegistry.MediaKindIsSeries` yields `true` for a series row, `false` for a resolved film row and `null` for a row without identifiers/season/episode. Without a row, without a hash and with an empty hash it yields `null` (then Jellyfin decides).

With this information the family's address list is cut exactly as in the queuer (`SourceOrder.AppliesTo`). Plugin database=series leaves **only** tvsubtitles.net standing (moviesubtitles.org drops out), plugin database=film **only** moviesubtitles.org, and on `null` **both** addresses stay standing. SubDL is in on all three cases, "rather one item too many".

On the source text: the queuer calls `MediaKindFor(item, mediaPath)` (the old line `isSeries = item is Episode …` is no longer there), and the helper asks `StoredMediaKind` **before** the item-type fallback. The plugin database question is `_db.Media.FindById(mediaHash)` via the primary key, and the helper block contains neither `Async(` nor `HttpClient` nor `_tmdb`. No network, no TMDb request in the scan. **7 checks**, all in the suite `endpoints`.

**T235:** A missing film is created, the number is looked up instead of guessed, and the duplicate guard decides beforehand (F-M513). `BuildAddMovie` carries `name`, `year`, `go=1` in the form's order (trimmed), and the body contains exactly these three parts in this order and neither `filename=` nor `tvid`. An empty name, 1887, `MaxAddMovieYear + 1` are refused **without a single request**, while the bounds themselves (1888, current year+3) and the one-letter title `X` go through.

The dry run reports `dry-run` with the live case's body size and sends nothing (0 POST, 0 GET). The live case against a counting handler posts multipart to `/add-movie.php` with the form as referer, and the id comes from the **lookup** (`4242`), not from the answer. A 500 is `Ok=false` with `Reason = "HTTP 500"`, the page's text and **without** a lookup. An accepted POST without a hit in the lookup yields `created-id-unknown` with the answer text and **without** an id, all three spellings having been asked (`The Matrix`, `Matrix`, `Matrix, The`).

The guard in three cases: same year → `take` with the existing number, different year → `refuse` with `FoundYear` 1999, no hit → `create`. Two rows with the same title and year are `refuse` with `Hits = 2`. The network version asks the first term (the substring filter does not find it), takes the core and **stops on the hit** (2 GET, no POST), and an unreadable search yields `unclear` with `Answers = 0` instead of "does not exist".

On the source text: the creating version stands **only** in `UploadPipeline` (the download path still calls `ResolveForMediaAsync` with `_config.DownloadDryRun`), the fixed line `neu angelegt — Id {Id}` stands in the resolver, and the new number goes into `MsubMovieId` via `Persist`. **15 checks**, all in the suite `moviesubtitles`.

**T237:** The answer to `add-movie.php` is read at the two live-evidenced markers, and "already exists" leads to the search instead of a second attempt (F-M515).

Driven on the page's real answers. `ClassifyAddMovieAnswer` recognises `Movie has been added. Thank you.` as `created` and `This Movie already exist` as `exists`, while the unchanged form (`fixtures/add-movie.html`, 8399 bytes, live the answer to a POST without a creation) and the empty text yield `rejected`. The marker constants read literally as the page writes them ("exist", not "exists"). The success answer's fixture (8467 bytes, `add-movie-added.html`) against a handler that lists the film in the search yields `created` with the number **from the lookup** (26230) and **exactly one** POST. The same fixture without a hit yields `created-id-unknown` with `Ok = true`, without an id and with all three spellings asked (`The Matrix`, `Matrix`, `Matrix, The`): the answer's text stands beside it.

The refusal's fixture (8459 bytes, `add-movie-exists.html`) with a hit yields `Ok = false`, `exists`, the existing number (23716) and **no second POST**; without a hit it yields `exists-id-not-readable` with 1 POST and 3 GET: no error, no id, no upload. The unchanged form yields `rejected` with `Reason` beginning `no creation marker`, **0 GET** (there is nothing to look up) and exactly one POST. On the source text: the client calls `ClassifyAddMovieAnswer(plain)` and knows `Rejected` as well as `ExistsUnknownId`, the resolver carries `MsubMovieStatus.ExistsFound` and `ExistsWithoutId` and names the page's sentence via `MovieSubtitlesClient.AddMovieExistsMarker`. Both fixtures stand with their checksum in `fixtures/MANIFEST.sha256`. **6 checks**, all in the suite `moviesubtitles`.

**T238:** The creation of a series is driven against the real form and the real answer (F-M516), in the suite `tvsubtitles`.

The form (fixture) is `multipart/form-data`, posts to `/add-tvshow1.php`. And the field names read out of the HTML are **exactly** those of the body (`name`, `year_s`, `seasons`, `go`, none invented, none missing), with `go` as a hidden `1` and a submit button without a `name`. The confirmation "TV-Show has been added." is recognised on its page and **not** on the unchanged form. The measured success answer (302 to `?done=yes`) is `created`, as is the same answer as text behind the redirect. While a 200 with an unchanged form, an empty text, a 302 without a marker and a missing answer stay `unclear` and a 500/403 is `refused`, **a 2xx alone is no creation any more**. The creation's dry run builds the body, reports `dry-run` and sends nothing (0 requests). The live case against a counting handler posts multipart to `/add-tvshow1.php` with the form as referer.

The body has **exactly four** parts, no file part and the same byte count as the dry run, and the answer is read as `created` together with the redirect target. The number: the real search page yields `4242 → Weissensee` (without a year range) and resolves narrowly, "No results found" yields no hit, and the soft search (`Pagan Peak` → *Twin Peaks*, *Peaky Blinders*) does **not** resolve. `SettleStatus` yields `created` for found+confirmed, `existed` for found+unconfirmed, `created-id-unknown` for confirmed without a number, `refused` for refused without a number and otherwise `unclear`. **15 checks**, all in the suite `tvsubtitles` (50 → 65).

**T239:** The found film number goes into the media row addressed by its **path** even **without a media hash** (F-M517), driven against a throwaway plugin database in the suite `moviesubtitles`. A row that was created via its hash and carries the path receives the found number **13392** in `MsubMovieId` via `ResolveForMediaAsync(registry, mediaHash: null, …, mediaPath: …)`. The status is `found`, the **row count stays the same** (1 → 1), and `MsubMovieIdAtPath` reads it afterwards; the second call does **not** search again (`stored`, exactly **one** GET).

Without a hash **and** without a path row it stays with the honest result: the number is there (`found`, 13392), the plugin database then has **0** rows, and the note is `MediaNumberNote.NotStored` ("holds for this run only"). A path carried by **two** rows is no address: `SetMsubMovieId` answers `None`, both rows stay without `MsubMovieId`, nothing is chosen. **4 checks**, all in the suite `moviesubtitles` (119 → 123).

**T240:** The series case has the same hole and the same path (F-M517), in the suite `tvsubtitles`. On a real plugin database file the row of an episode (`TvstShowId` empty, path set) receives the series' number **512** via `der Weg, der die Serie sucht und anlegt (registry, mediaHash: null, …, mediaPath: …)`: status `found`, `TvstShowId = "512"`, **row count unchanged**. `TvstShowIdAtPath` reads it, and the second call reads instead of searching (`stored`, exactly **one** request to the show list, that of the first call). Without a hash and without a path row the plugin database stays **empty** (0 rows) although the number was found, and the note is `MediaNumberNote.NotStored`. **3 checks**, all in the suite `tvsubtitles` (65 → 68).

**T241:** A **real season** lies as an area in the tree and the check measures the tvst reader against it (F-M505), in the suite `tvsubtitles`: `scripts/tests/tvsubtitles/fixtures/season-middle-s01/` contains the season page of **The Middle (512)** and all **24** episode pages, byte-identical from the site. The season page delivers 24 episode pages without duplicates, each of them lies in the tree, `ParseSeasons` names season 1, and `ParseEpisodeLabel` yields exactly one number **1–24** per page (each once). Episode **1x05** carries **10** entries. The release cells carry the **groups** of the uploaders (`FEVER` ×3, `REWARD` ×1, 5 empty). The **only** entry with a file name is our own from **before** F-M505 (`The Middle.S01E05…`), and exactly that one is the reason why the group is sent. The rip cells remain the vocabulary of the site (`HDTV` ×8, `WEB` ×1, `DVDRip` ×1), and the languages come back as the site's two-letter codes (`fr` ×2, `en` ×1, among others `ro`). **8 checks**, all in the suite `tvsubtitles` (68 → 76).

**T248:** The download tab no longer carries a `tvst` heading, the upload tab exactly one (F-M525), and the GUI suite counts its failures at the end. Checked in `gui-structure`: `<h3>tvst</h3>` **0×** in the download part, **1×** in the upload part. The address switch ids no longer occur in the whole page; `TvstUpload/DownloadEnabled` including reasons are there, each direction names both addresses in one sentence, and the order field carries exactly two values (`tvst first`, `SubDL first`). **7 checks** in `gui-structure` (314 → 321), summary now honest (ten outdated F-M508/F-M509 checks replaced).

**T398:** The identity check decides about the pair (F-M738), in `endpoints`. **5 checks**: without IMDb in the hit the old IMDb is discarded instead of mixed. A matching pair reports no correction; a really different series delivers both identifiers of the hit; a type switch counts as a correction; and the old mixed row no longer stands in the resolver.

## 8. Tests of the scheduler, the quota and the timing (§10)

**T11:** An HTTP 429 answers with backoff or waits for the reset (F-M54)

**T12:** A 403 stops the run immediately (F-M54)

**T21:** The transfer pause is spread ±30 %, the bare-call pause is constant (F-M26/F-M26a)

**T22:** A deferral lands exactly the job interval (default 15 min, 5–120 min) after the STOP moment (no roll-over anchor, no added jitter). It carries the direction's own stored cause rather than a generic "rate limit", and the unchanged items stay `Queued` with the worker row reading `deferred` (F-M26b/F-M288)

**T23:** An overlapping trigger reschedules instead of widening the running cycle (F-M204)

**T52:** A 429 is classified by its variant, not by its status code (F-M238)

**T53:** The quota counters decide exhaustion, and an unreadable quota stops (F-M238)

**T55:** Every day-long 429 stop schedules a fire (F-M238)

**T66:** The scheduler's due-state survives a restart (F-M248)

**T67:** A missed anchor slot is caught up on the next tick (F-M248)

**T100:** A deferred direction shows its own stored cause under the Workers list, never a generic "rate limit". That line sits below the quota box rather than inside it, and the bars keep their 75 %/95 % colours at every fill level. The line disappears once the scheduler holds no fire for that direction (F-M288)

**T101:** No code path counts API calls against a local limit. The limiter exposes no bucket, no counter and no refusing call. A run that makes 4000 calls in a minute is never stopped by the plugin; only a real HTTP 429 stops it (F-M20)

**T157:** A GUI stop cancels the pending fire of the direction it names (F-M351).

**T250:** The source badge does not break Jellyfin's check-mark chain (F-M527), in `gui-structure`: `</span> <span class="src-badge` occurs in the page **0×**, each of the four badge-carrying switch controls contains `SubDL only</span></span>` (thus in the text span), and F-M338 compares the visible sentence. **7 checks** in `gui-structure` (331 → 338).

**T303:** The tempo is adjustable 50–500, default 450, and the note names SubDL's 500/h (F-M593). One field for upload and download: the label in the form, `max="500"` on the field, the check on save including the fallback and the description WITHOUT the SubDL note and the default in the configuration model. **4 checks** in `endpoints` (284 → 288).

**T304:** The tempo is a slot before the transfer (F-M594): the limiter holds the static anchor and offers `WaitForTransferSlotAsync`/`MarkTransferDone`, and only the difference to the target is waited. The upload occupies the slot before dispatch and sets the mark only after an acceptance; the download occupies it before the fetch and sets it only on bytes. **4 checks** in `endpoints` (288 → 292).

**T375:** The last source badges are gone, their settings stand in the source (F-M711), in `gui-structure`. **4 checks**: no `SubDL only` and no badge shape in the page any more, the three boxes including IDs deleted, and the three fields declared in `SubdlEngine.Declared`. Two checks from F-M338 (sentence equality of the daily-limit boxes) and F-M527 (badge in the label) drop out with the boxes; the GUI gate therefore counts 8 checks fewer (383 → 375).

## 9. Tests of the content registry and identity (§11)

**T13:** The same text as LF, CRLF, BOM or lone CR yields one content hash and one payload (F-M185)

**T14:** The content hash is lowercase 32-character hex of the canonical payload (F-M186)

**T16:** Writing the same track, loose subtitle or candidate twice leaves one row (F-M194b)

**T29:** No predicate reaches the database that it cannot translate (F-M193a)

**T60:** The HI answer comes from the registry, and `cc` counts as a marker (F-M243/F-M254)

**T64:** A forced track does not count as present, SDH does (F-M246)

**T73:** A registry lookup takes the media path and resolves the hash itself (F-M256)

**T74:** The embedded area is written with the uploader switched off (F-M257)

**T78:** The database is exercised without a Jellyfin host (F-M257/F-M259/F-M260)

**T159:** A forced track is recognized by its title when the flag is unset (F-M367).

**T160:** A deferred anchor job comes back, and deadlines are read in the clock they were written in (F-M368).

## 10. Tests of the library scope and the skip filters (§12)

**T20:** An automatic save updates only the plugin's own fields (F-M201)

**T32:** An empty selection stops every direction, and a nested selection reports only itself (F-M213)

**T37:** Both description carriers hold the identical string, and the release aborts on drift (F-M220, F-M221)

## 11. Tests of the configuration and the settings page (§13)

**T39:** The plugin's own log mode produces each level's lines without touching the server configuration (F-M224)

**T43:** Required markers render in the accent colour with no stray space (F-M228)

**T44:** Every anchor on the settings page is the accent blue, none the browser default (F-M229)

**T45:** The Libraries description states function and default only (F-M230)

**T202:** The switches are separate per provider and direction and the old value remains the fallback: the provider switch wins, also against the old value. As long as it is empty, the old value answers in both directions; tvsubtitles.net counts as a provider, its two directions are individually switchable (F-M485), and in the unset case the tvst download is on, the tvst upload off (F-M503). **10 checks**, all in the suite `offstate`.

**T225:** The default of the two tvst directions runs counter to each other (F-M503). Without a set switch the tvst **download** is on, even when the legacy switch `DownloadEnabled` is off. An explicitly stored `false` still means OFF and is overwritten by no default. And without a set tvst **upload** switch the upload stays off, even when `UploadEnabled` is on. **3 checks**, all in the suite `offstate`; the consequences for the catalogue are recorded by the same suite `endpoints` (the unset tvst download pulls the second endpoint in, a set `false` keeps it out, "everything off" demands both download switches).

**T305:** The statistics count only the QA verdicts (F-M595): own counter beside the rejections, and `qaFail` only at the place of the gate verdict. The statistics read `RejectedQaFail` instead of `RejectedCandidates`, and the line reads "Uploads: QA fails not sent". **4 checks** in `endpoints` (292 → 296).

**T306:** The pre-run (F-M596): own class with login/take/clean-up, use only on the same file and all positions, **three** candidates in advance, one pump with exactly one ffmpeg. The run announces the candidates, and a cancellation really ends ffmpeg. **6 checks** in `endpoints` (296 → 302).

**T307:** Every track carries a stopwatch across four phases (F-M597): stopwatch and counters, four marks, the line names all four phases at `Detail`, and a second line measures the loop between two tracks. **4 checks** in `endpoints` (302 → 306).

**T308:** The whole run is measured (F-M598): upload names item list, pack phase and run end, plus every item individually; the download has the same points; all run lines lie at `Detail`. **4 checks** in `endpoints` (306 → 310).

**T309:** The pack phase from the inside (F-M599): walk, episode count inquiries, file language scan, summary line and the separate fill phase, all at `Detail`. **4 checks** in `endpoints` (310 → 314).

**T310:** The pre-inquiry in the pack (F-M600): interface and implementation, no extraction in the inquiry, check before the first extraction with named reason, and the verdict stays the same. **4 checks** in `endpoints` (314 → 318).

**T311:** The episode count in the plugin database (F-M601): reading/writing/appending/covering, same table name in schema and creation, plugin database before network with feeding in, appending at both context places and the number in the planning row. **5 checks** in `endpoints` (318 → 323).

**T312:** The accepted attempt (F-M603): result shape, both addresses (movie and series), mark before the verdict and `MarkOf` lets them through, statistics without upload claim. **5 checks** in `endpoints` (323 → 328).

**T313:** The write bracket (F-M602): `BeginBatch`/`EndBatch` at the plugin database, both runs open it and close it in the `finally`. **3 checks** in `endpoints` (328 → 331).

**T314:** The stored pack verdict (F-M604): signature content, validity only on the same signature, table, `stat` instead of reading, both levels. **5 checks** in `endpoints` (331 → 336).

**T315:** The remembered no (F-M605): signature, whitelist, three places, table. **4 checks** in `endpoints` (336 → 342).

**T316:** The pre-inquiry of the movie identifier (F-M606) — check before sending, reason on missing identifier. **2 checks** in `endpoints` (336 → 342).

**T317:** Create, search up, close the pair (F-M607). **1 check** in `endpoints` (342 → 343).

**T318:** The title ladder (F-M608): pauses 2/4/8 s, lock after the fourth failure, cache before every access, only tvst and mst, memo list against the repetition per track. **5 checks** in `endpoints` (343 → 348).

**T319:** The three verdicts (F-M609): four verdict values, no verdict on timeout/exception/cancellation, 429/5xx as disruption, SubDL special cases, remembering after the verdict, separate header. **6 checks** in `endpoints`.

**T320:** Not closing too much (F-M610): evidence instead of lock, disruption does not count, `NoVerdict` instead of closure, evidence locks are discarded. **4 checks** in `endpoints`.

**T321:** The pack verdict for the fill attempt (F-M611): signature, stored no, both abort paths, passing on from planning. **4 checks** in `endpoints`.

**T322:** The tvst identifier (F-M612): no re-reading any more, own mark. **2 checks** in `endpoints`.

**T323:** The pump stays fed (F-M613): login at every subject, also in the skip path, and release before fresh extraction. **3 checks** in `endpoints`.

**T324:** the plugin database per subject (F-M614): real flush, call in upload **and** download pipeline. **3 checks** in `endpoints`.

**T325:** No silent failure in the title plugin database (F-M615): log, messages, appending at start. **3 checks** in `endpoints`.

**T326:** All stores on both start paths (F-M616): one helper, two calls, no plugin database bypassed. **3 checks** in `endpoints`.

**T327:** Every plugin database helps itself (F-M617): self-help in all five stores. **1 check** in `endpoints`.

**T404:** The source cards sit behind a tab strip (F-M743), in `gui-structure`. **4 checks**. The strip `#SourceTabs` exists, and `subdlSourceTabs` is called after the cards are built. Exactly one card is visible. The `active` card carries a CSS rule that shows it *and* an inline `display`, because a rule alone once kept every card hidden: the tabs were there and no window was. The hidden cards stay in the document, so values, `visibleWhen`, the per-card `Save` and the result line keep working. The first tab opens when the page is drawn. The source tabs carry their own class `subdl-source-tab`, because `subdlShowTab` clears `.subdl-tab-btn` and would otherwise strip their active state.

**T405:** The 'Required' marker in the headings keeps its accent blue (F-M228), in `gui-structure`. **1 check**. `.subdl-required` has its own CSS rule with `color: #00a4dc`, and at least three headings carry the class. The rule had gone missing while the source cards were built, and without it the span falls back to the heading's text colour — the marker read as just another word.

**T406:** A source that cannot take part shows a dead lamp on its tab (F-M744), in `gui-structure`. **3 checks**. The lamp hangs on the tab strip — one per source — and no card carries a state word any more. It reads the endpoint's `participates`, never a second check of the page. Its colour is green when the source takes part and a hollow grey ring when it does not; red stays reserved for destructive and failing states (F-M228).

**T407:** A tab carries an abbreviation of its source (F-M746), in `gui-structure`. **3 checks**. The tab button takes `tabName`, falling back to the display name and then the key. The full name stays in its tooltip. The abbreviation arrives through the source endpoint on its own carrier (`TabName` / `Tab` in the capabilities). The read path carries it through the response mapper as well; without that line the tab falls back to the full name silently, which is exactly what happened the first time. The page holds no list of names of its own, and the row labels of F-M731 stay untouched.

**T408:** A help line with an address (F-M745), in `gui-structure`. **3 checks**. The line is built by the helper that replaces its link word with an anchor made of document nodes. The field mapper carries address and link word through; without those two lines the text stays unlinked, the same silent fallback as in F-M746. The source names address and link word, and the page sets no HTML from it.

## 12. Tests of the data model and persistence (§14)

**T17:** Two streams of the same language keep independent verdicts (F-M197)

**T18:** A higher schema version disables writing and is never rewritten (F-M195)

**T19:** A renamed or moved loose subtitle stays one row; different text stays two (F-M199)

**T49:** Replaced by F-M283. The refresh REPORTS open required files and writes nothing: there is no download mark left to drop (F-M234/F-M283)

**T57:** Replaced by F-M282/F-M283. A vanished `.sdh.srt` is an open pair on the next ask, with no invalidation step (F-M240, F-M283)

**T79:** The subtitle datum is the pair — a variant is a different datum from its regular file, and a variant file still proves its language (F-M282)

**T90:** One coverage reader feeds the pipeline, the seeder and the refresh, and splits the two search pools apart: only the variant is open when only the variant is missing (F-M282/F-M283)

**T91:** A forced subtitle is a datum with its own flag, observed in both areas, and it never covers its language (F-M284)

**T92:** The refresh backfills the forced flag on rows written before the field existed, from their own names (F-M284)

**T93:** `hi` and `forced` are three-valued — a legacy row reads back as `null` and claims nothing, a fresh write states `false` explicitly (F-M285)

**T94:** A forced subtitle is observed but never uploaded — the loose-file path asks its deliverability check and skips it, the embedded path drops it via the dialogue check (F-M284)

**T95:** A media container rewrite re-states the forced disposition and is REJECTED if it did not survive, so a language-tag write can never silently turn a forced track into the film's dialogue (F-M284)

**T173:** The `packs` table keeps what it promises (F-M390).

**T236:** A row without a business key is not appended, and a refused append does not make the plugin database unwritable (F-M514), driven on real plugin database files, not only on the source text.

The incident's call (`Registry.MarkDownloaded(contentHash, mediaHash: null, …)`, i.e. `MarkSidecar` → `EnsureMedia(null)`) **does not throw**, creates **no** media row (row count unchanged, no row with an empty `Id`) and writes the loose subtitle row with an empty media hash. `Media.Upsert` with `Id = null` **and** with `Id = ""` is refused without throwing and without a row.

Afterwards the plugin database keeps writing: the seeder and upload rows of the `WorkerRunRegistry` read **from the file** as `ok`. A deliberately poisoned change tracker (appending a media row without an id past the set, as EF Core 10 leaves it) is **repaired** on `SaveChanges` (no throw). The change set before the save survives (detail `ueberlebte die Reparatur` read from the file). The repair is therefore no silent data loss.

On the source text: `EnsureMedia` takes `string? mediaHash` and writes nothing without a hash. The shared set refuses keyless entries in `Upsert`, `Update(IEnumerable)` and `InsertBulk` (three guard sites). `SaveChanges` is overridden and names `Unexpected entry.EntityState` and `ChangeTracker.Clear()`. `MarkSidecar` takes `string? mediaHash` and normalises to the empty value, and the film line no longer claims a save (`ist Film {Id} — {Note}`, the old wording `gespeichert.` gone). **10 checks**, all in the suite `endpoints`.

**T277:** Completeness per source from chapter 3.4 in `endpoints`: the access verdict is a `file` row of the mark matrix per source (`CompleteSubdl`/`CompleteTvSubtitles`/`CompleteMsub`/`CompleteSignature` stood at `media` until schema 18, F-M668). The signature stands beside it as `Fingerprint`. `IsCompleteFor`/`MarkCompleteFor`/`SignatureOf` exist, and "done" demands stamp **and** the same signature. The settlement `SettlementFor` reads rejections along (`x.Status == SubtitleStatus.Rejected`), and the header sets the stamp only after `offen.Count == 0`. Header and single-rower ask the same question (`IsCompleteFor`). **4 checks** in `endpoints` (232 → 236).

**T334:** The mark matrix as the ONLY truth.

Build, takeover of the old stock and the laying down of the broad columns (F-M667/F-M668), in `storex` against a **rebuilt old file** (full plugin database, then the matrix table removed, the broad columns re-created and filled, marker reset to 17). **Before**, the file carries 5 normal tracks, 1 HI track, 2 media with access stamps and 2 packs **only** in the columns, and the table `source_matrix` does not exist. **after opening**, **11 marks in 7 pairs of source and kind** stand there (subdl/track 4, tvsubtitles/track-hi 1, moviesubtitles/track 1, one file mark each for the three sources, subdl/pack 2). Two tracks of the same language are **two** marks at their positions (2 and 3) with **their own** timestamp and **their own** ids (`SourceId`/`UploadId`). The loose subtitle carries the fixed position −1.

The HI track is its own row with its own source and own id, a source without a stamp gets **no** file mark. And the rejected pack stands there with reason, date, fingerprint and pack id (without verdict), the accepted one with verdict and member fingerprint. **The old columns are GONE** — checked via `PRAGMA table_info` across all four tables (nine track columns, four media columns, six pack columns), while all 6 track rows and the loose subtitle remain. **every mark exists exactly once** (row count = number of keys, 11). **exactly ONE** backup file lies beside it. It is demonstrably the file **before** the run (marker 17, marks still in the broad columns), the marker stands at **20** afterwards, and a **second** start does not take over again (same row count, same number of backups).

**One mark per position:** the neighbouring position of the same language stays open although the other is closed (F-M669). **No fallback:** the track mark comes from the matrix; if the row is removed, the source is **open**, for track, file and pack, because there is no old column any more that could answer. Plus the **format switch** (F-M637): a file under the predecessor name is converted, its matrix is filled, its old columns are gone, and **no** second backup arises (F-M638). 24 checks, all in `storex` (62 → 86). **F-M670 adds 15 checks** in the same run: in the rebuilt old file there are additionally the old table `source_marks` (three rows: one covers an existing mark, one carries a mark that exists only there, one points to no owner row). And the id column `SubdlId` (three values, one of them without a counterpart in the broad columns).

After opening the **table is gone**, the **column on `embeds` and `sidecars` is gone**. The two id values and two old marks (of them **one new**) are in the matrix, the row without owner gets **no** matrix row, every identifier stands **exactly once**, and the run reports 0 conflicts. Plus the path **20 → 21** on a real 20 file (matrix filled, no broad column, but both remnants there, marker 20). Marker 21, table and column gone, the real identifier **replaces** the self-assigned mark (`own:…`) and the timestamp remains. A mark from the old table stands there with id and time, **one** backup, a second start without a second backup. **39 checks**, all in `storex` (62 → 101).

**T335:** The four levels with their own identifiers (F-M671, step A), in `storex` against **rebuilt old files from their OLD DDL** — columns and keys exactly as schema 17 or 20/21, the rows by hand. **The build is the test:** an old file does **not** arise by renaming or copying the new tables. That would create artifacts (a primary key `PackId`, a missing `Id`) that never existed in a real file of that schema.

Checked is the result: 44 `media`/315 `embeds`/13 `sidecars`/4 `packs` of a 17 file become **44 `file`/315 `track`/13 `sidecar`/4 `pack`**. Every track and every loose subtitle gets **one** number from the number circle of its file (per file unbroken 0…n−1, no doubling, no reuse). `FfmpegIndex` yields the pack index for embedded tracks and **−1** for a loose subtitle. Every file and every embedded track of a pack carries its `PackId`, a loose subtitle carries **no** `PackId` column, and `OriginId` survives the takeover.

The four old tables are **gone** afterwards, the marker stands at **22**, **exactly one** backup file lies beside it, and a **second** start does nothing (no second backup, no second run). Plus the path **21 → 22** on a file that already carries the matrix. **20 checks**, all in `storex` (101).

**T336:** The four levels and the three source tables since schema 23 (F-M672…F-M677), in `storex` against real old files. Identifier shapes `[xxxx]`/`#yyyyy`/`#yyyyy#zzz`/`#yyyyy&zzz`, one number circle per file across track and loose subtitle, `pack_track` with foreign keys, `upload`/`reference`/`badapple` with a subject column, no foreign identifier outside `reference`/`badapple`, `source_matrix` and `rejected_candidates` gone after the ascent, `OriginId` without loss of value into `reference`. Ascent 17→23 and 22→23 with exactly one backup and a second start that does nothing. **15 checks**, the last for the source neutrality of `badapple` (also a non-numeric identifier such as `download-3234-2-en` beside a SubDL number).

**T337:** The member in the bad download (F-M679), in `storex` against a real schema 23 file. `badapple` arises in it from the OLD DDL (three-part key, no `Member`), one row with reason, finding, report state and write time.

After opening, the table carries `Member` (13 columns), and the row stands there with `Member = ''` and unchanged reason and finding. `ReportState`, `Written` and `ReportedAt` are **copied along** and not newly derived. The marker stands at **24**, **exactly one** backup file lies beside it, and it is the state **before** the run (`PRAGMA table_info` finds no `Member` there).

Afterwards: two rows that differ **only** in the member both exist (the key contains it). A second report of **the same** sub stays one update (three rows instead of four), and a row with member is re-read unchanged via its key. A **second start** does nothing: no second backup, no second row.

Plus the path **22 → 24**: a plugin database from schema 22 carries no `badapple` at all. The table arises freshly WITH `Member` on the ascent, one backup, afterwards `file.PackId` and `source_matrix` are gone. **15 checks** in `storex` (116 → 129).

**T338:** The matrix "source × form × language × direction" (F-M681), in `matrix`: **96 cells** are really driven. 3 sources × 4 forms (single non-HI/HI, pack non-HI/HI) × 4 languages (EN, DE, FR, ES; in the plugin database ENG/GER/FRE/SPA) × 2 directions.

The cell count is computed (3 × 4 × 4 × 2 = 96) **and** counted: every cell exactly once, no duplicate cell key, no check without cell. The plugin database is **real** (production writer in a `ScribeDbContext` in the temp directory). Per cell stand the identifier forms (`#yyyyy`, `#yyyyy#zzz`, `#yyyyy&zzz`, `[xxxx]`, no double mark), language and HI on **pack and track** (the file carries neither of them, checked by reflection, and no pack), IMDb **and** TMDb on file **and** pack. The evidence per direction (`reference.DownloadSourceId` in the engine's form, `upload.Source`+`UploadedAt`, own submission identifier). The membership in `pack_track` (and its absence for the single sub). The comment field from the **real** body (`BuildUpload`/`BuildSeasonPack`, `BuildUpload` for msub) against `UploadComment` without plugin version, and refusal/bad download (open verdict; `badapple` with source, identifier and `Member`). The suite measures the source itself against fixtures where there are some: tvst (en/fr) and msub (en).

DE/ES and SubDL run **without** HTTP, and that is stated in the report. moviesubtitles.org carries no seasons: its 16 pack cells check the source (empty season search, rejected SeasonPack ticket, no row) instead of staying silent. **678 checks** in `matrix` (0 → 678).

**T339:** The plugin ↔ source engine boundary (F-M682), in `sources` — the suite MEASURES the boundary instead of describing it, and speaks with no source thereby. **The engine side:** under `Sources/<Quelle>/` there lies exactly one file per source (three adapters, counted). None of them names `Data.` or `Pipeline.`, none the plugin database or an identifier builder (`ScribeDbContext`, `SourceStore`, `ContentHashRegistry`, `ScribeIds`, `MarkKind`, `PackEntity`, `PackKey`, `OwnUploadId`), and none builds one of our marks (`[xxxx]`, `#yyyyy`, checked at their shape). **The flow side:** none of the 28 files in `Pipeline/` and `Data/` names an engine namespace or an engine class; the flow builds the registry and passes the context into it. **The registry:** every engine file is explicitly built in `SourceRegistry.cs`, and the registry does not scan (no `GetTypes`, no `Activator.CreateInstance`, no `IServiceProvider`). The **real** registry delivers three engines in the order `subdl, tvsubtitles, moviesubtitles`: the values the plugin database carries in its source column. Every engine names key and display name, `ByKey` finds each and invents none, and each maps `DEU` onto its code (`DE`, `de`, `de`). The capabilities are right (SubDL via id and with packs, tvst via the name, the movie without pack and without report path).

 **The contract:** exactly **four** public methods (`SearchAsync`, `FetchAsync`, `SendAsync`, `ReportAsync`, counted, without the accessor of the property) and exactly **two** properties (`Capabilities` and `Fields`, methods and properties counted separately). The search request carries all twelve details about the title, and handle, pack membership, member name, comment, target, members and foreign identifier stand in the jobs. The context carries HttpClient, logger, timer and archive cache, and the six normalized faults stand in the contract. **No own HttpClient:** no file under `Sources/` creates one. **The one wiring point:** `PackSlotSources()` asks the registry (`_sources.PackSlot()`), the two hard-wired names have disappeared from the slot question, and the answer is unchanged: without the tvst upload switch `subdl`, with it `subdl, tvsubtitles`. Also the REASON why a source is outside comes from the registry (`PackSlotExcluded()`), not from a name comparison. **27 checks** in `sources` (0 → 27).

**T340:** The settings per source (F-M683), in `sources` — the suite speaks with no source (no HTTP, no upload; the only check value is an invented key in the temp directory, and it is only compared, never output).

**The contract:** four public methods as before and exactly TWO properties (`Capabilities`, `Fields`), counted separately, so that a new property does not pass as a fifth job.

**The transition:** from a configuration with key and account, the first run writes exactly one file per source under `sources/`. The key and the account read from the file are the previous ones (compared, not output). The file carries `schema 1` and its source key; a second run writes nothing and leaves the bytes unchanged.

**The files:** three different paths `<key>.json`, each source reads its own. A copied file does NOT apply to the other source (it names its key); the file's permissions are 0600.

**The endpoint:** one entry per source in registry order. Every declared field arrives in declaration order, with label, kind and mandatory flag, and no field is invented (4/1/2 fields). The defaults are the clients' addresses (`https://api.subdl.com`, `https://www.tvsubtitles.net`, `https://www.moviesubtitles.org`); the check key stands NOWHERE in the serialized answer, only `gesetzt: true`. A write to one source leaves the other sources' files byte-identical, and an empty secret field leaves the deposited value standing. An unknown field is rejected. After writing, a NEWLY built engine reads account and switches from the file.

A broken file makes exactly one source unconfigured (plain-text note, the others keep running), and this source refuses the search with a reason instead of going to the network. No secret field carries a default. The routes stand as `GET Sources` and `POST Sources/{key}` and hang on `LocalAccessOrRequiresElevation`, not on `AllowAnonymous`. **20 checks** in `sources` (27 → 47).

**T341:** The diagnostics (F-M684), in `sources` — the suite claims nothing it does not measure: with a collecting logger, the LEVEL of every line, and the counters in the plugin database; no network access, no upload (every job ends before the first inquiry). **The level (source text):** the line per record in the plugin database stands as `LogUtil.Debug(… "file upsert"`, and every search stamp line in the tracker stands at Debug (there is no `Detail` line in the tracker any more). The mark lines (`embed`, `sidecar`, `subtitles uploaded-complete`) stand at Debug, and the API pairs of both directions stand at Debug. `LogUtil.Debug` writes with `LogDebug`, while the `LogMode` switch (`mode >= LogLevelMode.Debug`) is preserved.

 **The four jobs:** search, fetch, send and report each produce at least one line with `[Sub Scribe-Q]` and the job name; the line is a REAL debug line (`LogLevel.Debug`) and still carries the `[D]` marker; "no search basis" and "field rejected" stand visibly at `WRN`. **The counters:** two searches count two fetches and two jobs; the endpoint names the numbers per source including the last fault; the live channel counts an inquiry and a 2xx answer as a page with time, a 429 as its own failure with reason. An unreadable settings file produces a log line AND a counter, and only for its source. **The summary line:** a line `[N] SubDL: 5 Aufrufe, 0 Seiten, 3 Fehler, 0 s (5 Aufträge)`. **No secret value:** none of the collected lines contains the check key, although it stands in the file. **20 checks** in `sources` (47 → 67).

**T342:** The one outcome of both directions (F-M685), in `sources` — the suite measures at the contract and at fixtures of the real source answers, it speaks with no source.

(a) `SourceOutcome` has **exactly four** values in the order Accepted, Rejected, Busy, Failed (`Enum.GetNames` counted).

(b) `SourceSendStatus` and `SourceReportStatus` no longer exist (searched by name in the assembly, not in the comment), and `NoVerdict`/`Deferred` no longer occur in the source text of `Sources/` (**0 hits**). `Failed` remains, because it is a value of the NEW outcome.

(c) A 429 answer yields `Busy` with `RetryAfterSeconds > 0` (SubDL fixture with reset time) and is explicitly **not** `Failed`, while a 429 without a stated wait time yields `Busy` with `null`. (d) A login answer (401 at tvst, 403 at msub) yields `Failed`.

(e) "duplicate … already published", "already-uploaded", "season must be 1..22", "Film nicht hochladbar" and "could not be served … cookies disabled" yield `Rejected` **with the source text as reason** (five cases, wording compared).

(f) The acceptance (`{"success": true}` → client `Ok`) yields `Accepted` including submission identifier, "thank" likewise, on both site adapters.

(g) Send and report carry **the same** outcome type (`Outcome` of both answers is `SourceOutcome`, the set of types has cardinality 1), and report counts into the same counters.

(h) `RetryAfterSeconds` stands as `int?` on the outcome, neither of the two answers carries a `SourceFaultInfo` field, and search/fetch keep `Fault`.

**The shell and the counters:** a "busy" answer counts `Busy` (2 jobs → `Busy = 2`) with `Errors = 0`. The WRN line names outcome and wait time (`beschäftigt: 30 s warten, nächster Versuch ab …`), the debug line says `→ beschäftigt (30 s)`, and the summary line carries "busy" as its own item with the stated wait time. No network, no upload, no credentials in any line. **21 checks** in `sources` (67 → 88).

**T343:** Search and fetch across the four jobs (F-M686), in `sources`. On an **identical** fixture the suite runs the old path (client or endpoint directly) and the new one (via the engine) and compares the result sets without speaking to a source.

(a) SubDL: the same candidates in the same order by handle, language, HI, release name, pack, season/episode, `EntryId`, `ReportId`, `Address`, framerate and fetch (comparison line by line). The member list of a pack travels along completely (name, season, episode and the HI mark of the SINGLE file). An entry without identifier does NOT drop out but gets the address handle (`u:`).

(b) SubDL fetch: **byte-identical** bytes from the archive, the same file name in the pack, the same foreign identifier and a content hash over exactly these bytes at status 200.

(c) tvsubtitles.net: the same entries (identifier, HI mark, release name, order) from the captured pages, and on fetch the same bytes, the same file name and the same identifier.

(d) moviesubtitles.org: the same entries of the movie page (identifier, release name, order), no packs, and the identifier of the plugin database is that of the entry. No network, no upload, no credentials in any line. **11 checks** in `sources` (88 → 99).

**T344:** Send and report across the four jobs (F-M687), in `sources` — per case the suite runs **both** paths via the same fixture handler (old path = client or endpoint directly, new path = job) and compares them. (a) The **inquiries** — method, path and body field by field, the random multipart boundary replaced from the Content-Type. (b) The **reading** of the answer in the three classes from which the plugin database effect follows (closed / rejected / open). Twelve send cases. SubDL single and SubDL pack each accepted / rejected / busy (429) / failed (500), tvsubtitles.net single and moviesubtitles.org single likewise. Per case the same body (series number, season/episode, language, rip, group, comment, member files, release list, `is_full_season`) and per case the prescribed outcome (`Accepted` → closed, `Rejected` → rejected, `Busy`/`Failed` → open).

 Six report cases: SubDL and tvsubtitles each accepted / busy / failed with the same body (targetType, nId, reason, comment) and the same outcome; moviesubtitles.org has no report path and answers `Failed` without a single inquiry. The one deviation found is recorded as its own check: a 200 answer with "could not be served" or "could not be uploaded" previously meant "uploaded" (only the status counted) and now means "rejected" (the source's sentence decides, F-M685). Two guards keep the comparison honest: no inquiry goes to an address that is not a fixture, and every inquiry hits a path the fixture answers. Otherwise two empty bodies would stand against each other. No network, no upload, no credentials in any line. **53 checks** in `sources` (99 → 152).

**T345:** The sources section (F-M688), in `gui-structure` — the suite reads the page and checks what it DOES, not what it claims. The section exists exactly once, stands as a real `<h3>` in the `general` tab (a block outside would appear under every tab) and its cards come from the endpoint (`GET Plugins/SubdlSync/Sources`, loading on page build, writing via `Sources/<key>`). In the drawing block stands **no** source name, neither `subdl` nor `tvsubtitles` nor `moviesubtitles`.

All six field kinds (`text`, `url`, `secret`, `number`, `switch`, `choice`) occur in it. The number carries `min`/`max`, the choice its `options`, `help` stands under the field and `required` is noticeable on the field. The page reads `visibleWhen` from the schema (`field.visibleWhen`) and evaluates it on every typing and toggling (listener on the card). A secret carries **no** `data-value` into the DOM, only its state "set"/"not set", and an empty secret field is not sent along; an invisible field is likewise not sent along.

The button is called "Save", writes exactly one source (`Sources/<key>` per POST) and does **not** call `updatePluginConfiguration`. The state is "active"/"off" from `configured`, and the capabilities stand as pure display without input, selection or checkbox. After saving the card is refilled from the answer and names the written fields, and a rejection appears in the plain text of the answer on the card.

The three old SubDL fields (`Username`, `Password`, `ApiKey`) have disappeared from the page, and the page no longer writes `config.Username`/`config.Password`/`config.ApiKey` either. The page's collective save is still exactly one call. Plus the counter-check outside the suite. The page ran against a rebuilt DOM with an answer of the endpoint (three sources, all six field kinds, `visibleWhen` on `account`), and 28 checks were green. Among them: the appearance of the account on typing, the empty secret cell, the body of a card save, the refill after writing and the 400 display. **25 checks** in `gui-structure` (335 → 362).

**T346:** The translation of the endpoint spelling (F-M689), in `gui-structure` — four checks: the page carries `subdlSourceWert`/`subdlSourceAntwort`; the painter takes the translated answer (`payload.quellen`) and no longer reads the raw key `payload.sources` anywhere; the write path also goes through the translation. And the translation functions are **driven** with `node` against a real endpoint answer (two sources, "active" and "off", all six field kinds in order, `visibleWhen` on `apiKey`, `gesetzt`, capabilities, tempo 500, language chart `DEU`→`DE`), the result compared field by field. If `node` is missing, the check fails with a reason, not silently. **4 checks** in `gui-structure`.

**T347:** The English interface in the Endpoints section (F-M690), in `gui-structure` — two checks: the heading is `Endpoints` (and `Quellen` no longer occurs), and the card's own words are `on`/`off`, `Save`, `set`/`not set`, while the condition value in the schema is language-neutral `"set"`. **2 checks** in `gui-structure`.

**T348:** The direction switches in the source's file (F-M691), in `sources`. **19 checks**. Every engine declares the directions it can serve (moviesubtitles without upload), and they stand first with the defaults download on / upload off. The file carries them. `ReadFlag` reads them without a schema (missing file, missing field and foreign file give `null`, not "off"). The file beats the old value in both directions, and without a file the old value answers as before. The addendum writes today's value exactly once without touching a stored value or the access.

**T349:** The page no longer carries its own direction switches (F-M691), in `gui-structure`. **3 checks**. The upload and the download tab carry no checkbox `*UploadEnabled`/`*DownloadEnabled` and point to **Endpoints** instead, and saving this page no longer writes the old fields (otherwise every click in another tab would reset the directions). The family sentence (Series go to tvsubtitles.net, films to moviesubtitles.org, TMDb decides) stays in both tabs, because it says where a submission goes, not whether it takes place.

**T350:** The address is no longer a field (F-M692), in `sources`. **3 checks**: no engine declares `baseUrl`, no card holds a field of the kind `url`, and **no engine compares the address from the file with the build's constant any more**. The third check came after the deploy of 12.3.8.3: there, on every call of the endpoint, `names the address „"` stood three times in the log, because the comparison read the default value of a field no longer declared. The expected list of fields moves along (tvsubtitles carries only its two directions).

**T351:** The sparse card (F-M692), in `gui-structure`. **3 checks**. The characteristics are ONE text of at most three words (upload, report, packs) instead of a row of checkboxes, a secret field carries a placeholder instead of an empty box and **no** dead `is="emby-input"`, and the card no longer draws an address field.

**T352:** The four capabilities as switches (F-M693), in `sources`. **4 checks**. The badges are exactly upload/download/packs/report in that order (moviesubtitles only the download, no access field in the row). `packsEnabled`/`reportEnabled` at `false` take the source out of the pack slot and off the report path. The season ZIP switch arrives from the file, and what the file does not say stays at the old value.

**T353:** The season ZIP switch of tvsubtitles (F-M693), in `tvsubtitles`. **2 checks**. With the switch off the season page is not fetched and no ZIP is loaded (only the single path of the episode page), and the single path still delivers hits, none of them from a pack. Driven against a handler that counts inquiries per kind.

**T354:** The card stays sparse and operable (F-M693), in `gui-structure`. **4 checks**. The characteristics row is gone and the badges carry their own switches, a badge field is drawn exactly once, the save button carries `raised button-submit` like the other buttons, and every `SUBDL_SOURCE_*` identifier the page uses is also defined on the page. The definition of `SUBDL_SOURCE_CAP_KEYS` dropped out while a function still read it. `node --check` was green, the card stayed empty in the browser.

**T355:** The three accesses of the SubDL card (F-M694), in `sources`. **2 checks**. The card carries `apiKey` (secret), `account` with the label **Email** and `password` (secret) in that order, and e-mail plus password from the file then stand at the client the run uses: the file beats the old value (`alt@example.org`/`altes-passwort` becomes `konto@example.org`/`geheim-123`).

**T356:** The Download and Upload tabs no longer describe a source (F-M694), in `gui-structure`. **2 checks** (per tab): neither the note about the cards nor the family sentence stands in the markup. Checked is the markup of the tab, not the JavaScript block, because a comment in the script is not text the operator reads.

**T357:** No field without a question (F-M695), in `sources`. **1 check**: moviesubtitles no longer declares a field `access`, and no other engine carries one; the expected list of fields moves along (this source carries only its download switch).

**T358:** The save button is an upgraded Emby element (F-M695), in `gui-structure`. **2 checks**. The button arises via `subdlSourceButton('button','emby-button')` with `createElement(tag, { is: isName })` and carries `raised button-submit emby-button`, and fields, checkboxes and selection of a card arise the same way (`emby-input`, `emby-checkbox`, `emby-select`). A subsequently set `is="…"` is not upgraded by the browser.

**T359:** Checkboxes stay clickable and the kind stands on the card (F-M696). **3 checks**: `gui-structure` proves that fields, checkboxes and selection are **not** upgraded (no `subdlSourceButton('input'` / `('select'`) and carry their Emby class. The card header builds its characteristic via `subdlSourceServes` from `servesSeries`/`servesMovies`. `sources` proves the declaration itself (subdl = series and movies, tvsubtitles = only series, moviesubtitles = only movies).

**T360:** A card talks little (F-M697), in `sources`. **1 check**: no description of a field is longer than 40 characters; the message names the longest.

**T361:** The order for any number of sources (F-M698), in `sources`. **4 checks**. A stored list `moviesubtitles,subdl,tvsubtitles,unbekannt` gives `MovieSubtitles, Subdl` for a movie and `TvSubtitles, Subdl` for a series (the family is resolved, the unknown key drops out), a list with only `subdl` appends the family, and without a list the old two-value setting applies. Plus `endpoints` (the pipelines read `SourceOrder.Sequence(_config, …)` instead of the old two-value setting) and `gui-structure` (order on the General tab, no two-value box, rows from the answer, the list is saved and `DownloadOrder` derived from it).

**T362:** The card header names the name once (F-M699), in `gui-structure`. **1 check**: no `subdl-source-key` element in the card header any more, the key hangs on the name as `title`.

**T363:** The light stands under the heading (F-M700), in `gui-structure`. **1 check**: the card header no longer carries a `subdl-source-state` element, the light sits in its own row (`.subdl-source-state-row`) directly after the header.

**T364:** Without a stored list the order shows the old setting (F-M701), in `gui-structure`. **1 check**: both preassignments (`tvsubtitles,moviesubtitles,subdl` and `subdl,tvsubtitles,moviesubtitles`) stand in the page, and the fallback is bound to `if (!liste)`.

**T365:** What a card says about a field (F-M702, F-M745). **4 checks** in `sources`. A help text is one line, with no line break. No help text is longer than 80 characters. An address travels only as a pair of link word and address, and that address is `https`, with its link word standing verbatim in the line. The ban on help texts was lifted by the operator: "too much talk" meant short, not silent. The check therefore guards the limit instead of the silence. Everything else of F-M702 — no placeholder, no `set` line, the light as the summary — is checked where it stood.

**T366:** The order stands under Endpoints (F-M703), in `gui-structure`. **1 check**: the block `SourceOrderBlock` lies in the section between the heading `Endpoints` and `Media files`, **before** `SourceCards`, and no longer in the General tab.

**T367:** A secret field carries neither a placeholder nor a `set` row (F-M702), in `gui-structure`. **1 check**: `set — type to replace` and `subdl-source-secret-state` do not occur in the page, but `input.placeholder = ''` does.

**T368:** The kind of a source and its light arrive (F-M704), in `gui-structure`. **3 checks**: the capabilities list stands **once** and names `servesSeries`/`servesMovies`, a secret answers the condition of another field with `set`/`not set`, and every row of the order carries kind and light.

**T369:** Only the movement is saved (F-M705), in `gui-structure`. **1 check**: the body arises from the difference, `nothing changed` stands ready as a message, and the old call that always sent all fields no longer occurs.

**T370:** Light, arrows, required mark, dot pattern and feedback (F-M706), in `gui-structure`. **5 checks**. The light stands only in the order (no `subdl-source-state-row`), the arrows hang on `margin-left: auto`, the card builds no `Required` mark, the secret field sets `••••••••`, and the feedback names `on`/`off` per moved switch. Check F-M700 moves along (it demanded the row in the card).

**T371:** Endpoint order without media kind, light centred (F-M707), in `gui-structure`. **2 checks**: the heading reads `Endpoint order`, a row no longer builds a `subdl-order-tag`, and the light stands via `flex: 0 0 3.2em; text-align: center` in its column. Check F-M704 (c) moves along: it demanded the kind in the row.

**T372:** Feedback and light (F-M708), in `gui-structure`. **2 checks**: the message builds `<Beschriftung> on|off` and chooses `fail` with ✖ on switching off, and the light of the order reads `participates` and is redrawn after saving.

**T373:** Every switch has an effect (F-M709), in `sources`. **9 checks**: with all switches on, three sources and both pack sources run in the slot; `downloadEnabled=false` really switches moviesubtitles.org **and** tvsubtitles.net off without dragging the other source along. `packsEnabled=false` takes SubDL out of the slot and names it as the reason; `reportEnabled=false` takes the report path; `uploadEnabled=false` switches the tvst upload off.

**T374:** The block names only its name (F-M710), in `gui-structure`. **1 check**: the sentence "Asked in this order" no longer occurs in the page.

**T376:** moviesubtitles.org declares both directions, the upload is off (F-M712), in `sources`. **2 checks**: the field `uploadEnabled` with default `false` beside `downloadEnabled`, and the badge row of the card reads `uploadEnabled, downloadEnabled` (before only the download).

**T377:** A quota takes only the one source out of the run (F-M713), in `sources`. **5 checks**. No `_stopRun` in the download path any more, `MerkeQuelleTot(Subdl)` in the 429 branch, `continue` instead of `return` on failure of the main search, `SourceAllowed` with `_quellenAus` and the end of "stopping upload run early" in the upload path.

**T378:** Every failure reason takes only the affected source out (F-M714), in `sources`. **2 checks**: "upload stopped" (403) and "run stopped" (hourly budget) no longer occur in the upload path, and both replacement messages name the rule in plain text.

**T379:** A timeout takes the source out for the rest of the run (F-M715), in `sources`. **1 check**: the timeout branch of the upload enters the source into `_quellenAus`, names F-M715 and lets the other source continue.

**T380:** A vanished row does not end the cleanup run (F-M716), in `seasonpack`. **2 checks**: `DeleteMany` skips `EntityState.Deleted`, and `SaveChanges` catches the `DbUpdateConcurrencyException`, ticks the row off as done and repeats the write.

**T381:** The repetition after a vanished row runs in a loop (F-M717), in `seasonpack`. **2 checks**: `RetryAfterVanishedRows` is called, and the loop (`runde <= 200`) writes one row per round with count and round number.

**T382:** TMDb stands as the first light (F-M718), in `gui-structure`. **1 check**: the five status rows lie in this order inside `#ScribeStatusBox`.

**T383:** The pack switch belongs to the source and means both directions (F-M719), in `seasonpack`. **3 checks**: the configuration field has dropped out, the upload page carries no checkbox, and `UploadPipeline` packs on `PackSlotSources().Count > 0`. One check each runs too, in `gui-structure` and `endpoints` that the checkbox does not come back.

**T384:** One rule, n sources (F-M721/F-M722), in `sources`. **3 checks**: the decision body runs over the list and names no source, `TargetsFrom` asks every engine of the registry. Four or five targets run unchanged (a known origin locks all, a mark locks exactly one). The origin is asked exactly **once** per decision and accepts no target.

**T385:** A dropdown value takes everything about a file (F-M723), in `endpoints`. **1 check**: `what=all` calls `ForgetMedia`, this deletes all track rows and loose subtitles and the marks **before** the rows, and the dropdown offers the entry.

**T386:** The expert block is one sentence (F-M723), in `gui-structure`. **2 checks**: the entry "everything of this file" stands in the dropdown, and the three old explanation paragraphs are gone.

**T388:** A cancellation is not a fault (F-M726), in `endpoints`. **1 check**: the read-back body catches `OperationCanceledException` with `ct.IsCancellationRequested` **before** the general catch, writes a detail line and passes the cancellation on with `throw`.

**T389:** The reset reports its findings (F-M727), in `endpoints`. **1 check**: return with `FoundTracks`/`FoundSidecars`/`Known`, loose subtitle search via the path stem (`GehoertZurDatei`), `false` on unknown file, and the three fields in the answer.

**T390:** The reset deletes via `Remove(entity)`, not via an identifier (F-M727a), in `endpoints`. **1 check**: `Remove` for tracks and loose subtitles, and the old `Delete(TrackId)` call no longer occurs.

**T397:** The series identity stands before the season verdict (F-M737), in `seasonpack`. **6 checks**: there is exactly one place that checks the identity; the grouping calls it before it forms the season key. The call lies in reading the library, not behind it; the inquiry uses the year from the file name (F-M231); a correction is named with title, year and both identifiers; and the memo holds per run (`_packIdentity.Clear()` at start).

**T399:** The reset forgets season verdicts without triggering an upload (F-M739), in `endpoints`. **4 checks**: the range `pack` exists and calls `ClearAll` or `ClearSeason(imdb, season)`. Without an identifier all verdicts fall, with an identifier only one series; log line and answer carry the number; and the `upload` branch stays untouched.

**T400:** Take a season completely out of the plugin database (F-M740), in `endpoints`. **6 checks**: the purge counts ten tables; the children fall before the parents; `dryRun` deletes nothing. Refusals are found via the media hash and verdicts via the season prefix; the reset offers the range `season` with `dry` while the plugin database is appended; and no query accesses the unmapped `FileId`.

**T401:** The language inquiry decides before collecting (F-M741), in `endpoints`. **4 checks**: the collector divides the sources without packs into "does not carry the language" and "collects". If none remains, nothing is collected and the reason is named; the `HasPack` gate applies only if really every source has the pack; and the single path asks the same inquiry before extraction.

**T402:** The light speaks English and does not repeat the bars (F-M742), in `endpoints`. **4 checks**: the SubDL light names no quota numbers. The reachability light says `reachable` and carries the code only when needed; the login light names the three cases in English with `OK` as success; and no German message text stands in the status path any more.

## 13. Tests of logging, status and transparency (§15)

**T26:** The counters live in the database, reset with it, and are adopted from the XML exactly once (F-M207)

**T28:** A run that leaves on an exception tears its watchdog down (F-M209)

**T34:** The reject counters, download and upload, persist, reset with the volume counters and render as numbers (F-M218). The type and year counters were withdrawn on and must NOT reappear in any carrier.

**T40:** A `Normal` run counts exactly like a `Debug` run (F-M225)

**T41:** Every plugin line carries its level marker at every setting (F-M226)

**T62:** One message produces exactly one log line at every level (F-M245)

**T65:** A dry run contributes nothing to any of the six counters (F-M247)

**T72:** Every discarded candidate and stream is named with its reason (F-M255)

**T80:** A media container rewrite is reported at `Normal`, the per-track detail at `Verbose` (F-M262)

**T83:** The stored last-run rows survive a restart and are read from the database (F-M265)

**T85:** The waiting row reports the cycle's fate in the right colour (F-M267/F-M268)

**T86:** The refresh detail line names a rebuild fallback without turning the light red (F-M269)

**T87:** The configuration page's structure and wiring hold without a host (F-M270–F-M273)

**T96:** Every path that fetches and then discards a candidate increments the reject counter, the count reconciles with the day's spend (requests = saved + rejected), and both the download and the upload run line print it (F-M286)

**T102:** An arrival cycle updates the Download, Upload and Seeder rows together. Each direction's row carries that cycle's fate: green on work done, yellow on a quota stop, red on a failure. A direction that is switched off is recorded grey/`disabled` rather than green. A scheduled cycle leaves the arrival path out of it entirely (F-M289)

**T103:** With no library selected, a seed step leaves a grey `skipped`/"no libraries selected" seeder row, logs `No libraries selected — seeder not started`, holds no run lock and does not walk the library, neither on a scheduled run nor on an arrival (F-M290)

**T106:** A Jellyfin restart during a run records `ok`/"restart" for the affected worker, not `cancelled`/"user stop". No stop marker exists, so neither the task catch nor the in-cycle path reports a user stop, and both directions keep their own fate. A real stop through the endpoint still records `cancelled`/"user stop" for the direction it names (F-M293)

**T107:** Every worker holding a pending deferred fire shows a yellow `defer` lamp and its own line under the Workers list naming the worker, its stored cause and the moment the fire is due. The fire overrides `ok` AND `cancelled` — a direction with a pending fire is never shown as cancelled — while `failed` and `run` are left alone. The line is gated on the fire alone, so it survives a restart and disappears once the fire is consumed. The seeder never appears (F-M294)

**T158:** Off and manual are named on the status line (F-M352).

## 14. Tests of the test discipline itself (§19)

**T242:** "5xer" are no upload candidates (F-M519), in the suite `endpoints`: `IsOriginalSidecar` refuses the slots **51**, **52**, **50** and the variant with `sdh` marker and slot **99**, while the corrected block (**01**, **49**, without slot) remains a candidate. The candidate list from `FindLooseSrts` then contains **no** entry from the original block. **3 checks**, all in the suite `endpoints` (179 → 182).

**T243:** The interface shows the tvst family as **one** source with **one** switch per direction (F-M520), in the suite `gui-structure`: in both tabs stands exactly one box (`TvstUploadEnabled` or `TvstDownloadEnabled`) and **none** of the four old address boxes. Heading "tvst" exactly once, and one sentence names both addresses and "TMDb decides". Saving writes **both** address fields (`tvstUp`/`tvstDown`), loading reads the effective state of both. **8 checks**, all in the suite `gui-structure` (318 → 326).

**T244:** The tvst family logs its inquiries (F-M521), in the suite `endpoints`: the inquiry line names method and path **without** query, and the answer line repeats the path with identifier. `SiteTrace.Answer` reads as `HTTP 200, 3 hit(s)` or `no answer, 0 hit(s)`. Both clients carry a `DebugTrace` event and `LastStatus`, and **both** pipelines hang on it (`_tvClient`/`_msubClient` on download, `_tvstClient`/`_msubClient` on upload). **5 checks**, all in the suite `endpoints` (182 → 187).

**T245:** The series list is fetched once per run (F-M522), in the suite `tvsubtitles`: two `ShowsAsync` calls yield **one** inquiry and the same list. An **empty** answer is not stored (two calls, two inquiries). After a **real** `AddShowAsync` the plugin database falls (list, POST, list = **three** inquiries, so that the new number is seen). **3 checks**, all in the suite `tvsubtitles` (76 → 79).

**T246:** The download writes the tvst series number (F-M523), in the suite `tvsubtitles`: a search over the page images of The Middle reports `LastResolvedShowId` = **512**. A following search for an unknown title sets it to **null** (no leftover value). The download pipeline uses `SetTvstShowId` for writing, reads the stored value beforehand via `TvstShowIdAtPath` and is covered by `!_config.DryRun`. **3 checks**, all in the suite `tvsubtitles` (79 → 82).

**T247:** The cycle's fault path logs the exception with stack (F-M524), checked in the suite `counts`/`spec-doc` environment via the source text of `ScribeEventDispatcher.cs`: the site `_logger.LogError(ex,` stands there, and `MarkCycleFailure(ex.Message)` before it is preserved. **1 check**, suite `endpoints` (187 → 188).

**T249:** The source badges (F-M526), in `gui-structure`: each of the six SubDL-only controls carries `src-subdl` in its surroundings (window around the control, because on number fields the badge stands in the label **before** it). The three CSS classes are present, and the text reads `SubDL only`. `src-tvst` occurs **once** (only as a class, no badge set: there is no tvst-only case). **10 checks** in `gui-structure` (321 → 331).

**T251:** Leniency when reading foreign numbers (F-M528), in `seasonpack`: a hit with `"season": 1.7`, `"episode": "3"`, `"episode_from": 99999999999`, `"framerate": 1e3` and `"download_count": {"n": 5}` does **not** throw, and delivers 1 / 3 / `int.MaxValue` / 1000 / 0. The source text goes via `JsonAnomalies.GetInt`, and the batch catches `FormatException` per hit. **F-M529:** the hit is discarded (`LastRejectedByFormat++`, `LastJsonMismatches = befund`) and the number reaches the closing line via `summary.RejectedCandidates`. **12 checks** in `seasonpack` (203 → 215).

**T252:** The field-shape table (F-M530), in `seasonpack`: a documented entry, including `"fps": "23.976"` as text, yields **0** findings. An entry with `"season": "3"`, `framerate: 23.98`, `"hi": "true"`, `author: ["a"]` and `unpack_files[0].season = 1.5` yields **5**, each named. `unpack_files` as text instead of array is a finding instead of a crash. The identity (id, release name, language) is readable from the entry, so that the finding names its sub. **9 checks** in `seasonpack` (215 → 224).

**T253:** The report text (F-M531), in `seasonpack`: five lines, first line `Metadata format mismatch.`, second names attribute with documented and received shape, third the sub with release name and language, fourth empty, fifth the origin. Category `wrong_release_info`; and the text contains neither `because` nor `please`. **5 checks** in `seasonpack` (224 → 229).

**T254:** The field-shape check of the remaining answers (F-M532), in `seasonpack`: movie block reports `imdb_id` as number. The quota answer reports `search.limit` as text and `downloads.used` as decimal (nested, path in the finding). An entry of the own list reports `subtitle.id` as text, the upload answer reports `subtitle.subtitle_id` as text, and a finding from the movie block reaches the same reporter (`OnMismatch`). **5 checks** in `seasonpack` (229 → 234).

**T254b:** The stub in the upload tab (F-M534), in `gui-structure`: `<h3>tvst</h3>` no longer occurs in **either** of the two tabs (before, the same place checked it for **exactly one**). Same check number (338), only the expectation turned.

**T255:** Robustness and report path of the tvst site (F-M533), in `tvsubtitles`: a documented entry yields 0 findings; unknown language code, `WEB-DL` as rip and a season/episode outside 1–22 / 0–100 each yield one finding, an empty rip **not**. The report text goes via `/report_bad.php` with `sid` and `message`. **7 checks** in `tvsubtitles` (82 → 89), plus **1** in `reporting` (82 → 83: the label no longer names a source, and the old wording must not come back). Plus in `gui-structure`: the report switch no longer carries a source badge and reads `<span>Report broken subtitles</span>` (**2 checks**, 336 → 338).

**T256:** The order flush → detach (F-M535), in `reporting`: in the pipeline's source text `FlushReportsAsync(summary, ct)` must stand **before** `_api.DebugTrace -= OnApiTrace` (**1 check**, 83 → 84).

**T257:** Legacy code pages on fetch (F-M536), in `packretry`: the same SRT line as **cp1256** (Arabic) with `ar`, as **cp1251** (Bulgarian) with `bg` and as **cp1253** (Greek) with `gre` yields the **readable** text each time and no `U+FFFD`. The same cp1256 bytes **without** language indication still yield `U+FFFD` (nothing is guessed). A **UTF-8** payload with `ar` stays UTF-8, because strict UTF-8 applies first. **5 checks** in `packretry` (128 → 133).

**T258:** The client on every tvst inquiry (F-M537), in `tvsubtitles`: `SiteClientQuery` is `sub-scribe-` **plus** the running assembly version. `WithClient` appends `?client=` to a URL without query and `&client=` to one with query; **every** `HttpRequestMessage` build in the client goes via the helper (`ziel`). And a real search call via the client shows `client=` in the incoming URL. **5 checks** in `tvsubtitles` (89 → 94).

**T259:** The own language set per site (F-M538), in `endpoints`: `AnyUploadSourceStillWants` filters series and movie address each via their **own** table. A language not carried ends as `Skipped("second-source-no-language")` with its own plain text, and the tables are really separate (Hindi on neither site, Arabic on both). **3 checks** in `endpoints` (188 → 191).

**T260:** The early exit for done entries (F-M539), in `endpoints`: it stands **before** `FfmpegTools.ExtractAllAsync`, it asks **both** (no pair wants a source any more **and** all positions terminal), and a switch change opens the entry again. **3 checks** in `endpoints` (191 → 194).

**T261:** The queue asks the plugin database per pair and per source (F-M540), in `endpoints`: `IsUploadedTo(mediaHash, mapped, hi, quelle)` replaces the counting heuristic, the page is asked for its language, untagged tracks and loose `.srt` keep the entry open. **3 checks** in `endpoints` (194 → 197); the existing F-M499 check was pulled onto the new place.

**T262:** The exit for files with only discarded tracks (F-M541), in `endpoints`: it stands **before** `FfmpegTools.ExtractAllAsync`, checks all positions via `EmbedRejectedReason`, and the line stands once per file. **2 checks** in `endpoints` (197 → 199).

**T263:** The TMDb answers in the plugin database (F-M542) — as a **runtime** check in `storex`, not as a source-text search. Unknown question without answer, answer in the same process, answer **after a restart** (new context, empty plugin database). The series switch as part of the key, the stored non-hit as a real answer, a 200-day-old id answer and an 8-day-old non-hit as expired, and the cleanup removes both. **8 checks** in `storex` (35 → 43).

**T264:** The memos in the tvst client (F-M543) in `tvsubtitles`: a synthetic season 9x01–9x05 with a counter per inquiry: the episode page is found, the same question costs **no** second inquiry. A later episode of the same season costs **2** instead of 5 again. After `InvalidateSeasonPages` the client reads the season list anew exactly once, the headings not. **4 checks** in `tvsubtitles` (94 → 98).

**T265:** The media kind at the file header (F-M544) in `endpoints`: the header reads it from the item (`Episode`/`Movie`), and an unknown type keeps `null` and thus the cautious reading. The dry run stays at exactly one place without media kind. **2 checks** in `endpoints` (199 → 201).

**T266:** The wiring of the TMDb plugin database (F-M545) in `endpoints`: the attachment stands at exactly **two** places (RecreateDbContext and the getter `SharedDbContext`), and the plugin database fetches a missing context itself. **2 checks** in `endpoints` (201 → 203).

**T267:** The queue per source (F-M546) in `endpoints`: the seeder carries it (`UploadBySource`, `UploadTodoSources`, entry per source), and the run limits its sources per item (property, field, `QueuedForSource` in the source list **and** in the gate). Nothing is omitted without a report, and the scan row breaks down the numbers. **4 checks** in `endpoints` (203 → 207).

**T268:** The limits (F-M547) in `endpoints`: the `PacingHandler` sits in **both** HTTP chains and **inside** (every attempt counts). The pause comes **after** the answer and has 0.5 s default, 0 disables it, the transfer rhythm hangs on `itemUploadedThisRun` (exactly one call site), and the download search no longer pauses before the inquiry. **5 checks** in `endpoints` (207 → 212).

**T269:** The download rhythm (F-M548) in `endpoints`: exactly **two** pause places, both **after** the deposit (comment anchor per place), no pause in the 400-character window before the fetch any more, and no pause in the window around the dry-run path. **3 checks** in `endpoints` (212 → 215).

**T270:** The sources per item (F-M549) in `endpoints`: the item carries them as names and the seeder sets them. The distributor builds the card **from the queue** (exactly two calls: after the merge and after loading), the assignment from the scan is gone, and a missing indication falls back to "all sources" instead of guessing. **3 checks** in `endpoints` (215 → 218).

**T271:** Stamp and source name in the log (F-M550) in `endpoints`: the upload row names the answering source (`QuellName(result)`, set in the chain, field on the result). No stamp in upload/download/seeder starts with `[SubDL` any more, and an unknown source is called `no source answered`. **3 checks** in `endpoints` (218 → 221).

**T272:** The source in the download row (F-M551) in `endpoints`: the `SAVED` row carries `{Source}` and fills it from `cand.Provider`, and both HI rows (own version and as main track) carry it from `hi.Provider`. **2 checks** in `endpoints` (221 → 223).

**T273:** The refresh on merge (F-M552) in `endpoints`: a known item gets the seeder's fresh source list, and an empty answer overwrites no known one. **2 checks** in `endpoints` (223 → 225).

**T274:** The header question of F-M554 in `endpoints`: the pipeline passes the real tracks along, and the mark applies only if every present track is done (the fallback without positions remains). **2 checks** in `endpoints` (225 → 227).

**T275:** The TMDb plugin database of F-M555 in `endpoints`: the handler exists and stores or reads, it touches only `api.themoviedb.org`, the key carries no `api_key` and the prefix `url:`, and it sits in **both** HTTP chains. **3 checks** in `endpoints` (227 → 230).

**T276:** The shared loose subtitle rule from F-M556 in `endpoints`: it exists as `SidecarsNeedWorkFor` and is called by both places (no second copy any more), and the header skips only on `!seitenwagenOffen`. **2 checks** in `endpoints` (230 → 232).

## 15. Tests of the source contracts (§22)

**T328:** The tvst season pack (F-M641/F-M642), in `tvsubtitles`: `BuildSeasonPack` writes into the season slot (`episode=0`), the pack name stands in `release`, the comment names the season instead of the episode and carries neither version nor client name. `hi` goes along only if the whole pack is HI, `double` never. The field order is that of the form, a non-zip and a missing pack name are rejected, the client reads the episode list from the entry names in ascending order and without duplicates, and the body carries the zip unchanged. **8 checks** in `tvsubtitles` (97 → 105); plus **4** in `endpoints` (the pack is rejected when `UploadSeasonPacks` is off, built with the switch on and not sent, a non-zip and a missing pack name are rejected, 388 → 391).

**T329:** The pack row per source (F-M642), in `storex`. The key names the source, and a call without a source stays on `subdl`. Both sources keep one row each for the same season, and each row keeps its own pack identifier. A row in the old format, column missing, key without a source part, stands after the next opening under its source key and carries `subdl`. **6 checks** in `storex` (11 → 17).

**T330:** The tvst season pack in the run (F-M643), in `seasonpack`. The collector hands the pack to a dispatch of the second source; the pack is really built, and the site's **real** zip reader finds every episode in the entry names. The entries carry exactly the episode file names (with season, unlike the SubDL members). The ticket stands in the season slot with `SeasonPack=true` and carries the existing pack switch of the GUI along. `hi` describes the whole pack. It is built only in the **full** run and only with the source released, and a tvst pair counts as the one row of the season (F-M640). **12 checks** in `seasonpack` (235 → 247).

**T331:** The remembered pack rejection (F-M644), in `storex`. A rejection stands in the row with reason, date and fingerprint, and a rejected pack carries **no** evidence (`UploadedAt` null). A refusal by one source creates no row of the other. Changed content gets a different fingerprint. A pack that went through clears the refusal **and** sets the evidence. A second language of the same season is rejected on its own row.

The single-file reset of the GUI (F-M620) lifts the season's refusal, while an unknown hash lifts nothing. A rejected row is readable but does not count as a pack, and the three columns stand in the table (also pulled onto an existing file). Plus the pack card per source: the tvst plan does not read the SubDL pair as "packed" (F-M643). **13 checks** in `storex` (49 → 62). Plus **4** in `packretry` (the coordinator compares the remembered refusal before the fingerprint memo, writes the source's duplicate answer with reason and fingerprint, `HasPack` answers "no" for a rejected row, and both pack cards leave it out: 133 → 137).

**T332:** The one slot per pair (F-M647/F-M648/F-M649), in `seasonpack`. The collector enters the members **once** for both sources (eight episodes, not sixteen), and one plan carries both name forms (SubDL members without season, tvst entries with season). Each source gets its own verdict from **the same** plan without collecting again. A content verdict (mixed sources) falls **once** per pair with **one** row.

The hourly budget writes **no** verdict and **no** rejection and counts nothing as a failure (`LeftOpen`, "hourly budget spent"). And the pipeline carries **one** pair loop, **one** collector, **one** slot with `Task.WhenAll` and the 0-5 % offset, while the second pack phase including the second planning has disappeared. The one row per pair names both reasons. **7 checks** in `seasonpack` (247 → 254).

**T333:** The rejection gate before collecting (F-M650), in `seasonpack` with a **real** plugin database: a refusal carries the signature it rests on; `PackRefusalStands` answers "yes" only on **the same** signature (a different and an unknown signature give "no"). With an unchanged signature **no** episode is collected (the filler is asked **0** times, the plan is null) and **exactly one** normal row arises, which names pair, source, reason, date and "signature unchanged" and drags no second row of the pipeline behind it (`LastSkipReason` empty). With a **different** signature collecting resumes (8 calls); the GUI reset clears the signature together with the refusal. And a pack that meanwhile exists wins against the refusal (`HasPack`, one F-M647 row, no F-M650 row); plus the wiring: the coordinator's refusal call really passes the signature. **9 checks** in `seasonpack` (254 → 263).

**T387:** One fold for every name comparison (F-M724, F-M736), in `textfold`. **31 checks**: the fold reads bold, small caps (`ꜱ01ᴇ05`), fullwidth (`Ｓ０２Ｅ０７`, also the ending), circled, squared and super-/subscript digits. Modifier letters remain **and** are not reported as foldable. An unstyled name comes back as **the same instance**.

The member search finds `ꜱ01ᴇ05` and does not hand out E6 for it. `ⒶⒷ` folds to letters but names **no** episode (refusal documented). The unpacker finds a styled archive entry via its plain-text name, an unknown name still as `null`. `ReadSeasonArchive`/`EpisodesInZip` read fullwidth tvst names and keep the original name.

The media parser reads the styled name **under `InvariantGlobalization`** (the old `Normalize` path did nothing here). And **one** source-text assurance: `MediaNameParser` no longer calls any Unicode normalization.

Plus **six** checks of the one input normalization (F-M736): it folds, trims and collapses inner whitespace. Empty input stays empty and never null. If there is nothing to do, the same instance comes back. `Same` normalizes both sides, so that spelling and style do not count but content does. Applying it twice changes nothing. And parser, tvst archive and SubDL member search really call this one function.

**T392:** Account maintenance as the second, narrow contract (F-M729), in `sources`. **7 checks**: the contract carries exactly its three tasks and stands in its own file among the narrow contracts (the contract of the four jobs does not name it). The built registry offers it only for the source with account. The login carries account and password from the source's settings file (measured at a holding handler). The source reads the settings of its file and does not know the general configuration. Follow-up and diagnostics call the contract and no client; and the diagnostics ask page by page. The statement per check stands in the suite.

**T393:** The inquiry as the third, narrow contract (F-M730, F-M733), in `sources`. **7 checks**: the contract carries its name and its two tasks and stands in the file of the narrow contracts. All three sources report themselves; a source without account reports "reachable" with its status code and its tile is called `tvst`. The inquiry of the source with account asks with the key from its settings file; the answer names neither key nor address. The status page asks the registry and builds no own connection. And outside the sources no file reads an access datum from the general configuration: the quota takes it from the source file.

**T394:** The flow without source knowledge (F-M731, F-M732, F-M733), in `sources`. **6 checks**: the short name comes from the capabilities (the series address carries `tvst`, the others fall back on their key). No branch after a source name when labelling any more; both coordinators ask the capabilities. The lists come from account maintenance (no call of the submission lists any more). The LIVE quota comes from the inquiry (no call of the client's quota reader any more). The registry asks only the source that promises a quota, measured at a holding handler, one inquiry with the key from the source file. And a source without account answers "I carry no quota".

**T395:** Title creation in the send job (F-M734), in `sources`. **5 checks**: the job carries the title question and the dry run, the answer the title. Only "created"/"ready" means ready, and only "without identifier" means decided. A source that finds titles via its identifiers answers "not askable" instead of inventing a title. Both lookup paths are plugin database-free and answer in the neutral states. Both sides create in the job and pass the dry run through. In the dry run there is no verdict.

**T396:** The language inquiry of the source (F-M735), in `sources`. **5 checks**: each of the three sources yields an inquiry and names its code for `DEU`. The two sites carry a supported-languages statement, the third carries **none** (`null`, not empty). A language that a site does not carry is denied, while the open source and an unknown key never deny. The return path delivers the plugin database's ISO identifier (`gr` → `GRE`, `cz` → `CZE`, `jp` → `JPN`). The unknown yields `null`. And in `Pipeline/` as well as `ScheduledTasks/` no file asks a client for its language chart any more.

## 16. Tests without a named requirement

**T1:** Episode with mixed-language text subs and one wrong stream tag

**T2:** Movie with bitmap subs only (PGS) is ignored

**T3:** Corrupted subtitle stream in a pack passes to the QA reject

**T4:** Mini sub below 2 KB and 30 cues hits the size gate

**T5:** Series without a series IMDB is skipped as "no-imdb"

**T6:** Skip-dir and filename pattern hits are skipped

**T7:** An already uploaded file is not uploaded twice

**T9:** The same episode in two library copies is recognised by content hash

**T10:** Rate-limit timing stays inside the target rate and the jitter band

**T99:** The candidate walk's exits are reachable and each is the one that applies: it stops at the first CORRECTED file, and it stops when the download budget is spent.

**T104:** The upload binding binds the SCHEDULED run only: with upload switched off no anchor is armed, while BOTH manual routes still work, the dashboard button and the job's own endpoint, neither of which carries an upload gate any more.

**T105:** With a deferred fire pending, the direction's worker row is painted yellow (`defer`) even when its last run ended `ok`, and it returns to green once the fire is consumed. A red, grey or running row is left untouched, and the stored outcome underneath is unchanged.

**T111:** End-to-end against real audio, driving the FEATURE rather than a copy of its arithmetic. The sign of the correction is established on the material itself, by planting +5 s and applying BOTH directions. The direction landing within 0.7 s of the plain subtitle is `−detector`, and the check asserts that, so a sign flip fails here instead of doubling every corrected file. A subtitle already in sync comes back `applied=False`. The check measures and removes a shift planted at +2.5 / −1.8 / +6.0 s, with the corrected text lying within 0.7 s of the plain subtitle by MEDIAN offset, a file that took no part in the measurement.

**T112:** Driving the registry decision itself, not a copy of its arithmetic. An `uploaded` row survives a `duplicate-content` skip: after the identity move that the language-tag gate triggers, a skip over the same content leaves the row `uploaded` with its reason cleared and increments no reject. The same skip over content that is NOT up still writes `rejected` with its reason intact, so the two cases are told apart rather than both being called settled.

**T113:** Driving the rendered page text, not the markup. Every intro block under a section heading measures at most **300 rendered characters**, with tags stripped and whitespace collapsed, and carries **no measured value**. The ban list is the forensic vocabulary itself: `Measured`, a before/after arrow, an episode count, an accuracy figure, so re-introducing "Measured on 36 drifting episodes: worst line 10.74 s → 0.17 s" fails here rather than passing as prose.

**T114:** The staircase correction, driven on the SAME synthetic episode whose cues were built from a known burst list, and judged against that burst list. The ground truth that took no part in the measurement, because a correction scored with the detector that produced it is the exact inverse of its own measurement and always reports success.

**T115:** The specification's structure, checked without a build or a host. Every Contents counter equals the number of definition lines in its section, sub-headings and superseded entries included. Every requirement id is defined exactly once, the test numbering is gapless from T1, and every test a requirement names exists as a definition. The header status names the version `build.yaml` builds.

**T116:** The correction section's wiring, driven against the page file and the built DLL: exactly ONE checkbox in the section. Its id present in the markup and bound on load (default-on aware) and on save. The four absent ids (drift check, drift refusal, the per-language audio-track check, the anchor sync check) ABSENT from the markup and from the inline script. The configuration page as an embedded resource, so the page actually ships inside the DLL. And the automatic sync check defaulting to `true` in C#.

**T117:** The staircase scored against REAL material with the AUDIO as the only yardstick: no subtitle takes part, neither embedded nor as a loose subtitle.

**T119:** The fit is scored per cue against a **planted** truth on a file the operator has confirmed as good. Staircases are planted (a three-step staircase, a fine multi-step staircase in the opposite direction, a reversal, and an untouched control) and every applied correction must land within the tolerance of the planted value.

**T122:** The streaming decoder is asserted as two ABSENCES on the plugin source, because neither shows up in a log line and both would return silently. The materialising decoder does not exist (kein `ms.ToArray` and no `new float[…]` on the audio path: it held a 514 MB `byte[]` and a 514 MB `float[]` at once, and the OOM killer took the 140-minute files), while the streaming entry point exists. And **nothing persists the level curve**: no file, no cache, no database column, and no curve-shaped field in the data model.

**T123:** The statistics table renders one ROW per counter with the label and its number in two bounded columns, and every row names the direction its count belongs to where the data is per-direction.

**T124:** The fit's logging split, asserted on the source. The run START line names the fit switch (`audio fit=`), the run DONE line carries the fit counter (`fitted to audio`), and the per-file track line is emitted only under the fit switch and only at Verbose.

**T125:** The alignment time is credited against the transfer pacing, with a floor at zero, driven against the real limiter. An unbooked credit returns the jittered pause. A credit above the pause lands on exactly 0 and never negative (asserted at two rates, because one base value would make the bound look like an artefact). A partial credit pulls the pause under the ceiling that credit implies. The credit is spent once and not carried. Zero and negative bookings are discarded, asserted through the pause because a negative booking would ADD to it.

**T126:** The seeder's language-code counter reaches the statistics row and survives all four paths it must cross: row field, writer, reset, API, plus the GUI row that displays it.

**T127:** The loose-subtitle counter reaches the statistics row and the dry run does not rename.

**T129:** The loose subtitle name carries its slot as a two-digit number, always, and the number is what orders the track list.

**T130:** The slot tells the two kinds of downloaded file apart, and the two ranges are counted separately.

**T131:** The download repeats until a correction proves itself, capped at the QA retry limit.

**T132:** The QA retry limit bounds the fit and nothing else.

**T133:** The refusal's TEXT is pinned on the real builder with one synthetic fit per cause, so the four shapes cannot collapse back into one. The thin-gain case must carry `t = +1.71 < 2.0`, the score pair and delta, the segment offsets, `887/887 cues` and `largest 2.50 s`. `nothing to move` must name the cue count; `the correction would make it worse (untreated better)` must name both scores, and must NOT print the `t < 2.0` comparison, which was not the condition that refused it. `below floor` must name the floor it missed (0.7 s).

**T134:** The auto-sync has its own worker row and its own light, and the statistics count only alignments that happened.

**T135:** The auto-sync is a worker with a contract, subtitle in, status and output back, and the download run keeps the files and the counters.

**T136:** The configuration page carries its JavaScript exactly ONCE.

**T137:** the plugin database is SQLite through the host and the engine rides along in no ZIP.

**T138:** the plugin database runs in WAL, and every file-level copy of it is checkpointed.

**T139:** A version that meets a leftover document plugin database does NOT import it and does NOT delete it.

**The same harness then drives the three paths the operator reaches for once an import has landed: DATABASE REFRESH, RESET and RESTORE, because the plugin database suite covers them against a SEEDED plugin database, and a seeded plugin database is not what an import leaves behind.**

Every section works on its OWN COPY of the imported file. The refresh prunes rows and the reset deletes them, so a shared directory would let one section's housekeeping decide whether the next has anything to assert, and a suite whose green depends on the order of its own sections proves nothing. The REFRESH half needs no Jellyfin host. The harness drives it through the same entry points the task calls (each tracker's dead-item prune, the oshash and loose subtitle sweeps), with a fixture the harness owns so both directions are provable. A DEAD item loses its search stamp, its retry budget and its burned candidate, while a LIVE item keeps all three **with its value**.

The file side keeps the verdict of a subtitle that exists and forgets the one whose file is gone. And the suite asserts the FAIL-SAFE rule from both sides: an unusable root (missing, and an empty list) prunes NOTHING even with a dead entry planted and waiting, because the dangerous outcome is not a missed prune but a false one on an unmounted volume. The RESET half drives all three scopes with a completed fixture, since a reset tested against a plugin database with nothing to clear proves nothing. `upload` clears every marked row and no embedded row, `download` removes every downloaded loose subtitle while an observed one survives AND clears the retired `file-retry:` rows (the only access to these orphans (F-M60)). And `all` runs the file-replacing sequence and must leave an empty but usable plugin database with no side file behind and a backup that opens ON ITS OWN with every row the plugin database held.

The RESTORE half mutates the plugin database first: two rows deleted, one planted, counters cleared, the three counters all set to 999. It then asserts VALUES, not presence, because a restore that keeps the old rows is the exact silent failure the pool caused. It also plants a foreign `-wal` beside the database and requires the restore to clear it. And it carries the negative control that gives the whole path its meaning. An OPEN context keeps reading its own generation across a file swap, which is why the plugin disposes and drops the pooled handles first: with those two calls withheld the plugin database still answers with the old generation.

**The order is asserted against the plugin's own source, not only against the harness.** The sections above drive the sequence as this file writes it, so they would stay green if the reset code changed its order. A separate check therefore reads the source and requires it: in `Restore`, the side files are cleared and the pooled handles dropped BEFORE the backup is copied over; in the `all` scope of `Reset`, the checkpoint precedes the backup and the backup precedes the delete. Two mistakes are invisible in behaviour and would otherwise pass: a backup taken without folding the journal looks valid and is missing the recent writes, and a restore that leaves the old `-wal` in place lets SQLite replay foreign pages over the restored file.

**Isolation is also proven, and the guard is tested where it is actually reached.** A check plants the old document plugin database BACK beside the populated SQLite file, the case a botched upgrade leaves behind, and the one the rename in the import path never exercises. It requires the plugin database's per-area counts to be untouched and the old file to still be in place. Because a check that cannot fail is not a check: the source file is located by walking up from the working directory and its absence is a FAILURE, not a skip. A fixed relative path silently pointed nowhere once and the section reported a skip that read like a pass: the measured lesson being that a suite's reachability check belongs to the suite.

**Planted and confirmed RED, one defect per assertion group:** the prune ignoring liveness (the LIVE item's stamp is lost). The oshash fail-safe removed (a dead entry disappears under an unusable root). The restore clearing the side files AFTER the copy, and not clearing them at all. The all-scope reset withholding the pooled-handle drop, and withholding the checkpoint before the backup. And the import's short-circuit removed, which re-imports over a populated plugin database. All confirmed RED, and the harness restores its own baseline to green afterwards. (F-M327)

**T140:** One worker row, one worker's own result, and the download tab in the cycle's order.

**T141:** One lit lamp, for the worker that is working.

**T142:** The IMDb/TMDb match is hard and has no switch.

**T143:** A spent allowance acts like the download switch.

**T144:** A worker row may not claim work the run did not do.

**T145:** The General tab carries everything a run needs: Target languages sits on the General tab, directly below the library selection and above the refetch interval, and is **absent** from the Download tab.

**T146:** The two daily-limit switches read the SAME sentence, and the upload one carries no "API".

**T147:** Every upload quality gate names its default.

**T148:** The Auto-Sync row is reachable while it runs, and closed by whoever opened it.

**T149:** Every number in an Auto-Sync log line is separated by a POINT, whatever the host's culture wants.

**T150:** The compaction refreshes the query statistics.

**T151:** Every SubDL call names its integration and carries the versioned agent.

**T152:** The upload reports the source quality of the subtitle.

**T153:** The filename fallback holds for real SubDL names. The plugin's own parser (reached by
reflection, since the parser is internal), plus source assertions for the wiring. The series route, the
strict title+year route, the confirmation of an id stated in the name, the refusal of a contradiction,
the refusal of an unusable title, the episode call in the resolver and the gate in front of the fold. The
fold checks skip themselves on a runtime without ICU instead of failing, because normalization is a
globalization feature.

**T154:** The arrival follow-up takes its directions from the follow-up **settings**, never from the cycle that happened to be running (F-M177, F-M1a). Asserted structurally on `ScribeEventDispatcher.RunFollowUpRoundsAsync`, because the defect is invisible in the log: the follow-up looked complete while one direction was simply absent.

The two follow-up switches must derive from the follow-up rounds of the download / the follow-up rounds of the upload and must name no cycle direction. Both legs must be guarded by their own flag, and each leg must run its own direction. Download must sit before upload (the F-M177 order), and both switches off must return before the scan.

**Mutation-verified, two plants:** the removed `dir != CycleDirection.DownloadOnly` term (RED by name in two checks) and a swap of the two legs (RED on the order check). (F-M177) **25 checks**, Suite `followup`.

**T155:** The upload filename carries the full content MD5, and both the dry run and the real upload take it from the same helper (F-M30). Asserted structurally on the upload pipeline, because a wrong name is invisible until somebody reads the file list at SubDL.

The neutral name must place `{contentHash}` in front of `{lang}` in BOTH branches, with `.srt` last. The episode branch must keep imdbId/season/episode. Both call sites must go through the helper: one must feed the early content hash (the hash of the bytes actually sent), the other `ContentHashRegistry.ComputeHash(cand.Srt)` (the candidate the dry run advertises). And no hashless inline name may survive outside the helper.

**Mutation-verified, three plants:** re-inlining the dry run's name (RED on three checks), dropping `{contentHash}` from the episode branch (RED on two), and substituting a random GUID for the content hash (RED on two). Such a per-upload id looks like a name fix and is exactly the fingerprint F-M29 forbids.

**T161:** Proves that one archive serves a whole download run. A second pass hits, the per-archive ceiling refuses oversized archives, the entry and byte ceilings evict the oldest, a re-plugin database replaces, an empty body is refused, `Reset` empties entries and counters, and the pipeline resets at both points.

**T162:** Proves the archive key and the disk cache. The entries of one pack share a key, an unusable URL has none, only ZIP bodies are stored. Retention expires and a hit restarts it, the ceiling drops the oldest first, and both switches read 0 as off.

**T163:** The season-pack suite asks the arithmetic and the documented upload shape without a network: the aired set drops the E0 entry IMDb reports, completeness is set equality on both sides, the pack hash is order-sensitive and changes with a corrected episode. The name carries no `.zip` because SubDL appends it, and the pack call sends the array of file ids with `is_full_season=true` and the episode range. **84 checks.**

**T164:** The season-pack suite asks the run plan without a library. A packable season is worked first and in episode order, the queued work outside it keeps its order. An empty plan leaves the order untouched. A season that is only partly queued is not the season that aired and is therefore not buffered, and the plan asks the same completeness rule as the season index.

**T165:** The language-correction suite asks the shipped gate without a library. A lying tag corrects to the detected language, a tag that holds corrects nothing, and a relative of the tag is not corrected. Text without a confident verdict corrects nothing, and the correction only runs behind the language checkbox. No mismatch reject is written any more, while rows from older runs stay readable. The corrected language reaches the candidate the run keeps and the retry path, and the GUI description says what is corrected and where it goes. **19 checks.**

**T178:** the plugin database harness refuses a repository as working directory and a call without directory: it deletes every file in its directory, and a repository root was once emptied that way. **3 checks** (repo marker, missing argument, normal operation).

**T194:** One way to the bytes: fetch the whole archive, put it in the area, unpack the member from it; only the entry id as address; no single-file shape; an entry without identifier is skipped and named. **8 checks.**

**T195:** A season pack passes on to the remaining episodes of the same release: only the episodes from the run's queue, no name comparison against the pack name, no completeness check. Insertion directly behind the served episode, index loop instead of enumeration; and a follow-up candidate of the same episode does not delete the memo. **7 checks.**

**T196:** The run queues the library with "search for new and updated files": only on stored files, once per library, no full scan; the switch before the quality gates can turn it off. **8 checks in the run, 4 in the GUI.**

**T197:** The ranking of candidates follows price and reach: four levels, free before paid, season pack before single version at the same price, a pack without the episode stays out. The cache check goes via the entry id, and the ranking also applies to the follow-up and the HI variant. **6 checks.**

**T198:** A deferral of the seed run is a fire of the direction. It replaces a later fire of the same direction and does not push an earlier one back. It sits in the fire list from which the status line reads its moments, survives a restart with its cause. The status line names the fire's cause instead of the last run's line. No timer in memory carries the wait, and a fire that is being consumed right now names its cause even once its deadline has passed.

The same holds for the three maintenance fires (Database, OSHash, follow-up): they name their cause, a normal arming clears the old cause, and a short marker without a fire is not shown. **29 checks.**

**T199:** The dry run is asserted against the QUEUE, per direction, and the Auto-Sync row against its own start.

**T200:** The dry run is exempt from the DOWNLOAD brake and NOT from the search one. The probe asserts the two halves by INDENTATION: the guard sits one level above the download read and exactly level with the search read, which is precisely the claim.

**T201:** The HI pool is a ranked list, not a single grab. It is walked, a duplicate does not end it, a saved candidate does. The same walk serves the variant beside a saved main track, and it serves the fallback when the regular walk ends without a save. The fallback stands before the "not available" message and counts as saving the language.

Both calls hang on the HI switch, a saved file carries the marker in its name and `hi = true` in the plugin database, its SDH lines stay in place, and the walk orders by price and reach before the text score. **20 checks.**

**T203:** The tvsubtitles.net methods are checked against the pages the site really delivered: 24 language codes of the site (not ISO) including the pairs an ISO table gets wrong, comment line from TMDb and IMDb identifier, an additional text sits behind it.

Rejection is checked for show id 0, season 0/23, episode −1/101, unknown language code, unknown rip, missing identifiers, empty file and wrong extension. The field order is as in the form, **`author` does not occur**, `double`/`hi` only when set, the multipart body is byte-exact with file part and boundary, and a dry run sends nothing and still reports the body size.

A real run goes as `multipart/form-data` to `/add1.php` with `/add.html` as referer, with the creation fields in their order. Plus the parsers against the fixtures: series list, exact name beats "contains", seasons, 24 episode pages without duplicates, `2x24` from the heading, entries with flag language, file name from the JavaScript fragments, and the zip detection at the bytes.

**40 checks**, all in the suite `tvsubtitles`; the live probe against the site is explicitly **no** part of the checks.

**T204:** The seam carries four endpoints and the rest remains: two download and two upload endpoints, one per source. The catalogue yields only what is **switch on** and **included**; tvsubtitles stays out even with the switch on and is named as refused with the sentence of the configuration page.

The SubDL mapping of a candidate onto the neutral hit is complete and invents no rip, and the site's series resolution needs the name. The language bridge knows the site's vocabulary. The tvsubtitles search walks the real pages and finds exactly the wanted language; a missing episode and an unknown series deliver nothing, and the fetch delivers name and unchanged bytes.

The upload dry run builds the body and sends nothing. The season pack is refused with reason when `UploadSeasonPacks` is off, as a non-zip and without pack name; unknown language, missing name and unknown series likewise. The SubDL endpoint refuses season pack, missing IMDb identifier and non-UTF-8 bytes, and without session a readable rejection comes instead of an exception.

Counting happens in T205: the same suite carries both.

**T205:** Both directions are individually switchable and the switches have an effect. With the tvsubtitles switch on, the catalogue yields **both** endpoints of one direction; with download-only exactly the download endpoint, with upload-only exactly the upload endpoint, with SubDL upload alone exactly that one. With all switches on nothing is refused, with the source switch off it is named as `switched off`. The series resolution of the second source still delivers hits only with a name, and the dry run of the upload endpoint sends nothing. Plus the source-text binding: `DownloadPipeline` asks the second source exactly at the place where a language would otherwise be "not available", and fetches foreign candidates at their own endpoint. `UploadPipeline` calls the second source exactly when the switch is on and SubDL has not taken it, both checked as source-text binding, so that a later rebuild does not silently remove the rule. **37 checks**, all in the suite `endpoints`.

**T206:** The origin of a subtitle stands in the plugin database and keeps the sources apart: the plugin database value names the source and can be read back (also `null`/empty as "says nothing"). A **known** origin is refused for **both** sources (tightened: "origin subdl: never back to subdl, never to tvst") and the reason names it (`origin:subdl — downloaded there, never uploaded`). Unknown origin remains allowed everywhere, and the rejection names both sides. The download writes origin **and** the source's id, the upload reads the origin before the endpoint choice, and schema 10 including the `ALTER TABLE` step is present. **8 checks**, all in the suite `endpoints` (counted in T205 together with the cut).

**T207:** The four own identifiers stand in the plugin database and are filled correctly: all four fields are present, and schema 11 adds them in `sidecars` **and** `embeds`. The download writes the field of its source, the SubDL upload writes its id into its own field, and the second source's id travels in the result and lands in its field. The read-backer exists (`FindUploadedIdAsync`) including `SourceId` in the result. A **missing** id is replaced by an own one: `OwnUploadId` always delivers an `own:` prefix, is recognizable as an own identifier (`IsOwnUploadId`), is reproducible on the same input and differs on other content. In the run, only the field of the source that took it is set. **9 checks**, all in the suite `endpoints`.

**T208:** Re-up and cross-contamination are prevented per source: without uploads both sources may run. If tvsubtitles is served, only SubDL remains (and vice versa); if both are served, nothing runs and the reason names both. Cross-contamination is refused before the re-up question, switched-off switches yield `no-upload-source`, and the pipeline asks **twice** through the same gate and no longer uses the old "uploaded somewhere" inquiry at this place. **8 checks**, all in the suite `endpoints`.

**T209:** "Uploaded" stands per source in the plugin database: both timestamp fields are present, and schema 12 adds them in `sidecars` **and** `embeds`. The run sets only the timestamp of the source that took it, `IsUploadedTo` reads the timestamp first (identifier as fallback), and an empty value overwrites no timestamp. **4 checks**, all in the suite `endpoints`.

**T211:** Source-dependent pre-checks. The SubDL key hangs on the SubDL download, the login pair on the SubDL upload, the TMDb key stays mandatory. A cycle without a SubDL download does not read the SubDL counters, and an exhausted SubDL quota does not hold a cycle that still has tvst. **6 checks**, Suite `endpoints`.

**T212:** Provenance at every deposit: **every** `MarkDownloaded` call in the download passes `origin:` (counted, not estimated), and the closing line names `DownloadedWithoutSignificance`. **2 checks**, Suite `endpoints`.

**T214:** The upload walks the chain: both sources in the configured order, and no single target any more (`vonZweiter` is gone, both fields may be set). The chain asks each source's toggle immediately before its start, through **one** helper (skip instead of abort, with a log line), and the provenance gate still decides per source: a forbidden target is skipped with its reason, not the chain ended. **3 checks**, Suite `endpoints`.

**T215:** The dry run asks for no credentials and plays the chain through. `MissingCredentials()` names only the TMDb key for a SubDL upload without a login pair (nothing refused). The tvst branch has its own dry-run guard **before** every call: one line per source (`[SubDL]`/`[tvsubtitles] DRY-RUN would upload`), order as in the chain, toggle before the line, result "skipped", no sending. **2 checks**, Suite `endpoints`.

**T216:** Only the TMDb key is mandatory (if it is missing, the run blocks), a missing SubDL configuration on the other hand skips the source instead of blocking. Upload via the switch **and** `IsConfiguredForUpload` with a log line, download via `IsConfiguredForDownload` before search and fetch, and the dry run does not ask the configuration question. **2 checks**, Suite `endpoints`.

**T217:** The configuration page marks the three SubDL fields as "Required for SubDL only" and the TMDb key as "Required" (the only mandatory one), without new switches and without renaming existing field ids. **1 check**, Suite `gui-structure`.

**T218:** The TMDb block stands in the page **before** the SubDL account block (index comparison of both headings and their fields). The JS bindings for loading and saving still hang on the field IDs, not on the position: the swap changes only the order, no ID, no field, no switch. **1 check**, suite `gui-structure`.

**T219:** Rip mapping (WEB-DL → WEB), call via the mapping, hearing-impaired field, error text without a wrong parameter name. **4 checks**, Suite `endpoints`.

**T278:** The same mapping in `endpoints`: header **and** single-rower call `LanguageMapper.MapToSubdl` before they ask the plugin database, and an unknown identifier is compared raw. **1 check** in `endpoints` (236 → 237).

**T279:** The ISO plugin database in `endpoints`: `LanguageMapper.Canonical` exists, every language-keying registry method normalizes its input, and schema 16 migrates the plugin database and empties the access stamps. **2 checks** in `endpoints` (237 → 239).

**T280:** The settlement per track in `endpoints`: `OpenPositionsFor` asks `IsEmbedTerminalFor` per position (no second verdict space), `PositionSignatureOf` carries position, language and HI, and neither header nor single-rower knows a `SettlementFor`. **2 checks** in `endpoints` (239 → 241).

**T281:** The season inquiry in `endpoints`: `PacksInSeason` reads the pack rows of the season, the planner fetches them via `PacksOf` from the run cache instead of per pair from the plugin database. **1 check** in `endpoints` (241 → 242).

**T282:** The comment in `endpoints`: the ticket carries `EpisodeTitle`, the tvst endpoint passes it on as `commentExtra`, the chain fills it from the library and filters placeholders. **1 check** in `endpoints` (242 → 243).

**T283:** The order of the season inquiries in `endpoints`: the packed season is rejected **before** `SeasonEpisodeIndex.AiredAsync`. **1 check** in `endpoints` (243 → 244).

**T284:** The comment builder in `tvsubtitles` and `moviesubtitles`: three lines with official spelling, missing parts drop out. The movie knows no episode line: two checks in `tvsubtitles` replace the old comment checks (the suite stays at **98**), and the comment check in `moviesubtitles` now carries title and year.

**T285:** The collection cards in `endpoints`: both tables are read once per run, the individual questions have disappeared from the chain. **1 check** in `endpoints` (244 → 245).

**T391:** A remembered no closes the track of its source, in `dupskip`. **7 checks**: `IsEmbedTerminalFor` answers `true` after `SourceRefusalStore.Put` for the refusing source and `false` for the other. A **new** text on the same track (different signature) is asked again. The loose subtitle (position `-1`) closes for the refusing source and stays open for the other. Plus the two old cases from T113 (uploaded mark remains, rejection remains rejection).

**T403:** No document carries a credential, an address or an operator path (NF-10), in `spec-doc`. The check reads every Markdown file under `docs/` plus the README, and a credential value behind `api_key`/`token`/`password`/`secret`, a provider key shape, a hex string of 32+ characters, a foreign e-mail address, a private host address, an IBAN or an absolute operator path is a failure. A state word (`apiKey = set`) and a template (`{key}`) are not. **1 check** in `spec-doc` (8 → 9).

These entries carry no `F-M` reference: they are older than the tagging discipline. 33 of them are
bound to a suite in `scripts/tests/counts.json`, the rest are proven where they stand.
