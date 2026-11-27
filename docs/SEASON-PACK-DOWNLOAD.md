# Season pack download — how an archive arrives, and when it is taken

**Owner: the archive taken in.** One archive for one series, one season, one language: how the run decides to
take it, what it checks before the first byte leaves the source, how it takes exactly one member out
of it, and what it books when the text holds. The other direction — how a pack is formed out of files
on disk — stands in `docs/SEASON-PACK.md` and answers the opposite question.

*Derived document: the rules are stated normatively in `SPECIFICATION.md` (F-M15, F-M16, F-M43,
F-M44, F-M215, F-M238, F-M247, F-M277, F-M334, F-M386, F-M387, F-M397, F-M450, F-M454, F-M471,
F-M472, F-M474, F-M486, F-M488, F-M493, F-M494, F-M497, F-M504, F-M566, F-M569, F-M594, F-M662,
F-M663, F-M665, F-M693, F-M719, F-M759, F-M760). This paper describes how those rules work together.
The words are those of `TERMS.md`. The archive on disk — its one cache, its key spaces, its
retention, its ceiling and its own log lines — stands in `docs/SEASON-PACK-CACHING.md` and is not
repeated here.*

## Contents

- [1. What this is about](#1-what-this-is-about)
- [2. What an archive is, per source](#2-what-an-archive-is-per-source)
- [3. The switches](#3-the-switches)
- [4. The order of one run](#4-the-order-of-one-run)
- [5. What is checked before the archive is fetched](#5-what-is-checked-before-the-archive-is-fetched)
- [6. What is checked after it arrived](#6-what-is-checked-after-it-arrived)
- [7. What is counted](#7-what-is-counted)
- [8. What a pack download never does](#8-what-a-pack-download-never-does)
- [9. What the log says](#9-what-the-log-says)
- [10. The checks](#10-the-checks)

## 1. What this is about

A **pack** is one container holding a whole season's subtitles for one language. On the way out we
form it. On the way in it is a file the source already holds, and it is the cheapest thing a run can
buy: one fetch, one unit of the daily allowance, many episodes.

Two sentences frame everything else. The run fetches the archive **whole** and unpacks from that body
(F-M471). And it writes **one** member of it — the one that names the episode it is working on, never
the archive and never "the first entry" (F-M215).

The unit is the pair: one source, one season, one language, one variant. A pack serves every episode
of its season that it really contains; episodes it does not contain stay open (F-M569).

## 2. What an archive is, per source

| source | what it hands out | what the offer carries before the fetch |
| --- | --- | --- |
| SubDL | one ZIP per subtitle page, whether it holds one film or a season | the member list from the API, with season, episode and each member's own hearing-impaired flag (F-M760) |
| tvsubtitles.net | one ZIP per series, season and language, taken from the season page | one hit per member, handle `seasonpack:<show>:<season>:<lang>:<index>` (F-M663) |
| moviesubtitles.org | nothing — `CanOfferSeasonPacks` is false | no pack switch on its card, no pack in its answer (F-M693) |

A tvsubtitles season archive carries both sides in one file: the regular members and the
hearing-impaired ones, told apart by `.HI.` in the member name (F-M662). No source therefore needs two
archives for one season.

The tvst handle is not a subtitle id. It names a position inside an archive the source never numbers,
and it is read back by `TryParseSeasonPackId` — a numeric subtitle id can never be read as a member.

## 3. The switches

Three settings stand on a source's card, and one capability stands beside them:

- `downloadEnabled` — may this source be asked at all. The last door before the network reads it
  through `SourceAllowed` (F-M494).
- `packsEnabled`, labelled `Packs` — does this source take part in packs, for **both** directions
  (F-M719).
- `preferSeasonZip`, labelled `Prefer season pack` — is the archive taken first (F-M693).
- `CanOfferSeasonPacks` — the capability, not a switch. It answers whether a pack is a subject for
  this source at all; only moviesubtitles.org answers false today.

The engine combines the two pack switches in one place, `(SourceSwitches.Flag(SourceKey,
FieldPacksEnabled) ?? seasonPacks) && Settings.Switch(FieldPreferSeasonZip)`, assigned to
`PreferSeasonZip`. Both default to on.

What happens when a switch stands off:

- **`packsEnabled` off** — no season archive is asked of that source and the episode comes through
  the single path. The source leaves the pack slot of the run.
- **`preferSeasonZip` off** — the tvsubtitles season page is not fetched at all; the single path is
  step one and no ZIP is loaded (measured for F-M693). Off means "single first", not "no containers".
- **Both off** — no archive from that source, and one unit is paid per episode that is taken.

**One finding, stated plainly.** The SubDL engine moves pack offers to the front of its answer only
when the search request carries `WantsPack`: `if (PreferSeasonZip && request.WantsPack && …)`. The run
sets that field to `false` at all three of its search calls. The order on the SubDL side therefore
comes from the run's own rank (§4), not from this switch; on the tvst side the switch keeps its
meaning.

## 4. The order of one run

The chain is the same for every candidate and every source: search, choose, fetch, unpack, write,
book. Only the source changes underneath.

1. **Search** — `SourceSearchAsync` asks the sources one after another in the configured order, never
   in parallel (F-M504). Each call is a `SourceSearchRequest` built by `SearchRequestFor`.
2. **Choose** — the candidate list is sorted by three keys in this order: `QuellenRang`, the source's
   place in the asking order (F-M494); then `CandidateRank`, price and reach (F-M474); then `Score`,
   text similarity (F-M44).
3. **Fetch** — `DownloadCandidateRawAsync` passes the gates of §5, then `FetchArchiveAsync` hands the
   job to the source's engine with the entry's handle, the season and the member it wants.
4. **Unpack** — `FindUnpackFile(season, episode)` names the member and the engine returns that
   member's bytes. SubDL goes through `ExtractArchiveMemberAsync`, tvsubtitles through
   `DownloadPackMemberAsync`.
5. **Write** — `SidecarNaming.PlanTarget` picks the free slot in the directory; `AtomicWriteAsync`
   writes a `.part` file and moves it into place.
6. **Book** — `Registry.MarkAndFlush(() => Registry.MarkDownloaded(...))` writes the mark, then
   `RememberPackCover` hands the season's other episodes to the run loop (F-M472).

`CandidateRank` is where "the pack before the single" actually lives, and it prices the offer, not the
format:

- `4` — a pack with this episode whose archive already lies in the files area: free, and free for the
  season;
- `3` — a single file whose archive already lies there: free, this episode only;
- `2` — a pack with this episode that must be fetched: one unit, whole season;
- `1` — a single file that must be fetched: one unit, one episode;
- `0` — a pack without this episode; it could not serve it at all.

The cache is asked by entry id (`_api.IsEntryArchiveCached(c.NId)`), the address the fetch actually
uses. A pack that carries the episode therefore beats an equally scored single, and a pack already on
disk beats everything.

## 5. What is checked before the archive is fetched

A refusal here costs seconds, because nothing has left the host yet. Six questions, in this order:

1. **Is this source allowed?** `SourceAllowed(cand.Provider)` and `IsConfiguredForDownload` — a source
   switched off, or without access data, is not fetched (F-M494, F-M497).
2. **Is the pair open at all?** The run searches and fetches only for pairs without evidence; a
   language that already lies in the file or the library is done, for every source at once (F-M566).
3. **Can this source be addressed?** tvsubtitles answers a fault instead of a fetch outside season
   1..`MaxSeason` (22) and episode 1..`MaxEpisode` (100).
4. **Does the source carry the language?** tvsubtitles maps our language onto the site's own code; a
   language the site does not list is an answer, and nothing is requested.
5. **Does the offer carry this episode?** `FindUnpackFile` is asked before the request. A null ends
   the candidate with one warning line; the first entry of an archive is never a substitute (F-M215,
   F-M454).
6. **Is there allowance left?** Before a 429 is allowed to end the day, the run reads the counter over
   the status contract (`QuotaAsync(forDownload: true)`, `QuotaStopDecision`). Room left means a short
   respacing; spent or unreadable means the day-long path (F-M238).

Two rules sit under these. `WaitForTransferSlotAsync` holds the pace before **every** request, the
pack's included, and a retry pays its own slot (F-M594). And a dry run fetches no archive at all: it
stops at the chosen candidate and can therefore never report a member that does not exist (F-M277,
F-M334).

## 6. What is checked after it arrived

**The unpack.** `ReadSeasonArchive` splits the body and folds styled names before it looks for
`.srt`. A body with no readable member is a broken archive: the run says so and falls back to the
single path (F-M663).

**The name.** `SidecarNaming.PlanTarget` asks the directory which slots are taken and returns the
first free corrected slot; an unprocessed original takes the reserved range from 51 up.
`AtomicWriteAsync` writes to a `.part` name and moves it, so a half-written file is never the file
Jellyfin lists.

**The text, per file.** `QaGateFailure` judges the bytes that would be written, in this order:

- `QaDownloadVerifyLanguage` — the detected language must be the language of the slot; an
  undetectable text passes (fail-open, F-M15).
- Structure — the cues must parse, their timings must be monotonic, and each cue must lie between
  100 ms and 600 s (F-M43).
- `QaDownloadMinCues` — at least 30 cues, otherwise the file is a stub (F-M16).
- Runtime — the cue span must fit the item's runtime (`RuntimeMatches`, F-M43).

**What a refusal costs.** A gate verdict belongs to the **file**, not to the candidate (F-M387).
Before the candidate is given up, the archive already on disk is asked for its next member that names
our episode — no request, no unit — and the gates run again on it. Only when every matching member
failed is the candidate burned: `summary.RejectedCandidates`, its id in the QA-rejected memory
(`_qaFails.RecordSkippedCandidates`), the finding filed where the report switch of that source is on
(F-M450). The walk then continues with the next candidate.

**The booking.** `MarkDownloaded` takes the content hash, the media hash, the language, the variant,
the written file name and its path. Origin is the source's own name, and that source's own column
receives the value: `subdlDownloadId`, `tvSubtitlesDownloadId` or `moviesubtitlesDownloadId` — one
field per source and direction, never a sum (F-M486, F-M488, F-M493).

## 7. What is counted

`IstSeasonPack` answers with one number: `cand.ContainerFileCount >= 2`. The count comes from the
container itself, never from a file name — SubDL wraps every delivery in a ZIP, single films included
(F-M760).

`ZaehleSeasonPack` then asks `_gezaehlteArchive`, a set per run keyed by `ArchivKennung`: `n:` the
entry id, else `u:` the address, else `r:` the release name. So:

- one fetched archive is **one** season pack, however many episodes it serves;
- the second episode out of the same archive counts neither as a pack nor as a subtitle (F-M759);
- whether that one count happens is a second question: the archive's key is inserted on the first
  save out of it, and only a save that counts as a success turns the insertion into a counted pack;
- a kept original with Auto-Sync on is a download but not a success; it lands under "downloaded
  without significance" and never under packs.

`summary.SeasonPacksDownloaded` reaches the plugin database through `StatusCounterDelta.From` and
`Plugin.AddStatusCounters`, and appears as *Season packs downloaded*, directly under *Subtitles
downloaded*. A dry run contributes nothing (F-M247).

**The archive cache and this number.** Without the cache every episode of a season would need its own
fetch, and a run over five episodes would count five packs. The cache is what lets one archive serve
a season across runs; its keys, retention, ceiling and lines stand in
`docs/SEASON-PACK-CACHING.md`. Two consequences belong here: a cached archive lifts a pack from rank
`2` to `4`, and the run's closing line reports `{CacheHits} archive hit(s)`.

## 8. What a pack download never does

- It never writes a whole archive beside the media. One member, named by the offer (F-M215).
- It never takes the first `.srt` when no member names the episode — nothing is written, and the line
  says why (F-M454).
- It never fetches an archive a second time for the next episode of the same season: the body lies in
  the archive cache and every further episode of it is a read (F-M386). The one deliberate repeat is a
  stored body that cannot resolve the episode — refetched once, and then the answer stands.
- It never overwrites a file without asking the directory first. A taken slot gets the next number;
  when nothing is free, the save is refused and reported.
- It never invents a language. A member of another language is filtered out in the search, and the
  language gate judges the text again.
- It never uploads what it downloaded. The origin it writes is exactly what later excludes the text
  from a pack (F-M397).
- It never runs from a dry run, and never from a switch alone: rank, member and gates decide
  (F-M334).

## 9. What the log says

Each line below is the format the code writes, with the run's placeholders in braces and the level at
which it is written beside it. Quote marks are the ones the log renders.

| level | the line |
| --- | --- |
| Debug | `[Sub Scribe-D] unpack pick S{Season}E{Episode} from pack {Release} → {File} (HI={Hi})` |
| information | `[Sub Scribe] pack served whole — {N} file(s) per archive fetch: {Release}` |
| warning | `[Sub Scribe-D] pack {Release} has {N} files but none for S{Season}E{Episode} — candidate skipped instead of saving the wrong episode.` |
| warning | `[Sub Scribe-D] pack {Release} did not resolve S{Season}E{Episode} from the archive — nothing saved.` |
| information | `[Sub Scribe-D] {File} [{Lang}] — next file of the same archive accepted: {Sibling} (first refused: {First})` |
| Verbose | `[Sub Scribe-D] {File} [{Lang}] — QA reject {Release}: {Reason} (no usable file left in this archive)` |
| information | `[Sub Scribe-D] pack "{Release}" covers {Season}x{First}-{Last} — {N} episode(s) of that release moved right after {File}` |

The first line names the member the run picked, the second says that one archive served more than one
episode. The next-to-last line reports the hand-over of F-M472, the line before it the sibling retry
of F-M387, and the QA reject line closes an archive that was exhausted. The run's closing line ends
with `… | {CacheHits} archive hit(s)`. The cache's own lines begin with `[Sub Scribe-Cache]` and belong
to the caching paper.

## 10. The checks

- `seasonpack` — the four price ranks and the cache question by entry id (T197), the hand-over of a
  season's other episodes (T195), the counters through one writer (T422).
- `tvsubtitles` — the season ZIP as step 1: five episodes for one ZIP fetch, a member read with no
  further request, a missing episode served by the episode page, a broken ZIP that aborts nothing,
  regular and HI separated (F-M663, F-M665); and no memo of the archive at all (T428).
- `archivecache` — the one cache behind every fetch: a hit inside a run, a hit across runs, the
  switches at 0, retention, ceiling, purge (T429).
- `endpoints` — the rank leads with the source, with pack and score behind it (F-M494); the
  container's file count is read from the container and never from a file name (F-M760).
- `sources` — `preferSeasonZip` is a declared field of both cards, with the same label and the same
  key.
