# Single download

A single download fetches one subtitle for one media file, in one language and one variant, from one
source. The run writes that file next to the video and books one row in the plugin database. This page
describes that path in the order the plugin takes it. It covers the switches, the files that may be
picked, the search order, the checks before and after the fetch, the name the file gets, and the
things a single download never does.

## Contents

- [1. What a single download is](#1-what-a-single-download-is)
- [2. The switches that allow it](#2-the-switches-that-allow-it)
- [3. How one run proceeds](#3-how-one-run-proceeds)
- [4. What is checked before the fetch](#4-what-is-checked-before-the-fetch)
- [5. What is checked after the fetch](#5-what-is-checked-after-the-fetch)
- [6. The name, the place and the booking](#6-the-name-the-place-and-the-booking)
- [7. What a single download never does](#7-what-a-single-download-never-does)
- [8. What the log says, and where the rules live](#8-what-the-log-says-and-where-the-rules-live)

## 1. What a single download is

A single download is one subtitle for one media file: one language, one variant, one source. The
variant says whether the file is the regular subtitle or the hearing-impaired one. The run writes
that file as a loose subtitle beside the video, and it books one row about it.

One language plus one variant is a pair, and the code uses that word for the two together. Two pairs
of the same video can be open at once: the regular file and the hearing-impaired one. The run asks
for each of them on its own, so a missing hearing-impaired file never sends it looking for the
regular one again. A regular file that already lies on disk never closes the hearing-impaired pair
either (F-M282, F-M283). A forced track belongs to the pair as well, but it never counts as coverage
(F-M282).

A season pack is a different thing: one archive that carries many episodes. The run opens the
archive and takes out the one file it wants before it judges anything (F-M215). How a pack is formed
stands in [SEASON-PACK.md](SEASON-PACK.md).

Each source keeps its own mark on the row. A subtitle fetched at one site is no evidence for
another site, so every source has its own field: `subdlDownloadId`, `tvSubtitlesDownloadId` and
`moviesubtitlesDownloadId` (F-M487, F-M488).

## 2. The switches that allow it

Downloading is a task of its own, with its own switch and its own block in the status display
(F-M41). Six settings decide what it may do, and none of them overrules a check.

| Question | Setting | When it is off |
|---|---|---|
| May this source be used at all? | `Download enabled` (`IsDownloadEnabled`) on the source's card | the source is neither searched nor fetched (F-M485) |
| Which languages do you want? | `Download languages` (`DownloadLanguages`) | an empty list means that downloading is off (F-M42) |
| Do you want the hearing-impaired variant too? | `Download hearing impaired` (`DownloadHearingImpaired`) | the variant is neither required nor searched (F-M282) |
| May machine-translated subtitles come in? | `Allow AI translated subs` (`AllowAiTranslatedSubs`) | such a candidate is dropped at the search result (F-M449) |
| Must a correction prove itself before it counts? | `Auto-Sync` (`QaDownloadAutoSync`) | nothing is aligned, and every written file closes its pair |
| Shall the run only look? | dry run (`DownloadDryRun`) | a dry run searches and ranks and stops before the fetch (F-M277) |

A switch is a permission, not an order. It says that something may happen, and the checks in
sections 4 and 5 decide whether it does.

A language travels in three spellings, and each of them sits in its own place. The settings page
writes two letters, such as `EN`. The plugin stores ISO 639-2/T, so `ENG`, and only the source's own
request builds its own form from that (F-M790). The file on disk carries the stored form again:
three letters, lower case (`deu`).

A source with its switch off is not asked at all. Its candidates are dropped one by one with a line
that names the source, and the run goes on with the rest (F-M504). A source also drops out when its
access data are missing, and that is read per source rather than for the whole run.

## 3. How one run proceeds

A run walks its files one by one, and each file walks its open pairs. The names below are the ones
the code uses.

1. **Collect the files.** `CollectItems` reads the libraries you picked and returns the files of
   this run. `ProcessItemAsync` then takes one file and asks two things: is its folder writable, and
   has the refetch interval passed.

2. **Work out what is missing.** `Registry.OpenPairs` gets the languages you asked for, the
   languages inside the file and the loose subtitles on disk (F-M282). The answer splits into
   missing regular files and missing hearing-impaired files. When both lists are empty, the file
   ends with one line: nothing open.

3. **Search, one source after another.** `RunV2SearchAsync` asks the first source once with `hi=0`
   and, when the hearing-impaired file is missing, once more with `hi=1` (F-M241). The other sources
   follow in the order of `DownloadQueryOrder`, each through its own engine. A source that does not
   answer is not asked again in this run (F-M592).

4. **Choose one candidate.** The lists are merged, not swapped. `QuellenRang` puts the source order
   first, then `CandidateRank` follows and knows whether a pack carries this episode and whether
   its archive already lies on disk, and `Score` compares the release name with the file name
   (F-M44, F-M504).

5. **Fetch the bytes.** `DownloadCandidateAsync` takes one candidate through
   `DownloadCandidateRawAsync` and `FetchArchiveAsync`. The engine's `FetchAsync` is the only door
   to the network (F-M471).

6. **Judge the file.** The content gates run on the bytes, and the optional alignment runs after
   them (F-M387).

7. **Write it.** `AtomicWriteAsync` writes the file under the name that `SidecarNaming` planned.

8. **Book it.** `Registry.MarkDownloaded` writes the row, with the content hash as its key.

9. **Close the file.** `RememberSavedLibrary` names the library for the refresh, and the walk stops
   at the first corrected file of a language (F-M319).

## 4. What is checked before the fetch

These checks cost no download unit, and any of them can end a pair for this run. They read only what
the plugin already knows: the folder on disk, the stream lists and the database.

- **Is the pair open at all?** There is no stored completion mark to consult, because the answer is
  worked out again on every run from the tracks inside the file, the loose subtitles and the
  database. A deleted subtitle therefore reopens its pair by itself (F-M283).

- **Is this language really missing?** The search list comes from the pairs, never from the language
  list on the settings page. When the regular file lies on disk, that language is not searched
  again, even while its hearing-impaired pair is still open.

- **Was this candidate refused before?** `_qaFails.GetSkippedCandidates` holds the ids that an
  earlier run threw away, and the ranked list drops them before the first fetch. The line about
  such a drop names the candidate and the reason (F-M200, F-M255).

- **Is the source switched on and set up?** `SourceAllowed` and `IsConfiguredForDownload` are asked
  for every candidate, not once per run. A candidate from a list built under another setting is
  therefore skipped instead of fetched (F-M504).

- **How many pulls are left?** `walkLimit` is the one setting for correction attempts, and the same
  number is the search width (F-M50, F-M95). A value of `0` means no limit in either place.

- **Does the frame rate fit?** A candidate that states its rate is refused when it differs from the
  file's rate by more than the tolerance. A candidate without a rate passes (F-M43).

- **Does the pack carry this episode?** `FindUnpackFile` is asked before the archive is fetched. A
  pack with a file list but no entry for this episode ranks 0 and is never fetched (F-M215).

- **Is there allowance left today?** The plugin keeps no budget of its own, so only the source can
  answer. A 429 takes that one source out for the rest of the run and leaves the file due (F-M713).

## 5. What is checked after the fetch

The bytes have arrived, so the request is spent. From here on a refusal still ends the pair, and
nothing in this direction is corrected.

- **Did anything arrive?** Fewer than 100 bytes is nothing. An empty answer is asked once more after
  ten seconds, and only the second empty answer counts as a finding (F-M455).

- **Which file out of the archive?** The archive is fetched whole, kept in the cache on disk and
  unpacked for the one episode that is wanted. A pack with no member for this episode is skipped,
  and the first file in the zip is never taken instead (F-M215, F-M471).

- **Are there cues nobody can read?** Cues of 100 ms or less are dropped and the rest is renumbered
  before the gates and before the hash, so the gates judge what will be written (F-M382, F-M388).

- **Does the text speak the language of the pair?** The detector reads the bytes, and a verdict that
  names another language refuses the file. Unlike the upload direction, where a lying tag is
  corrected, this direction corrects nothing (F-M375).

- **Is the structure sound?** The timings must run forward, no cue may last 100 ms or less, and no
  cue may run longer than 600 000 ms, which is ten minutes (F-M43).

- **Are there enough cues?** Under 30 cues the file is no subtitle. That check has a switch of its
  own (F-M16).

- **Does the span fit the runtime?** The window is the larger of a fixed floor and a share of the
  runtime: 420 s for a film, 240 s for an episode and 12 % by default. Only the share is a setting,
  and the check is symmetric (F-M43).

- **Does it sit on the audio?** With the aligner on, the fit measures the file against the audio
  track of its language. A correction that proves itself is written aligned and closes the pair,
  and a fit that finds nothing wrong closes it too. A refusal that calls the file wrong keeps it in
  the reserved block and leaves the pair open for the next candidate (F-M318, F-M452).

- **What about the next file of the same archive?** The gates run again on the next member for the
  same episode, at no download cost, before the candidate is given up (F-M387).

## 6. The name, the place and the booking

The file lands in one place only: the folder of the video, beside the video itself. One builder
composes its name.

- The shape is `<base>.<lang>[.sdh].<slot>.srt`. So `Movie.deu.01.srt` is a regular file in slot 01,
  and `Movie.deu.sdh.01.srt` is the hearing-impaired one (F-M790).
- The slot is always written and always two digits, because Jellyfin lists external tracks in the
  order their names sort (F-M316).
- A corrected file takes slots 01 to 49. An untouched original that a refused alignment keeps
  beside it takes slots 51 to 99, so the two kinds can never collide (F-M315, F-M317).
- `PlanTarget` and `PlanOriginalTarget` read the folder first and return the first free slot. When
  the listing fails or the block is full, they return no name and the caller writes nothing
  (F-M315).

One row is booked per written file. `MarkDownloaded` takes the content hash, the media hash, the
language, the variant flag, the name, the path, and the source with its own identifier. The content
hash is the key, so the same text twice is one row. Two readers lean on that row: the seeder drops a
file whose content is already known, and the uploader refuses a subtitle that came out of a download
(F-M487). A kept original gets a row of its own and the same treatment (F-M306).

## 7. What a single download never does

- It never uploads what it fetched. A known origin is blocked in both directions: not back to its
  own source and not to the other one (F-M487).
- It never deletes a subtitle. The run removes its own probe file and trims the cache, while rows
  and files are removed by the refresh (F-M60).
- It never corrects a language in this direction. A text that speaks another language is refused,
  and the pair stays open (F-M375).
- It never takes the first file of a pack, because a pack without a member for this episode is no
  candidate at all (F-M215).
- It never invents a name or a slot. A full block refuses the write and says so.
- It never writes over a loose subtitle it did not plan, because the name comes from a free slot
  read off the folder just before the write (F-M315).
- It never runs for a source whose switch is off, and never for a pair that already has evidence.

## 8. What the log says, and where the rules live

The lines about a single file carry the marker `[V]`, so they appear from Verbose on. Their wording
is the wording of the code.

- `[Sub Scribe-D] SKIP {File} — nothing open` means that every required pair has evidence.
- `[Sub Scribe-D] {File} — no candidates for [{Langs}] — asked: {Sources}` means that not one asked
  source had a candidate, and the line names the ones that were asked.
- `[Sub Scribe-D] {File} [{Lang}] — not available` says that the language ends without a
  candidate.
- `[Sub Scribe-D] {File} [{Lang}] — download reject {Release}: {Reason}` says that the bytes did not
  arrive, or that they arrived too small.
- `[Sub Scribe-D] {File} [{Lang}] — QA reject {Release}: {Reason} (no usable file left in this
  archive)` says that the gates refused and that no member of the archive was left to try.
- `[Sub Scribe-D] {File} [{Lang}] — candidate limit reached: {N} candidate(s) pulled (limit
  {Limit}); {Kept} file(s) kept, {M} lower-ranked candidate(s) not tried` closes the walk at the
  pull limit.
- `pack {Release} has {N} files but none for S{Season}E{Episode} — candidate skipped instead of
  saving the wrong episode.` is a warning, and the log mode does not gate it.
- `[Sub Scribe] pack served whole — {N} file(s) per archive fetch: {Release}` says that the archive
  was fetched once and serves its episodes from the cache.
- `[Sub Scribe-D] SAVED {Source} {Path} (lang={Lang}, score {Score}, release {Release},
  subdl={ScribeName}, author={Author}, imdb={Imdb}, tmdb={Tmdb}) for {File}` says that the file is
  written and booked.
- `[Sub Scribe-D] original kept as {Path} — locked against upload (F-M315)` says that the untouched
  original lies beside the corrected file.

A pair that appears in the statistics but never in the log is not a pair that was served.

The rules themselves stand in [SPECIFICATION.md](SPECIFICATION.md), mainly under F-M41, F-M42,
F-M43, F-M44, F-M50, F-M95, F-M215, F-M282, F-M283, F-M315, F-M316, F-M317, F-M375, F-M382, F-M387,
F-M388, F-M449, F-M452, F-M455, F-M471, F-M487, F-M488, F-M504, F-M592, F-M713 and F-M790. This page
only tells how they work together, and the wording follows [TERMS.md](TERMS.md).

Eight suites guard the behaviour, and every one of them stands under `scripts/tests/`:

| Suite | What it holds |
|---|---|
| `coverage` | What a file on disk proves: a reserved original closes no pair, an aligned `01` does, and a deleted `01` opens it again (F-M452) |
| `canonical` | The cue drop runs before the gates and the hash, on the same bytes that get written (F-M382, F-M388) |
| `hipool` | The hearing-impaired pool is walked, and it carries the main track when the regular pool runs dry (F-M476) |
| `aifilter` | The machine-translation filter sits on the search result, before the ranking (F-M449) |
| `reporting` | The runtime window, with its floors, its share and the verdict that names the window (F-M43) |
| `archivecache` | One cache per run, one key per source, and every cache operation in the log (F-M767) |
| `sources` | The engine boundary: four jobs and no fifth, and no pipeline name inside a source (F-M682) |
| `seasonpack` | The pack counters stay apart from the subtitle counters, in both directions (F-M759) |

The catalogue entry of every test stands in [TESTS.md](TESTS.md).
