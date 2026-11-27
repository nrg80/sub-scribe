# Season pack download — one archive, many episodes

A season archive is one ZIP that carries the subtitles of a whole season in one language. Some
sources hand it out whole, so a single fetch can bring twenty episodes at once. That makes it the
cheapest thing a run can buy: one fetch, one unit of the daily allowance, many files.

This page follows such an archive from the search to the written file. It shows what each source
hands over, which switches have to be on, how the run ranks an offer and unpacks it, what it checks
before and after the fetch, and what it counts. Building a pack out of the files on your disk is the
opposite direction, and it stands in `docs/SEASON-PACK.md`.

## Contents

- [1. What a pack download is](#1-what-a-pack-download-is)
- [2. What each source hands over, and which switches decide](#2-what-each-source-hands-over-and-which-switches-decide)
- [3. How one run proceeds](#3-how-one-run-proceeds)
- [4. What is checked before the fetch](#4-what-is-checked-before-the-fetch)
- [5. What is checked after the fetch](#5-what-is-checked-after-the-fetch)
- [6. What the log says](#6-what-the-log-says)
- [7. What a pack download never does](#7-what-a-pack-download-never-does)
- [8. Where these rules live](#8-where-these-rules-live)

## 1. What a pack download is

One archive carries one season, one language and one variant, and it comes from one source. The
variant says whether the subtitles carry the hearing-impaired markers or not, so a season in 26
languages needs 26 archives and not one big pile.

The run fetches the archive whole and unpacks from that complete body. Out of it, it writes exactly
one member: the one whose name holds the episode it is working on. The archive itself never lands
beside the media, and the first entry of an archive is never a stand-in for a missing name.

A pack serves only the episodes it really contains. An episode the archive does not hold stays open,
and the run keeps looking for it at another candidate.

## 2. What each source hands over, and which switches decide

The three sources treat an archive very differently.

- **SubDL** hands out one ZIP per subtitle page, and such a page may hold a single film or a whole
  season. The offer already carries the member list from the API, with season, episode and each
  member's own hearing-impaired flag.
- **tvsubtitles.net** hands out one ZIP per series, season and language, taken from the season page.
  The offer carries one hit per member, and each hit is addressed by a handle of the form
  `seasonpack:<show>:<season>:<lang>:<index>`. That handle is not a subtitle id: it names a position
  inside an archive that the site never numbers, and `TryParseSeasonPackId` reads it back.
- **moviesubtitles.org** hands out nothing of the kind. Its capability `CanOfferSeasonPacks` is
  false, so its card carries no pack switch and its answer carries no archive.

A tvsubtitles archive holds both variants in one file: the regular members and the hearing-impaired
ones, told apart by `.HI.` in the member name. No source needs two archives for one season.

Three switches decide, and each of them sits on a source's card.

| Question | Switch | When it is off |
|---|---|---|
| May this source be asked at all? | the source's own download switch (`downloadEnabled`) | the source is neither searched nor fetched |
| Does this source take part in packs, in both directions? | `Packs` (`packsEnabled`) | no season archive is requested there, and every episode comes through the single path |
| Is the archive tried before the single? | `Prefer season pack` (`preferSeasonZip`) | at tvsubtitles the season page is not fetched at all: single first |

Both pack switches default to on, and the engine joins them in one place:
`(SourceSwitches.Flag(SourceKey, FieldPacksEnabled) ?? seasonPacks) && Settings.Switch(FieldPreferSeasonZip)`.

With the pack switch off, a source drops out of the run's pack slot. With the preference off,
"single first" is meant rather than "no archives". With both off, every episode costs its own unit.

**The preference reaches only one source today.** The SubDL engine moves pack offers to the front of
its answer only when the search request carries `WantsPack`, as in
`if (PreferSeasonZip && request.WantsPack && …)`. The run sets that field to `false` at all three of
its search calls. So on the SubDL side the order comes from the run's own ranking and the switch is
never asked, while at tvsubtitles it keeps its meaning.

## 3. How one run proceeds

Every candidate walks the same chain, whatever the source: search, choose, fetch, unpack, write,
book.

1. **Search.** The run asks its sources one after another in the configured order, never in
   parallel. Each call is a `SourceSearchRequest` built by `SearchRequestFor`.
2. **Choose.** The candidate list is sorted by three keys: the source's place in the asking order
   (`QuellenRang`), then `CandidateRank`, which weighs price and reach, then `Score`, the text
   similarity.
3. **Fetch.** The candidate passes the checks of §4, and `FetchArchiveAsync` hands the job to the
   source's engine together with the handle, the season and the member it wants.
4. **Unpack.** `FindUnpackFile(season, episode)` names the member, and the engine returns the bytes
   of that member alone: SubDL through `ExtractArchiveMemberAsync`, tvsubtitles through
   `DownloadPackMemberAsync`.
5. **Write.** `SidecarNaming.PlanTarget` picks a free name in the directory, and `AtomicWriteAsync`
   writes a `.part` file before it moves it into place.
6. **Book.** `Registry.MarkAndFlush(() => Registry.MarkDownloaded(...))` writes the mark, and
   `RememberPackCover` hands the season's other episodes to the run loop.

The order "archive before single" lives in `CandidateRank`, and that rank prices an offer rather than
a format:

- `4` — an archive that carries this episode and already lies in the files area. Free, and free for
  the whole season.
- `3` — a single file whose archive already lies there. Free, but it serves this episode only.
- `2` — an archive that carries this episode and has to be fetched. One unit, whole season.
- `1` — a single file that has to be fetched. One unit, one episode.
- `0` — an archive without this episode. It could not serve the run at all.

The cache is asked by entry id, with `_api.IsEntryArchiveCached(c.NId)` — the same address the fetch
uses. An archive that carries the episode therefore beats an equally good single, and an archive
already on disk beats everything else.

**One archive, one count.** `IstSeasonPack` reads `cand.ContainerFileCount >= 2`, and that number
comes from the archive itself and not from a file name, because SubDL wraps every delivery in a ZIP,
single films included. `ZaehleSeasonPack` keeps one set per run, `_gezaehlteArchive`, keyed by
the archive's own mark: `n:` and the entry id when there is one, otherwise `u:` and the address,
otherwise `r:` and the release name. Three things follow from that key. One fetched archive counts
once, however many episodes it serves. The second episode taken out of the same archive counts
neither as a pack nor as a subtitle. And a kept original with Auto-Sync on is a download but not a
success, so it lands under "downloaded without significance" and never under packs.

The mark is inserted before the save is known to have worked: the set takes the key on the first save
out of the archive, and only a save that counts as a success turns that entry into a counted pack. A
first save that fails therefore spends the archive's single count for that run.

`summary.SeasonPacksDownloaded` reaches the plugin database through `StatusCounterDelta.From` and
`Plugin.AddStatusCounters`, and it appears as *Season packs downloaded*, directly under *Subtitles
downloaded*. A dry run adds nothing to it.

The cache and this number belong together. Without a cache every episode of a season would need its
own fetch, and a run over five episodes would count five packs. The cache is what lets one archive
serve a season across runs; its keys, its retention, its ceiling and its own lines stand in
`docs/SEASON-PACK-CACHING.md`. Two things belong here: an archive already in the cache lifts an offer
from rank `2` to `4`, and the closing line of the run reports `{CacheHits} archive hit(s)`.

## 4. What is checked before the fetch

Nothing has left the host yet, so a refusal at this point costs seconds. Six questions are asked, in
this order, and any one of them can end the candidate.

1. **Is the source allowed?** `SourceAllowed(cand.Provider)` and `IsConfiguredForDownload` reject a
   source that is switched off or carries no access data.
2. **Is the language still open?** The run searches and fetches only where a subtitle is missing, so
   a language already present in the file or in the library is done for every source at once.
3. **Can the source be addressed?** Outside season 1 to `MaxSeason` (22) and episode 1 to
   `MaxEpisode` (100), tvsubtitles answers with a fault instead of a fetch.
4. **Does the source carry this language?** tvsubtitles maps our language onto the site's own code,
   and a language the site does not list is an answer in itself, so nothing is requested.
5. **Does the offer carry this episode?** `FindUnpackFile` is asked before the request, and a null
   ends the candidate with one warning line. The first entry of an archive is never a stand-in.
6. **Is allowance left?** Before a 429 is allowed to end the day, the run reads its counter with
   `QuotaAsync(forDownload: true)` and `QuotaStopDecision`. Room left means a short pause between
   requests; a spent or unreadable counter means the day-long path.

Two rules sit underneath. `WaitForTransferSlotAsync` holds the pace before every request, the
archive's included, and a retry pays for its own slot. A dry run fetches no archive at all: it stops
at the chosen candidate, which is why it can never report a member that does not exist.

## 5. What is checked after the fetch

Unpacking comes first. `ReadSeasonArchive` splits the body and folds styled names before it looks for
`.srt`, and a body with no readable member counts as a broken archive: the run says so and falls back
to the single path.

Naming and writing follow. `SidecarNaming.PlanTarget` asks the directory which names are taken and
returns the first free corrected one, while an unprocessed original takes the reserved range from 51
up. `AtomicWriteAsync` writes under a `.part` name and moves the file afterwards, so a half-written
file is never the one Jellyfin lists.

The bytes that would be written are then judged by `QaGateFailure`, through four gates:

- **Language.** `QaDownloadVerifyLanguage` wants the detected language to be the language of the
  slot. A text that cannot be detected passes, because this gate fails open.
- **Structure.** The cues have to parse, their timings have to run forward, and each cue has to lie
  between 100 ms and 600 s.
- **Cues.** `QaDownloadMinCues` wants at least 30 of them, because anything below that is a stub.
- **Runtime.** The span of the cues has to fit the runtime of the item, through `RuntimeMatches`.

These gates reject; they do not repair. A wrong language is refused instead of being relabelled, and
nothing is patched until a check passes.

A refusal costs less than it looks, because a verdict belongs to the file and not to the candidate.
Before the candidate is given up, the archive already on disk is asked for its next member that names
our episode, with no request and no unit spent, and the gates run again on that member. Only when
every matching member has failed is the candidate burned: `summary.RejectedCandidates` is raised, its
id goes into the QA-rejected memory through `_qaFails.RecordSkippedCandidates`, and the finding is
filed where the report switch of that source is on. The run then walks on to the next candidate.

The booking records what was written. `MarkDownloaded` takes the content hash, the media hash, the
language, the variant, the written file name and its path. The origin is the source's own name, and
the value lands in that source's own column: `subdlDownloadId`, `tvSubtitlesDownloadId` or
`moviesubtitlesDownloadId`, one field per source and direction and never a sum.

## 6. What the log says

Each line below is the format the code writes, with the run's placeholders in braces and the level it
is written at beside it.

| level | the line |
| --- | --- |
| Debug | `[Sub Scribe-D] unpack pick S{Season}E{Episode} from pack {Release} → {File} (HI={Hi})` |
| information | `[Sub Scribe] pack served whole — {N} file(s) per archive fetch: {Release}` |
| warning | `[Sub Scribe-D] pack {Release} has {N} files but none for S{Season}E{Episode} — candidate skipped instead of saving the wrong episode.` |
| warning | `[Sub Scribe-D] pack {Release} did not resolve S{Season}E{Episode} from the archive — nothing saved.` |
| information | `[Sub Scribe-D] {File} [{Lang}] — next file of the same archive accepted: {Sibling} (first refused: {First})` |
| Verbose | `[Sub Scribe-D] {File} [{Lang}] — QA reject {Release}: {Reason} (no usable file left in this archive)` |
| information | `[Sub Scribe-D] pack "{Release}" covers {Season}x{First}-{Last} — {N} episode(s) of that release moved right after {File}` |

The first line names the member the run picked. The second says that one archive served more than
one episode. The third and the fourth say why nothing was saved. The line before the last reports
the hand-over of the season's other episodes, and the last one closes an archive that was exhausted.
The run's closing line ends with `… | {CacheHits} archive hit(s)`. The cache's own lines begin with
`[Sub Scribe-Cache]` and belong to the caching paper.

## 7. What a pack download never does

- It never writes a whole archive beside the media. It writes the one member the offer names.
- It never falls back to the first `.srt` when no member names the episode. Nothing is written, and
  the line says why.
- It never fetches one archive twice for the next episode of the same season, because the body lies
  in the archive cache and every further episode of it is a read. The one deliberate repeat is a
  stored body that cannot resolve the episode: it is fetched once more, and then that stands.
- It never overwrites a file without asking the directory first. A taken name gets the next number,
  and when nothing is free the save is refused and reported.
- It never invents a language. A member of another language is filtered out in the search, and the
  language gate judges the text again.
- It never uploads what it downloaded, because the origin it writes is exactly what later keeps the
  text out of a pack.
- It never runs from a dry run and never from a switch alone. Rank, member and gates decide.

## 8. Where these rules live

The rules themselves stand in [SPECIFICATION.md](SPECIFICATION.md), and this page only tells how they
work together. The entries behind it are F-M15, F-M16, F-M43, F-M44, F-M215, F-M238, F-M247, F-M277,
F-M334, F-M386, F-M387, F-M397, F-M450, F-M454, F-M471, F-M472, F-M474, F-M486, F-M488, F-M493, F-M494,
F-M497, F-M504, F-M566, F-M569, F-M594, F-M662, F-M663, F-M665, F-M693, F-M719, F-M759 and F-M760.

Five suites guard the behaviour:

- `seasonpack` covers the four price ranks and the cache question by entry id (T197), the hand-over
  of a season's other episodes (T195), and the counters through one writer (T422).
- `tvsubtitles` covers the season ZIP as step one: five episodes for one ZIP fetch, a member read
  with no further request, a missing episode served by the episode page, a broken ZIP that aborts
  nothing, and regular and hearing-impaired members kept apart. It also covers the fact that no memo
  of the archive exists at all (T428).
- `archivecache` covers the one cache behind every fetch: a hit inside a run, a hit across runs, the
  switches at 0, retention, the ceiling and the purge (T429).
- `endpoints` covers the rank leading with the source and with pack and score behind it, and the
  container's file count read from the container rather than from a file name.
- `sources` covers `preferSeasonZip` as a declared field of both cards, with the same label and the
  same key.

Their catalogue entries stand in [TESTS.md](TESTS.md).
