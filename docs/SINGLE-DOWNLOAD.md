# Single download — how one is fetched, and when it is taken

**Owner: the one file fetched.** One media file, one language, one variant, one source — one subtitle written
beside the file and one row in the plugin database. This paper states the whole way: which switches
decide, which files are candidates, in what order the sources are asked, which checks run before the
fetch, which run after it, how the file is named, and what a single download never does.

*Derived document: the rules are stated normatively in `SPECIFICATION.md` (F-M41, F-M42, F-M43,
F-M44, F-M50, F-M95, F-M215, F-M282, F-M283, F-M315, F-M316, F-M317, F-M375, F-M382, F-M387, F-M388,
F-M449, F-M452, F-M455, F-M471, F-M487, F-M488, F-M504, F-M592, F-M713, F-M790). This paper describes
how those rules work together. The words are those of `TERMS.md`.*

## Contents

- [1. What a single download is](#1-what-a-single-download-is)
- [2. The switches](#2-the-switches)
- [3. The order of a run](#3-the-order-of-a-run)
- [4. The checks before the fetch](#4-the-checks-before-the-fetch)
- [5. The checks after the fetch](#5-the-checks-after-the-fetch)
- [6. The name, the place, the booking](#6-the-name-the-place-the-booking)
- [7. What a single download never does](#7-what-a-single-download-never-does)
- [8. What the log says](#8-what-the-log-says)
- [9. The checks](#9-the-checks)

## 1. What a single download is

A single download is exactly one file: one episode or one film on disk, in one language, in one
variant — the regular subtitle or the hearing-impaired one. It comes from one source, and that source
is named on the row it writes.

`SubtitleRef` calls this the **pair**: a language plus the variant flag, with the forced flag as a
property that never counts as coverage (F-M282). The pair is the unit this whole paper speaks about.
Two pairs of one media file can be open at once, and the run asks for each of them on its own. A
missing variant therefore never re-requests the regular subtitle, and a present regular file never
closes the variant (F-M282, F-M283).

A third thing is not a pair: the **pack**. A pack is one archive that carries many episodes, and the
run resolves it down to the one file it wants before anything is judged (F-M215). What happens after
that resolution is this paper; how a pack is formed stands in `SEASON-PACK.md`.

Each source keeps its own mark on the file. The row carries the source's own identifier in the
source's own field — `subdlDownloadId`, `tvSubtitlesDownloadId`, `moviesubtitlesDownloadId` — because
"downloaded at one source" is no evidence for another (F-M488, F-M487).

## 2. The switches

Every switch answers a different question, and none of them overrules a check:

| Question | Switch | Effect when it is off |
|---|---|---|
| May this source be used for a download? | the source's own download switch (`IsDownloadEnabled`) | the source is neither searched nor fetched (F-M485) |
| Which languages are wanted? | `DownloadLanguages` | an empty list means download off (F-M42) |
| Is the variant wanted as well? | `DownloadHearingImpaired` | the variant pair is not required and not searched (F-M282) |
| May machine-translated subtitles come in? | `Allow AI translated subs` (`AllowAiTranslatedSubs`) | a candidate of the translation account is dropped at the search result (F-M449) |
| Must a correction prove itself before it counts? | `Auto-Sync` (`QaDownloadAutoSync`) | no fit runs, and every written file closes its pair |
| Shall the run only look? | dry run (`DownloadDryRun`) | a dry run searches, ranks and stops before the fetch (F-M277) |

The language travels in three spellings, and each one sits at its own seam. The page writes two
letters (`EN`), the pair canonicalises them on construction to ISO 639-2/T (`ENG`), and only the
source's request builds its own form from that canon (F-M790). The file name on disk carries the
canon again, three letters and lower case.

A source whose download switch is off is not asked at all. Its candidates are skipped one by one
with a line that names the source, and the run continues with the remaining sources (F-M504). A
source also drops out when its access data are missing; that is read per source, not for the run as a
whole.

## 3. The order of a run

One run walks its items, and each item walks its open pairs. The names below are the ones in the
code.

1. **Which files are candidates.** `CollectItems` reads the selected libraries and returns the items
   of this run. `ProcessItemAsync` takes one item — that is one media file — and asks whether its
   directory is writable and whether the refetch interval has elapsed.
2. **Which pairs are open.** `Registry.OpenPairs` is handed the required pairs from
   `RequiredPairs(targets)`, the languages embedded in the file and the sidecars on disk (F-M282).
   The answer splits into `regularMissing` and `variantMissing`, and an empty answer ends the item
   with one line: nothing open.
3. **The search, per source.** `RunV2SearchAsync` asks the main source through
   `SourceSearchAsync`, once with `hi=0` and, when the variant is missing, once with `hi=1`
   (F-M241). The other sources follow in `DownloadQueryOrder` order, each through its own engine and
   its own `SearchAsync`. A source that does not answer is not asked again in this run (F-M592).
4. **The choice.** The lists are merged, not swapped: `QuellenRang` puts the source order first,
   then `CandidateRank` (a pack that carries this episode, and whether its archive is already on
   disk), then `Score` against the file name (F-M44, F-M504).
5. **The walk.** `DownloadCandidateAsync` takes the bytes of one candidate through
   `DownloadCandidateRawAsync` and `FetchArchiveAsync`; the engine's `FetchAsync` is the only door to
   the network (F-M471).
6. **The gates.** The four content gates run on the bytes, then the optional alignment (F-M387).
7. **The write.** `AtomicWriteAsync` writes the file under the name `SidecarNaming` planned.
8. **The booking.** `Registry.MarkDownloaded` writes the row, with the content hash as its key.
9. **The end of the item.** `RememberSavedLibrary` names the library for the refresh, and the walk
   stops at the first corrected file of a language (F-M319).

## 4. The checks before the fetch

These cost no download unit, and each one can end a pair for this run:

- **Is the pair open at all?** There is no stored completion mark to consult. The question is derived
  again on every run from the embedded tracks, the sidecars and the store, so a deleted subtitle
  reopens its pair by itself (F-M283).
- **Is this language really missing?** The search list comes from the pairs, never from the language
  list of the page. A language whose regular file is on disk is not searched again, even while its
  variant is open.
- **Was this candidate already refused?** `_qaFails.GetSkippedCandidates` holds the ids a previous
  run discarded, and the ranked list drops them before the first fetch (F-M200, F-M255).
- **Is the source switched on and configured?** `SourceAllowed` and `IsConfiguredForDownload` are
  asked for every candidate, not once per run, so a candidate from a list that was built under
  another setting is skipped instead of fetched (F-M504).
- **How many pulls are left?** `walkLimit` is the single correction-attempts setting, and the same
  number is the search width (F-M50, F-M95). `0` means no limit in either place.
- **Does the frame rate fit?** A candidate that states its rate is refused when it differs from the
  item's rate by more than the configured tolerance. A candidate without a rate passes (F-M43).
- **Does the pack carry this episode?** `FindUnpackFile` is asked before the archive is fetched. A
  pack with a file list but no entry for this episode ranks 0 and is never fetched (F-M215).
- **Is there allowance left today?** Not knowable from here. The plugin keeps no budget of its own,
  because only the source can answer. A 429 takes that one source out for the rest of the run and
  leaves the item due (F-M713).

## 5. The checks after the fetch

The bytes have arrived, so the request is spent. From here a refusal still ends the pair, and no
correction is invented.

- **Did anything arrive?** Fewer than 100 bytes is nothing. An empty answer is asked once more after
  ten seconds, and only the second empty answer is a finding (F-M455).
- **Which file of the archive?** The archive is fetched whole, kept in the disk cache and unpacked
  for the wanted episode. A pack that carries no member for this episode is skipped, never "the
  first file in the zip" (F-M215, F-M471).
- **Are there cues nobody can read?** They are dropped and the text is renumbered before the gates
  and before the hash, so the gates judge what will be written (F-M388).
- **Does the text speak the language of the pair?** The detector reads the bytes, and a verdict that
  names another language refuses the file. In the download direction that mismatch is never
  corrected, unlike the upload direction, where a lying tag is corrected instead (F-M375).
- **Is the structure sound?** `ParseSrt` must return monotonic timings, no cue may be 100 ms or
  shorter, and no cue may run longer than 600 000 ms (F-M43).
- **Are there enough cues?** Below 30 cues the file is no subtitle. The check has its own switch
  (F-M16).
- **Does the span fit the runtime?** The window is the larger of a fixed floor and a share of the
  runtime: 420 s for a film, 240 s for an episode, 12 % by default, and the share is the only
  setting (F-M43).
- **Does it sit right on the audio?** With the aligner on, the fit measures the file against the
  audio track of its language. A correction that proves itself is written aligned and closes the
  pair. A fit that finds nothing wrong closes it too, because the file as downloaded is the good
  version. A refusal that calls the file wrong keeps it in the reserved block and leaves the pair
  open for the next candidate (F-M318, F-M452).
- **What about the next file of the same archive?** The gates run again on the next member for the
  same episode, at no download cost, before the candidate is given up (F-M387).

## 6. The name, the place, the booking

The file lands next to the media file, in the same directory, and its name is built by one builder:

- `<base>.<lang>[.sdh].<slot>.srt` — the media file's name without extension, the language in three
  letters and lower case, the variant marker when the file is the hearing-impaired one (F-M790).
- The slot is always written and always two digits, because Jellyfin lists external tracks in the
  order their names sort (F-M316).
- A corrected file takes slots 01 to 49. The untouched original that a refused alignment keeps
  beside it takes slots 51 to 99, so the two kinds can never collide (F-M315, F-M317).
- `PlanTarget` and `PlanOriginalTarget` read the directory first and return the first free slot.
  When the listing fails or the block is full, no name is returned and the caller writes nothing
  (F-M315).

Booked is one row per written file: `MarkDownloaded` takes the content hash, the media hash, the
language, the variant flag, the name, the path, and the source with its own identifier. The content
hash is the key, so the same text twice is one row. Two readers lean on that row: the seeder drops a
file whose content is already known, and the uploader refuses a subtitle whose origin is a download
(F-M487). A kept original gets a row of its own and the same treatment (F-M306).

## 7. What a single download never does

- It never uploads what it fetched. A known origin is blocked in both directions — not back to its
  own source, not to the other (F-M487).
- It never deletes a subtitle. The run removes its own probe file and trims the archive cache;
  removing rows and files belongs to the refresh (F-M60).
- It never corrects a language in this direction. A text that speaks another language is refused,
  and the pair stays open (F-M375).
- It never takes the first file of a pack. A pack without a member for this episode is no candidate
  (F-M215).
- It never invents a name or a slot. A full block refuses the write and says so.
- It never writes over a sidecar it did not plan: the name comes from a free slot read off the
  directory just before the write (F-M315).
- It never runs on a source whose switch is off, and never for a pair that already has evidence.

## 8. What the log says

The per-item lines carry the marker `[V]`, so they appear from Verbose on. The wording is the
wording in the code:

- `[Sub Scribe-D] SKIP {File} — nothing open` — every required pair has evidence.
- `[Sub Scribe-D] {File} — no candidates for [{Langs}] — asked: {Sources}` — not one asked source had
  a candidate, and the line names which ones were asked.
- `[Sub Scribe-D] {File} [{Lang}] — not available` — the language ends without a candidate.
- `[Sub Scribe-D] {File} [{Lang}] — download reject {Release}: {Reason}` — the bytes did not arrive,
  or arrived too small.
- `[Sub Scribe-D] {File} [{Lang}] — QA reject {Release}: {Reason} (no usable file left in this
  archive)` — the gates refused, and no member of the archive was left to try.
- `[Sub Scribe-D] {File} [{Lang}] — candidate limit reached: {N} candidate(s) pulled (limit
  {Limit}); {Kept} file(s) kept, {M} lower-ranked candidate(s) not tried`
- `pack {Release} has {N} files but none for S{Season}E{Episode} — candidate skipped instead of
  saving the wrong episode.` — a warning, not gated by the log mode.
- `[Sub Scribe] pack served whole — {N} file(s) per archive fetch: {Release}` — the archive was
  fetched once and serves its episodes from the cache.
- `[Sub Scribe-D] SAVED {Source} {Path} (lang={Lang}, score {Score}, release {Release},
  subdl={ScribeName}, author={Author}, imdb={Imdb}, tmdb={Tmdb}) for {File}` — the file is written and
  booked.
- `[Sub Scribe-D] original kept as {Path} — locked against upload (F-M315)` — the untouched original
  is beside the corrected file.

A pair that appears in the statistics but never in the log is not a pair that was served.

## 9. The checks

These suites guard this paper, and every one of them exists under `scripts/tests/`:

| Suite | What it holds |
|---|---|
| `coverage` | What a file on disk proves: a reserved original closes no pair, an aligned `01` does, a deleted `01` opens it again (F-M452) |
| `canonical` | The cue drop runs before the gates and the hash, on the same bytes that get written (F-M382, F-M388) |
| `hipool` | The hearing-impaired pool is walked, and it carries the main track when the regular pool runs dry (F-M476) |
| `aifilter` | The machine-translation filter sits on the search result, before the ranking (F-M449) |
| `reporting` | The runtime window: floors, share, and the verdict that names the window (F-M43) |
| `archivecache` | One cache per run, one key per source, and every cache operation in the log (F-M767) |
| `sources` | The engine boundary: four jobs and no fifth, and no pipeline name inside a source (F-M682) |
| `seasonpack` | The pack counters stay apart from the subtitle counters, in both directions (F-M759) |

The catalogue entry of every test stands in `TESTS.md`.
