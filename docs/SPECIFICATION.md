# Specification — Jellyfin Plugin "Sub Scribe" (Upload + Download)

**Owner: the rules.** This is the master document: every other paper is derived from it and cites its `F-M` tags. The contract boundaries stand in `CONTRACTS.md`, the vocabulary in `TERMS.md`.
**Project:** Native Jellyfin plugin: automatic upload of embedded subtitles to SubDL.com + download pipeline for missing external subtitles — both in ONE plugin
**Version:** 2.143
**Status:** Implementation — v12.3.8.66.

**A rule stands here with its ground: the decision behind it in one sentence, under `Concept decision`, and the
check that proves it. The detailed measurement archive — what was measured, which run showed it, what happened —
does not live here. It lives locally, sorted by chapter and marked with the requirement number. This document
states the rule, its constants, its ground and its test.**

**The vocabulary is `docs/TERMS.md` — one word per concept, and English is the master.** This document uses it without repeating it.

**Counting convention of the contents counters.** A section counter is the **number of definition lines in its block, sub-headings included, retired entries included** — a plain count that says nothing about what is in force. A section counter that excludes its sub-sections is not the convention. Checked by T115.

## Contents

- [1. Objective and Scope](#1-objective-and-scope) — 2 requirements
- [2. Seeder — what enters the queue](#2-seeder-what-enters-the-queue) — 13 requirements
- [3. Upload Pipeline](#3-upload-pipeline) — 127 requirements
 - [3.1 Quality Gates — Upload](#31-quality-gates-upload) — 9 requirements
 - [3.2 Dry Run — Upload](#32-dry-run-upload) — 3 requirements
 - [3.3 Season Packs — Upload](#33-season-packs-upload) — cross-cutting, cites F-M379–F-M448 and F-M475, F-M476
 - [3.4 Upload completion criteria](#34-upload-completion-criteria) — 26 requirements (F-M557–F-M563, F-M571–F-M577, F-M580–F-M592)
 - [3.5 The ffmpeg handle — one pass, one count, one exit code](#35-the-ffmpeg-handle-one-pass-one-count-one-exit-code) — cross-cutting
- [4. Download Pipeline](#4-download-pipeline) — 93 requirements
 - [4.1 Quality Gates — Download](#41-quality-gates-download) — 32 requirements
 - [4.2 Dry Run — Download](#42-dry-run-download) — 1 requirement
 - [4.3 Auto-Sync — Download](#43-auto-sync-download) — 20 requirements
 - [4.4 Season Packs — Download](#44-season-packs-download) — Querschnitt, zitiert F-M334, F-M384, F-M397, F-M454
 - [4.5 Download completion criteria](#45-download-completion-criteria) — 6 requirements (F-M565–F-M570)
- [5. Upload Postprocessing](#5-upload-postprocessing) — 14 requirements
- [6. Database Refresh](#6-database-refresh) — 10 requirements
- [7. OSHash Refresh](#7-oshash-refresh) — 3 requirements
- [8. Rules Shared by Both Directions](#8-rules-shared-by-both-directions) — 5 requirements

- [9. SubDL/TMDb API, IDs and Credentials](#9-subdltmdb-api-ids-and-credentials) — 75 requirements
- [10. Scheduler, Quota and Timing](#10-scheduler-quota-and-timing) — 24 requirements
- [11. Content Registry and Identity](#11-content-registry-and-identity) — 27 requirements
- [12. Library Scope and Skip Filters](#12-library-scope-and-skip-filters) — 8 requirements
- [13. Configuration and Settings Page](#13-configuration-and-settings-page) — 40 requirements
- [14. Data Model and Persistence](#14-data-model-and-persistence) — 92 requirements
- [15. Logging, Status and Transparency](#15-logging-status-and-transparency) — 61 requirements
- [16. Non-Goals](#16-non-goals)
- [17. Non-Functional Requirements](#17-non-functional-requirements) — 10 non-functional entries
- [18. Acceptance Criteria](#18-acceptance-criteria) — 7 acceptance entries
- [19. Test Library](#19-test-library) — 40 requirements
- [21. Adding another endpoint — checklist and pitfalls](#21-adding-another-endpoint-checklist-and-pitfalls) — 1 requirement (F-M564) + checklist
 - [21.1 The steps](#211-the-steps) — checklist
 - [21.2 What goes wrong (measured)](#212-what-goes-wrong-measured) — pitfalls
 - [21.3 What stays off limits](#213-what-stays-off-limits) — taboos

- [22. Subtitle Sources — one contract](#22-subtitle-sources-one-contract) — 45 requirements
- [20. References](#20-references) — 4 requirements

## 1. Objective and Scope

Automatic upload of all embedded text subtitles from the Jellyfin library to SubDL.com.
**Core idea:** Jellyfin already knows every file and every stream. The plugin handles discovery, extraction, quality assurance and upload entirely within the media server — no external polling cron, no separate state management for file discovery.
### Context — current state
**Before the plugin: manual/scripted extraction and upload outside Jellyfin** — ffmpeg extraction (one call per file, all streams at once), upload to `https://api.subdl.com` with one account, a queue and uploaded-state JSON, lock files, a not-found retry, and a repeating two-hour job with a random delay.
**Architecture decision: one plugin, two pipelines.** Download and upload are integrated in ONE plugin; the downloader is NOT a standalone app or a second plugin.
- **Upload pipeline:** extracts embedded subs and uploads them.
- **Download pipeline:** searches for missing external subtitles and downloads them.
- Both share the same services and process, with separate switches, separate scheduled tasks and separate status counters.
- **Shared components:** one SubDL API client (login, rate limit, backoff, retry), one content-hash registry (every subtitle uploaded OR downloaded is registered under a normalized content hash, consulted before either direction acts), one language mapping / IMDB resolver, one config and status infrastructure.
**Known problems the plugin must solve:** media container language tags are often wrong (ISO 639-2/B inconsistent), so language verification is needed; and files in subfolders can produce path resolution errors.
**F-M37 [B1]:** **Modular layering:** strict separation into (a) SubDL API client, (b) content-hash registry, (c) language mapping/IMDB resolver, (d) upload pipeline, (e) config/status. Only (d) is upload-specific; (a)–(c) and (e) are reusable.

**F-M40 [B1]:** **DI registration via IPluginServiceRegistrator:** the shared services are registered with the service registry. The download pipeline is built outside it and takes the registry from the plugin instance.

## 2. Seeder — what enters the queue

**F-M1 [B1]:** Automatic upload of new media files via scheduled task, interval configurable (per F-M21). The task is a trigger only and starts a dispatch cycle; "on new file" is the arrival path (F-M1a).

**F-M4a [B1]:** Only process libraries selected in the config (F-M18)

**F-M189 [B1]:** **Library selection resolves to PATHS, so a selection keeps working when libraries are NESTED.** An item is accepted when its path lies under a selected root on a **directory boundary** (a selected path prefix must not match, and the comparison follows the filesystem's case semantics), or when the resolved collection-folder name is selected. The containment check also marks libraries that CONTAIN or are CONTAINED BY a selected path. The gate applies at the seeder's hard gate and its event-path gate, the collection step in both pipelines, the dispatcher's event-library resolution and the status controller's directory check. The selected names are resolved per call site, not cached across a run.

**The scope must handle subdirectories of a selected library:** an inner library created over an already-covered path stays empty.

**F-M1a [B3]:** ItemAdded real-time trigger — the item-added event subscription in the event dispatcher (the handler); there is no separate watcher class. Each arrival starts a non-resetting debounce timer (the arrival debounce window, default 5 min). On fire the dispatcher runs one cycle: seed → download → upload (download first, F-M149). Events during an active cycle collect in the pending ids. A follow-up reseed runs at cycle end only if that list is non-empty; the follow-up settings decide which directions take part. Arrivals that are still pending when the cycle ends start one further full cycle after a 90-second delay.

**F-M233:** **An on-arrival run works ONLY the arrivals; a full-coverage pass happens on the schedule or the manual button.**

The arrival path must be scoped to the items that arrived. Unscoped, one arrival window swept every gap in the library into the queue and the run died on the daily limit before reaching its own items.

The debounce window collects EVERY event in it, not merely the first, and arrivals during an active run form the follow-up window. An arrival cycle seeds and works exactly those ids.

Only `event` and `arrival-followup` are scoped. `scheduled-upload`, `scheduled-download`, recovery fires and the manual button carry no item scope and keep full coverage for the selected libraries.

"Kein treffer, kein lauf": an arrival-scoped round whose seed queued nothing for that direction ends; it does not work leftover queue entries.

An arrival cycle with an empty collector ends without a scan. Falling back to a full scan on the arrival path is a defect, not a graceful default.

**F-M244 [B1/D]:** **The queue item is a complete work order; the pipelines only carry it out.** The seeder decides WHAT is to be done and hands it over in the item; the downloader and the uploader execute that order.

The download pipeline reads its languages from the item and does not answer the HI question again — that decision is the HI wish plus the languages the order names. The upload pipeline likewise consumes the positions it was handed.

The pipelines keep ONE non-derivation check — the media file's existence. It is the loop's precondition (F-M60), not a give-up, and a file that vanishes between seeding and the run is reported as a failure. The **Seeder** refuses such an item before it can enter a queue (`Scan` skips on `!File.Exists`), and a file already gone from disk is the **Database Refresh's** phase 1b.

**F-M263:** **A dry run suppresses the media container rewrite — for BOTH directions — and the seeder is the only caller of the language gate.**

With a dry run (upload) OR a dry run (download) armed, the gate still resolves untagged tracks and reports them but writes nothing into the media file. Detection is not suppressed: a dry run's value is answering what it WOULD do (F-M22).

**F-M259 [D]:** **Loose.srt files are observed by the seeder's scan.**

The scan records every loose subtitle file beside the media whose NAME carries a language as an observation row, keyed by the normalized content hash (the loose subtitle's identity) with its language and HI flag. A loose subtitle whose name carries no language token is resolved by detection and renamed first (F-M278); if its language cannot be established it gets no observation row. `Status = observed`, no verdict; an existing verdict is never overwritten.

Content is the key, not the path, and the hash is the uploader's own function. Two loose subtitles with the same language but different text stay two rows.

**F-M278 [D]:** **An unlabelled loose subtitle is detected and then RENAMED to the shape this plugin writes — `<base>.<lang>[.sdh].srt`. Only a file whose name carries no language token is touched.** The run order is detect, then rename, then record. Detection reads the file's own text (no ffmpeg pass). The gates apply unchanged and in this order: switch **`Allocate missing language codes` off** (F-M314 — the same switch as the pack write, NOT the resolution of undetermined language codes), text below the 2 KB floor (F-M16), or no language found. All of them mean **the file is left exactly as it is** — no rename, no row; only a detection that names a language renames. A file that already carries a language token is never renamed. A taken combination takes the next free slot (`<base>.<lang>.01.srt`, F-M316; then `.02.srt`, `.03.srt`, …), never an overwrite, and the slot is per combination. A rename moves the stored row's location with it: the content hash — the row key — is unchanged, so the row keeps its verdict. A refused rename is not a failed observation: a file that cannot be renamed is recorded under its current name.

**F-M55:** **Download-on-arrival as its own switch** + UI restructure: "Download subtitles on arrival" checkbox (Download tab), independent of the upload arrival setting. Both directions share the same diced anchors/jitter.

**F-M57:** **Symmetric per-direction config, one shared rhythm:** each direction carries its own "… on arrival" checkbox — "Upload subtitles on arrival" and "Download subtitles on arrival". The scheduled rhythm is **not** per direction: the single refetch dropdown governs both (F-M111). Both directions share the same diced anchors; `Manual` suppresses the scheduled pipeline fires for both.

**F-M111:** **Queue-driven operation:** both pipelines run only over items the seeder placed in the in-memory queue. Runs are atomic per direction and serialized by a global run lock.

**F-M188:** **Without a chosen library the seeder does not start.** The check sits before the run lock, before the pass over the change stamps and before every database access; the same condition stands in the scan itself as well.

## 3. Upload Pipeline

**F-M2 [B1]:** Manually triggerable scheduled task (dashboard "Scheduled Tasks") for a full library rebuild

**F-M3 [B1]:** **A media file is upload material when it carries at least one text subtitle stream, or at least one loose subtitle file next to it.** Bitmap streams (PGS/VobSub) do not qualify: they carry no text (F-M6). A stream Jellyfin reports as external is a loose file already, not a media container stream. A forced track carries only the lines of foreign-language scenes and does not qualify either (F-M246).

**F-M4 [B1]:** Never upload an (item, language) pair twice — persistent state file in the plugin data dir

**F-M6 [B1]:** Text subtitle streams only (SRT/ASS/SSA, converted to SRT); bitmap subs (PGS/VobSub) explicitly excluded

**F-M7 [B1]:** **Extraction writes nothing into the media library, and its working files are removed on every exit.** Each extraction runs in its own directory outside the library, one per job. That directory is removed when the job ends — after a success, after a failure and after a cancellation. The removal is best effort and stays silent: a leftover working file must never fail a run, and it is never worth a warning.

**F-M596 [U]:** **The extraction of the next file runs ahead while the current one sends — the clock thereby becomes rate instead of preparation time.** Concept decision: the next file's extraction runs ahead while the current one sends. Measurement before (24 tracks, setting 500/h = 7.2 s): interval median 11.6 s = **9.0 s preparation** (of which the one ffmpeg pass), **2.4 s transmission**, **0.0 s waiting at the slot**. The pacer never braked, because the preparation lasted longer than the clock; the rate was 309/h instead of 500/h, without any limit having taken effect. New: `ExtractionLookahead` holds **one** lookahead. After the extraction of file N, that of file N+1 is started; it runs while N sends and waits for the slot. The result is taken only for **the same file and all required positions**, otherwise it is discarded and freshly extracted. Brought forward is exclusively the reading, side-effect-free step — sending, verdicts, database lines, counting and order remain unchanged; therefore a discarded lookahead is without consequence. Limits: one lookahead, one file ahead, memory = text of one file (a few MB at 43 tracks).

The second half belongs to it: an abort **really** ends ffmpeg (`proc.Kill(entireProcessTree)`), before only the waiting broke off and the process kept writing into an already deleted directory — invisible in the regular run, an orphan process with a lookahead.

**Addendum** (after the first measurement): instead of **one** file ahead the lookahead now holds **three** ready. Reason: measurement over 26 tracks at 500/h gave 10.0 s interval (median) = 7.6 s preparation, 0.2 s waiting, 2.3 s transmission, and only **2 of 26** tracks used the lookahead. The chain broke at skipped files — the lookahead applied to the next file, which the head exit then skipped, and the next that really ran began cold at ~8 s. New: the run registers up to three coming candidates (`Plan`, filtered by `QueueFilter` and existing text tracks), a pump works them off **one after another** (never two ffmpeg at once) and deposits the results. If a candidate falls out of the plan, its result disappears. Memory: text of at most three files. Checked by T306; the abort is additionally **really** checked (case 12 in `autosync`): start extraction, wait until an ffmpeg process runs, abort the token — afterwards the process count is back at its initial value (measured: 1 → 0).

**F-M613 [U]:** **The extraction pump permanently runs one file ahead.** Concept decision: keep 8 s per file, and **never two ffmpeg at once**. The lookahead (F-M596) was so far only registered **after** a real extraction; with 541 skipped items per run the pump therefore almost never got an order and ran empty — measured **2 of 34** extractions from the lookahead. Now `MeldeVorlauf` registers, for **every** item, the next up to three files that really need work (in the queue, with text tracks) — also on the path "item settled before extraction". That gives the pump long undisturbed windows in the skip stretches and the next file that really has to extract is already finished. The one-process rule remains: before a fresh extraction, `ExtractionLookahead.Freigeben()` releases the pump (abort + short wait), afterwards the plan registers anew and the pump starts again — **never two ffmpeg at once**. Checked by T323.

**F-M8 [B1]:** SubDL API (`https://api.subdl.com`): login (user/pass → token) and the upload endpoint. The call rate is configured, not fixed (F-M20)

**F-M9 [B1]:** Metadata per upload: series IMDB (**not** episode IMDB) + season + episode for TV; movie IMDB for movies — from Jellyfin provider IDs. Without IMDB → skip (F-M28).

**F-M10 [B1]:** Language mapping to SubDL's two-letter codes (`nob`→`NO` etc.)

**F-M48 [D2]:** **Loose SRT files as an additional upload source:** external `.srt` files next to the media file are uploaded in addition to embedded streams. The language comes from the file name. A name that carries **no** language token goes through language detection, at the same switch as an `und` stream (F-M74) and with the same outcomes. Switch off, text below the 2 KB floor (F-M16), or no language found all mean **skip**, never a guess. The marker is **`sdh` only**, and such a file is uploaded with `hi=true` (F-M71). `hi` is NOT a marker — it is the ISO 639-1 code for **Hindi** (ISO 639-1 `hin` = Hindi), so a `.hi.srt` name is a Hindi subtitle. Nothing in this plugin writes `.hi.srt`; the downloader writes `.sdh.srt` (F-M239). The HI flag is part of the identity in both areas (F-M197, F-M199).

**F-M88c [B3]:** **Upload-side short-circuit (the upload-side completion mark):** a file whose embedded subtitle side is complete is skipped before any ffprobe, directory walk or API call. Complete means every stream position reached a terminal state (uploaded or rejected). The field is named after the SUBTITLES, not the file: the video file itself is never uploaded.

**F-M21 [B1]:** Update interval: **manual / daily / twice daily / twice weekly / weekly / monthly**. A scheduled interval fires at the per-installation diced random anchor (F-M51). **manual** leaves no scheduled fire at all and keeps the dashboard and config-page triggers; "on new file" = F-M1a. **Default: Weekly**, shared by both directions (F-M111).

**F-M26b [D]:** **A deferred direction waits the job spacing — one knob, no hidden clock.** Whenever a stop defers a direction, the one-shot recovery fire lands **JobSpacingMinutes after the stop** (both directions), clamped 5–120, default 15. The user configures it through the existing **Job spacing (minutes)** field — one knob for all spacing, no separate control to discover. A deferral is recorded with the direction's own stored wording, so a run-lock push is not reported as a quota problem (F-M288). Anti-herd spreading is not needed here: the offset is per installation. The daily-limit reset keeps its randomised 30–300 min offset (F-M152, F-M182). The coordinator must not add a second offset on top — this path uses the recovery-fire scheduler, which leaves the caller's offset untouched. The offset must not affect the daily-limit reset.

**F-M249:** **The file-name parser recognises a bare episode marker without a season, and a broadcast date in the middle of the name with the title after it.** Every other shape keeps its result.

**(a) Bare `Exx`.** A name with a bare `Exx` carries no `S<d>E<d>`. The rule sets a series flag and the episode number, then falls through to the shared head/year/quality cleanup.

**(b) Mid-name broadcast date.** The shape is `<strand>.<YYYY>.<MM>.<DD>.<title>.<tags>`. The rule takes the text after the date, cuts at the first release/language token and drops trailing bare numbers.

**Scope of the date rule:** it applies only when all of the following hold:

- (1) no `SxxExx`/long-form marker is present;
- (2) the date is a **separated** `YYYY<sep>MM<sep>DD` (the compact `YYYYMMDD` stamp belongs to the TV-stamp rule);
- (3) month and day are in range;
- (4) **text follows the date**.

A name with text BEFORE the date keeps its title and year.

**Scope:** every other file parses byte-identically in title, year, type, season and episode, and no file loses its title.

**Rules:** the prefix of (b) is dropped, not combined; a bare `Exx` never invents a season; the compact TV-stamp path is untouched; and a bare `Exx` marker is accepted in either case, upper or lower.

**F-M252:** **A bracketed year is a year, and its opening bracket is not part of the title.** The tail-year rule accepts `(YYYY)` and `[YYYY]` at the tail.

**Rules:** (a) the tail-year rule closes the bracket; (b) the "year anywhere" branch cuts at the **start of the match** (the separator), never at the year; (c) the title cleanup drops an unclosed trailing `(`/`[`/`{`.

**Scope:** every changed file must be a bracket name.

**F-M250:** **The parser reads two series markers: `NxNN`, and `SxxExx` at the end of the name.**

**(a) `NxNN` (`2x01`).** Read as a season/episode marker.

**The `NxNN` rule cannot misfire on a resolution.** The season is capped at two digits, must not start with `0`, and may not be preceded by a digit. `1920x1080`, `2160x1080` and `0x01` yield nothing; `1x01` → S1E1, `10x05` → S10E5.

**(b) `SxxExx` at the end of the name.** The separator after the episode number is optional at the end of the name.

**F-M202 [B1]:** **An episode TMDB id is PROVEN against a candidate show — never guessed.**

Rule: title + season + episode → `search/multi` → candidate show → `tv/{show}/season/{s}/episode/{e}` → accept the show **only when the episode id returned there equals Jellyfin's id**. A confirmed match yields the SHOW ids (imdb via `/external_ids`; the detail endpoint reports `imdb_id: null` for series).

**Fail-closed:** no confirmed equality ⇒ `(null, null)` ⇒ the item is skipped like any other unresolvable series.

Season and episode come from Jellyfin's own metadata where it reports them. The parsed file name is the fallback and leads only when the name itself marked the item as a series.

**F-M29 [B1]:** **No user-identifying metadata in uploads:** exclusively IMDB ID + season/episode + language + subtitle file. No username, no library/path information, no source media filenames, no server/installation identifiers, no version telemetry. A content hash of the uploaded bytes is allowed in the filename: it is a property of the subtitle, not of the sender (F-M30).

**F-M30 [B1]:** Neutral upload filename: derived generically from the IMDB/language/release info — never internal JF paths or user names. **Order ("the full md5 makes sense"):** the name carries the **full MD5** of the normalized upload bytes directly in front of the language — `tt1234567.S01E02.<32 hex>.EN.srt` for an episode, `tt1234567.<32 hex>.EN.srt` for a movie. The name is built in exactly ONE helper that the dry run and the real upload both call, so the advertised name and the sent name cannot drift. The hash is content-derived and recomputable from the delivered file: it names the bytes, never the sender (F-M29 stands).

**F-M264:** **A media file is rewritten at most once — a track that already carries a tag in the media container is never written again.**

Within ONE cycle both seed passes write the codes: the DOWNLOAD-seed pass, and the UPLOAD-seed pass that follows. The second pass reads a stale stream list and finds the same tracks untagged.

Jellyfin caches its stream list, so a corrected media container keeps reporting its old tag for the rest of the cycle. No registry read and no self-observation of a freshly written tag changes that.

Before the extraction pass, read the media container's ACTUAL per-subtitle tags (a single ffprobe read, one ffprobe call, no remux) and drop every pending position that already carries a mappable tag. Those positions are recorded as ordinary tracks instead, so the registry and the queue decision still see the real language. When nothing remains pending, the run returns with `Written == 0` and logs `N track(s) already carry a language tag in the container; nothing to write` at Normal.

The missing-language switch sits on the **General** tab under `Media files`, directly above the statistics section.

**F-M239 [B1/D2]:** **One reader for loose subtitle file names.** The name→(language, hearing-impaired) rule exists ONCE (the loose subtitle reader); the uploader, the pipeline's missing-language check and the database refresh all call it. Marker is `sdh` only — never `hi` (F-M48). Recognized shapes:

- `<base>.srt` — no language, it is detected before upload (F-M48);
- `<base>.<lang>.srt`;
- `<base>.<lang>.sdh.srt`;
- the numbered slots `<base>.<lang>.<n>.srt` the downloader writes (F-M316).

`.part` is never a subtitle.

A marker is consumed as a marker and the language token is read from the position BEFORE it; a marker must never reach the language mapper. A name whose marker has no resolvable language token yields nothing.

**F-M266:** **The registry sweep removes OBSERVATIONS only — a VERDICT and a detection-attempt record are out of its reach.**

**F-M191 [B1]:** **A SERIES item must carry SHOW-level ids** — TMDB and SubDL expect the tvshow id together with season and episode, **never an episode id**. Jellyfin's provider ids for a mis-typed episode are EPISODE ids in **both** fields.

**Episode → show resolution:** the search by the IMDb id returns the series id — that IS the series TMDB id; `tv_results[0].id` is the series itself when the id was already show-level. The series IMDb then comes from `/tv/{showId}/external_ids`.

**An episode TMDB id cannot be walked up directly:** `/find/{episodeId}?external_source=tmdb_id` returns EMPTY and `/tv/{episodeId}` is 404. Such a value is first PROVEN against a candidate show (F-M202). Only when that proof fails is it dropped, and the caller's type-free title search then resolves the show by name.

**F-M391 [U]:** **A single upload that is absorbed into a pack is deleted beforehand — duplicate first, pack afterwards.** A pack that goes up against entries with the same content is exactly the case one does not want to settle on SubDL's side. The order makes the pack the only carrier. Deletion happens through the delete call with the entry id from the plugin database. The delete call by upload id is the wrong route for this: it answers with success and deletes nothing. The ids move into the `packs` row (F-M390). If the pack then fails, the log names the deleted ids as a warning, and the next run rebuilds the pack from the library.

**F-M403 [P]:** **One spelling for the mode: inline lower-case `dry`, on event lines as the prefix `DRY-RUN`.** The forms are: `upload: started, dry`, `download: started, dry, refetch=…`, `DRY-RUN would have uploaded …`, `DRY-RUN — N item(s) left Queued`, `cycle start (…, dry)`.

**F-M402 [P]:** **The log says it too: the cycle lines carry the dry-run note.** `cycle start (…, dir UploadOnly, dry)` and the two `cycle end` lines as well, set through a helper from the dry-run setting or the download's dry-run switch — so the log answers the question "was that a dry run?" just like the worker line.

**F-M401 [P]:** **When a worker runs in dry run, that already stands in the worker line during the run — "run dry".** The flag is written at start and at completion, filled from the dry-run setting or the download's dry-run switch. The marker is the word **dry**, grey, directly next to the result word.

**F-M400 [P]:** **The language line in the log states the finding instead of calling the tag a liar.** The line reads "LANGUAGE … — tagged X, detected Y; uploaded under Y". The content is the same, the tone neutral — the tag is a statement that is wrong, not an actor.

**F-M399 [P]:** **A pack counts as present only when the `packs` row proves it — an id on the members proves nothing.** The old second proof rule ("every episode carries a ScribeId") was wrong. A **single** upload writes its own id there too. So a season whose episodes had all gone up singly looked "already packed" and was never packed — in the dry run 98 seasons for which a pack never existed. The counter and the heuristic are removed; the pack check is the only proof.

**F-M397 [U]:** **If even one `.srt` of the pack originally comes from SubDL — downloaded or unpacked from a downloaded pack — no pack is built for this season.** It is not the member that drops out, the whole pack drops out: a pack around such text would by construction be a republication of what SubDL itself released. The check is content-based (via the `downloaded` loose subtitle row for the content hash), so that renaming, moving or re-assigning does not defeat it, and it sits **before** the first deletion. Log line: "season pack refused — … came from SubDL itself".

**F-M396 [P]:** **A pack that is already up with exactly these members does not go out again — and a pack row that nobody carries any more is removed at the database refresh.** The re-upload guard sat only in the fill-only plan. The buffer path uploaded blindly as soon as something for the season lay in the queue. Now the coordinator checks the `packs` row before deleting: same episode set and same release means "nothing new in the container". A new episode changes the set, then the pack goes out legitimately. Second, the refresh did not know the `packs` table: if a season's files disappeared, the row stayed and silenced the season for good. The cleanup check removes every row whose pack identifier stands on no subtitle row any more — and counts it in the refresh.

**F-M395 [U]:** **No pack ever arises from downloaded subtitles.** Fill material must have been **uploaded** by this account; the proof is the row of exactly this track with status `uploaded`, not the content check — that counts a download and a rejection as known just the same. Putting a downloaded subtitle back into a pack would mean republishing under one's own name, as a new pack, what already lies at SubDL. The refusal carries its own log line ("only known from a download or a rejection").

**F-M394 [U]:** **Every season complete on disk is packable — regardless of how much of it stands in the queue in this run; what is missing is filled from what is already uploaded.** The old rule ("pack only when all episodes are queued, otherwise single uploads") left a dead end. A season with 7 of 8 queued was neither packable nor filled, and the queue ran as single uploads — 39 languages of one episode instead of one pack. Now exactly one place decides whether the pack can go up: the coordinator checks its buffer, fills the gaps through the filler and either uploads the complete pack or falls back to single uploads. The queue determines only the **order** of selection (fresh content before fill material), not the feasibility.

**F-M389 [U]:** **A season pack may be completed from subtitles that are already uploaded.** An episode missing from the run's queue because its subtitle went up earlier is filled in from the library instead of dissolving the season into single uploads. A season whose episodes are ALL up already is a pack of its own, with nothing new in the queue. Every member passes the same gates as a single upload and must be known to the registry as uploaded; a never-uploaded or gate-failed text never enters a pack. One release, one quality, one frame rate still hold, and when one episode cannot be filled, nothing is sent.

**F-M601 [U]:** **The episode count of a season lies in the plugin database — first the database, then IMDb/TMDb, then store it.** Concept decision: the IMDb/TMDb episode count inquiries are stored in the plugin database at the pack after the first time. Measured (run 14:29): the pack planning asked the episode count for 79 seasons — **16,248 ms for 79 calls** (206 ms per call), a good fifth of the pack phase, and anew after every restart, because `SeasonEpisodeIndex` holds only in memory. New: `AiredSeasonStore` (table `aired_seasons`, key `imdbId|season`, value the episode numbers) holds the answer.

**First the database**, only when it has nothing (or the stored list does not cover all episodes that lie on disk — a season grows) are IMDb/TMDb asked and the result stored. An age of 180 days lets an answer expire, so that a corrected season is fetched anew at some point. The table name in `OnModelCreating` and in `EnsureAiredSeasonsTable` is **the same** (`aired_seasons`) — a table that is created under one name and read under another is dead ballast that looks like a working cache. The planning line now also names how many answers came from the database. Checked by T311.

**F-M604 [U]:** **The pack verdict lies in the plugin database — with a signature of the members.** Concept decision: the stored verdict carries a signature, and a new signature triggers a new attempt to form a pack. The signature is exactly the cheap variant: the **aired episode numbers** plus, per episode file, **episode number, size and storage date (UTC ticks)**. One `stat` per file, no read (a checksum over the data would be the same, only more expensive; the OSHash in the database would be exact but demands reading the file).

New episode, newly ripped episode or replaced episode → different signature → **new attempt to form a pack**; same signature → the stored "no pack" holds and the season is done in one step (no language scan, no group walk). At **pair level** the number of tracks known in the database (`|store=N`) sits additionally, because a newly arrived track does not change the files but can make the pack possible. Stored are only `no-pack` verdicts (reason in plain text, table `pack_seasons`); "a pack was possible" writes nothing, because the pack itself stands in the pack register. Checked by T314.

**F-M611 [U]:** **The pack verdict is now also stored for the fill attempt.** Concept decision: 1x is enough — then plugin database verdict and status, and when the status changes a new attempt to form a pack. The skips of the fill attempt (`episode N cannot join`) were **never** stored, only those of the planning. Measured: the same season was on 18.11. completely unpacked **17 times in one day** and aborted each time at the same episode (`tt8806524` S2 [JA]: "none of the 1 track(s) of this language qualifies (the text speaks ZH)"). Now the pipeline gives the coordinator the file signature of the season (air count + size + storage date per episode file, as in F-M604). Every abort — before the extraction (F-M600), after the extraction (F-M442) and on a missing language in the files — writes `NoPack` with this signature into `pack_seasons`. The next run checks the signature first: equal ⇒ **no extraction**, changed ⇒ new attempt. Checked by T321.

**F-M600 [U]:** **A pack that fails at one episode no longer unpacks the episodes before it.** Concept decision: the pack asks the database per episode before the first extraction. The measurement of the pack phase: run 14:29 had **8 fill attempts in 45.7 s and 0 packs**. Each failed at "episode N cannot join", the most expensive one (tt9737326) after 20 s, because only episode 3 tore the language gate. Before the first extraction the pack now asks the database per episode: does the file have any text track of this language at all, and is not every one of these tracks already locked (`SpurIstGesperrt`, F-M443/F-M584)? Both costs one track list and a few database looks. **The verdict is unchanged** — the extraction would have rejected the same episode just the same; it only comes earlier, and the line names the reason ("cannot join (no embedded text track …)" / "(every track … already refused …)"). Checked by T310.

**F-M68:** **The rejection record is persistent and keyed per (file content hash, track).** A track that the quality check rejected or that dropped out as a duplicate is not extracted again; the single and the pack path read the same record.

**F-M69:** **One media item costs one search, not one search per track.** The upload works in three stages: first all tracks and loose subtitles are checked locally (extraction, quality, duplicates), then one search with a comma-separated language list asks for all open languages, then what is missing at SubDL goes up.

**F-M71:** **An SDH variant goes up with the SDH flag set.** The flag is `sdh`; `hi` is no mark but the language code for Hindi. Both directions read the SDH statement from the same two sources: the language flag first, the track title as fallback.

**F-M85:** **The track selection carries the optional marker.** A track index that does not exist does not abort the extraction and yields no duplicate track; the caller repeats the empty tracks one by one and books one that stays empty as a real failure.
### 3.1 Quality Gates — Upload

The upload chain, in the order the code applies it. Each switchable gate names its own switch and default.

**F-M16 [B2]:** **Minimum size and minimum cue count.** The 2 KB size floor applies to UPLOADS only and is always on. The cue count (30) applies in BOTH directions and is switchable per direction, default on. Below either, the stream is skipped as "too small".

**F-M13 [B2]:** **SRT parser validation (switchable, default on):** ≥1 cue, monotonically increasing timestamps, plausible cue duration (>0.1 s, <10 min — long music cues stay legal)

**F-M14 [B2]:** **Sync plausibility (switchable, default on):** cue span vs. media runtime (+5 min tolerance; all cues concentrated in <10 % of runtime = reject)

**F-M15 [B2]:** **Language verification (switchable in each direction, default on):** detected language of the content (bundled n-gram classifier) vs. stream tag; undetectable content passes (fail-open).

**F-M375:** **On upload a lying tag is corrected, not rejected.** A track whose text speaks another language than its tag claims is uploaded under the detected language — the tag is what is wrong, not the subtitle — and the correction is logged with the tag that lied. A tag that names a relative of the detected language (the Norwegian written forms, Czech vs. Slovak, the Serbo-Croatian block) is no lie and is left alone. In the download direction the same check keeps rejecting: there the file is saved under its tag, so a corrected language would leave a mislabelled file on disk.

**F-M142:** **A pair already uploaded in this run is recorded as rejected, never as uploaded.** A stream whose (media content hash, language, hearing-impaired) pair is already up is skipped, and its position is recorded with the reason `duplicate-self-echo`. It is not marked `uploaded`. The hearing-impaired variant has its own key space, so a normal upload never blocks an SDH variant.

**F-M17c [B2]:** **Own upload dedup:** (item, language) pair + content hash in persistent state — never upload an already-uploaded or in-session-processed content hash twice. Depends on the canonical SRT form (F-M185): the hash is only stable across extraction runs when the payload is normalized first.

**F-M17x [B2]:** **Remote duplicate learning:** when SubDL rejects an upload with "Duplicate upload: identical file already published", the normalized content hash is stored as `duplicate-remote`. Future runs skip that content before any API call.

**F-M74:** **An `und` stream is resolved by detection before upload. Switchable, default on.** With the switch on, the detected language replaces the tag and the stream uploads normally; a detection that fails skips the stream. With the switch off, every `und` stream is removed from the upload set and logged. Detection needs text to work on, so a stream below the 2 KB floor (F-M16) is skipped as too little text.

### 3.2 Dry Run — Upload

**F-M22 [B1]:** **Dry-run switch, one per direction (both default off): the run walks its decision path, reports what it WOULD do, and writes nothing.** No file is transferred in either direction and no stored verdict is written. The two directions do not cost the same: the download dry run searches (F-M277), the upload dry run does not (F-M276).

**No stored verdict is written or cleared by a dry run** — a mark a dry run sets takes the work away instead of describing it, because the next real run reads the mark and skips the file.

**The rule covers every write that sits BEFORE the mode's own exit, not only the writes after it** — a dry run exits per item, so a write in the per-item preamble runs unless it is guarded at its own site. What that covers, and the two sanctioned exceptions, are stated once in **F-M287**.

**F-M276:** **The upload dry run walks the upload decision path up to the transfer and spends no API call.**

It runs: the extraction and the quality gates of 3.1, the id resolution, and the per-file candidate list.

It does not run: the login (F-M8), the duplicate pre-check — which a real run does not make either (F-M184) — and the upload transfer. No search call is made, so no search quota is spent.

The report is one line per stream: `DRY-RUN would upload <neutral name>`, the neutral name being the one a real upload would send (F-M29, F-M30). **Since F-M496 one line per stream AND per source.**

A dry run writes no completion mark (F-M88c). It has no retry counter to leave stale any more — that state went with F-M60.

**F-M496 [D]:** **The dry run plays through the chain, not a single target — and it demands no credentials.** Two holes, both measured in the testbench: (1) **The dry run did not know the second source.** It stood as **one** exit before the chain (`F-M22`) and wrote **one** line `[SubDL] DRY-RUN would upload …`. Since F-M495, however, **both** endpoints ask one after the other, and exactly that the report concealed: the second switch, the second line, the order. Now **every source gets its own line**, in **the same order** in which the chain would ask them (`UploadSourceOrder`, **one** enumeration for chain and dry run instead of two truths), and only **after** its toggle was asked via `SourceAllowed`. A switched-off endpoint does not appear, the other does. The lines read `[SubDL] DRY-RUN would upload <neutral name> [<lang>]` and `[tvsubtitles] DRY-RUN would upload <neutral name> [<lang>]`.

The gates apply in the dry run **read-only**. A stream whose content already lies at one source (F-M489) or that belongs to another source (F-M487) is named with reason instead of being output as "would upload". The dry run shows what the real run would do, not what it could do. **No API call, no login, no write from these lines**. The QA stage above still writes its rejections in the dry run too — its own, older place. The tvst branch had **no protection of its own** up to here: the exit covered the chain only as a whole, and the endpoint itself still fetches the show list in the dry run. The line therefore stands **before** every call, and the endpoint additionally receives the switch (`TvSubtitlesUploadEndpoint(…, dryRun: _config.DryRun)`) instead of a fixed `false`.

(2) **The dry run was turned away at the credentials.** `MissingCredentials()` demanded user name and password for a SubDL upload, although a dry run makes **not a single** call — the run thus died of something it would never have used. That demand is gone: what a source needs **to start** is asked **live** by `IsConfiguredForUpload` (F-M497). In the dry run it is not raised. The two rules do not contradict each other: in the dry run nothing is *necessary*, a source must be *present* only when it actually steps up. Verified by T215.

### 3.3 Season Packs — Upload

This subsection summarises what a pack is, when it may arise at upload and what it must not contain. It introduces **no new requirements**; the normative sentences stand under the cited ids.

**What a pack is.** A pack for exactly **one season × one language × one variant** (SDH or non-SDH), filled with **all aired episodes**. 26 languages mean 26 packs, not one collection archive. SDH is mixed only with SDH. Forced tracks belong to the episode and never in a season pack; all members carry the same release (F-M389, F-M390).

**Who is packable.** Every season that is complete on disk. Since F-M420 the single path decides the order in the run; the queue determines only what arises at all. A season with 7 of 8 queued is filled and packed instead of falling apart into single uploads (F-M394).

**Order in the run.** An **arrival** run uploads singly only and builds no pack (F-M418). A **refetch** or **manually** started run first uploads every position singly (with id, F-M413) and then closes the season with the pack that supersedes the singles (F-M420).

**Where the members may come from.** Fill material must have been **uploaded** by this account: proof is the `uploaded` row of exactly this track, not "content known" (F-M395). If even one `.srt` comes from SubDL — downloaded or unpacked from a loaded pack — the **whole** pack drops out (F-M397). What was booked as `downloaded` on the download side is therefore permanently no pack material (§4.4).

**Gates and fallback.** The same gates apply as for a single upload (structure, runtime, known contents, minimum size, language check). If one episode fails, the season stays single for that language; the episode does not poison the pack (F-M387, F-M388).

**Duplicate protection.** A pack that is already up with the same members and the same release is not sent again. The proof is **exclusively** the `packs` row; an id on the members proves nothing, because a single upload writes its own id too (F-M396, F-M399).

**Absorbing and deleting.** Single uploads that are absorbed into a later pack are deleted at SubDL **before** the pack; their ids remain in the `packs` row. If the pack then fails, that stands in the log with the ids (F-M391). Only what the state carries as an **own** upload with id is deleted — the single path fetches the id after every upload from `/user/mySubtitles` and writes it into the same embed or loose subtitle row (F-M412, F-M413, F-M414).

**Dry run and transparency.** In the dry run nothing is deleted, uploaded or booked. The line says `DRY-RUN would upload season pack`. The worker line carries the note **dry** next to the result word, the cycle lines in the log likewise `, dry` (F-M401, F-M402, F-M403).

**Cleanup.** Pack rows whose pack identifier stands on no subtitle row any more are removed by the database refresh and counted in its sum — otherwise a disappeared season would silence itself for good (F-M396).

**Second source.** tvsubtitles.net also knows season packs (F-M641): a zip in the season slot of the site (`episode=0`), one pack per (language, variant), bound to the **same** switch as the SubDL packs — there is no second switch. The pack row in the plugin database names the source in its key and holds its own identifier per source, so that a pack of one source does not overwrite that of the other (F-M642).

The rules above (completeness, same release, no HI mix, duplicate protection via the `packs` row) apply to both sources. The tvst pack is built in the **full** run by the **same** collector as the SubDL pack. An arrival run leaves it lying (F-M643). A pack row rejected by the source remembers its reason together with date and content (F-M644) and is not offered again with unchanged content.

The tvst pack phase **always reports** (F-M645): if it occurs, it names at Normal level the number of pairs to be built and of complete seasons in scope. If it does not occur, it names the reason (arrival run or switched-off upload switch of the source). Before, it was entirely silent. In addition every tvst pack names itself **twice** at Normal level (F-M646): at build (number of episodes, zip name, target source) and after dispatch. A pair without pack names the reason (F-M646).

**F-M405 [P]:** **The pack filler reads a file once per run, not once per language.** For one season with 37 languages that was 37 full ffmpeg passes over the same pack — with eight episodes 296 accesses where eight suffice. The filler uses the same one-pass call as the single upload since F-M5 and keeps the result per media path for the duration of the run.

**F-M406 [P]:** **The pack loop reports progress, so that the run does not count as standstill and gets aborted.** The run monitor aborts a run after six minutes without a sign of life. The pack loop built silently and was therefore killed after eleven to twelve minutes in the middle of the plan. With 659 announced pairs, 28 packs remained, which looked like incapability and was an abort. It reports every episode it reads.

**F-M407 [P]:** **The aired episode count comes from TMDb, and future episodes do not count.** IMDb answers the season query with the whole series — for `tt33270224` S1 with `total: 20` and the numbers 1 to 20, where the season has 7 episodes and 4 have aired. That one number decided completeness: the season could never become complete and stayed single for good. TMDb is asked first (the season endpoint is correct per season), IMDb remains the fallback for titles TMDb does not know. Aired dates do not matter in this: a season is complete when its episodes lie on disk.

**F-M408 [P]:** **A season is complete when all episodes come from the same release group.** A pack carries one release, one quality and one frame rate; a season from two groups is no pack. The group that supplies all aired episodes wins, and only its files may come along — with Cupertino S01 (`-MeGusta` E01/E02, `-ELiTE` E03/E04) no group covers the season, it stays single. The episode set itself comes from TMDb (F-M407).

**F-M410 [P]:** **The pack filler asks the media hash once per episode, not twice.** Proof hash (F-M395) and candidate hash were two calls of the same query; each wrote its own oshash row.

**F-M411 [P]:** **A cache hit is logged once per file, not once per track.** The hit line arises once per file and names the number of hits, not one line per track.

**F-M412 [P]:** **The state answers two questions per single episode: whether we uploaded it, and whether we can delete it.** The first stands in the status of the embed row (`uploaded`), the second in its SubDL id — without an id there is nothing to delete. The pack therefore names every member episode without id before sending: `… carries no SubDL id in the store — uploaded by us, but not deletable, they stay beside the pack.`

**F-M413 [P]:** **The upload id of a single episode is caught up at upload when the answer contains none.** SubDL answers an upload with "subtitle sent for review" and without id. The id stands in the list of one's own subtitles. It is fetched there directly after the upload — first via the file name, otherwise via IMDb id, season, episode and language, newest first — and written into the same row. Without it, a pack can never delete the single episode it replaces.

**F-M414 [P]:** **The upload id is fetched for single episodes and for season packs by the same route — from `/user/mySubtitles`, directly after the upload.** The trial run proved it: single upload → `id 376477`, season pack (two episodes, `is_full_season=true`) → `id 376481`, in each case seconds after the answer `"subtitle sent for review"`. The match runs over the **release name**, because the list names the entry that way (`…S01…` for the pack, `…S01E08…` for the single episode) and not after the file name. The language there appears spelled out ("Turkish") and is compared via the language mapping. Without a hit, the pack keeps the name as identifier, the single episode keeps the column empty.

**F-M415 [P]:** **The season pack check runs before the extraction.** If the season in this language already lies in the plugin database as a pack, not a single extraction happens. The log says `season pack skipped — …: already up as a pack, nothing is extracted.` Before, the run first pulled every episode out of every file and then established that there was nothing to do — the most expensive part of the cycle. The remaining pre-checks (mixed releases, incomplete season, QA failure) still prevent only the pack, not the single uploads.

**F-M416 [P]:** **Completeness is decided per language from the state, before the extraction.** A season can be complete for EN and incomplete for DE, because two episodes carry no German track. The episode query per language reads from the plugin database which episodes know this language. If one is missing, no pack arises for this language ("… no pack for it, this pass sends nothing"). The episodes that are there go up via the single path (F-M420). Together with the pack check (F-M415) and the release group (F-M408), all pack decisions are thus possible before the extraction; only the QA failure remains with the subtitle text.

**F-M417 [P]:** **The check order is: plugin database, then file names, then extraction.** Per season the planning first queries the languages of the season, then for every known language the pack check and the episode query. Only when at least one language is open and complete is the release group read and the candidate row written. If nothing remains, the season ends before any extraction ("already up as a pack, nothing is extracted" or "not every aired episode carries this language in the plugin database"). The most expensive work thus stands at the very back.

**F-M418 [P]:** **An arrival run uploads singly only — no season pack arises there.** The question of which season is complete is not even asked in the arrival (without a full run), every position goes via the single path; the log says it with `no season pack this run — an arrival run uploads single only`. Reason: the arrival run works off the arrival that triggered it, not the season. Technically the dispatcher passes the cycle reason down into the pipeline (direction run → pipeline → its fill switch = !IsArrivalTrigger(reason)`), true for everything except `event` and `arrival-followup`.

**F-M420 [P]:** **In a refetch and a manual run the rule is: single upload first, then season pack.** Nothing is held back for a pack any more — `packable` stays empty, no candidate lands in a buffer, the closing order falls away; every position goes up singly right away and writes its SubDL id in the process (F-M413). The pack plan is therefore computed **after** the episode loop from the plugin database: a season is packable per language when every aired episode carries an `uploaded` row and no `packs` row exists (F-M399). The pack then arises from the same texts and deletes the single uploads whose ids it knows beforehand at SubDL (F-M391). Computed before, the plan did not see the uploads of the same run and the season waited a whole cycle for its pack. Consequence: a complete season costs single uploads **and** a pack per run — intended.

**F-M421 [P]:** **The SubDL upload id of every single episode stands in the log — always, and with its origin.** Until now the single path was silent when the upload answer already contained the id or when it could not be determined at all; the line was written only after a catch-up from `/user/mySubtitles`. Now there is exactly one line per upload:

- `upload id {Id} from the upload answer for {File} [{Lang}]` — the id came in the answer;
- `upload id {Id} taken from mySubtitles for …` — caught up (F-M413);
- or, when none can be obtained, a warning `no upload id for {File} [{Lang}] — this single stays beside a pack that replaces it, because there is no handle to delete it.`

For a missing id the pack names `no pack id for {Imdb} S{Season} [{Lang}] — the pack row carries its name`. Reason: the id is the only handle with which a later pack can delete the single episode; a silent gap is an inexplicable remnant at SubDL.

**F-M422 [P]:** **The rejection of a season names its evidence.** Until now the line said only "no language is uploaded for every episode, no pack is built". That was a verdict without proof, from which it did not emerge whether an episode was missing, whether the SDH flag split the language into two pairs or whether a media hash no longer matched. The log follows:

- `Closest: [EN] 9/10, missing 4 | [DE/SDH] 3/10, missing 4,5,6` (up to three closest pairs with count and missing episodes);
- `Episodes with no uploaded row at all: 4,7` (episodes for which no `uploaded` row exists at all — that is a hash/orphan problem, not a coverage problem).

Reason: the check is the bottleneck between 7,958 uploaded rows and the packs actually built; without these numbers every explanation for it is a guess.

**F-M423 [P]:** **Already released uploads can be taken over into the registry** (the takeover endpoint). The completeness check of a season reads exclusively `uploaded` rows of this registry — rows that only this plugin writes. Everything that an earlier seeder released under the same account is therefore invisible and yields neither completeness nor pack. The endpoint takes per entry `{path, subPos, language, subdlId, hearingImpaired}` and writes exactly the row the uploader would have written: `Status = uploaded` with the id that makes the single episode deletable later (F-M412). It uploads nothing, deletes nothing and does not address SubDL — it only records what this account released anyway. The answer counts taken over, file missing, unreadable, id missing, position wrong; the log line names the same numbers with the file count.

**F-M424 [P]:** **An arrival run checks whether its own items close a season, and then builds the pack exclusively from them.** F-M418 had taken the arrival run out of pack building entirely. This instruction replaces that. Procedure: after the item loop, per (series, season) it is compared whether the episodes **queued in this run** cover the aired episode set. Yes → `season pack closing` from exactly these episodes (the pack planning receives the permitted episode set as a cut, the fill run goes over the same set). No → `arrival: … brought {Have} of {Aired} episode(s)`, no pack, the episodes go up singly. The cut is the actual promise: the library usually holds the season completely, without it the pack would contain members this run never touched — rows from the plugin database instead of from the arrival. Non-arrival runs (refetch, manual, scheduled) are unchanged: there F-M420 still applies (single upload first, then season pack from the plugin database).

**F-M425 [P]:** **The pack rule applies the same for every trigger.** One run — arrival, refetch, manual, scheduled — asks the same single question: are the episodes queued in **this** run the whole season? Yes → pack from exactly these items. No → no pack, the episodes go up singly. The fill-only path thus falls away (F-M418): it built packs from rows that an earlier run had left in the plugin database, without this run having touched any of these episodes. The property is not set, and the cut (the permitted episode set) is passed to plan and filler for every run. The log lines read `queue: … brought {Have} of {Aired} episode(s)` or `queue: … brought all {Aired} episode(s)` — the trigger is irrelevant to the decision.

**F-M426 [P]:** **A season whose languages all already lie in a pack is not investigated.** The question "are the queued episodes the whole season?" (F-M424/F-M425) is therefore asked only when the open-language check affirms it. For the queued episodes the language of their `uploaded` rows is held against the pack check. If every language is already inside a pack, the season ends with `queue: … every language is already inside a pack, nothing is investigated` — no extraction, no plan row. A season without any `uploaded` row counts as open (nothing is packed either). The lines that only reported that a pack already exists thus fall away.

**F-M427 [P]:** **The database reset also deletes the archive files area.** `scope=all` additionally removes `<Datenordner>/archive-cache` via `ArchiveDiskCache.PurgeDirectory` (all `*.zip` and leftover `*.tmp`). The log line names the number (`status counters zeroed with the database, N cached archive(s) purged`). Reason: a reset says "forget everything" — keeping archives would mean serving the next run files from a state that exists nowhere any more. The files area is plugin-owned and not part of the plugin database (F-M370), hence the separate cleanup step; `Trim` remains responsible for age and size unchanged. Other scopes (`upload`, `download`) leave the cache untouched.

**F-M428 [P]:** **The defaults of the archive files area are 7 days and 256 MB**. Changed in the configuration (archive files area 7 days and 256 MB) and in the configuration page, which uses the same numbers as fallback when a field arrives empty. A week covers the case for which the files area was built — a season archive that spreads the free daily limit over several runs (F-M370) — and the files area stays small. **Existing installations keep their stored values**, because the configuration spells out the numbers; the new defaults apply to a fresh installation or after resetting the fields.

**F-M429 [P]:** **The pack runs before the single uploads.** After the season planning and the login, but before the item loop, every planned pack is filled and uploaded; only afterwards do the singles go up. What a pack has taken over the run remembers in `_packAbsorbed` (series, season, language, variant); a position from it is skipped (`carried by the pack built in this run, no single beside it`). Packs that do not go up leave no trace in this set — their episodes go up in the single run, so nothing is lost. Two consequences follow. (a) The pack planning takes the languages from the **embedded tracks** of the files instead of from plugin-database rows, because a new run has no rows yet (the plugin database remains the fallback when the stream list is not readable). (b) The proof rule F-M395 is rewritten — a pack may be the **first publication** of a text (`first publication: the pack carries this text up`); text known only from a download or a rejection remains excluded.

**F-M430 [P]:** **The release group is read in all spellings the file name offers.** The old rule took only the last `-` segment; it thus overlooked three forms that carry the same statement: dot-separated (`…x264.YIFY`), space-separated (`…HEVC x265 BONE`, also in brackets: `…(1080p AMZN WEB-DL x265 Ghost)`) and with a site tag behind it (`…x265-MeGusta[EZTVx.to]`, `…x264.[MPup]`, `…x264-[YTS.LT]`). The rule: a closing bracket or bracket group is cut off and the remainder is asked first — that keeps the group before the site tag visible. Only then is the bracket content read (mark behind a domain only for short names, a long watermark like `THENKIRI.COM` is no group). After that the last token after `-`, `.` or space counts, but **only when the token before it is a technical one** (codec, source, audio, resolution, bit depth, HDR, edition); a segment with spaces is a title. In addition an underscore pair (group name with underscore pair) and a second extension (`.mp4`) are resolved. **Nothing is invented:** a name without release statement ("For All Mankind S03E04") still yields `null`, and a technical token like `-AAC5.1` stays `null`.

**F-M431 [P]:** **The pack filler tries every track of the requested language, not only the first.** Before, the fill search took the first embedded dialogue track that matched language and HI variant. If it failed — extraction, quality gate, a wrong language statement in the tag, or text known only from a download — the episode counted as not packable, although a second track of the same language lay in the same file. Now the fill search collects all matching positions (in file order, from the same enumeration the extraction reads from, so that a position means the same in both places) and the position check tests them individually. The first track that passes all gates becomes the member; every discarded one names its reason and passes on (`trying the next track of this language`), a successful successor is named (`track {Nth} of {Count} joins the pack`). Only when all tracks of the language fail does the episode stay outside with `none of the {Count} track(s) of this language qualifies`.

**F-M432 [P]:** **The upload id is also fetched from the publication list.** The id is the only handle with which an own entry can be deleted again (a pack replaces a single episode), and it stands in the list of one's own subtitles. That list carries only unpublished submissions and is therefore empty as soon as SubDL has released everything. A movie additionally has no season and no episode number through which an entry could be found again, so it had no id at all. New: the query of the publication list is read (newest first, page by page, as the API delivers it). The resolution of the own upload id and of the own pack id ask it when the submission list yields nothing, and match over the **release name** — the same handle that pack and single episode already use. The log line names the source (`… taken from myPublishedSubtitles for …`). The entry parser therefore also understands the spellings of the publication list (creation time, season as `s`).

**F-M433 [P]:** **The plugin database does not decide alone — the files are the second witness.** The completeness check per language asks the plugin database, that is what was already uploaded or observed. A season whose texts go up for the **first** time is unknown to the plugin database. The filler therefore also asks the files: the language query names the language pairs that lie in the tracks of a season (stream lists, no extraction), the carrier check answers the question per episode. The pack plan takes its languages from **plugin database and files**, and completeness counts as given when **either** the plugin database **or** the files show the language in every aired episode. Only when both do not find it does it stay at the skip (the log then names both numbers). The case without any track in this language still saves the extraction, because a stream list costs nothing (F-M416).

**F-M434 [P]:** **The files are also found at pack time.** F-M433 asked the files but got "no" for every language: the mapping episode → file ran via `episode.Series?.ProviderIds`, and that series object is not loaded when the pack phase asks — since F-M429 it runs **before** the item loop that fills the season anchors. Proved on "For All Mankind" S1: the files carry `eng` (E4 additionally 35 languages), the run nevertheless reported for **every** language "neither plugin database nor files". Two corrections: the completeness scan passes its episode items to the filler (`_seasonEpisodes`), and the mapping runs via the raw resolution (IMDb, season, episode number) instead of via the series object — in the carrier check as in the language query. The plugin database remains the first witness; the files are the second, which actually answers.

**F-M435 [P]:** **What is stored is the entry id, not the upload id.** The publication list carries two numbers per entry: `id` is the id of the published entry, `subtitles_uploaded_id` the internal upload id. The delete call `/user/deletePublishedSubtitle` takes the entry id; both numbers lie in different ranges, so a row with the upload id names nothing any more after the release. The entry parser separates them (entry id, upload id) and offers the handle that is to be stored: the entry id as soon as a published entry names it, otherwise the upload id of a still open submission. Season pack, single episode and movie store via this handle; the log line names both numbers (`entry id … (upload id …)`). Because a submission is usually still open at upload time, the postprocessing catches up the entry id. It reads the publication list, forms upload id → entry id and rewrites the stored handles in the three tables, reported as `stored handle(s) moved to their published entry id`.

**F-M436 [P]:** **An open fire is the last state — light and text say the same.** If a fire is pending for a worker, its row shows `defer` in yellow, regardless of the result of the expired run; only a running one (`run`) stays untouched. If the last run **failed**, the line under the list first names the failure with cause (`last run failed: <Detail>`) and then the next attempt (`— next attempt <Tag Zeit>`); "deferred" is only the fallback without its own cause. Red "fail" would be wrong at this point: the worker is not broken, it waits for its next attempt. **The failure line stays until the next run was successful** — it does not hang on the open fire, because the row of the workers list names only the word, never the cause. A healthy worker without fire says nothing.

**F-M437 [P]:** **The postprocessing pages up to the watermark, not to a fixed page count — and it runs daily.** The publication list comes newest first; the run remembers in the postprocessing watermark the newest creation time it has seen and ends as soon as a page reaches older entries. In normal operation that is a few pages; after a pause the same run catches up until it reaches the watermark. The page cap of the postprocessing (default 20000) is only the cap for such a catch-up run, not the depth of the run — default **20,000** pages, so that even a backlog of hundreds of thousands of entries remains reachable in one run. Such a catch-up run is braked by the runtime limit, not by the page count. The cadence stands at **daily**, because an entry id exists only from the release on and a fresh upload would otherwise not get it for up to a week. The watermark is deposited via the partial writer, so that an empty store cannot overwrite user settings.

**F-M438 [P]:** **A plugin-owned field that is not yet set in the configuration file is written so that Jellyfin also reads it on the next load.** The partial writer (F-M201) sets only the element text. Two cases get lost in this. A `Nullable<DateTime>` stands in the file not absent but as `<PostprocessWatermarkUtc xsi:nil="true" />` — the text was set, the `xsi:nil` remained, and the next load read `null` again. And a field the file does not know at all (configuration of an older version) was appended to the end of the document, and Jellyfin's serializer reads elements in **declaration order** — everything behind a later-declared element is ignored. Both looked like a successful save and were gone after exactly one restart. The writer therefore removes `xsi:nil` when setting and inserts an unknown field before the first element that the configuration declares **after** it. Both cases are recorded, the old spelling as a counter-proof.

**F-M440 [P]:** **A pack fill takes the subtitle that is already uploaded — it does not extract the file again for every language.** The text of an episode that is already out as a single upload is read from the file at pack build and then deposited in the **working folder of the run**. The file is the only place where it still lies, since the plugin database holds only hashes, status and SubDL ids. A second pack that needs the same episode reads it from there instead of calling ffmpeg again — and **only the tracks actually requested**, never all tracks of the media container.

**F-M441 [P]:** **The subtitles lie on disk only for the duration of a run.** The working folder is `<Temp>/subdl-scribe/run-<Zeitstempel>-<Id>`, one file per episode/language/variant/track (`tt1234567_s01e02_de_hi_p2.srt`). At the end of the run it is deleted; what an aborted run leaves behind is cleared away by the start of the next run (older than two days). There is **no persistent text store**, no cap and no cleanup task.

Order per season: **first build and upload the pack, then delete the absorbed single uploads by entry id at SubDL**, finally upload the episodes that were not accommodated in the pack singly. The deletion remains **before** the pack upload as commissioned (F-M391).

**Addendum (error against this requirement, no new concept):** The cache was there, but its key carried **language and variant**. So every language unpacked the same episode again (measured: 15 s per episode and language, ten episodes = 2:45 per language, Unicorn Academy S2 with around 20 languages nearly an hour). The first access now fetches **all dialogue tracks of one file in one run** and deposits them language-independently (`RunWorkFiles.WriteTrack`/`TryReadTrack`: series, season, episode, short hash of the file path, track position); every further language reads from disk. Line: `one pass over the file: N text track(s) extracted, kept for every language of this run`.

**F-M442 [P]:** **Equality of the source is checked BEFORE the first extraction, and the skip line says what really happens.** Quality stands in path and file name, the frame rate in the stream list — both readable without ffmpeg. If quality levels or frame rates deviate across the aired episodes, the season is rejected without extracting a single file. The line names the deviating episodes (`S01E04 (ATVP.WEB-DL/23,809)`). Instead of "nothing is re-sent — it is already up" the actual state stands: if all episodes are up, nothing is sent again; otherwise the missing ones go up afterwards as single uploads.

**F-M443 [P]:** **An already rejected track is not extracted again.** Before the ffmpeg call the fill asks the rejection record per (file hash, track) — the same one the single-upload path has used since F-M68. If all tracks of a language are rejected, nothing is extracted.

**F-M444 [P]:** **If an episode lies several times in the library (second version), every file gets its own attempt.** Before, the first find decided; if that file failed a gate, the pack died although a second version of the same episode carried the language too. The files are tried in library order; the line names which version won or why the previous one failed.

**F-M445 [P]:** **There is no fixed anchor for the upload postprocessing: every finished upload run re-terminates it, and the last order wins.** There is no interval, no diced day-plus-time anchor and no minimum spacing any more; a still open appointment is **cancelled and replaced**, not added. If upload 1 ends at 12:15, the postprocessing stands at 12:45; if upload 2 ends at 12:30, it is rescheduled to 13:00. **The appointment is cancelled at the start of an upload run** — an order from an earlier run would fire against a state this run is changing — and set anew at the end of the run.

**A user stop cancels the scheduled postprocessing run** — stop means stop; the next regular upload run schedules again. Lead time adjustable (the lead time of the upload postprocessing, default 30 min, 0–1440), **0–1440**, default 30 — **0 = inactive**: no appointment, and an open one is cancelled. The appointment survives a restart; with upload switched off nothing is scheduled (F-M291).

**The status page names it in its own line under the workers** ("Postprocessing due …"); the line disappears as soon as the run starts. Deliberately not at the worker light: since every upload run schedules, it would stand permanently on yellow and say nothing any more. Reason for all of it: the handle remap upload id → entry id and the clearing away of rejected uploads hung on the daily anchor and ran only the next morning. A pack built the same day still deleted its single uploads with the upload id, which the delete call by entry id does not know (F-M435).

**F-M446 [P]:** **An arrival run is recognisable as such in the pipeline core.** The arrival flag had stood as the literal `false` in both directions since the changeover to the queue scope — the scope (only the freshly arrived ids, F-M233) carried the restriction, the flag no longer told either direction that it is an arrival run. The two arrival exceptions thus never applied: an arrival run that hits the daily limit (429) does **not** wait it out but stops cleanly and leaves the waiting to the scheduled run. Otherwise it blocks the run lock for hours (F-M65, user decision option B). The flag is derived from the scope (non-null scope), the same in both directions. The scoping rule itself remains: **arrival loads only the arrivals, manual and refetch the whole queue.**

**F-M447 [P]:** **The scheduled postprocessing order stands in the worker status line as `schedule`, not as `deferred`.** The first run of this order is on plan; `deferred` remains reserved for deferrals. The word applies only to this order, all other workers keep their real deferrals. A failed line keeps its error text (F-M436).

**F-M448 [P]:** **A seed run that meets a running library scan is deferred instead of executed.** The measure of the deferral is the job spacing (default **15 min**, adjustable **5–120 min**). Wait lists, scan timestamps and the memo list of the items that arrived during the run (F-M233) remain unchanged; after the waiting time expires the run is triggered again. The status line carries the waiting time as `deferred` with "library scan running — retry in <N> min". Library scan and media refresh count as running.

**F-M475 [D]:** **A deferral is an appointment of the direction, not a timer — and it names its reason.** If a seed run meets a running library scan (F-M448), the waiting time is entered as a scheduled run of the affected direction: time = now + job spacing, without random addition. Only the direction that the cycle requested is entered; a run over both directions enters both. An already entered **later** appointment of the same direction is replaced by the earlier one — the earlier attempt wins; an already earlier appointment stays untouched, nothing is pushed back. The appointment survives a restart, because it lies together with its reason in the appointment file; the reason is kept as a short code (`quota`, `respaced`, `overload`, `scan<Minuten>`), because the appointment file separates at comma and colon. Next to the next attempt the status line names the reason of **this appointment**, not the cause of the last run; an expired appointment is no open appointment.

That applies to **every** of the seven workers: the two directions (download, upload) keep their reason as a short code, the three maintenance appointments (database, OSHash, postprocessing) keep `lockbusy`, because they wait only from an occupied run lock. Auto-sync and seeder have no appointment of their own. An appointment without short code shows the text of the last run.

**F-M476 [D]:** **The HI pool is walked, and it carries the main track when the regular source is exhausted.** The pool of the hearing-impaired versions is not read with one handle but walked in the ranking of F-M474. For every candidate the walk runs the burned list, the fetch, the gates and the adaptation. The first stored candidate ends the walk; a rejection names its candidate and the walk continues. It ends early only at the provider's daily limit, which counts as a deferral. If the regular walk of a language ends without storing — pack exhausted, pack discarded, no bytes — the same pool is walked as the source of the **main track**. The file keeps its SDH lines, carries the flag of the hearing-impaired version in the name and `hi = true` in the plugin database. Its storing counts as storing the language, which therefore does not count as unavailable. Both paths are one implementation. The pool exists only with the HI switch on; if it is off, it is not searched and there is nothing to walk.

**F-M379:** **The pack name carries the SDH variant.** An SDH pack names it in the name, because only the name is visible in archive, member file and entry title.

**F-M380:** **The width is decided by the resolution token, not the height.** A cropped image stays 1080p even when the height lies below the threshold of the next step.

**F-M381:** **A series name is not yet a release name.** The characters invalid in the file system are removed from the series name, so that none wanders into the archive name.
### 3.4 Upload completion criteria

**Why a chapter of its own.** "Done" was nowhere described up to here, but only decided in the code — and differently in two places. The queuer asked the tracks, the head of the run asked additionally a stamp that only a finished run sets. Result: **285** discards per cycle at 0 uploads, afterwards **292** discards (439), and files whose tracks all ended "already up" or "discarded" returned in every cycle. This chapter defines when a file is closed for a source — one rule that both sides call.

**F-M557 [U]:** **The closedness criterion is one stamp per source on the file; accounting is per track.** The stamp does not arise from a verdict space of its own but from the same rule the rest of the pipeline already uses: `IsEmbedTerminalFor` per dialogue track — uploaded at this source, rejected, or origin known. Positions, not (language, HI) pairs: the verdicts in the plugin database hang on the position. A second key would be a second truth (measured: "Resident.Evil.2026" reported 9 open pairs with 8 listed languages, and all 22 mst files with open pairs ended in the F-M541 exit, which proved the same tracks long since rejected).

At the end of the pass for a source it is checked whether **every present dialogue track** of this file was *uploaded* or *deliberately skipped* for this source. "Present" here means the number of **distinct (language, HI) pairs**, not the number of text tracks: a language that lies twice in the file is **one** upload unit. Since schema 16 the plugin database carries the language **once in ISO 639-2** (`ENG`, `DEU`, `LanguageMapper.Canonical`); every source gets its spelling only at the edge (tvst/mst via their `SiteLanguage` table, SubDL via `MapToSubdl`). Two keys on the same row would be two truths — in the first 441 run stood `ENG` and `EN` side by side: 839 open tvst pairs, 0 stamps. The migration rewrites the plugin database and clears the old stamps (schema 16).

Evidence ("His & Hers" S01E06): 38 text tracks, of which four languages twice (FRA, POR, SPA, ZHO) → **34** units; the run decided **35** (23× identical text, 11 language skips, 1 upload ES). With the track count as denominator the file would wrongly stand open for ever. Equal ⇒ stamp of the source on the media row (`Subdl…`, `TvSubtitles…`, `Moviesubtitles…`). Not equal ⇒ **no** stamp. The track verdicts in the plugin database remain diagnosis; they no longer decide "done". The column `VotedBy` thus falls away and schema 15 — the rule does not need them.

Concept decision: a file counts as closed for the source when all its tracks were processed in the pass or skipped.

**F-M558 [U]:** **A deliberate skip counts, a failed attempt does not.** Deliberately skipped means: the site does not carry the language (F-M538), the pair is already up at this source (self-echo), or the text is unfit (structure, runtime, too small, unmappable flag). A failed upload, an abort, a read error and a `deferred` do **not** count — then the stamp stays away and the file rightly returns. That is the place where "done" was hitherto claimed too early.

**F-M559 [U]:** **Every file accounts, and the accounting stands in the log.** One line per file: `12 tracks, 3 uploaded, 8 skipped, 1 unaccounted — no stamp for tvsubtitles` or, with coverage, `12 tracks accounted — stamp for tvsubtitles`. The unaccounted track is a statement, not silence; it is the measure of whether the verdict space is tight. If it stands at 0 over several cycles, "every track closed, per source" is reached.

**F-M560 [U]:** **Active means in play.** Completeness asks only sources that are switched on for this direction, are configured and have an upload path for the media type (`HasUploadPath`; for a series moviesubtitles.org does not step up at all, F-M544). A switched-off source blocks nothing — otherwise the film upload switch, off by default, would hold every movie open for good.

**F-M561 [U]:** **The language refusal becomes the source's voice.** F-M538 skips a language the site does not carry, today **without a line** in the plugin database. Under F-M557 that is a deliberate skip of this source — so it counts for tvst/mst, while SubDL keeps the same track open. Without this point tvst would stay open on every track whose language it does not carry (measured: **174** such cases, ID 28×, HE 18×, NO 18×, TH 16×, VI 16×, HR 14×, TL 12×, ET 6×).

**F-M562 [U]:** **Next to the stamp stands the track signature.** Position, language flag and HI per dialogue track, in position order (`PositionSignatureOf`) — the same counting as F-M557. If it deviates at the next look, the file counts as open and is queued again — otherwise a subtitle track added later or a remux would never be sent. The stamp alone would be a claim about a state that can change.

**F-M563 [U]:** **One question, two callers — and the queue arises from it.** `IsCompleteFor(file, quelle)` is the only place that answers "closed"; queuer and head of the run call it. The queuer forms the queue **only** from files that have at least one open source stamp, and the entry carries exactly these sources (F-M546/F-M549). A loaded plugin database thus produces no entries, no extraction and no API request — the full pass reads one plugin-database row per source per file and finds nothing. The early exit before the track pass (F-M541) counts only if it can prove that every present track already carries a final verdict for this source; otherwise the pass runs and accounts (F-M559).

Concept decision: the queue is formed only from files whose source stamp is not closed — that is why the daily pass does not work off the whole library and no incremental mode is needed.

**F-M571 [U]:** **The pack question is asked once per season and PER SOURCE, not per pair.** Packs arise at **every** source that builds them — SubDL and tvsubtitles (F-M641, F-M642); which (language, HI) pairs of a season are already packed is read by `PacksInSeason` **per source** in one query, the run remembers them per source (`PacksOfSource`). Before, the planner asked the plugin database per pair and wrote one line per pair — at one season ("The Thundermans" S1/S2) 67 queries and 67 lines in one minute, all with the same answer. Now one line per season (`N of M pair(s) already up as a pack`).

**Addendum:** The original version said "packs arise only at SubDL" and meant by that a **source-blind sum** over all `packs` rows. That was right as long as only SubDL built packs, and is wrong since F-M641: a SubDL pack marked the pair as done for tvsubtitles too. The query is therefore **per source** — a pack of one source says nothing about the other (the same rule as F-M489, F-M582, F-M584).

**F-M575 [U]:** **Both season questions read their table once per run, not once per season.** The collective question costs a full pass through all subtitle rows (9,385) per call. With 125 seasons that was 125 passes and around **6 seconds per season** — measured in run 449 (02:41:24 start, rows at 6-second intervals). The plugin database is 4.7 MB in size (1,415 file rows, 9,385 subtitle rows), one read of everything together is cheap; afterwards the question per season is a lookup in memory.

**F-M577 [U]:** **An extraction error is parked as the rejection `extraction-failed` — and only when the file is there at all.** Concept decision: no extraction error is parked before the file's existence has been checked. No field of its own, no schema step: the reason stands in the existing rejection record on the row of the track, and the time is that of the verdict row (`StatusAt`). The track is thus done — the file can get its completion mark, and the cheap head exit (F-M576) skips it. After **7 days** the rejection counts as stale (`ExtractRejectIsStale`): the track falls back into the extraction list without anyone having to delete anything. **Existence is checked first:** a missing file (share gone, path moved) is an access error and no verdict about the track — it gets no rejection and is tried again in the next run. If exclusively parked positions are open, the file ends with one line instead of a read pass. Verified by T287.

**F-M586 [U]:** **A source that does not carry the language does not count as "still wants it" — the pair is closed for it with `unsupported-language`.** Concept decision: a track whose language the site does not carry is closed for tvsubtitles or mst instead of being reported as still to be uploaded. Finding: tvsubtitles.net offers **24 languages** (en, es, fr, de, br, ru, ua, it, gr, ar, hu, pl, tr, nl, pt, sv, da, fi, ko, cn, jp, bg, cz, ro). Norwegian, Slovak and Estonian are neither in the `setlang` select (shows list and "Add TV-Show" form, 25 entries each with `all`) nor present as a flag: `sk.gif`/`no.gif`/`et.gif` → HTTP 404, `de.gif`/`cz.gif`/`fi.gif` → 200. Nevertheless the log named 1,316 DUE lines, of which **104 for Norwegian** ("still open for tvsubtitles"), because the list of open sources asked only switch, media type, proof and origin — never the language list of the site.

New: (1) `NimmtSprache(quelle, sprache, istSerie)` is the one place where language and source meet (SubDL without table → always true; tvst/mst via `SiteLanguage`, depending on media type); the question is a pure property of language and source, no switch and no plugin database. (2) `AnyUploadSourceStillWants` and the lists in `SettleDuplicateContent` filter with it — the DUE line names no source any more that does not carry the language. (3) If the **last** open source drops out only because of its language list, the verdict carries the reason `unsupported-language` instead of a content reason, and `PairIsDecided` closes the pair on it. It is written only when the pair is vouched for anyway — so no verdict arises that stems solely from a switch position and would block a SubDL switched on later. Verified by T296.

**F-M587 [U]:** **The two sources of a track run simultaneously, with a small offset — and the upload id is fetched only from page 1 of the own list.** Concept decision: SubDL and tvsubtitles upload in parallel, and the own-subtitle lookup is optimised. Measured on one track (`Elle.S01E08`, 19:28): tvsubtitles 0.87 s (`add1.php`) + 0.05 s (episode page), SubDL 6.8 s over five calls (`getNId` 0.99 s, `uploadSingleSubtitle` 1.34 s, `uploadSubtitle` 0.92 s, `mySubtitles` page 1 **1.94 s**, page 2 **1.58 s**). In between **9.8 s** transfer cadence. Together that is 17.5 s per track, one after the other. New:

- (1) The checks per source (switch, credentials, gate) still run **before** dispatch and in chain order. Afterwards all responsible sources are started **simultaneously** (`Task.WhenAll`), the second with an offset of 0–5 % of the transfer cadence (at 400/h up to 0.45 s) instead of a simultaneous start. A failure of one source is returned as a result, not thrown — otherwise it would tear off the dispatch of the other mid-way. The selection of the version still reads the **chain order**, not the response time; the marks per source stay separate.
- (2) `mySubtitles` is read only on **page 1** now: the API ignores `page`/`per_page` and repeats page 1 (in the daily log **3,426** page-2 calls at ~1.6 s ≈ **91 minutes** of waiting for nothing). The id never stands in the upload answer (**0** of **2,790** cases) — the list therefore remains necessary, but once instead of twice.

Verified by T297.
**F-M588 [U]:** **The SubDL id is fetched once at the end of the run, not after every upload — matched via key and creation time.** Concept decision: the SubDL id is resolved once, from the own-subtitle list at the end of the run. Finding: the upload answer never names the id (**0** of **2,790** cases), and `/user/mySubtitles` delivers no page excerpt but always the **whole** list. Measured **339,604 bytes / 798 entries** per fetch (pages 1/2/3 byte-identical, same MD5; `per_page` 10/100/500, `offset`, `limit` without effect). With ~2,790 uploads a day that was around **950 MB and a good hour of waiting time** just to look up one number.

New: the upload remembers the track (`PendingSubdlId`: IMDb, season, episode, language, HI, mark key, release, time) and writes its mark without id. `ResolveDeferredUploadIdsAsync` fetches **one** call at the end of the run and records the ids (`SetDeferredSubdlId`, hit via media hash + position or file name + language + HI). Matching runs per key (IMDb, season, episode, language) in the order of the uploads against the entries **by creation time**; every entry is consumed exactly **once**, so that the HI and non-HI pair of the same episode do not get swapped. What SubDL has already released no longer stands in the submission list — for that the published list is **once** pulled along (F-M432). If a track remains without a hit, that stands in the log as a warning; the row stays without id and the single stays beside a pack (F-M421). Verified by T298.

**F-M589 [U]:** **The transfer pause hangs on a real transmission of THIS track, not on the file.** Concept decision: the adjustable rate limit of 400/h applies only after a real transmission of this track — always afterwards, and otherwise no pause is paid. Up to here the pause stood on `itemUploadedThisRun` — a flag **per file**, set at the first successful upload and never reset for this file. From that moment **every further track of the same file** paid the 9 s, even when it was only skipped and transmitted nothing (measured: **41** such cases in **1,114** files, around 6 minutes a day). New: `dieseSpurHochgeladen` is set to `false` at the candidate head and to `true` only where `summary.Uploaded++` counts; the pause asks this flag. Effect: real uploads still lie 9 s apart (400/h remains), skipped tracks pay nothing any more. The file flag remains for the run count (`summary.FilesUploaded++`). Verified by T299.

**F-M590 [U]:** **Every source notes its acceptance immediately — and a timeout of one source does not abort the run.** Concept decision: each source notes its acceptance directly after its own upload, a timeout of one source lets the other carry on, and the run is an abort only when every source is gone. Finding: SubDL accepted the track of `A.Talent.for.Murder.2026…NeoNoir` (`uploadSingleSubtitle → 200`, `uploadSubtitle → 200`); at the same moment `GET https://www.moviesubtitles.org/search.php` ran into the 90-second timeout of its own HttpClient. The timeout comes as `TaskCanceledException` — that is, as `OperationCanceledException` —, the chain passed it on as a "real abort", and the whole run ended with `cancelled — 0 uploaded`. Consequence: the subtitle lay at SubDL, our plugin database knew nothing of it.

New:

- (1) `MarkSourceUploadNow` writes the mark of **this** source immediately after its answer (under a lock, because the sources run simultaneously since F-M587); the marks of the other source stay untouched, because `MarkEmbed`/`MarkSidecar` set known flags individually (F-M488).
- (2) An `OperationCanceledException` **without** a set `ct` is a timeout and becomes the failure of this one source (`UploadResult.Failed("timeout")`) — the other source carries on; only a real abort (user stop, watchdog, restart) flies on.
- (3) `_quellenAusfaelleInFolge` counts tracks in which **all** responsible sources landed in the timeout; at three in a row the run ends with its own line, because then the sites are gone and not the subtitle bad.

Verified by T300.

**F-M591 [U]:** **The same rule on the download side: a source timeout is a failure of this source, not a run abort.** The download did not yet have the handle from F-M590. Finding: the run had a movie before it, asked `moviesubtitles.org/search.php` (in the movie path via `MsubMovieResolver.ResolveForMediaAsync`, that is **before** the safeguarded search attempt) and got no answer; 90 s later it read `download: cancelled — keeping the partial result. 0 saved`. The previous run (05:59, before the lock) had saved 102.

New:

- (1) The movie path (TMDb inquiry + movie number of the site) lies in a safeguard of its own — `OperationCanceledException` **without** a set `ct` becomes a warning line and an empty answer;
- (2) the same applies to the search attempt of the additional source and to the main search — if SubDL fails, the next source is asked instead of ending the run;
- (3) `OperationCanceledException` **with** a set `ct` flies on, a real abort (user stop, watchdog, restart) remains an abort;
- (4) `_quellenAusfaelleInFolge` counts items in which **no** asked source answered — after three in a row the run ends with its own line (`_stopRunNoSources`, separate from the quota exit).

Verified by T301.

**F-M592 [U]:** **The fetch of the file and the reporting are safeguarded against a source timeout too — and a dead source is no longer asked for the rest of the run.** Concept decision: the safeguard covers the file fetch and the reporting as well. F-M591 had safeguarded only the **search**; the actual file fetch still stood bare: `DownloadCandidateAsync(...)` (tvst/mst via `endpoint.FetchAsync`, SubDL via `DownloadV2FileAsync`) lies in no safeguard, a timeout there tore the run along.

New:

- (1) The fetch is converted — `OperationCanceledException` **without** a set `ct` means "this source did not deliver the file", the next candidate is up; with a set `ct` it flies on (real abort).
- (2) `MerkeQuelleTot` keeps the failed sources of this run: a dead source is no longer asked for the **rest of the run** (neither search nor fetch) — otherwise every further item would again cost 90 s without prospect of an answer. The next run asks all again.
- (3) If **no** source remains, the run ends with its own line — exactly the case "only when all are gone is the run an abort".
- (4) The reports to SubDL (`FlushReportsAsync`) defer on timeout instead of ending the run (F-M450: open, not lost).

Verified by T302.

**F-M585 [U]:** **A single upload and a season pack no longer exclude each other — the pack steps beside the single.** Concept decision: SubDL allows a single upload that is at the same time part of a season pack. Three places hold up today's prohibition:

- (1) F-M391 deleted the single before the pack via `deletePublishedSubtitle` — the call falls away, the ids are named in the `packs` row as "standing beside it";
- (2) F-M429 skipped every position of a pack (`pack-carries-it`, 127 rows) — it now goes up additionally as a single, with a log line instead of a rejection;
- (3) the coordinator wrote `Uploaded` plus pack id into every member row — thus "uploaded" was a truth value for three states of affairs and the episode row lost its own single id to the pack id.

The pack proof now stands **only** in the `packs` row (`PackId`, `Episodes` = ticked-off episodes), the episode row is exclusively the single upload (`CoveredByPack` answers the pack question separately). The source control remains untouched: this rule is SubDL's single/pack relation (since F-M641 tvst builds packs too, own row per source), the provenance lock (`Provenance.MayUploadTo`) does not change, and `IsUploadedTo` means unambiguously "up as a single" after the changeover. No one-time cleanup: existing rows whose `subdlId` carries a pack id, and the 127 `pack-carries-it` rows, remain as they are — for already packed seasons the single upload therefore remains suppressed, "both" applies from the next newly built pack.

**The switch in the GUI:** `Upload season packs` carries **only** the checkbox (the source marker `SubDL only` from F-M526/F-M527 has fallen away with F-M641, because tvst builds packs too) — **no description text** beneath it. The checkbox switches pack building on and off, it says no more than that. Verified by T295.

**F-M584 [U]:** **A rejection locks a track only for the source it concerns — the pack builder compares per source.** Concept decision: the comparison runs only for the source the rejection concerns. `duplicate-self-echo` means "a source already has the pair"; up to here the pack filler read only "rejected" (`EmbedRejectedReason`, F-M443) and threw the track away — also for the source that does not have the pair. Measured on Hunting Alice Bell S01E01 (`tt37253101`): the EN-HI track lay at tvsubtitles, stood in the plugin database as `duplicate-self-echo`. The SubDL pack dropped out with "episode 1 cannot join, 5 of 6 episodes are up" (F-M442), although SubDL never received the episode. `SpurIstGesperrt` now locks only when no switched-on source still wants the pair (switch, media type, proof — F-M581); quality verdicts (too small, QA, pack carries it, language unknown, extraction failed) stay source-neutral and still lock for all. The head exit F-M541 asks the same question: it engages only when no pair of the file is wanted any more. Verified by T294.

**F-M582 [U]:** **"Duplicate" applies only to the same source — a text that SubDL has still goes to tvsubtitles.** Concept decision: duplicate holds only within the same source, so a text that already lies at SubDL still goes to tvsubtitles. Two places violated that:

- (1) `SettleDuplicateContent` wrote a source-neutral rejection without source proof and threw the candidate away — if the text lay at SubDL, tvst never got it. Now the candidate goes to the chain as long as **any** switched-on source may take it, and only when the origin is known (it was downloaded, F-M487) is the pair decided.
- (2) The content verdict from F-M580 closed the pair for **all** sources; it now closes only when the text may no longer go up anywhere.

The source check thus stays source-sharp: `IsUploadedTo` asks per source, a proof at SubDL does not settle tvsubtitles. Verified by T292.

**F-M581 [U]:** **The pair question checks the PROOF, not the circumstances of the run — and "no source wants it" is no verdict.** `AnyUploadSourceStillWants` additionally asked queue (F-M546) and language set of the site (F-M538); if the queue stood empty or the site did not know the language, it answered "nobody wants it" — and the caller wrote that into the plugin database as `duplicate-self-echo` ("already uploaded"). Because "rejected" is a final state, the pair was thus closed **for ever**. Measured: **700** such rows at **43** files, at `Invasion.S01E03/04/08` **38** tracks each (ET, HR, ES …) — whole files "done" without a single transmission. The plugin database itself holds **5,114** such rows, beside them 8,265 `rejected` with only 507 `uploaded`, although 8,433 rows carry a SubDL id. Rule: the pair question asks **only** switch, media type and proof; queue and language set the chain checks itself before it lets a source step up. A `duplicate-self-echo` is written **only** when a source demonstrably has the pair (`IrgendeineQuelleHat`); otherwise the track stays open without verdict. Verified by T291.

**F-M580 [U]:** **A stored verdict closes the pair — also for a sidecar file.** Concept decision: a verdict already stored counts as the decision and closes the pair. F-M557 has long since fixed the final state for embedded positions ("uploaded at this source, rejected, or origin known"). For a sidecar file the counterpart was missing, because its verdict hangs on the **content** and not on a position. The pair question `AnyUploadSourceStillWants` saw only upload marks — a rejected track thus kept the pair "wanted". Consequence in production (measured, `Best.Medicine.S02E06`): five times the same sequence of "still open: tvsubtitles, subdl", SKIP of the sidecar as "identical subtitle already known in this file" and "no stream survived local screening". All of it without transmission, without file mark, with a new counter reading each round.

The rule is one question. Does the pair (file, language, HI) carry a stored `duplicate-content` verdict — embedded **or** as a sidecar — **and** may the text no longer go up anywhere (origin known, F-M582)? Then no source wants it any more (`PairIsDecided`), and the pair question ends before the source gate. Second, a **stored** verdict is no new rejection: it does not count the counter "stopped before sending" again. Verified by T290.

**F-M576 [U]:** **The done entry ends with ONE look into the media row — before every stream fetch, before every position accounting, before every directory listing.** That is F-M88c, restored after the finding ("certainly not gigabytes of extractions and hundreds of db accesses"). Stand 12.1.12.449 the head asked `GetMediaStreams` per file, then the open positions per source, then a verdict per text track — with 58 tracks that is 58 plugin-database lookups, and that for **every** file of the library in **every** cycle. Measured in run 449 (3 h): 1,415 files, 16,395 writes, 139 ffmpeg passes (94 GB read) for 155 uploads.

Now the **completion stamp per source** from the same media row decides (F-M557): if all sources that this run serves are stamped, the entry ends here. The signature is no longer passed along on the read path — it changes only with the (language, HI) pairs of the file, and those change only with the file, which then has a new hash. The **sidecar file remains checked** (F-M556 must not come back): the directory listing falls away only when the directory is older than the youngest stamp (`NewestCompletionStamp`), otherwise it is asked. A switch switched on later opens the entry again, because the source list reads the switches. Verified by T286.

**F-M574 [U]:** **The comment carries three lines — ids, episode, year.**
`IMDb: tt15875168 / TMDb: 201124 — S01E05: Mo / Tombstone — 2022`

The site turns every line break into a space (seen in production), therefore a **dash** separates the three parts (operator decision). For movies (mst) the episode line falls away; there the movie title stands. Spelling **IMDb** and **TMDb** in official capitalisation. A line falls away when its content is missing (no title in the library, only a placeholder, no year) — nothing is guessed. One builder for both sources (`UploadComment`), so that tvst and mst do not drift apart.

**F-M573 [U]:** **The cheapest season question first — and that per source.** If all pairs that the plugin database knows for a season are already packed **for every source served in this run**, the season ends with **one** line — no TMDb call, no look into a file, no hash. Before, the expensive chain (episode count at TMDb, release groups, file list) ran for **every** complete season and reported at the end "already up as a pack". **Addendum:** The question is asked **per source**; a season is done only when **every** switched-on source has its own pack for every known pair. A SubDL pack does **not** end the season for tvsubtitles.

**F-M572 [U]:** **The episode title goes along as a comment — the file name stays that of the site.** tvsubtitles.net builds the file name from **its** episode entry. Measured on "Mo" S01E05: the library carries "Tombstone" (Jellyfin `ProviderIds` = TMDb 3701256), the site shows "Episode 5", and a title is no field that the upload sends. So the comment carries the title (`TMDB 3701256 / IMDb tt16274608 / Tombstone`), and only when it is no placeholder (`Episode 5`, `episode 5`, empty). The author-stays-empty decision and "ids only" continue to apply to everything else.

### 3.5 The ffmpeg handle — one pass, one count, one exit code

These three rules together are the reason why an extraction either is right or fails loudly. They stand here as *one* reading view, because they are only usable together; the requirements themselves are F-M405, F-M441, F-M658 and F-M659.

**1. One pass per file, not per language.** ffmpeg reads the media container once and writes one file per requested track. For three languages of an episode that is *one* process, not three:

```
ffmpeg -y -loglevel error -i <datei.mkv>        -map 0:s:0 -c:s srt -f srt out/pos0.srt        -map 0:s:3 -c:s srt -f srt out/pos3.srt
```

The result lies language-independently in the working folder (key: IMDb, season, episode, hash of the media path, track position — F-M441). The second language therefore costs no second pass; it reads the file that the first has already written.

**2. `0:s:N` counts only the subTITLE tracks IN the media container.** N is the position in media container order, **bitmap formats (PGS/VobSub) count**, the `.srt` side files next to the movie **not**. Two other counts are obvious and both wrong:

- `0:N` (absolute index in the media container) lands on video or audio — exit 8, "no stream".
- The index in Jellyfin's track list, which **carries external side files along**, is too large by their number. ffmpeg answers `Stream map '' matches no streams.` and ends with **234** (F-M658, found on MobLand S02E02, which carries two `.de.01/.de.51.srt` beside the MKV).

Therefore there is exactly **one** place that fetches the track list — `ContainerSubtitleStreams` — and all callers count through it. Whoever creates a second version creates the error again.

**3. No `?` in the mapping, and the exit code is a finding, not noise.** If the track is missing, ffmpeg must fail loudly; a silent substitute would deliver a *different* track than the one meant — and subtitles that fit the wrong episode are caught by no check. Conversely: **exit 0 means "something is said about the content"**, **exit ≠ 0 means "the pass went wrong"**. In the second case every position that stayed empty is **individually** re-checked; if it stays empty, that is no verdict and is not deposited as a memory mark (F-M659). Otherwise a one-off extraction error would lock the pair for all future runs.

## 4. Download Pipeline

**F-M151a [B3]:** **Download-side short-circuit — replaced by the derived open-pair question.**

There is no download-side completion mark any more. The pipeline asks the subtitle coverage which required PAIRS have no evidence (14.2b) and returns when the answer is empty. That answer is derived fresh on every run, so it cannot be overtaken by a deletion. It needs no stored language set and no validation pass. See F-M283 for why the stored mark had to go.

**F-M578 [U]:** **The search knows no candidate cap per language — it runs to the page budget.** The `DownloadMaxCandidatesPerLanguage` switch of the predecessor version (default 3) is gone. `SearchSubtitlesAsync` still only accepts the threshold as a parameter, and **no call site sets it** (default `0` = off). The early exit "enough candidates for this language, end the search" does not take place. A parameter that nobody sets is not a feature — whoever brings the rule back needs a requirement of its own including a switch. Concept decision: the absence of a cap stands as a requirement of its own, because the removal had been without evidence during the review of the store concepts. Verified by T288.

**F-M579 [U]:** **There is no mandatory IMDb switch — the ID ladder runs in both directions.** The `DownloadRequireImdb` switch of the predecessor version (default `true`) is gone; the gate question no longer exists. If the IMDb id is missing, the ladder fetches it from TMDb (`ResolveImdbAsync`) as soon as a TMDb key is configured, and only afterwards falls back to the title search. Concept decision: if TMDb is available, the IMDb id is fetched, and the switch's removal stands as a requirement. Verified by T289.

**F-M187:** **Downloaded bytes have exactly one decode path.** Every conversion of downloaded subtitle bytes to text goes through one path, which honours a UTF-16 byte-order mark (LE/BE, stripped) and falls back to UTF-8. The download side hashes the decode path, and the upload side later reads the written file as UTF-8/UTF-16 text, so the two must agree. An **uncorrected** file on disk stays **byte-identical** to SubDL's payload — normalization applies in memory for hashing and upload, never to the stored file.

The one exception is a file the correction CHANGED: that goes out canonical (F-M296), because the bytes on disk must be the bytes the stored hash describes. The archived original is always the fetched bytes verbatim (F-M306).

**F-M251:** **One name parser, not two.** Both directions read names through the same parser; the download side holds no copy of the name logic.

**Scope is arithmetic:** the files this change may touch fall into six named groups (`NxNN`, bare `Exx`, `SxxExx`-at-end, date names, long form, bracket titles), and the groups must add up to the diff total. If they do not, an unintended change hides in it.

**F-M41 [D, parallel]:** **Download pipeline in the same plugin:** search for missing external subtitles per item (missing languages against a configurable target-language list), download via the same API client, registration in the registry, storage as an external stream next to the media file. Own scheduled task, own switch, own status block.

**F-M42 [D]:** **Preferred languages:** target-language list (multi-select, no priority). Default `AR, EN, ES, FR, HI, ZH`; empty = download off. `DE` is absent.

**F-M42b [D]:** **Hearing-impaired version additionally** (checkbox, default off): when on, the best hearing-impaired candidate per (item, language) is downloaded in addition to the regular version and stored as `<basename>.<lang>.sdh.srt`. It comes from the second search (F-M241), and the branch must sit before the best-per-language cut break, or with the default one per language it is unreachable.

**F-M333 [D]:** **"Only missing languages" has TWO real modes. OFF means: once per language, whatever the file already carries.** *"If on: embedded counts as present, sidecar counts as present. If off: a single download of the language regardless of whether a sidecar or an embedded track is there."* **ON (unchanged):** sidecar and embedded track both count as present.

**OFF:** only **our own recorded download** closes a pair. Library sidecars, embedded tracks and the stream list stop counting, so every configured language is fetched once even when the file carries it. Reason: *"An embedded title disappears with its container"* — a language never fetched has no copy this plugin can re-derive. **"Once" is our own row, not the disk:** the reader RETURNS after the `downloaded` rows and never reads the file's own evidence. That evidence would close the pair again. Intended: the second download lands beside the existing file (numbered cleanly). A deleted subtitle heals, because the refresh forgets the row whose file is gone (F-M234). **No overwrite:** the slot is planned against the directory (F-M315). **One reader, three callers** — pipeline, seeder and refresh pass the mode in.

**F-M334 [D]:** **A DOWNLOAD dry run escapes the download brake, but not the search one.** *"Well, that sounds as if this brake would have to be exempted from the dry run test"*, and *"So continue when the download dry run test is on"*. **The defect it fixes:** with the account at 50/50 downloads, F-M331 held back the very run whose whole purpose is to report what WOULD happen. The dry-run test could not be started at all (measured on the test server: `download held back this cycle — "download limit spent (50/50)"`, then `cycle end`, not one dry-run line).

**The exception is HALF, and that is the point:** only the DOWNLOAD brake is skipped, because a dry run fetches no file. The SEARCH allowance keeps holding it in both modes. A dry run does search every item, spends search quota and draws every line it prints from those answers. Lifting that brake too would let it walk a library with the allowance spent and print nothing — the same wall F-M331 was written against. A real run reads both counters exactly as before.

**F-M335 [D]:** **The dry run consumes no work, and every worker row opens itself.** **(1) The download dry run marked its items `Done`:** the download outcome handler guarded on `_upInFlightDryRun` — the **upload** flag — so on a download run the guard never fired. A dry run wrote **7 `Done` rows, exactly the 7 items it printed a DRY-RUN line for**, and the next real run skipped them.

Same F-M22 defect the upload branch was already fixed for. Only one half was repaired, and an asymmetry reads as correct on either side alone. The dead half is the tell: `_downInFlightDryRun` was **written and never read**. Each direction now consults its **own** flag, asserted as write **and** read.

**(2) The Auto-Sync row was only ever FINISHED, never STARTED:** every writer called `Finish` for the alignment while `Start` ran for the seeder, both directions and the three refresh tasks. `Finish` falls back to "now" only when `Started` is null, so a stale stamp survived. Prod showed **a nine-hour run**: *"5 aligned, 5 already in sync (274.9s)"*. The row now opens from the download summary that holds the work, **before every outcome branch**.

**F-M340 [D]:** **The Auto-Sync row must be reachable while it runs.** Concept decision: a row that opens and closes in one instant must still be visible to the status poll while the run is under way. F-M335 sets the start-row entry in the same block — the same **synchronous block** — as its five `Finish` calls. So the row opened and closed in one instant and the 10 s poll never saw it blue (prod: the stamp read 99 s LATE).

It now opens at the **run’s edge**, before `await ExecutePipelineAsync`, and is closed by the summary path or, when a run ends without one, by the `finally` guard. **Not in the worker** (static, no `Plugin.Instance`, called PER CANDIDATE — the row would flicker) and **not around the sync measurement** (the candidate, not the run; never runs with the switch off).

**F-M336 [D]:** **A worker row may not claim work the run did not do.** The row's verb follows the run's own log words (**"would have saved"** / **"would have uploaded"**), so row and log can be compared word for word. The counters are untouched, because a dry run's counts are honest about what it FOUND and only the verb is wrong. The mode is read at the call site from the summary of the direction being written — each direction consults its **own** summary (the F-M335(1) asymmetry) — and passed in; a method that takes no configuration cannot derive it. The log line carries the same label (F-M247).

**F-M337 [D]:** **The General tab carries everything a run needs.** *"Target languages (download, required) please under library in general gui. That is not quite clean but then we have everything important on the first page"*, and on the shape: *"The download is missing in the heading and the heading is not a heading"*. **Target languages moves from the Download tab to the General tab, directly under the library selection** — libraries, languages and the schedule in that order: the first page answers the three questions a run depends on. The operator accepted the impurity himself (*"not quite clean"*: a download setting on a general page).

**Moved, not copied:** a second control would render on both tabs. Both would be bound at save and the last one read would win. **It is a real `<h3>` and it names the direction** — it was a field caption (a caption in a field block, the form this page uses for fields) reading *"Target languages"* alone, while its siblings name theirs. Only the control moves: the picker button, the modal and the four JS bindings stay and keep working.

**F-M260 [D]:** **The downloader reads the hearing-impaired flag of the file it actually fetched.** When a candidate resolves to one file inside a season or range pack (the pack member), that file's own `hi` flag governs naming and registration. For a plain single-file release the candidate's flag does, since the candidate is the file.

The HI block of the download loop is guarded by the *effective* flag, not the candidate's: when the file just saved already was the HI variant, no second download follows.

**F-M319 [D]:** **A language receives exactly ONE corrected file. The "best subtitles to keep per language" setting is REMOVED.**

**F-M242:** Withdrawn — the rule stands in F-M319.

**The rule.** The candidate walk ends at the first candidate whose correction proves itself against the audio. An **unprocessed** file does not end it: it is a fallback, kept so the repeat costs no content (F-M317/F-M318).

**Why the setting went.** It existed to save X numbered files per language. Once the slot encodes WHICH KIND a file is (F-M317) and the walk repeats until a correction is proven (F-M318), the count had one meaningful value left — `1`, its own default. Every other value asked for the same language twice. The setting, its GUI field, the slot preview, the second parameter of the download budget

**A removed setting must be removed at every layer** (config type, page, pipeline): a field left on the config type is a knob the next reader wires back up. Asserted in T131.

**F-M95:** **The search's early stop is the one correction-attempts setting, derived — never a literal.** The page walk stops once every requested language has at least N candidates, where N is that setting (F-M50); `0` disables the early stop. Until then the threshold was hard-coded to 3, so raising the setting widened the download side but never the search. The walk still stopped after three candidates per language and the loop never saw a fourth. Until then there were two settings feeding it; **one setting, one number** — the helper takes that value alone.

**F-M241 [D]:** **Two searches, one per side of the hearing-impaired split.** The regular slot is filled from a `&hi=0` search and the HI slot from a `&hi=1` search. The HI search runs only while the switch is on, so a user who does not want HI pays one search exactly as before.

SubDL filters HI server-side and the two pools do not overlap — a release appears in one or the other, never both. An unfiltered response is a MIXTURE, and the regular ranking would silently depend on how many HI releases it happened to contain.

A failed HI search keeps the regular candidates and logs it — the HI variant is a bonus (F-M42b), the regular subtitle is the target.

A dry run reports the HI pick from the HI pool; the real selection happens after the file download, which a dry run never reaches.

**F-M47 [D]:** **Configurable refetch interval:** manual / daily / weekly / monthly. **Default: Weekly.** The per-file last-search stamp (the last-search stamp and the stored language list) protects already-downloaded languages and controls re-search. The unified cycle interval governs both directions; the per-direction refetch property is carried for XML compatibility only and read by no code path.

**F-M661 [D]:** **An empty answer buys a longer clock.** When a run asked every configured source for a file and *none* returned a candidate, that verdict is stored as its own stamp (`LastSearchEmptyUtc`, late column on `media`). The item is due again only after **seven times** the configured refetch gap (capped at 30 days) instead of the plain gap. The rule exists because the plain interval spent the whole search budget on the same empty questions. With a daily gap that meant **2.015 SubDL searches per run** for **1.160 targets**, of which **1.128** came back empty (`no candidates`).

- **The mark rides on a search that really ran** — it is only written when a search stamp exists *and* the main source was actually asked this run, and never in a dry run (same guard as the stamp itself, F-M504). An item that was never asked is not deferred.
- **Any search that returns something clears it** (`MarkSearched`), and so do the reset paths (single-file reset, `ResetAll`, dead-item prune). The longer clock is never a verdict about the *title*, only a pause on asking.
- **A new file is a new record**: a freshly arrived episode has no stamp at all and is searched at once, so the delay never touches an arrival.
- Per-item evidence is logged as `SEARCH deferred <file> — the search came back empty, so the next ask waits <n> day(s) instead of <gap> (F-M661)`.

**F-M215 [D]:** **A season or range pack is resolved to the episode it belongs to — never saved whole.** A pack must yield exactly one subtitle file per episode, chosen from the pack's own listing:

**The ZIP fallback matches the entry NAME** (`S01E06`, `s01.e06`, `1x06`, SubDL's `S0106`). A multi-file archive whose entry cannot be resolved returns **null**.

A pack with no entry for this episode is **skipped**, not saved wrong. Non-pack paths keep their existing behaviour.

**F-M383 [D]:** **A pack that lists at least two files is fetched as the whole archive; the episode is then taken from that archive's own listing.** A member file costs **one** download unit, and the whole archive costs the same **one** unit. Ten episodes of a season therefore cost one unit instead of ten.

The archive body stays in the archive cache on disk (14 days, F-M370/F-M386), so the remaining episodes of the same season are served without another request. The run summary reports them as archive hits. The episode is still chosen by name from `unpack_files` (F-M215), never as "the first.srt": an archive that lacks this episode, and a pack that lists only one file, fall back to the single-file download.

**F-M388 [D]:** **A cue too short to be seen is dropped on the download side too — and the state hash is taken afterwards.** The rule existed only on the upload side (F-M382). A downloaded file carrying a 1-ms cue was convicted by the structure gate, so the very defect that had once blocked a season upload still threw away downloads. The fetched payload is now corrected before the gates run: cues of 100 ms or less fall away, the remainder is renumbered, and the file goes to disk in the canonical form.

Because that happens first, gated text, written bytes and the stored hash are one artefact. The hash of a corrected file is never the hash of the payload it was cut from. A clean file keeps every cue; only the canonical byte style is applied (UTF-8 without BOM, LF, no trailing blank line), the form the content hash has always described. The retried sibling of an archive (F-M387) and the hearing-impaired file are corrected the same way.

**F-M387 [D]:** **A quality gate judges a FILE, not a candidate.** A pack can carry several files for the same episode, and a verdict on the first says nothing about its sibling. Before the candidate is given up, the archive that is already on disk is asked for its next file — no request, no download unit — and the gates run again. Only when every matching file failed is the candidate counted as `rejected after fetch` and burned, and the reject line then says that the archive was exhausted rather than that one file failed.

**SDH is matched with SDH only.** A plain request never falls back to an SDH file and an SDH request never to a plain one. The filter compares the file's OWN `hearing_impaired` flag, never the pack header (which is often the opposite of its contents, F-M260). The two are different tracks, and swapping them silently changes what the viewer gets.

**F-M384 [D]:** **The ranking prefers a season pack over an equally good single file, and a cached pack over one it would still have to fetch.** Two sort keys sit in front of the text score, which itself is unchanged:

- `2` for a pack that carries this episode and whose archive already sits in the run or disk cache (free);
- `1` for a pack that carries it but must be fetched;
- `0` for everything else.

A pack without the episode never wins the preference.

Between two packs the text similarity still decides, and a matching release group still beats a foreign pack, because the preference is a rank and not a score bonus.

**F-M156:** **Missed refetch anchors:** an unhandled anchor is taken up by the next regular fire; the marker counts the claim, not the outcome. Missed slots do not stack.

**F-M58:** **Transient-overload 429 classification.** A `service_busy` 429 is server overload, not the daily allowance. The download side retries in place, up to 3 attempts, waiting the server's retry hint (default 5 s) and continuing the run. The upload side has no in-run retry — it ends the run and schedules the overload fire. A `rate_limit` 429 gets the same treatment (default 30 s); the status code cannot tell the two apart (F-M238). A login is classified rather than propagated. 404 and 403 are auth verdicts and fail closed at once, 429 retries, and a 5xx or a non-JSON body after 3 attempts ends the run as a transient overload and schedules the overload fire.

**F-M64:** **Target-language change needs no reset pass:** each file stores the language list it was last searched under plus the timestamp (the stored language list). The due check compares that stored list against the current configuration, so a changed list simply makes affected files due again — no global reset run and no second bookkeeping row. Items already holding all new languages still skip without API calls.

**F-M183:** **Legible download run reporting:** the download run counts **queued** items as the denominator of its abort line; the progress counter runs over every item of the selected libraries. At run end one compact aggregate line (saved, no candidates, language not available, skipped with reasons, failed, processed/queued), so a run that searched and saved nothing is distinguishable from a run that did nothing without raising the log mode.

**F-M52:** **An incomplete search does not count as done.** If the search delivers no result (rate limit, server error, denied access), the run aborts; the item and all following stay due instead of counting as done with empty results.

**F-M89:** **An unwritable target directory skips the file and does not abort the run.** The reason stands per file in the log.

**F-M93d:** **For the match only the identifier that was actually sent counts.** If SubDL filters server-side on one identifier, its own second identifier is meaningless and may not sort out a matching candidate.

**F-M382:** **A subtitle cue below the visibility threshold is discarded, not used as evidence against the file.** That holds in both directions and happens before the checks and before the write, so that the state hash describes the file actually written.

### 4.1 Quality Gates — Download

The download chain runs against each candidate in score order, before the file is saved. F-M15 and F-M16 are the two gates both directions share.

**F-M44 [D]:** **Release match:** candidate release name scored against the local filename: release-group match > token overlap > download-count tie-breaker. Weights configurable (expert mode).

**F-M43 [D]:** **Runtime/FPS match with tolerance:**
**Stage 1 — pre-download (FPS):** SubDL provides `framerate`/`fps` per candidate. If set: difference > ±1 % vs. item FPS → candidate rejected. Missing → criterion skipped, no hard fail.
**Stage 2 — post-download (structure + runtime):** corruption check on the downloaded bytes (cue timings present, monotonically increasing, plausible durations), then runtime check of the SRT cue span against the item runtime. The window is a share of the item runtime with a fixed floor under it, whichever is larger. The share is the only setting of this check (default 12 %, `0` = the floor alone). The floors are fixed constants: 420 s = 7 min for a film, 240 s = 4 min for an episode, and an episode is measured with the episode floor, a film with the film floor; outside → discard, and the verdict names the effective window. A missing runtime metadata deactivates the criterion.

Concept decision: the window grows with the item, because a long film carries long credits, and an episode carries less of them than a feature. A subtitle that ends when the dialogue ends is complete, and one fixed number measured the credits of a short film and of a long film with the same ruler. The share never tightens the floor, and the check stays symmetric.

**F-M45 [D]:** **IMDB/TMDB match — HARD, with no switch.** Candidates are matched against item IDs, and an item with no resolvable id does not search SubDL at all: no id, no download.

**There is no such switch.** It was `Require IMDb/TMDb match` (default on, F-M45 until) and its OFF position ran a `film_name` search on a title **parsed from the release filename**. That is the defect it looks like: a filename-parsed title is a guess, SubDL matches it against its own catalogue. A wrong-year release with the same title passes the search and is fetched. The hard gate is the only position that stays correct on its own — so the option went, not the gate. The `film_name` branch in the search call is removed with it, and `_searchFnTitle`/`_searchFnYear` (the parsed-title state) went too.

**Two gates carry this, and both must stay.** The ladder's id-less branch returns before any search, and the item-level gate does the same for an item that arrives id-less *after* the ladder. The TMDb fetch needs a tmdb id to begin with, and a directed fire never runs the ladder.

**F-M50 [D]:** **ONE setting, TWO effects: how many candidates per language are searched, and how many the downloader may pull before it gives up** (page label: *Auto-Sync attempts and max. download/search limit*). There is no second setting ("Max candidates per language") — the two knobs described one intent and drifted. The walk's cap was checked before the correction budget and counted the same attempts, so at equal defaults the cap always fired first and the budget could never trigger.

With the Auto-Sync **on** the limit counts only **fits** the walk could not resolve, never gate rejections (F-M320). With the Auto-Sync **off** there is no fit and no verdict, so the walk pulls N candidates through the active quality gates and keeps them at 50, 51, 52 … `0` = no limit, in both modes. Fixed with it: the "correction hunt abandoned" break fired on the **first** candidate of every language whenever the Auto-Sync was off.

**The same language verification runs here verbatim** (F-M15), on the downloaded bytes, with its own per-direction switch.

**F-M295 [D]:** **Drift gate — cue vs. speech: does the subtitle hold ONE offset, or does that offset MOVE?**
Decodes the audio, derives speech islands from the frame envelope, anchors each cue to the last island start before it, and compares the model "one offset" against "two offsets split at a candidate cue" by their marginal likelihood (Bayes factor). Recursion left and right finds further boundaries. Two switches: **analyse** (report only) and **reject**; both default off.

**What it decides — and what it refuses to:** a direction plus a span. A drifting file has NO valid single offset, so a correction value printed here would be read as an instruction and would be wrong. The gate therefore never emits a shift.

**Failure posture:** no ffmpeg, unreadable audio, no speech, or too few cues → the gate reports "did not run" and the file passes, like every other gate.

**F-M331 [D]:** **A spent allowance acts like the download switch.** *"API limit or download limit full, downloader does not even start."*

**Rule.** A direction whose allowance is spent is switched off for that cycle exactly as the download switch being off does it: the queue is **not filled** and the **run does not start**. No queue entry, no run, items stay due, and the direction still writes its own row. Both allowances are checked: the file-download counter (50/day) and the search allowance the search phase needs before any candidate exists.

**The verdict is the dispatcher's.** The seeder fills queues, reads a flag and owns no quota logic; the dispatcher probes **once per cycle, before the download seed**, and only when the cycle covers the download direction. The read is the quota inquiry, which consumes no allowance.

**An unreadable counter is NOT a verdict.** A failed quota read returns null and the cycle proceeds as before — the usual fail-closed posture is deliberately not taken here.

**F-M46 [D]:** **Overall selection:** one best candidate per (item, language) by combined score from F-M43–F-M45 — the best CORRECTED one where a correction can be proven (F-M318/F-M319). No candidate passing → the language counts as "not available".

**F-M449 [D]:** **Machine-translated subtitles are off by default.** A subtitle of the translation account (SUBDL AI) is only loaded when the switch `Allow AI translated subs (SUBDL AI)` is set. The exclusion takes effect at the search result: such a candidate neither enters the ranking nor costs a download unit, and the SDH variant follows the same rule. The account is recognised by the uploader name; spaces, dots, hyphens and underscores fall away before the comparison, other names stay untouched. The run summary names the discarded count.

**F-M450 [D]:** **An observed defect is reported to SubDL.** If a pack delivers no data or a file fails a content check, the finding is reported in the same run; an answer without content is a finding too. A reported finding is never repeated, and a reported candidate is spent — it is not fetched again, because every further request costs a quota unit and can change nothing about the finding.

Reporting goes through the portal's report form, with the bare finding (pack identifier, release name, language, measured value) and the assignment to one of four categories (no subtitle, wrong release details, copied, other). For a pack it carries the name of the affected file, taken from the resolution of the archive and not from a guess, in both tracks (main and SDH). It also carries a sender line from product name, version and account name — never the login address. A pack without a report identifier is not reported. If the portal does not accept a report, it stays open and is retried in the next run; an accepted report is never repeated. The run summary names reports and deferrals. The switch `Report broken subtitles to SubDL` is on by default.

**F-M451 [D]:** **A refusal of the fetch is no defect of the upload.** What is requested is the archive — the only form that answers for every subtitle; the wanted episode is unpacked from it. An archive carries a fixed cache key from its identifier, so a season pack is fetched once per run. If the archive answers with `200` and carries nothing, that is the finding: no second request in another form follows. The single file is only requested when the archive itself was refused. Rejection lines name the HTTP status and the portal's error name instead of a guess. A subtitle is only reported when the response actually came with `200` and contained nothing — status and content length are recorded for that, and an archive without a matching file does not count as an empty answer. A lock of the daily allowance ends the run, it is never reported.

**F-M452 [D]:** **Only a version that passed significance closes a language pair.** A subtitle in the reserved original block (51–99) is the uncorrected fetch that a rejected alignment kept — it lies there *because* the file proved wrong. It therefore does not count as evidence: the pair stays open, the run keeps its N attempts, and a later candidate still has the chance to land as `01`. File names without a slot (written before F-M316) belong to no block and count as before. The rule applies only with alignment switched on. If Auto-Sync is off, nothing is ever aligned, every file lands in the original block — then the first fetch closes the pair, otherwise none ever would. So that the open pair does not pull the same bad release again every round, the run remembers the release when keeping the original. The marker skips it before every fetch and costs no quota.

**F-M454 [D]:** **A pack is resolved via the episode, and the episode may also stand in the name as `Staffel.episode`.** The search runs with a file list, and the file that names *our* episode is pulled — never the first in the archive. Beside `S01E06`, `s01.e06`, `1x06` and SubDL's `S0106`, the bare form `6.10 …` applies; the API reports season 0 and episode 0 for such members, the episode stands only in the name. This form is checked last and read only at the name start, so that `DDP5.1` does not count as season 5 episode 1 and a resolution does not become an episode. The season named in the name decides, a deviating season yields no episode. If no file names the episode, nothing is written — better no subtitle than one of a foreign episode.

**F-M455 [D]:** **An empty fetch is no verdict — it is repeated once after ten seconds, and only the second empty fetch is a finding.** Therefore the run asks the same candidate again exactly **once** (ten seconds, no second attempt, no quota special path). Only when this attempt stays empty too is the finding reported — the only place where the two-failure rule still applies. A finding is thus still reported in the same run. A pack stays exempt: more than one member means a selection result, not a defective upload; **one** member is the file itself and therefore reportable. The statistics carry the accepted reports as their own row `Downloads: Broken subtitles reported` directly under the Auto-Sync row.

**F-M456 [D]:** **What was no fetch is no empty answer.** Every fetch attempt begins with a cleared verdict; if a candidate lies there before the first request, the log reads `no bytes (no request made)` instead of a status that stems from another candidate. A candidate that lies before the first request carries no foreign status. An empty answer is one only when it came with **200**; status 0 means: we did not ask.

**F-M457 [D]:** **Ornamental letters in the name are read before the episode is read.** Releases name their episode mark in stylized characters (`𝐒𝟎𝐄𝟏𝟎`, fullwidth forms); the mathematical alphanumerics and fullwidths are folded back to ASCII, then the four name forms apply. The folding is arithmetic and needs no ICU — a Unicode normalization throws under invariant globalization, which a Jellyfin host may well run. A **single-digit** zero in the season (`S0E10`) counts as a lost digit, the written `S00` stays season 0.

**F-M458 [D]:** **A statistics row is named so that the number stays in its column.** No row name is longer than `Downloads: rejected after being fetched` — the longest row the table already carries. Trimming happens at the name, never at the number.

**F-M459 [D]:** **The statistics' numbers end on the same vertical as the status columns.** Right-aligned in the value column alone is not enough: as long as the table is only as wide as caption plus number, its right edge lies where the widest caption ends. The table therefore spans the field, the caption column stays `max-content` and the value column is pushed to the right edge — a narrow device scrolls horizontally as before instead of squeezing the number.

**F-M460 [D]:** **The SDH path reports what it found.** The SDH fetch checks the same things as the main path — structure, runtime, bytes — and reports with the same categories, the same wording and the same switch. The finding is filed **on the spot**, not at the end of the language run. Nothing delivered stays no finding (F-M456), and an empty SDH fetch is likewise repeated once after ten seconds (F-M455).

**F-M461 [D]:** **Every fetch route writes its own verdict.** The v1 route (`dl.subdl.com/subtitle/…`, single file or archive) and the v2 route both record status, error name and length of the **own** answer. A rejection reason thus names the reason of this request, never that of another.

**F-M462 [D]:** **A measured verdict is filed where it was measured — not at the end of the language run.** At the end of the language run, cleanup happens only when **nothing** was saved for the language. A later hit leaves the previous candidate's finding untouched. The finding goes into the queue on the spot, and the `ReportedUtc` mark prevents the in-flight pass from filing it a second time.

**F-M463 [D]:** **The chosen file name goes into the fetch.** If a pack file does not name its episode in the name (`16 - Relevance.en.srt`), the resolution via season and episode does not find it. The fetch therefore receives the already chosen file name and takes exactly that entry; if it is missing in the archive, the episode search follows on the **same** bytes, and only afterwards is the archive loaded anew. An archive that already lies on disk is never paid for a second time.

**F-M464 [D]:** **A fetch carries its own verdict; a resolution happens once.** Every fetch returns status, bytes, error name and payload as one result; no consumer reads state that a later fetch can overwrite. An entry resolved once (name, season, episode) is passed on as identity and not read again from the file name at a deeper level. An archive is fetched via the route its file list comes from; the cache key is the archive's identity, not the route.

**F-M465 [D]:** **The upload follow-up hangs on a source that has one.** Concept decision: an upload run arms the follow-up appointment whenever a source with a postprocessing pass takes part in it — whether that run delivered subtitles, season packs or nothing at all. The count of uploads is not the criterion. The pass exists for the handle remap: SubDL hands out a handle while uploading, and only the pass brings the entry id beside it. A run that delivered nothing but containers therefore needs the pass exactly as much as one that delivered singles. A source whose upload switch is off, or which dropped out of the run, is not part of it and arms nothing; an appointment set from another place stays untouched. A user stop still cancels.

<!-- Bediener-Anweisung 11.11.2026: "nur bei > 0 uploaded anmahnen" — abgeloest am 26.11.2026. -->
<!-- Bediener-Anweisung 26.11.2026: "Sobald ein Upload laeuft der eine Quelle mit Postprocessing umfasst (aktuell nur subdl) soll das pp geschuled werden" -->

**F-M466 [D]:** **Season memo with fingerprint.** Per series, season, language and variant the built pack is remembered with a fingerprint over its members. The same fingerprint means the same pack: nothing is derived or sent again. A different fingerprint means a new pack. A row without a fingerprint falls back to the member comparison.

**F-M467 [D]:** **The series is resolved via its id, not via the convenience of the object.** If an episode does not carry its series (the pack phase reads a season as folder children), it is fetched via `SeriesId`; an access that cannot answer costs the pack phase nothing. Every per-file language check compares against the series' ids, never against the episode's.

**F-M468 [D]:** **Tracks are read via the id of the medium.** The per-file language check asks the media source manager with the entry's id; the object's own track list is only the fallback when this delivers nothing. A season read as folder children carries no media sources — its own list is empty and would deny every language.

**F-M469 [D]:** **A pack carries one release — even when the season lies completely on disk.** Only the release group that supplies all aired episodes is packed; if no group supplies the season, no pack arises, the single uploads remain. Files without a recognisable release form their own group and never win against a named one.

**F-M470 [D]:** **One circumstance, one row.** The scheduled follow-up job is shown exactly once — by its own row. The workers' status row no longer lists it; it names it only when its last run failed, because the reason would otherwise stand nowhere.

**F-M471 [D]:** **The archive is the one route.** A fetch gets the archive in full, places it in the files area and unpacks the wanted member from it — via the entry id, the only address route. There is no single-file fetch, no member link, no second address and no second shape behind the archive: a refused archive is the finding, and an entry without identifier is skipped and named.

**F-M472 [D]:** **A season pack immediately serves the remaining episodes of the same release.** If an episode comes from a season pack, the remaining episodes of this release that already stand in the queue in the same run are enqueued immediately afterwards and served in the same run. The archive already lies in the files area, so they cost neither a fetch nor a wait for their own round. Decisive is solely the entry that served the episode: its members are the release, a name comparison does not take place. Only the queue is re-sorted — an episode nobody enqueued stays untouched; completeness of the season is not required.

**F-M473 [D]:** **After the run Jellyfin learns of the new subtitles.** If a run ends in which subtitles were saved, the library is enqueued with the mode **search for new and updated files** — the same function the dashboard offers as "scan library" (`MetadataRefreshMode` and `ImageRefreshMode` = `Default`, no replace, no ForceSave). The notification is enqueued, not awaited; the run ends, the server works afterwards. A full database scan is not triggered; a dry run reports nothing because it writes nothing. The switch **Refresh Jellyfin library after download run** in the Download tab before the Quality Gates switches the notification off. Switch off: no notification. Default: on.

**F-M474 [D]:** **What costs nothing comes first — and a season pack before the single version.** The ranking of the candidates follows the price the run pays and the reach:

- <c>4</c> season pack with this episode, archive already in the files area (costs nothing and serves the whole season);
- <c>3</c> single version, archive already in the files area (costs nothing, serves this episode);
- <c>2</c> season pack with this episode, must be loaded (one fetch, whole season);
- <c>1</c> single version, must be loaded (one fetch, one episode);
- <c>0</c> pack without this episode.

The fetch is the scarce unit, so nothing is paid as long as something free lies there, and at equal price the pack covers the season. The ranking applies equally to the first choice, to the follow-up and to the HI variant; the text score decides only within a price tier. Whether an archive lies in the files area is checked via the entry id — via the address under which it is also loaded — not via the old single URL.

### 4.2 Dry Run — Download

**F-M277:** **The download dry run runs the search and the selection, and stops before the fetch — the search quota IS spent.**

It runs: the id quality gate (F-M151b), the searches (F-M241), the release scoring and ranking (F-M44), the FPS tolerance filter (F-M43), the skip of candidates QA-rejected in earlier runs (F-M50), and the choice of the best candidate per language.

It does not run: the file fetch, the quality gates of 4.1 (language verify, minimum cue count, runtime match), the file write, the download mark and the registry write.

The report names, per language, the chosen release with its score and its hearing-impaired flag; while the hearing-impaired switch is on, the candidate from the hearing-impaired pool is also named (F-M241). It stops at the candidate, not the file: a dry run fetches nothing, so it cannot know the byte size and never builds the F-M260 name's hearing-impaired marker.

The searches DO cost API quota, one per language set (two while the HI switch is on, F-M241); the GUI text must name both "without saving files" and "without API calls". Statistics stay untouched (F-M247).

### 4.3 Auto-Sync — Download

**Explained in `docs/AUTOSYNC.md`.** That paper carries the constants of the fit, the speech rule that keeps cues describing sound from voting, the gap and order rules between cues and the significance rules that decide when a correction is written. This section states the rules normatively.

**F-M296 [D]:** **A fetched subtitle is shifted to the audio: by the ONE constant offset the detector measures while that offset is constant, and by a STAIRCASE — one offset per segment — when it moves (F-M300).**

The feature has two halves that must not be confused: **which** track the audio is read from, and **whether** the file is then moved.

**F-M297:** **As reference for the correction an audio track of the same language is preferred.** The unchanged original is preserved in both cases.

#### 4.3.1 Method — the audio track is chosen by LANGUAGE, not by stream order

**Rule.** Priority: **(1)** a track whose language equals the subtitle's language, **(2)** an English track, **(3)** the first track without a language tag, else the first track at all.

#### 4.3.2 Method — a constant offset is corrected, a moving one is corrected as a staircase

**The sign.** The correction is **minus** the detector's value — `srt_time − measured = synced`. The sign is measured, never assumed, and T111 drives both directions so a future flip fails loudly.

**The refusal names its cause.** A refusal carries which of its four conditions refused — no movement, not significant, worse value, below the threshold — and the numbers the decision was made on. The measurement survives the revert, so a refusal is never composed from values the revert has already zeroed. **Significance settles the class, not the score (operator).** No correction is deployed without `t > DeployZ`, and there is no such thing as a material score improvement without significance. An improvement under `DeployZ` is not sufficiently evidenced and hunts the next candidate, and so is a correction that is worse but not significantly worse. The score keeps exactly one vote, where it decides: a correction at `t > DeployZ` that would LOWER the score is refused as "worse". Nowhere else is the score consulted; it is reported so the operator can read it.

**Two classes, and the difference decides whether another candidate is fetched**: a correction that improves the score but cannot prove it is `gain not statistically proven (..) — original kept, next candidate` and is CANDIDATE-SPECIFIC. The reserved `9x` original stays and the pipeline pulls the next candidate (F-M318). `no proven gain` stays for the three shapes where the file really is the best there is — nothing had to move, the shift is below the floor, or the untreated file is SIGNIFICANTLY better (`t <= -2`) — and only those END the walk. The rejection check matches the `no proven gain` prefix exactly, which is what keeps the two apart.

**Applied to times only.** Text is carried through byte-for-byte, the cue count and cue order are unchanged, and the corrected file is written **canonical** (UTF-8, no BOM, LF, trailing whitespace trimmed) — the same form F-M185 establishes and the content hash describes. The fetched original is untouched by this: it keeps its own byte style inside the archive (F-M306), because that artefact is the uncorrected file.

**`I wanted files I change to be written normalized right away as well.` The former rule wrote the corrected file in the fetched payload's own byte style, which made the stored hash describe a byte sequence that existed nowhere on disk. The two agreed only because the normaliser happened to strip exactly what the style put back. Normalizing on the way out makes written bytes and hashed bytes identical **by construction**, so the correction and the hash cannot disagree.

#### 4.3.3 STAIRCASE — a moving offset is repaired segment by segment (F-M300)

**Rule.** When the detector reports `Drifts = true`, each cue is shifted by the offset of **its own segment**: the boundaries the detector already found divide the cue list, and segment *k* covers the cues from its start time up to the next boundary. The applied shift is **minus** that offset — the same sign the constant path proved by measurement (§4.3.2).

**The staircase is the ONE correction for a moving offset.** There is no second route: the audio is the only reference, and the segments the detector found are the repair.

**Order guard.** At a step the two neighbours move by different amounts. Where that would push a cue back across its neighbour's end — the case a player renders as stacked text — the previous cue's shift is carried forward. The guard is measured against the **previous cue's END**, and because the staircase **adds** its shift the bound is a **lower** one (`eff[k] ≥ eff[k−1] − gap + MinGapSec`, `MinGapSec` = 0.04 s). A gap already below the floor is not shrunk further: then the previous shift is carried forward unchanged. Demanding the floor everywhere makes the guard fire on nearly every cue and flattens the staircase into one constant shift while the step heights still look right.

**F-M305 [D]:** **A real-material score is taken against the AUDIO, never against a subtitle.** The synthetic episode T114 runs on puts the boundaries where the test put them, so it cannot fail on boundary placement — the decider on real audio. A real-material score is therefore required, and its yardstick must be independent of every subtitle: **the audio envelope itself**, the same input the correction reads.

The detector runs on it and the result is read as the **per-segment offset curve** — in sync yields offsets near zero and a small span, drifting yields several segments far apart. **A subtitle must not be the yardstick, because that makes the measurement circular:** a reference that itself drifts makes the result look better than it is. It may be used to **find** a defect, never as the number a requirement is accepted on. **The control run comes first:** the untouched material is measured before anything is corrected, and the **order-guard count is read with the result** (a guard firing on hundreds of cues flattens the staircase into one constant shift while the step heights still look right).

**F-M300 [D]:** **A subtitle whose offset MOVES is repaired segment by segment — one offset per segment, the boundaries the drift gate already found — instead of being left as downloaded.**

This supersedes the "a drifting file is only reported" clause of F-M296. The average describes no real state, so it must not be applied; the **segments** are not an average, they are the steps themselves. Constants, the speech rule and the significance rules: `docs/AUTOSYNC.md`.

**F-M302:** **A user-facing text must not contradict the code, and length plus a ban list cannot carry that.** A short sentence stating the opposite of the code satisfies a length cap AND a ban list. So T113 also matches the **refuted claims** — `cannot correct` / `can not correct`, `has no valid single correction`, `is only reported`, `never shifted`, `cannot touch` — across **every user-visible text node**: the section intros, the per-switch description blocks **and the switch labels**. A description-only scope misses a label. The list stays narrow on purpose: it names the refuted phrasings, not every mention of drift, because a check that fires on correct prose gets disabled.

**F-M303:** **The correction sections are ONE section, and it carries ONE switch.** One block, one switch, the automatic sync check, governing the ONE correction — and the block carries **no intermediate heading**: the switch is the entry point, not a heading above it. What the switch does sits in its **own** description block: the audio path of §4.3, constant as one shift and moving as a staircase. The audio track is chosen by language (§4.3.1) — not a choice the operator makes, so it carries no switch. **The behaviour stays.** The removed entries keep their defaults in the configuration file (the per-language track check `true`; the drift check and its rejection switch `false`), so an existing installation behaves as the page shows.

**F-M304 [D]:** **A switch switches the whole of what it names, and the page offers no switch for something that is simply right.**

**Rule — the switch governs every route that moves the file.** One visible switch, the automatic sync check, governs the constant shift (§4.3.2) **and** the staircase (§4.3.3) — every path that moves a file. A switch that does not switch what it names is worse than no switch.

**Rule — the absence of a removed control is asserted, not assumed.** A check that asserts only PRESENCE stays green over a stale checkbox left in the markup. The failure mode that matters is the other one. A leftover element access on an element that does not exist returns `null`, the next property read throws, and the throw aborts the whole load handler — every binding on the page dies although the surviving switch's own markup is present and correct. The check therefore asserts the absent ids (Drift-Prüfung, Drift-Ablehnung, die Prüfung der Tonspur je Sprache, die Anker-Synchronprüfung) are absent from the markup **and** from the inline script, and counts the section's checkboxes and fails unless there is exactly **one**.

#### 4.3.4 Order of operations, and what the database is told

**Order (user specification):**

1. fetch the candidate bytes,
2. **sync** — measure and, if the offset is constant, shift,
3. **normalize** (idempotent, F-M185) — the correction output is written canonical, so written bytes and hashed bytes are identical by construction,
4. **write two files**: the corrected `<base>.<lang>.srt` **and** the untouched original as the one-entry archive `<base>.<lang>.srt.unsynced.zip` (F-M306),
5. **register the hash of the CORRECTED subtitle.**

The hash describes the file that lies on disk and that later goes up to SubDL, so the duplicate guards (content check, sidecar upload check, rejection reason) keep seeing the truth. **A correction must never lift a duplicate guard** — had the hash been registered over the pre-shift bytes, every corrected file would have looked new and been re-downloaded and re-uploaded.

**Why the name sits AFTER `.srt`.** The sidecar listing is `Directory.EnumerateFiles(dir, baseName + "*.srt")` (F-M251). Against this pattern: a name ending in `.srt.unsynced.zip` is **ignored** (correct), while the swapped `<base>.<lang>.unsynced.srt` **matches** — and the name parser then reads `unsynced` as a language code, inventing a language. Jellyfin does not index such a name as an external subtitle track either, so exactly one new track appears per corrected file. The archive keeps the `.unsynced` part in its name for the same reason the name sits where it does: it is the artefact, and it stays recognisable.

**The original is kept on purpose, as an archive.** It makes the correction reversible without spending download quota again — and re-downloading may return the same drifting file. It is kept as the one-entry zip of F-M306 and nothing else; unpacking it is the operator's step. A failure to write it does not undo the corrected file; it is logged as a warning.

**F-M306 [D]:** **The untouched original is kept as a selectable sidecar in the reserved slot block, and is locked against upload.**

**Rule.** After a correction the pipeline writes the original beside the corrected file as a **loose sidecar** `<base>.<lang>.51.srt`, **numbered from 51 upward** to the first free number up to **99**. It carries the corrected file's language token and `.sdh` marker, and **no marker of its own**. The reserved block is **51–99**; the writer of *corrected* files stops at slot **49** (F-M315). With every reserved slot taken, **no original is written** and the pipeline refuses.

**The lock.** The original is registered like every other downloaded subtitle — a row booked as downloaded, keyed on the content hash of the bytes on disk. The content check counts every row **except `observed`** as known content and the uploader does too, so the seeder drops the file and the uploader returns `duplicate-content`. **Canonical** bytes (F-M296); both tracks share one helper under the automatic sync check (F-M304).

**F-M315 [D]:** **The slots 51–99 are reserved for kept originals; corrected files stop at 49.**

**Rule.** The naming rule carries the reservation as a pure property of the number: the reservation check is true for 51–99 and for nothing else. The target name — the name a *corrected* sidecar is moved to, and the name the downloader composes — never returns a reserved slot: it walks 1…49 and, when every one is taken, returns slot 1 so the caller's own existence check refuses the move. The target name search for originals runs **50…99** and returns **null** when all fifty are taken. Both blocks count upward on purpose — corrected from 1, originals from 50 — so a viewer reads the reserved number in the track list and tells the two kinds apart without opening anything (F-M316).

**Why the cap is not cosmetic.** Without it, the next corrected file of a language whose slots 1–49 are taken would be handed a name an original already occupies — the correction would overwrite the very file it exists to keep. The corrected writer therefore stops at 49 and returns slot 1 when the block is full, never a reserved number (F-M316).

**F-M316 [D]:** **The slot is always written, always two digits; it is the track order.**

**Rule.** `SidecarNaming.Build` composes every sidecar name as `<base>.<lang>[.sdh][.forced].<NN>.srt` with `NN` = the slot as `D2` (`01`…`99`). Slot 1 does not carry the bare name. The number stays the LAST name token, so the name parser reads it exactly as before — they accept one and two digits, so names written before F-M316 keep parsing and no migration shim exists. The track list therefore runs languages alphabetically and, within a language, `01`…`49` (corrected) before `50`…`99` (originals) — the order F-M315 reserves them for.

**Cost.** Names written before this rule keep their old form until they are renamed; the pipeline only writes new names, and nothing renames on disk by itself.

**F-M317 [D]:** **The slot encodes WHICH KIND the file is: aligned takes 01 upward, unprocessed 51 upward.**

**Rule.** A downloaded subtitle goes into one of two ranges: was it ALIGNED?

- **Aligned** (the fit applied a correction, or the fit is off and the file is the corrected artifact) → `<base>.<lang>.<NN>.srt`, `NN` = `01`, `02`, `03` … **upward**, its own counter.
- **Unprocessed** (the fit found the file **WRONG** — shift beyond the limit, first cue negative, too few cues — or the fit is off) → the **reserved block**, `50`, `51`, `52` … **upward**.
- **`no proven gain` is NEITHER** (F-M321): `01` **and** a byte-identical copy at `51`.

**"Regardless of whether a corrected version exists"**: an unprocessed file takes a reserved slot **even with no corrected sibling beside it** (F-M321).

The counters of corrected slots rise on the aligned path only; the unprocessed path writes once. Every reserved slot taken → the write is **refused** and counted. F-M315's reservation (51–99, corrected stops at 49) stands; GUI text must match the code (F-M302).

**F-M318 [D]:** **The download REPEATS until a candidate's correction proves itself, capped at the QA retry limit.**

**The rule.** A fetched subtitle whose correction the fit REFUSES does not end the walk. The pipeline keeps fetching lower-ranked candidates, up to **the number of download QA retries (default 3)** candidates refused by the fit (default 3, the same knob the QA-reject give-up already uses; `0` = no cap). It stops at the first candidate whose correction proves itself against the audio.

Only a verdict on the FILE justifies another download; a verdict on the AUDIO or the tooling ends the hunt (T131). Each refused candidate is still kept, landing in the reserved block as an unprocessed file (`50`, `51`, `52` … F-M317).

The repeat sits AFTER every other download gate (FPS, no-bytes, language verification, structure, min-cues, runtime). The walk's candidate limit still caps the total fetches, counting only fits the worker could not resolve, never gate rejections (F-M50); each budget has its own log line.

**F-M320 [D]:** **The QA retry limit bounds the FIT. No other gate gives up, and nothing closes a pair.**

**The rule.** The number of download QA retries (default 3) is the fit's budget — how many candidates may be fetched hoping for a provable correction (F-M318). Nothing else counts. A gate rejection (language, structure, min-cues, runtime, FPS, no-bytes) is **not** a budget: the candidate is discarded, counted among the rejected candidates, memorized by release id (F-M200, so it is never fetched twice), and the walk moves on.

Counters of failed runs (error counter and exhaustion check) and the two status counters do not exist (skipped counter and give-up counter); nothing closes a (item, language) pair. The QA counter is the record of burned candidates and nothing else. What bounds the work is the download budget per run (F-M50) plus the burned-release memory (F-M200).

**F-M321 [D]:** **A file the fit finds nothing wrong with is a GOOD file: it is written at 01 upward, its original at 51 upward, and the walk ends.**

**The rule.** `no proven gain` is not a failure — the deploy rule DECLINES to move the file, so the file as downloaded is the best version of that language. It is written as a **good file at `01` upward** (`<base>.<lang>.01.srt`, then `02` … F-M316), advances the **corrected counter** (the reserved block stays free for wrong files), and its **original is written too**, into the reserved block (`51` upward). "both original": BOTH carry the original subtitle, identical by construction because the reserved write REUSES the array sent to `01`. The walk **ENDS** there: no candidate can beat a file the fit found nothing wrong with (F-M318).

Only the deploy rule's OWN refusal counts: a refusal that says the file is **wrong** keeps the reserved range and the F-M318 hunt alive. `SubtitleSync.RefusalMeansAlreadyGood` tells the two apart on the exact `no proven gain` prefix. Both tracks carry the rule (F-M317).

**F-M322 [D]:** **The auto-sync has its own status row, the same words as every worker; and the statistics count only the alignments that really happened.**

The alignment's status is its own readout, in ONE place: a **row of its own** in the Workers list — `Auto-Sync`, kept as `WorkerRunRegistry.AutoSyncWorkerKey` — separate from Download, which cannot say whether anything was aligned. A second readout (a light under the download switch) is asserted ABSENT (T134). **The words and colours are the existing ones** (F-M268): `ok` green when it aligned something or was already in sync, `skipped` grey when switched off or nothing measured, `failed` red when the run failed and aligned nothing; a DRY RUN reports `skipped`.

**The statistics count successful auto-syncs only.** The row is `Downloads: Auto-Sync`, reading the fitted-to-audio figure, advanced ONLY where a correction was really applied; an already-good file (F-M321) does NOT reach it and keeps the already-good mark. The setting's label reads *"Automatically synchronize subtitle to spoken track"*; the postprocessing row is `Upl. Postproc.`.

**F-M323 [D]:** **The auto-sync is a WORKER of its own — it is handed the subtitle and hands back a status and its output.**

one entry point for both tracks (F-M307 kept). It returns the corrected text and a status; the **download run keeps writing the files and counting**. **Input:** subtitle as **decoded TEXT**; audio as a **file reference plus a stream map** (file path plus track map, e.g. `0:a:2`). **Output:** `Outcome(Status, Applied, Corrected, Reason, ElapsedMs, AlreadyGood, CandidateSpecific)`; `Corrected` is text — the worker writes NO file, files no slot, keeps NO counter.

**Six per-file statuses** (vs F-M268's per-RUN vocabulary): `ok` (moved), `already-good` (declined — F-M321), `disabled` (switch off), `refused-audio` (nothing about the file — F-M318), `refused-file` (about this file), `failed` (could not measure).

**Measurement (F-M310):** the stopwatch wraps the fit and reports its duration. **The row goes red for its OWN breakdown only** (`DownloadRunSummary.AutoSyncFailed`), never a refusal.

See F-M307, F-M310, F-M318, F-M321, F-M322.

**F-M324 [D]:** **The configuration page carries its JavaScript ONCE. There is no second page script, no second script route, and no page-script file.**

**The rule.** The page carries its JavaScript exactly once, inline in the configuration page. There is no second copy: no second page script, no second page-info entry, the resource registration and the API route `Plugins/SubdlSync/ConfigJs` are all removed. Five assertions of ABSENCE hold the state — no file beside the page, no controller, no registration, no resource, and the renderer present exactly once.

**What stands unchanged:** the renderer's FIELD must still be `s.FittedToAudio`, and the statistics endpoints must still declare the JSON data type (F-M216).

**F-M328 [D]:** **Every worker row reports only ITS OWN result; no row borrows a neighbour's sentence.**

**The rule.** A worker's light and its detail text are both derived from that worker's own evidence, and from nothing else:

- The cycle's **green** fallback states the DIRECTION's own detail, never the seeder's string.
- This holds at the level the rows are WRITTEN: the check reads the live endpoint and requires that no two rows carry the same detail, not merely that each field is non-empty.
- A direction that never reached the pipeline reports that.
- The direction's own detail is set **before** the cleanup may record a quota stop; a quota stop outranks a green one-line result.
- A **skipped leg does not overwrite a scan that ran this cycle.**

**A gated scan names only the directions it covered** — a hard `0` is never printed for a direction that was not asked. **The seeder row SUMS the cycle's legs**, so the download leg's numbers do not vanish.

The download tab's switch-block order and the row-level rule are asserted against source and the live endpoint.

**F-M329 [D]:** **The download tab reads in the order the cycle works: "Continue after daily limit", then "Follow-up rounds for downloads", then "Automatically synchronize" — all three above the quality gates.**

**The reason is the operator's own:**„automatically synchronized goes in download gui up right below follow-up rounds and both go below continue after daily limit."* The three controls render correctly in any sequence, so the rule is asserted as an **ORDER**, never as presence. A presence check passes on exactly the layout this rule exists to correct.

See F-M328.

See F-M216, F-M218, F-M323.

**F-M307 [D]:** **The offset is a piecewise-constant function of time, fitted by exact dynamic programming. Supersedes the recursive Bayes-factor gate (F-M295) and the staircase applied from gate boundaries (F-M300).**

**Speech** (§4.3.1): mono 16 kHz, band-pass **300–3400 Hz**, **20 ms** RMS frames; levels clipped at the file's **10th** and **90th percentiles** → the percentile value lies in **0–1**; dilate **0.30 s** (`SLACK`).

**Score:** a cue at shift `d` scores the mean `p(t)` over its shifted window; off-audio → **0**, excluded (T121a). **161** shifts (**−20…+20 s**, step **0.25 s**) per cue.

**Fit:** maximise `sum(score) − Z·sigma·sqrt(cues)` exactly by DP over **`BLOCK_CUES = 40`** blocks; `Z = 1.0`; `MIN_SEG_FRAC = 0.12` of the file's cues, floor **40 cues**; a shift-**0** segment competes.

**Deploy (all must hold):** moved cues gain over `DeployZ = 2.0` standard errors (paired, moved cues only); file not worse; largest shift ≥ the minimum shift (§4.3.2).

**Scope:** language-neutral, times only, text byte-for-byte; below-zero clamped to **0**; no audio or speech ⇒ "did not run".

### 4.4 Season Packs — Download

This subsection records what the download side does with packs and why that binds the upload side. It introduces **no new requirements**; the normative sentences stand under the cited IDs.

**A pack is the cheaper source.** A pack candidate is preferred over an equally good single candidate: one archive body serves **all** episodes of the pack and costs one quota unit. A pack already lying in the disk cache stands right at the front (F-M384). When unpacking, the `hearing_impaired` flag **of the individual file** applies, not that of the pack header (F-M384). Which file is the wanted episode is decided by the member name (F-M454). Every unpacked file is booked as `downloaded` — exactly the provenance that later disqualifies it as pack material.

**What a dry run on the download side never reaches.** A dry run fetches no archive body, thus unpacks nothing and writes no file — the numbers it names are hypothetical, and it leaves no `downloaded` row behind (F-M334).

**The one direction that follows from this.** Every unpacked file is booked as `downloaded`, and exactly this provenance later disqualifies it as pack material at upload: if only one `.srt` of a pack comes from SubDL, the whole pack falls out (F-M397).

### 4.5 Download completion criteria

**Why a chapter of its own.** For the upload, "done" stood nowhere (chapter 3.4) — on the download side the counter-rule did stand in the code (`ArePositionsTerminal`: "the answer comes from the result rows instead of parallel settled rows"), but not in the specification. Not **what** a result row must be for it to count as a verdict, not what **no** verdict is, and not that the question is asked **per source**. Without that the same gap remains as at upload, only in the other direction: the episode comes back because nobody ever decided.

**F-M565 [U]:** **The download's completeness is derived from the result rows, not from a second stamp.** There are no parallel "settled" rows beside the result — one row says what happened, and from that follows "closed" or "open". The same lesson as chapter 3.4, other direction: a claim without evidence would again be a second truth.

**F-M566 [U]:** **The verdict space is total per (target language, source) — three verdicts, no more.**
*Delivered:* the own identifier and the own timestamp of this source stand at the track (`SubdlDownloadId`/`SubdlUploadedAt`, `TvSubtitles…`, `Moviesubtitles…`). *Deliberately rejected:* the site does not carry the language (F-M538), or it had no candidate after exhausting its search, or all its candidates failed the quality check — per source with reason. *Neutrally done:* the language already lies in the file or in the library; that is no statement about an endpoint and therefore holds for **every** active source.

**F-M567 [U]:** **No verdict without a decision.** Exhausted quota (429), network and write errors, failed fit, aborted cycle and `deferred` are **no** verdicts — the language stays open and is asked again in the next cycle. A verdict is only what would still be true on the next run.

**F-M568 [U]:** **The inquiry order does not break completeness.** If the first source delivers, the language counts as done, and for the second source "already present" is the neutral verdict (F-M566). Otherwise the second source would have to keep asking forever although the language is present. Conversely: a source that was **not asked**, because it is switched off or not configured or does not carry the media kind, is **not active** (chapter 3.4/F-M560) and holds nothing open.

**F-M569 [U]:** **A season pack delivers the language for its episodes.**
A pack closes the language for the source on every episode it really contains. Episodes the pack does not contain stay open. The known blocker (the filler delivers no text for episode 1, S02 only 3 of 24 episodes) is thus **no** completeness case but a pack case, and is reported as such, not booked as "done".

**F-M570 [U]:** **One inquiry, two callers — and the status says per source what is missing.**
The enqueuer and the run's head call the same function, `IsDownloadCompleteFor(file, quelle)`, as at
upload (F-M563). The status line names **per source** the open languages, not a sum: "tvst: DE missing,
mst: —, SubDL: DE, FR". A sum over sources is the answer that was already wrong once.

## 5. Upload Postprocessing

**F-M287 [B1]:** **Everything a run writes that is reachable from a dry run sits behind the dry-run flag — the guard belongs at the WRITE, not at the mode's exit.**

In beiden Richtungen: der Refetch-Stempel, the (now removed) file-retry counter, the (now removed) id-resolution budget, the media-row creation, the track observation and the file-missing deletion all executed in a dry run. The dispatcher marked every reported item `Done` and deleted it from the cycle queue — one dry run consumed the work it described. The guard rule: **a write is dry-run-guarded at its own site**, so moving an exit cannot silently expose it. Sanctioned exceptions: a write that makes state stale-free rather than claiming a fact (recording a success, clearing a stamp), and the worker-run record, which reports that the run happened.

**F-M184:** **Duplicate handling delegated to postprocessing:** the upload path does not search SubDL for duplicates before uploading. A duplicate upload is accepted by the API ("sent for review"), resolves to `rejected` on the SubDL dashboard, and the postprocessing job deletes that entry and marks the local row `remote-duplicate`. This removes one search call per item from the daily search quota; the cost is one upload per duplicate. A local self-echo guard (registry, per media/language pair) still prevents uploading the same pair twice.

**F-M227:** **Every plugin task appears under one heading in the dashboard.** Only `SubDL Postprocessing` reported the category `Sub Scribe`; the other four reported the default category, so the dashboard split the plugin's work across two groups. Rule: every task this plugin schedules reports `Category => "Sub Scribe"`; a new task must not inherit a Jellyfin category.

**F-M223:** **Every name the user sees says Sub Scribe — the ASSEMBLY name is the one thing that must NOT follow.** Jellyfin's logger derives its category from the type, so the namespace says Sub Scribe. The names of the embedded resources travel with the namespace (the page and its script as well as the literal it reads), and all three must change together, otherwise the configuration page does not load. **the assembly name stays the assembly name** — Jellyfin derives the plugin data folder and the configuration file from it, so renaming would orphan the credentials, the state database and the run history. The assembly name is a persistence key, not branding. Free text follows (postprocessing the category, console tags, reset dialog, GPL headers); API routes stay the plugin's own routes.

**F-M210:** **Every scheduled job is driven by its OWN setting.** Database refresh, OSHash refresh and upload postprocessing each carry their own cadence setting and their own diced anchor, and must fire on them regardless of any other job's setting. **One exception, and it is not a return to the old coupling:** postprocessing additionally requires the upload direction to be ON (F-M291). Not another *job's* setting but the direction it works for, since with upload off it has no subject at all. the cycle interval governs **only** the automatic pipeline cycles; `Manual` and `OnArrival` there suppress those cycles and nothing else. A job must never sit behind an early return belonging to a different job's configuration.

**F-M210a:** **Every scheduled job carries a manual start button under its own description, and its disabling option is labelled `Manual` — the bare word.** All three tasks carry a button and therefore offer `Manual` instead of `Never`; the rule is mutual — **`Manual` is offered exactly where a button exists**. The option text is **exactly `Manual`**, nothing appended: the button sits right under the description it belongs to, so naming it again in the menu was noise. A button touches NO cadence field — the interval stays the only thing that schedules the job — and it reaches the work through the job's own TASK, which writes the durable status row (F-M295).

The stored VALUE stayed `Never` until, when the operator resolved the two spellings into one — *"resolve never / manual for all 3 jobs to manual"* — so the menu now stores the value it shows, and the page offers `Never` nowhere at all. That rename is safe only because of three things, each asserted by T87: the legacy member survives in every enum (a dropped name makes an old configuration file fail to deserialise, or fall back to a cadence nobody chose). The load path reads a stored `Never` as `Manual` in memory (`Plugin.DiceSchedulerConfig`), and the disabling member is named explicitly where it is consumed. The OSHash switch falls back to Monthly, so an unnamed disabling value would start rehashing a library whose owner had switched it off. Nothing is written back: these are USER fields and the plugin persists only its own (F-M201), so a file keeps `Never` until its owner saves the page. And the two spellings mean the same thing, which is what makes leaving the file alone safe.

**F-M212:** **No fire may be consumed before its task is registered.** Jellyfin's task queue drops the fire (logging `Unable to find scheduled task of type "X"`) when the target task is not yet registered. Every fire path tests the registration **before** it consumes its slot (no marker set, no reschedule counter reset), so the next 30-s tick retries. This applies to **all six** paths: database refresh, OSHash refresh, postprocessing, the refetch cycle, the F-M156 catch-up and the F-M65 recovery fires.

**F-M131:** **Manual stop via marker files:** the Stop button creates `.stop-upload` and/or `.stop-download` in the plugin data directory. Each pipeline checks its marker before processing the next item (and, for uploads, between streams of the same item), cancels the direction, deletes the marker and reports the stop marker. The dispatcher ends the cycle without starting further directions. Stop markers do **not** affect the delayed postprocessing task. The canonical endpoint for programmatic stops is the stop endpoint; cancelling the running task}` cancels the Jellyfin task wrapper, but a running pipeline item may finish first.

**F-M17y:** **Delayed upload postprocessing:** after an upload run (normal finish or stop), on the postprocessing schedule (F-M176; not tied to the run and not to a fixed delay — SubDL review latency varies). The plugin queries `/user/mySubtitles` and resolves every locally pending-review entry whose status is `rejected`: an entry with "Duplicate upload" is deleted on SubDL and marked duplicate-remote; any other rejected entry is deleted without a mark. Accepted entries are not touched. All steps logged at Debug. The task runs on its own cadence and diced anchor (F-M210), independent of any run.

**F-M291:** **The upload binding applies to the SCHEDULED run only — a manual run always works.** Concept decision: only the automatic runs from postprocessing bind to the upload, a manual run may always. With the upload direction off the scheduled run has no subject. An entry can only be pending review if something was uploaded, so **the anchor is not armed**. Enforced at that ONE point: the two downstream gates are removed, because both refused a MANUAL run — the dashboard button reaches the work through the postprocessing task, and the job's route is the follow-up endpoint. A fire queued before the toggle went off still runs (idempotent work, accepted). The page names the BOUND half and only that: *"Scheduled run is inhibited if upload is disabled."* The pointer to the button falls away, since the button sits directly under the line. The page therefore documents the rule that needs documenting (the schedule is held back) and leaves the manual route to the control the operator can see.

**F-M332 [U]:** **The page's postprocessing button starts the TASK, not the job's endpoint.** Both routes run the same work, but only the postprocessing task writes the durable status row ("Upl. Postproc."); the endpoint writes none. The endpoint ran correctly (two rejected uploads cleaned up) while the list stayed empty — the operator saw no entry and reported the run as not having happened. The button therefore uses the task starter like the two refresh jobs. The name in the page is matched against the class's `Name` at runtime, so the check ties both literals together: a rename on either side fails only in the browser, as "Task not found", with every build green.

**F-M176:** **Postprocessing reschedule spacing:** if postprocessing cannot start because the global run lock is busy (F-M94h: immediate `false`, no waiting), it schedules a one-shot re-fire in the job spacing (5–120 min). Ein lokales Rate-Limit vertagt ihn nicht (F-M20). The re-fire still respects the diced anchor and does not move the next regular run. A busy lock is first checked for staleness; only a living previous run causes a deferral. **Cadence default: `Weekly`**, changed from `Daily` on: rejected-upload rows arrive as a slow trickle and every run costs a read of the own-subtitles list, so a daily sweep mostly re-read the same pages to find nothing. A weekly one still resolves a rejection within days of it happening. The diced anchor is a day+time either way, so a stored value needs no reinterpretation — and an existing installation keeps the cadence it chose.

**F-M94h:** **ONE global run lock for all six state-mutating components** (seeder, downloader, uploader, postprocessing, database refresh, OSHash refresh) — they all touch the same state, so one mutual exclusion is what the design needs. **Overlap is never waited out:** a caller that cannot acquire is refused immediately (a 20-s in-process hand-off grace) and reschedules itself by the job spacing.

**Stale detection:** dead holder PID → take over immediately; holder alive but stamp older than the 6-h run ceiling → wedged run, take over; file older than 24 h → take over. the startup cleanup deletes a leftover block file at plugin start. Fail-open: a malformed block file never wedges the plugin.

**F-M205:** **Every reschedule is counted; after 16 the rescheduling stops.** One counter per slot (upload, download, postprocess, database refresh, OSHash refresh, refetch); once a slot has been rescheduled 16 times it is refused. The database refresh carries two slot keys, one for its anchor and one for its reschedule fire. **The limit is 16** — a single constant; every log line renders `{Limit}`, so raising it touches no message text.

Giving up is logged as a **warning** exactly once; further refusals are logged at debug.

The counter is cleared by the reschedule reset on a real fire and by the per-day budget reset (F-M208). It lives in RAM only — a restart resets it, which is the intended fail-open.

**Not counted:** daily-limit quota-reset fires (a quota-reset fire) — they are driven by the reset, and dropping them would skip the direction outright.

## 6. Database Refresh

**F-M583 [U]:** **Verdicts without evidence are discarded — the refresh resets them and the next run normalises.** Concept decision: duplicate self and duplicate content are reset once in the store and normalised again afterwards. A `duplicate-self-echo` or `duplicate-content` only stands if some source demonstrably holds the text (upload mark or upload time) **or** the origin is known (downloaded, may not upload anywhere — F-M487). If both are missing, the verdict was a claim; it arose from queue and language checks (F-M581) or from evidence that could not be found again. `ClearVerdictsWithoutEvidence` deletes such rows (embedded and loose subtitle) — deleted means „unknown", and the next run judges anew. Idempotent: a run without such rows reports 0. The refresh reports the number in its summary **and in the worker line's sum** (F-M622: it was missing there, which is why „removed 532 dead state" stood next to 968 verdicts in the log). Checked by T293.

**F-M94:** **Database refresh as scheduler task:** a dedicated task reconciles the stored state with reality — it removes tracker state for Jellyfin items that are gone and verifies the file side of stored verdicts (F-M234). The task, its dashboard name and its log prefix read "database refresh" (`[SubDL-Refresh]`). It uses the global run lock and operates on the shared database.

**F-M274:** **The refresh runs its steps in one fixed order, in a single run, under the global run lock, and reports what it changed.**

**0 — probe:** resolve the full item id set; an unqueryable or empty library skips the run. **1 — dead rows:** rows whose Jellyfin item is gone are removed and counted. **1b — gone files (F-M60 removal,):** media rows whose FILE is gone while the item survives are removed with their subtitles, by PATH. **2 — guid-keyed trackers:** the search and QA-fail trackers lose gone-item rows. **3 — OSHash cache paths:** cached paths under a non-listable root are dropped. **4 — vanished subtitle files:** the loose subtitle verdicts of F-M234 and the open required FILES of F-M283 (reported, never repaired). **4b — embedded rows:** F-M258. **5 — compaction:** the fold and rebuild of F-M214.

Steps 1b, 3, 4 share one fail-safe: every stored media root must exist and list cleanly, else the step is skipped, never judged ("unknown ≠ deleted"). The run's line reports removed rows per area and forgotten state; a no-change run reports the database matches reality; a lock-less run is `deferred`, never failed.

**F-M234:** **The state prune is a database refresh: it verifies the FILE side of a stored verdict, not only whether the item still exists.**

A subtitle file deleted while its item stays leaves a verdict ("uploaded"/"rejected"/"downloaded") and a download mark both permanently wrong, the item reported complete forever. A mark is stale only when the language has lost its evidence EVERYWHERE the configuration counts it: no loose subtitle file AND no embedded track, or a track settled as unavailable. The refresh reads "settled as unavailable" from the whole stored set, through the same reader the download pipeline uses.

Fail-safe: every stored-path root must exist and list cleanly, else the file side is skipped; rows without a stored path are never judged ("unknown ≠ deleted": a missed refresh costs nothing, a false one destroys valid verdicts). A missing probe is not a deletion: when the embedded half cannot be read, the check falls back to the files alone.

Subset rule: a stored language set that COVERS the configured one counts as complete, so removing a language does not invalidate every mark; adding one still does.

**F-M258 [D]:** **The database refresh checks the embedded side too, not only the loose subtitle side.**

For every media row whose Jellyfin item still exists and which has stored embedded rows, the refresh reads the item's current streams and forgets every stored row that disagrees — on the key (position) and on the recorded facts (language, hearing-impaired). A row whose position is gone, or whose language/HI no longer matches the stream at that position, is dropped.

The check is structural for observations only: a row carrying a verdict (`uploaded`/`rejected`) or a detection attempt is kept unconditionally; only a plain observation is dropped on a disagreement. The comparison is against positions, not the tracks whose language resolved: a position that exists but answered nothing keeps its row; only a position that exists and answers differently is a disagreement.

Fail-safe, as the rest of the refresh ("unknown ≠ deleted"): an unresolvable item, an unreadable stream list and an EMPTY stream list are skipped, not judged; only a NON-empty list that disagrees is evidence. The check runs only for files that HAVE stored rows.

**F-M214:** **A database refresh compacts the database in the same run.** The prune is followed, in the same run and while it holds the global run lock, by the compaction: fold the journal in (the journal fold), then rebuild the file (the rebuild) to release the free pages.

**Compaction is measured as the whole footprint** — data file plus journal, before and after.

**It must never fail the task:** an exception is logged as a warning and swallowed. It is housekeeping, and the refresh's own work has already succeeded.

**F-M237:** **A rebuild that cannot release the pages is replaced by a rebuild from the file's own rows.**

Rule: when the library's own the rebuild fails, rows are read out, written into a fresh file, counted against the original, and only a matching count is swapped in. Rows are never traded for a smaller file.

The swap is a rename, not a copy: a running Jellyfin holds the file open, and a copy over it is refused with "being used by another process".

The previous file survives as exactly one timestamped backup, reachable through the database restore function. Nothing is deleted until the row count is proven equal.

The rebuild's side files are addressed by their STEM (`subdl-scribe-log.db`, `subdl-scribe-temp.db`), never by appending to the full name, which matches nothing on disk.

**F-M236:** **A failed compaction must not leave the shared database dead.**

The compaction is verified, not trusted: after a failed rebuild the database is probed with a real query (the compatibility row), and only a failing probe replaces the engine.

The repair preserves the data: the failed swap leaves the original file on disk, so rows survive.

**F-M342 [D]:** **A database refresh refreshes the query statistics in the same pass as the compaction.** After the rebuild the run executes `ANALYZE`, which fills `sqlite_stat1` — the row count and the rows-per-key average of every index — because that is what the query planner reads to pick an index; without it the plan is a guess. Auf diesem Schema: `embeds WHERE MediaHash = ? AND Status = ?` searched one index before the statistics existed and another after, and MediaHash is the selective one (13 rows per key against 29).

The cost is one database page.

The statistics run as their OWN guarded step. A failure is logged as a warning and does **not** turn a shrunken file into a failed compaction — the file WAS rebuilt at that point, and the failure wording belongs to the rebuild alone.

The rewrite is folded in before the footprint is measured (the journal fold), so the reported "after" describes the settled file instead of a journal holding the rebuilt database. See F-M214.

**F-M404 [P]:** **The database refresh also cleans up the pack side: a `packs` row whose pack identifier no longer stands on any subtitle row is removed and counted in the refresh sum.** Such a row would have no carrier left — the media were rehashed, the files cleared away or the pack replaced —, but still claimed „this season is packed" and thereby shut it down for good.3.

## 7. OSHash Refresh

**F-M119:** **OSHash refresh as scheduler task:** a dedicated task recomputes expired or fingerprint-changed OSHash cache entries on **its own diced WEEKLY anchor** ("D HH:mm", drawn once at install and never re-rolled). It fires **every week**. It holds the global run lock while mutating the shared cache. The OSHash cadence bounds how long a cached fingerprint is trusted (`Never` = fingerprint mismatch only, zero media reads in the steady state), while the fire date comes from the anchor alone. Setting it to `Manual` (stored value `Never`) does **not** disable the job.

**F-M275:** **The refresh works on a snapshot of the cache and decides per entry from the file's fingerprint.**

For every entry the file is stat-ed for **size** and **modification time**. A file that is missing, or that cannot be stat-ed, leaves its entry untouched and is counted as **missing** — removing entries is the state prune's job (F-M94), not this task's.

A fingerprint mismatch (size or modification time differs) recomputes the media hash. An unchanged fingerprint recomputes only when the entry's trust window has expired; with the cadence set to `Manual` (stored `Never`) the window never expires, so only a mismatch triggers a recomputation.

A recomputed hash is stored together with size and modification time. A file that cannot be hashed keeps its old entry and is counted as **skipped**.

The cache is flushed at the end of the run. The run's line reports the entry count and the changed, missing and skipped counts. The run ends `ok`, `cancelled` or `failed`; a failed run keeps the entries already flushed and leaves the remainder untouched. A run that cannot take the global run lock is recorded as `deferred` and re-fires after the job spacing (default 15 minutes, clamped to 5–120).

**F-M279 [D]:** **The OSHash VALUE is verified against an independent oracle, not merely against itself. Switchable: not applicable — this is a test obligation, not runtime behaviour.**

**The reference:** a standalone check script implements the published OpenSubtitles algorithm (size + first/last 64 KiB summed as little-endian 64-bit words, 16 lowercase hex, unsigned 64-bit wraparound), NOT from the media hash; `scripts/seed-db-test` section J asserts the shipped code against its literals. The contract is: block size = min(64 KiB, file size).

**Pinned:** the values at 8 B, 64 KiB, 128 KiB and 200 KB, the window edges at 64 KiB ±1 and 128 KiB ±1, that a change in the tail or the last window changes the value. A missing path yields `null` (empty file too), never a throw.

**Negative-controlled:** reverting the algorithm in a COPY of the source must turn the assertion red. The cache on an unreadable host is audited by a standalone forensic script; its verdicts separate `MATCH`, `MISSING FILE` (F-M119), `SIZE DRIFT` (F-M61b) and `MISMATCH` (wrong value).

## 8. Rules Shared by Both Directions

**F-M5:** **One ffmpeg call per file, however many subtitle streams it carries.** All text subtitle streams are extracted in a SINGLE invocation into a temp folder: repeated `-map 0:s:N -c:s srt -f srt <out>` output pairs on one input.
**The index rule:** stream positions are SUBTITLE-relative (`0:s:N`), never pack indices. Mixing the two numbering schemes lands on video or audio and fails with exit 8 or a no-stream error.
**No optional mapping — `0:s:N` without `?`.** On an index that does not exist a trailing `?` makes ffmpeg write the FIRST subtitle stream into that slot, exit 0 and report nothing. An unknown index must fail the call; the caller reads that as a whole-pass failure and retries the empty streams one at a time.
**The fallback rule:** ffmpeg's exit code decides what an empty result means. Exit 0 → the stream carries no text; that is a verdict and must NOT be retried. Non-zero → the pass failed as a whole, and each empty stream is retried once with the per-stream call.

**F-M261 [D]:** **An untagged or `und` subtitle track is resolved, and the found language is written back into the pack. Switchable, default off — it governs both the resolution and the write.**

A text track whose tag is absent, empty, `und` or `undefined` is not a fact about its language: the gate extracts it, detects offline, records an observation and — when enabled — writes the tag into the pack. It belongs to the SEEDER, before the queue decision.

**Sequence:** untagged text tracks from the stream list → one ffmpeg pass for all (F-M5) → offline detect with the 2 KB floor (F-M74) → write the codes → move the registry state to the new hash → record the tracks.

**Registry move:** the rewrite changes the file's IDENTITY, so the identity shift rewrites the state. **Position rule:** position is `0:s:N` over TEXT-eligible subtitle streams only (not external, not forced, not bitmap); bitmap and forced tracks occupy and keep a position, external streams are skipped. **The write is a stream copy (`-c copy`)**, read back and verified, and counts as PRESENT for the same run's coverage check.

**F-M66 [D]:** **Id resolution has NO give-up — an item without a resolvable id is retried by the next run.** Concept decision: the id-resolution retries are deleted. The counter and the limit appear in neither configuration, page, both pipelines nor the refresh; `0`-means-never-give-up was the only surviving meaning, so the rule is now unconditional.  An id-less item is skipped for the run and re-enters the ladder next time, which costs one TMDb attempt per run at worst.

**F-M59:** **LIFO queue order:** both pipelines process items by the creation stamp descending — newest first. The id-order partition keeps id-resolvable items before the id-less backlog.

**F-M60:** **Removed — there is no file-error retry before the final skip.** The counter per item, the setting the retry limit), its page field and both pipelines' give-up branches are removed, and the tracker type is deleted. **The gone-file case has one owner now and it is the Database Refresh (phase 1b, F-M274):** it sweeps media rows by PATH behind the same root fail-safe as its neighbours, so it also reaches rows whose item is fine. A second, slower owner that only fired after three failed runs was what the order removes.

## 9. SubDL/TMDb API, IDs and Credentials

**F-M11 [B1]:** Upload queue with retry: a failed item is retried across runs up to the configured maximum, without backoff. At the limit it is purged. Timeouts are retried in-transport three times with a doubling 1 s backoff.

**F-M12 [B1]:** Configurable account (SubDL user/pass) in plugin config, password never in plaintext in logs

**F-M17c2:** **Remote duplicate detection requires a canonical payload AND a matching hash function.** The registry hash is comparable with SubDL's stored raw-file MD5 only when both describe the same bytes and use the same function (F-M185, F-M186).

**F-M19 [B1]:** **All four credentials are required — they are not alternatives.** The SubDL **email/password** pair authenticates the UPLOAD; its three steps carry a Bearer token from `/login`. The SubDL **API key** authenticates search, file download and the quota read. The **TMDb key** resolves and corrects the ids in both directions. A missing one is not a degraded mode: the run is refused up front, naming the field (F-M203).

**F-M208:** **The reschedule budget is per UTC day and resets at the day roll-over** — the same 00:00 UTC boundary the SubDL quota reset uses, so the budget returns when the quota does. The daily reset clears every slot at the roll-over. It is a no-op when the day has not changed.

A slot at the limit has no other path back: the refusal drops its pending fire, so it can never fire, so it can never clear its counter. With `RefetchInterval = Manual`/`OnArrival` no anchor exists that could.

Both refusal messages name the day roll-over as the recovery path, and do not promise a regular anchor — that anchor does not exist in those modes.

A manual run and a successful run do not clear the counter. Only a real fire or the day roll-over does.

**F-M219:** **The type-neutral TMDb search is the FIRST rung wherever an id is resolved, and the year is checked on its RESULTS.**

The year is not sent to `search/multi`. The typed endpoints honour it, and the typed search is the second rung.

The year is applied when the hits are read: a hit whose own date (`first_air_date`, `release_date`) equals the year wins over the first hit. With no year-equal hit the first film/series hit is used; a Jellyfin year is often the import year (F-M217).

Both id-less paths start type-neutral: the download pipeline's title rung and the id resolution. A typed search inherits Jellyfin's guess, so a name-detected series typed as a film asks `search/movie` and can never match.

The seeder parses the file name before it asks TMDb (the same parser, F-M217), and returns the type TMDb reports.

**F-M231:** **Jellyfin's ids are ALWAYS tested against TMDb by TITLE and YEAR; the year comes from the FILE NAME when it states one.**

Year source: the file name's year first, Jellyfin's only as fallback (Jellyfin's film year, else Jellyfin's series year) — Jellyfin's year comes from the metadata that may be pinned to the wrong title.

Correction only on an EXACT year match (the strict year match, the strict twin of the multi-search). No exact hit ⇒ Jellyfin's ids stand, so a missing year or a differently spelled title costs nothing (F-M190).

**F-M738 [D]:** **The identity check decides on the PAIR, not on an identifier.** Finding: `Too Good to Be True` lay in the library as a series with the IMDb identifier of the 2024 four-parter and the TMDb identifier of the 2003 two-parter — two entities in one item.

The check compared only the TMDb identifier and, when the IMDb was missing in the hit, carried the **old** IMDb on: `(strict.Imdb ?? jfImdb, strict.Tmdb, …)`. The result was a pair made of two series, which lay accidentally right or wrong depending on the data situation.

Likewise the flag `Corrected` reported a correction already when **nothing** changed — it meant „TMDb identifier deviates", not „something was changed". Callers and log thereupon claimed a correction that was none.

Newly a **pure function** computes the result. (1) TMDb wins. (2) **a pair stays a pair** — if the IMDb identifier is missing in the hit, Jellyfin's IMDb is **discarded** instead of mixed. (3) `Corrected` means „what comes out differs from what came in", across all three fields (IMDb, TMDb, type). A type change (series ↔ film) counts as a correction. Verified by T398.

**F-M232:** **A login that fails on a server error is CLASSIFIED, never propagated raw.**

An unclassified failure propagates as a raw parse exception: the cycle logs a generic failure, the recoverable overload is never recognised, and the F-M67 overload fire is never scheduled.

Every login attempt is classified. 404 is a not-found verdict, and 403 is an auth verdict (fail closed). 429 keeps its retry/backoff. A 5xx, or a non-JSON body after the retries exhaust, is a **transient overload**: it sets a transient overload, ends the run cleanly, and schedules the F-M67 fire.

A JSON body behind a 5xx is still transient, never a credentials verdict: the status code decides.

The client's log channel (login retries, raw 429 bodies, rate headers) is subscribed per run by both pipelines, so the retry line is reachable.

**F-M235:** **A transport timeout is retried three times; a user stop never is.**

A timeout is transient, like the 5xx login answer of F-M232. An expired request threw a timeout, which the API clients do not catch. It therefore reached the run loop's `catch (OperationCanceledException)` and was reported as "cancelled — keeping the partial result": a slow server looked like the stop button.

The two cancellations are told apart by the caller's token. Already cancelled → the request is not repeated. Otherwise the cancel came from the handler's own per-attempt timeout and the request is repeated.

Budget: 3 attempts per request (as in F-M232), 1 s base backoff, doubling. Transient means a timeout, a cancellation or a transport error; a malformed answer is an answer and is never retried.

Per-request timeout, not one global value. 60 s per attempt for API calls, overridable via the per-request timeout; a subtitle FILE download asks 5 minutes. The client-wide timeout is disabled.

The request body is buffered once and the request rebuilt per attempt.

The give-up line names the server as not answering in time, never as a caller cancel, and the retry lines are routed into the run's log channel.

**F-M221:** **The plugin card states the defaults and the two required keys in the same sentence as what the plugin does.** Card text (all carriers, F-M220): *"Sub Scribe brings SubDL.com to Jellyfin: it downloads missing subtitles for the languages and libraries you pick and uploads your own. Download is on by default; upload is off — enable at your choice. Requires a SubDL login and API Key plus a TMDb API Key."*

Order: what it does, both things the user picks (languages AND libraries — F-M42 pre-fills a list, it is not a commitment), download on by default, upload off by default and enabled by choice (F-M41), then the keys.

Do not write non-ASCII characters as `\uXXXX` escapes in the plugin description literal: the F-M220 guard compares source text, and an escape is six characters where the YAML carries one.

**F-M24b [B1]:** **Credentials never in logs:** password/API key/token are never written at any log level (redaction before writing, also in Debug)

**F-M25 [NOT IMPLEMENTED]:** A per-run start offset (random wait before the first upload) is not implemented — no code path delays a run, no config property backs it. The anti-herd function is carried by the per-installation diced fire times (F-M51), the per-stop deferral offset (the job spacing, F-M26b) and the daily-limit recovery jitter (F-M182, 30–300 min). Manually triggered runs start immediately.

**F-M26a:** **Which pause applies where (single source of truth).** The bare-call pause — bare API calls, deterministic. The transfer pause — real transfers, ±30 %.

**F-M27 [B2]:** **Auto-backoff:** on HTTP 429/rate-limit or 5xx a file transfer is retried up to 3 attempts with exponential backoff (2 s, then 4 s). The server's retry hint with a 5 s floor applies on the login and transient paths. On exhaustion the request fails cleanly and the run-level reaction matrix (F-M54) applies.

**F-M28 [B1]:** **IMDB ID mandatory — correct per media type.** For series strictly the series IMDB (tvshow, not episode) plus season and episode; for movies the movie IMDB. Resolution order: (1) Jellyfin metadata as-is; (2) TMDB id → IMDB via the TMDB REST API (key required, F-M203); (3) TMDB title search; (4) skip "no-imdb". Episode IMDB is never used.

**F-M190 [B1]:** **ID resolution is type-free and TMDB-authoritative, and it is the standard path for every upload.**

**Type detection comes from the FILE NAME and from TMDB, never from the Jellyfin library type.** The parser reads the name (`S01E05`, `Season 1 Episode 5`, TV stamps). When it reports a series while Jellyfin reports none, the item IS resolved as a series. A later `search/multi` answer overrides the type — **TMDB decides** film or series.

**ID arbitration (JF vs TMDB) with a report:** both IDs present → cross-validate; on disagreement a **Normal-level** ID-conflict line is emitted and **TMDB wins**.

**Fallback chain:** TMDB returns no IMDb → the Jellyfin ids are kept; Jellyfin carries only a TMDB id → the IMDb id is fetched from TMDB; **no IMDb resolvable at all → NO upload** (fail-closed). **Upload payload:** IMDb + TMDB when both are known, IMDb alone when TMDB is missing, neither → no upload. **Series IMDb needs `/external_ids`:** the detail endpoint reports `imdb_id: null` for series, while `<kind>/<id>/external_ids` returns the real `tt…`. The fallback uses it when the detail response leaves the id empty.

**F-M28a:** Before every SubDL search/upload the plugin calls the id validation to cross-check and correct Jellyfin's IDs via TMDB when a key is configured. Mismatches are corrected, missing IDs backfilled. Automatic whenever a TMDB key is present; no separate UI switch.

**F-M151 [B1]:** **The upload ID quality gate — no switch, it always runs.** Before an upload Jellyfin's IMDb/TMDb ids are validated against TMDb: missing ids are filled, conflicting ids are corrected towards TMDb, and the type is re-decided from the answer. A quality-gate setting existed and was read by no code path — the gate is not an option but the thing that makes the TMDb key load-bearing (F-M203).

**F-M151b [D]:** **The download ID quality gate — same, and no switch either.** Before a search the ids are validated and corrected via TMDb so the SubDL search points at the right title. The download-side gate was equally unread.

**F-M203 [B1]:** **The TMDb API key is required — without it no run starts, in either direction.**

Rationale: id resolution is TMDB-authoritative (F-M190) and the SHOW id a series upload must carry (F-M191) is only obtainable through the key. With Jellyfin's own ids a mis-typed episode would be uploaded with an **episode id in the series slot**.

**No partial operation.** Id resolution runs in both directions and feeds the search and the upload payload alike. The plugin refuses to start and names the missing field (F-M19).

Upload and download side alike, from one check (`MissingCredentials`): the run stops before the first item, so nothing is ever uploaded or searched with unverified ids.

GUI: the field label reads **"TMDb API key (v3) — required"**, the input carries `required`, and the save handler **refuses to save an empty key**.

Status: a missing key reports **red**; the settings page refuses to save it and the run refuses to start.

**F-M63:** **Official API endpoint:** `https://api.subdl.com` for all API calls (login, search, upload, /me).

**F-M217 [D]:** **For a download the FILE NAME decides type, title and episode — not the Jellyfin library.** Jellyfin types an episode living in a library declared as *movies* as a film, and its item NAME is the raw file name, which resolves against nothing. Three consequences:

**The seeder prefilter must not drop such an item:** the id gate may not depend on a lookup that resolves the item's name. An item that cannot be resolved by name still has to reach the queue.

**The search query is the PARSED title** (the file-name parser) — a raw file name returns 0 hits against the type-neutral `search/multi`, its parsed title returns the show.

**Season and episode come from the file name too.** For a film-typed item Jellyfin reports 0/0, so the F-M215 pack guard (`episode > 0 || season > 0`) could never fire.

**A year-filtered search retries once without the year.** Jellyfin's year is frequently the IMPORT year (a 2022 show carrying 2025): the filtered query returns 0 hits where the unfiltered one finds the title.

**F-M62:** **429 classification fix:** a 429 carrying no rate headers and naming neither `service_busy` nor `rate_limit` is treated as the conservative fallback (server-level 429, next-midnight anchor), not as the daily allowance. A 429 whose kind cannot be named must never be guessed in the direction that discards a day of work. The real daily allowance carries a reset signal.

**F-M206:** **A respacing log line must not claim a quota reset.** A fire carrying an exact, caller-computed time (lock-busy, rate-limit respacing) applies **no jitter** and must log `respaced by JobSpacingMinutes after lock-busy or rate-limit (no jitter)`. The quota-reset wording (reset anchor + 30–300 min jitter) is confined to the branch that actually dices it.

**F-M191b [D]:** **Unresolvable IMDb id ⇒ DROP it, never keep it.** When `/find` returns every result array empty, TMDB does not index that IMDb id at all — for a series item it is an episode id TMDB has no record of. Keeping it "as the best available" uploads an episode IMDb id in the SERIES slot, exactly what this rule forbids. Both ids are dropped so the caller's type-free title search resolves the show by name.

**F-M343 [D]:** **Every SubDL call names the integration, and the agent carries the real version — the TMDb calls carry that agent too.** The search appends `client=other`. The published value list is bazarr, stremio, kodi, subdl_player, custom_integration, other (subdl.com/api-doc, read). This plugin has no entry of its own, and `other` is the honest value rather than claiming `custom_integration` — a category the operator did not choose. A dedicated value is SubDL's call, not ours. The parameter is documented for the SEARCH endpoint only, so no other call carries it.

The same search with and without the parameter returned an IDENTICAL payload (HTTP 200, status true, 10 candidates, equal content signature), and an unknown value (`client=bogus_wert`) was accepted just as silently. The parameter cannot break a call, and the published list is a convention rather than a server-side check.

The User-Agent is built from the assembly version and names the product — `Sub Scribe/<version> (+https://github.com/nrg80/sub-scribe)`. F-M537 replaced the retired project name `SubDL-Scribe` there; that is also the name every request to tvsubtitles.net carries as `client=sub-scribe-<version>`. Until then it was the fixed literal `Jellyfin.Plugin.SubdlSync/1.0`: every release since 1.0 introduced itself to SubDL as version 1.0, and no two versions could be told apart. Read from the assembly, a version bump needs no second edit and cannot fall out of step with it.

`ScribeApiClient.NewHttpClient` is the one factory every SubDL client is built with, and the client factory sets the same agent as its own client's default. Both exist for the same reason. The LOGIN call builds its own request and adds no header of its own. The plugin also builds SubDL clients at more than one place: the main API client, the status probe, the quota read, the upload postprocessing controller and the upload pipeline's own pass. Those ad-hoc clients carried NO agent at all, so their calls (login in particular) went out with no User-Agent whatsoever. Verified that login succeeds with the plugin agent, with the old one and with none, so the agent is free to set. The status probe's said `SubDL-Sync-Status/1.0` — the pre-rename product name, frozen at the same version — and carries the shared agent now.

**Who sees the agent (traced through the tree):** three counterparties — `api.subdl.com` (search, login, quota read, status probe), `dl.subdl.com` (the subtitle file download) and `api.themoviedb.org`. The third is not an oversight and the operator confirmed it on. The same client factory hands its HttpClient to the resolver, and the status controller's probe client serves the TMDb configuration probe as well. So an id lookup identifies its caller for the same reason a search does. Both services sit behind a TLS-terminating front, so the operator and its edge read the header; the plugin's own log, Jellyfin itself, the LAN and the ISP do not. The `client=other` parameter reaches less far than the header: it is appended to `api.subdl.com` search URLs only, so TMDb never sees it.

**F-M344 [D]:** **The upload reports the SOURCE quality of the subtitle, read from the file name; the fixed literal `web` is not sent.** subdl.com/api-doc lists exactly five values for the upload field `quality` — `web` (Web release, streaming, WEB-DL), `bluray`, `dvd`, `hdtv`, `cam`. The metadata step sent `web` for every file since the first upload implementation (F-M8), with no requirement behind it. It was wrong for every non-streaming file: the Dinosaurs `480p DVD x265 Panda` episodes went to SubDL as WEB, and so does every BluRay or HDTV file. The field is the release's quality tag in SubDL's lists and what its own filters read, so a wrong value mislabels the upload for everyone who filters by quality. On the live site: our own uploads, sent as `web`, carry the tag `webdl` — the label differs from the value, the value is accepted.

The value is read from the NAME, and the subtitle's OWN name wins over the pack's: a loose subtitle states where THAT subtitle came from, while the file next to it may be a different release of the same film. An embedded track has no own name, so the media file is read.

The LAST source word in the name decides, and words are matched as WHOLE TOKENS. A release name puts its title first and its source after it, so `The.DVD.Club.2019.1080p.BluRay.x264` is a BluRay and the disc word belongs to the title. A substring rule would read the CAM inside an ordinary word as a camera recording. Compact forms are consulted only when no token matched and only against distinctive strings (`BLURAY`, `BDRIP`, `DVDRIP`, `HDTV` …), for spellings whose separators swallow the word (`Blu-Ray`).

**The asymmetry decides the tables:** a missed BluRay falls back to `web`, which is the behaviour that shipped before, while a false BluRay mislabels a streaming release — so the fallback is cheap and the false positive is not. The value is a REQUIRED parameter of the upload call, with no default, so no call site can silently keep the literal. `SourceQuality.Allowed` carries the documented five and the assertion reads it.

**F-M350 [D]:** **The upload reports the FRAME RATE CODE of the item's video; the fixed literal `0` is not sent.** The metadata step sent `framerate=0` — SubDL's "default/unknown" — although the rate is already read for the download-side FPS gate (F-M43 stage 1).

The codes are SubDL's, not invented. Measured on the live search API across 120 entries: code `2` always arrives with `fps: 23.976` and code `0` always with `fps: null`. The numeric table is the one the download side already parses (`0` unknown, `2` = 23.976, `3` = 25, `4` = 29.97, `5` = 24, `7` = 30).

The rate comes from the item's primary video stream (average first, real second). The **nearest** table entry wins, because 23.976 sits only 0.024 from 24 — a fixed epsilon would have to be smaller than that and would then reject the float noise of real pack values. Anything further than 0.05 away, and every rate outside the table (48, 50, 59.94, 60), stays `0`, which is what shipped before, so an unusual rate cannot become a wrong claim.

**Fallback:** neither field is trustworthy alone. In one library 27 files carry a wrong average (`1000`, which a pack reports for one frame in one millisecond) with a correct real rate, 18 a good average with a real rate 0.05+ off (`29.9166`). And 11 map to a DIFFERENT code from both.

The average is taken first and the real rate only when the average does not map. **98.0 % of the library then reports a code, against 96.8 % on the average alone and 97.3 % on the real rate alone.** The average goes first because it is the playback rate — frames divided by duration, so a file with every second frame dropped reports 12.6 against a real 25. It also goes first because the download gate (F-M43 stage 1) reads the same order, so a file gets the same rate in the gate and in the upload; on the 11 conflicts that is a consistency choice, not a truth claim.

The lookup runs **once per ITEM**, before the candidate loop — the rate is a property of the video, so an item with four uploadable tracks must not read it four times. **`production_type` deliberately stays `0`:** SubDL documents no value list for it and the search API does not return the field at all, so a guessed enum would be a claim without evidence. See F-M8, F-M325.

**F-M17a:** **The search is narrowed to the one episode.** Season and episode number go with it; without them a season pack fills the page limit and hides matching releases on later pages.

**F-M17d:** **A search error does not block the item.** What the search delivers holds; an error leads to an open result, not to exclusion.

**F-M17f:** **Withdrawn — there is no inquiry of the own publication list before the search.**

**F-M190b:** **If the IMDb identifier is missing in the detail answer of a series, it is fetched via the external identifiers of the same id.** The detail answer carries it empty for series. Without the second call every series episode would have counted as „without IMDb" and would never have gone up.

**F-M480 [D]:** **tvsubtitles.net is opened up via its pages, not via an API — and the path is the site's own.** The site has no API (measured: `/api*`, `/xmlrpc.php` and `api.tvsubtitles.net` → 404 resp. no DNS; only `/rss.xml` without episode data). The fetch therefore runs `/add.html` → `/tvshow-<id>.html` → `/tvshow-<id>-<staffel>.html` → `/episode-<seite>.html` → `/subtitle-<id>.html` → `/download-<id>.html`.

**The language is not in the text** — only the flag image is reliable, and it is read **per block** (from one subtitle anchor to the next), because a search across the whole page catches the flag of the following entry. Entries without a flag are skipped. **Page ids are not episode numbers** and the season list runs descending — season and episode come from the heading of the episode page, with fallback to the first `NxM`. **The download's file name is not in the link**, but as numbered JavaScript fragments on the download page, which are assembled in order. Every parser is its own side-effect-free function and is driven by the `tvsubtitles` suite with the pages actually delivered, not with the live page. Verified by T203.

**F-M481 [D]:** **The upload is the site's form, and the comment field carries the identifiers.** Sent is `multipart/form-data` to `/add1.php` with Referer `/add.html`; the fields go in the site's order: `tvshow`, `season` (1–22), `episode` (0–100), `lang`, `rip`, `release`, `comment`, `go`, plus `double` and `hi` **only when applicable**. `comment` carries **only** the TMDb and IMDb identifier (`TMDB 1422 / IMDb tt1442464`), because the site has no field for it; **without both identifiers the build aborts**.

The client note is **no longer** in the field — what stood there before stays on the site, because it knows no editing. The field `author` is **not sent along** — a missing part is the only way not to overwrite a prefill of the site.

The language codes are the site's, not ISO (`br` = Brazilian Portuguese, `cn` = Chinese, `jp` = Japanese, `gr` = Greek, `cz` = Czech, `ro` = Romanian). A code the select list does not know makes the form fall back to its first entry and the upload lands as English — therefore it is checked against the language mapping. A series is created via `/add-tvshow1.php` (`name`, `year_s`, `seasons`, `go`), **after** the name has been searched in the series list. A dry run builds the body completely and sends nothing; it sends only with an explicit `dryRun: false`. Verified by T203.

**F-M482 [P]:** **The transport does not decode, and it decides on the bytes that came.** The download endpoint answers in two forms — sometimes with the waiting page including the file name in the script, sometimes with the zip directly (measured). The client checks the zip header (`PK\u0003\u0004`) and parses only when no archive came, instead of relying on a Content-Type that the site does not always set.

The subtitle bytes are passed on **unchanged**: the site delivers per language UTF-8 next to cp1250, cp1251, cp1253, cp1254 and cp1256, and `cp1252` practically never fails — exactly from that arose the codepage error that made Russian, Greek and Arabic unreadable. Decoding happens in the pipeline, not in the transport. Verified by T203.

**F-M483 [D]:** **There are two download endpoints and two upload endpoints, and the rest of the plugin stays as it is.** One per source and direction: `ScribeDownloadEndpoint`/`ScribeUploadEndpoint` and `TvSubtitlesDownloadEndpoint`/`TvSubtitlesUploadEndpoint`, behind a common pair of interfaces (`IDownloadEndpoint`, `IUploadEndpoint`) with a neutral model (`SubtitleQuery`, `SubtitleHit`, `SubtitlePayload`, `UploadTicket`, `EndpointAnswer`).

The SubDL endpoint is a **translation, not a rewrite**: it calls exactly the methods the pipeline called itself before, and routes, quota, cache and gates stay where they were. `DownloadPipeline`, `UploadPipeline`, plugin database, QA gates, scheduler and GUI structure are not touched.

What a source cannot do, **its** endpoint says with a reason instead of sending halfway: season pack (the pipeline builds it, F-M372), missing IMDb identifier, bytes that are not UTF-8 (SubDL takes text, F-M482), unknown language, missing series name, series not on the site. One rejection each, no exception for something the caller could have known. Verified by T204.

**F-M484 [D]:** **An endpoint that is not wired into the run says so — the catalog delivers only what may run.** `SubtitleEndpointCatalog.Enabled…` demands **both**: the switch from F-M477 **and** `Wired`. As long as the pipelines do not call the tvsubtitles endpoint, it stands at `Wired = false` and stays outside even with the switch on.

`Refused(...)` names it together with the reason, and this sentence is **literally the same** one the configuration page carries under the switch and `MissingProviderReason` — one statement, not three (line from F-M366). A wired endpoint whose switch is off reports `switched off`. Verified by T204.

**F-M485 [D]:** **Both directions are switchable individually, and the switches take effect in the run.** With F-M483/F-M484 the four endpoints are wired in, not merely present.

**Download** — if SubDL has no candidate for a language, the second source gets its opportunity, provided its download switch is on. Its hits then run through **the same** ranking, the same gates, the same fit and the same files area as a SubDL hit. They only carry their provider in the candidate and are fetched at their own endpoint (the SubDL routes archive/cache/v2 apply to SubDL only).

**Upload** — SubDL is called exactly as before as long as its switch is on (the response body is read by the caller and preserved). If it is off or does not take the subtitle, the same subtitle goes to the second source, provided its upload switch is on. The series name for the second source comes from the media file name via the same parser the download uses — the source has no id index.

**Every switch acts for itself:** SubDL download on and upload off, tvsubtitles only upload, everything off — the catalog (F-M484) delivers exactly what the switch says. **Limits that remain:** season packs and the HI pool are SubDL's business, the second source can only do single episodes. An error of the second source does not tip the run, it is logged as a warning. Verified by T205 and T202.

**F-M486 [D]:** **A downloaded subtitle carries its origin together with the source's id in the plugin database.** Two fields are kept per subtitle: `Origin` (`subdl` or `tvsubtitles`) and `OriginId` — the source's **own id**, i.e. the tvsubtitles subtitle id (e.g. `170678`) or the SubDL entry id.

A find is thereby unambiguously identifiable: the id names the entry, the content hash names the text, and together they say what came from where. `null` means **„this line says nothing"** (legacy line, or a file the plugin did not fetch) and is never guessed. A later writer without knowledge may not delete an existing statement. The fields live in `sidecars` and `embeds` (schema 10, the migration adds them). Verified by T206.

**F-M487 [D]:** **No cross-contamination: a find goes back only to its own source.** On upload the origin is read from the plugin database via the content hash; if it is **known**, the subtitle may be uploaded exclusively at **the same** source — a tvsubtitles find never to SubDL, a SubDL find never to tvsubtitles. If it is **unknown** (`null`: manual copy, embedded track, legacy line), every endpoint stays permitted, otherwise the operator could no longer upload his own files.

The rejection is logged and names both sides. The rule is deliberately short and sits in one place (`Provenance.MayUploadTo`), so that it does not get two versions in two pipelines. Verified by T206.

**Addendum — Concept decision: a subtitle found at subdl is never uploaded back to subdl and never to tvst.** A known origin is **not uploaded at all** any more — not back to its own source (reup) and not to the other (cross-contamination). A download is a find, not an own candidate. Unknown resp. external origin (embedded tracks, loose `.srt` without origin) stays permitted to **both** endpoints, in chain order and per source with its own mark. The reason stands in the log as `origin:subdl — downloaded there, never uploaded` resp. `origin:tvsubtitles — downloaded there, never uploaded` (`Provenance.OriginBlockedReason`).

**F-M488 [D]:** **Four own identifiers in the plugin database, one per source and direction** — `ScribeDownloadId`, `ScribeUploadId`, `TvSubtitlesDownloadId`, `TvSubtitlesUploadId` (schema 11, in `sidecars` **and** `embeds`, added via `ALTER TABLE`). A line thereby says by itself what happened where; nobody has to interpret `Status`.

The download writes the field of **its** source (`ScribeId` resp. the tvsubtitles subtitle id), the upload writes the id that the source returns for **our** upload. **SubDL** delivers its id in the answer of the upload — nothing new was needed for that, it is merely written additionally into its own field. **tvsubtitles.net returns no id**, therefore it is **read back** from the episode page after sending (language + release must match; `FindUploadedIdAsync`, 60-second leash).

**Every upload gets an identifier:** if the source delivers no id — the read-back finds nothing, or SubDL answers without one — **the plugin assigns its own**. The format is `own:<quelle>:<JJJJMMTTTHHMMSSZ>:<8 Hex>`, always with the prefix `own:`, so that it is never taken for an id of the site (`Provenance.OwnUploadId`, `IsOwnUploadId`). An upload without an identifier could not be distinguished from one that never took place.

Only the field of the source that **really took** the upload is set. Only what is known is written; an empty value does not delete an existing statement. The upgrade path 9 → 11 was tested on a real database. Verified by T207.

**F-M603 [U]:** **An accepted attempt without an identifier closes the pair.** Concept decision: path "A" — Oak Street is old, which reads as a pair not being closed there. Measured was the file "The End Of Oak Street" on 18.11. with **465 log lines, 209 × "still open", 176 SKIP duplicate-self-echo and 0 uploads** — and it returned in every run, each time with extraction (2.7 s) and 26 database writes (~20 s). Cause: the film address reported for the EN track after a real attempt (`search.php` → 500 with body, `add-movie.php` → **200**) `created-id-not-readable`, hence "created, identifier not readable". According to the operator's finding the film does **not exist at all** there ("Osk street does not exist as a film on moviesubtitels"), so the `200` had created nothing.

Exactly therefore it remains a SKIP without an upload claim: the attempt is proven, the success is not. That was booked as a SKIP without a mark — the proof was missing, the pair stayed open, the next run repeated everything. Now there is `UploadResult.AcceptedWithoutId` (SKIP **with** proof: `SourceAccepted`), the mark is written **before** the verdict (`MarkAcceptedSource`, own `own:` identifier) and `MarkOf` lets it through.

**The statistics still claim no upload** (`Ok = false`) — the attempt counts as skipped, not as uploaded. The same gap had the series address (`TvstShowStatus.CreatedIdUnknown`, "created, number not readable", the operator asked whether it is the same uploader): there too the accepted attempt now closes the pair. Checked by T312.

**F-M612 [U]:** **The tvst identifier is no longer read back.** Concept decision: the tvst identifier is not read back — the number addresses nothing, so we write none or our own mark. It stood only as a mark in the database ("uploaded for tvst"), and for that `MarkOf` has always written its own mark `own:tvsubtitles:<Zeit>:<hex>`. F-M488 had built in the read-back because the page returns no id on the upload; the 60 s deadline was the most expensive item of the run. Measured: **88 attempts, 0 readable identifiers, ~15 s each** (largest `send` values 15.3 / 16.0 / 17.3 s), for a file with ~24 tracks six minutes for nothing. The call is removed; the log line now says "none — we write our own marker". The method `FindUploadedIdAsync` remains in the client (it is proven and tested), but is used by no upload any more. Checked by T322.

**F-M489 [D]:** **No reup, and that per source.** „Already uploaded" is no longer read from `Status=Uploaded` (that means „uploaded somewhere"), but from the **source's own identifier** (F-M488): `ScribeUploadId` for SubDL, `TvSubtitlesUploadId` for tvsubtitles — in `embeds` **and** `sidecars`. Thereby: **no reup from SubDL to SubDL**, **no reup from tvst to tvst**, and yet **no mutual blocking** — a tvst upload does not stop a SubDL upload and vice versa.

The decision for both sources falls in **one** place (`UploadGate.Decide`: switch × origin × „does the source already have it") and is used in three places: collection stage (embed), collection stage (loose file), upload itself. Legacy lines without an own identifier count as uploaded for SubDL when `ScribeId` **and** `Status=Uploaded` are set — otherwise every old upload would go out a second time after the upgrade. `ScribeId` alone does not suffice, because the download writes it too. The reason is logged (`already-uploaded:subdl+tvsubtitles`, `cross-source`, `no-upload-source`). Verified by T208.

**F-M490 [D]:** **„Uploaded" stands per source as its own field in the plugin database** — `ScribeUploadedAt` and `TvSubtitlesUploadedAt` (schema 12, `sidecars` **and** `embeds`, added via `ALTER TABLE`). A point in time instead of a switch: „uploaded" without „when" answers half the question, and a line can carry **both** sources — in March to SubDL, in November to tvsubtitles.

Only the timestamp of the source that really took the upload is set; a timestamp is never overwritten with an empty value. `Registry.IsUploadedTo` reads this timestamp first and then falls back to the source's identifier (F-M488) — a line with an id but without a timestamp (legacy data from schema 11) still counts as uploaded. Thereby F-M489 no longer hangs on whether an id was present, but on an own, readable statement. The upgrade path 11 → 12 was tested on a real database. Verified by T209.

**F-M491 [D]:** **The order of the download sources is configurable, and „successfully downloaded" demands the significance.** Under Downloads stands the choice `SubDL first, tvsubtitles.net as fallback` / `tvsubtitles.net first, SubDL as fallback` (`DownloadOrder`, default **tvsubtitles.net first** — Concept decision: tvst is first). If tvst stands in front, it is **asked first**; the SubDL candidates of the same language are **merged** afterwards, not replaced (F-M494). The rest of the run — ranking, gates, fit, files area, origin (F-M487) — is the same for both orders.

**Success term:** with auto-sync on, only a version that has **passed the significance** (`alignedForNaming`) counts as successfully downloaded. A kept original version is **downloaded, not successful** (`summary.DownloadedWithoutSignificance`, the pair stays open via the coverage — F-M452). If auto-sync is **off**, there is no fit and hence no difference: every fetch counts. Applies equally to both sources. Verified by T210.

**F-M492 [D]:** **Credentials and quota follow the sources that are really in play.** Found in the testbench run: a tvsubtitles-first run died twice on checks that concern SubDL only — first on missing SubDL credentials, then on „download limit spent (50/50)". Both belong to the SubDL account, not to the run.

Therefore: the **SubDL API key** is demanded only when SubDL download is on (it authenticates search, file fetch and quota read). The **login pair** is demanded only when SubDL upload is on (it authenticates the upload). The **TMDb key** stays mandatory in both directions and for both sources, because it resolves the ids on which plugin database, ranking and the tvst upload comment rest.

The **download quota** holds a cycle only when SubDL is the only source. If SubDL download is off, it is not read at all; if SubDL is exhausted and tvst available, the cycle runs on tvst and the reason stands once in the log.

The direction gates also ask the **direction**, not SubDL: the download task, the upload task and the light-off states use `AnyDownloadEnabled`/`AnyUploadEnabled`. Otherwise „SubDL off, tvst on" would look like a switched-off direction and the run would be skipped with „Download pipeline disabled" (exactly what happened in the testbench run). The **enqueueing** in the seeder also asks the direction (`AnyDownloadEnabled`/`AnyUploadEnabled`). The SubDL download quota may empty the queue only when no other source is there — otherwise a run with SubDL exhausted and tvst on would fill an empty queue (measured: „0 download queued", although 27 episodes stood there without an ES subtitle). Verified by T211.

**Continued by F-M497:** the *demand* itself has become narrower in the meantime — only the TMDb key is mandatory, and an unconfigured source drops out of the chain instead of blocking the run.

**F-M497 [D]:** **Only the TMDb key is mandatory; SubDL is optional and is passed over without configuration — in both directions.** Concept decision: the TMDb key is the only mandatory configuration; if SubDL is not configured, SubDL upload and download are skipped and the SubDL fields are marked as required.

Until here four credentials stood in ONE mandatory list (F-M19/F-M203/F-M492): if one was missing, the run did not start at all. Thereby a run died on an account it would never have addressed — measured twice on jf-test.

Now: (1) **The TMDb key is the only mandatory one.** It resolves and corrects the ids on which plugin database, ranking, fit and the tvst upload comment rest in **both** directions and for **both** sources. Without it no direction can do its work, so the run is **not started** as before with a named message. `MissingCredentials()` delivers exactly this one entry.

(2) **The SubDL credentials are a source question, not a run error.** If the API key (download) or the login pair (upload) is missing, that is **no blocker**. The source does not step up and is passed over with **one** line (`[SubDL] … — skipped — not configured (required for SubDL only)`, Download: `[SubDL-D] … — SubDL skipped — not configured …`), the other source keeps running.

The question stands as an **own helper** (`IsConfiguredForDownload` / `IsConfiguredForUpload`) and is put **next to** the switch (`SourceAllowed`) immediately before the start of every source — toggle **and** configuration decide together whether a source steps up. In the download the search already drops out without an API key (otherwise its 403 ended the run), and the fetch itself asks the same door a second time.

(3) **The page says it.** The three SubDL fields (API key, user name, password) carry the label **„Required for SubDL only"**, the TMDb key stands as **Required** and as the only mandatory field. And that **before** the SubDL block (use: „Tmdb confi then also before subdl confi in the gui"). Whoever reads the page first sees what he must have. Only the two blocks are swapped; loading and saving runs over the field ids, which know nothing of their position. Field ids and switches stay unchanged, no new option is added.

(4) **Interaction with the dry run (F-M496):** nothing is *necessary* in the dry run — it makes no call and is never turned away for credentials; a source must be *present* only when it really steps up, i.e. live. The two sentences describe two different questions and do not contradict each other. Verified by T216, T217 and T218.

**F-M493 [D]:** **Every files area carries its origin — at all four write points.** Measurement on jf-test: 5 of 24 kept originals stood in the plugin database without `Origin`, because the path „keep original" wrote its own line without origin.

The kept original is its own line, so IT needs the mark — otherwise a later upload does not know where the file belongs (F-M487), and „no reup per source" (F-M489) does not see it. Applies to the main files area, the kept original, the HI files area and the kept HI original. The run completion additionally names `DownloadedWithoutSignificance`. Verified by T212.

**F-M494 [D]:** **Both download endpoints are asked, the second gets its own attempt.** Measurement (jt-test, 27 episodes, tvsubtitles-first): every episode had exactly one tvst hit, and because F-M491 put the tvst list in the place of the SubDL list, **not a single SubDL line** stood in the log. Of 27 episodes 6 were aligned to the sound, 19 remained lying as original, 1 was discarded.

Two consequences: (1) the candidate lists of both sources are **merged** instead of replacing each other — tvst hits and the SubDL hits **of the same language**, each source only if its download switch is on. The log line names **both** numbers (`{Tv} tvsubtitles candidate(s), {Sd} SubDL candidate(s), order …`). (2) The ranking gets a **leading stage by source** that follows the configured order (`TvSubtitlesFirst`: tvst before SubDL; `ScribeFirst`: the reverse); pack and text score stay unchanged behind it — the second endpoint is asked but does not displace the first.

(3) The abort of the correction search applies per source: a rejection word stems from **one** source and says nothing about the other. If among the not yet attempted candidates (`ranked.Skip(attempts)`) at least one of the other source stands, the abort is withdrawn and this source's rejection counter set to 0 — the other source gets its own attempt. Only when no candidate of the other source is left does the search end as before (F-M318).

(4) **Before the start of every endpoint** it is checked whether exactly this source is released by its switch — via **one** helper (`SourceAllowed(provider)`), which the start of the tvst search, the main pass, the SDH variant and the fetch itself use. A switched-off source means „skip this candidate", never „end the run", and the line `candidate from {Source} skipped: that source is switched off` stands in the log (Concept decision: before starting the respective endpoint it is checked whether the toggle releases it). Verified by T213.

**F-M504 [D]:** **The sources are asked in the configured order — an empty first source no longer ends the procedure, and the sources are a list, not a pair.** Measured in the trial run on jf-test (dry run with production configuration, `DownloadLanguages` DE): for every episode only `[SubDL-D] … — no candidates on SubDL for [DE]` stood in the log. **27 lines, not a single `[tvsubtitles-D]` line**, although the tvst download switch was on the whole time.

Cause was the construction, not a switch: after the SubDL search the procedure ended at `candidates.Count == 0`, and the place that asked the second source lay **behind** it in the language loop. The second source was thereby mute exactly in the case it exists for (F-M485) — with the first source holding hits everything ran as expected.

Now: (1) **The sources are asked in the configured order, not merely sorted in it** (Concept decision: the search engines are asked in order; at the moment there are 2). Until then the SubDL search ran as a fixed block **always first**, and `DownloadOrder` decided only about rank and follow-up inquiry — with `TvSubtitlesFirst` tvsubtitles was therefore asked **after** SubDL.

Now the inquiry order is a **loop over the same list**: if tvsubtitles.net stands in front (default), tvst is asked **first** for all missing languages and **then** the SubDL search is run; if SubDL stands in front, it is the reverse. The position of the SubDL search in this list (`SourceOrder.MainSearch`) is the one statement that determines the pipeline; it remains an **own inquiry** (id check with backfill, HI pool, quota paths) and is merely no longer fixedly the first.

It is asked **before** every exit decision, for **all** missing languages, **each source exactly once per language** — and the loop has **no early exit**: an empty answer of the first source ends nothing and skips no source (both remain the occasion of F-M504). The **ranking of the hits follows the same order** — rank 0 is the source asked first —, pack and text score stay unchanged behind it.

(2) **Exactly once per source and language:** the answer lies in a dictionary (language → hit list) and is only **read** in the language loop; there is exactly **one** call site of `ForeignCandidatesAsync`. A double fetch (same question, second network access, potentially different answer) is thereby excluded — the rule applies to **every** source, not only to the second.

(3) **Only when all asked sources are empty** does the previous result stand (`NotAvailable`, `SkippedNoCandidates`, return without fetch). The line names the sources that were **actually** asked (`no candidates for [DE] — asked: subdl, tvsubtitles`), instead of claiming a SubDL search in which tvst was not involved.

(4) **No two-source assumption** (Concept decision: a third to n-th source is likewise conceivable): the list comes from `Enum.GetValues<SubtitleProvider>()` in the order of **`SourceOrder.ForOrder(DownloadOrder)`** — the configured source first, all others in enum order —, and **per source** `SourceAllowed` decides whether it is asked. A third source therefore needs only an enum entry and an endpoint in the catalog; the loops, the search (`ForeignCandidatesAsync(quelle, …)`) and the fetch (`EndpointFor(provider)`) do not change.

(5) **The gates stay unchanged:** `DownloadOrder` decides **who is asked first** (and thereby who leads the ranking) — not **whether** it is asked. The leading rank stage is a **rank** in the inquiry order (`QuellenRang`, 0 = first), with two sources the same statement as before, with three correct without a rebuild. The significance term (F-M491) is **not** touched. Films are untouched: `ForeignCandidatesAsync` exits itself without series/season/episode before it touches the network (F-M480/F-M503).

**The upload does not have this error** (its chain asks every source per candidate, a failure is logged as a warning and the next source keeps running, F-M495). There only the order derivation was two-valued and now uses the same `SourceOrder` helper.

**Remaining one-source assumption:** the main search is SubDL's id search (`RunV2SearchAsync` via `ScribeApiClient`, with id check, HI pool and quota paths) and delivers the candidates of the leading SubDL source itself. Converting it to the endpoint catalog would be an own rebuild and is named as `MainSearchSource` (an alias on `SourceOrder.MainSearch` — inquiry order and pipeline thereby read the same statement). Verified by T226.

**F-M495 [D]:** **The upload too asks both endpoints one after the other, in the configured order.** Until here the run chose exactly **one** target (`vonZweiter`) and took the second source only when the first had rejected. Afterwards exactly one source stood in the fields.

Now a **chain** runs: source A is attempted, then source B — in the configured download order (no own setting, no new GUI option) —, and every source gets its own attempt. Before the start of **every** endpoint its **toggle** is asked via **one** helper (`SourceAllowed`, the same checkpoint as gate and collection stage). Off means „skip this source" with a log line (`source {Source} skipped: that source is switched off`), not „end the chain".

The existing gates remain and are asked **per source** instead of being abolished: (a) „this source already has it" via its timestamp and id fields (F-M488/F-M490), (b) origin via `Provenance.MayUploadTo` — known means **only** to the own source, unknown everywhere (F-M487). A forbidden target is skipped and named (`cross-source`, `already-uploaded:<Quelle>`). An **error** at source A does not prevent the attempt at source B (principle: the priority always goes; if the second cannot, that is acceptable) — the exception is logged as a warning, the chain keeps running.

After the chain **both** id/timestamp field sets are set, as far as the respective source took it. The SubDL-specific id follow-up inquiry (`mySubtitles`) runs only when SubDL really took it. Verified by T214.

**F-M499 [D]:** **The upload queue too asks per source — the second endpoint gets its attempt all the way into the queue.** Measured at 16:04 in the testbench: a run had uploaded 27 embedded FR tracks via SubDL. Afterwards the seeder enqueued **0 episodes** (`scan finished (upload) — 0 upload queued`), so no further run started, and the second endpoint never got its own attempt.

Cause was the question „already uploaded" in the queue: it ran via `ArePositionsTerminal`, `CountKnownPairs` and `IsSidecarUploaded`, and those read `Status=Uploaded` — that means „uploaded somewhere" (F-M489 has abolished exactly this reading in the upload itself).

Now the condition is per source. `IsUploadTodo` runs over the sources that **switch and configuration** jointly permit (`IsUploadEnabled` **and** `IsConfiguredForUpload`, F-M477/F-M497), and the item is todo as soon as **one** of these sources does not yet have its own mark (`IsUploadedTo(…, provider)`, F-M488/F-M490 — in `embeds` **and** `sidecars`). The blind readers remain but are no longer used by the queue: `ArePositionsTerminalFor`, `CountKnownPairsFor` and `IsSidecarUploadedTo` are their per-source counterparts, and „this source has it" is read via the same rule at all three.

A **rejection** stays source-neutral: it is a verdict about the subtitle (structure, running time, language check), is written before every source choice, and reading it per source would hold the track open for the other source forever.

The language remains the source's business — here **no** common language or codec mapping is introduced. Concept decision: the mapping stays tvst specific; the other mapping is SubDL specific. So tvst maps in `TvSubtitlesClient`, SubDL in `ScribeApiClient`, and the pair counter keeps using SubDL's own mapping without deciding a language question with it.

**Second part, measured at 16:17 in the live run:** the seeder enqueued 27 episodes, and the pipeline threw them **all** away again (`[SubDL] SKIP {File} — upload file-complete`). The run ended with `0 uploaded` — the same source-blind question still stood in the head of the run. The file mark `SubtitlesUploadedAt` says „this file is done", not „who made it done", and tvsubtitles had no track yet on these files, so it never got its turn.

The mark stays source-blind — it describes the file, not the endpoint — and its per-source counterpart `IsSubtitlesUploadedFor(mediaHash, provider)` filters it at the source. It holds only when it stands **and** every embedded track is done for **this** source (`IsEmbedTerminalFor`, i.e. the source's own mark or a source-neutral rejection — the same reading rule as in the upload itself).

The run skips a medium only when **none** of the switched-on **and** configured sources is still open (`UploadSources()`; in the dry run without the configuration question, F-M496) and names them in the skip log. If a source is open, it stands in the detail log. Without a switched-on source and without track evidence there is **no** skip: then the run works the item instead of putting it aside on suspicion for all. Verified by T220 and T221.

**F-M500 [D]:** **The SETTLE verdict too („is this subtitle already uploaded") asks per source — an upload at SubDL does not settle tvsubtitles.** Measured at 16:24 in the live run: the run processed the 27 embedded FR tracks but ended with `0 uploaded | 34 rejected, 61 stream(s) skipped`, and **every** line read „identical subtitle already uploaded earlier (F-M298)".

The place is `Registry.RecordSkippedContent`, called from four points of the upload path (reject replay embedded and loose subtitle, QA dedupe, skip of the chain). It decided the reason `duplicate-content` via `IsContentUploaded` — i.e. via „uploaded somewhere". If the text lay at SubDL, tvsubtitles counted as done and never got an attempt.

Now the verdict takes the source along. `IsContentUploadedFor(contentHash, provider)` answers per source from that source's **own** mark (timestamp, own id, for SubDL the legacy line `Uploaded`+`ScribeId`, F-M488/F-M490), and `RecordSkippedContent(…, settledSources)` writes the `Uploaded` line **only** with the marks of the sources that really hold the content. A source without the content stays open.

The rule stands ONCE in `UploadPipeline.SettleDuplicateContent`: if the content lies at one source and an **other** is still open (switched on, configured, without the content), the candidate counts as **not** settled, the rejection is not written, and the candidate goes on to the chain. There `UploadGate` asks per source, and SubDL recognises its own text as `already-uploaded:subdl`. **Pure content dedupe against duplicate entries at the SAME source remains:** the same content may go up at tvst although it lies at SubDL, but not twice at tvst. A **rejection** stays source-neutral, and `duplicate-remote` (the server's verdict) is likewise no source evidence.

**The origin gate applies unchanged strictly and is only read.** Concept decision: a subtitle downloaded from one source is never uploaded to any source — not back to the source it came from and not to the other. The settle path asks `Provenance.MayUploadTo` before it lets a candidate run on; a subtitle with **known** origin gets no attempt on this path either, and `IsEmbedTerminalFor` reads the same origin, so that such a track is not enqueued again in every run.

**Unknown resp. external** origin (embedded tracks, loose `.srt` without origin) may go to both endpoints, in chain order and per source with its own mark. The queue reads the same question (`ScribeSeeder.SidecarsNeedWork`: known is no longer a skip when the **other** source holds the content). An `Uploaded` line **without** a source mark (settle from before F-M500, pack line) still counts as SubDL evidence, so that this text does not go to SubDL a second time. For tvsubtitles it does not count — otherwise the rebuild would continue the measured defect. Verified by T222.

**F-M501 [D]:** **The tvst series number stands in the plugin database, and a missing series is created with the TMDb title.** Concept decision: the tvst series number goes into the store on the same level as the TMDb show id and the TMDb id. The TMDb title leads, also for duplicate entries. The series lookup is a similarity search after all: case ignored, the ampersand counts as the same word as and, spacing is flexible, and the match stays tight.

Until here the endpoint resolved the series on EVERY call from the **file name** (`MediaNameParser`) and compared with „contains" — a guess about a spelling, and the number stood nowhere, which is why the plugin database could never learn it. A series the site does not carry was never created.

(1) **The field `MediaEntity.TvstShowId`** (`string?`) sits directly next to `TmdbId`/`ImdbId` — no own table, because it answers the same question: which series IS this file. Schema **12 → 13** with migration at start (`EnsureMediaColumns`: `ALTER TABLE media ADD COLUMN TvstShowId TEXT NULL`, bound to the column list, failure logged and swallowed). An existing plugin database stays valid, all lines read the field as `null` = „not yet determined".

(2) **The order of resolution** (the path that searches and creates the series) is fixed. **a)** if the number stands at the medium, IT is used — **no network access for the search, no comparison, no creation**.

**b)** otherwise the TMDb title is searched in the site's series list, **compared tightly** (`NormalizeShowName`: upper/lower case irrelevant, `&`/`and`/`und` as the same **word**, whitespace to one space, otherwise **equality** — no substring, no Levenshtein, no token quota, no cutting of leading articles, no year tolerance). A hit is written into the medium and used.

**c)** no hit: create with `name` = TMDb title, `year_s` = TMDb first-air year, `seasons` = TMDb season count **limited to 1..22**. The own check `BuildAddShow` now likewise permits only 1..22 instead of 1..100, because the site's select field does not reach further and a larger value would be silently pulled to 22. Then fetch the series list **again** and search tightly again: **only when the number stands there** is it written. An unclear answer (timeout, exception, page without select field) is followed up **once**; if it stays unclear **nothing** is written, a line is logged and tvst is skipped for this medium. **d)** creation happens **at most once per medium and run** (`BeginRun` clears the lock at the start of the run), so that a failure is not attempted again on every episode.

(3) **None of this blocks the chain:** every outcome is a result, not a throw — if the number is missing or the creation fails, tvst is skipped (`Skipped`) and SubDL gets its attempt as before. Without a TMDb identifier or without a TMDb series record it is likewise skipped instead of guessed. The TMDb record (name, original title, first-air year, season count) comes from `TmdbImdbResolver.SeriesInfoAsync` (`/tv/{id}`, cached).

(4) **The dry run creates nothing and writes nothing** (`would-create`, no POSTs). The endpoint uses the number from the ticket (`UploadTicket.TvstShowId`) and then fetches the series list **not at all**; only without a number does it search the name, with the same tight comparison. Verified by T223.

**F-M502 [D]:** **The language check of the tvst upload runs via a tvst-specific bridge from ISO to the site's codes — and only there.** Concept decision: the mapping stays tvst specific; the other mapping is SubDL specific.

Measured: `TvSubtitlesClient.Languages` is **complete** — the site's 24 codes, congruent with the real form `/add.html` —, but four of them are **not ISO**: `gr` instead of `el`/`ell`/`gre`, `cz` instead of `cs`/`ces`/`cze`, `cn` instead of `zh`/`zho`/`chi`, `jp` instead of `ja`/`jpn`.

Jellyfin delivers languages as **three-letter codes** (`gre`, `cze`, `chi`, `jpn`), configuration and target languages partly as **two-letter codes** (`el`, `cs`, `zh`, `ja`). If such a code arrived, the check of `BuildUpload` found nothing and the upload was rejected with „language must be one of the site's codes".

Now: (1) **`TvSubtitlesClient.SiteLanguage(code)`** is the only mapping place. It knows the four deviations in every common spelling (`el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`, `zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp`) and the remaining site languages in their ISO-639-2 form (/B and /T). These are: `eng`→`en`, `spa`→`es`, `fra`/`fre`→`fr`, `deu`/`ger`→`de`, `pob`→`br`, `por`→`pt`, `rus`→`ru`, `ukr`→`ua`, `ita`→`it`, `ara`→`ar`, `hun`→`hu`, `pol`→`pl`, `tur`→`tr`, `nld`/`dut`→`nl`, `swe`→`sv`, `dan`→`da`, `fin`→`fi`, `kor`→`ko`, `bul`→`bg`, `ron`/`rum`→`ro`. The site's own codes stay valid unchanged.

(2) **Upper/lower case does not count**, and a statement with region or script falls back to the base code: `zh-Hans`/`zh_CN` → `cn`, `ja-JP` → `jp`, `es-419` → `es`. The **one** exception is the region that IS the language: `pt-BR` (also `pt_BR`) → `br` (Brazilian Portuguese), while `pt` and `pt-PT` stay European Portuguese (`pt`) — the site carries both separately, therefore this rule stands **before** the base-code reduction.

(3) **Unknown means `null`, not guessed:** what stands neither in the bridge nor in the language mapping is rejected as before, with **one** line that names the site's codes (`language must be one of the site's codes (ISO codes are mapped): …`).

(4) **The bridge applies at every language site of the client:** the check of `BuildUpload` (and thereby the field `lang`, which now always carries the **site code**). The language mapping `TvSubtitlesDownloadEndpoint.SiteLanguage` (which now only passes through) and the read-back `FindUploadedIdAsync`, which sends the searched code and the flag language of the entry through the same bridge. Otherwise it would not find an entry that the upload has just created.

**The upload's file name is not rewritten:** it keeps its ending, and the three letters for the name continue to come from the site's language mapping (`gre`, `cze`, `chi`, `jpn`, `pob`, `rum`). **No common mapping:** SubDL maps in `ScribeApiClient` and stays untouched (F-M499). Verified by T224.

**F-M505 [D]:** **The release field of the tvst upload carries the release GROUP, not the file name — and stays empty when the name names no group.** Finding on the site: in the release column of tvsubtitles.net stands the group. 193 entries of the first season of *The Middle* read out — 2HD (28×), REWARD (24×), FQM (14×), FEVER, POW4, CTU, LOL, DIMENSION, **91 entries empty**, and **not a single one** with dots.

Our upload carried the media file name instead: on `episode-26403.html` stood literally `The Middle.S01E21.MULTI.1080p.WEB-DL`. The rest `.H265-FTMVHD` was missing, because the site cuts the value at the hyphen server-side, and the group (FTMVHD) did not appear in the field at all.

Rule: the field is filled from the release name via `SeasonPack.ReleaseGroup` (the same rule as for the download rank, F-M430). If the name names no group (`null`), an **empty field** is sent — that is a permitted value and the majority of the site, so neither a reason to refuse the upload nor an invention. The file name may no longer occur in the tvst ticket. Verified by T227.

**F-M506 [D]:** **The third source is a client for films alone — with its own mappings, its own resolver, and without any wiring.** Finding, recorded **exclusively reading** (no POST to `add.php`/`add-movie.php`, no upload, no film created): moviesubtitles.org has **no API** (`/api.php`, `/sitemap.xml`, `/rss.xml`, `/login.php`, `/panel.php`, `/upload.php` → 302 to `/`), everything runs via HTML. `movies-<A..Z|0>.html` **or** `search.php?q=` **or** `add.php?filter=` → `movie-<id>.html` → `subtitle-<id>.html` → `download-<id>.html` → **302 with a RELATIVE `Location`** → `files/<…>.zip`.

Three measurements forbid any reuse of the tvsubtitles mappings. The supported languages list has **13** instead of 24 codes (no `pt`/European Portuguese, no `cz`, `cn`, `jp`, `nl`, `sv`, `da`, `fi`, `ko`, `bg`, `ro`), and the file ending is the site's **two-letter code** (`Oppenheimer_2023.WEB-DL.MA.en.zip`), not ISO3. The rip field knows **eight named values** (`DVDRip BluRay BrRip BDRip HDRip WEB WEBRip WEB-DL`) plus the empty value, which the select field carries **twice** (first and last entry) — so nine different values. `HDTV`, `HEVC` and `DVDSCR` are missing there and `WEB-DL`, `WEBRip`, `BrRip`, `HDRip` exist only here. `search.php` answers **HTTP 500 with a valid hit list in the body**; titles stand **inverted** (`Matrix, The`); an unknown id looks like success (`subtitle-…` HTTP 200 with an empty title and `size: 9140 kb`, `download-…` 302 to `files/`).

Rule: (1) **Two own mappings per source** — `MovieSubtitlesClient.Languages` (13 values with display name, `portugese(br)` in the site's spelling, and the file code) and `MovieSubtitlesClient.Rips` (the nine values in form order); no common mapping with tvst and none with SubDL.

(2) **The language bridge knows only what the 13 give:** `eng spa fra/fre deu/ger pob rus uk/ukr ita el/ell/gre ara hun pol tur`, region only where it IS the language (`pt-BR`/`pt_BR` → `br`). `pt`/`por` (European Portuguese), `nl`/`dut`, `sv`, `da`, `fi`, `ko`, `zh`/`zho`/`chi`, `ja`/`jpn`, `bg`, `cs`/`ces`/`cze`, `ro`/`rum` and everything unknown deliver **`null`** — **no fallback to English**, no guess.

(3) **Rip: source before codec, and NO codec fallback.** `WEB-DL` stays `WEB-DL` (not `WEB`), `BluRay.x265` becomes `BluRay` — but `x265`, `HEVC` or `H.265` **alone** yield the **empty** value, not `HEVC`. tvst pulls a codec onto `HEVC` because its select field carries `HEVC`. This one carries no codec, and a codec as rip would be a claim about the source that the site does not make. For the same reason `HDTV` stays empty.

(4) **Resolution tight and without guessing:** the comparison key (`TitleKey`) lifts the article inversion (`Matrix, The` = `The Matrix`) and otherwise applies exactly the three alignments from `TvSubtitlesClient.NormalizeShowName` (upper/lower case, `&`/`and`/`und` as word, whitespace). The **year must match** (a wrong year is no hit, not even „approximately"), and if **more than one** candidate remains, **nothing** is delivered — the site has no year search and no desired id (`filter=dune` → five films).

(5) **All parsers are `static`** and take the HTML as a string; the language of an entry comes from the **flag image of its own block**, and free rip values of old entries (`1080p`, `720p.BlueRay`) stay — the nine values are a **write** list.

(6) **Download:** the 302 is evaluated (target **relative**, parentheses such as `ru(2).zip` remain part of the name), decided on the **bytes** whether a zip (`PK\x03\x04`) or a bare `.srt` came. And from the zip the file with the language code is taken, otherwise the first. `parts`>1 is **marked** in the result (`Members`, `IsMultiPart`) instead of being silently glued together — that is a cut film, not a pack. A target `files/` is the **error signature** and is refused.

(7) **Upload:** the fields literally as the form (`tvid lang rip release author UFile UFile2 UFile3 comments go`), `go=1`. **`author` is absent entirely** when it is empty (a missing part does not overwrite a prefill). **`release` carries the release GROUP** via `SeasonPack.ReleaseGroup` (the same rule as F-M505), **never the file name**, and stays empty when the name names no group. `comments` carries the identifiers and the client (TMDb/IMDb + assembly version); `UFile2`/`UFile3` go along only when they are filled.

The check runs **before** building: `tvid` > 0, language in the mapping (ISO is mapped), rip in the mapping **or empty**, at least one id, file not empty, ending `.srt`/`.zip` — otherwise `ArgumentException` with **one** line and **no network call**. `dryRun` comes from the caller and is never read from the configuration.

(8) **None of this is wired:** no configuration field, no plugin database column, no endpoint, no entry in the source catalog — that is stage 2. The client stands alone so that the protocol can be proven against the recorded pages before anything depends on it. Verified by T228.

**F-M507 [D]:** **The third source is a provider — and it is responsible ONLY for films; TMDb decides the media kind.** Concept decision: the third source carries the series to tvsubtitles.net and the films to moviesubtitles.org, for upload and download alike; what a title is gets resolved via TMDb; SubDL does not differentiate and stays responsible for both. Stage 2a wires the **download** of it; upload path, GUI fields and documentation chapter of the source are stage 2b.

Rule: (1) **The assignment stands in ONE place** (`SourceOrder.AppliesTo(provider, isSeries)`). SubDL does not differentiate and stays responsible for **both**; tvsubtitles.net carries series (`isSeries`), moviesubtitles.org carries films (`!isSeries`) — mutually exclusive. The media kind comes from the **TMDb resolution** (`isSeries` after `VerifyIdsAgainstTmdbAsync`/`ResolveByMultiSearchAsync`), **never** from the file name and never from the library name.

With one medium only ONE of the two pages is thereby ever applicable. The order setting (`DownloadOrder`) therefore in fact decides only between SubDL and the responsible page, and `DownloadSourceOrder` needs no third value.

(2) **The source's two switches** (`MovieSubtitlesDownloadEnabled`, `MovieSubtitlesUploadEnabled`, both `bool?`) fall contrary like with tvst (F-M503): `null` → download **on**, upload **off**; an explicit `false` stays off, an explicit `true` holds. The old switch (`DownloadEnabled`) does **not** answer for this source (F-M477). `HasProvider` is true for it, `IsConfiguredForDownload`/`IsConfiguredForUpload` too — the site takes no access.

(3) **The origin knows it** (`Provenance.Name`/`Parse` = `moviesubtitles`), and the one rule remains: a **known** origin is not uploaded to **all** targets (F-M487).

(4) **Plugin database, schema 14:** `sidecars` and `embeds` get `MoviesubtitlesDownloadId`, `MoviesubtitlesUploadId`, `MoviesubtitlesUploadedAt`; the medium gets `MsubMovieId` (`string?`) on the same level as `TvstShowId`/`TmdbId`. Added at start (`EnsureReportColumns` for the two subtitle tables, `EnsureMediaColumns` as a list for the medium), every `ALTER` bound to `PRAGMA table_info`, a failure logged and swallowed. Existing lines stay untouched and read the new fields as `null`. The upload timestamp and the upload id are written only in stage 2b.

(5) **The download path** asks per medium only the responsible sources (`DownloadQueryOrder(isSeries)`), every applicable source **exactly once per language**, without early exit. The gates (`SourceAllowed`, wired endpoint) stand before the inquiry.

For the film page the resolution runs via the **TMDb** data: `TmdbImdbResolver.MovieInfoAsync` delivers title and year, `MsubMovieResolver` reads `MsubMovieId` from the medium or searches it (title **and** year, compared tightly, inverted spelling, nothing on ambiguity) and writes it firmly. `MovieSubtitlesDownloadEndpoint` fetches the ONE film page (cached 15 minutes) and filters the entries via the language **of their flag**. Films without year, without a TMDb record or without an unambiguous hit get **one log line and no error** — the other sources keep running. The ranking of the hits stays unchanged (inquiry order → pack → text score); the significance term (F-M491) is not touched.

(6) **The upload path does not know the source.** Concept decision: rather than upload wrong languages, nothing is uploaded. `SubtitleEndpointCatalog.HasUploadPath` is false for it in stage 2a, and the chain order is cut to that. It does **not even stand there as a dead entry**, because the chain has exactly one branch for „everything except SubDL" (tvst) and a dead entry would get its endpoint.

**Continued by F-M508:** the upload path exists since stage 2b, `HasUploadPath` is true for it, and the chain has no „everything else" branch any more; it stays locked via the switch (default: off). Verified by T229/T230.

**F-M508 [D]:** **The upload path of the film endpoint is built, wired and LOCKED — it is released only after a proven language mapping.** Concept decision: the series go to tvsubtitles.net and the films to moviesubtitles.org, for upload and download alike, and rather than upload wrong languages nothing is uploaded. Stage 2b builds the path and does **not** arm it.

Rule: (1) **One endpoint, one ticket, one film** (`MovieSubtitlesUploadEndpoint`, `IUploadEndpoint`, `Provider = MovieSubtitles`, `Wired = true`): the ticket carries title, year, `MsubMovieId` and the release name. Season or episode in the ticket are **rejected** („a film has no season and no episode"), a season pack likewise — a film has neither.

(2) **The number leads from the plugin database:** `MsubMovieId` is used when it is set (then the endpoint does not ask the site's search at all). If it is missing, it resolves via title **and** year, and without a year nothing is guessed.

(3) **The payload comes from `MovieSubtitlesClient.BuildUpload`**, unchanged: `tvid lang rip release`, then `author` **only when set** (the plugin carries no author; a missing part does not overwrite a prefill), then `UFile`/`UFile2`/`UFile3`, finally `comments` and `go`. `release` is the release **group** from the release name, never the file name (F-M505).

(4) **No invented id, no invented moderation:** what the site says on acceptance is **not proven**; the endpoint passes the text of the answer through, reports success only on HTTP 2xx and returns `Id = null` („accepted, id unknown"). The identifier is assigned by the chain as `own:moviesubtitles:<Zeit>:<hex>` (F-M488). A non-2xx is a rejection that names the status and carries the site's text.

(5) **The chain is a loop over the responsible sources** (`UploadSourceOrder(isSeries)` → `SourceOrder.ForMedia`), no case distinction „SubDL or everything else" any more: for a series SubDL and tvsubtitles.net stand in it, for a film SubDL and moviesubtitles.org. Per source: own switch (`SourceAllowed`), own configuration, own gate (`UploadGate.May`), own dispatch (`SendToSourceAsync`), **own result** in a files area per source, **own mark** and **own timestamp** (`MarkOf`), and an error at one source does not end the chain.

`UploadGate.Decide` knows the third endpoint (`MovieSubtitles`, `AllowedCount`), `IsUploadedTo`/`IsContentUploadedFor` read its columns, `RecordSkippedContent` writes its timestamp, the mark-less line counts its mark too.

(6) **The lock is hard:** `MovieSubtitlesUploadEnabled` is `bool?` and **off** in the unset case (F-M503 rule). That a source therefore does not send stands as a line in the run („skipped: that source is switched off, nothing sent", in the dry run „DRY-RUN … not sent"). **In this stage the endpoint is checked exclusively against fixtures — no POST to the site, not even as a test.** Verified by T230.

**F-M509 [D]:** **Two sources, not three: SubDL and the tvst family — and the family resolves to its endpoint by media kind.** Concept decision: the sources separate into SubDL and the tvst family; the tvst family is split within itself into tvsubtitles.net and moviesubtitles.org.

Rule: (1) **At the top level there are two sources** — SubDL and the tvst family. The family is **split within itself** into tvsubtitles.net (series) and moviesubtitles.org (films); which of the two is asked is decided **solely** by the media kind from TMDb (`SourceOrder.FamilyPage(isSeries)`).

(2) **The order applies at family level**, not at page level: `DownloadSourceOrder` stays **two-valued** (SubDL first / tvst first) — there is **no third value**. „tvst first" means tvsubtitles.net first for a series, moviesubtitles.org first for a film, then SubDL; „SubDL first" is the reverse. Thereby the setting acts in **both** media kinds; the gap from F-M507 („tvsubtitles first" had no effect on films because SubDL always led) is closed.

(3) **`SourceOrder.ForMedia(order, isSeries)`** delivers exactly **two** entries in every media kind: the family's endpoint for that kind and SubDL, in the configured order; `FamilyFirst(order)` and `InFamily(provider)` say the same one level down.

(4) **The upload follows the same order** (there is still no own upload setting, F-M495), and there too the endpoint of the family that carries this kind stands in every media kind.

(5) **The GUI shows the family as one source with two endpoints:** a common heading over the two switch pairs (download/upload per page), then the two-valued select field of the order. It is **one source with two endpoints**, not three sources. Verified by T230.

**F-M510 [D]:** **The first film run on a film library says WHICH endpoint was asked, WHICH source delivers the winner — and the title comparison against TMDb separates a bracketed year.** Measured at 22:12 on jf-test (first film library `Movies_Test`, `Oppenheimer (2023)`, TMDb 872585, IMDb tt15398776, `DownloadLanguages` EN, dry run, `DownloadOrder` tvst-first):

```
[V] [tvsubtitles-D] Oppenheimer (2023).mkv [EN] — 2 moviesubtitles candidate(s), 9 subdl candidate(s), order moviesubtitles, subdl
[V] [SubDL-D] DRY-RUN Oppenheimer (2023).mkv [EN] → MA (score 0, hi=no)
[V] [SubDL-D] [TMDB] strict title+year test found no hit for "Oppenheimer (2023)" (2023) — not confirming
[V] [SubDL-D] Oppenheimer (2023).mkv — 1 language(s) closed by the QA budget this run; nothing left to search
```

Three findings, three rules: (1) **The label of the collection line is the endpoint that was asked, no fixed text.** The line counted correctly (`moviesubtitles` stood in the count), but its label claimed the **series** page of the family — the text stood fixed in the code. It now comes from `SourceOrder.FamilyPage(isSeries)`, the one place where the media kind selects a page: film → `[moviesubtitles-D]`, series → `[tvsubtitles-D]`.

The **statement** of the line stays unchanged — candidates **per asked source** and the inquiry order. The count keeps running over the list of sources, not over a fixed name. The same applies to the shared lines of both directions: the upload dry run and the count read `Provenance.Name(quelle)`. A fixed label remains permitted only where it stands in the **own** endpoint branch (the film endpoint calls itself `[moviesubtitles]` in its film branch).

(2) **The dry run names the winner's origin.** The line reads now `DRY-RUN <Datei> [<Sprache>] → <Release> (<Quelle>, score <n>, hi=<ja|nein>)` — with two asked sources, „who won" is exactly the question a dry run is to answer. The source comes from the **candidate** (`cand.Provider`), not from the inquiry order: only the candidate knows where it comes from. `score` and `hi` keep their place and order, the line shape of the other suites is untouched.

(3) **A final bracketed year belongs separated off before the TMDb title comparison — only there, and only a final one.** `Oppenheimer (2023)` + year 2023 found nothing, because **both** the search inquiry and the year hit carried the name with the year statement. TMDb carries the film as `Oppenheimer` (`release_date` 2023-07-19). `TmdbImdbResolver.ComparisonTitle` removes **one** final `(Jahr)`, `(Jahr–Jahr)` or `[Jahr]` (also with `-`/`–`/`—` as span) and is used at **both** title searches (type-neutral `search/multi` and typed `search/movie|tv`), together with the cache key, so that the second inquiry does not cost again. A separation is logged in the dry-run trace as `title for the compare: "<roh>" → "<rein>"`. **Only for the comparison:** the display name of the item, the file name, the loose subtitle name, the pack formation and `MatchesRelease` read the raw name unchanged — nothing is renamed. **(Ghost) Riders In the Sky (American Outlaws_ Live …)** therefore stays as it is, likewise **Blade Runner 2049** (year in the title, without brackets) and the doubling `O Beautiful (2002) (2002)` (only ONE final year goes).

(4) **Finding without change — „closed by the QA budget" is wrongly labelled in the dry run** (decision open): `closedLangs` has exactly two sources, „not available" and the branch `if (savedAny)`. The dry run sets `savedAny = true` (F-M22, „would have saved") and thereby closes the same language without **any** QA gate having been asked. Evidence: the same run reports „1 would have saved … 0 lang not available", and the plugin database contains **no** `rejected_candidates` line for this medium (the three present belong to another episode). So it is **no** spent budget and no budget of the second source — it is the would-save mark. The comment at the log line records that; the text itself is **not** changed. Verified by T231/T232.

**F-M511 [D]:** **The quality from the release name is pulled onto a rip value of the film page for the film upload — once in the endpoint, before the form.** Finding on jf-test (first film upload after F-M508): the chain passed the quality **raw** into the ticket (measured `"web"`), `Api/Endpoints/MovieSubtitlesEndpoints.cs` gave `ticket.Rip` raw to `MovieSubtitlesClient.BuildUpload`, and the client accepts only the nine values of its select field. The answer is `ArgumentException` with the wording „rip must be empty or one of: DVDRip, BluRay, BrRip, BDRip, HDRip, WEB, WEBRip, WEB-DL (Parameter 'upload')", from that `Ok=false`. Only the **empty** statement got through: so **every** film upload failed.

Rule: (1) **The mapping is mandatory on the way into the form** — `MovieSubtitlesClient.RipFromQuality(ticket.Rip)`; the language mapping is the site's (F-M506): `web`→`WEB`, `1080p BluRay x264`→`BluRay`, `WEB-DL`→`WEB-DL`, `HDTV`→empty (this site does not know it), `x265`→empty (no codec fallback), empty→empty.

(2) **It sits in the ENDPOINT, not at the call sites:** the endpoint is the one seam of every film upload (chain as well as direct call). The chain keeps passing the raw quality in — as it does for the series endpoint, where `TvSubtitlesClient.RipFromQuality` does the mapping (F-M498).

(3) **Dry run and the real case are the same body:** the payload is built **once before** the dry-run branch, therefore the dry run shows byte-identically what the real case sends.

(4) **The mappings stay separate** (F-M506): the film page keeps `WEB-DL` where the series page writes `WEB` — borrowing the foreign assignment would mean sending a value that the select field of this page means differently. Verified by T233.

**F-M512 [D]:** **The seeder determines the media kind from the PLUGIN DATABASE (TMDb-corrected) — Jellyfin's type is only the fallback.** Concept decision: the type is determined based on TMDb.

Finding: `ScribeSeeder.IsUploadTodo` read `item is Episode ? true : item is Movie ? false : null`, i.e. **Jellyfin's** type. The pipelines however correct the kind from the file name and from TMDb (`UploadPipeline`: file name, then `verified.IsSeries`) and write it via `ContentHashRegistry.PatchMediaMetadata` into the media line — the seeder **never** saw the correction. Consequence on a deviation Jellyfin↔TMDb: Jellyfin says film, TMDb says series → the queue considered only the **film endpoint**, an open tvst source **never** got the item. Conversely a never fulfillable endpoint was enqueued anew in **every** scan (no-op runs).

Rule: (1) **First source is the plugin database:** `ContentHashRegistry.MediaKindIsSeries(mediaHash)` answers `true` (series) at `media.IsSeries`, `false` (film) only for a **resolved** line (it carries the IMDb/TMDb identifier of the same patch) and `null` when the line proves nothing. `false` alone is no evidence, because any arbitrary line (`EnsureMedia`, `MarkSearched`) stands on the default value and a series would thereby wrongly become a film. Reason in the code: the media line is the **only** place where a run deposits the TMDb-corrected kind.

(2) **The access stays cheap:** ONE inquiry via the primary key per item, no network call, no TMDb request in the scan; the media hash comes from the same OSHash cache the seeder asks anyway.

(3) **Otherwise Jellyfin's type, otherwise both endpoints:** without an answer from the plugin database the item type decides (episode/film). If that is unknown too, `null` remains, and **both** endpoints of the family stay — „rather one item too many" (F-M499/F-M509), no narrowing that would lose an item.

(4) **The selection itself stays with `SourceOrder.AppliesTo`** (F-M507/F-M509): better proven is the media kind, not the rule. Verified by T234.

**F-M513 [D]:** **A missing film is created on upload — with a tight duplicate-entry guard, and the new number is looked up instead of guessed.** Concept decision: a film that moviesubtitles.org does not carry is created on upload and the subtitle uploaded for it, and the creation is tested once.

Finding: `MovieSubtitlesClient.BuildAddMovie`/`AddMovieAsync` were **not built** until then („Films are only created when the resolution answers cleanly and the language mapping is proven"). Both are proven now (live evidence: film download 5819, film upload 23716/entry 133347).

Rule: (1) **Payload:** `BuildAddMovie(name, year)` builds `name`, `year`, `go=1` in the order of the form and checks BEFORE sending — an empty name and a year outside 1888..current year+3 are an `ArgumentException` without a request. A one-letter title is permitted. `BuildAddMovieBody` makes the multipart body out of it including the boundary, so that dry run and the real case are **byte-identical**.

(2) **The site's answer is unproven** (no POST was ever made): `AddMovieAsync` does not interpret it. On 2xx it **looks it up** — the same tight search as before (`CheckForAddAsync`, title + year, all spellings) — and takes the number from the hit list. If the follow-up search names nothing, the result is `created-id-unknown` („created, id not readable") **with the text of the answer** and without an id. A non-2xx is `http-error` with `Ok=false` and the status as reason; a rejection invents nothing and does not search either.

(3) **The duplicate-entry guard is tight and decides in ONE search round** (`CheckForAddAsync` → `ResolveForAdd`, `TitleMatches`/`TitleKey`). *same year and exactly one line* → take this number, create nothing (`take`). *title exists with a different (or unknown) year or in more than one line* → **do not** create (`refuse`, the found year and the hit count stand in the result, so that the log line can name them — the user does not want duplicates). *no title hit, but at least one readable answer* → create (`create`). *no readable answer* → `unclear`, likewise **do not** create („does not exist" is unproven). The decision stands in the result object (`MovieSubtitlesAddCheck`), not only in the log.

(4) **The creation sits in the upload path, nowhere else:** `MsubMovieResolver.ResolveOrCreateForMediaAsync` is the only version that may create. `ResolveForMediaAsync` (download path) stays unchanged and never creates. It is called only from `UploadPipeline.SendToMovieSubtitlesAsync`, i.e. only when the upload switch of the film endpoint is on (gate/`SourceAllowed`).

The line reads fixed `[moviesubtitles] '<Titel>' (<Jahr>) neu angelegt — Id <n>`; the new `tvid` goes **immediately** via the same `Persist` method as the resolution into `MediaEntity.MsubMovieId`. The dry run builds the same payload, reports its size (`would-create`, `AddMovieBytes`) and sends nothing.

(5) **The endpoint stays transport and creates nothing** — if it lacks the number, it searches it and refuses with 'film not resolvable on moviesubtitles.org by title and year'. A second creation place would be a second way to a number nobody checked. `ReportBrokenSubtitles` is untouched. Verified by T235.

**F-M515 [D]:** **The answer on `add-movie.php` is read at two live-proven markers — „created" is said only by whoever read the site's sentence, and an id never comes from the answer.** Live measured against moviesubtitles.org — supplement to F-M513, whose point (2) „the answer is unproven" is thereby overtaken.

(a) **The form.** `GET /add-movie.php` answers 200 with **8399 bytes**, `<form action="/add-movie.php" method="post" enctype="multipart/form-data">` and exactly the fields `name` (text), `year` (text, `maxlength=4`), `go` (hidden, `1`), submit `Send`.

(b) **The wrong way.** A POST with `application/x-www-form-urlencoded` and `go=Add movie` gets 200 and **the same form** back — no message, no entry; that is **no** creation, yet looked like one to the plugin.

(c) **The success.** A POST as multipart with `go=1`, `name=The Little Rascals`, `year=1994` answers **200, 8467 bytes**, in the body `<div class="subtitle2"><b>Movie has been added. Thank you.</b></div>` — **no `Location`, no film id, no link**.

(d) **The duplicate case.** The same POST a second time answers **200, 8459 bytes** with `<div class="subtitle2"><b>This Movie already exist</b></div>` (the site writes „exist"). The same sentence came for `Project Hail Mary` (2026), although search, `movies-P.html` and `add.php?filter=` do **not** carry the film — the sentence therefore proves only the refusal, not the findability.

(e) **After the creation.** The search (`POST search.php`, `q=Little Rascals`) found **0** hits, `movies-L.html` stayed byte-identical (478353 bytes, no „Rascal"), `add.php?filter=Rascals` delivered **0** lines, the statistics stayed at `Movies: 23368`. The newest film page `movie-26230.html` carries the year `(1994)` with an **empty name**, `movie-26231.html` and following do not exist — the id is thereby **not proven by title**, and none is claimed.

Rule: (1) **The request is multipart/form-data with `go=1`** (`BuildAddMovieBody`), byte-identical in the dry run.

(2) **Classification runs at exactly two markers** (`ClassifyAddMovieAnswer`): `Movie has been added` → created, `This Movie already exist` → refused; **everything else** — including the unchanged form — is `rejected` with `Ok=false` and the text excerpt as reason. There is no third proven sentence, so none is invented either.

(3) **`Ok=true` only with the success marker**; a 2xx alone is no creation any more.

(4) **„already exists" leads to exactly ONE follow-up search** (`CheckForAddAsync`, title + year, all spellings), **never to a second POST and never to an error**. If the search names the film, its number is the result (`exists`); if it does not name it, the result is `exists-id-not-readable` — no `tvid`, no upload.

(5) **The id always comes from the follow-up search**, never from the answer (which names none): `created` resp. `created-id-unknown`. In the resolver the branches `MsubMovieStatus.ExistsFound` (number stored, nothing created) and `ExistsWithoutId` (nothing stored, no upload) are visible, and the log lines name the site's sentence word for word.

(6) **The finding stays reported as open:** that a created film is not findable via search/letter list/filter is a property of the site, not of the client — the client therefore says „created, id not readable" and uploads nothing. Verified by T237.

**F-M516 [D]:** **A missing series is created with the site's form, the answer is read at ONE proven marker, and the series number comes from the site's search — not from the answer and not from „highest id + 1".** Concept decision: the creation of a missing series runs in the plugin, with proven answer evaluation and without a guessed number.

Starting point: the path was built (`TvstShowLookup.cs` → `TvSubtitlesClient.BuildAddShow`/`AddShowAsync`), but **never ran** — no creation entry in the log of the test instance or in prod. The three known series numbers (4238 *Babylon Berlin*, 4239 *The Thundermans*, 4240 *Cunk on Earth*) came from a search, not from a creation.

Finding, exclusively reading plus **exactly one** real creation: (a) **The form** (`GET /add-tvshow.php`, 13767 bytes) reads `<form action="/add-tvshow1.php" method="post" enctype="multipart/form-data">` and stands — like the upload form — in a broken nesting (`<table><form …>`). It has **exactly four fields**: `name` (text), `year_s` (text, `maxlength=4`), `seasons` (text, `maxlength=2`), `go` (hidden, `1`, behind the submit line, still inside the form). The submit button `Send` carries **no `name`** and therefore does not go along. No captcha, no token, no account, no comment field, no external identifier — the name is the only key.

(b) **Our client's body was already correct in form** (multipart, four fields in form order, Referer `/add-tvshow.php`), but **unchecked**: `AddShowAsync` returned only `Status`/`Text`, the resolver demanded `Status is 200..299` and would have read the real answer as an error.

(c) **The answer, measured on a real creation** (`Weissensee`, 2010, 4 seasons; absence proven beforehand by search **and** by the complete series list — 0 hits among 2.772 entries, no spelling variant): `POST /add-tvshow1.php` → **HTTP 302**, body empty, `Location: https://www.tvsubtitles.net/add-tvshow.php?done=yes`. This page prints `<div class="subtitle2"><b>TV-Show has been added. Thank you.</b></div>`. **No id, in no form.** The „TV Shows" counter of the sidebar stayed at **2768** (before and after the creation) — reading a creation off the counter is no evidence.

**„Already exists" and a refusal are NOT proven:** a second POST would either have produced a duplicate entry (a creation cannot be deleted on the site) or proven nothing.

(d) **The id** stood immediately afterwards in the site search (`GET /search2.php?qs=Weissensee` → `<a href="/tvshow-4242.html">Weissensee (2010-2026)</a>`) and in the select field of `/add.html` (`4242` → `Weissensee`). `/tvshows.html` did **not yet** carry it (2.772 lines before and after the creation) and is no evidence. The season count was taken over: `/tvshow-4242.html` answers 302 to `tvshow-4242-4.html`, title „… season 4", season links 1–4.

Rule: (1) **`BuildAddShow`** builds exactly the four fields of the form in its order (trimmed), `seasons` 1..22; a field the site does not have is not invented. **`AddShowAsync`** sends them as `multipart/form-data` to `/add-tvshow1.php` with Referer `/add-tvshow.php` — not URL-encoded (the error the film site has just shown). The **dry run** builds the same body, reports its size and sends nothing (check: same byte count as the real case).

(2) **Classification runs at exactly one marker** (`ClassifyAddShow`): `created` only when the target URL or the text names `done=yes` resp. „TV-Show has been added.". An error status (≥400) is `refused`; **everything else — including a 2xx with the unchanged form — is `unclear`** and is never output as a creation. There is no proven sentence for „already exists", so none is invented or guessed either.

(3) **The number comes from the search, then from the list** (`TvSubtitlesClient.SearchShowsAsync`/`ParseShowSearch`, fallback `ShowsAsync`), and both run through the same **tight** comparison (`ResolveShow`, F-M501). The site search is **soft** — measured: `Pagan Peak` delivers *Twin Peaks* and *Peaky Blinders* —, its result alone is no proof, and „no hit" means nothing there. The year range (`… (2010-2026)`) is cut off when parsing, otherwise the tight comparison would never find the name again.

(4) **The resolver decides on the evidence, not on the wording** (`TvstShowLookup.SettleStatus`). Series found **and** confirmed → `created`. Found **without** confirmation → `existed` (it stood there already, the number is stored, **no** second entry). Not found **and** confirmed → `created-id-unknown` (**nothing** is stored, honest log line). Not found **and** refused → `refused`; otherwise `unclear`. **Only** a number really seen is stored (`MediaEntity.TvstShowId`), at most one creation attempt per medium and run, none in the dry run.

(5) **None of this blocks the chain:** every outcome is a result, not a throw; without a number tvst is skipped for this medium and the other source gets its attempt. Verified by T238.

**F-M606 [U]:** **First ask whether the identifier is usable — then send.** Concept decision: whether the identifier is usable is asked before the upload, and nothing is sent when it is not usable. Measured on "The End Of Oak Street": the film does not stand at moviesubtitles, `add-movie.php` nevertheless answers `200`, the identifier is not readable — the attempt ended in a SKIP without proof. Now the film address fetches `add.php?let=<tvid>` before sending and checks whether the upload form stands there (language selection, `tvid`, film reference). If it is missing, **nothing** is sent and the reason `film-source-id-not-usable` is remembered (F-M605). If the page does not answer at all, that counts **not** as "not usable" — then it is sent as before and the result of the chain decides. Checked by T316.

**F-M607 [U]:** **Create → search → if the title does not appear, the pair is closed.** Concept decision: the title is created anew first, then searched; if it does not appear, the pair is skipped and closed. The order of the film address is thereby fixed: first it is created (`add-movie.php`), then searched (the search in the same call, `AddMovieAsync` reads the identifier from it). If the search names no identifier, the pair is decided and is not repeated. That holds for the two cases in which the page proves that the title stands there (`created-id-not-readable`, `exists-id-not-readable`), with a mark (F-M603), and for all others (`not-found`, `exists-other-year`, `create-failed`, `unclear`, `no-title`, `no-year`) as a remembered substantive No (F-M605). **No case claims an upload thereby.** Checked by T317.

**F-M608 [U]:** **Title ladder: create, search, 2 s / 4 s / 8 s — on the fourth failure the title is permanently closed.** Concept decision: create, search, then fail 1 → 2 s, fail 2 → 4 s, fail 3 → 8 s, fail 4 → permanently close all pairs of the movie, of the series; tvst and mst only. Affected are exactly the two addresses that must **create** titles — subdl submits and done, there is no such step there.

Four moves per title, in between 2 s, 4 s and 8 s, because the page sometimes needs a moment after creating until the search finds the title. Before each move the memo of the run is cleared (otherwise the resolver answers with the remembered No and the move would be pointless). After the fourth failure a **title lock** lies in the database (`title_failures`, key `quelle|imdb-oder-tmdb-kennung`): all pairs of this film or this series are thereby decided and are no longer asked. A successful move deletes the count. In the dry run there is only one move — pauses without effect would be only waiting time.

**Addendum (measurement):** the ladder initially ran again **per file**. For "tt14129116" four times 2+4+8 s, hence 14 s per track, because the database did not see the lock in the same run (the writes lie in the bracket from F-M602 until the run end). Now the run keeps a memo list (`_titelGeschlossen`): a title that was closed in this run is skipped immediately for every further track. Checked by T318.

**F-M609 [U]:** **Three verdicts instead of "everything that did not work".** Concept decision: not everything that fails acceptance is a rejection; a connection fault or no answer is something other than no positive verdict. Every attempt now ends in exactly one of four cases. **Accepted**: 2xx at tvst/mst, `status:true` at SubDL. **AcceptedWithoutId** (F-M603): the source has taken, but names no identifier. **Refused**: there is a decision — every answer other than 2xx with the exception of 429 and 5xx, **or** one of our pre-checks without a request (season outside the form, no TMDb entry, identifier not usable). **NoVerdict**: nobody decided — timeout, connection fault, 429, 5xx, SubDL hourly cap, `rate-limited`, abort by us.

**Only Refused closes the pair**; NoVerdict writes nothing and is asked anew in the next run — otherwise the IP block (21:25–22:41, 76 minutes) would have become the permanent verdict for every subtitle requested in that time. The whitelist from F-M605 is thereby gone: remembered is by **verdict** (`ShouldRemember(reason, verdict)`), not by a hand-kept list of word beginnings. Exactly on that "season must be 1..22" fell through, which is why Grey's Anatomy S23 cost four failures including ffmpeg extraction in every run.

The verdict is **different per source**, because the sources answer differently: SubDL delivers JSON (the hourly cap comes as HTTP 200 with `status:false`!), tvst and mst deliver only an HTTP status. The header line separates the cases: `… | N rejected, N refused, N without answer, N stream(s) skipped`; in the log two separate lines stand for that (`REFUSED …` and `NO ANSWER …`). Checked by T319.

**F-M610 [U]:** **The ladder only closes what it can prove.** Concept decision: the ladder closes only what it can prove. Two signs of overblocking stood: **(1)** `MitTitelLeiterAsync` closed after the fourth failure **regardless of the reason**, hence also after a timeout or a server fault — in contradiction to F-M609, which forbids exactly that for the pairs. **(2)** `created-id-not-readable` and `exists-id-not-readable` are a **proof** ("the page carries the title, only the identifier is not readable") and **not a No** — yet afterwards the whole title lay in the lock forever, with all its tracks. Measured: **12 titles**, 57 lines `exists-id-not-readable`, 29 lines `exists-other-year`. Now this holds: a proof counts as a success of the ladder and the pair is closed via F-M603, the title stays open. `unclear` does not count as a failure and closes nothing (answer `NoVerdict`, the next run asks anew); closed is only a substantive No (`not-found` after creating, `exists-other-year`, `create-failed`, `refused`). Existing lock lines that rest on a proof are **discarded** on read and removed from the database — no clean-up run needed. Checked by T320.

**F-M605 [U]:** **A source's No is remembered.** Concept decision: the plugin database remembers a source's substantive No, also where the title is created anew at tvst. F-M603 closed only the case "created, identifier not readable". Every other state of an address — at tvsubtitles `would-create`, `create-already-tried`, `refused`, `unclear`, `no-title`, `no-year`, at moviesubtitles the corresponding film states — still ended as SKIP **without** a mark, the pair stayed open and the file returned in every run. Now the plugin database remembers the substantive No per **track, source and content** (`source_refusals`, key `mediaHash|subPos|langTag|quelle`, signature = content hash): the same track is not asked again for the same text, a different text (or a newly ripped medium) asks again. **Not** remembered are temporary Nos (rate limit, timeout, abort) — they may and must be asked again in the next run; if a source has taken the content, an old No is forgotten. Checked by T315.

**F-M517 [D]:** **A found opposite-site number goes into the plugin database even when no media hash is known — the same column is written, only the line is found via its path.** Evidence (real run at 04:13, jf-test, `jf-run.log`): `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — nicht gespeichert: kein Medien-Hash bekannt — die Nummer gilt nur für diesen Lauf.` The upload passed afterwards, the number was gone.

Cause, in this order: (1) the media hash comes from `ContentHashRegistry.GetMediaHash` (Registry/ContentHashRegistry.cs, „Media hash for a file: cache first … computed and stored on a miss"), and the method answers **`null`** as soon as the file is not readable. Measured, the path `media/movies-test/Hotel Transylvania 2 (2015)/Hotel Transylvania 2 (2015).mkv` is a **symlink without a target** (`readlink -f` → `<library>//…ShAaNiG.mkv`, `test -e` → not present), so there is neither an OSHash entry nor a freshly computed hash.

(2) `MsubMovieResolver.Persist` returned **silently** on empty `mediaHash` („without a medium there is no line"), although `MediaEntity` is identified via `Id` **and** `Path` and the line was unambiguously addressable via its path. (3) The log line read only the hash and therefore reported „not stored". The same gap hit the **series number**: the write path of the plugin database had the same early exit and did not write `TvstShowId` in the same case.

Rule: (1) **One write path, two addresses.** `ContentHashRegistry.SetMsubMovieId`/`SetTvstShowId` (jointly via `SetMediaNumber`) write with an existing hash as before (line via `Id`, path is written along), otherwise into **exactly one** line that carries this `Path` — **the same column**, no new field, no second storage path.

(2) **No guessing.** No hash **and** no path line, or **several** lines with the same path → `MediaNumberWrite.None`: nothing is written and no line is chosen; the log line then says so too.

(3) **The number must also be readable back.** Without a hash, `MsubMovieIdAtPath`/`TvstShowIdAtPath` read the same line (only on uniqueness). Otherwise a number stored via the path would be invisible to every subsequent run and would be searched anew; the run's memo is accordingly the path, no longer only the title.

(4) **Both resolvers and both paths.** `ResolveForMediaAsync`/`ResolveOrCreateForMediaAsync` (film) and the path that searches and creates the series (series) take the path of the media file as parameter; `UploadPipeline` (film and series endpoint) and `DownloadPipeline` pass it along.

(5) **The log line says HOW**. One text for both addresses (`Api/Endpoints/MediaNumberNote`): „stored (via the media hash).", „stored via the path (no media hash known)." or „not stored: no media hash and no unambiguous path line in the plugin database — the number holds only for this run."

Evidence on the image of the live plugin database (copy, only the copy written): 31 lines, **0** lines with the film's path → `None`, 31 → 31. Line present, hash not → `ByPath`, `MsubMovieId = 13392`, 32 → 32 (the +1 came from creating the line for the evidence). The same write again → 32 → 32; read via path and hash 13392 each. Honestly: for *this* film it stays at „not stored" on the test instance as long as no line with its path exists — the symlink points into the void, so no hash arises either. The fix works where the line is there and only the hash is missing. Verified by T239 and T240.

## 10. Scheduler, Quota and Timing

**F-M20 [B1]:** Configurable pacing — a transfer rate (**default 400/h**) and a pause between API calls (**0.1–10 s, default 0.5 s**) — **and no local call limit of any kind**. A configured pause replaces the rate-derived one; the two are not compared. The range is the server's own fastest allowed rate at the floor (SubDL allows 600 req/min = 0.1 s) and a slow, polite pace at the top. It never permits an unpaused burst. The GUI range and the limiter clamp are identical (0.1–10), so a value entered in the page is the value the limiter uses. The rate is clamped to **1–2000**. **The plugin enforces no quota of its own.** No code path counts API calls and no run is stopped for making "too many" calls. **Only SubDL stops a run**: on a real HTTP 429 the server's own counters are read and the quota decision decides between a short respacing and a day-long stop (F-M238). The limiter now only spaces calls; it cannot refuse one.

**F-M288:** **A LOCAL deferral is reported under the Workers list, in plain text — the quota bars stay a pure server reading.**

The line sits under the Workers list, in the page's normal text colour, and is not part of the quota box. The bars keep the 75 %/95 % rule unchanged, and the two facts stand side by side instead of over each other.

**The cause is passed through, never guessed.** `deferred` covers three unrelated situations: the run lock being busy, our own quota/rate cap, and a user stop. The line carries the direction's own stored wording ("download quota/rate limit — rescheduled", "cycle already active", "run lock busy — rescheduled") instead of a label invented in the page.

**The line answers "why is this direction idle", so it is gated on the direction being STOPPED — a pending fire alone is not enough** (reworked). **A deferral whose fire is gone is not shown either**, because when the scheduler holds no fire the task has already moved on. The time the line names is that fire, in the viewer's zone.

**F-M272:** **The page has THREE refresh cadences, and none of them is "10 s for everything".**

**The SubDL/TMDb lights, the login light and the library read/write rows** have **NO interval at all** — they run at page init, after Save, and on the Refresh button only.

**Every refreshed endpoint carries a cache-buster** (a cache-buster), so no layer can serve a stale body.

**F-M238:** **A 429 is not a verdict — the COUNTERS decide whether the allowance is spent.**

Every 429 is classified: rate headers → the daily allowance, exact reset from the header; body `service_busy` → transient overload; body `rate_limit` with a retry hint → a short-term trip, waited out like the overload. Only when the counters say the allowance is spent does the run stop for the day.

Counters come from the settings page's source — the quota inquiry (search/downloads) — read only after a 429. Each direction names its own. An unreadable quota is not free: a failed or limit-less read leaves the 429 a stop.

A next-day fire anchors on the server's own `reset_at`, never a local midnight guess; `Continue after daily limit` gates that path per direction. The respacing offset is the job spacing (5–120 min, default 15; clamped 5–120). Every day-long stop ends in a fire at the reset plus the 30–300 min jitter; only the toggle off, or an arrival run holding the slot, stops without a fire.

A dry run reports the hearing-impaired choice (`DRY-RUN HI … (n/m candidates are HI)`).

**F-M26 [B2]:** **Random jitter in the rate limit — on the transfer rhythm, not on bare calls.**

The pause between two real transfers (upload→upload, download→download) is the base interval ±30 % (the transfer pause).

The pause between two API calls carrying no transfer (empty search, skip, metadata lookup) is **deterministic** (the bare-call pause): exactly the configured minimum pause when configured, otherwise `3600/rate`.

The band is base ±30 %, floor 1 s. With the default rate of 400/h that is 6.3–11.7 s.

A failed or skipped candidate gets **no** transfer pause — nothing was transferred — and stays on the bare-call pause (F-M26a).

The "derive from the rate" sentinel `-1` is **not** clamped; only a real value is clamped.

**F-M49 [D]:** **Daily-limit resume per direction:** two independent checkboxes, "Continue after daily limit" (Download) and "Continue after daily limit" (Upload). **Both default ON.** ON → wait once until reset (max 24 h) and retry; OFF → clean stop.

**F-M338 [D]:** **The two directions read the same sentence.**„In upload gui: continue after daily limit (without api)"*. The upload control carried a word the download control does not — *"Continue after daily **API** limit"* against *"Continue after daily limit"* — and that difference is misleading rather than merely redundant. The two switches do the same thing in their own direction (F-M49), so different wording reads as different behaviour. The same fault was corrected on the page texts before (*"the three maintenance menus read just Manual"*), which is why the parity is asserted as a **pair of labels** and not as the absence of one word.

**F-M51:** **Per-installation random schedule anchors:** all scheduled fires come from a background coordinator (30-s tick). Daily, weekly and monthly anchors are diced once per installation and persisted in config, and every maintenance job carries its own weekly anchor. No default fixed-time triggers. `Manual` disables the scheduled pipeline fires.

**F-M65:** **One-shot night recovery fire per direction:** a run stopped on the daily limit schedules one re-fire after the quota reset plus a random 30–300 min jitter (away from the 00:00 UTC herd), diced freshly per fire by cryptographic RNG — see F-M182.

**F-M351 [D]:**a stop cancels the pending fire of the direction it names.** The stop call now calls `ScribeSchedulerCoordinator.CancelPendingFire(direction)` in addition to writing the marker and aborting the running cycle. That direction's one-shot fire is nulled, its reschedule counter is reset (the fire was removed by hand, not exhausted by the budget), and the fire list is rewritten so a restart cannot resurrect it (F-M116). The other direction keeps its fire; `all` cancels both — the page's single button ("\u25a0 Stop all uploads & downloads") issues both calls. The cancelled moment is logged in UTC, unconverted, like the deferred fire's retry moment.

**A stop does not block the regular anchor** (operator decision): the direction comes back at its next anchor, and the manual task button clears the marker and runs. Reason: no path in the fire ladder read the stop marker, and both pipelines clear the markers at run start, so the marker was no backstop either. See F-M65, F-M26b, F-M116.

**F-M182:** **Jittered recovery fire after the daily limit:** when a pipeline stops on the daily quota, the direction is re-fired once after the server-reported reset plus a random 30–300 minutes. The offset is diced freshly per fire (cryptographic RNG) and rolled independently per direction, so the retry does not land on the 00:00 UTC reset moment every API consumer hits. The jitter applies to the **quota-anchor path only**; fires carrying their own offset (run-lock retry, rate-limit respacing) keep their exact time. Log lines name the reset anchor, the resulting fire time in the local zone, and the jitter window applied.

**F-M116:** **Scheduler state persisted:** the next scheduled download/upload fire times, the anchor markers, the catch-up flag and the lock-busy deferrals are persisted and restored on restart.

**F-M248:** **The scheduler's whole due-state is persisted, and a missed anchor slot is caught up instead of waiting for the next window.**

Every dedup marker, the catch-up flag and the lock-busy deferrals survive a restart. A slot that already fired stays consumed; a slot whose window was missed fires on the first tick where it is past-due and unconsumed. No fire carries a time window.

The marker counts the CLAIM, not the outcome: a run that fails after being queued stays consumed for the day, so persistence cannot turn a failure into an infinite retry.

**No fire window.** The refetch slot carried `if (now > fire.Effective.AddSeconds(2 * TickSeconds)) continue;` — a 60-s window that made a missed anchor wait for the NEXT regular window, contradicting F-M156. It is removed: a past-due, unconsumed slot fires on the next tick, and stacking is prevented by the persisted marker.

**F-M54:** **Reaction matrix per server response:** 403 auth → immediate stop. 429 → classified by variant (F-M238) and decided against the live counters — a spent allowance follows F-M49/F-M182 (wait-until-reset or clean stop per the direction's setting), a short-term `service_busy`/`rate_limit` trip is respaced by the job spacing. 5xx → retry with backoff. 200 + `status:false` → warn, fail-open for searches.

**F-M204:** **A running cycle is never widened and a trigger is never merged into it.** Any overlap between scheduled upload and download is answered by a **reschedule**: `ScheduleRecoveryFireAt(direction, now + JobSpacingMinutes)` for the direction not covered by the running cycle. The dispatcher logs `rescheduled +N min — cycle already running` and returns `false`. When the fire comes due, the coordinator's tick re-checks the lock check and defers again if still busy. The per-direction latch exists for the fresh-cycle case only.

**F-M67:** **A service overload gets a short recovery window.** A temporarily busy service ends the run cleanly and schedules a nearby fire; it is not a quota problem.

**F-M70:** **The page shows the account's quota without handing out the key.** The inquiry runs server-side, is briefly cached, and is discarded when the key changes.

**F-M72:** **The upload's hourly cap is not an error.** When SubDL reports the limit, the item stays due, the run ends, and a later run continues with a fresh hourly budget; the mark is reset at run start.

**F-M81z:** **A run ended by the daily quota is a clean stop, not a failure.** Per direction the open fire is stored and can be queried.

**F-M117:** **A fire that hits a running run is deferred.** The new fire time stands in the log; the mark stays unset so the schedule tries it again.

**F-M146:** **When the global run lock skips a direction, that direction reschedules itself.** The new fire time stands in the log as a warning.

**F-M149:** **In the first round the download goes before the upload.**
**F-M593 [U]:** **The transfer rhythm is adjustable from 50 to 500, default 450 — and the page says why no more.** Until now 100–500 with default 400 applied (F-M20). Concept decision: the rate limit is adjustable from 100 to 650 with default 450, later from 60 to 600, with a GUI note that SubDL allows 500 uploads or downloads per hour.

Pulled along with F-M594 („The adjustable limit must then go back to 50 to 500"), because the slot makes the pacing exact and 500/h is the limit SubDL itself enforces. New: the bound in the input field (`min="50"`, `max="500"`), the label „Transfer rate per hour (50–500)", the check on save (`Math.min(500, Math.max(50, …))`, fallback 450) and the default in the configuration model (`UploadsPerHour = 450`). The GUI note on SubDL's 500/h demanded first — „Pick a value below is stupid" → „SubDL temporarily blocks any prolonged upload/downloads at a rate above 500 per hour." — is **removed** again. Concept decision: the SubDL rate-limit note in the GUI description goes away again. The description names only pacing and default, because the server's limit now already sits in the field's upper bound (500). Verified by T303.

**F-M594 [U]:** **The pacing is a slot BEFORE the transfer, counted from the end of the last real transfer — not a pause after it.** Concept decision: the time since the end of the last download or upload is measured and the pause adjusted exactly, so that the rate-limit pause takes place exactly before the upload or download that actually occurs. Finding before that: after a transfer, `TransferPauseMs()` was waited out **blindly** (upload) or `TransferPauseMsLessFit()` (download); extraction, gate checks and preparation of the next track came **on top**. Measured at a configured 600/h: 6 s pacing, actually **8.2 s per track** (around 440/h by pacing, 276/h by wall clock).

New: `GlobalRateLimiter` holds a **process-wide time anchor** (`_lastTransferDoneUtc`) with `WaitForTransferSlotAsync` and `MarkTransferDone`. Before the transfer of a track, `ziel = anker + Intervall (3600/Rate, ±30 %)` is computed and only the **difference** is waited; if the target lies in the past, nothing is waited. Afterwards the track sets the anchor — **only** if it really moved bytes (upload: at least one source accepted; download: `rawAttempt.Bytes.Length >= 100`), so a skip or failure track does not penalise the rhythm (F-M547/F-M589). One slot per **track**, not per source, otherwise the rate would halve with two sources (F-M587).

The anchor is static because upload and download share **one** field — otherwise each direction would pay its own pacing. The old post-pauses are removed (upload `Task.Delay(_limiter.TransferPauseMs())`, download both `TransferPauseMsLessFit()`); `TransferPauseMsLessFit` stays as a method but is no longer called by the run — the credit for the audio alignment (F-M310) is contained in the slot, because the audio alignment lies between two transfers. Verified by T304.

## 11. Content Registry and Identity

**F-M17z [B2]:** **Upload dedup respects the stored outcome:** a stored verdict — uploaded, or rejected with any reason — counts as "known"; an observation row does not.

**F-M298 [D]:** **A `duplicate-content` skip never overwrites an `uploaded` row — the state follows the CONTENT, not the reason.**

The content-known check deliberately counts a **rejected** row as known just as much as an uploaded one, so a skip raised on that answer fires routinely over content that is **demonstrably up** — most visibly after a pack tag write. There the language-tag gate gives the file a NEW media hash and the identity move carries the accepted rows across (F-M61).

The rule: when a skip carries the reason `duplicate-content` **and** the same content hash already holds an `uploaded` row anywhere — embedded or loose subtitle, under this media hash or under another — the row is written as `uploaded` (reason cleared). The stream is **not** counted as rejected. In every other case the reject is recorded exactly as before, so a skip over content that is genuinely NOT up still says so.

The decision lives in ONE registry method that every skip path calls — the embedded reject-replay, the loose subtitle reject-replay, the phase-1 QA gate and the phase-3 upload skip.

**F-M243 [D]:** **The HI variant counts as present whether it is a file or an embedded track.** Both evidence sources answer the same question, and Jellyfin's own the hearing-impaired flag decides a stream — read through ONE detector (the hearing-impaired predicate) used by uploader and downloader alike.

A target language counts as having its HI variant when EITHER `<base>.<lang>.sdh.srt` exists on disk OR the item carries an embedded subtitle stream of that language whose the hearing-impaired flag is true. Only when both are absent is the language queued for an HI download.

The detector reads the flag first and falls back to the stream title (`sdh`, `hearing impaired`) — the same two sources F-M71 uses on the upload side, so both directions agree on what HI means. A title-only rule misses flagged-but-untitled streams; a flag-only rule misses libraries where MediaElch/TinyMediaManager set the title alone.

The verdict is stored once and read from there (F-M254). The detector still runs on the upload side, where the stream is in hand; the download side does not re-derive it.

**F-M254 [D]:** **The HI switch is answered from the registry, not from the streams.** Whether a target language still needs its hearing-impaired variant is read from the stored verdict (the hearing-impaired rows), which joins the embedded rows the uploader wrote (the embedded mark) and the loose subtitle rows the downloader wrote (the download mark). Jellyfin's live stream list and the on-disk file names are not consulted.

A language counts as having its HI variant when the registry holds a hearing-impaired row for it — embedded or loose subtitle. Only when no such row exists is it queued. The seeder's queue gate and both download-pipeline branches read the same answer.

The marker list gains `cc`: closed captions serve the same audience as SDH and carry the same non-dialogue cues (`[ BEEPING ]`, `[ GRUNTING ]`, musical notes). Matched on a word boundary (`(?<![a-z])cc(?![a-z])`), never as a substring — `cc` sits inside real language names, and reading that as hearing-impaired would repeat the `hi`/Hindi mistake.

**F-M256 [D]:** **A registry lookup takes the media PATH and resolves the hash itself.** the hearing-impaired reader receives the media file path, resolves the media hash internally (the media-hash lookup), then reads the embedded and loose subtitle rows. No caller passes a hash for this question.

The resolution lives inside the method, once.

 The reader finds a stored HI row for a media path and finds nothing for the same call with the hash.

**F-M257 [D]:** **The embedded area is written by whoever reads the streams, not only by the uploader.**

The seeder's scan and the download pipeline record each embedded, non-forced, mappable subtitle track as an observation row (position, language, hearing-impaired) carrying `Status = observed` and no verdict. The uploader keeps writing verdicts (`uploaded`/`rejected`) on the same key, and an observation never overwrites them.

The position is the index into the UNFILTERED subtitle list: ffmpeg's `0:s:N` counts bitmap and forced tracks as well (F-M246), so a position taken from the filtered list names a different stream.

A failure to observe is swallowed and logged at Debug. An observation is an improvement, never a precondition: a scan whose streams cannot be read still queues its items normally.

**F-M246:** **A forced subtitle track does not make its language present — a hearing-impaired track does.**

When the download side asks which target languages are covered (the coverage question), an embedded subtitle stream counts as evidence only when Jellyfin's forced flag is NOT true. A forced track carries only the lines of foreign-language scenes, so counting it as coverage leaves the language without a real subtitle and the item marked complete.

A hearing-impaired track DOES count: SDH is the same dialogue with annotations. A bitmap track does NOT (F-M257). The rule is "any non-forced TEXT subtitle stream", and the HI switch is answered separately from the registry (F-M254).

**F-M367:** **`forced` is read from the track TITLE as well as from the flag** — a track titled `Forced` with `DISPOSITION:forced=0` is forced. Matched on a word boundary, so `enforced` is no marker.

**F-M368:** **A deferral re-arms its job, and deadlines live in one clock.** The anchor blocks folded the deadline into "due" (`… || now < until`) and tested it again in the same `if` (`now >= until`). The two cancel each other once the day marker has been consumed by the earlier fire. A run that deferred itself on a busy lock never came back (measured: refresh deferred 16:25 with "re-fire scheduled at 15:40 UTC", no refresh line for the rest of the day).

Deadlines are now written and compared in UTC only — the tasks passed UTC while the anchor blocks passed local time and every comparison used local — anchors stay local, and the parser normalises a pre-fix state file. The decision lies in the anchor check.

The refresh also carried a SECOND reschedule budget ("dbrefresh") that the fire never reset, so after 16 task-side deferrals the arming call refused silently. One slot per job now. The refetch slot printed a retry time and stored nothing: it re-probed every 30-s tick, burnt its 16× budget in about eight minutes and then went silent. The spacing is a stored UTC deadline now (gated, cleared on fire, persisted).

Third: an already elapsed deadline is not reported as an open one — the row read "defer, next attempt Wed 4:40 PM" six hours after that moment while the run had finished green. A task on Jellyfin's own trigger never tells the coordinator.

The upload side follows the same rule: a forced track is removed from the upload set, through the same predicate the pipeline's collector and the seeder's upload-todo prefilter use.

**F-M369:** **One download unit per archive, not per extraction.** An archive is fetched once and its body is kept for later calls, keyed by the archive segment of its download URL, so the same pack yields every episode without a second quota unit. Until the body lived in a run-scoped memory cache in front of the disk cache; F-M386 removed that layer.

**F-M370:** **Packs are cached across runs; single files are not.** Only a ZIP body enters a cache. A cached archive that does not contain the entry a call wants is fetched again, and the call is not rejected. On disk the plugin keeps archives for a configurable number of days (default 14) in its data folder, bounded by a configurable ceiling (default 512 MB, oldest first) and trimmed at the start of every run; 0 in either setting switches the disk cache off. A hit on disk restarts the retention. The run summary reports the number of archive hits and how many of them came from disk; both settings sit in the Download tab.
**F-M386 [D]:** **There is ONE archive cache, and it is the disk cache.** The run-scoped layer (F-M369) is removed, not merely bypassed: a pack is fetched once, written to disk, and every further episode of it — in this run and on the following days — is a read from that one entry. Two layers meant two code paths for the same rule, and the second number of the run summary (`N archive hit(s) (M from disk)`) was 0 on every measured run; the summary now reports a single count.

The consequence is deliberate: with `days = 0` or `megabytes = 0` nothing is cached at all, not even within a run. The cap per archive is its own constant (16 MB). 

**F-M372:** **A season pack is earned, never assumed.** SubDL builds the pack itself: ein Einzelaufruf je episode, dann ein Aufruf mit the array `file_n_ids`, `is_full_season=true`, the episode range in `ef`/`ee`, and a `name` that becomes the zip name on download. The switch sends a season that way only when the season is complete — the episodes on disk are exactly the episodes that aired, asked at IMDb with TMDB as the fallback — and every episode passed the gates the single upload passes. Incomplete seasons, a failed gate, mixed sources, loose subtitles and forced tracks stay single uploads. The switch sits directly above the upload gates and is on by default. One pack is one language, one HI flag and one source. Its name is `{imdb}.S{ss}.{pack hash}.{LANG}`, the pack hash being the MD5 over the episodes' content hashes in episode order, and every episode of the pack carries the pack's id in its own registry row.

**F-M373:** **A pack needs the whole season in the same run.** The run plans its packable seasons before the first item: a season is packable only when the episodes queued for this run are exactly the episodes that aired — an episode that is already up individually cannot join a pack. A season that is only partly queued is uploaded as singles immediately instead of waiting for the end of the run. The plan is worked first and consecutively, per series then per season then per episode, so a season closes in one go and no buffer outlives it; queued work outside the plan keeps the order the queue gave it.

**F-M374:** **A pack carries season release names, and the first one titles it.** SubDL lists a full-season upload under the first element of `releases` and ignores the `name` field, and the packs of other uploaders carry exactly that: one season release (`Foundation.2021.S03.COMPLETE.ATVP.WEB-DL…`, `Game of Thrones, SEASON 8 4K x265`), never the episode releases. This plugin therefore reduces each episode's release name to its season form (`…S01E05…` → `…S01…`, `3x04` → `S03`) and names that release only when **every** episode of the season reduces to the same one. Release names are never mixed: episodes from different groups, and release names without an episode marker, name no release at all. The neutral label (`<series> S<nn> - complete season [(SDH)] [<lang>]`, IMDb id standing in for an unknown series name) takes its place, so the pack never claims a source it does not have.

**F-M378:** **A pack's members name their season and episode.** SubDL reads both out of each member's own file name and stores what it finds. `S01E05 - Upon Awakening.en.srt` becomes (season 1, episode 5), a member named `01 Upon Awakening.en.srt` becomes (season 0, episode 1) and every episode of the pack is listed under season zero (`S00E01` …). Pack members are therefore named `S<season>E<episode> - <episode title>.<lang>.srt`; the pack's own `season` field is not enough to place them.

**F-M377:** **A pack states no episode range.** SubDL files a full-season upload by `ef`/`ee` when those fields arrive: the entry comes back as `episode: 1, episode_from: 1, episode_end: 10, full_season: false` — one episode with a range note, which is not a season pack. A real full-season entry carries `episode: null, episode_from: null, episode_end: 0, full_season: true`. The file set is the range, so the pack upload sends `is_full_season=true` and `season` and nothing else about episodes.

**F-M376:** **When no file name carries a release, the file's own facts name the season.** Resolution and codec come from the video stream, the source from the documented quality code, the series name from Jellyfin (the IMDb id when Jellyfin knows none), assembled as `<Series>.S<nn>.<res>.<source>.<codec>` with unknown parts simply left out. No group is appended unless a file name carries one, and when the file says nothing at all the neutral label stands in — the pack never invents a source.

The plan names each season it takes and each season it leaves behind, and a dry run names the pack it would send instead of the episodes — the plan is part of what the run decides.
**F-M371:** **Norwegian is one language.** SubDL offers no split between Bokmål and Nynorsk, so a track tagged `nob`, `nno`, `nb` or `nn` counts as `NO` present — a file that already carries one written form is not given a second Norwegian subtitle.

**F-M240 [B1]:** **The hearing-impaired variant is its own subtitle datum, and completeness is derived rather than marked.**

It answered the HI question with LANGUAGES and carried the variant as a `:hi` token inside the download mark's language list. Both halves are withdrawn: the token with the vocabulary that could not express the datum (14.2a), the mark with the duplicated answer (14.2b). The pipeline trusted a stored mark while the seeder queued from the disk and the refresh judged from the stored rows, so one file could be simultaneously complete, due and stale. Every component asks the same reader (the subtitle coverage), so a vanished `.sdh.srt` is simply an open pair.

**F-M193a:** **No construct the database cannot translate may cross into a query.** A string-comparison overload, a helper-method call or any predicate without a database expression must be applied **outside** the query lambda: keep the indexed equality inside and move the rest to LINQ-to-objects afterwards.

 ```
 // wrong — the ItemId index stays unused and the query fails at execution
 Find(x => x.ItemId == itemId && string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
 // right
 Find(x => x.ItemId == itemId).Where(x => string.Equals(x.Language, language, StringComparison.OrdinalIgnoreCase))
 ```
**F-M194b:** every content area is keyed by a **computed business key stored in the record's own `_id`**, never by a database-assigned auto id. The counters area and the pipeline-run area use a database-assigned id and are addressed by a separate key field.

**F-M185:** **Canonical SRT form — one normalization for hash AND payload.** Every extracted or loose SRT is normalized once on arrival, before any QA gate, hashing or upload: strip a leading UTF-8 BOM, CRLF → LF, lone CR → LF, trim trailing whitespace. The same string feeds both the hash function and the upload body, so hash input and uploaded bytes are identical by construction. The normalization is **idempotent** and runs at extraction, at loose-file read, and again in the upload path (reachable via the retry path).

**F-M186:** **The registry content hash uses SubDL's own function** — the hash function returns the MD5 of the canonical payload from F-M185, lowercase hex, 32 characters, equal to the `md5` SubDL stores in `raw_files[].md5` for our uploads. Changing the function invalidates the existing registry; the database reset (F-M90) is the intended path, no migration. The media hash (F-M61) is unaffected — it is the OpenSubtitles OSHash over size + first/last 64 KiB.

**F-M61:** **Path-independent dedup key (OSHash):** upload dedup keyed on the media content hash, persisted across restarts, moves and renames.

**F-M39 [B2]:** **No double work across directions:** every subtitle the system uploads or downloads is registered under its normalized content hash, so a subtitle that already arrived from either direction is neither fetched nor sent again. The two directions answer it at different points: the **upload** side asks the content-known check on the candidate itself, right before sending. The **download** side does not ask it during the fetch of a regular candidate — the seeder asks it before the item enters the queue and keeps a file out whose loose subtitle content is already known. The hearing-impaired variant does ask it during the fetch. The download records the hash it saved.

**F-M249b:** **The date rule needs text after the date.** A date at the end of the name is not a date but part of a title.

## 12. Library Scope and Skip Filters

**F-M213:** **The library selection is a hard gate for every trigger and BOTH directions. Every report names only what was SELECTED.**

**Work:** with the library selection empty, no direction may start a run. The upload pipeline and the seeder stop up front; the download pipeline does the same (`No libraries selected — nothing to do`).

**Report:** the directory list (the GUI's *Library directories* list) filters on the selection check, not on the containment check. The walk scope includes the outer library of a nested selection (F-M189), but listing that library as a status row shows a directory the user never picked.

**Not in scope:** the database refresh's full-server id probe stays unscoped. It answers "does this item still exist", not "should this item be worked", so narrowing it would delete rows for merely deselected libraries.

**F-M220:** **The plugin description has THREE carriers and they must not drift — the card reads the DLL, not build.yaml, and an installed Jellyfin reads the package's build.yaml, not the manifest.**

All three — the plugin card, the package manifest and the catalogue manifest — must hold the same text. That text is capped at **320 characters**, must name the required SubDL and TMDb keys, and must end with the **AI disclosure** (`Created by AI under the directions of the maintainer.`.

The disclosure rides in all three because the reach differs: somebody browsing the catalogue reads the catalogue entry, while a server that has the plugin installed shows the description Jellyfin copied out of the package's `build.yaml` at install time. Jellyfins Installer schreibt diesen Pakettext in die Metadaten des Plugins, not the manifest entry — so a card without the sentence would hide it exactly where the plugin is in use.

`scripts/release.sh` enforces all of it (three identical, under the limit, keys named, disclosure last) as a precondition, so changing one carrier alone aborts the release.

Changing the card text requires a **DLL rebuild and a Jellyfin restart**; the string is not read at runtime.

**F-M34 [B1]:** **Skippable directories:** configurable list of folder/path patterns (e.g. `incomplete/`, `downloading/`, `.tmp/`) — matches are skipped entirely, reason "skip-dir" in status.

**F-M35 [B1]:** **Skippable strings in filenames:** configurable list of substrings (e.g. `sample`, `trailer`, `incomplete`, `.partial`) — match → skip with reason "skip-pattern". Case-insensitive, simple substrings.

**F-M211:** **The shared database context is created under a lock.** Concurrent first access by two jobs firing in the same tick must yield exactly one context; two contexts over the same data file race the object mapper and one task is lost.

**F-M177:** **Conditional follow-up reseed:** after the first seed/download/upload round of a cycle, a follow-up reseed runs exactly once — and only if new library arrivals were collected while a seed, download or upload was already running. Gated by the pending arrivals; the follow-up settings decide which directions take part, and with both off no reseed runs. Otherwise the forced scan seeds the pending arrivals and, if it adds new work, download runs before upload.

**F-M192 [B1]:** **The partial reset scopes (`scope=upload`, `scope=download`) WRITE BACK the rows they mutate.** Clearing a file-complete marker mutates the entities returned by a full read, so the persist step receives **those same objects**. Collect the touched entities and call the write-back. The scope logs the number of cleared markers.

**F-M201:** **The plugin persists its OWN configuration fields by patching them into the file on disk**, not by writing its in-memory object over it. The file is read, only those fields are replaced, the rest of the document is preserved byte for byte, and the result is written as a temp file that then replaces the original.

**Consequence:** the fields are taken from the file, so an empty or damaged in-memory state cannot erase user settings. The temp-file-and-rename write additionally ensures an interrupted save cannot replace a valid file with a truncated one.

A `null` field value is **skipped**, never written as empty: "unknown" must not become "delete".

When no file exists yet, only the plugin's own fields are written; the document is **not** seeded from the in-memory object.

## 13. Configuration and Settings Page

**The page as the operator meets it — tabs, fields, switches, readouts, colours and wording — stands in
`docs/GUI.md`**, every statement there carrying its `F-M` tag. This chapter keeps the normative rules.

**F-M18 [B1]:** Library selection: checkbox list of all JF libraries — only selected ones are scanned/uploaded (default: none).

**F-M53:** Manual run buttons and statistics reset on the General tab

**F-M216:** **A GUI call to a plugin endpoint that answers `application/json` must request `dataType: "json"`.** Without it the Jellyfin apiclient bundle returns the **raw response object** — `application/json` is not `text/*`, so the bundle falls through to its last branch. The object is truthy, so a `!s` guard never fires, while every field reads `undefined`. Set `dataType: "json"` on the statistics endpoint GET **and** the reset endpoint POST, in the **inline script of the page markup** — the page's only JavaScript since F-M324 withdrew the second copy (no file-pair trap; cf. F-M218). Other loaders in this file call `.json` themselves.

**F-M270:** **ONE page-wide "Refresh status" button at the END of the General tab reloads every live readout at once.**

**Rule:** the button sits INSIDE the general tab pack, at its very end below the Status block (F-M749), and writes one timestamp line directly above itself. It refreshes five readouts in one click: the API/search quota AND the download quota (both in `#ScribeQuotaBox`), the SubDL/TMDb lights AND the library read/write rows, the statistics counters, and the Workers list. Implementation: the refresh control.

**It is not a form action:** it must NOT sit next to Save, and no tab may carry a second refresh button. Nothing live exists on the Expert/Upload/Download tabs, so the General tab is the only correct home.

**Width and depth are set INLINE** (`display:block;width:100%;box-sizing:border-box;background-color:#424242;border-color:#5a5a5a;color:rgba(255,255,255,0.85)`) with `class="raised button-submit block emby-button"` as a fallback, inside a bare wrapper `<div>` — any wrapper margin breaks the flush fit with Save. The `block` class alone is NOT authoritative: a client/theme whose own `.emby-button` rule is ordered after `.block` overrides it silently.

**F-M271:** **The refresh timestamp has exactly ONE writer — the button's own click handler — and it names the manual act.**

**Rule:** the stamp sits directly above the button and reads `Manually refreshed <time>`, rendered from the CLICK moment in the viewer's timezone, seconds included. `Not refreshed yet.` before the first click.

**Resolution IS the feedback:** the time formatter must show seconds. It rendered hour/minute only, so a refresh inside the same minute produced a byte-identical string and the user concluded the button was broken.

**F-M222:** **Every required field is marked ONCE on the configuration page, and states where to get the value.**

Four required inputs: **Libraries** (F-M213), **SubDL account** (login + API key), **TMDb API key** (F-M203), **Download target languages** (F-M42: empty = download off). Each carries a required marker via `.subdl-required` / `.subdl-req-inline`, defined in both the page markup and the page script. The colour is F-M228's accent blue.

The marker sits in parentheses after the field name: `Libraries (opt-in, Required)`, `SubDL Account (Required)`, `TMDb (Required)`, `Target languages (Required)`. Opt-in and required are separated by a comma in the same parenthesis, never stacked.

The description explains what the value is for. A requirement repeated per field (label suffix, `required` attribute, bold "Required") reads as emphasis and stops being read.

Each field names its source in the order the user needs it — register first, then the key. SubDL: `https://subdl.com`, then `https://subdl.com/api-doc`. TMDb: `https://www.themoviedb.org`, then `https://www.themoviedb.org/settings/api`.

Libraries is opt-in AND required: nothing is processed until a library is picked, and the description states that an empty list stops every run.

**F-M230:** **The Libraries description states function and default in one line.** Wording: `Libraries to work in. Empty = no run.` Field descriptions state function plus default value, nothing else. **Every gate names its default, not just the newest lines** — *"the defaults are still missing in the GUI for quality gates upload"*, then *"3 of 5 are missing"*. Of the five gates under *"Quality gates (before upload)"* exactly three carried no default (the minimum number of cues, the SRT check, the sync check), while the two written later did. A default is what makes a switch readable without flipping it: the operator must be able to see what happens out of the box.

**F-M299:** **An intro block under a section heading describes what the section does — measurements never appear on the settings page.** Measurements, episode counts, before/after figures, accuracy figures and shares are **spec and commit material**, not UI text. **Budget: 300 rendered characters** per intro. The same holds for a description block under a checkbox: it states what the switch does and its default, and a diagnostic figure such as a failure share belongs in the log line that measures it.
**F-M229:** **Links in the settings page use the same accent blue as the rest of the page.** Jellyfin's stylesheet ships only `a{color:inherit}`, so the links in the field descriptions fell back to the browser default `#0000EE`. Rule: `#SubdlSyncConfigPage a { color: #00a4dc; }` — exactly one blue, no separate hover shade. Scope: link colour only; the destructive red and the status colours are untouched.

**F-M228:** **The settings page marks required fields in the accent colour `#00a4dc`, without extra spacing.** Red is reserved for destructive and failed states, so a red marker made a mandatory field look like a fault. The inline variant carries no margin of its own. Scope: the markers in the three headings and the inline variant, each with its own style rule.

**F-M36 [B2]:** Both lists maintainable on the config page (one pattern per line, textarea), defaults pre-filled (`incomplete`, `sample`, `trailer`, `partial`, `downloading`).

**F-M90:** **Database maintenance UI:** the config page exposes only **Reset** and **Restore** for `subdl-scribe.db`. **Reset** creates a timestamped backup, then removes the database so the plugin starts with a fresh registry. **Restore** restores the most recent backup after a single confirmation. No "Initialize database" control exists.

**Initialisation, first start:** the shared context creates the plugin data directory, opens the data file, raises the collection indexes and checks the schema marker. A missing data file is created empty; an existing one is opened and kept.

**Initialisation, every start:** the same sequence runs on each context creation, so a data file that lost its indexes or its schema marker is repaired to the current shape on open.

**Schema mismatch:** a lower stored schema version logs a warning and raises the marker (F-M195b); a data file written by a newer build is opened read-only.

**Reset is the only path to an empty registry.** Structural changes are never migrated; the stored state is reconstructible from the media files.

**F-M181:** **Single backup retention:** after a database reset all older `subdl-scribe.db.bak-*` files are deleted; only the backup created by that reset remains.

**F-M385 [D]:** **A quality-gate switch names its own action, and the two directions do not share a wording.** The upload language gate corrects (F-M375) and is labelled `Correct language (content vs. tag)`. The download gate throws the candidate away and is labelled `Reject wrong language (content vs. tag)`. The statistics row for attempts that never left reads `Uploads: stopped before sending` — "discarded" was read as SubDL throwing a finished upload away.

**F-M477 [D]:** **Upload and download are switched per source and direction, and the old switch remains the fallback.** Four switches replace the two global ones: `ScribeDownloadEnabled`, `ScribeUploadEnabled`, `TvSubtitlesDownloadEnabled`, `TvSubtitlesUploadEnabled`. The four are **nullable**, so that "never touched" stays distinguishable from "switched off". As long as a source switch is empty, the old value answers (`DownloadEnabled` or `UploadEnabled`), and a configuration file from before the split still means exactly what it meant — without migration, without a restart artifact. Reading happens exclusively through `IsDownloadEnabled(provider)` and `IsUploadEnabled(provider)`, never through the field: no caller may rebuild the order itself. The two **arrival switches** (`DownloadOnArrival`, `UploadOnArrival`) stay global — they say *when* a direction runs, not *where to*, and quadrupled per source it would become a schedule nobody can oversee. Checked by T202.

**F-M503 [D]:** **The tvst download is ON in the unset state, the tvst upload stays OFF.** Concept decision: tvst download is the download default; its upload default is deactivated. `IsDownloadEnabled(TvSubtitles)` therefore answers `TvSubtitlesDownloadEnabled ?? true`, `IsUploadEnabled(TvSubtitles)` stays `TvSubtitlesUploadEnabled ?? false` — the two directions of the same source come out **counter-running** in the default.

The default answers **only** the case "never written": a stored `false` still means OFF and is overwritten by no default. The old switch (`DownloadEnabled`) does **not** answer for tvst (it belongs to SubDL, F-M477), so a configuration from before the split changes nothing about tvst. Consequence that explicitly holds: `DownloadEnabled = false` no longer switches the **direction** off as long as the tvst switch is unset. The download light and the task read `AnyDownloadEnabled` (F-M492) and stay on until **both** sources are off; "everything off" has meant, since this decision: both download switches off.

The page shows the **effective** state: an empty field is displayed as ticked (the old `!!value` expression showed OFF where ON held), and the field description names "Default: on" for the download and "Default: off" for the upload. Movies are not affected: tvst has no id index and is not even asked without series/season/episode (`ForeignCandidatesAsync` exits before the call), the default costs no call there. Checked by T225.

**F-M478 [D]:** **A switch without a built-in source is visible, switched off and says why.** tvsubtitles.net has its two switches on the page, but neither a fetch nor an upload in the code. `HasProvider(TvSubtitles)` is false, both directions are off regardless of the stored value, and under the switch stands the reason (`MissingProviderReason`) in the same wording that the worker note also uses. The field is switched off (`disabled`) and is written to `false` on save, so that an old value never remains standing as an invitation. The rule is the same as with F-M366: a control that does nothing is worse than none, because it claims a choice. Checked by T202. **Continued by F-M485:** the second source is wired in — `HasProvider(TvSubtitles)` has been true since, the two fields are operable, and the switches act per direction (the source part of this entry no longer holds). **Continued by F-M503:** the default of the two tvst directions is download **on**, upload **off**. Checked by T225.

**F-M479 [P]:** **The four switches stand in the tab of their direction, not in a provider tab of their own.** Download switches in the download tab, upload switches in the upload tab, one row per source with the source name in the label (`Download enabled — SubDL`). A provider tab would have separated the direction from its switching and forced the path "first here, then there"; the direction is the question the user has.

**F-M743 [U]:** **The sources stand as tabs, not as a stack: one tab per source, one card visible at a time.** Concept decision: the endpoint cards are cycled through, because three stacked cards pushed the media-file block off the first screen and made the page scroll before the operator had seen anything. The tab strip carries the source's short name (F-M746) and a dot that says whether the source takes part (F-M744), so one glance says which sources run at all. The first tab is open when the page is drawn. Switching hides the other cards but keeps them in the document: values, `visibleWhen`, the per-card `Save` and the result line all keep working on the hidden card, and nothing is written that the operator did not ask for. The endpoint order (F-M698) keeps standing directly under the `Endpoints` heading, above the strip. Checked by T404.

**F-M744 [U]:** **A source that cannot take part shows a dead lamp on its tab; no card carries a state word.** Concept decision: three cards repeating `on`/`off` is too much on one page, so the state sits where the choice between sources is made — one lamp per tab. Filled green means the source takes part, a hollow grey ring means it does not, and the tooltip names the reason; the hint under the card's name says what is missing. The answer comes from the source endpoint (`participates`): `off` holds when the source is not configured *or* when both of its directions are closed, `on` only when it is configured and at least one direction is open (F-M708). Grey marks the state, not red — red stays reserved for destructive and failing states (F-M228). Checked by T406.

**F-M745 [U]:** **A field of a source card may carry one short line of help — at most 80 characters.** Concept decision: a short hint under a field is allowed, a paragraph is not. That is the operator's line: the ban in F-M702 came from "too much talk in sources gui description", and a ban was the wrong reading of it. So: one sentence, one line, 80 characters at most, naming the place or the unit — never a reason, never a second sentence, never a step-by-step. Everything else of F-M702 stands unchanged: a secret field carries no placeholder and no `set`/`not set` line, and the light at the top stays the one summary of whether a secret is stored. The card is drawn from the schema, so the page needs no second copy of any text. **A help line may carry one address as a link.** The source names the address and the word of the line that becomes the link; the page builds the anchor from document nodes, so no markup crosses the source boundary and the line stays the same sentence (F-M682). Concept decision: the access fields point at the provider — the API key line names registration, the email line names the login credentials — and the switches say in one line what they do. The three later switches of the SubDL card read: continue after limit waits for the daily quota reset in both directions, prefer season ZIPs takes the pack instead of single episodes, and the machine-translation switch names the account it concerns. Checked in `sources` and `gui-structure`.

**F-M746 [U]:** **A tab carries an abbreviation of its source; the card head keeps the full name.** Concept decision: three full source names side by side eat the width of the bar, so the tabs read `SubDL`, `tvst` and `mst`. The abbreviation rides on its own carrier in the source's capabilities, and an empty carrier means "take the row's short name". It is *not* the row's short name itself: F-M731 keeps the labels of the pipeline lines as they are, and a tab label is not a log label. The full name stays in the tooltip and in the card head, so nothing has to be guessed. Checked in `gui-structure`.

**F-M747 [U]:** **Reporting is a switch of the source, not of the download section.** Each source card carries the tick that decides whether broken subtitles of that source are reported back to it; the card of a source without a report route carries none. Concept decision: the old switch in the download section said the same thing for every source at once, which is wrong for a source that has no report route, and it hid the decision away from the card it belongs to. Its stored value survives as the fallback for a source file that predates the field, so nothing that was switched off comes back on. Checked in `reporting`.

**F-M748 [U]:** **A secret field carries an eye that shows the stored value for as long as it is pressed.** Operator request: while the button is held the field shows the value instead of the dots, and on release the dots are back. Concept decision: the page never receives a secret with the list, and that rule stays. The value comes over a route of its own instead: one field per call, and only for a field the source declares as secret; anything else is refused. Every call is logged with source and field, never with the value. The eye stands on the two API keys and on the password; the address of the SubDL card stays a plain text field and carries none. A value that is currently shown counts as unchanged, so pressing the eye and saving does not send the secret back. Checked in `sources` and `gui-structure`.

**F-M749 [G]:** **The Status block stands under the Workers list.** Concept decision: the run readout comes first and the reachability readout follows it, so the general tab reports what the plugin did before it reports whether the sources answer. The block keeps its content unchanged — quota bars, the five lights, the library directory rows — and the refresh control follows the Status block, with the Statistics block below it (F-M757). The deferral line keeps its place directly under the Workers list (F-M288). Checked in `gui-structure`.

**F-M750 [U]:** **A written setting names itself in the log — which file, which field, which value before and after.** Concept decision: the line is a detail line — written at information with the `[D]` marker, so the server level never has to be raised, and shown while the plugin's own mode is `Debug`. It stands one per changed field, and an unchanged field stays silent so that a save with nothing to change costs no line. The value appears only for switches, numbers and selections; free text and secrets are reported as `gesetzt (<n> chars)` or `leer`, because a log line is no place for an address or a key (F-M683). Both write paths are covered: the card of a source, which writes one settings file, and the settings page, which writes the plugin configuration. Reason: two lines 32 seconds apart reported opposite states of the upload direction, and nothing in the log could decide whether a switch had been misread or just saved. Checked in `sources`.

**F-M751 [U]:** **No password and no API key reaches the log, not even on the debug level.** Concept decision: a settings line names switches, numbers and selections with their value, and every character string only as `gesetzt` or `leer` — no length, no excerpt, no beginning. That covers passwords, API keys, addresses and library names alike, because a log line is read by more eyes than the settings page. A replaced key still reads as a change: `gesetzt -> gesetzt (neu)`, so a swapped secret never looks like nothing happened. Checked in `sources`.

**F-M752 [G]:** **The refetch interval stands directly above the media files block.** Concept decision: the General tab is read from the run outward — libraries, target languages, the TMDb key, the endpoint order and the source cards, then the interval, then the work on the files. The field stays inside the page form and its save path is untouched, and the schedule line that names the next run travels with it. Checked in `gui-structure`.

**F-M753 [G]:** **The TMDb key field follows the pattern of a source card's secret field.** Concept decision: the
stored key is never written into the field, so the field cannot show one dot per character while a source card
shows a fixed pattern. The field carries the same eight-dot placeholder. The eye stands on it only while a
key is stored, and it takes the shown value back on release. An empty field means *leave the stored key
alone* — the same rule that governs a secret in a card. The field is therefore no longer a required HTML
input; the page keeps the requirement and refuses to save while neither a stored nor a typed key exists.
Checked in `gui-structure`.

**F-M754 [G]:** **The season-pack switch carries one name and one help line on every card that offers it.** Concept
decision: the switch reads `Prefer season pack` on the SubDL card and on the tvsubtitles card alike. Both carry the
sentence `Takes the season pack instead of single episodes.` A switch that means the same thing says the same thing —
and the help line is the one a field may carry, at most 80 characters (F-M745). The key in the settings file stays
`preferSeasonZip`; renaming it would orphan every stored value for a word nobody reads. Checked in `sources`.

**F-M755 [G]:** **The TMDb help line says how to get a free key, and nothing else.** Concept decision: the line reads
`Free API key after registration at themoviedb.org.` — one sentence, the address as the link word and its target the start page of that site, 50 characters. What the
key is used for is not said again: the heading already carries `Required`, and the page's other lines stay silent about it.
Checked in `gui-structure`.

**F-M756 [G]:** **The runtime tolerance field says what it measures, then its default and its floor.** Concept decision:
the field carries two lines. The first names the measurement — `Allowed drift between the cue span and the media
runtime.` The second carries the setting — `Default 12 %. Floor: 7 min films, 4 min episodes; 0 = floor only.` Two lines
are the exception this field needs: a percentage whose subject stays unnamed cannot be set well, and the floor is part of
the rule rather than a reason (F-M745). Checked in `gui-structure`.

**F-M757 [G]:** **The Statistics block is the last block of the General tab.** Concept decision: the counters describe what the refresh control reloads, so the control is the handle and the numbers stand under it. The tab is read from the run outward and ends with the two readouts that report the run: the Status block, the refresh control, and then the statistics with their reset button. The block itself is unchanged, and the counters still belong to the tab that holds every live readout (F-M270). Checked in `gui-structure`.

**F-M758 [G]:** **The reset control carries the footprint of the refresh control.** Concept decision: both controls only re-read or clear a readout, neither of them saves, so they look alike — full width, flush with both edges, the same grey. The blue Save stays the only blue control of the tab, and the page never shows two different kinds of button in one column. Checked in `gui-structure`.

**F-M759 [S]:** **Season packs are counted as season packs.** Concept decision: the statistics carry two rows per direction — *Season packs downloaded* directly under *Subtitles downloaded*, and *Season packs uploaded* directly under *Subtitles uploaded*. A delivery counts as a season pack when its archive carried at least two episodes — SubDL wraps every delivery in a zip, so the file name proves nothing. One accepted container is one uploaded pack, however many episodes it carries. One fetched archive is one downloaded pack, and further episodes served from that same archive count neither as a pack nor as a subtitle. A dry run counts no packs, like every other counter. The two rows reach an existing data file additively, so a counter that did not exist before starts at zero while the older volume row keeps its history. Checked in `seasonpack` and `gui-structure`.

**F-M760 [S]:** **The container's file count comes from the container.** Concept decision: every source fills one neutral number on the offer it makes — how many subtitle files the archive holds, or zero when the source does not say. SubDL counts the files it lists for the entry, tvsubtitles counts the season archive it has just read, and a source that carries no archives leaves the number alone. Nothing is read from a file name: SubDL wraps every delivery in a zip, so a name ending in `.zip` says nothing about how many episodes travelled inside. The statistics read that one number and nothing else, which is why the same rule holds for every source. Checked in `endpoints`.

**F-M761 [S]:** **A release group stands behind a technical word, never behind an episode title.** Concept decision: a season pack carries one release, so the group is read from the file name — but only where the name states one. Behind a dash the group counts only when the whole segment in front of it is a technical token, which is the same rule the dot and space branches already follow. A segment that ends on an episode tag can never be followed by a group: `Better Call Saul - S04E01 - Smoke.mkv` ends in a title. Nothing is invented where a name names nothing; those episodes share the unknown group, and a season that is unknown throughout still becomes one pack. A stored pack verdict is not invalidated by a rule change; the operator clears it for the season in the expert view, so the moment a verdict is set aside stays visible. Checked in `seasonpack`.

**F-M762 [S]:** **Whether a source has a follow-up pass is a capability of that source.** Concept decision: the run asks each source whether its uploads need a postprocessing pass and arms the appointment from that answer, so the pipeline names no source. SubDL is the one source that says yes today — it hands out a handle while uploading and needs the pass for the entry id; a second such source would be wired by its engine alone. Checked in `packretry`.

**F-M764 [S]:** **The arrival question comes before the work, not after it.** Concept decision: an arrival run asks whether it builds a pack *before* the library is examined, so the completeness question is never put to it. Until 12.3.8.65 the scan ran first and the arrival branch then emptied the set. Measured in the arrival run of 26.11.2026: `pack phase done after 166343 ms` inside a 9 min 35 s run. In it stood 128 seasons with at least two episodes, 1,093 judgement lines over 124 seasons, 1,084 language pairs and 85 candidates — discarded two lines later. A guard that stands behind the work is not a guard. Checked in `packretry`.

**F-M763 [S]:** **Every delivery counts — the statistics count per source, in both directions.** Concept decision: a subtitle that two sources accept is two deliveries, and the counter says two; a container likewise counts once per source. Until here the upload counter added one per subtitle, no matter how many sources took it, while the pack counter already counted per send — measured on 26.11.2026: 42 subtitles stayed 42 in the statistics but were 70 deliveries (SubDL 42, tvsubtitles 28). The download direction needs no change: a fetched subtitle comes from exactly one source, so counting per save already counts per source, and one archive counts one pack at the source that served it. A dry run still counts nothing. Checked in `seasonpack`.

## 14. Data Model and Persistence

**F-M38 [B1]:** **Persistent state split by the kind of thing described** (section 14). Embedded tracks live under their video file, keyed by media hash + stream position; loose subtitles are keyed by normalized content hash; the file's own record carries path, IDs, season/episode and the derived language aggregate. The **hearing-impaired flag belongs to each subtitle datum**, never to the file (F-M282), and **completeness is derived rather than stored** (F-M283) — so the file record carries no download mark and no HI aggregate. Both pipelines read and write the same plugin database.

The plugin keeps its state in **one database file**, grouped into **five clearly separated areas**, each keyed by the thing it actually describes. The areas share a lifetime and are written by the same lock, but they are not mixed.

**F-M194:** **The business keys are the primary keys.** There is no automatically assigned id; an upsert of an apparently new row can therefore never produce a second row.

**F-M327:** **A plugin database format is adopted once at start, before the plugin database is opened, and remains as a backup.** The adoption path is the only reader of the old format and deletes nothing.
#### 14.0 Area 0 — Compatibility record (`meta`)
**F-M195:** exactly **one** row (`_id = "db"`) describing the file itself: the schema version (integer, raised by hand whenever the stored shape changes), the plugin version that wrote it last, the Jellyfin version, and a timestamp.

A **lower** version logs a warning and raises the marker; no migration (F-M195b).

The row is written **once per context creation**, never per run: an audit record that rewrites itself every cycle is a write amplifier and tells nobody anything.

#### 14.1 Area 1 — Media hash cache (`oshashes`)
#### 14.2 Area 2 — Video files and their embedded tracks (`media`, `embeds`)
**F-M61b:** keyed by **file path** → the hash, the size, the modification time, its stamp. A lookup validates size **and** mtime and treats a mismatch as a miss, so a replaced file is re-hashed automatically.

**F-M196:** one record per **video file**, `_id` = the stable OSHash (16 hex characters, F-M61). Carries the path, the Jellyfin item id, the IMDb id, the TMDb id, the SubDL id, the series flag, the season, the episode, the last-seen stamp. For a series episode the ids are the **SHOW** ids, never episode or season ids (F-M191).

Derived, written automatically whenever an embedded track is stored so it cannot drift: the language aggregate (sorted, comma separated). There is **no hearing-impaired aggregate** — the flag belongs to the datum (F-M282) and an aggregate would be a second truth beside the rows.

**F-M197:** one record per **embedded subtitle stream** in `embeds`, `_id` = `"<media hash>|<stream position>"`.

The key is media hash **plus position**. Two streams of one file can share a language (a normal and an SDH variant), so the language alone never identifies the row.

Fields: the language, **both datum properties** (the hearing-impaired flag and the forced flag), the content hash, the status, the reason field, the status stamp, the SubDL id (F-M198). Each property is stored three-valued (F-M285) — `ja` / `nein` / not said — and a row that does not state them claims no coverage.

#### 14.2a The subtitle DATUM is the pair (language, hearing-impaired) — F-M282
**F-M282 [B1]:** **A hearing-impaired subtitle is its own subtitle DATUM** — its own row, identity, content hash and flag, never a property of the media file and never a modifier of a language. Every reader and writer asks at the grain of the **pair** `(language, hearing-impaired)`: area 2 keys by media hash + position (F-M197), area 3 by content hash (F-M199), so `Movie.de.srt` and `Movie.de.sdh.srt` are two rows and two data. The pair is also the key space of the upload check, so a normal upload never blocks an SDH variant (F-M71).

**The one rule that does NOT split:** a subtitle of language L proves L whether or not it is the variant — SDH is the same dialogue with annotations (F-M243) — and proves `L (HI)` only when it IS the variant. So `Movie.de.sdh.srt` alone satisfies both pairs of German; `Movie.de.srt` alone satisfies German only.

#### 14.2b Completeness is DERIVED, never stored — F-M283
**F-M283 [B1]:** **There is no download completion mark.** Both download-completion fields do not occur in the file record, and with them the whole support apparatus that read and wrote it.

Completeness is answered **on every ask** by ONE reader — the subtitle-coverage question, asked by pipeline, seeder and refresh alike — from three sources: the loose subtitle files beside the media, the media container's embedded rows. The language-level stream list (counted only where the configuration counts embedded tracks as coverage).

**No invalidation step exists, and none is needed.** Deleting `Movie.de.sdh.srt` removes its evidence, so the pair is open on the next ask; the refresh therefore **reports** open files and writes nothing. A pair SubDL does not carry is retried on the next refetch (F-M47) — no expiry, no retry budget as a brake.

#### 14.2c The second property: FORCED — F-M284
**F-M284 [B1]:** **`forced` is a property OF THE SUBTITLE DATUM, like hearing-impaired** — its own row and flag, never a flag on the media file and never a way to identify a language. Grain: `(language, hearing-impaired, forced)`.

**They are NOT symmetric on SubDL's side.** Hearing-impaired is bilateral — two server-side pools (`&hi=1` / `&hi=0`, F-M241) plus an upload field — so it can be searched, fetched and announced. **Forced is LOCAL:** no filter, no field, no candidate names it. A forced datum is therefore **OBSERVED, never DELIVERED** (`SubtitleRef.IsDeliverable`) and never covers its language (F-M246).

**A forced track is RECORDED, not dropped** — that rule lives in the coverage reader alone. Marker `forced`, read as a SET with `sdh` in either order; bitmap tracks stay excluded (F-M6), and a rewrite re-states the flag or is rejected.

#### 14.2d `hi` and `forced` are stored THREE-valued — F-M285
**F-M285 [B1]:** **Every subtitle datum states `hi` and `forced` explicitly as `ja` or `nein`.** Three-valued: `true` (it IS the variant, or IS forced), `false` (it is NOT — also a statement, written explicitly, because a writer that knows the fact must never leave it unsaid), `null` (the row does not say).

**A gap is never read as a statement.** A reader treats `null` as "no claim", so the datum reports no coverage. That direction **converges** — the item is worked once more and that pass states the value — whereas the opposite default would look settled forever. The refresh fills a `null` from the row's own file name **with the value it read, `false` included**. Both properties are REQUIRED parameters on both paths, and both name builders carry `forced`.

#### 14.3 Area 3 — loose subtitle files (`sidecars`)
**F-M199:** one record per loose `.srt` file, `_id` = the **normalized content hash** (F-M186).

Content is the identity of a loose subtitle: the same subtitle stays known after the file is renamed or moved, and two loose subtitles sharing a language but differing in text stay two rows. The file name is stored for diagnosis only and plays **no part** in the key.

Fields: the shared subtitle state (F-M198). Plus the file name, the path, the media hash and a copy of the parent metadata (the IMDb id, the TMDb id, the series flag, the season, the episode), so a loose subtitle can be judged without loading the file record.

Downloaded subtitles are stored here too: once fetched, the file on disk **is** a loose subtitle.

loose subtitles carry **no stream position**. Terminality is the outcome itself.

#### 14.4 Area 4 — Burned download candidates (`rejected_candidates`)
**F-M200:** one record per fetched-and-discarded download candidate, `_id` = `"<item id>|<language>|<SubDL id>"`.

Its grain is neither a stream nor a piece of content but "this remote release was burned for this file and language", and SubDL's own id is the only identifier such a verdict has.

Fields: the reason field, its stamp. Capped at the newest 50 per file and language. Cleared for a file and language when a download finally succeeds.

#### 14.5 The four subtitle states (F-M198)
**F-M198:** **A subtitle sits in exactly one of four states — `uploaded`, `rejected`, `downloaded`, `pending` — and `pending` is never stored.** The state name carries no reason; the reason has its own field. There is no settled state: whether a position is finished is answered by the presence of one of the three stored outcomes.

A subtitle in area 2 or 3 is in exactly one of four states:
- **uploaded** — sent to SubDL and accepted.
- **rejected** — not accepted. The **reason** lives in its own field (the reason field), never in the state name: `duplicate-remote`, `duplicate-content`, `too-small`, `too-few-cues`, `lang-mismatch`, `bad-structure`, `runtime-mismatch`, `und-off`, `und-too-small`, `und-detection-failed`, `unmapped-language`, `duplicate-self-echo`, `candidate-rejected`.
- **downloaded** — fetched from SubDL.
- **pending** — nothing recorded yet. This is **never stored**: it is the absence of a row.

There is **no "settled"/"done" state.** "Is this position finished?" is answered by asking whether any of the three stored outcomes is present.
#### 14.6 Key discipline (all areas)
- Composite keys are built in exactly one place per area (the embedded key, the candidate key).
- Every area has an index on its key; the query paths used by the pipelines are indexed as well (media hash, content hash, status, language).
#### 14.7 Consequences for readers and writers
- `IsUploaded(mediaHash, language, hearingImpaired)` answers "do I still need to upload this language for this file".
- Per-position questions use the embedded lookup / the rejection reason / the terminal-position test — never a pair-level query.
- A stream skipped **because its language is already uploaded** is recorded as **rejected with `duplicate-self-echo`**, not as "uploaded".
- The derived file aggregate (the language aggregate) is recomputed whenever a track is written, in one place. There is no hearing-impaired aggregate (F-M282/F-M283).
#### 14.8 Migration policy
**F-M195b:** structural changes are **not migrated**. The schema marker in area 0 makes the change visible and the database reset (F-M90) is the intended path; records from an unrecognised schema version are ignored rather than rewritten.

**F-M325 [D]:** **The data plugin database is SQLite through the host's Entity Framework Core. The engine rides along in no ZIP, and there is no second engine in the tree at all.** The plugin keeps a file of its OWN, `subdl-scribe.sqlite`, in its data directory — nothing is ever written into Jellyfin's database, which its own migrations rebuild. The schema is created without migrations, and the business keys are the primary keys — never an auto-assigned id (F-M194). The engine is a host dependency (`ExcludeAssets=runtime`), so no SQLite file rides in the ZIP. A one-time import of the earlier document plugin database does not exist, and with it the previous engine: the operator had the migration function and the old database taken out. Every installation that could still carry an old file has long since imported it. No code reads a legacy file any more, and none is deleted either — a leftover document plugin database stays where it lies. See F-M194, F-M236, F-M326.

**F-M326 [D]:** **the plugin database runs in WAL mode with a busy timeout, and every file-level copy of it is checkpointed first.** WAL is set explicitly — it is not the default — so a reader never blocks the writer and never sees a half-written state. `busy_timeout=5000` covers a second context or a second process over the same file. The reset and restore routes checkpoint first and clear the side files before they copy. Connection pooling is OFF as a correctness setting: the file-level routes dispose the context, copy a file over the database and open a fresh context, and a pooled connection would keep the old handle. The pool is cleared as well for the operation that cannot tolerate a stale handle. See F-M325, F-M327.

**F-M602 [U]:** **The writes of a run are bundled.** Measured in the upload run: **320 `media upsert` + 123 `embed` writes**, median interval 99 ms, mean 238 ms. In sum **105 s of 257 s run time (41 %)** went for individual database writes, and an item with 26 text tracks alone cost ~20 s. The database **can** bundle that long since (`ScribeSqliteContext.BeginBatch`/`EndBatch`, `BatchFloor = 500`, in use in the seeder since October); the upload and download runs only never opened it. Now the run opens a bracket and closes it in the existing `finally`: one commit per run (or per 500 lines) instead of two per track. Reading remains untouched by that, because all readers go through the row cache of the areas.

**F-M614 [U]:** **The database is written at the end of each item, not only at the end of the run.** Concept decision: the plugin database is written at the end of each item, not only at the end of the run. There was no good reason, only a fault: `ContentHashRegistry.Flush()` was an **empty method**. The callers believed in a flush, but the lines lay in the F-M602 bracket until the end of the run. Consequence, experienced live: after a restart in the middle of a run (twice on 18.11. through deploys of 483 and 484) the title locks were gone. The same eight titles (`tt35538033`, `tt34685692`, `tt34379051`, `tt27613895`, `tt34564059`, `tt27419466`, `tt32547691`, `tt28638842`) ran in 482, 483 **and** 484 again through the title ladder — 4 attempts each with 2/4/8 s pause. Now the pipeline calls `Registry.Flush()` (now `_db.FlushPending()`) at the beginning of each item; at most the work of the running item is thereby still open. `FlushPending` is a no-op without open lines, so it costs a commit only for items with a write. **Generally** (operator: "And that generally. Not per track, not per run, per file"): the same line now also stands in the download pipeline. Checked by T324.

**F-M616 [U]:** **The title lock (and the refusals) hung on no data context — in the normal start.** Diagnosis from the log of 487: the ladder closed the same titles again in every run — `closed for good` at 20:52, 20:54, 20:56. But **not a single** database line appeared — neither `closed and written` nor `attempt N written`. Cause: `TitleFailureStore.Attach` and `SourceRefusalStore.Attach` stood only in `RecreateDbContext` (the path after a reset). The normal start goes through the getter of `SharedDbContext`, and only three databases hung there (TMDb, season airing, pack verdicts). Consequence: `_db` was `null`, `Close` and `IsClosed` ran silently into the void — hence 482, 483, 484, 485, 486, 487 always the same eight titles. It is the same fault as F-M545 (found there for the TMDb database) — only F-M605 and F-M608 were later wired half the same way again. Now there is **one** method `AttachStores(db, logger)` that attaches all five databases and the log, and **both** start paths call it; T326 checks that there are exactly two calls and that no database is attached past the helper.

**F-M617 [U]:** **Every database helps itself when it is not attached.** The lesson from F-M545 was only half implemented: of five databases, so far **only** `TmdbAnswerStore` fetched a missing context itself (`Plugin.Instance?.SharedDbContext?.Context`) — the other four wrote silently into nothing when the wiring was missing. Exactly that made F-M616 so expensive: the title lock could have survived the fault. Now each of the five databases has the same self-help in the `Store()` access (attachment via `AttachStores` remains the regular path, this is the net underneath); `TitleFailureStore` additionally warns once per process when the self-help also fails. T327 checks for **every** database in the directory that the self-help is there, and names the missing files on failure.

**F-M390 [P]:** **The published season packs have their own table `packs`, one row per season, language and SDH variant.** The key is `"<imdb>|S<season>|<lang>|<hi|plain>"`, the row carries the release, the pack id, the episodes it contains and the ids of the individual uploads that went into the pack. The reason is not tidiness: without this note there is no memory of the fact that a season has already been packed — a run would repeat the same pack endlessly or never try it again after the episodes went up individually. The absorbed ids are kept because folding means a deletion on SubDL; a deletion without a record can neither be checked nor undone. The file is created without migrations and the table is additionally pulled in at start via `CREATE TABLE IF NOT EXISTS`, because creating an existing file adds nothing; schema version 6.

**F-M514 [D]:** **A refused attach does not poison the plugin database — and a row without a business key is not attached in the first place.** Incident on jf-test, verbatim from `jf-run.log`.

02:51:48.410 `[ERR] DownloadPipeline: [SubDL-D] WRITE FAILED … Unable to track an entity of type 'MediaEntity' because its primary key property 'Id' is null.`, 02:51:48.428 `[WRN] WorkerRunRegistry: … recording the result of worker ScribeAutoSyncTask failed: … (Parameter 'Unexpected entry.EntityState: Detached')`.

Then in **every** cycle `[SubDL-Dispatch] … cycle failed` and `[N] [SubDL] cycle finished without an upload run` (03:03:46, 03:07:56 repeated). The upload cycle started no run any more. Cause: the download writes a loose subtitle row after a save, and the media hash comes from `Registry.GetMediaHash(mediaPath)`, which for a file just written **knows no OSHash row yet** and therefore returns `null`; `ScribeDbContext.EnsureMedia(null)` built `new MediaEntity { Id = null }` from it. Entity Framework Core 10 refuses the attach **and leaves the refused instance in the change tracker as `Detached`**; from then on **every** `SaveChanges` of this context throws `ArgumentOutOfRangeException: Unexpected entry.EntityState: Detached` (from the `CommandBatchPreparer` of `EntityFrameworkCore.Relational`), including the recording of the workers.

Rule: (1) **Without a business key there is no row.** `EnsureMedia` writes **nothing** for an empty media hash and returns an unsaved instance (only a `Detail` line). `ScribeSet<T>.Upsert`, `Update(IEnumerable)` and `InsertBulk` refuse every row with an empty key and **warn** (`[SubDL-DB] {Area}: a row without a business key was not written`). The rule lives in the shared set and therefore applies to **every** area and **every** worker, not at the individual caller. The attach runs **before** the row is taken into the row cache, so a refusal leaves no row in the cache that no table carries.

(2) **`MarkSidecar` passes "no medium known" through as an empty value** — the media hash is a `NOT NULL` column, and the observing caller (`RecordSkippedContent`) has always written `mediaHash ?? string.Empty`. The loose subtitle row stays correct because it is addressed by its **content**, not by the file.

(3) **A `Detached` ghost is not a permanent state:** `ScribeSqliteContext.SaveChanges` catches **exactly** this exception, rebuilds the change tracker (`DetectChanges`, note the states of the tracked rows, `ChangeTracker.Clear`, re-attach the same instances with their state) and repeats the write **once**. `Entry.State = Detached` on the ghost does **not** remove it (measured) — only `Clear` does, which is why the remaining rows are rescued rather than discarded. The retry is safe because the exception falls while **preparing** the commands, i.e. before any command runs: a failed preparation writes nothing. A **second** failure is not swallowed — then the plugin database stays visibly broken.

(4) **The recording stays a log, not a business branch:** `WorkerRunRegistry.Start`/`Finish`/`MarkCancelled` keep warning and do not throw; the fix only makes their rows writable again at all.

(5) **The film line says what it did:** `[moviesubtitles] {Media}: '…' — gespeichert.` claimed a save although `Persist` writes **nothing** without a media hash. It now reads `— gespeichert.` or `— nicht gespeichert: kein Medien-Hash bekannt — die Nummer gilt nur für diesen Lauf.` (The `?` at the same place is the placeholder for "no medium known" and stays.) Checked by T236.

**F-M620 [U]:** **A single file goes back into the queue through a route of its own — the plugin database stays untouched.**
Route `POST /Plugins/SubdlReset/ResetPath?confirm=true&path=<absoluter Pfad>&what=both|stamps|verdicts`. `confirm` must be exactly `true` and the path absolute, otherwise the route answers with `{"ok":false,"error":…}` without any write access.
Reason (measured): the queue arises only from open stamps (F-M557/F-M563) — while a stamp stands, the file is never looked at again. Eleven episodes (Eyes S01E01–E07, Taylor Swift S01E02/03/05/06) stood as `complete` in the plugin database, while neither SubDL nor tvsubtitles had anything of them and not a single line in the log explained the verdict.

The only available way out was the whole-plugin database reset (F-M90), disproportionate for eleven files.
`what=stamps` takes back the three belonging stamps together with the track signature (since F-M668 the `file` rows of the mark matrix, before that the columns `CompleteSubdl`, `CompleteTvSubtitles`, `CompleteMsub`, `CompleteSignature`), `what=verdicts` deletes the track rows with `Status=rejected`, `both` (default) both. **Untouched remain** rows with `Status=uploaded` and the origin of a find (`SidecarEntity.Origin`) — both is evidence, not a verdict, and whoever deletes evidence uploads the same file a second time (F-M489/F-M500). The route writes one line per call (`LogUtil.Normal`, code F-M620) and calls `ReloadQueues()`, so that the next scan queues the file again; return `{ok, path, media, stamps, verdicts}`. **The operator path is the GUI**, not curl: Expert → Plugin database → „Reset one file" (path field, choice `both`/`stamps`/`verdicts`, button) calls exactly this route and states in the answer how many stamps and verdicts have fallen.

Checked in `scripts/tests/endpoints`.
**F-M660 [D]:** **The file reset can take the verdicts of the pack phase along — and that is selectable in the dropdown.** The reset of a file (F-M620) clears stamps, rejected track verdicts and the refusal in the `packs` row (F-M644). The verdicts of the pack phase, however, lie in a **third** plugin database (`PackSeasonStore`, key = IMDb, season, language mark, value = signature of the files, F-M611/F-M657). Because the signature is built from episode count, size and date of the files, it keeps fitting unchanged after a file reset: the track is free, the season stays locked. Measured on MobLand S02 — track verdicts deleted (`verdicts: 2`), the plan skips the season anyway. That is why the route now knows the value `pack` (single or joined with `+`, e.g.

`verdicts+pack`), which deletes **all** season verdicts of this series and season — plan and fill namespace — and reports the number back in the field `seasonVerdicts`. The dropdown of the settings page offers &bdquo;only the pack verdicts (season memory)&ldquo; and &bdquo;rejected verdicts and pack verdicts&ldquo;. `both` keeps its old meaning (stamps and track verdicts), so that an existing call does not silently delete more than before. Checked in `gui-structure` (both options in the dropdown, hint text) and `endpoints` (value `pack`, report, `ClearSeason`/`ReopenSeasonVerdicts`).

**F-M667 [D]:** **The marks lie in ONE matrix — one row per (medium, source, kind) — and the plugin database is adopted there once at the first start.** Concept decision: the marks lie in ONE matrix with the sections track normal, track HI, file and pack. Each section carries its ids (source (subdl, tvst), imdb, tmdb) and its verdicts (track closed non HI, track closed HI, file closed with fingerprint mtime, pack closed with fingerprint mtime), plus Oscache and the further sections that we have in the store. Until schema 17 the marks stood as **wide columns** on the row: `SubdlUploadedAt`/`TvSubtitlesUploadedAt`/`MoviesubtitlesUploadedAt`, `SubdlDownloadId`/`TvSubtitlesDownloadId`/`MoviesubtitlesDownloadId` and `SubdlUploadId`/`TvSubtitlesUploadId`/`MoviesubtitlesUploadId` on `embeds` **and** `sidecars`, the belonging stamps `CompleteSubdl`/`CompleteTvSubtitles`/`CompleteMsub` on `media`, refusal and fingerprint on `packs` — a fourth source would have meant four new columns per table.

Rule: (1) **One table `source_matrix`, one row per (medium, source, kind).** Source and kind are VALUES, not column names: `Source` ∈ {`subdl`, `tvsubtitles`, `moviesubtitles`}, `Kind` ∈ {`track` (track normal), `track-hi` (track HI), `file` (file closed), `pack` (season pack)}. N sources produce N rows, not N columns.

(2) **The key** is `&lt;Anker&gt;|&lt;Quelle&gt;|&lt;Art&gt;|&lt;Sprache&gt;|&lt;hi|plain&gt;`; the anchor is the media hash (`h:<hash>`, F-M194), for a pack the series with its season (`i:<imdb>#S<saison>`), because the same series can have a pack in every season. The language belongs in it (a file has several tracks per source), the HI statement as well — and it is **normalized** in the key (ISO 639-2, F-M557), so that a caller with `EN` and one with `ENG` mean the same row. **F-M669** adds a sixth member for `track`/`track-hi`, the position; `file` and `pack` stay without.

(3) **The row carries** what the requirement names: `MediaHash`, `ImdbId`, `TmdbId`, `Source`, `Kind`, `Language`, `Season`, `Episode`, `ClosedAt` (the verdict „closed"), `Fingerprint` and `MtimeUnix` (only `file` and `pack`), `SourceId` (the id **at the source**), `RefusalReason`/`RefusedAt` (refusal) and `HearingImpaired` (three-valued, F-M285). For `pack` it is the only field that separates the normal from the SDH pack of the same season. Without it both would fall into **one** row.

(4) **The adoption of the plugin database runs ONCE at start, BEFORE the plugin database is opened** (`SourceMatrixMigration`, called in the constructor of `ScribeDbContext` before the context is built). It first backs up the whole file under `<dateiname>.bak-<yyyyMMdd-HHmmss>` (after a WAL checkpoint, F-M326) and then fills: `SubdlUploadedAt`/`TvSubtitlesUploadedAt`/`MoviesubtitlesUploadedAt` → `Kind = track` or `track-hi` with `ClosedAt`; `…DownloadId` → `SourceId` of the same row. `CompleteSubdl`/`CompleteTvSubtitles`/`CompleteMsub` → `Kind = file` with `ClosedAt`; every `packs` row → `Kind = pack` with `Fingerprint`, `PackId` → `SourceId`, refusal, `UploadedAt` → `ClosedAt` (the old row knows no mtime, `MtimeUnix` therefore stays empty — nothing is invented).

(5) **Nothing is deleted** — in step A the wide columns stayed column by column and readable, as a fallback level until the readers are converted (F-M327). **Superseded by F-M668:** since 12.3.0.0 the adoption lays the old columns down in the same transaction; there is exactly one truth (the matrix) and exactly one backup copy (the backup file).

(6) **The adoption does not hang on the schema marker but on the state of the file:** it runs as long as an old column still stands in it (F-M668). Creating and filling sit in **one** transaction, so an aborted run leaves no half table and is caught up at the next start instead of counting as "done" forever; a second start finds filled rows and does nothing (idempotent). The backup arises only when the table was missing; on the path of the format change (F-M637) it is omitted altogether, because the predecessor file stays put after F-M638 and is the backup itself.

(7) **The fallback was ONE read path** (`SourceMatrix.TrackMark`/`FileMark`/`PackMark`) — in step A it read the matrix and fell back to the old columns only when no row stood there. **Superseded by F-M668:** the readers ask the matrix exclusively; a column branch no longer exists.

(8) **Schema 18** (`CurrentSchemaVersion`), DDL and indexes beside the other tables (`ScribeDbContext.SourceMatrixDdl`, indexes on `MediaHash`, `ImdbId`, `TmdbId`, `(Source, Kind)`).

(9) **Limit of that step, honestly:** in step A the pipeline kept writing the old columns, only the migration run filled the matrix, and no reader of the pipeline had been converted. **That is done with 12.3.0.0 (F-M668).** Checked by T334.

**F-M668 [D]:** **The mark matrix is the ONLY truth for source verdicts — the wide columns are removed from the live file, and there is no second data plugin database.** Concept decision: **No duplicate data store** — the migration produces only the new structure, and the old store survives only as a **backup copy** (the backup file), never as a second truth in the live database.

Rule: (1) **Write only into the matrix.** Every operation that sets a source verdict writes a row into `source_matrix` — and as the **only** path.

In detail, first the upload/download evidence of a track (`Kind = track`/`track-hi`, `SourceId` = fetch id, `UploadId` = submission id, `ClosedAt` = submission time).

Then the belonging verdict of a file (`Kind = file`, `ClosedAt` + the track signature as `Fingerprint`) and the pack verdict (`Kind = pack`, `ClosedAt` = acceptance, `SourceId` = pack identifier, `Fingerprint` = member hash, `RefusalReason`/`RefusedAt`/`RefusalSignature` = refusal). There is **no switch and no double write**.

(2) **Read only from the matrix.** `SourceMatrix.TrackMark`/`FileMark`/`PackMark` are the only read paths; they have **no** column branch any more. If the row is missing, the source is open — that is the answer, not a fallback.

(3) **The old columns fall.** The adoption (`SourceMatrixMigration`) first backs up the whole file under `<dateiname>.bak-<yyyyMMdd-HHmmss>` (WAL checkpoint beforehand, F-M326), then fills the matrix **and** removes the wide columns in **ONE** transaction. On `embeds` and `sidecars` the nine mark columns per source and direction (`…DownloadId`, `…UploadId`, `…UploadedAt` for `subdl`/`tvsubtitles`/`moviesubtitles`), on `media` `CompleteSubdl`/`CompleteTvSubtitles`/`CompleteMsub`/`CompleteSignature`, on `packs` `PackId`/`Fingerprint`/`RefusalReason`/`RefusedAt`/`RefusedSignature`/`UploadedAt`. If anything fails, the transaction rolls back: matrix and columns unchanged, the backup stays put. The condition is the **plugin database** ("an old column still stands"), not the marker — an aborted run is caught up at the next start, an adopted one does nothing (idempotent).

(4) **What the `packs` row still carries are MEMBERS, not a verdict:** release, episodes, absorbed individual ids and the source in the key. `GetPack` merges both into **one** answer (`PackView`), so that no reader has to ask two stores.

(5) **The granularity is the POSITION (F-M669, since 12.3.0.1):** a track mark applies per (medium, source, language, variant, **position**) — two tracks of the same language are TWO marks with their own ids and their own timestamp. Until 12.3.0.0 the key condensed them into ONE mark (the later timestamp won, 63 positions of the jf-test plugin database disappeared); the gate therefore asked per pair and could regard a second, never sent track as done. Now the gate asks the mark of the track it wants to send (`IsUploadedToPosition`); the pair question (`IsUploadedTo`/`TrackMark` without position) remains only for callers that have no position in hand (loose subtitle lists, content questions), and then answers „any position of this pair".

(6) **Three additive fields, schema 20:** `UploadId` (the submission id beside the entry id — the two number ranges are different, F-M435), `RefusalSignature` (the season signature of a refusal, F-M650) and `SubPos` (the track position, F-M669; null for `file`/`pack`, −1 for a loose subtitle). All three are pulled in at open when a file already carries the table from 12.2.0.28 or 12.3.0.0. The marker rises to **20**, because afterwards the file no longer has columns that an older build expects.

(7) **Untouched:** `NormaliseLanguages()` (language conversion at open), the origin (`Origin`/`OriginId`) and the pack plugin database of the pack phase (`PackSeasonStore`). `SubdlId` on the row and the evidence table `source_marks` (F-M628) still stood here as "untouched" until 12.3.0.1 — **they have been dropped with F-M670**: they too were a second plugin database of the same statement. Checked by T334.

**F-M669 [D]:** **The track position stands in the key of the track marks — one row per (medium, source, language, HI, position); `file` and `pack` carry no position.** Concept decision: the track position stands in the key as well. Until schema 19 one track mark applied per (medium, source, language, variant): two tracks of the same language were condensed into ONE mark, the later timestamp won with its ids. The upload gate could regard the second, never sent track as done.

Rule: (1) **The key** reads `<Anker>|<Quelle>|<Art>|<Sprache>|<hi|plain>` and ONLY for `track`/`track-hi` additionally `|<position>` (`SourceMatrix.KeyOf` with `subPos`).

(2) **Writers:** `MarkEmbed` writes the position of the row (`SubPos`), `MarkSidecar` the fixed **−1** — a loose subtitle is a file beside the video, not a track inside it, and `ffmpeg` counts from 0, so −1 collides with no real track. Two loose subtitles of the same language share this mark, their content hangs on the content hash anyway. `file`/`pack` write without position.

(3) **Readers:** the upload gate, the accounting and the head exit ask `IsUploadedToPosition`/`SourceMatrix.TrackMarkAt` (language, HI, position); the content question reads the position of the row in which the content stands. The PAIR question (`IsUploadedTo`/`TrackMark` without position, „any position of this pair") remains only for callers without a position (loose subtitle lists).

(4) **Schema 20:** the column `SubPos` (`INTEGER NULL`) additive on `source_matrix`, in DDL, model and follow-up list. The adoption of an old plugin database takes the `SubPos` of the `embeds` rows along (loose subtitles −1), and a step-A matrix with pair-wide rows is thereby replaced by the position rows in the same transaction, so that no verdict stands twice.

(5) **Measured on the real jf-test plugin database:** 315 `embeds` rows yield **315 track marks** (before 252: 243 `track` + 9 `track-hi`), plus 2 loose subtitle marks (`track|tvsubtitles`, before 3 rows condensed into 2 marks) and 4 `pack|subdl`. **317 track marks instead of 252**, `track|subdl` 306, `track-hi|subdl` 9. The 63 condensed positions are back; the old columns are gone, exactly one backup file remains, a second start does not back up again.

(6) **A file from schema 19** (matrix without `SubPos`) keeps its pair-wide rows as a bridge; `TrackMarkAt` reads them until the next mark adds the position — no fallback to columns, only an older granularity of the same table. Checked by T334.

**F-M670 [D]:** **Every source id stands exactly once — the legacy table `source_marks` and the column `SubdlId` have been dropped; there is no second mark table and no id column on `embeds`/`sidecars`/`media`/`packs`.** Concept decision: **No duplicate data store.**

Rule: (1) **The matrix is the only place for source ids.** `SourceId` (the entry the subtitle came from) and `UploadId` (the submission) are the two fields; no other table and no other column carries a source id.

(2) **`source_marks` was the last remnant of the old format** and in the end had **neither reader nor writer** — only `SourceMarkStore.Attach`/`EnsureTable` and the `DbSet` kept it alive; all three are removed. Its key was `OwnerKey|Source`, and `OwnerKey` named the **owner row** (`embeds.Id` = `<medien-hash>|<position>`, `sidecars.Id` = content hash). The granularity was therefore the owner row — medium and position —, the **language came from the row**, not from the key. Precisely for that reason the adoption reads every old mark via its owner row (language, HI, position) and writes the track mark of the matrix: `UploadId` ← `UploadId`, `SourceId` ← `DownloadId`, `ClosedAt` ← `UploadedAt` (`CompletedAt` steps in when no submission time stands). Writing happens **only into empty cells** (COALESCE), so that no newer statement is overwritten; a mark that existed only in the legacy table is created, one without an owner row is **not** guessed (it counts as „without anchor" and lives on in the backup).

(3) **`SubdlId` was the last own id column** on `embeds` and `sidecars` (definition: „SubDL upload or download id this verdict refers to"). It is routed per row by its **status**: `Status = uploaded` → `UploadId` (the submission, F-M435), otherwise → `SourceId` (a find). If the target cell is empty or carries only a self-assigned mark (`own:…`), the real identifier wins; if it is occupied by **another** foreign identifier, the value goes into the second cell of the same mark (two number ranges of the same submission, F-M435). If that one is occupied too, **nothing** is overwritten — the value lives in the backup and the run counts it as a conflict.

(4) **The code does not follow the column:** the parameter `subdlId` on `MarkEmbed`/`MarkSidecar`/`MarkDownloaded` is removed (the callers pass `subdlUploadId` or `subdlDownloadId`).

`SetDeferredSubdlId` writes to the mark via `SourceMatrix.SetDeferredUploadId`, `RemapSubdlId` is `SourceMatrix.RemapId` (both cells, F-M435), `PruneOrphanedPacks` reads the carried identifiers via `SourceMatrix.TrackIds`, the old evidence in `IsUploadedTo`/`IsUploadedToPosition` is dropped (the adoption lifted it into the matrix). The diagnosis counts `WithSubdlId` via `SourceMatrix.CountWithId`. `QaFailTracker` stays untouched: its `SubdlId` is the burned **candidate** id in `rejected_candidates`, a plugin database of its own with a key of its own.

(5) **One sequence, one transaction:** backup → read old columns and remnants → fill matrix → take `SubdlId` and `source_marks` in → remove columns and table → commit. An abort rolls everything back; the condition is the **plugin database** (an old column **or** the legacy table), not the marker — which is why an already migrated file from schema 20 catches up as well. The marker rises to **21**.

(6) **Measured on the real jf-test plugin database (only via a copy):** 17 → 21: `source_marks` **318 rows** (315 `subdl`, 3 `tvsubtitles`) — **318 adopted, 0 new** (all were already covered by matrix rows), 0 without anchor, 0 conflicts. `SubdlId` **262 values** (252 `embeds` + 10 `sidecars`) adopted. Matrix before **no table**, after **327 marks** (`track|subdl` 311, `track-hi|subdl` 10, `track|tvsubtitles` 2, `pack|subdl` 4); afterwards no table `source_marks`, no column `SubdlId`, marker **21**, exactly **one** backup, second start without a second backup.

20 → 21 (real 20 plugin database from the pre-build `ad2844b`): **321 → 327** marks (the same six that only the id column carried), 318 old marks adopted, 0 new, table and column gone, marker 21, second start without a second backup.

(7) **What „exactly once" means, honestly:** 583 id cells in the matrix, 543 distinct values. **5 values stand at several marks** (one season-pack fetch id at 5 episodes, four entry ids at 9 rejected duplicates each plus the pack) — a source id names a **foreign** entry, and every local mark that refers to it carries it. No mark carries the same value in both cells.

(8) **What does NOT happen:** `Origin`/`OriginId` stay (origin, not a verdict), `rejected_candidates.SubdlId` stays, `PackSeasonStore` stays. Checked by T334.

**F-M671 [D]:** **The four levels carry their own random, immutable identifiers — `pack`, `file`, `track`, `sidecar`; the key of a track is `(FileId, TrackId)`, and a file's number range assigns track and loose subtitle numbers, never reused.** Concept decision: an identifier belongs to the thing, not to its content. Until schema 21 the identifier of a medium was its OSHash and that of a track „`<hash>|<position>`". Both were a statement about the **content**, not about the **thing**. A newly tagged pack got a new hash, hence a new identity together with new track identifiers, although file and tracks were the same.

Rule: (1) **Four tables, four levels.** `pack` (pack: title ids, season, language, HI, release, members, signature, timestamps).

The `file` table (the video file: `PackId`, path, file name, **OSHash as a property**, size, mtime, title ids, timestamps). The `track` (only embedded tracks: `TrackId`, `FileId`, `PackId`, track number, language, HI, forced, content hash, origin, timestamps) and `sidecar` (only loose subtitles: `TrackId`, `FileId`, track number, content, name, path, language, HI). `media`, `embeds`, `sidecars` and `packs` are absorbed into them; they no longer exist afterwards.

(2) **The display string `xxxx-yyyyy-zzz` carries the level, three columns are stored.** `xxxx` = pack (4 characters), `yyyyy` = file (5), `zzz` = track (3); an empty segment is the zero run (`0000`/`00000`/`000`). The alphabet is `1-9a-zA-Z` (61 characters, **without** the zero — it is pure filler, so that „empty" stays distinguishable from „assigned"): 13 845 841 packs, 844 596 301 files, 226 981 tracks per file. The identifiers are drawn cryptographically (`ScribeIds.NewSegment`) and **checked against the plugin database**; a segment is never re-assigned.

(3) **The key of a track is `(FileId, TrackId)`** — the three characters are **not** globally unique, only within their file. Uniqueness is carried by the display string, because it contains the `FileId`.

(4) **One number range per file, across `track` AND `sidecar`.** Every track of a file — embedded or loose subtitle — gets the next free number at first sight; it is never re-assigned and never reused (`TrackNumbers.NextFree`/`IsTaken`). A track that has vanished leaves a gap.

(5) **`FfmpegIndex` is derived at runtime**, never stored: it is the number of **embedded** tracks of the same file with a lower number (loose subtitles do not count — they do not lie in the media container), plus the way back `AtFfmpegIndex`. The existing single-computation logic (F-M441: one ffmpeg run per file; F-M658: pack tracks only) stays unchanged.

(6) **All files and all embedded tracks of the pack carry the pack id; a loose subtitle carries NO `PackId`** — its display is `0000-yyyyy-zzz`, and no packs are built from loose subtitles. The column does not exist in `sidecar`.

(7) **`OriginId` stays on the track for now.** The foreign id may only fall when `reference` (step B) takes it up; deleting it now would mean losing it. `Origin` (the source name) stays anyway — origin is not a verdict.

(8) **Migration 17 → 22 and 21 → 22:** first the backup file with timestamp (after WAL checkpoint, F-M326), then in **ONE** transaction create and fill the four tables. `SubdlId`/`source_marks`/wide columns have already fallen at that point through F-M668/F-M670. Then `DROP TABLE` of the four old tables and marker **22**. If something fails, everything rolls back and the next start catches up; if no old table is present any more, the run does nothing (**idempotent**). **Nothing doubled:** the old plugin database lives only in the backup file, and for the whole ascent **exactly one** backup arises (that of the mark adoption, if it ran in the same start). The run hangs on the **plugin database**, not the marker, and asks every old column before reading (`PRAGMA table_info`) — a 17 file knows neither `packs.Source` nor `LastSearchEmptyUtc`. If a track's file is missing, it is created as a file without a path („no track without a subject"); that is counted, not invented.

(9) **Measured on the real jf-test plugin database (only via a copy through the SQLite backup API):** schema 17, 44 `media`, 315 `embeds`, 13 `sidecars`, 4 `packs`, 318 `source_marks` → **44 `file`, 315 `track`, 13 `sidecar`, 4 `pack`**.

**328 numbers** (315 + 13, gap-free 0…n−1 per file, no duplication), 10 files with their pack, 0 orphans, marker **22**, no old table any more, **one** backup, second start without a second backup and without a second run. `OriginId` survives (checked on a copy with a set origin: `170678`/`3758706`).

(10) **Limit of this step, honestly:** the **source tables** `upload`/`reference`/`badapple` and the dissolution of `source_matrix` are **step B** and still pending — until then the matrix keeps carrying the source ids, and `OriginId` stays on the track. Chapter 14 is rewritten completely with step D. Checked by T335.

**F-M672 [D]:** **The source verdicts and identifiers lie in three tables — `upload`, `reference`, `badapple` —, each with ONE subject column.** Concept decision: the verdict belongs in an upload table, Bad Apple covers downloads that were not good, and foreign ids stand only in references. Until schema 22 everything stood in the mark matrix `source_matrix`; it has been dropped with schema 23, and so has `rejected_candidates`.

Rule: (1) **`upload`** carries the verdict of a submission — `SubjectId`, `Source`, `UploadedAt`, `RefusedAt`, `RefusedReason`; „closed" needs no field, closed means: `UploadedAt` is set.

(2) **`reference`** carries the identifiers — `SubjectId`, `Source`, `DownloadSourceId` (the foreign entry the content came from), `UploadedId` (our submission), `ItemId`, `SdId`, for tvsubtitles the address.

(3) **`badapple`** carries the bad download — `SubjectId`, `Source`, **`SourceId`** (the foreign number that the report names to the source), reason, `ReportedAt`, report state.

(4) **Foreign identifiers stand ONLY in `reference` and `badapple`** — in `badapple` expressly, because a report without the foreign number could not be filed; every other place works with our own identifiers. (5) A **good** download needs no row of its own: the track exists, and its origin (source name) stands on the track. (6) „Per source" means a **value** in `Source`, not a table per source — a fourth source creates rows, not a schema.

(7) **No shape test of a single source in the write path:** the former filter in the candidate path only let purely numeric identifiers through. That is the shape of a SubDL entry, whereas tvsubtitles delivers keys like `download-3234-2-en` and would therefore silently never have been recorded. What an entry is, knows the engine that fetched it; the writer only requires that an identifier is present and takes the source as a parameter.

**F-M673 [D]:** **The level is a property of the row, not of the display string: a file carries neither language nor HI nor a pack, and `track-hi` remains a KIND of its own.** As long as a file had a `PackId` field, it could belong to only **one** pack — although it contains tracks in many languages. The consequence was a field that pulled 315 tracks into the English HI pack and made the packs FR, PT and ES look empty. Rule: the **pack** keeps language and HI, the **track** keeps language and HI, the **file** keeps neither and **no** `PackId`. `track-hi` is and remains a kind of its own beside `track`, because a verdict about the HI version is not the same as one about the normal version — a flag alone would have made both mergeable again.

**F-M674 [D]:** **A loose subtitle carries a leading `&` in the third field — `#yyyyy&zzz` against `#yyyyy#zzz` of the embedded track.** The `&` is not a character of the alphabet, so it can never be confused with an embedded track of the same file. The shared number range of a file remains untouched by that. Thereby the kind of a subtitle is readable from its identifier, without consulting a second table.

**F-M675 [D]:** **The identifier IS the readable text — there is no separate display column.** `[xxxx]` pack, `#yyyyy` file, `#yyyyy#zzz` embedded track, `#yyyyy&zzz` loose subtitle. The file identifier sits inside the track and loose subtitle identifier and is therefore not kept as a column of its own (`ScribeIds.FileOf` reads it out). An additional `Display` field would be a second truth about the same thing and has fallen away again with schema 23.

**F-M676 [D]:** **The pack keeps its membership — `pack_track` (pack identifier, track identifier), real foreign keys, indexed; no `PackId` on track or file.** The array „my tracks" is stored relationally as a mapping table, so that „which tracks does pack `[aB3x]` have?" and „in which pack sits `#3CWmV#ZE6`?" can be answered in one go. The mapping arises during the ascent and during the migration run via **series, season, language, HI**. Measured on the live test plugin database: english+HI **9** tracks / 9 files, french **25** / 9, portuguese **25** / 9, spanish **25** / 9, **84** rows in `pack_track`; before, all 315 tracks stood in the English HI pack.

**F-M677 [D]:** **The origin of a find stands as a source name on the track; the foreign identifier moves to `reference` — no value may be lost in the process.** `OriginId` on `track`/`sidecar` has fallen away with schema 23. The migration run takes every set `OriginId` into `reference.DownloadSourceId` and checks the number of identifier values before and after the ascent (on the live test plugin database: 583 cell values, **543** distinct, before as after). Two ascent paths are mandatory: **17 → 23** and **22 → 23**, both in ONE transaction after ONE backup with timestamp, plugin database-driven (not marker-driven) and idempotent; a second start does nothing and creates no second backup.

**F-M678 [D]:** **The season archive of tvsubtitles.net is fetched in a session that carries the cookies of the season page.** Finding: the download page answers without a cookie with 171 bytes „Your request could not be served … cookies disabled", with the cookies of the season page with 58.639 bytes and a valid ZIP (measured live). The German season pack `MobLand - season 2.de.zip` lies there with 3 files and 57,26 kB. Cause: the shared `HttpClient` keeps no cookie jar, `GetBytesAsync` sent only the referer — stage 1 of the tvst ladder therefore delivered `null` for **every** language (826 refusals in the run of 22.11.), and the single path of the episode page had to step in. Rule: the `Set-Cookie` headers of the season page are read and sent along as a cookie header on download — through the **same** `HttpClient`. That the fixture handlers of the suite keep taking effect (an `HttpClient` of its own let the suite run on 0 fetches and was dropped).

**F-M679 [D]:** **A bad download is a SINGLE sub — `Member` names it and belongs to the key of `badapple`.** Concept decision: the grain is the single subtitle, both for an individual download and inside a pack. Until schema 23 the key was (SubjectId, Source, SourceId): the foreign ENTRY. A season pack, however, is an entry with many subtitles — a broken sub from it burned the whole pack, and two broken subs of the same pack were one row that lost the second.

Rule: (1) **The field.** `Member` is the name of the subtitle WITHIN the foreign entry (e.g. `MobLand - 2x05 - Birdland.WEB.NeoNoir.en.srt`), empty for an individual download — there the entry WAS the sub.

(2) **The key** reads (SubjectId, Source, SourceId, Member). Two broken subs of the same pack are two rows; a second report of THE SAME sub stays an update, not a second row.

(3) **The ascent 23 → 24** rebuilds the table. SQLite cannot change a primary key.

Step by step: rename the old table, create it anew from the current DDL, copy the rows back with `Member = ''`, drop the old one. The indexes are laid only afterwards (during the rebuild their names are taken at the renamed table, a `CREATE INDEX IF NOT EXISTS` before that would be a silent failure). `ReportState`, `ReportedAt` and `Written` are COPIED ALONG and not derived anew — otherwise every adopted row would suddenly be the newest in the consumer's 50-row pruning. One transaction, one backup with timestamp, plugin database-driven (`PRAGMA table_info(badapple)` for `Member`, NOT the marker) and idempotent: a second start does nothing and creates no second backup. The paths 17 → 24, 22 → 24 and 23 → 24 all end here.

(4) **Limit of this step, honestly:** the write path does not pass the member yet — both callers (`QaFailTracker`, `ContentHashRegistry`) call `RecordBadApple` without it, and the parameter is deliberately optional so that they compile unchanged. Until then every row writes `Member = ''`, i.e. „the sub is the entry"; the wiring in the download path follows. Checked by T337.
**F-M680 [D]:** **Season packs arise only in a full, refetch or manual run — and for all sources under the same conditions.** Concept decision: season packs arise only in a refetch or manual run, never in an arrival run, and the same holds for all sources. The finding that triggered the rule: the arrival run on 21.11. at 09:58 built **two SubDL packs** (MobLand S02, regular 524.500 B and HI 574.748 B), while tvsubtitles.net was excluded in the **same** run with „stays with a full run".

Rule: (1) The branch F-M424 („the arrival run packs its own episodes") has fallen without replacement; an arrival run gets **no** pack scope, says so in one line and uploads individually. (2) The source special case in the pack slot has fallen — which sources stand in the slot is answered **solely** by the source switch (F-M495), the same for every run; the slot reports symmetrically `subdl, tvsubtitles`. (3) Fallen along with it, because without a caller: the arrival-specific version of the F-M426 question and the arrival-scoped shortening of the pack plan; F-M426 still applies, now at **one** place in the full run.

(4) **Side finding, fixed along with it:** the id builders are idempotent. The ascent of a schema-23 plugin database wrapped the already **readable** identifiers a second time (`[FrEI]` → `[[FrEI]]`), the foreign key of `pack_track` made the transaction fail and the plugin database stayed behind half way, while the marker already said 24. Whoever passes a finished id in gets it back. Checked by T337 and the suites `seasonpack`/`packretry`.
**F-M681 [D]:** **The matrix „source × form × language × direction" is run for all three sources — 96 cells, each checked against the same metadata set.** Concept decision: the test programme covers every source in four forms — individual non-HI, individual HI, pack non-HI, pack HI — each in different languages and both directions, with metadata (ids, IMDb, TMDb, comment fields).

Rule: (1) **The scope.** Three sources (`subdl`, `tvsubtitles`, `moviesubtitles`) × four forms (individual non-HI, individual HI, pack non-HI, pack HI) × four languages (EN, DE, FR, ES; the plugin database carries ENG/GER/FRE/SPA for that) × two directions = **96 cells**. The cell count is itself a check (3 × 4 × 4 × 2 = 96), and a cell key that occurs twice is a failure.

(2) **What is measured per cell.** The own identifiers in their form (`[xxxx]`, `#yyyyy`, `#yyyyy#zzz`, `#yyyyy&zzz`, no double mark), language and HI on **pack and track** (the file carries neither and no pack), IMDb **and** TMDb on file **and** pack. Per direction the evidence (`reference.DownloadSourceId` in the shape of the engine on download, `upload.Source` + `UploadedAt` and the own submission identifier on upload), the membership in `pack_track` (or its absence for an individual sub), the comment field against `UploadComment` **without** the plugin version. Refusal/bad download (`RefusedAt` without `UploadedAt`; `badapple` with source, foreign identifier and `Member`).

(3) **the plugin database is real, the HTTP stretch only partly.** Every cell writes through the production writers (`ContentHashRegistry.MarkEmbed`/`RecordPack`/`RecordPackRefusal`, `SourceStore.AddMember`/`RecordBadApple`) into a `ScribeDbContext` in the temp directory; the comment arises in the real builders (`TvSubtitlesClient.BuildUpload`/`BuildSeasonPack`, `MovieSubtitlesClient.BuildUpload`). The HTTP stretch is covered **only** where fixtures lie — tvsubtitles.net: episode page (`fixtures/episode-31911.html`, entries en/fr/hu) and the season handle; moviesubtitles.org: film page (`fixtures/movie-24811.html`, entries en/br). **DE and ES run without HTTP**, SubDL **entirely without** (the API needs a token; the transport is covered by `apiclient`). That is a named gap, not coverage.

(4) **moviesubtitles.org carries no seasons.** The 16 pack cells of this source check the source itself — the season search of the page is empty, its upload endpoint refuses a SeasonPack ticket, nothing is written — instead of skipping the cell.

(5) **Finding, fixed along with it:** `pack.TmdbId` was a column without a writer; `RecordPack` never took the identifier, which is why it was missing on **every** pack. `ContentHashRegistry.SetPackTmdbId` now carries it in afterwards, and both pack writers (`SeasonPackCoordinator`, tvst pack endpoint) call it with the identifier they have in the ticket anyway; `RecordPack`/`RecordPackRefusal` no longer delete a known identifier. Checked by T338.
**F-M682 [D]:** **The boundary between plugin and source engine: four orders, capabilities instead of names, three boundary rules — and exactly ONE wiring point.** Concept decision: all communication with SubDL, tvst and further sources rests on standardised interfaces and methods — the engine handles the source, and the 4 orders are its only functions. Until here 37 places stood in the pipeline that recognised a source by name; this chapter draws the boundary at which that ends.

Rule: (1) **Four orders, no fifth.** `ISourceEngine` (`Sources/SourceContract.cs`) has exactly four public methods.

**Search**: the request carries EVERYTHING we know about the title — IMDb identifier, TMDb number, name, original name, year, series/film, season, episode, content hash — plus the desired languages in our ISO-639-2 spelling, the HI variant and whether a pack is wanted. The answer is a candidate list, every candidate with an opaque handle, language, HI, release name and pack membership. A source without an identifier may make arbitrarily many fetches within ONE search order — find series, season, episode page, entries).

**Fetch**: handle, for packs episode and member name → bytes, name of the subtitle in the pack, foreign entry identifier for `reference`, content hash.

**Send**: bytes, language, HI, release name, target from series/film + season + episode, the comment built by the PLUGIN, individual or pack, member list → one of the four outcomes (F-M685) + reason + own submission identifier, if the source gives one.

**Report**: foreign identifier + kind + text → THE SAME outcome as on send, F-M685).

SubDL answers an upload with `{"success": true}`, tvsubtitles.net with a page that says „thank" — **neither** names a report id; the field for it is optional and **no** column is created for it. The normalised faults of the boundary are NotFound, RateLimited (+ stated wait time), AuthFailed, Rejected (+ reason), Transient and NoSearchBase.

(2) **What the engine declares about itself**, instead of the pipeline comparing a name: `Capabilities` with key and display name, what it can do (upload, report, HI, season pack, create titles, search via IMDb/TMDb/name/hash), its rate limits, how the source names them itself. The mapping of our ISO languages onto its codes — as a query via the source's own language bridge, so that no second list arises. The values describe the engine **as the plugin has wired it for this run**: the capability „season container" is already the answer to „does this source stand in the pack slot?" (F-M654/F-M680), the operator's switch is accounted for in it.

(3) **Three boundary rules, measurable instead of descriptive:** (a) no file under `Sources/<Quelle>/` names `Data.` or `Pipeline.`, the plugin database (`ScribeDbContext`, `SourceStore`, `ContentHashRegistry`), our identifier builders (`ScribeIds`, `MarkKind`, `PackEntity`, `PackKey`, `OwnUploadId`) or one of our marks — the engine does not know the plugin database. No file in `Pipeline/` or `Data/` names an engine namespace (`Sources.Subdl`, `Sources.Tvst`, `Sources.Msub`) or an engine class, only the contract. (b) The plugin passes in the `HttpClient`, the shared archive cache, the logger, the timer and the settings together with access (`SourceEngineContext`). The engine creates **no** HttpClient of its own and no cookie jar of its own — precisely on that the tvst season fetch failed (own client, own pack, the test suite ran on 0 fetches). (c) the plugin database and our identifiers (`[xxxx]`, `#yyyyy`, `#yyyyy#zzz`, `#yyyyy&zzz`) do not occur in the engine; only neutral data crosses the boundary — no HttpClient, no JSON, no source URL, no foreign identifier shape.

(4) **The registry** (`Sources/SourceRegistry.cs`) builds the engines from the configuration and hands them out as a list, with lookup by key; no reflection scan, no dependency-injection wiring, no service locator.

(5) **Exactly ONE wiring point**, so that the adapters are not dead code: the question „which sources stand in the pack slot?" runs via the registry and its capabilities (`PackSlot()`), no longer via two hard-wired names. The answer is **unchanged**: without the tvst upload switch `subdl`, with it `subdl, tvsubtitles` — the same list in the same order, evidenced by the suite.

(6) **Limit of this step, honestly:** the boundary together with the three adapters is built and wired. Search, fetch, send and report are still called by the existing paths (download pipeline, upload pipeline, endpoints), and the remaining source switches (state: 37 before this step) stay where they are. Behaviour does not change — the version stays 12.3.5.0, the schema 24. Checked by T339.
**F-M683 [D]:** **Every source declares its settings itself — a field descriptor in the contract, a file of its own per source, an endpoint for reading and writing.** Concept decision: the overarching settings stay; each engine declares flexibly showable properties and keeps a configuration file of its own — preferring season ZIPs is a property of SubDL.

Rule: (1) **The field descriptor.** `SourceField(Key, Label, Kind, Default = "", Required = false, Help = null, Min = null, Max = null, Options = null, VisibleWhen = null)`.

That carries a stable key (at the same time the storage name), German label, kind (`text`, `secret`, `number`, `switch`, `choice`, `url`), default, required yes/no, optional help text, limits of a number, options of a choice. Plus `VisibleWhen(Field, Value)` (show a field only when another field has this value; for a secret field the expected value is `gesetzt`). The ORDER of the declared fields is the order in which they appear. The fields are a PROPERTY of the contract (`Fields`), not a fifth order: methods and properties are counted separately (4 methods, 2 properties).

(2) **The fields in this step.** `subdl`: `apiKey` (secret, required), `account` (text, visible as long as a key is stored), `baseUrl` (URL, default `https://api.subdl.com` — the constant that carries the requests). Finally `preferSeasonZip` (switch, default on; takes effect in the search: if a pack is wanted and the switch is on, the season ZIPs stand in front, the remaining order stays that of the source). `tvsubtitles`: `baseUrl` (URL, default `https://www.tvsubtitles.net`) — nothing else, the page demands no access. `moviesubtitles`: `baseUrl` (URL, default `https://www.moviesubtitles.org`) and `access` (secret, empty today). All three addresses come from the constants of the clients, not from a second typed-out version.

(3) **One file per source:** `<Datenordner>/sources/<key>.json`, content `{ "schema": 1, "key": "<key>", …Werte… }` (switches as true/false, number as number, otherwise text), written atomically (temporary file in the same folder, then move) with permissions **0600** (folder 0700). A missing file means „empty values, the defaults apply". An unreadable, broken or other-source file makes EXACTLY THIS source „not configured" — with a plain-text hint, without an exception and without the other sources noticing anything of it. The file must name its source key itself, so that two engines never use the same file.

(4) **The endpoint:** `GET Plugins/SubdlSync/Sources` delivers per source `key`, display name, `eingerichtet`, `hinweis`, `file`, the capabilities (the truth values from `Capabilities`, rate, language bridge for DEU/ENG/FRA/SPA) and the declared fields in order with their values. For a secret field **never** a value stands in the answer, only `gesetzt: true/false` (value and default are `null`). `POST Plugins/SubdlSync/Sources/{key}` writes exactly this one source. Passed fields are written, an empty secret field means „leave unchanged", unknown fields are refused (400 with their names). Required fields are checked against the state AFTER writing, numbers against their limits, choices against their options, addresses against http/https. Both routes hang on the administrator rule `LocalAccessOrRequiresElevation` — accesses stand here, so not anonymous. A write touches no other source file (byte-identical, checked).

(5) **The transition without behaviour change:** at the first start `SourceSettingsSeed.Ensure` writes today's values ONCE into the respective file — API key and account from the plugin configuration, the three addresses from the client constants, everything else the default — and sets the mark `sources/.seed-1`. Afterwards the engine reads from its file (the SubDL key is set on the client, account and switches come from the file). A second start writes nothing; an EXISTING file is never overwritten, a deleted one stays deleted. The overarching settings (languages, rate, directions, packs, HI) stay untouched in the plugin configuration; the interface (`configPage.html`) stays unchanged in this step.

(6) **Limit of this step, honestly:** the addresses are read, shown and reported in a log line when they deviate from the address of this build — the clients still carry their address as a constant. Converting them is a step of its own, otherwise the transition would change behaviour. The version stays 12.3.5.0, the schema 24. Checked by T340.

**F-M684 [D]:** **The diagnosis becomes honest: the mass lines stand on a real debug level, the network work is bundled per run, the four orders speak, and every source carries counters — without the behaviour changing.** Concept decision: the mass lines move onto a real debug level, so that the diagnosis produces enough debug messages. The trigger was a measurement on the prod log of the same day: **32 988 lines**, of which **32 608** from Sub Scribe, and **not a single `[DBG]` line** — everything `[INF]` with the text marker `[D]`. The fattest items: 7 964 × `media upsert` (one line per record), 2 030 API calls + 2 030 answers of the search, 1 478 × episode page + 1 478 answers (7 016 pairs), 2 274 search stamps, 714 mark lines.

The NEW paths, by contrast, were completely silent (SourceSettings 0, SourceRegistry 0, engine orders 0).

Rule: (1) **Honest level.** The mass lines go from `LogInformation` to `LogDebug`, via the new `LogUtil.Debug`: the same text, the same `[D]` marker, the same condition (only when the own `LogMode` is Debug) — only the level is now the one that Jellyfin's Serilog filters can also switch off. Affected are 18 call sites.

**1** in `Data/ScribeDbContext.cs` (the line per record, `file upsert` — in the prod log still `media upsert`), **6** in `Registry/DownloadSearchTracker.cs` (every search stamp). **9** in `Registry/ContentHashRegistry.cs` (the mark lines: `embed`, `sidecar`, `subtitles uploaded-complete`, the two `forgot stale embed`), **2** in the pipelines (the call/answer pair per direction: `OnApiTrace` in download and upload pipeline). Milestone lines — start, end, result, refusal, error — stay `Normal`/`Warning` and therefore visible at every server level.

(2) **Bundle instead of repeat.** At the end of every run stands PER SOURCE a summary line on `Normal`, form `SubDL: 38 Aufrufe, 2 Seiten, 0 Fehler, 41 s` (for orders of the boundary additionally ` (5 Aufträge)`). It stands in the `finally` of both pipelines and therefore appears ALSO after an abort, then with the state up to that point. The individual pairs are preserved — on debug.

(3) **The four orders speak.** `AuditedSourceEngine` wraps every engine (one place instead of twelve). Per order a debug line with source, handle/identifiers, language, HI, duration and result (example: `[Sub Scribe-Q] subdl Sende — einzelne Datei x.de.srt, Sprache DEU, HI=nein, 21 458 Byte, The X s02e05 → angenommen in 1 200 ms`). Per error case a visible line stands on `WRN`.

Those cases are: no access/unreadable source file, refused field, brake WITH point in time (`Rate begrenzt: 90 s warten, nächster Versuch ab 12:44:10Z`), no search basis, „source cannot upload" (`CanUpload=false`), network. An unreadable settings file is no longer silent: `SourceSettingsFile.Load` reports it (source + reason, NEVER a value), and a refused input in the endpoint is reported by `SourceSettingsView.Save`.

(4) **Counters per source into the status light.** `SourceTraffic` keeps in memory per source: fetches, orders, pages, failures separated by rate/auth/network and additionally by Rejected/NoSearchBase/NotFound/ConfigFailed, last success (time), last error (time + short reason). It is filled at the place of the event, not by reading a log. The request is counted by the live channel of the clients (`NoteTrace`, live at the call, not afterwards from the file), the order by the wrapper, the broken file by the loader. An order counts ONE request (F-M682: the boundary counts orders), which is why the request lines of its engine are not counted twice during its run. `GET Plugins/SubdlSync/Sources` delivers the numbers per source in the field `counters`; none of it touches the plugin database (no table, no column, schema stays **24**). **Limit of this step, honestly:** the four orders are called by no pipeline today (F-M682 built the boundary, the wiring of the run is pending). The counters therefore fill in operation from the live channel of the existing clients, the order counters only with the wiring. The version stays 12.3.5.0. Checked by T341.

**F-M685 [D]:** **Both writing orders end in THE SAME outcome — four values, the same for send and report, and „busy" is an outcome instead of a side field.** Concept decision: both writing orders end in one unified outcome. Finding: for the same state of affairs two statements stood in the contract. On **send** a 429 was not an outcome but a side field — the answer read `NoVerdict` with `Fault = RateLimited` (SubdlEngine ~430/447, TvstEngine ~422-430, MsubEngine ~332-338); on **report**, by contrast, `Deferred` was a real outcome. Whoever read the answer had to read both, otherwise he took a 429 for „don't know" and wrote a wrong verdict.

Rule: (1) **One outcome, four values:** `enum SourceOutcome { Accepted, Rejected, Busy, Failed }` in `Sources/SourceContract.cs`, in that order. `Accepted` = the source has accepted; `Rejected` = it has finally refused (reason in the field `Reason`, in the source's PLAIN TEXT); `Busy` = temporarily not possible; `Failed` = network, login or unknown (reason in the field `Reason`). `SourceSendStatus` and `SourceReportStatus` fall away without replacement.

(2) **The wait time stands AT THE OUTCOME:** `SourceSendAnswer(SourceOutcome Outcome, string? Reason, int? RetryAfterSeconds, string? SubmissionId)` and `SourceReportAnswer(SourceOutcome Outcome, string? Reason, int? RetryAfterSeconds, string? MessageId)` — filled from `Retry-After` or the reset time of the source, `null` when the source names none (none is guessed). On send and report hangs **no** error field any more; **search and fetch keep their fault path** (`SourceFaultInfo` together with `RateLimited` + wait time) unchanged.

(3) **Each of the three adapters translates its client answer onto the four outcomes — according to what the sources really answer.** SubDL: `{"success": true}` → `Accepted`, `already-uploaded` and „duplicate upload: identical file already published" → `Rejected` (in the wording of the source), quota message/`429` with reset time → `Busy` (with wait time), timeout/network/abort → `Failed`.

tvsubtitles: page with „thank" → `Accepted`, „Your request could not be served … cookies disabled" and „season must be 1..22" → `Rejected`, `429` → `Busy`, `401`/`403`/`5xx` → `Failed`.

moviesubtitles: „film not uploadable" → `Rejected`, `429` → `Busy`, login/server → `Failed`. The translation stands per source in ONE pure function (`SubdlEngine.Translate`/`TranslateReport`, `TvstEngine.Translate`, `MsubEngine.Translate`), so that it can be checked without network; the names `NoVerdict` and `Deferred` no longer occur in the source text of `Sources/`.

(4) **Side channels of the pipeline catch up, without changing the behaviour.** Where previously onto `UploadVerdict.NoVerdict` or `ScribeReportOutcome.Deferred` was mapped, it is now mapped onto the new outcome: `Busy` means „again later", `Failed` means „do not book as a verdict, but log it". So that the mapping does not hang on the wording, `UploadResult` carries the two typed questions `TemporarilyBusy` (429/hour cap) and `Unanswered` (timeout/network/abort), and the report outcome of the client itself is called `ScribeReportOutcome.Busy` instead of `Deferred` — one word for the same state of affairs. The four orders are still called by no pipeline today; what has changed is the mapping, not the flow.

(5) **Log and counters:** the debug line of the wrapper (`Sources/AuditedSourceEngine.cs`) names the outcome in one word (`→ angenommen`/`abgelehnt`/`beschäftigt (30 s)`/`fehlgeschlagen`), `Busy`/`Rejected`/`Failed` stand visibly on WRN (for `Busy` WITH wait time and point in time). `Sources/SourceTraffic.cs` keeps `Busy` as its OWN counter beside the failures (a brake is not a disturbance). It remembers the last named wait time (`LastBusySeconds`) and names both in the summary line (`SubDL: 5 Aufrufe, 0 Seiten, 4 Fehler, 2 beschäftigt (45 s zuletzt), 7 s (5 Aufträge)`). The numbers also stand in the endpoint `GET Plugins/SubdlSync/Sources` as `counters`. None of it touches the plugin database — no schema step, the version stays 12.3.5.0. Checked by T342.

**F-M686 [D]:** **The lower direction runs over the four orders: search and fetch ask the engine, no longer the client — and what stays in the pipeline stays there.** Concept decision: the engine handles the source and the 4 orders are its only functions; the next step is the wiring, so the pipelines call the four orders. Finding: the boundary (F-M682) together with its adapters was built. It was not called by the download pipeline — it spoke ITSELF with the sources (2 × `ScribeApiClient.SearchSubtitlesAsync`, 1 × `endpoint.SearchAsync` per additional source, `DownloadV2FileAsync`, `endpoint.FetchAsync` in two paths, `ExtractArchiveMemberAsync` for the second member of a pack: **7 call sites, now 0**).

Rule: (1) **The search goes to the engine as an order.** `Pipeline/DownloadPipeline.cs` builds a `SourceSearchRequest` from what it knows about the title. That is the IMDb identifier, TMDb number, name, series/film, season, episode, desired languages, HI variant, pack wish, the search breadth (`EarlyStop`, F-M95), the year and the content hash (`OsHash`). For the film address it adds the number that the plugin database carries for this medium (`MediaNumber`, F-M507). With that it calls `ISourceEngine.SearchAsync`. The request arises at ONE place (`SearchRequestFor`). The engine of this source delivers `SourceCandidate` with handle, language, HI, release name, pack tagging and the details by which the pipeline ranks and books. These are season, episode, range, frame rate, fetches, uploader, file name, `EntryId` = identifier of the plugin database entry, `Address` = address of the entry, `ReportId` = identifier of the report path, `Members` = members of a pack).

(2) **The fetching goes to the engine as an order.** `SourceFetchRequest` carries the handle, for packs season, episode and member name; back come bytes, file name in the pack, the foreign identifier (for `reference`) and the content hash over exactly these bytes. The shared `ArchiveDiskCache` (F-M370/F-M665) is PASSED to the engine and not maintained twice: configuring, resetting and trimming stay the business of the run, reading and writing happens via the same instance. For the second member of a pack (F-M387) there is the second handle `u:<Adresse>`, which reads via the same archive path. What the pipeline then does with the bytes — storing, verdict, QA gates, loose subtitles, `reference.DownloadSourceId`, `badapple` — is unchanged.

(3) **What stays in the pipeline is what is overarching:** the order of the sources (configuration/`SourceOrder`), the language check, the HI split (`SplitByHearingImpaired`), the rate (`WaitForTransferSlotAsync`, the 30-minute cap). The idle state (`DownloadSearchTracker` with search stamp, empty-search postponement and `EmptySearchFactor`) and every verdict about a candidate. The engine decides none of that; it answers orders.

(4) **No silent behaviour change — evidenced instead of claimed:** with an identical fixture the old path (client or endpoint directly) and the new one (via the engine) deliver **the same candidates** (handle, language, HI, release name, pack, season/episode, identifiers, frame rate, fetches, member list). On fetching they deliver **the same bytes**, the same file name and the same foreign identifier. The check thereby found and fixed a real difference — the engine reported the language as `ENG` instead of `EN`, which would have rewritten every language statement in the plugin database. The language on the candidate therefore stays the spelling that the plugin database carries for this source.

(5) **Limit of this step, honestly:** send, report and the pack path (upload pipeline, `SeasonPackCoordinator`) stay unchanged. The source switches do not fall in this step. `SourceRegistry`/`ContentHashRegistry`/`SeasonPackCoordinator`/`UploadPipeline`/`QaFailTracker` still carry **35** places of the narrow count (unchanged from F-M682), the download pipeline **0** of them and **23** of the wide count (before 22 — the one new is the question about the format counter of this source). The version rises to 12.3.6.0 (behaviour-relevant), the schema stays 24. Checked by T343.

**F-M687 [D]:** **The upper direction runs over the same four orders: send, report and the pack go to the engine, no longer past it — and what is bookkeeping stays bookkeeping.** Concept decision: after F-M686, send, report and the pack dispatch run over the orders of the engines, no longer past them, and the routing switches fall. Finding: the orders were built and eloquent (F-M682), search and fetch went through them since F-M686 — the writing paths did not. The upload pipeline spoke ITSELF with the sources: `_api.UploadSubtitleAsync` (SubDL single), `endpoint.SendAsync` per address (film single, tvst single, tvst pack), `_api.UploadSeasonPackAsync` (SubDL pack) and in `UploadSinglesAsync` once more `_api.UploadSubtitleAsync` — **7 transport sites**, each with its own payload and its own reading of the answer. Plus `_api.ReportBrokenSubtitleAsync` at **2** places of the download pipeline (format reports, bad-apple row).

Rule: (1) **Send goes as an order.** A single upload becomes ONE `SourceSendRequest` — bytes, file name, language (ISO-639-2 as in the plugin database), HI, release name, target from series/film + season + episode, the comment (which the pipeline builds, `Api/Endpoints/UploadComment.cs`), `AsPack=false`.

The `SourceSendAnswer` is read at ONE place (`UploadResult.FromAnswer`). `Accepted` closes the verdict (`upload.UploadedAt`), `Rejected` refuses it and carries the plain text of the source (`upload.RefusedAt`/`RefusedReason`). **`Busy` is NO verdict** — the row stays open, the wait time stands at the outcome (`RetryAfterSeconds`) and the next run asks anew (F-M609/F-M649). `Failed` is likewise no verdict and is logged. „Already present" stays a SKIP with the short reason `duplicate-content` and the sentence of the source beside it, because the coordinator recognises its pack refusal by it (F-M644) and the single track is decided by content (F-M298). The pre-checks that only the run knows (TMDb entry, series/film number, title ladder, „identifier usable") stay where they were — they decide WHETHER sending happens, not HOW.

(2) **The pack goes as an order.** `SeasonPackCoordinator` still builds the pack itself (that is our work) and sends it with `AsPack=true` and the member list. Whether it is expected as a finished archive (tvst) or as member files (SubDL) is said by the capability `Capabilities.TakesPrebuiltPack`, not by the source name — no `if (source == …)` stands in the dispatch any more. The row of a tvst pack is now written by the coordinator (before by the endpoint, which had seen the zip itself): the same episodes from the same archive (F-M642), the same identifier `null` (F-M612).

(3) **Report goes as an order.** Both report paths call `ReportAsync` with the NEUTRAL kind of the defect (`SourceReportKind`); only its engine makes the category in the wording of the page out of it (`ReportKinds.Of` ↔ `SubdlEngine.Category`, lossless for the three assigned categories). `Busy` (429) means as before: the row stays `open`, no `ReportedAt`.

(4) **Bookkeeping stays bookkeeping.** `upload.Source`, `reference`, `badapple.Source` carry the source value as DATA; likewise the mapping table `Provenance`/`SourceOrder`/`UploadGate`, the plugin database column per source (`ContentHashRegistry`) and the mark fields (`MarkAcceptedSource`). These places are marked in the source text as bookkeeping (short comment with the reason), so that nobody takes them for forgotten switches: there the source name is content, not knowledge about the source.

(5) **No silent behaviour change — evidenced instead of claimed:** an A/B comparison over fixtures (old path = client directly, new path = order) contrasts per case **the same requests** (method, path, body field by field, boundary normalised) and the same reading. That is **12 send cases** (three sources × accepted/refused/busy/failed, single and pack) and **6 report cases** (two sources × three outcomes; moviesubtitles.org has no report path and answers `Failed` without a request). The check found **one real difference**: both sides of the tvst family answer a refusal with HTTP 200 and the sentence in the text. The old path read only the STATUS and booked „uploaded", the new one reads the text of the source (F-M685) and books „refused". That is the improvement this step demands (Rejected = verdict refused with source text) and it is expressly recorded in the comparison, not silently adopted.

(6) **Count before → after.** Routing switches (a source is called): **13 → 2** — the 7 transport sites and 4 name decisions about the pack form (`SeasonPackCoordinator` 714/910/1090/1358) have fallen. What remains is ONE pre-check switch in `UploadPipeline.SendToSourceAsync` (which title resolution this address needs) and ONE in `DownloadPipeline` (film sub-path, F-M686, outside this step). Bookkeeping switches (source name as value): **133 → 132 lines** in the nine files of the narrow count — they stay and are designated.

(7) **Limit of this step, honestly:** the two upload endpoints (`TvSubtitlesUploadEndpoint`, `MovieSubtitlesUploadEndpoint`) stay in the tree but have disappeared from the run; they still carry the payload checks of the clients and are checked directly by the suites. The buffer path of the coordinator (`AddAsync`/`FlushAsync`) still collects only for SubDL — it is not part of this step. The version rises to 12.3.7.0 (behaviour-relevant), the schema stays 24. Checked by T344.

**F-M688 [D]:** **The section „Quellen" draws one card per engine from what the engine itself declares — and saves per card.** Concept decision: every engine gets flexibly shown properties and a configuration file of its own, maintained on the page. Languages and rate limit stay overarching; preferring season ZIPs is a property of SubDL, and the HI adjustment is a property of the engine as well. This is the last step to F-M683: the details that the engines declare about themselves since F-M683 the operator gets to see and to maintain as well.

Rule: (1) **One section, one card per engine.** The configuration page carries the section `Quellen` (`<h3>Quellen</h3>`, element `id="SourceCards"`, in the tab `general`, because a block outside the tab packs would appear under EVERY tab — F-M265). The cards arise in the order and number that `GET Plugins/SubdlSync/Sources` delivers; the drawing code names **no** source name — if one occurred, the card would be built from our knowledge about the source and not from its schema. Per card: the display name of the engine, small beneath it its `key`, the state („aktiv" when `configured` holds, otherwise „aus") and the capabilities as **pure display** (no switches: what the engine can do, it decides, not the page).

(2) **The field kinds are those of the schema.** `fields[]` is drawn in the delivered order: `text` and `url` as input field, `secret` as password field with the statement „gesetzt"/„nicht gesetzt" — the value stands **never** in the DOM (the endpoint does not deliver it, and the page does not store it either). An empty secret field means „leave unchanged" and is not sent along. `number` with `min`/`max` from the schema, `switch` as checkbox, `choice` as selection list from `options`. `help` stands as a hint beneath the field, `required` is visible on the field (marker of the page plus `required` on the control).

(3) **`visibleWhen` takes effect immediately.** A field with `visibleWhen` appears only as long as the named field has the expected value. What is compared is the EFFECTIVE value of the field (text/number from the field, switch as `true`/`false`, a secret as „gesetzt"/„nicht gesetzt" — a stored OR a just typed one counts). It is evaluated on every keystroke and every toggle via a listener on the CARD, not only when the page is built: whoever types a key sees the dependent account immediately, without a reload.

(4) **Saving happens per card.** Every card carries its own button „Speichern", which writes **only** this source (`POST Plugins/SubdlSync/Sources/{key}`, body = the visible fields of the card; an invisible field stays in its file as it was). Afterwards the card is refilled from the answer and names the fields really written; a refusal (400, unknown field, required field empty, limit violated, invalid address) stands in the **plain text of the answer** at the card. The collective saving of the page (`updatePluginConfiguration`) stays unchanged for the overarching settings and is the only remaining call of this kind.

(5) **The old source fields have moved — one value, one place.** `Username`, `Password` and `ApiKey` (SubDL account) have disappeared from the page; they live as `account` and `apiKey` in `sources/subdl.json`, whose adoption ran at the first start (F-M683). The page no longer reads them from the plugin configuration and no longer writes them there (no `config.Username`/`config.Password`/`config.ApiKey` any more in the page script); the TMDb key stays where it is and stays required.

(6) **Overarching stays overarching.** Languages, rate, direction switches, pack switches, HI, order and `LogMode` stay unchanged in the previous structure; the new section does not replace them.

(7) **Limit of this step, honestly:** the capabilities and the success line carry the keys of the contract (`canUpload`, `canSeasonPacks`, `uploadsPerHour`, `DEU DE`) and the written field names — the operator has prescribed no German words for these details. The page invents none (a German legend is an order of its own). The diagnosis still reads the old fields: status lights (`ScribeStatusController`), quota fetch (`ScribeQuotaService`), the service registrar and the upload pipeline read `Configuration.ApiKey`/`Username`/`Password` from the plugin configuration that the page no longer writes — converting them changes behaviour and is a step of its own. The version rises to 12.3.8.0 (visible surface), the schema stays 24. Checked by T345.

**F-M689 [D]:** **The drawer understands the spelling of the endpoint — otherwise it draws empty cards.** Concept decision: the drawer reads the spelling of the endpoint. A screenshot of the prod surface after the first deploy of 12.3.8.0 showed three cards, each only with „aus" and „Speichern" — no name, no capability, no field. Cause: `GET Plugins/SubdlSync/Sources` answers in the spelling of .NET (`DisplayName`, `Configured`, `Fields`, `Key`, `Capabilities`), the drawer read that of JavaScript (`displayName`, `configured`, `fields`, `key`). Every field was `undefined`, and because `configured` was missing too, every source stood at „aus".

Rule: (1) **One translation at the boundary.** The answer is led ONCE through `subdlSourceAntwort` (`subdlSourceWert` reads both spellings, `subdlSourceFeld`/`subdlSourceEintrag`/`subdlSourceFaehigkeiten` build the small form from it); the drawer reads only that. Applies to the read **and** the write path, because both deliver the same form.

(2) **The language mapping stays data.** `languages` (`DEU` → `DE`) is **not** rewritten — its keys are content, not field names; a general lower-casing of all keys would have made `DEU` into `dEU`.

(3) **Checking happens by running, not by searching.** The fault was syntactically flawless JavaScript; a text check would never have seen it. `gui-structure` therefore runs the translation functions of the page with `node` against an answer in the endpoint spelling (both state values, all six field kinds, `visibleWhen`, capabilities, rate, language mapping) and compares field by field. If `node` is missing, that is a failure with reason — not silent.

**F-M690 [D]:** **The configuration page speaks English — the source section too.** Concept decision: the section is called Endpoints. The page is consistently English (Libraries, Target languages, Status, Statistics, Workers, Media files). A German section would be the only tonal difference.

Rule: (1) **The section is called `Endpoints`** — the operator's choice of the two names.

(2) **The words that a card writes itself are English:** `on`/`off` as state, `Save` as button, `set`/`not set` as statement about a secret.

(3) **The field labels and help texts come from the schema and are English as well** — they belong to the engine, and the engine does not know the language of the surface. That is why the condition value in `VisibleWhen` is language-neutral too (`"set"`, before `"gesetzt"`).

(4) **The field name `gesetzt`/`Gesetzt` stays** where it stands: it is the key of the endpoint, not a displayed word.

**F-M691 [D]:** **The direction switches of a source stand in the file of this source.** Concept decision: the upload and download switches of a source stand in the file of that source. Until then they lay as `SubdlUploadEnabled`, `TvSubtitlesUploadEnabled`, `MovieSubtitlesUploadEnabled` and the three download counterparts in the plugin configuration — at a place that belongs to no source. With four checkboxes in the upload and download tabs that nobody could read as „property of a source" any more.

Rule: (1) **Every engine declares its directions itself** — `uploadEnabled` and `downloadEnabled` as switches, with the operator's defaults (download on, upload off, F-M503), and as the **first** fields: the coarsest question to a source comes before address and access.

(2) **Only the directions that the source can serve.** tvsubtitles and SubDL declare both; moviesubtitles declares **only** the download — its upload is locked until the documented language mapping (F-M508), and a switch that may do nothing would be a claim.

(3) **The file answers before the old value.** `IsUploadEnabled`/`IsDownloadEnabled` remain the only readers; they ask `sources/<key>.json` and fall back to the old configuration value as long as the file says nothing — a plugin database from before F-M691 therefore behaves unchanged.

(4) **The follow-up runs once** (`SourceSettingsSeed.EnsureDirectionSwitches`, mark of its own `.seed-directions-1`): it writes the **today's** value into every existing file that does not yet carry the switch, leaves stored values and accesses untouched, and does not touch missing and broken files. Without it the card would show the default of the engine while the old value still applied — the surface would lie about a running direction.

(5) **Checking happens by running:** the checks drive all four stages — declaration, stored value, precedence of the file, follow-up — against a temporary directory.

**F-M692 [D]:** **The card stays sparse — the address is no setting, the badges are one line, a secret field looks filled.** Concept decision: the card stays sparse — the address is no setting, the badges reduce to one line, and a secret field looks filled and editable.

Three consequences: (1) **The address is no longer declared.** `baseUrl` falls out of all three declarations; the address stands as a constant in the client of this build, a setting for it would be a question nobody has to answer. `tvsubtitles` therefore declares only its two directions.

(2) **The badges are ONE line with at most three words** — `upload · report · packs`, only what changes the behaviour. Rate, language mapping and search key are machine details and belong in the log, not on the card; the carpet of checkboxes falls away. Part of this is that a capability that this build does not offer is none: `moviesubtitles` reports `CanUpload = false`, because its upload is locked until the documented language mapping (F-M508) — otherwise „upload" would stand beside a switch that does not exist.

(3) **A secret field is a visible, normal input field.** It carried `is="emby-input"`. An element created by JavaScript is thereby **not** upgraded (the upgrade applies only to markup that already stands at load), which is why the field stayed unformatted and practically invisible in the dark Jellyfin theme — „password entry not available". Now it carries only the class `emby-input` together with an explicit width, and a stored secret appears as the placeholder `•••••••• set — type to replace` (or `not set`), so that the box does not look empty („Api key not filled"). The value itself still stands nowhere in the DOM.

**F-M693 [D]:** **The four possibilities of a source are switches on its card — and the season ZIP wish belongs to tvsubtitles as well.** Concept decision: the four possibilities of a source are badges on its card — upload, download, packs, report — each with its own toggle.

Four consequences: (1) **Every engine declares four possibilities as fields of the group `badges`** — `uploadEnabled`, `downloadEnabled`, `packsEnabled`, `reportEnabled` —, in that order, and the card draws them as ONE row at the top instead of as rows: every name with its own switch. They are fields like any other (the same plugin database, the same reader, the same write path), no second way past the values. What a source cannot do, it does not declare: moviesubtitles carries only the download (F-M508). Thereby the pure badge line from F-M692 is gone again — it said the same without switches.

(2) **The two new switches take effect.** `CanSeasonPacks` and `CanReport` read the own file before any old value: a season-pack switch off takes the source out of the pack slot of the run (F-M682 then names it as excluded), a report switch off blocks the report path. A switch that only stands on the card would be a claim.

(3) **The season ZIP wish applies to tvsubtitles just as to SubDL.** The engine passes it into the search request (`PreferSeasonZip`), and the search takes stage 1 (the season ZIP) only when it is on — off means „the single path first", not „no containers". Measured: with it off the season page is not fetched at all and no ZIP is loaded.

(4) **The save button of a card looks like every other button of the page** (`raised button-submit`), and a stored secret stands as a placeholder at the field (`•••••••• set — type to replace`).

**F-M694 [D]:** **The SubDL card carries the three accesses that a human enters — API key, Email, Password —, and the Download and Upload tabs no longer describe the sources.** Concept decision: the three accesses stand on the SubDL card, and the Download and Upload tabs describe no source.

Three consequences: (1) **The account is called „Email".** Until here it read „Account"; the page thereby did not say what goes in — namely the e-mail address of the account. The key in the file stays `account`, because the plugin database hangs on it.

(2) **A password field exists again, and it takes effect.** Since F-M688 the page no longer wrote the old access fields; the password lived solely in the plugin configuration and was no longer reachable via the surface. Now the engine declares `password` as a secret field (visible when an API key is set). The engine sets it together with the e-mail on **the same client that the run uses** — the file beats the old value, the old value remains the fallback. A follow-up (`.seed-credentials-1`) fetches the existing password ONCE into the file; without it the card would read „not set", although the run very well works with a password.

(3) **The Download and Upload tabs name no source any more — not even in the sentence.** The hint about the cards and the sentence of the family („Series … tvsubtitles.net, films … moviesubtitles.org. TMDb decides.") are removed: what concerns a source stands in its card under **Endpoints**. A second description of the same thing at another place is exactly the kind of remnant that the operator has complained about twice. The order of the sources („Which source is asked first.") stays, because it concerns the run, not the source.

**F-M695 [D]:** **No field that queries nothing — and a button that gets upgraded.** Concept decision: no field that queries nothing stands on a card, and the save button carries the blue type used elsewhere on the page.

Two consequences: (1) **The field `access` falls out of the moviesubtitles card.** It was empty from the start, and its own help text said that the page demands no access (measured: `/login.php`, `/panel.php`, `/upload.php` answer with 302 to `/`). A field that queries nothing and achieves nothing is a question without an answer — the same rule that already removed the address (F-M692). The card of this source therefore carries exactly one field: its download switch. `HasAccess` remains as a property and answers `false`, because the report path asks it.

(2) **An element created by JavaScript is only upgraded when the identifier is present at creation.** `document.createElement('button')` plus a later `setAttribute('is','emby-button')` yields an ordinary HTML element: no Emby shape, hence not the blue of the other buttons. Correct is `document.createElement('button', { is: 'emby-button' })` — the helper `subdlSourceButton(tag, isName)` does exactly that, additionally sets the class itself and pulls `customElements.upgrade` along where the identifier is already known. The same upgrade goes to the fields of a card (`emby-input`), its checkboxes (`emby-checkbox`) and its selection (`emby-select`) — otherwise a card looks different from the rest of the page. An unformatted secret field is practically invisible in the dark theme (the finding „password entry not available" of F-M692 had the same cause).

**F-M696 [D]:** **Tick boxes click, and the kind of a source stands as a badge — from its declaration, not from its name.** Concept decision: every tick box of a card must click, and the media kind stands as a badge from the declaration — series, films or both — passed as a variable in case the seeder does not already decide it.

Two consequences: (1) **Fields of a card are not upgraded.** F-M695 had created every field with `createElement(tag, { is: … })`; that upgrades the element **immediately** — at a point in time at which `type`, `id` and `name` are still missing. The widget takes over a half-finished element, and part of the checkboxes could no longer be clicked. Correct is an ordinary element with the classes `emby-input` / `emby-checkbox` / `emby-select`: the shape comes from the classes, not from the upgrade. Only the save button is upgraded (F-M695) — it is blue because it is a real `emby-button`.

(2) **Every engine declares whether it carries series, films or both** (`ServesSeries`, `ServesMovies`), the answer of the endpoint carries both as a variable, and the card writes a badge from it into the head: „TV shows", „Movies" or „TV shows + movies". The statement comes from the capability of the source, not from a name comparison — the seeder still decides the kind today by fixed source names (`ScribeSeeder.cs:938`), but can read the variable; the conversion of the switch is thereby prepared, not completed.

**F-M697 [D]:** **A card talks little.** Concept decision: a card talks little — a description is a few words, reasons belong in the documentation, and the hint for an incomplete setup reads in English. The previous implementation got by with „Default: off.". The cards afterwards carried fully worded sentences („Send a whole season as ONE archive. Off means every episode goes on its own."). Now applies: **a description is a few words** — `Default: off.`, `Default: on.`, `From your SubDL account.`, `Used to sign in.`, `Only for uploads.`, `One request per season.` —, measured: none longer than 40 characters. Reasons belong in this documentation, not on the card. In addition the hint for incomplete setup: **„Not configured: 'API key' is empty."** instead of a German sentence with instruction — the page is English, and a German sentence in the middle of the card was the most conspicuous remnant of the old implementation.

**F-M698 [D]:** **The order of the sources stands on the General tab and is a list that grows with every number of sources.** Concept decision: the source order stands on the General tab and works for every number of sources, so the input field adapts as well.

Three consequences: (1) **No two-value box any more.** The setting `DownloadOrder` (`tvst first` / `SubDL first`) could not express a third source; the selection field is removed from the Download tab.

(2) **The list is the truth.** `SourceOrderList` carries the source keys in the operator's order (`subdl`, `tvsubtitles`, `moviesubtitles`), the run reads it via `SourceOrder.Sequence(config, isSeries)`. For a media kind every source only counts if it carries that kind (F-M696); a key of the tvst family is resolved onto the responsible page (series → tvsubtitles.net, film → moviesubtitles.org). Unknown keys fall away, unnamed sources are appended — nothing disappears silently. If no list is stored, the old setting still applies; it is stored at the first save of the page, then `DownloadOrder` is derived from the first list entry, so that the old readers see the same order.

(3) **The input field adapts itself:** the page builds one row per source from the answer of the source endpoint — name, kind (F-M696) and two arrows —, no fixed list on the page. A new engine thereby appears without a change to the surface.

**F-M699 [D]:** **The source key no longer stands twice beside the name.** Concept decision: the source key no longer stands twice beside the display name. The card head carried display name **and** key („SubDL subdl", „tvsubtitles.net tvsubtitles"); that said the same twice. The key remains reachable as `title` on the name, because one needs it when searching in the log or in the file — visible is only the name, beside it the state and (F-M696) the kind.

**F-M700 [D]:** **The status light of a source stands as a row of its own directly beneath the heading of its card.** Concept decision: the status light of a source stands as a row of its own, directly beneath the heading of its card. The `on`/`off` stood beside the name in the head row and was thereby part of a row that carries name and kind at the same time. Now it has a row of its own beneath the head (`.subdl-source-state-row`), before the switch row. The light still says the same: `on` when the file of the source is readable and no required field is empty (F-M683), otherwise `off` with the hint beneath it.

**F-M701 [D]:** **Without a stored list the order shows the old setting — not the order of the engines.** During checking in the browser (Chromium, real page with a stubbed endpoint) „1. SubDL" stood on the General tab. The run drove with `DownloadOrder = TvSubtitlesFirst`: the list had pre-filled itself from the answer of the endpoint and thereby concealed the truth of the run. That is the kind of lie that a single save turns into a wrong setting. Now applies: if `SourceOrderList` is empty, it is pre-filled from `DownloadOrder` (`tvsubtitles,moviesubtitles,subdl` or `subdl,tvsubtitles,moviesubtitles`) — display and run say the same. Measured after the correction: `1. tvsubtitles.net`, `2. moviesubtitles.org`, `3. SubDL`.

**F-M702 [D]:** **A card says only what a field is — nothing else.** Concept decision: a card says only what a field is — no help text, no placeholder, no set line, no request-per-season note on the 2 pack switches; the light answers whether a secret is stored. Thereby the last stage of the descriptions falls away, which F-M697 had still shortened to a few words. What holds: **no field of a card carries a help text**, and all engines declare `Help: null`. **Superseded by F-M745:** that sentence is lifted — any field may carry ONE line of at most 80 characters. The rest of this rule stands: no placeholder, no `set` line, no request-per-season note, and the light as the summary.

The value of a secret still never leaves the plugin (F-M683); on saving only a really typed value goes along, an empty field leaves the stored one standing. On the card there therefore remain: name, light, four switches, the field labels (`API key`, `Email`, `Password`, `Prefer season pack`) with `Required` on the required field, and `Save`.

**F-M703 [D]:** **The order stands directly beneath the heading „Endpoints", before the cards.** Concept decision: the source order stands directly beneath the Endpoints heading, before the cards. The block had wandered to the General tab with F-M698. It does not belong there, because it orders the sources and not the run. Now it sits in the section **Endpoints** immediately after the heading and **before** the cards — one row per source with name, kind (F-M696) and two arrows, stored in `SourceOrderList` (F-M698), pre-filled from `DownloadOrder` as long as nothing is stored (F-M701). On the General tab it no longer stands.

**F-M704 [D]:** **The kind of the source and its light arrive — on the card and in the order.** Concept decision: the kind of a source and its light arrive on the card and in the order. Three causes, each individually evidenced: **(a)** the list of capabilities (`SUBDL_SOURCE_CAP_KEYS`) stood **twice** in the page. The second overwriting won, and in it `servesSeries`/`servesMovies` were missing — the endpoint delivered the values the whole time, the page never fetched them. Now the list stands **once** and names every field that the page reads; the badge („TV shows", „Movies", „TV shows + movies") thereby stands on every card **and** in every row of the order, together with the light (`on`/`off`) of the same source. **(b)** the condition of another field („`VisibleWhen apiKey = set`") got a truth value instead of `set`/`not set` during the cleanup of F-M702 — thereby it was never fulfilled and **Email and Password stayed invisible**. The displayed card still carries no `set` (F-M702); the condition language of the schema stays neutral `set`/`not set`. **(c)** every row of the order carries the light as its own sign beside the kind.

**F-M705 [D]:** **Only what was moved is saved — and without movement nothing at all.** Concept decision: only what was moved is saved, and without movement no request goes out at all. Before, every save sent **all four switches** along, and the feedback named them all — „downloadEnabled" therefore stood in the list even when only another checkbox had been moved. The operator reads that as „it has switched itself back on". Now the body compares every value with what stands in the card (`data-value`, for secrets: empty = unchanged) and sends **only the deviation**. If nothing is moved, **no** request goes out and the card reports `✔ nothing changed`. The state of the checkboxes still follows solely the answer of the endpoint (measured: tick off → body `{"downloadEnabled": false}` → answer `Value = "false"` → checkbox stays off).

**F-M706 [D]:** **One light, right-aligned arrows, no required marker, dots in the secret field — and a feedback that names the state.** Concept decision: the light stands only in the order, the arrows sit right, the card carries no required marker, and the feedback names the new state. Five consequences: **(a)** the light stands **only** in the order. The row of its own beneath the card heading (F-M700) falls away — the same statement twice was a duplication. **(b)** the arrows of the order sit on the **right** (the free space lies before the first arrow).

**(c)** the card carries **no** required marker any more; that the API key is required is said by the light (`off` as long as it is missing, and the run leaves the source out) — the check in the schema remains (`Required = true`). **(d)** a secret field shows a **dot pattern** (`••••••••`) for a stored value, but no word and no value: the field looks filled, without `set` (F-M702 applies to the text, not to the dots). **(e)** the feedback of a save names the **new state** (`downloadEnabled: off`), not only the field name — „downloadEnabled" alone read like „switched on", even when something had just been unticked.

**F-M707 [D]:** **The block is called „Endpoint order", names no media kind and puts the light centred into a column of its own.** Concept decision: the block is called Endpoint order, names no media kind, and puts the light centred into a column of its own. Three consequences: **(a)** the heading of the block reads **Endpoint order**. **(b)** the row carries **only** number, name, light and the two arrows — the media kind stands on the **card** (F-M704), not in the order, because there it would be the same statement a second time. **(c)** the light gets a column of its own with fixed width (`3.2em`) and **centred** alignment, so that the values stand one below the other.

**F-M708 [D]:** **The feedback names the label and the state — „off" red with a cross —, and the light of the order follows participation, not completeness.** Concept decision: the save feedback names the label and the state; an off state stands red with a cross instead of a tick. Three consequences: **(a)** the message names the **label** of the field and the state (`Download off`, before `downloadEnabled: off` — the key with „Enabled" read like „switched on"). **(b)** a switched-off state stands **red with ✖**, a switched-on one green with ✔; **(c)** the light of the order hung on `Configured` — that is, only on the completeness of the file. Whoever switches **both directions** of a source off still has a complete file, and the light stayed `on` forever. The endpoint therefore delivers anew **`Participates`** (configured **and** at least one direction open, read from the same file as the run), the light hangs on it. After a save the order is redrawn from the answer — otherwise it would show the old state until the next load. The strip of tabs is redrawn from the same answer, and the tab that was open stays open; without that redraw the lamp on the tab kept saying `on` while the row above already said `off`.

**F-M709 [D]:** **Every switch of every source has a measurable effect — evidenced, not claimed.** Concept decision: every switch of every source is evaluated, in particular at the endpoints, and an off switch really is off. The check stands in the suite `sources` and drives a file of its own per source: **off** means **really off** everywhere. Measured: `moviesubtitles.org` with `downloadEnabled = false` → `IsDownloadEnabled(MovieSubtitles) = False`, and the other sources stay on (`subdl = True`); `tvsubtitles.net` with `downloadEnabled = false` → `False`. `packsEnabled = false` at SubDL → `CanSeasonPacks = False`, the pack slot carries only `tvsubtitles` and `PackSlotExcluded()` names `subdl` as the reason; `reportEnabled = false` → `CanReport = False`; `uploadEnabled = false` at tvst → `IsUploadEnabled(TvSubtitles) = False`. The reading places are: directions via `PluginConfiguration.IsDownloadEnabled/IsUploadEnabled` (gates in `DownloadPipeline.SourceAllowed`, `UploadPipeline.SourceAllowed`, `SubtitleEndpoints.EnabledDownloads/EnabledUploads`, scheduler, seeder, `WorkerOffState`), pack via `CanSeasonPacks` → `SourceRegistry.PackSlot()` → `UploadPipeline.PackSlotSources()`, report via `CanReport` → `AuditedSourceEngine.ReportAsync`. **Timing behaviour:** the direction switches are answered by the file on **every** call, pack and report are read by the run when it builds its registry — hence from the **next** run, without a restart.

**F-M710 [D]:** **The block „Endpoint order" carries no description.** Concept decision: the Endpoint order block carries no description. The sentence „Asked in this order — the first one that has the subtitle wins." stood as `fieldDescription` beneath the heading. At that place only the name remains. The order itself explains itself through the rows (number, name, light, two arrows).

**F-M711 [D]:** **The last three „SubDL only" settings stand in the card of the source — the marker falls away.** Concept decision: the 2 remaining SubDL-only settings go into the card of the source, and the SubDL-only badge falls away. Meant are the three switches that as the only ones still carried a source marker (the two daily-limit switches and the AI switch). They are now **fields of the SubDL source**: `uploadContinueAfterLimit`, `downloadContinueAfterLimit`, `allowAiTranslatedSubs` in `SubdlEngine.Declared` (`continue … (upload)`, `… (download)`, `Allow AI translated subs`), defaults `true`/`true`/`false` = the previous old values. Reading happens via the properties of the configuration, which ask **the file first** and otherwise fall back to the XML value (as F-M691); a follow-up (`EnsureSubdlSwitches`, mark `.seed-subdl-switches-1`) writes the value valid today once into the existing file. In the web surface the three boxes together with the marker are **deleted** (`SubDL only`, `.src-badge`, `.src-subdl`, `.src-tvst` no longer occur in the page), and the card stays sparse: label plus switch, without description.

**F-M712 [D]:** **moviesubtitles.org carries an upload switch — default OFF.** Concept decision: moviesubtitles.org carries an upload switch, default off — the upload path exists and files have already been handed over there. The upload of this address is not a plan but evidenced: the path stands in the code since F-M508 (stage 2b) (`MovieSubtitlesUploadEndpoint`, `MovieSubtitlesClient.BuildUpload/UploadAsync`, `FilmUsableForUploadAsync`), `SubtitleEndpointCatalog.HasUploadPath` carries the source. Files have already been handed over there. Thereby `CanUpload = false` (F-M692) was wrong: the lock is not the capability but the switch. New: `MsubEngine.Capabilities.CanUpload = true`, and the card declares as badges **`uploadEnabled` (default `false`)** and `downloadEnabled` (default `true`) — so only whoever sets the switch uploads; `IsUploadEnabled(MovieSubtitles)` still falls back to `false` without a file value. The marker „SubDL only" is thereby needed nowhere any more.

**F-M713 [D]:** **An exhausted quota takes ONE source out of the run — not the run.** Concept decision: an exhausted quota takes ONE source out of the run, not the run. Before, the opposite applied: a search 429 with a used-up quota set `_stopRun` (DownloadPipeline.cs, read in the main loop), a download 429 likewise (F-M65). The upload broke off with „stopping upload run early" — although tvst and mst need neither SubDL's key nor SubDL's quota and could serve entries that lay on their page. New: **download** — all four end cases of the main search (key refused/403, persistent overload, daily quota with and without follow-up fire) report the source via `MerkeQuelleTot(MainSearchSource)` as failed, a download 429 via `MerkeQuelleTot(Subdl)`. `if (!hauptsucheLief)` continues with the remaining sources (`continue` instead of `return`), and the flag `_stopRun` has fallen without replacement. **Upload** — `SourceAllowed` now asks switch **and** failure (`_config.IsUploadEnabled(provider) && !_quellenAus.Contains(provider)`), both 429 branches (arrival run and daily limit) enter SubDL into `_quellenAus` and continue the run with the other sources. **Boundary:** the run still ends when **no** source is left (`_stopRunNoSources`, F-M592) — „only when all are gone is the run an abort".

**F-M714 [D]:** **The rule applies to every failure reason, not only to the quota.** Concept decision: the rule applies to every failure reason, not only to the quota — when one source fails, the others carry on. After F-M713 two cases in the upload still ended the whole run, although only SubDL was affected: a refused key (403, „upload stopped") and the hourly budget (500/h, „run stopped"). Both now take **only SubDL** out of the run (`_quellenAus.Add(Subdl)`, afterwards `SourceAllowed` says no) and the run goes on with the remaining sources; both messages say so in plain text (F-M714). The dry run and the hourly fire fall away in the process, because SubDL takes part again by itself in the next scheduled run. Unchanged: the run ends when **no** source is left (`_stopRunNoSources` F-M592 in the download, `alleQuellenWeg` F-M590 in the upload), and the season pack phase was always booked per source (`PackSend(source, …)`) — a refused pack at one source does not stop the other.

**F-M715 [D]:** **A source that does not answer is not asked again per track.** Concept decision: a source that does not answer is not asked again for every further track of the same run. In the download that already applied (F-M592: `MerkeQuelleTot`, a timeout takes the source out of the run for the rest of it). In the **upload** it was missing: the timeout did not end the run (F-M590, „the other source carries on"), but tvst was afterwards asked again for **every further track** with the full client timeout (90 s). New: the timeout branch enters the source into `_quellenAus` (one line in the log says it); `SourceAllowed` then says no, the rest of the run proceeds with the remaining sources. The next run asks it again. For the series that means: if tvst drops out, SubDL alone keeps searching and fetching; if SubDL drops out as well, the run ends (no source left).

**F-M737 [D]:** **The identity of a series is checked BEFORE its season is judged.** Finding on prod: `Brothers` S1 lay **complete with eight episodes** in the plugin database and nevertheless got no pack. The log names both halves of the contradiction. First `season pack — "tt6155146" S1 ["EN non-HI"] unchanged since the last judgement ("neither the store nor the files of every aired episode carry this language")`. Seconds later `ID conflict for "Brothers" (2026): Jellyfin says tmdb=66515 imdb=tt6155146, TMDb's title+year match says tmdb=250203 imdb=tt6773088 — using the TMDb id`. Jellyfin carries two series with this name (2015 and 2026) and had hung the series on the **2015** one — 262 aired episodes in season 1 against eight files, hence „not complete".

The identity check (F-M231) ran then only in the single path, **after** the pack phase; its verdict was thereby never visible for the pack, and the stored „no pack" (F-M604) hung on the wrong identifier. New: **before** building the season key the pack phase checks the identity of the series against TMDb — with the **year from the file name** (F-M231; Jellyfin's year comes from the same source that can be wrong), once per series and run. If TMDb meets another series under title and year, the judgement is made under **that** identifier: episode count, signature and stored verdict thereby hang on the series that the files really are. A correction is named (`identity checked before the judgement — "<Titel>" (<Jahr>) is <neu> on TMDb, the library carries <alt>`), no match leaves the identifier of the library standing. Checked by T397.

**F-M742 [D]:** **The light of the sources speaks English — and says only what the bars do not say.** Concept decision: the light speaks English with the old texts and has no thematic overlap with the bars. With the third contract (F-M730) the message texts of the inquiry had slipped into German, while the page otherwise speaks English: `angemeldet`, `erreichbar (HTTP 200)`, `Suche 201/2000, Downloads 50/50`. Now the **old English texts** of the former status check stand there again: `reachable`, `HTTP {code}`, `OK`, `wrong username/password`, `not authorized — check API key`, `rate-limited — try again later`, `Login failed: …`, `no API key configured`, `no account configured — uploading is off`. **No quota numbers in the light any more:** how much an account has used stands in the bars above (F-M727); the light only says whether the address answers. The same principle applies to two further UI texts in the status path (`not stored: no media hash and no unique path row in the store — the number applies to this run only.`, `no data folder — nothing can be stored`). Checked by T402.

**F-M741 [D]:** **The language statement of a source belongs BEFORE the collecting, not in the send step.** Finding: in the season path the question „does this source carry the language at all" (F-M538) stood only at sending. The collector saw only „this source does not have the container" and unpacked the **whole season** for a source that can never accept it. Afterwards the pack was discarded, and because nothing was ever **refused**, nobody wrote a verdict — the pair stayed **open forever** and did the same in the next run. Measured on one day: 633 lines „the collector builds it for tvst", of which **42 pairs in languages that tvsubtitles does not carry** (ID, BN, HR, …), and `tt32020831` **nine times per language**.

New: the sources without a pack are split **before** the collecting: the ones do not carry the language (they are no longer missing sources), the others collect. If no source remains, **nothing is unpacked** and the pair reports in one line „no source carries this language". The `HasPack` gate above it keeps its statement („already up as a pack") and only takes effect when really **every** source has the pack. The **single path** was already right: there `AnyUploadSourceStillWants` asks the statement per track position **before** the extraction (F-M586/F-M581), checked by T401.

**F-M740 [D]:** **A season can be taken out of the plugin database completely — counted before anything is deleted.** Concept decision: a season can be taken out of the plugin database completely, e.g. Brothers S01 — counted before anything is deleted. The plugin database carries no date for a season. A tangle: files carry track identifiers, tracks and loose subtitles hang on the files. The mark matrix hangs on the file **or** track identifier, pack and season verdict on the key `imdb|staffel|…`, the aired episode count likewise, the language refusals on the **media hash**, the title exhaustion on the key `quelle|titel`. Emptying a single table leaves the rest — and the next run behaves further as if the season were done. That is why **ten tables in one reckoning**, in the order of the dependencies (marks, loose subtitles and tracks before the files).

Tracks and loose subtitles are found via their **own identifier** (they carry the file identifier inside them, `#yyyyy#zzz`, F-M676) — the column `FileId` does not exist, an access to it runs into the translation of the query and not into empty sets. The refusals are found via the hash of the files and tracks, not via guessed columns; the verdicts via the season prefix. `dry=true` **only counts** and deletes nothing, so that the numbers can be named before the deletion; deletion happens only in the second call. Checked by T400.

**F-M739 [D]:** **Season verdicts can be forgotten — and that without triggering an upload.** Finding: after an identity correction the stored „no pack" verdicts (F-M604) lie under the **old** identifier in the key `imdb|staffel|sprachtag` and are thereby unreachable — they block nothing. Grow along and make the table unreadable. The reset so far knew only `all`, `upload` and `download`; `upload` is unsuitable as a cleaning path, because it marks the episodes as „not uploaded" and thereby triggers the next upload (F-M192). New is **`scope=pack`**: without further detail **all** verdicts fall, with `&imdb=<kennung>&season=<n>` only those of one series and season. Log line and answer name the number of forgotten verdicts; the upload state remains untouched. Checked by T399.

**F-M716 [D]:** **„Already gone" is the goal, not an error.** Finding on prod: the database refresh broke off after 15 s with a `DbUpdateConcurrencyException` („expected to affect 1 row(s), but actually affected 0 row(s)"), the status showed `Database: fail`. Chain: `ScribeDatabaseRefreshTask` → `ContentHashRegistry.PruneOrphanedPacks()` → `SourceStore.ForgetSubject(db, pack.PackId)` → `ScribeSet.DeleteMany` → `SaveChanges`. Cause: `DeleteMany` also announced rows that were already marked as `Deleted` in the same context (the loop deletes first the pack row, then the evidence of the same subject; a `pack_track` can appear in two passes via `TrackId` as well as via `PackId`). The second DELETE hit 0 rows and EF throws for that. New: `DeleteMany` skips entries with `EntityState.Deleted`, and `ScribeSqliteContext.SaveChanges` catches a `DbUpdateConcurrencyException`, ticks the affected entries off as done (`Detached`), writes a warning line with the number and repeats the write. For a DELETE the intention is thereby fulfilled; for an UPDATE there is nothing left to write. No data loss, no aborting of the run any more.

**F-M717 [D]:** **The retry runs in a loop.** Measurement on prod (12.3.8.21): the refresh broke off again despite F-M716. The log shows both one after the other — „1 row(s) were already gone when the write ran … dropped as done and the write was retried" and directly afterwards the same `DbUpdateConcurrencyException` from `PruneOrphanedPacks` → `ForgetSubject`. Reason: the exception names only the entries of **one** command from the batch; the one retry hit the next row cleared by the cascade (`pack_track` → `pack`/`track`, `ON DELETE CASCADE`, `ScribeDbContext.cs:270–271`). New: `RetryAfterVanishedRows` ticks off the reported entries in every round (`Detached`) and repeats the write until no „already gone" row stands in the batch any more — limit 200 rounds, after that the last exception flies. Every round writes a warning line with number and round number.

**F-M718 [G]:** **The status lights stand in the order of their importance — TMDb first.** Concept decision: the status lights stand in the order of their importance, TMDb first. In the block **Status** the light of the key therefore stands at the top, without which neither download nor upload starts (**TMDb API**), beneath it the two SubDL accounts (Login, API), then the reachability of the websites (tvsubtitles.net, moviesubtitles.org). The order stands in the HTML (`#ScribeStatusBox`); the scripts access via the ids (`#TmdbApiStatus`, `#SubdlLoginStatus`, `#ScribeApiStatus`, `#TvstStatus`, `#MstStatus`) and are independent of the position.

**F-M719 [G]:** **The pack switch stands only at the source and means both directions.** Concept decision: the Upload season packs checkbox falls away; the pack switch stands only at the source and means upload and download for packs of that endpoint. Thereby the checkbox **Upload season packs** falls out of the upload section (together with the field `PluginConfiguration.UploadSeasonPacks`). The question „is packing done at all?" is now answered by the switch **Packs** of the source card (`packsEnabled`). If it stands open at a source, the run packs and addresses exactly this source (`PackSlotSources().Count > 0` in `UploadPipeline`). On **fetching** the same source demands a season archive only then (`PreferSeasonZip` applies only while `packsEnabled` is open). F-M641 (a source-blind switch for both sources) is thereby superseded; stored old values in the field `UploadSeasonPacks` are ignored on reading, because the field no longer exists.

**F-M721 [U]:** **One origin rule, arbitrarily many sources — the gate counts no source up.** Concept decision: after the withdrawn first attempt (F-M720), one rule instead of 3, formulated so that it also works with a fourth, fifth and further endpoint. F-M720 had put the rule per source as a line into the engine — three lines for three sources, that is the same counting at another place. F-M721 does it the other way round: the rule stays **one**, and `UploadGate.Decide(string? origin, IReadOnlyList<UploadTarget>)` runs through a **list** — no source name in the body. The list is built by `UploadGate.TargetsFrom(SourceRegistry, isSeries, switchedOn, alreadyThere, alsoTakes?)` from `sources.Engines`: per engine „can upload?", „carries this media kind?" (the source declares it itself, F-M696), switch, mark, language. The four call sites in the upload (`Trockenlauf`, `frühes Tor`, `Kette`, `Paar-Frage`) build their targets that way. A fourth or fifth engine that the registry builds is in **without a change to the gate** — only its mark in the plugin database and its switch in `sources/<key>.json` must know the new key.

**F-M722 [U]:** **The origin question has no target — it is asked once.** Concept decision: on F-M721, a sub or sidecar ever fetched by us from any of 1 to 10 sources is never uploaded — and the origin question has no target, so it is asked once. Justified: `Provenance.MayUploadTo(origin, target)` accepted a source key and threw it away with `_ = target;` — a target dependency that does not exist, and thereby a search for a source that nobody uses. New: **`Provenance.MayUpload(string? origin)`** without a target parameter; `UploadGate.Decide` asks it **once** before the loop (`herkunftErlaubt`), and per target only two questions remain — is the switch open (`SwitchedOn`), and has this source the text at this position already (`AlreadyThere`, F-M669/F-M488). The origin itself is a **permanent evidence**: it is written on download to `track.Origin` and `sidecar.Origin` (F-M486/F-M493, four write sites in the download) and deleted neither by the refresh nor by a new verdict. It hangs on the **content** (hash), so it blocks every later upload of the same text, independently of how many sources are added in the meantime.

**F-M723 [G]:** **The Expert dropdown can take away „everything of this file" — and the block explains nothing any more.** Concept decision: a dropdown resets all entries of one file from the plugin database, and the gui description is shortened to manual manipulation of a file in the plugin database. Until then `ResetPath` took back only **refusals** (`ReopenForRetry` deletes exclusively `Status == Rejected`) and left the loose subtitles out entirely — for a season whose verdicts are all `uploaded`/`observed`/`downloaded` it therefore reported 24 × 0. New is the value **`all`** in the dropdown („everything of this file"): it calls `ContentHashRegistry.ForgetMedia(mediaHash)`. That takes **all** track rows of the file (every status), **all** loose subtitles with this `MediaHash`, the belonging stamps and via `SourceStore.ForgetSubject` the marks of the subjects (file, track and loose subtitle identifier) — order: marks first, otherwise no mark finds its subject any more.

Pack refusals and season verdicts fall with them. Afterwards the next scan sees the file as new. The explanation of the block is shortened to **one sentence**: „Manual manipulation of a file in the plugin database." — the three earlier paragraphs (return to the queue, reup protection, pack memory) have fallen away; `both`/`stamps`/`verdicts`/`pack` and their meaning remain unchanged in the dropdown.

**F-M723a [G]:** **The key is the file, not the media hash.** Finding on the first prod run: `what=all` over all 24 S01 episodes took **244 marks** and **0** track/loose subtitle rows. Cause: `TrackId = TrackIdOf(FileId, Segment)` — the rows hang on the **file identifier**; their `MediaHash` can deviate from that of the file when the file has been repacked and carries a different `OsHash` since. `ForgetMedia` therefore asks both keys (`MediaHash` **or** `FileId`), the same for tracks and loose subtitles.

**F-M726 [G]:** **An abort during read-back is no error — and no statement about the content.** Finding on `Yaga.S01E01.The.Burning.Man…mkv` (27 subtitle tracks in the media container, 26 dialogue tracks, no companion files): 24 ERR lines „reading extracted stream 3…26 failed: A task was canceled" for **one** aborted pre-run. Cause: in the read-back loop head of `FfmpegTools.ExtractAllAsync` the `catch (Exception ex)` caught the `TaskCanceledException` of `File.ReadAllTextAsync(outFile, ct)`, wrote an error line per track and carried on with the next track. The method then returned `ExitCode` **0** plus nothing but `null` — „not read" could not be distinguished from „no text". Now: `catch (OperationCanceledException) when (ct.IsCancellationRequested)` **before** the general catch, one detail line, and the abort passes through (`throw`). The callers are set up for it — the pre-run (`ExtractionLookahead`) catches it as „plan ended" and extracts fresh, run and cycle treat `OperationCanceledException` as abort; the collector calls `ct.ThrowIfCancellationRequested()` anyway. The companion-file count stays untouched: `ContainerSubtitleStreams()` filters `IsExternal` (F-M658), so `-map 0:s:N` counts only tracks in the media container.

**F-M727 [G]:** **The reset says what it has found — and loose subtitles also answer to the path stem.** After F-M723a the S01 question stayed open: the run reported „0 track, 0 sidecar rows", and „nothing to delete" could not be distinguished from „key does not hit". Three follow-ups: (1) `ForgetMedia` returns beside the deletion numbers the **found numbers** (`FoundTracks`, `FoundSidecars`) and a `Known` that is `false` when the plugin database does not know the file at all — the answer of the endpoint carries them as `foundTracks`, `foundSidecars`, `unknownFiles`. (2) loose subtitles are searched via a **third key**: their own path must begin with `<Ordner>/<Dateiname-Stamm>.` — thereby the run also finds rows whose file identifier has no file row any more (file repacked, re-read). (3) The log line writes `gelöscht/gefunden` per kind. Tracks stay with media hash and file identifier: they carry no path, and a guessing path via the file name would be a second truth.

**F-M727a [G]:** **`Delete` takes an identifier, but the set compares its key — which is why no rows fell.** Prod run of 12.3.8.32 over The Middle S01: 24 tracks and 123 loose subtitles *found*, **0 deleted**, DB size unchanged. `ScribeSet<T>.Delete(id)` seeks the row via `_keyText`; for tracks that is `MediaHash + "|" + SubPos`, for loose subtitles the content hash — the passed `TrackId` never hit and the return `false` went unnoticed. Now `Remove(entity)`, which takes the row itself and finds it via the same key. `ForgetSidecars` (`Sidecars.Delete(hash)`) was never affected: there the content hash is really passed.

**F-M728 [G]:** **A source's no closes the track of that source — so far only the dispatch wrote it.** Concept decision: an mst refusal (no US film id, title closed) closes the track permanently, with a test. Finding: `SourceRefusalStore` remembered the content-related no since F-M605/F-M609 under `mediaHash|subPos|langTag|quelle`, but it was read **only** in the dispatch (`StillRefused`) — the refusal prevented the second call, the **track stayed open**. Consequence in the prod run: `Resident.Evil.2026` reported at 06:30, 08:12, 09:04 and 09:52 each time `14 of 37 track(s) open for "moviesubtitles"`, had 37 text tracks read out in one ffmpeg pass, wrote 112 `DUE … still open` lines and uploaded 0. The same repetition hit `Borderline`, `Moana`, `Paddington.2`, `Sister.Act.2` and every other file with mst tracks.

Now the one terminal rule (`ContentHashRegistry.IsEmbedTerminalFor`, F-M499/F-M669) reads the remembered no along — via the **position**, not via the language tag (`SourceRefusalStore.RefusedAtPosition`): the plugin database carries ISO (`ENG`, F-M557), the refusal stands under the code of the source (`EN`). Both mean the same track, because the track IS the position. Valid only with the **same signature** — a corrected text carries a different hash and is asked again; a temporary no is never remembered (F-M609). The loose subtitle hangs on the same rule (`UploadPipeline.SidecarsNeedWorkFor`, key `-1`) and stays open forever without it. **The no applies only to the source that said it**: a refusal at moviesubtitles leaves the same track open for SubDL and tvsubtitles, and `Rejected`/known origin remain untouched.

**F-M724 [U]:** **One fold for every name comparison — and it computes instead of normalising.** Concept decision: one fold for every name comparison, standing at the top above the packages; implementation after approval. Until then there were **two** fold implementations with different answers: `Pipeline/MediaNameParser` called `Normalize(FormKD)` — that needs ICU and does **nothing** under `InvariantGlobalization` (measurable in the harness: the styled name stayed styled, the check skipped itself with „SKIP fold checks"), while `Api/ScribeApiClient` folded deliberately arithmetically. New is **`Text/TextFold`** as the one place — above the packages, so that the sources can read it too without naming the pipeline: `Fold(string?)` / `NeedsFold(string?)`. Covered are the mathematical alphanumerics U+1D400–U+1D7FF (13 letter styles plus the digit runs). Covered too are the italic small h U+210E, the **whole** fullwidth forms U+FF01–U+FF5E (so also `．`/`－`, which the episode patterns need), the circled letters U+24B6–U+24E9, the squared letters U+1F130–U+1F149, super-/subscript **digits** and the small capitals. These do **not** lie in one block but scattered over five ranges. Are mapped slot for slot (a small-capital X does not exist in Unicode; the gap stays a gap). **Not** folded are the modifier letters U+02B0–U+02FF: „ᵃ"/„ʸ" also stand for phonetic script, a fold would read a pronunciation hint as a letter.

Folding happens on **both** sides of every comparison and **nothing is stored folded** — the fold is a reading aid, not a correction of foreign file names. Four call sites: `ScribeApiClient.EpisodeFromFileName` (before `FoldStyledLetters`), the `memberName` branch of `UnpackAsync` (before `string.Equals` on the raw forms — a styled archive entry thereby counted as „not in the archive": candidate burned, download unit paid, the log said „no bytes"). Then `TvSubtitlesClient.ReadSeasonArchive` (folds **before** the `.srt` check, otherwise a styled extension hides the file altogether) and `TvSubtitlesClient.EpisodesInZip`. `MediaNameParser.Parse` calls the same helper instead of the normalisation and returns an unstyled name **byte-identically** (the same instance, no copy) — the gate therefore remains „only for styled input". `FoldStyledLetters` and `NeedsUnicodeFold` have fallen away; there is no second table any more.

Evidenced in `scripts/tests/textfold` (25 checks, T387) and followed up in `packretry` (137), `namefallback` (24), `tvsubtitles` (139), `seasonpack` (275), `canonical`, `sources` (209). The case that gave the occasion stays silent: in the prod log of 24.11. there stood **0** styled characters — the fault was retrospective, not acute.

#### 14.9 The mark matrix — the only truth (up to schema 22; superseded by 14.11)

The matrix has been since 12.3.0.0 the **only** place where a source verdict stands: one row per (medium, source, kind) in the table `source_matrix` — and for `track`/`track-hi` per (medium, source, kind, language, HI, **position**) (schema 20, F-M669). Since 12.3.0.2 (schema 21, F-M670) it is also the only place for the **source ids**: there is no legacy table `source_marks` and no column `SubdlId` any more. The wide columns, from which it was filled until 12.2.0.28, are **removed** from the live file; the old plugin database lives on only in the backup file. There is no second data plugin database and no second read path.

| Field | Meaning |
|---|---|
| `MediaHash`, `ImdbId`, `TmdbId` | reference of the medium (business key, F-M194) |
| `Source` | `subdl` \| `tvsubtitles` \| `moviesubtitles` |
| `Kind` | `track` (track normal) \| `track-hi` (track HI) \| `file` (file) \| `pack` (season pack) |
| `Language`, `Season`, `Episode` | language and episode reference of the track or of the pack; for `file` the season/episode of the file |
| `SubPos` | F-M669: the **position of the track** in the video — part of the key of the track kinds; `null` for `file`/`pack`, `−1` for a loose subtitle |
| `HearingImpaired` | HI statement, three-valued (F-M285) — for `pack` the field that separates normal and SDH |
| `ClosedAt` | the verdict „closed" for this source and kind |
| `SourceId` | the id **at the source** (the entry the subtitle came from; for the pack the pack identifier) |
| `UploadId` | the id of the **submission** at this source (F-M668) — a number range of its own (F-M435) |
| `Fingerprint`, `MtimeUnix` | content signature: for `file` the track signature of the pass (F-M562), for `pack` the member hash (F-M466) |
| `RefusalReason`, `RefusedAt`, `RefusalSignature` | refusal of the source — not a verdict, but the reason not to try again at the same signature (F-M650) or the same fingerprint |

**Who writes:** `MarkEmbed` (track mark, position = `SubPos` of the row), `MarkSidecar` (loose subtitle mark, fixed position −1), `MarkCompleteFor` (file mark), `RecordPack`/`RecordPackRefusal` (pack verdict) — all via `SourceMatrix.WriteTrackMark`/`WriteFileMark`/`WritePackMark`. **Who reads:** `IsUploadedToPosition` (gate, accounting, head exit), `IsUploadedTo` (pair question without position), `IsContentUploadedFor`, `IsSidecarUploadedTo`, `IsCompleteFor`, `NewestCompletionStamp`, `PacksInSeason`, `PacksBySeason`, `HasPack`, `PackRefusalStands`, `GetPack` — track verdicts via `SourceMatrix.TrackMarkAt` (position) or `TrackMark` (pair), file/pack via `FileMark`/`PackMark`, without a column branch.

**The key, verbatim:** `<Anker>|<Quelle>|<Art>|<Sprache>|<hi|plain>` — and ONLY for `track`/`track-hi` additionally `|<position>`. Examples: `h:5ec580eb2b82ce69|subdl|track|GER|plain|11`, `h:591ac68265668a48|tvsubtitles|track|ENG|plain|-1` (loose subtitle), `h:5ec580eb2b82ce69|subdl|file||plain` and `i:tt0804484#S01|subdl|pack|ENG|plain` **without** position. `file` and `pack` carry no position, so that a file or pack verdict does not fall apart into positions.

**A file from schema 19** (matrix without `SubPos`) keeps its pair-wide rows (`SubPos` null) as a bridge: `TrackMarkAt` finds no position row and reads the pair-wide one; the next mark adds the position. A file from schema 17/18 is derived anew per position from the old columns at open. In the process the pair-wide rows of a step-A matrix fall in the same transaction (`DELETE … WHERE Kind IN ('track','track-hi') AND SubPos IS NULL`), so that no verdict stands twice.

**What does NOT stand in the matrix (and why):** solely the origin of a find (`Origin`/`OriginId`) — it says WHERE the text came from, not what the source has judged about it. Nor do the verdicts of the pack phase (`PackSeasonStore`, F-M611/F-M657), a plugin database of its own with a signature of its own. The refused **track** (`Status = rejected` together with reason) likewise stays on the row — it is a verdict per position and source-neutral (F-M499), not a source verdict. **`SubdlId` and the evidence table `source_marks` still stood here until 12.3.0.1 as „property of the row" or „stays what it was" — both have fallen with F-M670;** their values now stand as `SourceId`/`UploadId` in the matrix.

**The rule that applies expressly since F-M670:** source ids and source verdicts stand **exclusively** in `source_matrix`. There is **no second mark table** and **no id column** on `embeds`, `sidecars`, `media` or `packs`. Whoever needs an id asks the matrix (`TrackMark`/`TrackMarkAt`/`FileMark`/`PackMark`, `TrackIds`); whoever writes an id writes a matrix row (`WriteTrackMark`/`WriteFileMark`/`WritePackMark`, `SetDeferredUploadId`, `RemapId`). The old plugin database from the time before the matrix lives only in the backup file.

**The areas that stay as they are:** `meta`, `oshashes` (Oscache), `media`, `embeds`, `sidecars`, `rejected_candidates`, `counters`, `runs`, `tmdb_answers`, `status_stats`, `worker_runs`, `packs` (members), `aired_seasons`, `pack_seasons`, `source_refusals`, `title_failures`. **`source_marks` has fallen with F-M670** — it was the last second mark table.

**The path of the old plugin database, in this order:** (1) backup file with timestamp, complete old plugin database. (2) fill the matrix from the columns (track timestamp → `ClosedAt`, fetch id → `SourceId`, submission id → `UploadId`, `Complete…` → `file` mark with the signature as `Fingerprint`, `packs` row → `pack` mark with identifier, fingerprint and refusal). (2b) **F-M670:** take the two last remnants in — `embeds.SubdlId`/`sidecars.SubdlId` by their status (`uploaded` → `UploadId`, otherwise `SourceId`) and every row of the legacy table `source_marks` via its owner row (`UploadId`, `DownloadId` → `SourceId`, `UploadedAt`/`CompletedAt` → `ClosedAt`), only into empty cells. (3) remove old columns, the id column **and** the legacy table. All in ONE transaction; an abort leaves the file unchanged and the backup lying. Afterwards every verdict and every source id exists exactly once — in `source_matrix`.

#### 14.10 The four levels and their identifiers (schema 22; identifier shapes superseded by 14.11)
Since schema 22 (12.3.1.0) the plugin database is split into four **levels**; every row names its level itself, and the identifier is random, unique and immutable.

| Table | Key | carries | Display |
|---|---|---|---|
| `pack` | `PackId` (4 characters) | title ids, season, language, HI, release, members, signature, mtime, timestamps | `xxxx-00000-000` |
| `file` | `FileId` (5 characters) | `PackId`, path, file name, OSHash (property), size, mtime, title ids, timestamps | `xxxx-yyyyy-000` or `0000-yyyyy-000` |
| `track` | `(FileId, TrackId)` | `PackId`, track number, language, HI, forced, content hash, origin, timestamps | `xxxx-yyyyy-zzz` |
| `sidecar` | `(FileId, TrackId)` | track number, content, name, path, language, HI, **no** `PackId` | `0000-yyyyy-zzz` |

**The id syntax:** `xxxx-yyyyy-zzz` — pack, file, track; an empty segment is the zero run. The alphabet is `1-9a-zA-Z`, i.e. 61 characters; the `0` is pure filler, so that a display string can be read back unambiguously. Capacity: 13 845 841 packs · 844 596 301 files · 226 981 tracks per file.

**The number range:** per file **one** sequence across `track` and `sidecar`; at first sight the next free number, never re-assigned, never reused. The **ffmpeg index** is computed from it at runtime (`TrackNumbers.FfmpegIndex`: number of embedded tracks with a lower number, loose subtitles do not count) and led back via `AtFfmpegIndex` — it is not stored, because it depends on the current pack.

**The path of the old plugin database (17 → 22 and 21 → 22):** backup file with timestamp → in ONE transaction fill the four tables (ids random, number range per file, pack id on file and embedded track). `DROP TABLE` of the four old tables → marker 22. Rollback on every error; idempotent (the condition is the plugin database, not the marker). The backup of the whole ascent is **one**.

**What this step does NOT contain (step B follows):** the source tables `upload` (verdict), `reference` (foreign ids together with `DownloadSourceId`/`UploadedId`) and `badapple` (bad download together with `SourceId`), the dissolution of `source_matrix` and the removal of `OriginId` on the track. As long as `reference` does not stand, the foreign id would otherwise be lost. Until then `source_matrix` carries the source ids unchanged.

#### 14.11 The state since schema 23 — four levels and three source tables

Since 12.3.2.0 (schema 23) the plugin database is split in two, and this split is the rule: the **entity level** says what the things *are*, the **source level** what a source has done with them (F-M672).

**The entity level**

- **`pack`** — identifier `[xxxx]`; title ids, season, **language**, **HI**, release, signature, mtime, timestamps. The pack keeps its **membership** in `pack_track` (F-M676).
- **`file`** — identifier `#yyyyy`; path, file name, **OSHash** (checked against path and name), size, mtime, title ids, timestamps. **No** language, **no** HI, **no** pack (F-M673).
- **`track`** (only embedded) — identifier `#yyyyy#zzz`; track number, language, HI, forced, content hash, origin (source name), timestamps.
- **`sidecar`** (only loose subtitles) — identifier `#yyyyy&zzz`; host file in the identifier, name, path, content, language, HI, timestamps (F-M674).

**The source level** — `upload` (verdict of a submission), `reference` (foreign and own identifiers), `badapple` (bad download together with the foreign `SourceId`). Per source a **value** in `Source`, not a table per source. `source_matrix` and `rejected_candidates` no longer exist; `OriginId` has moved to `reference` (F-M677).

**The path of the old plugin database:** backup with timestamp → ascent in ONE transaction (17 → 23 or 22 → 23, plugin database-driven) → old tables and old columns removed. Evidenced on the live test plugin database: 44 files, 315 tracks, 13 loose subtitles, 4 packs, `upload` 315 verdicts, `reference` 337 rows, `badapple` empty, 84 rows `pack_track`, 543 identifier values unchanged, marker 23, exactly one backup, second start without a second.

**The number range stays as before:** one per file across `track` **and** `sidecar`, never re-assigned, never reused; the ffmpeg index is derived from it at runtime (number of embedded tracks with a lower number) — the identifier is never the index.

**The member since schema 24 (F-M679).** `badapple` no longer hangs on the foreign ENTRY alone but on the subtitle within it: the column `Member` names the file in the pack („MobLand - 2x05 - Birdland.WEB.NeoNoir.en.srt"), empty for an individual download. It stands in the key — (SubjectId, Source, SourceId, Member). Two broken subs of the same pack are thereby two rows, and a bad sub burns only itself. A plugin database from schema 23 is rebuilt at open (one backup, one transaction, rows with `Member = ''`, marker 24); measured on the own 23 test plugin database: one row adopted, `ReportState` and `Written` unchanged, exactly one backup, second start without a second. The write path does not set the member yet (the two callers stay unchanged), which is why `''` stands there until the wiring.

## 15. Logging, Status and Transparency

**F-M273:** **The configuration page has a host-free structural regression check.**

**The checks are structural, not wording-dependent:** a comment rewrite must not be able to raise a false failure.

**F-M23 [B2]:** Status view: two cumulative counters on the config page — subtitles uploaded and downloaded since the last reset. The line reads "N subtitles uploaded, M subtitles downloaded since the last reset"; the date stands on its own line below it. "Reset statistics" zeroes both and moves the date. Both are incremented at the end of every run from that run's summary, including runs cancelled from outside.

**F-M595 [U]:** **The statistics line "stopped before sending" counts only QA verdicts — and is called that.** Concept decision: the stopped-before-sending line counts only QA fails in the statistics. Finding: `StatusCounterDelta.rejectedUpload` fed from `summary.RejectedCandidates`, and this counter stood at **twelve** places — duplicates (`duplicate-remote`, `duplicate-content`), unmappable language identifiers, extraction faults, locked sources, self-echo. The line thereby claimed a quality verdict over tracks that had never seen a gate. New: `summary.RejectedQaFail` counts exclusively the verdicts from `GateReject` (size floor 2 KB, cue count < 30, SRT structure, sync plausibility) — `ScreenContentQa` returns `out bool qaFail` for that and sets it at exactly one place. The statistics read `RejectedQaFail`, `RejectedCandidates` remains as a run count in the log. The line is now called "Uploads: QA fails not sent", 25 characters, under the table width of 41. Checked by T305.

**F-M207:** **The cumulative counters live in the database; a database reset resets them with it.** Stored as one single row in the database, with 64-bit counters.

`scope=all` calls the reset in the same operation, so display and data cannot disagree.

There is no one-off adoption of the XML totals, together with its flag. The XML fields stay in place, unread, so a downgrade finds them — nothing adopts them and nothing writes them. Counters that a database reset clears stay cleared; no path seeds them back from the configuration file.

**F-M209:** **A run's in-run watchdog is torn down on EVERY exit path, and its grace is a constant 6 minutes.** The grace does not scale with any setting: it must cover the longest legal single operation. One file transfer is allowed 5 minutes, so a scaled value could abort a working run (the old scale gave 2.4–3.8 min). Waiting out a configured pause is always legal; only a genuinely frozen run exceeds the grace.

**F-M224:** **The plugin's own log mode is the only authority.** Jellyfin's Serilog pipeline filters a record before the sink writes it, and Jellyfin exposes no API to change its own log level. The plugin therefore writes **everything at the normal level** and gates the detail itself in the log helper (the per-item level, the trace level → Verbose+; the detail level → Debug+); `Normal` stays lifecycle/summary only and warnings/errors are never gated. No plugin line may be written at Trace.

**F-M615 [U]:** **The title database no longer keeps silent.** Concept decision: the title database no longer keeps silent about its faults. `IsClosed`, `Close`, `Note` and `Clear` caught **every** exception without a word; the title lock did not hold in four runs (482–485, always `tt35538033`, `tt34685692`, `tt34379051` → `exists-other-year`), and nothing was to be seen in the log. Now the plugin attaches a log at start (`AttachLogger`): read, write and close faults appear as a warning with type and text, an **unattached** database is reported once per process. Every access stands as a Debug line in the log (`not in the store`, `open, attempts N`, `is closed`, `attempt N written`, `closed and written`). That makes visible in the next run whether the entry is written and is there again in the next run. **Addendum 487:** the first three lines (`attempt N written`, `closed and written`, `is closed … the ladder is skipped`) stand at **Information** — Jellyfin does not show Debug in the standard log, which is why run 486 was blind despite the log (0 lines). Checked by T325.

**F-M618 [U]:** **The title database says loudly what it reads.** Finding: `tt28638842` was closed at 21:14 and logged (`closed and written`), but began the next run again at `attempt 1 written`. The line was gone or was not found, and the read lines stood on Debug (hence invisible). Now **every** read access stands at Information: `NOT in the store — the ladder runs again`, `open, attempts N (<Grund>)` or `is closed (<Grund>, attempts N) — the ladder is skipped`. Three lines per title and run, so it is unambiguous whether it is written, read or deleted. Checked by T325 (the lines are the same, only at Information).

**F-M619 [U]:** The call site logs the answer of the title database itself: `title <id>: store says closed=<wahr/falsch> ("<Grund>"), run memo=<wahr/falsch>` — one line per title and run, through the log of the pipeline (not through that of the plugin database). Reason: in 490 all write lines stood in the log, but **no** read line — so the answer had to be made visible where the question is asked.

**F-M226:** **Every plugin line's level is visible — as a text marker, written at the normal level (F-M224).** The marker `[N]` (Normal), `[V]` (Verbose and up), `[D]` (Debug and up) sits in front of the message. It is written by the normal level/the per-item level/the detail level/the trace level. Warnings and errors carry no marker.

**F-M225:** **Every plugin line belongs to exactly one level — and the statistics never depend on logging.** Ungated normal-level calls must not make `Normal` show diagnostics (resolved ffmpeg path, reschedule budget, seed pre-check timestamps). Assignment: internals and diagnostics → the detail level; per-item work → the per-item level; run lifecycle, abort reasons and task summaries stay at Normal. The counters are written in the single statistics writer from the run summaries, unguarded in the run path, and no log call carries a side effect in its arguments, so a `Normal` run counts exactly like a `Debug` run.

**F-M247:** **A dry run contributes NOTHING to the statistics — all six counters, not only the volume ones.**

A run started with a dry run/a dry run adds zero to uploads, downloads, QA-rejected downloads, QA-rejected uploads. A dry run does real work — searches, ranking, the TMDb id test, the QA gates — so its summary fills with plausible numbers while it saves no file and uploads nothing.

The log line reads `DRY RUN, would have uploaded {N}` / `{Saved} would have saved`. The rest of the download diagnostic aggregate is unchanged.

**F-M245:** **A message reaches the log through exactly ONE gate.** A call site picks the lowest level that must carry its message and calls the one matching gate; it never calls two gates for the same message. `Normal < Verbose < Debug` is cumulative, so two gates for one message put it in the log twice. Scope: the TMDb call trace in both pipelines.

**F-M597 [U]:** **Every track is measured — QA, source gates, slot waiting, sending.** Concept decision: every track is timed. The measurement showed: the interval between two uploads lay at 9.6 s (median, 500/h), of which 2.0 s network. The rest lay in a block that wrote **not a single log line**. Instead of guessing, `UploadSrtContentAsync` now carries a stopwatch: `qa`, `sources`, `slot wait`, `send`, `total` per track as one line at `Detail` (hence visible only in the debug log mode, so that normal operation is not spammed). The purpose is explicitly the diagnosis: the line says which step holds the time, and every optimisation afterwards can be checked against it instead of against a guess. **Addendum:** the first measurement showed that the time does **not** lie in the track (`send` 0.7–3.0 s, `qa`/`sources`/`slot` each under 100 ms at an interval of 9.5 s between two uploads). Therefore the same line now also measures the loop: `TAKT-SPUR` names per candidate the **interval since the last track** (verdict, database lines, bookkeeping) and the share of the chain in it. Checked by T307.

**F-M598 [U]:** **The whole run is measured too — upload with pack phase, and the download.** Concept decision: diagnosis points at the detail log level cover the whole upload run-up (pack etc.) and the download. Four lines at `Detail` per direction: **item list** (count after collecting and sorting), **pack phase** (duration until the end of the season pack planning, upload only). The other two are **item loop** (one line per item with the duration of the previous item, so that a single expensive item does not disappear in the sum) and **run end** (item loop and total duration). Together with F-M597 (track) and F-M596 (lookahead) every second of a run can thereby be assigned: run → item → track → phase. The lines lie explicitly at `Detail`, so that normal operation remains unchanged. Checked by T308.

**F-M599 [U]:** **The pack phase is measured from the inside.** Concept decision: the pack phase is optimised, so it has to be measured from the inside. The phase line from F-M598 names only the sum (measured 70.7 s at 1,416 items), with which nothing can be optimised. Therefore four further lines at `Detail`: **library walk** (the episode list that `CompleteSeasonsAsync` fetches once more itself), **episode count inquiries** (count and sum of the IMDb/TMDb calls via `SeasonEpisodeIndex.AiredAsync`), **file language scan** (count and sum of the looks into the files via `FileLanguages`). Separated from the planning is the **fill phase** (`FillOnly` per pack) — planning computes, filling talks to the network; whoever throws both into one number optimises the wrong part. Checked by T309.

**F-M218:** **The reject counters are persisted too, per direction, next to the volume counters.** Two 64-bit fields on the same single status row as F-M207, one field per counter — rejected downloads / rejected uploads: candidates **fetched and then thrown away**, one field per direction. The scope is every reject path, and it is F-M286 that defines it.

**Two counters were WITHDRAWN from this requirement:** *type-corrected-from-file-name* and *TMDb year-filter misses* — the row AND the counter. They are removed from every carrier: the two statistics rows, both entity fields, the writer and its early-out, the reset, both API responses, the pipeline increments and the resolver event. The BEHAVIOUR they measured is unchanged and still required (F-M217). Withdrawing a counter means withdrawing it everywhere: the check asserts the absence across page, entity, writer and API.

Every timestamp uses one format — US order (M/D/YYYY) with local AM/PM time — through the timestamp formatter and the time formatter, so stamps cannot drift apart per call site.

**F-M310 [B1]:** **The time an audio alignment spends is taken off the next download's pacing wait, never below zero.** The alignment runs after a subtitle download and before the next real download, inside a transfer pause sized for a download only.

Measured and settled in the rate limiter: the fit time books, the pause spends. Both alignments are measured — main track and hearing-impaired — in one helper. The measurement wraps the CALL, not the write, in a `finally` block, so a cancelled or failed alignment also counts. The credit is CONSUMED at the pause, once, and never banked: it can only bring the next pause down, never a later one. Floor 0 — the plugin never waits a negative time. Zero and negative bookings are discarded, not booked. With the alignment switched off nothing is measured, so the pauses are exactly as configured. Uploads are NOT affected (own limiter, own rhythm).

The run's DONE line reports what the alignment cost: `N fitted to audio (Xs)`. See F-M26, F-M308, F-M309.

**F-M309 [B1]:** **The audio fit is logged on two levels: WHETHER at Normal, WHAT AGAINST at Verbose.** At Normal, in the lines F-M24d prescribes: the run START line names the switch — `audio fit=on|off`. The run DONE line carries the counter — `N fitted to audio` (F-M286). At Verbose, the per-file detail to REPRODUCE a fit: `fit reads audio track {Pos} of {N} ({Reason}); subtitle {Release}` — the track actually READ, gated on the fit switch. Verdict lines: `auto-sync corrected …` / `not applied: …` at Verbose (F-M24a), one branch each; there is no `ffmpeg not available` branch, a missing ffmpeg reported once per run at Warning. See F-M24a, F-M286, F-M307.

**F-M314 [D]:** **The loose subtitle rename is gated by the SAME switch as the pack write — `Allocate missing language codes` — and not through resolving undetermined language codes.** Default changes ON→OFF, because `Allocate missing language codes` is off by default (F-M261). The gate only gates: the file is left as it is, the 2 KB floor and the "no confident verdict" exit unchanged.

**F-M313 [B1]:** **The statistics row counts the LOOSE subtitle files the seeder renamed so their name carries the language, in its own row.**

F-M278 renames an unlabelled loose subtitle to the shape this plugin writes. That is work on a file the operator owns, and it was visible only in the log. It gets its own row — `Loose subtitles: language codes added` — rather than sharing the pack row, because it is a different act on a different kind of file. A media container gets a language tag **inside**, a loose `.srt` gets a new **name**. One number for both could be read as either.

**No direction prefix**, for exactly the reason the pack row has none: the seeder serves both directions.

Counted on the MOVE, not on the attempt. Every refusal — the target name taken, a directory that cannot be listed, a filesystem error — leaves the file where it was and must not appear in the statistics as work that was done.

**F-M312 [B1]:** **A dry run does not rename either.**

The media container rewrite was already suppressed by F-M263, but this rename had **no dry-run guard at all**: the seeder contained not a single dry-run check, and the gate's switch covers the pack only. So a dry run moved the user's files around while its documentation promises it "writes nothing" (F-M22). The seeder now has one predicate for the whole class — the dry-run check, true when **either** direction has a dry run armed — because the seeder belongs to no direction. Tying it to one switch would let the other dry run edit the library. Detection still runs: the value of a dry run is answering what it WOULD do. The file is then recorded under its CURRENT name, the one really on disk.

 See F-M22, F-M259, F-M263, F-M278, F-M311.

**F-M311 [B1]:** **The statistics row counts the media FILES whose language codes the seeder wrote, and that row carries no direction.**

The seeder's allocation pass (F-M261) rewrites media containers; it is work the run did to the LIBRARY, so it gets its own statistics row. The count is one per FILE, not per code: a file that got three tags counts once. The gate keeps reporting the per-track number on its own Normal line.

**No direction prefix** — the seeder serves both directions, and the allocation pass sits BEFORE the direction checks, so a `Downloads:` prefix would claim a scope the count does not have. The label reads `Media files: language codes added`.

Counted only when the write actually happened: a dry run `stand`s the write down (F-M263), the switch `Allocate missing language codes` is off by default, and an undecidable detector writes nothing (F-M261) — all three leave the counter at 0. See F-M261, F-M308.

**Placement:** the row sits with the upload rows, directly under `Uploads: stopped before sending`. The two rows that carry NO direction (F-M311/F-M313) stay the last two — asserted in T123. A new row reads naturally as "append at the end", and only the grouping is lost when it goes there.

The column reaches an EXISTING plugin database: the schema is created only for a new file, so a new column arrives through an explicit `ALTER TABLE` guarded by `PRAGMA table_info`. The schema version is deliberately not bumped — the row grows by addition, an older build ignores the extra column, and a bump would stop the previous build from writing for nothing. See F-M30, F-M308, F-M311.

**F-M308 [B1]:** **The statistics counters are a TABLE, one row per counter, and the table names the direction each count belongs to.**

Form: label left, number right, both columns bounded so the numbers form **one vertical line**. Order: the two volume counters with **download first**, then the counter covering **both** directions, then the remaining rows **grouped by direction — all downloads, then uploads**. A row whose data is per-direction names it (`Uploads:` / `Downloads:`); one covering both says so. Order asserted (T123).

The wording states what happened, not the code's vocabulary:

 uploads DISCARDED BEFORE TRANSFER — upload candidates dropped before anything was sent, on the QA gates, the und/language checks and the self-echo guard; the upload direction fetches NOTHING.
 downloads rejected after being fetched — download candidates fetched and then thrown away.
 auto-synch — downloaded subtitles whose timing the run MOVED onto their audio track (F-M307), counted when APPLIED; no upload row. Operator's wording: `Downloads: Auto-Sync`.
 broken subtitles reported — sits under the Auto-Sync row, because the fit measures the file and this number counts the answer to it: files reported to SubDL as broken and accepted by the portal (F-M455). Only an accepted report is counted; a deferred or rejected one is none.

 See F-M218, F-M286.

**F-M286 [B1]:** **A reject counter counts what the run SPENT, and every counter is printed somewhere.**

Scope: **every path that fetches and then discards** — no bytes, content already known, broken content, hearing-impaired gate, und-off, unmappable language, self-echo, duplicate-remote, forced. A stored `Rejected` verdict without an increment is a silent discard.

Not counted: anything the run did not spend on — an empty hearing-impaired pool, candidates the walk left untried, a retry that re-runs a gate already counted.

The number reconciles with the day's quota (`requests = saved + rejected`) and both run lines print it. See F-M218, F-M24d.

**F-M24a [B1]:** **Tiered logging, all levels via Jellyfin's logger. DEFAULT: Normal.**

Normal: run STARTED/DONE lines with counters, run aborts with reason, critical errors, aggregate skip lines per reason class, wrong API keys reported ONCE as Error.

Verbose: per-file up-/downloads, SKIP reasons, dry-run lines, search results, QA reject reasons, TMDB API traces.

Debug: every SubDL API round-trip (search, download, upload, login), api_key always redacted; TMDB round-trips too.

**F-M24d [B1]:** **High-level log concept for Normal mode:** one summary line per run/direction with counters; one aggregate line covering the skip reasons of a download run; lifecycle events and warnings/errors once per event. **Amended (F-M286): the upload run writes ONE line with its two numbers — uploads and rejections — instead of none.** The old sentence ("no skip aggregate") dates from when the upload's reject number covered a single gate. With the counter now spanning every reject path, a run that discarded its whole inventory read exactly like a run that found nothing. What stays: it is one line with two numbers, not a per-reason aggregate — the per-item SKIP lines remain the Verbose detail.

**F-M152:** **The 429 reaction is decided by the server's rate headers and a live counter read, never by the status code alone.** Every response is parsed for the API's rate headers; the values are the daily limit, the remaining count and the exact server reset.

The counter is read from the account endpoint after a 429 and only then.

A 429 carrying a reset header is the account's daily limit: the run stops against the live counter unless continue-after-limit is on, and the next fire is anchored on the server reset plus the randomised 30–300 min offset (F-M182).

A 429 naming a transient server state is retried in place (F-M58); a 429 carrying no rate header and no named state is treated as an edge case and anchored on the next midnight, not on the daily limit.

The daily-limit decision and its anchor are one rule (F-M62, F-M238).

**F-M255 [D]:** **No candidate and no stream is discarded without a line naming it and the reason.**

Every exit inside the candidate walk that does not end in a save, and every per-stream exit of the upload collector, writes one line at Verbose (`[SubDL-D] … reject …` / `[SubDL-V] …`) carrying the release or file, the language, and the measured reason. The gates are named individually: download failure (no bytes, too few bytes, with the byte count), language detection, structure (monotonic flag, cue span), cue count, runtime, content-already-known. Named too: keep-best stop (with the number of untried lower-ranked candidates), the QA memory filter (with the ids it removed), the correction-hunt stop and its abandonment (with the reason no other candidate could change), and the empty HI pool (with its size). The reason carries the measured value, not a verdict.

Every candidate walk and upload-collector exit carries its measured value (monotonic flag, cue span, byte count).

The level is Verbose, not Normal: these lines are per candidate and per stream. The exits that already carried a counter but no line gained the line; the counters keep their meaning.

**F-M262:** **A rewritten pack is reported at NORMAL — one line per file naming which languages were written; the per-track detail stays at Verbose.**

The gate logged through the per-item level (Verbose and up) and the tag writer logged nothing on success, so at the default `Normal` a rewrite of a media file left no trace at all. A pass that EDITS the user's media must be visible at the level every install runs at.

One Normal line names the file, how many codes were written, which languages (distinct ISO codes, position order) and — when the identity moved — the old and new hash. Per-track positions and byte counts stay at Verbose. The seeder's identity line carries only what only the seeder knows, so the two do not repeat each other.

**F-M267:** **The waiting download/upload rows report the CYCLE's fate, not their own wait.**

`WorkerRunRegistry.DescribeCycle(cycleFinished, seederOutcome, seederDetail, directionOutcome, directionDetail)` decides the row of a wait-only task, with a strict ranking. Top of the ranking: (1) cycle not finished at the wait cap → `running` ("cycle still running at the wait cap"); (2) a `failed` direction or seeder → `failed`. Then (3) a `deferred`/`cancelled` direction, then a `deferred`/`cancelled` seeder → yellow; (4) a `skipped` direction, then a `skipped` seeder → grey; (5) otherwise → `ok`, carrying the seeder's numbers when it reported any, else "cycle finished".

Reporting the task's own "ok" is wrong twice over. It claims success when the wait cap expires while the seeder is still scanning, and it hides a quota stop behind a green light. the fallback word substitutes "not recorded" so a row cannot show a bare outcome word.

**F-M289:** **The direction rows are written by whoever OWNS the cycle — on an arrival cycle that is the dispatcher, not the waiting task.**

An arrival cycle starts in the dispatcher and never passes through the download/upload tasks. The dispatcher records both direction rows for the arrival triggers (`event`, `arrival-followup`) and only for those: on a scheduled or manual run the waiting task owns its row, and a second writer would fight it. The ranking is the cycle description (F-M267), so a row reads identically whichever path produced the cycle. The dispatcher marks both rows `running` at cycle start, so a cycle that dies mid-scan shows the attempt rather than the previous green.

A direction that is switched OFF is recorded `skipped`/`disabled`, not `ok`.

**F-M290:** **With no library selected the seeder does not start at all — it does not scan, pre-check, or take the run lock to conclude that there is nothing to do.**

No selection means there is nothing this seeder could look at, so the answer is known before any work begins. The check sits at the TOP of the seed step, ahead of the global run lock, the change-stamp pre-check and every database read. Reaching the same conclusion from inside `Scan` costs a held lock, a walked library and a touched database for a result that was already certain.

The row is `skipped` with "no libraries selected" — GREY, because a scan that was never allowed to run must not read like one that ran and found nothing. The wording matches the line `Scan` logs for the same condition, so the two cannot be told apart.

**F-M330 [D]:** **One lit lamp, for the worker that is working.**

Concept decision: the seeder gets blue only, download only download, upload only upload — not seeder and downloader, because they are successive. The cycle is a sequence — seed, then download, then upload — so at most ONE direction may be blue, and blue claims its own work, never a sibling's.

Each direction marks its run at the moment its own queue is prepared, and closes its own row from its own fate in a `finally` (closing the direction row). A failure or a stop still leaves no lamp hanging. The wait-only scheduled tasks never call `Start`: they start a cycle and wait. A direction whose run never began (switched off, empty queue, lock busy) keeps its row for the waiting task — which is what governs the writing of the direction row.

**F-M268:** **One colour rule for every worker: green = the work ran and ended without an exception, yellow = the work did not happen but nothing is broken, red = something is broken, grey = not run.**

The palette: the page accent is `#00a4dc`. `run` → accent; `ok` → `#107c10`; `failed` → `#a4262c`; `cancelled` and `deferred` → `#ffc107` (quota, run-lock deferral, user stop); `skipped` and `never` → `#767676`; a dry-run note → `#9a9a9a`.

The accent is also the link colour (F-M229) and the required-field colour (F-M228). Red is reserved for destructive and failed states. The quota bar uses the same palette for fill, warning (`#ffc107` at 75 %) and danger (`#a4262c` at 95 %). The bar's colour is decided by the fill ALONE — a local deferral never recolours it; that state is stated as its own line under the Workers list (F-M288).

**F-M352 [D]:**the status line names what does not run by itself — the lamps stay untouched.**

**F-M353 [D]:** **A stop clears the direction's lamp with its fire.** A stop cancels the direction's pending fire, and the quota answer carries no fire (`deferred: []`, `Deferred: false`). The row stops showing `defer` with the previous run's detail, instead of waiting for the next run to rewrite it.

**F-M354 [D]:**a status line names the STATE, not the row's own name a second time.** The three status rows are labelled `SubDL Login`, `SubDL API` and `TMDb API`. Their messages therefore carry the state only: `OK`, `reachable`, `wrong username/password`, `not authorized — check API key`, `rate-limited — try again later`, `no token returned`. The success lines carry no duplicated prefix: Upload and Download share one API line, `reachable` is ONE wording, used twice. The counter stands at exactly 2; statements about the KEY — `API key invalid / not authorized`, `API key not configured`, `API key invalid (HTTP 401)` — stay as they are, because the key is a different subject than the endpoint.

**F-M355 [D]:**a directory row names the library and its state — the path is not repeated.** The `Library directories` rows render the library name plus the state only (`Writable`, `Not writable: …`, `Directory does not exist`). The absolute path is not appended, because „Movies Writable" says the same thing twice and a long path pushed the state off a phone-width line. The path stays in the API answer (`DirectoryStatusDto.Path`) — diagnostics and tests read it from there, the page does not.

**F-M356 [D]:**the state word sits on the RIGHT edge of its row.** `SubDL Login`, `SubDL API`, `TMDb API` and the `Library directories` rows put the state word on the row's right edge (`.subdl-msg { margin-left: auto; text-align: right; }`). The word sits in one column with the workers block, instead of starting right after the label. Reason: the labels have different widths (login, API, library name, test line), so a left-aligned message began at a different x on every line and the block read ragged; `text-align: right` also keeps long messages (`Not writable: …`) ending on that same edge. The workers block itself stays LEFT-aligned — a right-pinned worker table was rejected earlier (F-M308 comment) and is not reintroduced.

**F-M357 [D]:**the refetch field describes itself in the words of its own name.** The description under `Refetch interval` reads `Refetch interval for download and upload.` The text names the thing, not a second name for it (F-M354/F-M355).

**F-M358 [D]:**the status rows sit on the same rhythm as the worker lines.** At 390 px the three status rows lay **40 px** apart (19 px line + 2×4 px row margin + 12.8 px flex gap from the inline style). The workers block packs its lines at **one line height** — its rows are `display:contents` inside a grid without row-gap, so margins and gap have no effect there. Two blocks of the same page therefore breathed differently and the airy one looked broken on a phone. The flex gap left the inline style of `#ScribeStatusBox` and `#ScribeStatusBox.subdl-status-row { margin: 0; }` puts both groups on one rhythm; the `h4` between them keeps its own margin.

**F-M359 [D]:**the deferral note names the DAY of the next attempt.** The line below the Workers list reads e.g. `Download: download quota/rate limit — rescheduled — next attempt Tue 6:10 AM`: short weekday from the locale plus the time to the minute. The seconds are dropped here; a scheduled slot does not need them.

**Where.** The line under the Workers list that already reports a pending fire (`#ScribeLocalDeferLine`). The notes are appended after the fire notes, separated with `·`, and come from the same answer as the fire notes (quota `local.notes`), so the two cannot disagree. Nothing to say ⇒ empty line.

**The two words.** `off` = cannot start at all. `manual` = can start, but nothing starts it by itself. No word = it runs by itself; that includes `OnArrival`, which is an automatism too. One word per worker: `off` wins over `manual`.

**When `off`.** Download and Upload: toggle off, no library selected, or credentials missing. Download also: no target language set.

**When `manual`.** Download and Upload: the refetch interval (default weekly) on `Manual` (or the legacy `Never`) — no automatic pipeline cycles. Upload Postprocessing: upload toggle off, or its cadence on `Manual`/`Never`. Database refresh: the cleanup mode (default weekly) on `Manual`/`Never`. OSHash refresh: the hash refresh (default monthly) on `Manual`/`Never`.

**Unchanged.** The lamps, the stored results, the details and pending fires stay as they are, so a run never disappears behind a switch.

**F-M360 [D]:**the offset fit is fitted on the DIALOGUE lines only.** Music (`♪`/`♫`), bracketed sound labels (`[door slams]`, `{phone rings}`) and bare capitalised sound words (`GUNSHOT`) are excluded from the fit's input; every other cue is used. They are still MOVED — each takes the shift of the last dialogue cue at or before it, so the writer shifts every cue of the file. Fewer than 40 dialogue cues ⇒ the file is fitted on all its cues. Why: those lines sit on music or effects, vote the same at every candidate offset, and set σ, the segment-length floor and the block grid. Measured: Line.of.Fire S01E02 collapsed a real 6 s drop into one step (3 s off for 4.5 minutes) and Lioness S02E06 left a 12 s block standing. The exclusion fixed both and left seven exact files exact.

**F-M366 [D]:**no status without a run.** F-M365 took the row away from a download held back by the spent allowance; this closes the other four paths that wrote a row for work that never happened. The lamps know `run`, `ok`, `fail`, `cancel`, `skip`, `defer` and the grey "never" of a missing row — a switched-off direction rendered as a grey `skip`, which reads like a run that skipped something. Now no row is written for: the switch being off (download and upload), a cycle already active when the task fires, a run that never began (where DescribeCycle painted the SEEDER's sentence onto the direction's line). Nor for a Jellyfin restart before the run started (which claimed `ok / restart`). The cycle keeps saying all of it in the log — `download ran nothing this cycle (switched off|run never began) — no worker row` — so a missing row always has an explanation. A direction row is now written by exactly one thing: its own run, when it begins. A user stop still writes `cancel`, and a real deferral with a scheduled re-fire still writes `defer` with its time.

**F-M365 [D]:**a held direction writes no row.** When the SubDL download allowance is spent the direction does no work at all — no seed, no fetch. An own row with the cycle's timestamp and the word `defer` is dropped: the flag means „a re-fire is scheduled", which a spent allowance does not schedule. The hold is logged (`download held back — "download limit spent (50/50)"`) and both writers now leave the row alone: the waiting task and the cycle-end writer skip it. The switch being off still writes `skipped: disabled` — that is a deliberate configuration state, not a run that did not happen. Supersedes the row-writing half of F-M331.

**F-M364 [D]:**a segment that buys level on a song is vetoed.** The objective is the mean voice-band level under a cue, and sung vocals are louder there than speech. The Rainmaker S02E01 scored 0.640 for its opening block placed on the title song against 0.538 where the dialogue sits. So the fit pushed 58 cues 15.50 s early and the writer clamped two of them to `00:00:00,000 --> 00:00:00,000`. Mean level cannot separate the two, the ONSET at a cue start can: the level rises by 0.0006 across the cue starts on the song against 0.0227 for the same file's dialogue. Each segment is checked AFTER the fit; a segment that wins ≥ 0.10 mean level over the unshifted placement with an onset below 0.01 takes its NEIGHBOUR's shift. Calibrated on 46 files (~16 000 cues): exactly one segment reaches the 0.10 floor (Rainmaker +0.132; next-highest anywhere: 0.057), so only that file changes — from `−15,50` to `−2,00`, the shift its own remaining segments already use. The onset argmax inside a segment was rejected as a rule: it swings ±0.1 between neighbouring 0.25 s grid points and would fit noise.

**F-M361 [D]:** **A cue is a sound label when nothing but bracket groups remain.** After the markup strip and the dash trim, every `(..)`, `[..]` and `{..}` group is removed; if no letter or digit is left outside them, the cue does not feed the fit. This REPLACES the capitalisation guess and the leading-bracket rule of F-M360. Embedded SDH writes labels in lower case — `(gunshot)`, `(crowd shouting)` — which the capitals rule could not see: 336 of 22 992 cues over 30 embedded HI tracks. A cue that starts with a group but carries dialogue outside it — `(SCOFFS) Okay, so now what?`, `[Shirley] Hey!` — IS dialogue, and the old rule had thrown 1 848 of those away. Net on the same corpus: labels drop from 22.9 % to 16.3 % of the cues, the fit gains 1 848 dialogue anchors, 0 false hits on bracket-free lines.

**F-M193 [B1]:** **`/user/mySubtitles` is NOT paginated — the counters must count DISTINCT upload ids.** The endpoint ignores `page`, `per_page`, `offset`, `limit` and `start`. The listing deduplicates by the upload id and breaks on the first page that adds no new id; rows with `UploadId <= 0` are kept unconditionally. The status line does not claim "in N pages".

**F-M265:** **The LAST run of each worker is stored in the database and shown in a "Workers" section on the General tab of the configuration page — one line per worker with the time of its last run and how it ended.** Only the last run is stored, not a history.

**Six outcomes**, each mapped to a colour and a word in the GUI: `ok`, `failed`, `cancelled`, `skipped` (a scan that found nothing to do). Also `deferred` (nothing broken, the work was pushed forward: run lock busy, quota, user stop) and `never`, plus `running` while a cycle is still working at the wait cap. Colour rule: F-M268. A `skipped` run still writes its row — "the task was not allowed to run" is information, and without it a disabled direction looks identical to a broken one.

**The dry-run note is CONDITIONAL**: a grey `dry run` note appears in the result cell only while the direction's dry-run flag is on. With the flag off the line stays clean.

**F-M398 [P]:** **The worker row of the status page marks a dry run with the word `dry`, grey, directly next to the result word.** When the worker stands on `run` (blue), the row reads as "run dry". A badge in its own column widened the row by roughly 50 px and pushed the status out of frame on narrow displays.

**F-M409 [P]:** **The `dry` mark carries the same grey as the word `cancel` (`#767676`).** Two shades of grey side by side on the same page claimed two meanings where one is meant: "nothing happened".

**F-M293:** **A Jellyfin restart is not a user stop; the worker row reports what it actually did.**

A restart cancels the running task exactly as the stop button does, and the token carries no reason. The task's catch therefore treated both the same — it wrote a stop marker, called the user-stop path, and that records `cancelled`/"user stop" for **both** directions at once, so a restart reported an action the user never took.

**The stop marker is the discriminator:** the `Stop` endpoint writes it *before* it signals, a shutdown never does. `PipelineStopSignal.HasMarker` tests it **without consuming it**; without it the row is `ok`/"restart". A worker with no stop button (postprocessing, OSHash refresh) can therefore never report a user stop.

**F-M294:** **"Deferred" means a fire was scheduled — for ANY worker, and the moment is named.**

A pending re-fire IS the deferral: it exists only because a run could not finish, so it decides the word and the colour of that worker's row — YELLOW as `defer` — for every row that does not claim something stronger. `failed` (broken) and `run` (in progress) are left alone; `ok` and `cancelled` both claim that nothing is owed, and a fire proves otherwise. All workers that can hold a fire are covered (both directions, database refresh, OSHash refresh, postprocessing), and the flag is read per worker from the coordinator rather than from the row.

The line under the Workers list names, per worker and in list order, its stored cause and when the fire is due. It is gated on the fire alone, so a restart cannot blank it; the seeder holds no fire of its own and never appears.

**F-M269:** **The database refresh's detail line names the compaction fallback.**

**This is a NOTE, not a red light:** the outcome stays `ok`.

**F-M339 [D]:** **Every number in an Auto-Sync log line is written culture-invariantly.** An interpolated number follows the host's locale, so the same code printed `t = +8,62` on one day and `t = +8.62` on the next, and the operator parses these lines. **The SRT timestamp formatter keeps its comma** — that is the file format, not a display choice.

**F-M292:** **A worker with an open fire is yellow, regardless of which worker it is.** The stored row describes the past run, the open fire describes what is still outstanding.

**F-M341:** **Every number in the correction line carries a decimal point, regardless of the host's language.** Tools read this line; a comma breaks the reader.

**F-M621 [D]:** **A worker row whose process died mid-run is closed at the next start — as `failed`, not as `cancelled`.** The end of a row is written only by `Finish` and `MarkCancelled`, and both need the running process. After a restart mid-run `Ended` stayed empty and `Outcome` at `run` (restart 04:36:40, the download last at item 1015 of 1417). The display reported „running" and the stop button answered `no cycle active — nothing to stop`, because there was no cycle left for it to abort. The lock file has long been treated as a corpse at start-up (`PipelineRunLock.ClearOnStartup`: „after a restart no previous run can still be alive"). The worker rows get the same counterpart: `WorkerRunRegistry.CloseOpenRunsOnStartup` closes every row with `Outcome=run` to `failed` with the detail „process ended mid-run (closed at the next start)" and writes a log line with the code F-M621 for it. **No user stop:** F-M293 records that a restart is not a button press, and `cancelled` would pin a stop on the operator that nobody pressed. `Ended` carries the moment the corpse was found, because at start-up nobody knows any more when the process really died.

**F-M623 [D]:** **The deferred cycle gets its own row under the workers — with reason and attempt count.** The refetch is the pipeline cycle, not a worker; that is why it was deliberately left out of `GetPendingFires` (F-M368) and appeared nowhere in the display. The daily anchor „daily 05:43" moved seven times in a row („Refetch fire … deferred — a run holds the lock"), while nothing stood under the Workers list — the reason existed only in the log. `ScribeSchedulerCoordinator.GetCycleDeferral` returns due time, reason and attempt count, the `WorkerRuns` answer is given `CycleDue`/`CycleWhy`/`CycleAttempt`/`CycleAttemptLimit`, and the page writes **one** line from them („Next cycle Fri 7:28 AM — run holds the lock (attempt 7/16)"), which disappears as soon as nothing is deferred. Deliberately **no** yellow worker row: the cycle has no own row it could hang on, and a permanent yellow on a foreign worker would be a lie. An expired due time is no report (F-M368).

**F-M640 [D]:** **Every complete season gets exactly one line from the pack phase — built, skipped or rejected, with reason.** For `tt31510819` S2 (MobLand, 10 of 10 episodes, one release group) at 20:36:46 only the candidate line stood in the log. After that never again a line about this season — neither pack nor rejection. Cause: the plan had no open pair for the season (each was already up as a single upload), so the pair loop ran empty, and the candidate fell silently out of the phase. From „everything already up" through „no pair open" to „skipped" nothing could be distinguished from the log — the operator asked whether a pack had been built, and the log could not answer. The phase therefore counts per season whether the plan had a pair for it, and writes **one** line for every season without a pair („nothing to pack from this run — the plan found no open pair for this season"). Seasons with pairs report per pair, unchanged.

**Addendum (12.2.0.2):** the line hangs not on the run but on the library — a season that is complete in the library but brings no episode from an **arrival run** no longer falls out silently. It reports at the debug level „complete in the library (N episode(s)), but this run brings none of them — an arrival run builds no pack". Exactly this case remained open on 19.11. after the first fix (MobLand S2: candidate 22:06:33, nothing after).

**Addendum (12.2.0.3):** F-M420 now applies literally — a **full** run (refetch, manual) packs from the **library and the plugin database**, not from its queue. Only the arrival run stays limited to its own episodes. Before, the narrowing to the queue was active in both cases. The full run on 20.11. had 71 complete seasons in front of it and produced **0** packs, because only 64 positions lay in the queue.

**Addendum (12.2.0.4):** two silent spots closed. (1) `SeasonPack.ReleaseGroup` gets a **path with extension** — with `… 5.1 BONE.mkv` the last dot token was the extension, the group token before it was never checked (Pierre S1 reported `release group "?"`). The video extension now falls away first. (2) A file that carries **bitmap subtitles only** (PGSSUB or similar) now says so: `bitmap only (PGSSUB) — no text subtitle in this file, nothing to upload from it`. Before, the text filter produced an empty list and the run stayed silent — exactly the Pierre case, six episodes, only `eng` as PGS/SDH.

## 16. Non-Goals

- N-1: Standalone downloader app or separate downloader plugin — integrated in Sub Scribe.
- N-3: Transcoding/opening bitmap subtitles
- N-5: Changes to Jellyfin core

## 17. Non-Functional Requirements

**NF-1:** Platform independence: pure IL DLL (net10.0), no native binding, no P/Invoke, no platform-specific dependencies.
**NF-2:** GPL-3.0-or-later (GNU GPL v3, or any later version) — the SPDX identifier the source headers carry. Every file that can carry a comment carries the same notice in its own syntax; a file whose format has no comment (JSON, PNG, SVG) carries none.
**NF-3:** Performance: no blocking of library scans; uploads asynchronous (background queue, IHostedService), extraction sequential per file
**NF-4:** Robustness: queue survives Jellyfin restart (persistent state files); no infinite retries
**NF-5:** No telemetry/external calls except api.subdl.com (+ dl.subdl.com for downloads, + api.themoviedb.org when a TMDB key is configured)
**NF-6:** Testability: test JF instance with a small test library
**NF-7:** Cross-platform discipline in code: no hardcoded path separators, no P/Invoke, no case-sensitive file operations without normalization
**NF-8:** **Manual stop button** ("■ Stop all uploads & downloads", General tab): one click cancels the running task for BOTH directions.

**NF-9:** **The specification is checked like the code — it is the eighth test suite.** Its own test checks without a Jellyfin host: every Contents counter matches the definitions in its section, every requirement id is defined exactly once, the test numbering is gapless from T1. Every test a requirement names exists, the header status names the version `build.yaml` builds, and no definition-shaped line escapes the pattern it counts with. Since it is a gate inside `scripts/release.sh`: the specification step aborts the release when the document and the build disagree. The `Test suites` step runs `anchorgate`, `forcedtitle` and `archivecache` right after the build, against the DLL just produced, so a suite that is not green stops the tag. Written by hand until then, the document had drifted twice while every release looked clean.

**NF-10:** **The documentation is published with the repository, so it carries no credentials, no addresses and no operator paths.** Every document under `docs/` and the README is checked for: a credential value behind `api_key`, `token`, `password` or `secret` (a state word such as `set` or a template such as `{key}` is not a value). Also checked: a provider key shape (a GitHub, OpenAI, Slack or AWS prefix followed by its payload); a hex string of 32 characters or more. Then: an e-mail address outside `example.org`; a private host address; an IBAN; and an absolute path of the operator's machine. Placeholders stand in their place: `<repo>`, `<dotnet>`, `<library>`, `<scratch>`. The whitepapers per source (`docs/engines/`) follow the same rule, which is why no account name, no field value and no library listing appears in them. Checked by T403. The same eight patterns run over any other repository and branch with `scripts/tests/doc-hygiene/check.py <tree>`. Public identifiers of a plugin database link (a Roku channel id, a content hash inside a GitHub raw link) and the contact address of an upstream project are the only allowed exceptions, each named with its reason in the script.

## 18. Acceptance Criteria

**A-1:** New episode with embedded subs appears in JF → all text subs automatically extracted, QA-checked, uploaded, without manual intervention
**A-2:** Dry run on test library produces a complete status report without API calls
**A-3:** Corrupted/garbled subtitle stream is detected by QA and skipped
**A-4:** Wrong-language test case → detected per config
**A-5:** Upload error (simulated 5xx) → retry + "failed" status, no crash
**A-6:** State survives JF restart: already uploaded files are not processed again
**A-7:** Library scan is not blocked by running uploads (<1s in-scan overhead)

## 19. Test Library

Every functional requirement (F-M*) carries at least one automated test case: a unit test where possible, an integration test where the behaviour needs one.

**The catalogue itself stands in `docs/TESTS.md`** — every test entry, verbatim, grouped by the chapter
of this specification it proves. There are **404 tests** in sixteen chapters, the numbering runs from
`T1` upward without a gap, and a number is never reused: a test keeps its number when it moves. What a
test claims as its check count is bound to the suite that carries it in `scripts/tests/counts.json`, and
that binding is what the metric runner verifies.
The row's survival of the UPLOAD direction is covered too — by `T134`, in section 6b2 of the GUI structure check.

**F-M498 [D]:** The tvst upload derives the **rip** from the release name and maps it onto the page's seven values (WEB, HDTV, HEVC, DVDRip, DVDSCR, BDRip, BluRay); source beats codec, and without a hit the field stays empty (a permitted value). Measured in the live run: all episodes `refused: rip must be one of …`, because `WEB-DL` was passed through — the pre-check therefore rightly discarded the value. The form field **Hearing impaired** is now served (`hi=1`, read off the real `/add.html` — a checkbox with `value="1"` that is not sent at all when unchecked); before, the knowledge from the HI detection was thrown away for tvst. Author stays empty as instructed. Checked by T219.

**F-M556 [U]:** **The head of the run also knows the loose subtitles — otherwise nothing is ever sent.** Finding after the deploy of 439: a cycle from 21:28 with **292** lines `upload file-complete for every upload source`, **0** extractions, **0** uploads. The queuer queues an item when the embedded tracks are settled for the source but a **loose subtitle** beside them is still open (`SidecarsNeedWork`: neither uploaded nor rejected nor known content, no provenance gate). The run's head asked only `IsSubtitlesUploadedFor` over the embedded tracks and threw such items away in every cycle — the loose subtitle was never sent, and the item came back forever. New: the rule stands **once** in `UploadPipeline.SidecarsNeedWorkFor` (the queuer only calls it), and the head skips only when `seitenwagenOffen` is false as well — so only sources that queued the item, and per source, not "somewhere". An unreadable loose subtitle and a missing media hash answer "work instead of skip".

**F-M555 [U]:** **Every TMDb answer is deposited in ONE place and answered from there.** Concept decision: TMDb answers repeat without end, also for titles finished long ago. Over the day **1221** TMDb requests to **305** different addresses — **916** repetitions, the same `/tv/1416` 21×, the same `/movie/1423191` 19×, some twice in the same second. The "id test" ran **2173**× for 183 different titles (The Middle alone 495×, Elle again for every episode). Cause: the resolver asks a cache before **9 of 13** sending sites and not at four sites (`/tv/{id}/external_ids`, the search, the retry attempt) — the cache helped only where somebody had thought of it. New: `Api/TmdbCacheHandler.cs` sits in the HTTP chain (both chains) and answers **every GET request to `api.themoviedb.org`** from `TmdbAnswerStore`; only a miss goes to the network, and a status 200 is deposited. No future call site can thus forget the cache. Other addresses (SubDL, tvsubtitles, moviesubtitles) the handler does not look at. The key is the address **without `api_key`** and carries the prefix `url:` — an address key can therefore never collide with a resolver question sentence. Only 200 is deposited: an error is no answer.

**F-M554 [U]:** **The head of the run judges the tracks that lie in the file — not the rows the plugin database knows.** Concept decision: The run reports itself as running while nothing is sent. In the cycle from 21:09 stood **285** files with `upload file-complete for every upload source`, **178** with `every text stream was rejected earlier`, **129** self-echo and **0** uploads. Cause: `IsSubtitlesUploadedFor` checked the file mark and then `tracks.All(...)` over the **stored** embed rows. If the plugin database knows only part of the tracks — say because an earlier version aborted at the file or tracks were added later —, then all known rows are settled and the file counts as done, although most tracks were never looked at. The queuer on the other hand asks the item's real positions and says "open": the same item was thereby queued in every cycle and thrown away in the head at once, without anything ever being sent.

New: `IsSubtitlesUploadedFor(mediaHash, provider, requiredPositions)` — mark **and** every present track must be settled for this source; the head passes the file's tracks along (`kopfPositionen`, the same counting as F-M5, forced loose subtitles stay out). Without a track specification the old reading stays, without a file mark nothing is skipped.

**F-M553 [U]:** **The plugin is also called "Sub Scribe" in the log, not "SubdlScribe".** Concept decision: The log category names the plugin as it is displayed, not its .NET namespace. The category of every line read `Jellyfin.Plugin.SubdlScribe.Data.SubdlDbContext` — that is the .NET namespace, not the display name; which had long been "Sub Scribe". Now: namespace `Jellyfin.Plugin.SubSub …` **no**: `Jellyfin.Plugin.SubScribe`, and the 25 types with `Subdl…` are called `Scribe…` (503 namespace occurrences, 773 type names, 21 files renamed). **The assembly stays `Jellyfin.Plugin.SubdlSync`** — the data folder with plugin database and configuration hangs on it; renaming the assembly would create an empty files area. Equally **unchanged** stay the stored keys and the interface to the GUI. The configuration fields `SubdlUploadEnabled`/`SubdlDownloadEnabled`, the enum value `SubdlFirst` (stands that way in the stored configuration), the status field `SubdlLogin` and the routes `Plugins/SubdlSync`, `Plugins/SubdlReset` (controller now `ScribeReset`), `Plugins/SubdlQuota` (controller now `ScribeQuotaService`). They carry data and addresses, not identity. A rename would have reset settings and left surface buttons running into the void (in the first pass exactly that happened, reverted here). The GUI file was pulled along (22 identifiers), so that surface and test run read the same names.

**Addendum 20:53 (prod):** the first pass had also caught the **plugin database's column names** — `SubdlId`/`SubdlUploadId`/`SubdlDownloadId`/`SubdlUploadedAt` keep those names in the existing SQLite files area, and EF maps the property name onto the column name. Result: `SQLite Error 1: 'no such column: e.ScribeId'` and an aborted cycle. These four names are turned back; the entity model now matches the state before the rename again (check: property list against the commit before it, no differences). No check of its own: a rename is covered by the build and the existing suites (`endpoints` 225, `gui-structure` 338, metric 43/43).

**F-M552 [U]:** **An item that already stands in the queue is refreshed instead of frozen.** Concept decision: An entry already known to the queue is refreshed at every seeding, so that no item keeps a stale source specification. In the last full cycle (18:49–20:11) stood **341** uploads against roughly **345** "already up" skips. The largest single item was a file with **38 text tracks** for which **SubDL** was asked although it was only open for tvst ("still open: tvsubtitles"). Cause: `MergeQueue` added only **new** items and never touched known ones. An entry from a version before F-M549 thus carried no source specification forever, and the run kept asking all sources for it — 38 plugin database queries for a source that has nothing to do with the file any more. Now the seeder's fresh answer replaces the old specification (`vorhanden.Sources = c.Sources`, plus path and name); an **empty** answer does not delete a known specification — not guessing holds in both directions.

**F-M551 [U]:** **The download line names the source too.** Concept decision: The download log line names its source too, which it did not before. The line `SAVED {Path} (lang=…, release "…", subdl="…", author=…, imdb=…, tmdb=…)` named release, SubDL name, author and identifiers, but not the source; with three sources that is the one specification that was missing. Now it stands directly behind `SAVED`: `SAVED tvsubtitles {Path} …`, likewise for the HI version (`SAVED tvsubtitles HI variant …`). The source comes from the candidate (`cand.Provider`/`hi.Provider`), not from a search — the lists of both family addresses are mixed, so a candidate can come from a search that ran under a different switch.

**F-M550 [U]:** **The log line names the source, and the log stamp is called like the plugin.** Concept decision: The log line names the source that answered, and the log stamp is called like the plugin. (1) The line `UPLOADED {File} [{Lang}]` named no source at all — before it stood only the old stamp `[SubDL]`, and that reads like "uploaded to SubDL". Now the line carries the answering source: `UPLOADED … → tvsubtitles`. The source travels along in the result (`UploadResult.Source`), set where it is known (chain), not at the caller; unknown means `no source answered`, nothing is guessed. `UPLOAD FAILED` and `SKIP` carry it as well. (2) All 589 log stamps `[SubDL…]` are now called `[Sub Scribe…]` (34 files). The plugin name in the Jellyfin log (`Loaded plugin: "Sub Scribe"`) was already right; the 40 lines "Skipping disabled plugin … of SubDL Scribe" come from the **old package 12.1.12.380**, which still lies in the plugin folder.

**F-M549 [U]:** **The sources per item stand at the item and survive the restart.** Concept decision: The source specification belongs at the item itself, because the queues are separate. The queue **was** separate, but the separation got lost on the way. The source specification hung as a map **beside** the queue and was filled only by the last scan; the queue itself is merged across scans. For every item from an earlier scan the map was empty, the run fell back to "all switched-on sources" — and a file that only needed tvst reported SubDL as open again. Measurement: **116** files with "subdl open" against **67** SubDL entries in the queue. Now `QueueItem.Sources` carries the sources (as a **name**, not as a number — a reordering of the enum must not reinterpret the stored queue), it is stored with `cycle-queues.json`, and the dispatcher builds the map from the items (`RebuildUploadBySource`) instead of from the scan. Items without a specification (queue from a version before F-M549) keep asking all sources — not guessing.

**F-M548 [U]:** **The transfer rhythm hangs behind the deposit, in the download too.** Concept decision: The rate limit also applies after a file was downloaded. Up to here the pause stood **before** the fetch (F-M26, download→download) and before the HI fetch in the fit gap (F-M310). It punished the attempt, not the transfer: a fetch that failed still cost the rhythm. A successful fetch stood last without a pause. Now `TransferPauseMsLessFit()` sits at the **two** places where a file is really deposited (HI version and main track); the kept original version without significance counts along, it fetched bytes. Both before-pauses are removed, otherwise a download would pay twice. Still not: the dry run (`DRY-RUN HI`) transfers nothing and therefore waits for nothing — it has its own counter and no bytes.

**F-M547 [U]:** **Limits hang on the external request and on the real transfer — otherwise there are none.** Concept decision: An adjustable rate limit of 400/h applies only after a fetch, and only when an external API (TMDb, SubDL, tvst, mst) is asked does the adjustable 0.5 sec API pause apply — always afterwards, otherwise there are no limits. The finding before: the transfer rhythm (3600/`UploadsPerHour`, ±30 %) hung on the **end of every processed file**, even when only the plugin database answered. At 400/h that is 9 s per file, in the cycle 15:30–16:08 around 250 files, so ~38 minutes of waiting without a single network call. The TMDb resolver itself has no limiter at all (`TryCache` before every `SendAsync`).

Now: (1) `PacingHandler` as a `DelegatingHandler` **inside**, below `TransientRetryHandler`, in both HTTP chains. After **every** external answer (TMDb, SubDL, tvst, mst, also after a retry attempt) `ExternalApiPacer` pulls the configured pause (`MinCallPauseSec`, default 0,5 s, 0 = off). Pauses at the call sites would be 40+ sites with a guaranteed forgotten exception, a handler inherits for new sources without any doing. (2) The transfer rhythm applies only when this file really went out (`itemUploadedThisRun`). (3) The double 0,5-s pause before the download search drops away — it stood before *and* after the request.

**F-M546 [U]:** **One queue per source.** Concept decision: The queue is kept per source, one queue per source instead of one file list for all. Up to here the queue was ONE file list. The run asked all switched-on sources per file. A source that did not want the item at all kept it open that way. The head skip could never be the cheap one-mark question of 12.1.12.378 (there SubDL was the only upload target, which is why **one** mark sufficed for the whole file). Now the seeder keeps the queue **per source**: `UploadTodoSources` yields the sources the item really still needs, `SeedSnapshot.UploadBySource` holds them, `Upload` stays the union (which items the run touches). The run narrows its sources per item to that (`QueueFilterBySource` → `_queuedSourcesForItem` → `QueuedForSource`), both in the source list and in the gate `AnyUploadSourceStillWants`. Without a report (no previous scan, stored queue after a restart) everything stays as before — the narrowing may only leave out what was not wanted anyway. The scan line names the numbers per source: `977 upload queued (subdl 92, tvsubtitles 61, moviesubtitles 4)`.

**F-M545 [U]:** **The TMDb plugin database is attached on every path on which a context arises.** Concept decision: The plugin database must be attached wherever a context arises, not only on the reset path. At 16:01, in the cycle 15:48 **42 of 42** different TMDb questions were repetitions from earlier cycles — the plugin database was **empty** the whole time. Cause: `TmdbAnswerStore.Attach` stood only in `RecreateDbContext` (the path after a reset); the normal start builds the context in the getter `SharedDbContext`, and the call was missing there. Two consequences: (1) the attachment now stands at **both** places where a context arises; (2) the plugin database fetches a missing context itself (`Plugin.Instance?.SharedDbContext?.Context`) instead of silently working only in memory. The lesson belongs to the finding: the test run covered the **plugin database** (answer survives a reopening) — not its **wiring**; two source-text checks now stand for that.

**F-M544 [U]:** **The media kind at the file head comes from the item — a series is no longer measured against moviesubtitles.org.** Concept decision: A series goes to tvsubtitles and is then no longer considered for moviesubtitles. That is how it is built (F-M509: the family has **one** address per media kind, series → tvsubtitles.net, film → moviesubtitles.org, SubDL for both). The run's head did not know the media kind and therefore left **both** addresses open. Measured in the cycle 10:33–12:10: of 92 files with an open source, **80** stood as `still open: "tvsubtitles, moviesubtitles"` — series for which moviesubtitles.org never gets a mark, because we do not upload series there. These files never became "done" and returned in every cycle. Now the item decides: `Episode` ⇒ series, `Movie` ⇒ film; every other kind stays unknown and keeps F-M509's cautious reading (both addresses open), so that no file silently counts as done. The chain itself keeps asking with the real media kind.

**F-M543 [U]:** **The episode page is searched once per run, not per request.** Measured in the upload cycle 10:33–12:10: **1.988** requests to `episode-<id>.html` and **350** to `tvshow-<show>-<season>.html` — for a run that should predominantly look into the plugin database. Cause: `FindEpisodePageAsync` fetches the season's episode list and then **every candidate page's heading**, to read (season, episode); for episode 22 thus 22 requests, quadratic for a season. And `EpisodePagesAsync` fetched the season page anew on every call. Now the client remembers the heading per page (it never changes), the episode list per (series, season) and the found page per episode — with a ceiling. The episode list is discarded as soon as **we** create something (`AddShowAsync`) and on request per season (`InvalidateSeasonPages`); the headings stay, because a page does not change its number. Measured on testability: the same question 0 requests, a later episode of the same season +2 instead of 5 again, after a creation exactly one new season fetch.

**F-M542 [U]:** **The TMDb answers lie in the plugin database, not only in memory.** Concept decision: The answers go into the plugin database, so that a plugin restart does not throw them away. Measured the same day: **555** TMDb requests, **272** different URLs — the 283 repetitions did not come from one run (a cycle asked 24 different, 2 twice), but from the day's **31 plugin starts**. `TmdbImdbResolver` held its answers in a static dictionary, every restart threw them away. Stored is the **question** as key and the **answer** as value, unchanged the resolver's pipe line (`imdb|tmdb|gefunden|istSerie`, `imdb:<tmdbId>|S`, `tmdb:<imdbId>|M`, `showByEp:…`, `1`/`0`): no films, no subtitles, no metadata. New table `tmdb_answers` (`Id`, `Value`, `Written`), created like `packs` with `CREATE TABLE IF NOT EXISTS`, thus without a schema change and without touching existing data. Durability: an answer **with** an id holds 180 days, a miss **7** days — otherwise a series would stay "not found" forever although TMDb adds titles later; the cleanup at start deletes what no longer holds after both deadlines. An empty value is the real answer "asked, found nothing" and comes back to the caller as `null`, as from the dictionary. Without an attached plugin database the resolver keeps working in memory only.

**F-M541 [U]:** **A file whose text tracks are all discarded ends before the ffmpeg pass.** Found in the cycle 14:26 at `The.Assassin.S02E02`: `still open: "tvsubtitles, subdl, moviesubtitles"` → extraction → two `SKIP … "uploaded earlier in this run (self-echo lock, F-M142)"` → `no stream survived local screening (F-M69)`, so nothing uploaded and **no file mark**. Cause: the source list at the file head asks only for **uploaded** marks (F-M499). A discarded track, however, can no longer be uploaded by anyone, so the file stayed "open" for all three sources and was unpacked again in every full scan and skipped track by track. Now: if **all** text positions carry a discard reason (`EmbedRejectedReason`), the entry ends with one line per file — the decision per track (F-M68) stays unchanged, this exit does the same one level higher. By the way: the message text "uploaded earlier in this run" was wrong — the reason comes from the plugin database, thus from an **earlier** run.

**F-M540 [U]:** **The queue asks the page's language — and the marks stay per source (F-M499).** Concept decision: The seeder checks before the mark whether the page carries any tagged language, and skips what is already marked as uploaded — per source, each its own queue. The pre-filter did not know the page's language, which is why Cunk on Cinema S01E01–E06 stood in the queue in every run: 6 entries, 22 languages each, of which 8 (ID/HE/NO/TH/VI/HR/TL/ET) that tvst does not carry — 156 self-echo lines, **0** real uploads. Now `UploadTodoFor` asks before the mark check: does this page carry any of the tagged languages of this file at all? No ⇒ the entry does not belong in its queue. An untagged track keeps the entry open (language only after extraction, no guessing). The mark check itself stays unchanged at `ArePositionsTerminalFor`/`CountKnownPairsFor` per source (F-M499). A first attempt replaced it with a direct `IsUploadedTo` per language and let **1087** entries into the queue instead of 0–1, so it was discarded. The pair question is stricter than the mark, because it compares spelling and HI flag exactly.

**F-M539 [U]:** **A settled entry ends before the ffmpeg pass.** Concept decision: A settled entry is skipped right away, without further check. Before, the one ffmpeg pass over all text tracks ran first for **every** file (F-M5) and only then the decision per track fell — that day 633 self-echo hits and 288 `already-uploaded:subdl` at files where there was nothing to do. Now the pipeline asks directly after the track list: if **no** recorded pair wants an allowed source any more (`AnyUploadSourceStillWants`, which reads the switches per source) **and** every text track has its final state in the plugin database (`ArePositionsTerminal`). Then the entry ends without extraction, without language mapping, without source call — counter `SettledItems`, one log line. Safe, because the media hash covers the **file content**: a freshly ripped file has a different hash, and a switch turned on later opens the entry again, because the pair question reads the switches per source.

**F-M538 [U]:** **Each page carries only its own languages — what it does not carry no longer enters the chain.** Concept decision: Languages tvst does not support no longer enter the chain for tvst, and a different language set applies for SubDL. Measured in the prod log of the same day: **174** refusals `tvsubtitles.net has no language 'X'` (ID 28×, HE 18×, NO 18×, TH 16×, VI 16×, HR 14×, TL 12×, ET 6×). Each of these pairs was not only sent through the chain again in every run, but additionally booked as **`[SubDL] UPLOAD FAILED …: tvsubtitles.net has no language 'X'`**, which blocked the file mark. Now `AnyUploadSourceStillWants` asks per source whether the page carries the language at all (`TvSubtitlesClient.SiteLanguage` for series, `MovieSubtitlesClient.SiteLanguage` for films, each its own table: tvst 24 codes, mst 16 plus ISO3 aliases), and the chain answers with `Skipped("second-source-no-language")` instead of `Failed`. SubDL is **not** filtered — it has its own, wider language set (Hindi is on neither of the two pages, on SubDL it is).

**F-M537 [U/D]:** **The client goes along on every request to tvst.** Concept decision: The client is sent along on every request to tvst — it does not hurt. Every request to tvsubtitles.net additionally carries the parameter `client=sub-scribe-<Assembly-Version>` — the same idea as SubDL's `&client=` (F-M343), only without a documented parameter on the page. It hangs in **one** place (`WithClient`) and thus on search, show list, show/episode/subtitle/download page, the report form (`report_bad.php`), the creation (`add-tvshow1.php`) and the upload (`add1.php`); the user agent carries the same name and the same version. **Compatibility measured live beforehand:** `download-<id>.html` answers byte-identically, `search2.php` and the show/subtitle pages differ only in their own language-switch links, which mirror the query back. A POST to `add1.php` answers the same with and without the parameter (HTTP 200, empty body). PHP reads `$_POST`/`$_FILES`, an unknown query parameter changes nothing about the upload. The comment field stays untouched (TMDb/IMDb only).

**F-M536 [D]:** **Differently encoded subtitles are read via the file's language, not guessed.** Concept decision: Differently encoded files are converted and read correctly via the file's language, never guessed. Measured on the page's real files: Arabic lay as **cp1256** (Prison Break 1x22), Greek as **cp1253** and Bulgarian as **cp1251** (Game of Thrones 1x10), Russian by contrast as UTF-8 — the page normalises nothing. `DecodeSrt` knew only BOM and UTF-8 until then, so each of these texts became `U+FFFD` and was destroyed before QA, hash and writing. Now: BOM first, then **strict UTF-8**; if that fails, the candidate's **known language** decides the codepage (`ar`→1256, `gr`/`el`→1253, `bg`/`ru`/`ua`→1251, `cz`/`pl`/`hu`/`ro`/`sl`→1250, `tr`→1254, `he`→1255, `jp`→932, `cn`→936, `ko`→949, `th`→874, `vi`→1258), region and three-letter forms included. Without a language nothing is **not** guessed — then the old behaviour stays. UTF-8 payloads never reach the codepage branch, everything that works today stays unchanged.

**F-M535 [D]:** **The report dispatch keeps running while the tracks still hang.** Concept decision: The report dispatch keeps running until the flush is done, so that the 2 reports of the statistics are countable in the log. The statistics counted **2** reports since the reset 06:22:50, in the log only **one** `API call POST …/report` line was to be seen. The cause was the order at the run's end: `_api.DebugTrace -= OnApiTrace` stood **before** `FlushReportsAsync`, so the POST line of every report that went out in the flush was discarded — the counting was right, the evidence was missing. Now the flush comes first, then the detach. Thereby every accepted report leaves both lines (POST + per-item), and the number in the statistics can be counted back in the log.

**F-M534 [D]:** **The tvst stub is gone from the upload tab too.** Concept decision: The tvst heading is deleted in the upload tab too. Thereby `<h3>tvst</h3>` stands **nowhere** in the surface any more — the family is one source with one switch per direction, the heading was the remnant of the old two-source view (F-M525 had caught only the download tab). The statement "one source, two addresses, TMDb decides" is still carried by the description line under the switch.

**F-M533 [D]:** **The same robustness and the same reporting system for tvsubtitles.net.** Concept decision: tvsubtitles.net gets the same report class and the same robustness against wrong metadata, through its report-bad field. The page has **no** JSON, but its own value lists — those are the yardstick (`Api/TvstMetadataChecks.cs`). `language` must be one of the form's **24** codes, `rip` one of the **seven** values (or empty, the page's normal case). The episode page's season/episode statement must lie in the ranges the form accepts (**season 1–22, episode 0–100**). Deviation ⇒ the same report (`Metadata format mismatch.` / `Attribute: … (documented: …, received: …)` / `Subtitle: <id> (<release>, <lang>)` / `Automated report by …`), the entry drops, `LastRejectedByFormat` counts, and the number goes via `_tvstFormatRejected` into the same closing line as with SubDL.

The report route is the **page's own form**, `POST /report_bad.php` (multipart: `sid`, `go=1`, `message` — linked on `subtitle-<id>.html` as "Report bad"); the answer is read defensively: 429 = deferred, a page without thanks = failed, and a failure changes nothing about the run. The switch is the same (`ReportBrokenSubtitles`). That is why it carries **no source marker** in the GUI any more (F-M526: "SubDL only" removed there; the remaining four markers stay, because they still concern SubDL only — with F-M641 the marker on the pack switch has gone too).

**F-M532 [D]:** **Every SubDL answer is checked for field shape, not only the search.** Concept decision: The field-shape check covers all attribute mismatches, not only the search. The same report, the same rule (report, discard the value, keep running), now at four further places. The **film block** `results[0]` (`imdb_id` text, `tmdb_id` number or number text; finding ⇒ the ids are **not** used). The **quota answer** (`usage.search`/`usage.downloads` with `used`/`limit`/`remaining` a whole number, `reset_at` text; finding ⇒ the counter reading counts as **unknown**). The **own subtitle list** (`subtitle.id`, `subtitle.subtitles_uploaded_id`, `subtitle.tmdb_id`, `n_id`, `name`, `language`, `lang`, `imdb_id`; finding ⇒ the entry drops). The **upload answer** (`subtitle.sd_id` text, `subtitle.subtitle_id` whole number; finding ⇒ the id is not read, the row stays without an id instead of with a guessed one). Because quota and list have no logger of their own, they report via a hook (`ScribeFieldSchema.OnMismatch`) that the client sets at build time — one reporting site, five answers. The run aborts nowhere.

**F-M531 [D]:** **The report on a metadata error says only what is wrong.** Concept decision: The report on a metadata error names only what is wrong — the affected attribute and the affected subtitle — and tells no stories. Text, four lines, nothing beyond that:

```
Metadata format mismatch.
Attribute: framerate (documented: a whole number, received: 23.98)
Subtitle: 3676577-8593549 ('The.Odyssey.2026.720p.WEBRip', EN)

Automated report by Sub Scribe (Jellyfin plugin) v12.1.12.413, account <account>.
```

No reason, no backstory, no request, no suggestion. Built in `Pipeline/MetadataMismatchReports.cs`; the category is SubDL's `wrong_release_info` (the list carries a value that contradicts its own documentation). The text goes **into the log** and — when `ReportBrokenSubtitles` is on and a report id (`nId` from `subtitlePage`) is present — to SubDL by the same route as the defect reports (F-M450); a failure there changes nothing about the run.

**F-M530 [D]:** **Every metadata field is guarded against a wrong entry: report, drop the hit, continue the run.** Concept decision: Every metadata field is guarded against a wrong entry — report, drop the hit, continue the run, and no analysis of how often that occurs. The check now sits in a **table** (`Api/ScribeFieldSchema.cs`) and no longer in whatever the parser happens to read. Per field the shape promised by `subdl.com/api-doc` — `season`, `episode`, `episode_from/end`, `framerate`, `download_count`, `size` whole number; `hi`, `full_season`, `followed`, `muted` true/false; `release_name`, `language`, `name`, `author`, `url`, `subtitlePage`, `file_n_id`, `format`, `md5` text. `fps` and `match_score` number **or** number text.

Checked is the entry **and every file in `unpack_files[]`** (a finding names the position, e.g. `unpack_files[0].season`). A field that is missing or `null` is **no** finding — SubDL omits what does not apply. Deviation ⇒ one log line per search with **sub and attribute** (`[SubDL] 1 Treffer verworfen — … "framerate" = 23.98 (Number) → documented as a whole number`), the hit is dropped, `LastRejectedByFormat` counts, and `summary.RejectedCandidates` brings the number into the closing line of the run. The run **never** aborts: the readers do not throw (`JsonAnomalies`, `JsonFields`), and on top of that the batch catches `FormatException` per hit. `fps` as text is explicitly **no** finding: the documentation calls it "human-readable" and we read it correctly — throwing away a quarter of all hits over that would be the wrong answer to a documentation detail.

**F-M529 [D]:** **A hit with an undocumented field shape is dropped and counted — not reported, not rescued.** Concept decision: Such subs are rejected and the counter bumped instead of being reported. The lenient reader from F-M528 stays, but only as **detection**. If a hit carries such a field, it drops out of the batch (`continue`), `LastRejectedByFormat` counts it, and the number goes via `summary.RejectedCandidates` into the **closing line of the run** (`… rejected …`) so that it is visible without a debug log. One log line per search with sub and attribute: `[SubDL] 1 Treffer verworfen — Feldgestalt nicht wie dokumentiert: "framerate" = 23.98 (Number) → read as 23`. Deliberately **no** individual report to SubDL: the report would be a separate report per upload about a field that SubDL fills differently per file — counted and named is the track, without flooding their report form.

**F-M528 [D]:** **A foreign number in SubDL's answer costs a hit, never the run — and is logged.** Concept decision: A foreign number in SubDL's answer costs a hit, never the run, and is logged. Two cycles (06:45:05 and 07:44:46) ended with `FormatException: One of the identified items was in an invalid format.`, stack: `SubtitleCandidate.FromJson` ← `ScribeApiClient.SearchSubtitlesAsync`. Cause: `GetInt` only checked `ValueKind == Number` and then called `GetInt32()`; that throws on a **number with a decimal part** or **outside the Int32 range**. A single such hit tore through the pipeline right up into the cycle.

New, in three parts: (1) `JsonAnomalies` reads numbers leniently — whole number, number with decimal part (truncated), too large number (clamped), number as text (`"3"`, `1e3`), everything else 0, **without** an exception. All hard `GetInt32()` calls on raw API elements in the SubDL client are replaced. (2) The candidate batch catches `FormatException` per hit: the hit drops out, the search continues. (3) Whatever had to be read leniently stands in the log **once per search**: `[SubDL] JSON mit Nachsicht gelesen: "season" = 1.7 (Number) → read as 1`. That is the proof that the value came from the other side, and the basis for a report to SubDL. TMDb stays out of it: its API is type-safe, there is no lenient reading there.

**F-M527 [D]:** **The source badge sits IN the switch's text, never beside it.** Concept decision: The source badge sits inside the switch's text, never beside it. Jellyfin does not draw the check mark on the box but via a **neighbour chain** — `.emby-checkbox:checked + span + .checkboxOutline > .checkboxIcon-checked`. The `emby-checkbox` component itself attaches the `.checkboxOutline` element to the **end** of the label with `insertAdjacentHTML('beforeend', …)`. If another `<span>` stood between the text span and the outline (the badge from F-M526), the chain was broken: the click toggled the input, but **no check mark** appeared — the box looked untickable. New: the badge sits **inside** the text span (`<span>Continue after daily limit <span class="src-badge …">SubDL only</span></span>`), so `input + span + .checkboxOutline` stays intact.

Affected were all switches in the upload/download tab that carried a badge (with F-M641 the pack switch has dropped out of the group, because tvst builds packs too). The two number fields (archive cache) are untouched, because their badge sits in the `label` element, which is not an `emby-checkbox`. In addition, check F-M338 now compares the **visible** sentence instead of the raw spans, so that the badge does not distort the parity of the two directions.

**F-M526 [D]:** **Every setting that concerns only ONE source says so at the switch.** Concept decision: Every setting that concerns only one source says so at the switch, as SubDL only or TVST only. The badge sits **on the control itself** (`<span class="src-badge src-subdl">SubDL only</span>`, blue; the green class `src-tvst` is ready), not in a legend further down. Marked are the **unambiguous** cases, evidenced at the code. `UploadContinueAfterLimit` and `DownloadContinueAfterLimit` (SubDL's daily limit, `WaitOutDailyLimitAsync`), `DownloadHearingImpaired` (the HI pool arises from the second search with `&hi=1` — tvst knows no SDH tag), `AllowAiTranslatedSubs` (SUBDL AI), `ReportBrokenSubtitles` (the report goes to SubDL) and `DownloadArchiveCacheDays`/`DownloadArchiveCacheMaxMb` (`_api.ArchiveDiskCache`, SubDL's pack cache).

**Addendum (F-M665):** the two archive-cache fields no longer carry **any** source badge. Since the tvst season ZIP lies in the same disk cache (own key space `tvst.<Serie>.<Staffel>.<Sprache>`), "SubDL only" is wrong there. The badge including its tooltip ("tvst does not have this") is removed, the field text remains. So there were **four** badges; with **F-M664** the HI switch has also dropped out (tvst delivers HI via ear tag and `.HI.` marker as well) — **three** remain: `UploadContinueAfterLimit`, `DownloadContinueAfterLimit`, `AllowAiTranslatedSubs`. At the same time the two cache descriptions now name the code's defaults (**7** days, **256** MB) instead of 14 and 512. **No** badge sits on anything that affects both sources (TMDb key, languages, quality checks, auto-match, library refresh, rate limits, order) — and **no** tvst-only case exists at present: the two tvst switches name themselves, everything else of the family is an address under a switch.

**F-M525 [D]:** **The download tab no longer carries a "tvst" heading, and the GUI check suite counts honestly again.** Concept decision: The tvst heading stub goes from the download tab. In the download tab the line `<h3>tvst</h3>` sits directly above the one family switch — next to "Download enabled — SubDL" it is a stub, because the switch names itself; it is **removed**. In the upload tab it stays, there it separates the family block from the SubDL blocks. When catching up the checks, a **larger fault** came out: the suite `gui-structure` computed `failed` in the middle of the file (line 1723) instead of at the end — **every** check after that was invisible to the summary. It reported `326/326 OK` and exit 0 while **ten** checks were red: the old F-M508/F-M509 sentences, which still demanded the **two address switches** and the **German** family heading — exactly what F-M520 removed. New: the count stands at the end, the outdated sentences are replaced by F-M520/F-M525 checks (one switch per direction, addresses are data, order two-valued, heading only in the upload tab). Suite: **321/321 green**, exit 0.

**F-M524 [D]:** **A failed cycle names the place, not just the sentence.** Concept decision: A failed cycle must name the place where it failed, not only the sentence. The prod log showed at 06:45:05 `"scheduled-download" cycle failed: "One of the identified items was in an invalid format."`. That is the default message of `FormatException` (from a `string.Format`-like call) and contains **no** indication of where it arose; `ex.Message` was all the line carried. The run itself was finished at that point (`download: finished — lock released.`), so it was the cycle's bookkeeping **afterwards** that tipped — outward presentation and cause were thus apart, and a diagnosis was not possible. New: the exception goes **with stack** into the log (`_logger.LogError(ex, …)`), the plain-text message stays beside it. The next case names file and line.

**F-M523 [D]:** **The series number from tvsubtitles.net is now also stored on download.** Concept decision: The series and movie numbers from tvst are kept in the plugin database on download too. The fields exist since schema 13 (`MediaEntity.TvstShowId`, `MediaEntity.MsubMovieId`), but they were only written on the **upload** path (the series lookup path). The download resolved the number anew on every search via the series list (`ShowsAsync` + `ResolveShow`) and threw it away. That is why 512 for The Middle from the season upload stood in the plugin database, while MobLand and Stranger Things from the download did not. Every run fetched the list again (exactly what F-M501 was meant to prevent).

New: the endpoint places the resolved number in `TvSubtitlesClient.LastResolvedShowId` (reset at the start of **every** search, so that the previous title's number never survives). The download pipeline writes it via **the same** route as the movie number (`Registry.SetTvstShowId`, F-M517: hash first, path as second address, nothing on ambiguity) and logs `„<Titel>" ist Serie <Id> — <wie>`. Three rules: a **dry run** writes nothing (F-M22), an **already stored** number is left untouched (it was verified when it was written), and **nothing is created** on download — creation remains the business of the upload path (F-M513).

**F-M522 [D]:** **The series list is fetched once per run, not once per search.** Concept decision: The series list is fetched once per run and remembered for that run only. `https://www.tvsubtitles.net/add.html` is the only page of the site on which series stand with their number — the download needs this number for `tvshow-<id>-<staffel>.html`. One run on prod asked the page **53 times** for a list that cannot change during the run. New: `TvSubtitlesClient.ShowsAsync` keeps the list in the client; a client lives **one** run, so "remembered" equals "for this run" and nothing survives it. Two rules keep that honest. An **empty** answer is **never** stored (a truncated page would otherwise produce "no such series" for a whole run). A **real** creation discards the plugin database (that is the only event in the run that changes the list; the caller looks up the new number directly afterwards). A dry run does not reach this place, so the rule "a dry run changes nothing" stays untouched.

**F-M521 [D]:** **Both addresses of the tvst family write their inquiries into the log — in both directions.** Concept decision: The tvst family logs its inquiries in both directions, as SubDL always did. SubDL had always logged `API call`/`API response`, tvsubtitles.net and moviesubtitles.org **nothing**. So "tvst was asked" was a claim without proof — the line `0 tvsubtitles candidate(s)` looked exactly like a run in which the address was never addressed (measured on prod: "asked: tvsubtitles, subdl" without a single line from that source). New: **two lines per inquiry at Debug** (`API call GET /search.php`, `API response GET /search.php → 200`), following the existing rule F-M24a — never at Verbose, so **zero** additional lines in normal operation. The lines name **method and path, never the query** (`SiteTrace.Short`), so that neither search term nor identifier gets into a log file. Search, fetch, movie/series creation and uploading run through the same call sites, so **one** wiring covers both directions: download pipeline (`_tvClient`, `_msubClient`) and upload pipeline (`_tvstClient`, `_msubClient`) hang on the same channel as SubDL. The **Verbose** summary line additionally names the identifier: `tvsubtitles (HTTP 200, 0 hit(s))`, on a missing answer `no answer` — the line that logged before.

**F-M520 [D]:** **The tvst family has ONE switch per direction in the interface — the addresses are data fields, not controls.** Concept decision: The tvst family has one switch per direction, the addresses are data fields and TMDb decides which one an object gets. The download and the upload tab each show one line "tvst" with one switch. The two stored address fields (`TvSubtitlesDownloadEnabled`/`MovieSubtitlesDownloadEnabled`, `…Upload…`) are set **together** on save and read as the **effective** state on display (on as soon as one address is on; for download `null` counts as on). Which address an object gets is decided by **TMDb** — series → tvsubtitles.net, movie → moviesubtitles.org (`SourceOrder.FamilyPage`), not by the operator. The page explains this in **one** sentence and names "tvst first" in the order selection instead of both addresses; the status lights stay per address, because one can answer while the other does not.

**F-M519 [D]:** **loose subtitles from the original block ("5xer") are never uploaded.** Concept decision: Loose subtitles from the original block, the 5xer files, are never uploaded. Files with a slot number **50–99** are the unsynchronized originals that a correction keeps beside its result (F-M315) — working files, not goods. They drop out at the **one** place where upload candidates enter the run (`UploadPipeline.FindLooseSrts`), and the question is decided **by the name alone** (`SidecarNaming.IsOriginalSidecar`, slot via `SlotOf`) — no file is opened for it, and a season with 24 episodes pays nothing for it. Slot **50** counts too, although no writer assigns it any more: old trees carry such names, and originals they are anyway. The **corrected** block (slot 1–49) remains a candidate as before.

**F-M301:** **The specification is checked like the code.** A section that loses a rule turns red, and no other check catches that.

## 20. References

- Plugin template: github.com/jellyfin/jellyfin-plugin-template
- Reference plugin (download): Jellyfin OpenSubtitles plugin
- SubDL API: `https://api.subdl.com`

**F-M346:** The download's filename fallback is the last resort — it runs only when an item has no id at
all — and there it **confirms instead of guessing**. An IMDb id stated in the name (`tt` plus seven to
nine digits, the way uploaders write it: "The Counterfeiters 2007 tt0813547 sqi") is looked up through
`/find/{id}` and used **only when the title route agrees with it**. A contradiction is logged and
neither route is trusted. Reason: measured on 1224 real SubDL names, the id is the one part of a name
that survives every title variant — a translated title cannot be found by text search at all ("Tan Thuy
Hoang" is the Vietnamese title of "Qin Shi Huang"). One measured entry stated an id belonging to
a different film than its own title. The id is also stripped from the title, so a name never becomes one.

**F-M347:** A **series** whose name states season and episode but no year is resolved through the
**episode itself**: for the first few search candidates the plugin asks `tv/{id}/season/{s}/episode/{e}`
and accepts the first candidate that really carries that episode. No candidate with it means no
resolution. Reason: measured on 1224 names, **61 of 70 series names carry no year**, so the strict
title+year test cannot run for them, and the loose route's first hit was right only **4 of 14** times. A
**film** without a year stays unresolved for the same reason — never TMDb's most popular namesake. The
the loose first hit does not occur in the fallback; it still serves the Jellyfin-title rung.

**F-M348:** A parsed title that still carries brackets or percent signs is **not a title**, and the
fallback refuses it (54 of 1224 measured names), while leading bracket labels (`[NETFLIX]`,
`[Crunchyroll]`, `[cFpR]`) are stripped from the title (73 measured). Keeping one made the title search
miss.

**F-M349:** Names that dress their letters in presentation forms (mathematical alphanumerics, fullwidth)
are folded with Unicode **NFKD** before parsing — but **only when such a character is present**, so
ordinary names pass through byte-identical. Reason: the bold digits in
"[𝐜𝐅𝐩𝐑] 𝐃𝐀𝐅𝐓 𝐒𝐓𝐀𝐓𝐄 (𝟐𝟎𝟐𝟒) 𝐏𝐑𝐈𝐌𝐄 - 𝐓𝐛𝐕" are a year to no pattern, and such a name yielded the whole
string as title and no year (measured: **0 of 12** resolved). The check is a surrogate test, not a
regex:.NET's regex engine has no `\U` escape, and the first version threw at type-initialisation time —
caught by the harness, not by the build.

the plugin's own parser (reached by reflection, since the parser is internal), plus source assertions for
the wiring. The series route, the strict title+year route, the confirmation of an id stated in the name,
the refusal of a contradiction, the refusal of an unusable title, the episode call in the resolver and
the gate in front of the fold. The fold checks skip themselves on a runtime without ICU instead of
failing, because normalization is a globalization feature.

## 21. Adding another endpoint — checklist and pitfalls

**F-M564 [U]:** **A further endpoint is adopted through the contract, never through the pipeline.**
The pipeline knows no source by name: it asks the catalogue which endpoints exist, whether the
switch is on and whether the source carries the language. Whoever parks a source "quickly" in an `if` chain of the
pipeline has two truths — exactly the defect from chapter 3.4, only in another place.
The order of the steps below is the order of the dependencies; every step without the previous one
yields a source that silently does not run.

### 21.1 The steps

1. **Fulfil the contract.** `IDownloadEndpoint` (`Provider`, `Wired`, `SearchAsync`, `FetchAsync`) or
   `IUploadEndpoint` (`Provider`, `Wired`, `SendAsync`). `Wired` is the statement "this build can do it",
   not "it is switched on" (F-M484) — an endpoint that is not wired is shown with a reason,
   not kept quiet.
2. **Enum value at the end** of `SubtitleProvider`. Existing values keep their number: the stored
   configuration contains them, and an inserted value silently shifts every mapping.
3. **Into the catalogue** — `SubtitleEndpointCatalog(downloads, uploads)` (built in `DownloadPipeline.…`).
   `EnabledDownloads`/`EnabledUploads` filter `Wired` **and** the switch; what is not in the catalogue
   is never asked, even with the switch on.
4. **Extend `SubtitleEndpointCatalog.HasUploadPath(provider)`** by the new value. This method
   decides whether the source appears in the upload order at all (`UploadSources`), and the
   plugin database asks it too (F-M508). If the value is missing here, the download runs and the upload stays silent.
5. **Place the order** in `Api.Endpoints.SourceOrder` — not in the pipeline. There sit
   the family membership (`InFamily`), the address per media type (`FamilyPage`) and the setting for who
   is asked first (`FamilyFirst`). A new address of the same family is not a new source (F-M509).
6. **The site's language mapping** as its own static `SiteLanguage(code)` in the client, its own table, its own
   ISO3 aliases, no table shared with another site (F-M538). This table is the voice of the
   source: what it does not carry is a **deliberate skip** and counts for its stamp (F-M561). Whoever forgets it
   gets silent skips, and that source never closes.
7. **Own plugin database fields per direction** — `<Quelle>UploadId`, `<Quelle>UploadedAt`, `<Quelle>DownloadId`
   in `embeds` **and** `sidecars`, added via `ALTER TABLE` in `ScribeDbContext` and
   `CurrentSchemaVersion` raised (F-M488/F-M490). Never share a field of another source: otherwise
   one upload settles the other source along with it (F-M500).
8. **Origin** — extend `Provenance.Name`, `Parse`, `MayUploadTo`, `OwnUploadId` by the new value.
   Without that the origin gate stands open, and reup as well as cross-contamination are possible again (F-M487).
9. **Configuration** — one branch each in `IsDownloadEnabled`, `IsUploadEnabled`, `IsConfiguredForUpload`
   and `IsConfiguredForDownload`. The default for a new upload path is **off**; "configured" means
   for a site with access: access data present, for a site without: true.
10. **GUI** — one switch per direction (`Subdl…`, `Tvst…`), one sentence per switch saying which site
    it moves; `TVST only`/`SubDL only` only where a direction really does not exist. Do not rename
    stored keys of existing sources (see 21.3).
11. **Network** — every new client runs through the same HTTP chain (`PacingHandler`, `TmdbCacheHandler`,
    `TransientRetryHandler`). No client with its own `HttpClient` outside the chain: otherwise limit and
    response cache are missing at exactly that spot (measured: 1221 requests on 305 addresses, 916 repeats).
12. **Log** — every line names the answering source via `Provenance.Name`; stamp `[Sub Scribe]`,
    no special texts per source. "Which source answered" is the first question with every finding.
13. **Completeness** — the new source gets its own file stamp and counts only when it
    is in play (switched on, configured, upload path present). Chapter 3.4 applies to it without
    exception; a source without a stamp holds every file open forever.
14. **Prove it** — cases in `scripts/tests/endpoints` for the catalogue, the order, the language mapping and the stamp;
    the requirement of this chapter counts in `spec-doc`. An endpoint without a test is unproven, even if
    it happens to run.

### 21.2 What goes wrong (measured)

- **Silent language exclusion:** without a row in the plugin database the track stays open for the site — 174 cases on
  one day (F-M561).
- **Language mapping shared:** one common table for two sites uploads languages that one of the two
  does not carry ("Bevor wir falsche Sprachen hochladen, laden wir nix hoch").
- **Response cache per call site:** helps at 9 of 13 places and falls flat on the tenth.
  What is right is the handler in the chain (F-M555).
- **Two truths about "done":** the enqueuer and the head of the run must call **the same** function
  (F-M563), otherwise the file returns forever (chapter 3.4).
- **Order in the pipeline:** whoever hard-codes the asking order there bypasses the setting and
  the dry run (F-M496/F-M504).

### 21.3 What stays off limits

- **Assembly name and data folder** (`Jellyfin.Plugin.SubdlSync`) — the plugin database and the configuration hang on that.
- **Stored configuration keys** (`SubdlUploadEnabled`, `SubdlDownloadEnabled`, `SubdlFirst`,
  `SubdlLogin`), **API routes** (`Plugins/SubdlSync|SubdlReset|SubdlQuota`) and **DB column names**
  of existing sources (`SubdlId`, `Origin`, `OriginId` — the nine mark columns per source and direction
  fell away with F-M668, because the source there is a VALUE in `source_matrix`).
  A renaming sweep over them sets switches to default, sends buttons into the void or breaks
  the cycle with `SQLite Error 1: 'no such column: e.ScribeId'` (F-M553).
- **The product name in the log and in the GUI** — that is "Sub Scribe"; the project name is history.

## 22. Subtitle Sources — one contract

**Where the boundary runs between the contracts — which contract answers which question, and what none of them may do — is stated in `docs/CONTRACTS.md`.** This chapter states the rules themselves; that document states the separation, cites the tags below instead of repeating them, and adds the allocation table. Wiring a further source: §21.

This chapter governs how a subtitle source is attached. The rules of the two directions stand in chapter 3 and 4, the stored plugin database in chapter 14.

**How the sources play together as a whole** — what is settled at one source and open at the other, how download and upload deal with several sources and which measurements prove it — stands drawn together in [`MULTI-SOURCE.md`](MULTI-SOURCE.md). This chapter remains the source of the requirements (F-M624…), that sheet is the reading view of the same rules.

**F-M624 [D]:** **Every source is attached through exactly one shared contract** — search, fetch, upload and duplicate check. The pipeline of both directions knows only this contract, not the source behind it.

**F-M625 [D]:** **Every source reports its capabilities itself**: search, fetch, upload, create a film, quota. The pipeline asks for the capability; a comparison against the source name is inadmissible in the pipeline. A capability that is not reported is never called for that source — in particular, a source without the "create a film" capability never produces a creation attempt (chapter 9).

**F-M626 [D]:** **Sources are registered, not branched.** Exactly one registration site holds the active sources; both passes work through that list. Outside the registration and the source configuration, a source name may appear in no condition of the pipeline.

**F-M627 [D]:** **Precedence is a setting**, not a property of the contract: it applies per direction and the same for all files (chapter 10, chapter 13).

**F-M628 [D]:** **Evidence is read and written independently of the source** — download identifier, upload identifier and completion moment per source through one access that maps the source to its evidence field. The pipeline names neither a source-specific column nor a source-specific stamp (chapter 14).

**F-M629 [D]:** **A failing source does not hold up the others.** When a source reports an error, the rest carry on with their part; for the failed source a verdict with a reason is written, and the file stays open for it (chapter 3, chapter 4).

**F-M630 [D]:** **The minimum scope of an attached source** is search, fetch and duplicate check. Upload and the creation of films are optional capabilities; if one of them is missing, the source takes on only the remaining jobs and says so in the run.

**F-M631 [D]:** **One piece of evidence holds for exactly one source.** The key is the pair of track and source; if the entry is missing, the track is open for that source. There is no "held by" field — a track may be evidenced for one source and open for another.

**F-M632 [D]:** **N sources produce N entries per track, not N columns.** Attaching a source means: add a source key and a registration entry without changing the stored plugin database. The entries of a source no longer attached stay readable, are not deleted on their own and produce no work any more.

**F-M633 [D]:** **The queue is a matrix, not a yes/no:** for each file and each attached source it is asked whether the source reports the job and whether its evidence is missing. Work arises only from this pairing, so that one source can catch up while another is finished.

**F-M634 [D]:** **Every piece of evidence is written as a whole per pair of track and source** — one write replaces exactly one piece of evidence and leaves the evidence of other sources untouched.

**F-M635 [D]:** **No verdict of one source deletes that of another**, not even with material of identical content. When a content verdict reads "lies at source A", it sets only A's evidence; B stays open (chapter 11).

**F-M636 [D]:** **When a source fails, its evidence stays open and furnished with a reason**, while the remaining sources carry on. Precedence (F-M627) decides who is asked first, not who receives the evidence.

**F-M637 [D]:** **A change of data format is cross-checked before the switch.** The new file is created completely beside the old one and only becomes the live file when line counts and source evidence agree; on deviation or abort the old file stays the live file, the plugin keeps running and reports the reason in one line. An abort at start is inadmissible (F-M327).

**F-M638 [D]:** **The predecessor file stays as a backup and is never deleted on its own** — neither at the switch nor later. It is the fallback level: going back is a file swap, not a schema change.

**F-M639 [D]:** **A build without a takeover path creates an empty plugin database** and reads the predecessor file neither whole nor in part; it stays untouched and is named in one line. From this point on the fallback level is the file swap, not the build.

**F-M641 [D]:** **A season pack of one source is ONE pack in exactly one language and one variant.** At tvsubtitles.net it goes as one zip into the site's season slot (`episode=0`, their pack page is called `episode-<show>-<staffel>.html`), `release` carries the pack name, `comment` the season line without episode title, and the filename ends in `.zip`. A single `.srt` is refused there instead of landing as an episode without a number. `hi` is set only when the **whole** pack is SDH; HI and non-HI are never mixed, a separate package arises per (language, variant). A pack is admissible only with the configuration's pack switch turned on; there is no second switch for the second source. Neither version nor client name stand in the comment. Checked by T328, T329.

**F-M642 [D]:** **Pack rows are keyed per source.** The key of a pack row names season, language, variant **and source**; every source keeps its own pack identifier in its own row, and no source overwrites the row of another. Cleanup and re-read passes of one source do not touch rows of other sources; rows from before the column count as SubDL and are carried forward to their source key when the file is opened. Checked by T328, T329.

**F-M643 [D]:** **The season pack of the second source is built in the full run — by the same collector as the SubDL pack.** Concept decision: The pack is built only for the full run, refetch or manual, and the hi flag is set only when the pack is exclusively hi. The collector that gathers the members of a season pack works for tvsubtitles.net the same way. The episode SRTs of a complete season go as **one** zip into the season slot. The entries carry the **episode filenames** (the same name that the single upload of the episode sends — with season and episode, because the site reads the number from the name). The ticket carries `SeasonPack=true` and the source `tvsubtitles`. That is steered by the **existing** switch `UploadSeasonPacks`, a second switch does not arise. An arrival run builds no library pack (F-M420).

Since F-M647 both packs stand in ONE slot per pair: the same collection run, the same members, and sending goes to both sources at the same time — the earlier second phase for tvst has thereby been absorbed. `hi` stands on the pack only when the **whole** pack is HI: a separate pack arises per (language, variant), HI and non-HI are never mixed. The plan asks the `packs` row **per source** (F-M642) — a SubDL pack does not make the tvst pack superfluous and vice versa. Every complete season keeps its **one** row (F-M640): a tvst pair counts as a planned pair of the season. Checked by T330.

**F-M644 [D]:** **A refused pack row remembers its reason — and a pack that went through its evidence.** Concept decision: Singles go immediately and the pack follows later. When SubDL answers a pack with its duplicate rejection ("already exist"/"duplicate"), that refusal is written **into the `packs` row**: reason, date (`RefusedAt`) and the fingerprint of the content. The pack phase then skips every row with the **same** fingerprint and reports that in exactly one line with reason and date; a **changed** content (one episode more, another language, another variant) has a different fingerprint and is tried again. A row with a rejection is **no** pack: it counts as "packed" in no pack question (F-M396/F-M415/F-M571), otherwise the refusal would have shut the season down forever. A successful pack sets `UploadedAt` instead (per source, like `SubdlUploadedAt`/`TvSubtitlesUploadedAt` on the subtitle row) — **evidence and rejection exclude each other**: success clears the refusal away, the refusal the evidence. The reset of a single file from the GUI (F-M620) lifts the season's rejection with it, otherwise it would stay locked despite an express reset. Checked by T331.

**F-M647 [D]:** **A season pack has ONE slot per pair — both sources are served in it at the same time.** Concept decision: One slot per pair, both sources served in it at the same time. Until 12.2.0.8 two separate pack phases ran: first a loop over the SubDL packs, then a separate block for tvsubtitles.net with a second plan and a second collection run — the same episodes were unpacked twice and every content verdict reached twice. Now there is **one** loop over the pairs of the plan, **one** collector and **one** send slot: `SeasonPackCoordinator.FillAsync` collects the members (episode tracks per language and variant) and reaches the verdict, `SeasonPackCoordinator.SendAsync` sends the pack to **one** source. The pipeline calls both per pair from the same loop (`UploadPipeline`, section „Erst packbildung dann Einzeluploads"). The members are collected **once**, and from them **both** packs arise: for SubDL the episode files (without season in the name, F-M378), for tvsubtitles.net the same content as a zip with the episode filenames (F-M643).

Sending is **simultaneous**, not one after the other: the sources run via `Task.WhenAll`, the first at once, each further with the same offset of 0-5 % of the transfer pace as the single upload (F-M587). And the pace is paid **once** per pair (slot before the send, anchor after the send, F-M594). The rate counts pairs, not sources. An error or a refusal at **one** source does not end the send to the **other** (F-M629): the failure comes back as a result instead of tearing `Task.WhenAll` into the middle of the second source's send. The semantics of `Refuse(...)` and of the stored verdicts stay unchanged (F-M611: reason, signature, `packs` row). Also unchanged apply the GUI switch `UploadSeasonPacks` (one switch for both sources, F-M641) and the arrival-run rule (F-M424: only the full run packs the library). The tvst branch still hangs on the upload switch **of the source** (F-M495) and therefore is not in the slot at all when the source may not upload. Checked by T332.

**F-M648 [D]:** **Content verdicts are reached ONCE per pair, source gates per source — and a pair that both sources skip reports itself in EXACTLY ONE line.** The verdicts about the **content** hold for both sources in common and are reached only once. "season not complete", "language not in every broadcast episode", "episode N cannot join", "the text comes from a download of one of our sources" (F-M487) and the separation of HI and plain (a pair is always **one** language and **one** variant). They therefore stand in the collector (`FillAsync`) and no longer in the send.

Per source only its **own** gates remain in the send (`SendAsync`): the source's upload switch (F-M495, in the receiver of the second source), "the container already lies at **this** source" (`HasPack` per source, F-M642), the rejection with identical fingerprint (F-M644), the unchanged fingerprint (F-M466). Plus the preconditions of the second source (missing language of the site, no TMDb identifier, no series entry, closed title) and at SubDL the source's own duplicate verdict. When a pair is skipped by **both** sources, **exactly one** line arises at the normal level that names both reasons in one line — not two separate lines, not two phases:

```
season pack — tt… S2 [EN non-HI]: nothing to build — subdl: refused 20.11.2026 (duplicate upload); tvst: upload switch off (F-M648)
```

(The quotation marks around series id and language label are set by the log writer itself; one's own are forbidden by `docs/LOGGING.md` 1.11.)

If the pack goes through at one source and not at the other, the same line names the delivery and the other's reason (`subdl sent; tvst: no TMDb id`). Checked by T332.

**F-M649 [D]:** **A source's hourly quota is no verdict.** When a source reaches its hourly pace limit (SubDL answers `HTTP 200` with `status:false` and "500 subtitles per hour", the second source its 429/5xx), **no** rejection and **no** plugin database verdict is written, and the pack does **not** count as a failure. The pair stays open, the next run takes it on. Before, this case ran into the error branch of `SendAsync` ("SEASON PACK FAILED", `_countFailed`), although nobody had rejected anything — a wall is no refusal (F-M609: `NoVerdict` closes nothing). The difference from the rejection stands at the normal level and names it expressly:

```
season pack — tt… S2 [EN non-HI]: no pack, no verdict — subdl: hourly budget spent — no verdict is written, the pair stays open for the next run (F-M649); tvst sent
```

Checked by T332.

**F-M650 [D]:** **A refused source is closed as long as the season signature is the same — nothing is collected and nothing is unpacked.** Concept decision: A refused source is closed as long as the season signature is the same; a pair for which **no** source is open any more (pack present **or** rejection with unchanged signature **or** source off) must not be collected at all. Until 12.2.0.9 a source's refusal (F-M644) stood in the `packs` row only with the **fingerprint** of the members — and that can only be compared after every episode has been unpacked. For a refused pair the whole season was therefore collected on every run, only to report "fingerprint … unchanged, nothing is re-sent" at the end. Three parts close the gap:
**F-M651 [D]:** **A stored pack verdict gets no second line — exactly one line stands in the log per pair.** Concept decision: A stored pack verdict produces exactly one line per pair, not a second one in another formulation. Until 12.2.0.10 the collector additionally wrote its own line for the stored verdict (`unchanged since the last judgement (<Grund>), nothing extracted`). The pipeline wrote its one line for the same pair (`nothing to build — "no pack (<Grund>), the verdict is stored with the files' signature"`). In the run, **379 of 382** shut-down pairs had both lines, in two different formulations for the same finding. Now the reason travels exclusively via `LastSkipReason` into the one line per pair (F-M648); the collector writes nothing for it. The season line from F-M604 (`nothing examined`, one per season) stays untouched, because it describes a different set. Checked in `endpoints` (F-M611 check: the reason stands in `LastSkipReason`, not as its own log line).
**F-M653 [D]:** **The members line names the reason, not a debt — under F-M429 there is no single identifier yet at pack build time.** Concept decision: The members line names the reason, not a debt, because at pack build time no single identifier exists yet. Checked in the runs: **274 packs, 274 times "carry no SubDL id", 0 times with identifier**; 65 times without, 0 with. The assignment in the code reads only `embeds.SubdlId`, while the upload also writes its identifier into `SubdlUploadId` (both are read now). And above all: under F-M429 the pack goes out **first**, the single uploads follow — so at pack build time no single row exists at all. The line nevertheless claimed "uploaded by us, but not deletable". It now says "{Count} member(s) have no single id yet … the pack goes up first, its singles follow and only then can their ids be recorded". Checked in `packretry` (F-M412 check pins the new formulation).
**F-M654 [D]:** **A pack settles a pair only at ITS source — SubDL packs count for SubDL, tvst packs for tvst, and no source ends the season for the other.** Concept decision: Upload marks at SubDL count at SubDL, uploads at tvst count at tvst, and no source ends the season for the other. That was documented as a principle in four places (F-M489 no mutual blocking, F-M582 duplicate holds only for the same source, F-M584 a rejection locks only its source, F-M570 status per source) — but **not** for pack formation. The plan read `Registry.PacksBySeason()` without a source and thereby the **sum** over all `packs` rows; the per-source version `PacksBySeason(source)` (ContentHashRegistry:1453) and `PacksOfSource` (UploadPipeline:6041) existed, but were not used in the plan.

Measured in the run from 13:05: **36 seasons** ended early with "all N known pair(s) already up as a pack" — **177 pairs** together. And tvsubtitles had **0** packs for these seasons (sole exception the season whose tvst pack had been built minutes before). Example: Mo S2, 31 pairs carried as settled, tvst pack none. Rule now: the pack question is put per source (F-M571), a season is only settled when every source served in this run has its own pack for every known pair (F-M573), and a pair with a pack at one source stays open for the other. Checked in `seasonpack` (source separation in the plan) and `packretry`.
**F-M655 [D]:** **The source of a pack question is named EXPRESSLY — there is no default value.** Five methods of the plugin database carried `string source = PackEntity.SubdlSource` (`RecordPack`, `RecordPackRefusal`, `GetPack`, `HasPack`, `PacksInSeason`). A forgotten argument thereby silently wrote "subdl" — on a write a wrongly assigned row arose, on a question the wrong source was asked, and in both cases nothing turned red. Concept decision: The source of a pack question is named expressly, with no default value, so that a forgotten argument can never silently write subdl. The write path was correct (the tvst pack writes `TvSubtitlesEndpoints` with `source: TvSubtitlesSource`; the coordinator does not even reach its `RecordPack` row for tvst, because the tvst branch returns before that). But the defaults were a trap for every future change. Now `source` is mandatory at all five places; the callers name it, and `RecordPack` has the source before the optional parameters so that it does not become optional again. Checked in `seasonpack` (no default any more, the caller names the source) and `storex`.
**F-M656 [D]:** **The pack key and the upload gate also name their source — and the early gate asks the film source.** After F-M655 three places remained with a default value: `ScribeDbContext.PackKey(..., string source = PackEntity.SubdlSource)` (the key builder), `SeasonPackCoordinator.UploadAsync(..., string source = PackEntity.SubdlSource)` (the buffered pack send) and `UploadGate.Decide(..., bool movieSubtitlesSwitch = false, bool movieSubtitlesAlready = false)`. With a forgotten argument the first two build the SubDL key or send as SubDL; the third **left the film source out silently**. Closing the defaults brought the proof: the **early gate** (`UploadPipeline`, `gateFrueh`, before collecting) did not pass the two film arguments at all. For a film it therefore answered "nothing to do" as soon as SubDL and tvst did not want it, although moviesubtitles did not carry the language (F-M489, F-M509). Now all three places are mandatory, the early gate asks the film source with the same media-type condition as the main gate (`SourceAllowed(MovieSubtitles) && !isSeries`, mark only at `!isSeries`), and the callers name the source expressly. Checked in `endpoints` (no default values, the early gate asks the film source, PackKey and buffer name their source).
**F-M657 [D]:** **Plan and collector share ONE stored marker — a verdict that arises during collecting also holds for the plan.** Since F-M611 the collector writes its verdict ("no pack, because episode N cannot join") under its own namespace `LangTag|fill` with the season signature; the plan asked under `LangTag` with `Signatur|store=N`. Two namespaces for the same question: in the run (12.2.0.16) **522 verdicts** were written, and **not a single one** was read by the plan — it examined the same pairs against the files again in the next run. Now there is `PackSeasonStore.StillImpossibleAny`, which asks BOTH namespaces (plan first, then filling), and `FillTagSuffix` stands exactly once in the plugin database instead of in the collector class. Both sides call the same method: the plan with the season signature and the plugin database count, the collector with the same signature and the count from the plugin database. Checked in `endpoints` (one namespace, both callers, the method in the plugin database).
**F-M658 [D]:** **Track positions count only the subtitle tracks IN the media container — not the loose subtitles beside it.** ffmpeg's `0:s:N` counts exclusively tracks that lie in the media container; Jellyfin's track list additionally carries external subtitles (`IsExternal`, the `.de.01.srt` / `.de.51.srt` beside the film). The single path has always filtered (F-M433), the collector did not: it formed the position over the unfiltered list.

On MobLand S02E02 (two .de SRTs beside the MKV) ffmpeg thereby got `-map 0:s:2` on a file with exactly two subtitle tracks, answered `Stream map '' matches no streams.` and ended with **exit 234**. The track counted as "extraction failed", episode 2 could not join, and the pair was stored as "no pack". In the log stood 13 such lines (Crystal Lake S01E01, MobLand S02E02, The Middle S01E01/S03E01/S05E01, Stranger Things S03E01, The Middle S07E01/S09E01) — every one of these files has loose subtitles. Now both places fetch the track list via `ContainerSubtitleStreams` (without `IsExternal`). Because the files have not changed, the fill key additionally carries `FillSignatureVersion` (`|fill=2`): the verdicts resting on the error are re-examined once, instead of standing forever. Checked in `endpoints` (filter at both places, version in the key at writer and reader).
**F-M659 [D]:** **A failed ffmpeg pass is a circumstance of the run, not a verdict about the content.** The collector read `var (extracted, _) = await FfmpegTools.ExtractAllAsync(…)` — it threw the exit code away, although `ExtractAllAsync` delivers it expressly, because it is the difference between "this track has no text" (0) and "the pass went wrong, follow up individually" (≠0). Every ffmpeg error (stale track list after a file swap, timeout, defective pack) thereby became the content verdict "no pack (extraction failed)" and, via F-M611, a stored marker. Now: at exit ≠ 0 every position that stayed empty is followed up INDIVIDUALLY; if the pass then still yields no text, the collector sets `LastFillInconclusive`, and the coordinator writes **no** marker but only one line (F-M659). Also removed: the dead methods `ExtractAllStreamsAsync`, `GetTextSubtitleStreams` (both with the old counting from F-M658) and `ProcessAndUploadAsync` — without callers they were only traps for the next change. Checked in `endpoints` (exit evaluation, individual follow-up, no marker on unclear outcome, none of the three methods present any more).

1. **The refusal remembers its signature.** `RecordPackRefusal(…, signature:)` additionally writes `RefusedSignature` — the season's signature at the moment of the refusal (F-M611: count plus size and save date of every episode file). The column is **additive**: `ALTER TABLE packs ADD COLUMN RefusedSignature TEXT NULL` when the file is opened (the same list as `RefusalReason`/`RefusedAt`/`UploadedAt`), existing rows read `null`, and the schema marker stays at **17** — a nullable column makes no plugin database unreadable.
2. **The question stands before collecting.** `ContentHashRegistry.PackRefusalStands(imdb, season, lang, hi, source, signature)` is true when the row carries a rejection, `UploadedAt` is empty (evidence and refusal exclude each other, F-M644) **and** the stored signature matches the one passed in. An empty or unknown signature yields **false**: in doubt one collects once too often, rather than shutting down a season whose signature nobody knows.
3. **The gate sits in `FillAsync` between the existing gates.** Order: season complete → `HasPack` per source (F-M647, pack present) → **standing rejection per source with unchanged signature (F-M650)** → stored content verdict (F-M611) → language/quality/episode → collecting. Sources with a standing rejection count as closed and fall out of the list of missing sources. If the list is empty afterwards, **nothing** is collected and **nothing** is unpacked, and **one** line arises at the normal level (F-M648) that names the pair and, per source, the reason:

```
season pack skipped — tt… S2 [EN non-HI]: no source left open — subdl refused 20.11.2026 (duplicate upload), signature unchanged, nothing is extracted (F-M650).
```

If one open source remains beside a closed one, the slot keeps collecting and names the closed source at the detail level. The behaviour in words: **rejected + signature unchanged** = no extraction, one line; **new episode or new file** (different signature) = it is collected and tried again. **Content the same, but the source has the pack by now** = `HasPack` wins (F-M647, no second line arises); **source off** = it is not in the slot at all (F-M495). A pack that went through clears the signature together with the refusal (`RecordPack`), and the GUI reset of a single file (F-M620) lifts it together with the season's rejection — otherwise a reset pack would stay closed forever. Checked by T333.

**F-M662 [D]:** **The tvst client can fetch the season ZIP and separate HI from regular — both stand on the page, not in the name.** Measured on MobLand S02:

- `https://www.tvsubtitles.net/subtitle-3234-2-en.html` is called "Download subtitles for MobLand season 2 (season 2 episode 1-10)" and carries the link `download-3234-2-en.html`. That one answers (with the season page's cookie) with a redirect to `/files/seasons/MobLand - season 2.en.zip` — **610.807 B, 27 entries, 9 episodes**, **9 of them with `.HI.`**.
- Regular and HI therefore lie in **ONE** ZIP per season and language; the difference stands in the filename (`…WEB.NeoNoir.en.srt` against `…WEB.NeoNoir.HI.en.srt`). For tvst, **no two separate season packs** are therefore needed.
- On the episode page HI stands **only** as an image: `<img src="images/hi.png" alt="hearing impaired">` (entry `subtitle-330930`), while the regular entry beside it (`subtitle-330929`) is called identically — without the image it cannot be told apart.

The client offers for that `DownloadSeasonArchiveAsync(showId, season, language)` (season page as cookie precondition, then the ZIP) and `ReadSeasonArchive(byte[])` (episode, language and `HearingImpaired` per `.srt` entry); `ParseEntries` sets `TvSubtitlesEntry.HearingImpaired` from the ear image. Checked in `tvsubtitles` (ear image on/off, ZIP with regular and HI). The placement in the run — order of the sources per GUI, within tvst the season ZIP before the single entry — follows as its own step.

**F-M663 [D]:** **The download side takes the tvst season ZIP as STEP 1 — one fetch per series, season and language instead of one per episode, and regular and HI stay separate.** Measured on MobLand S02 (610.807 B, 27 entries, 9 episodes, 9 of them with `.HI.`):

- Until now the search asked the episode page per episode (`EpisodePagesAsync` → `EpisodeLabelAsync` → `EntriesAsync`) — for 9 episodes of one language, nine times the same question to the same season. Now `TvSubtitlesDownloadEndpoint.SearchAsync` first fetches the season ZIP (`TvSubtitlesClient.SeasonPackAsync`, marker per `(Serie, Staffel, Sprache)` **per run**) and forms one hit per `.srt` entry with the handle `seasonpack:<serie>:<staffel>:<sprache>:<nummer>`. The fetch of this hit reads the entry from the marker (`DownloadPackMemberAsync`) — **no second request**. Measured: five episodes of one season cost three requests (series list, season page, ZIP) and not a single episode page.
- The assignment follows the marker alone: `.HI.` in the name → HI side, otherwise regular side (`DownloadPipeline.SplitByHearingImpaired`, a pure function). Both kinds run through the same gates (language and rip against the site's vocabulary at the seam, QA, language and ranking in the run). A regular file never fills the HI side and vice versa.
- STEP 2 (the previous single path) keeps running for **every side that step 1 did not cover**. If the episode is missing in the ZIP, if only the HI version is missing there, or if the ZIP is unusable, the episode page is asked further. A missing or broken ZIP aborts nothing — it costs one line with a reason (`LastSeasonPackError`) and falls out for this (series, season, language); the failure is noted along, so that the next episode does not ask the same dead address again.
- The order of the SOURCES stays the GUI setting (`Configuration.DownloadOrder` / `quellenReihenfolge`); within tvst the season ZIP stands before the single entry. The HI pool keeps its own order (F-M474: rank with price and width before the text score) — the source order of the regular side is NOT transferred to it.
- A ZIP entry carries no sid. For it there is therefore no "report bad" path: a format finding is counted (`LastRejectedByFormat`) and the entry falls away, nothing is reported — an invented number would be worse than no report.

Checked in `tvsubtitles` (one fetch per season and language instead of per episode via a counting handler, episode missing in the ZIP → the single path applies, HI and regular separated down to the bytes, broken ZIP → no abort). Checked in `endpoints` (split function, handle recognition, seam in the source text).

**F-M664 [D]:** **The order of the sources is the GUI; within tvsubtitles.net there are three steps — season ZIP, single entry, HI; and the HI side of every engine stays its own.** Concept decision: Order per GUI; within tvst first the season ZIP, then the single entry, then HI, and every engine works for itself.

- The ENGINES among themselves are still asked by the setting alone (`Configuration.DownloadOrder` → `DownloadQueryOrder`): the configured source first, the rest in enum order, each exactly once. This rule changes nothing about that.
- WITHIN tvst three steps in this order: **(1)** season ZIP, regular side; **(2)** single entry of the episode page, regular side — only for what step 1 did not cover; **(3)** HI. The HI hits from ZIP and episode page are passed on only **after** both regular steps (`TvSubtitlesDownloadEndpoint.SearchAsync` collects them in `hiTreffer` and appends them only at the end); in the answer every regular line therefore stands before every HI line. The episode page is read only as long as one of the two sides is open — if the ZIP covers both, the episode costs no request.
- EVERY ENGINE WORKS FOR ITSELF: the HI hits lie in a basket **per source** (`hiProQuelle`), not in a common pot. The HI side of a language arises via `DownloadPipeline.HiPoolForLanguage` — engine by engine in asking order, and a basket of a source that was not asked is not in there at all. Until F-M663 all foreign HI lists tipped into the pot of the main search; with that, one engine could fill the other's HI side.
- Untouched: the early exit "regular basket empty ⇒ not available" (F-M504) and the ranking of the HI pool (F-M474: rank with price and width before the text score). Changed is only the composition of the list that this pool reads: it now follows the engine order. The walk sorts stably, so on equal points the source asked first decides — the same statement the GUI makes.

Checked in `tvsubtitles` (if the ZIP covers the regular side, the single entry of the episode page is not asked; ZIP without HI version → HI comes from the episode page, whose regular entry is not taken over). HI stands behind every regular line; the request order via a writing-along handler; episode outside the ZIP continues via the episode page. Checked in `endpoints` (baskets separated as a pure function, HI side in GUI order, seam in the source text of both files).

**F-M665 [D]:** **The archive cache holds for EVERY engine — which source delivered a file stands in the key space, not in the files area.** Concept decision: Season packs from tvst are preferred and cached as well. The same cache for all sources, a separate key per source, the same two fields for lifetime and size, every engine works for itself.

- ONE CACHE, NO SECOND (F-M370): `ArchiveDiskCache`, directory `<Datenordner>/archive-cache`. **Lifetime and size are properties of the files area, not of a source** — they hold for everything that lies in it: `Configuration.DownloadArchiveCacheDays` (default 7), `DownloadArchiveCacheMaxMb` (default 256), oldest out first, a hit resets the clock. There is no lifetime per source and no upper bound per source, but one files area with one clock and one ceiling. The run builds every client with the same instance that it configures and trims; the separation of the sources happens in the KEY, not in the directory. **There are no new settings and no new fields** — the two existing ones suffice for every engine, and `configPage.html` stays untouched; what the cache does stands here.
- WHICH SOURCE STANDS IN THE KEY: `<Quelle>.<…>`, for tvsubtitles `tvst.<Serie>.<Staffel>.<Sprache>` — e.g. `tvst.3234.2.en` (`ArchiveKey.ForTvstSeason`). With that no engine can read or overwrite another's file, and not merely improbably but **impossibly**: the existing key space admits only letters, digits, `_` and `-` (`ArchiveKey.FromUrl`), here stands a dot; an address like `…/subtitle/tvst.3234.2.en.zip` yields no key there at all. The keys of the source already served stay unchanged — nothing that already lies on the disk is renamed.
- DOT AND NO COLON: the cache stores its files under `key + ".zip"`, and a colon is not a permitted filename on Windows — there the season would fall out of the cache silently, without anything turning red anywhere.
- READ BEFORE FETCH, for every engine: the cache is asked BEFORE the network. For tvsubtitles that means: `TvSubtitlesClient.SeasonPackAsync` reads the disk first; a hit goes through `ReadSeasonArchive`, costs **no** HTTP request and sets `LastSeasonPackError` to `null`. Only a miss goes to the network, and only a successful fetch is written before it is returned.
- A FAILURE DOES NOT REACH THE DISK. As before it is noted only in the run's marker (empty list plus reason, F-M663), so that a broken fetch does not block the next run. A ZIP already stored that cannot be read falls out like every broken ZIP: the first step carries nothing, the reason stands in `LastSeasonPackError`, the single entry keeps running — nothing aborts.
- THE STEP ORDER FROM F-M664 STAYS UNCHANGED: within tvsubtitles season ZIP (regular) → single entry (regular) → HI, the engines among themselves per GUI, each for itself. The cache changes only WHERE the bytes of the first step come from.

Checked in `tvsubtitles` (16 checks: the key and its own space, hit in the disk cache ⇒ 0 requests via a counting handler, empty cache ⇒ exactly one fetch and the file lies there afterwards, two runs share the file). Two sources with the same number do not collide, lifetime and size limit bite for every source's entry, broken ZIP in the cache ⇒ the first step falls out without abort, seam in the source text.

**F-M666 [D]:** **The refetch counts the upload postprocessing in — as long as a postprocessing deadline is open or a postprocessing is running, the refetch fire shifts, exactly as with an occupied run lock.** Concept decision: The refetch counts the upload postprocessing in, and any upload sets the postprocessing anchor. The anchor itself stays unchanged (F-M445: every finished upload run sets it to run end plus `UploadPostprocessDelayMinutes`, default 30, and the last job wins) — new is only that the pace counts it in.

- **The same build as the lock, no second mechanism.** The decision is a pure function, `AnchorJobGate.UploadPostprocessHold(nowUtc, Termin, läuft, Upload an, Delay)`, and returns the reason or `null`. When the reason is set, the refetch block does exactly what it does with an occupied lock: `_refetchDeferredUntil = now + JobSpacing`, `_refetchFireWhy` = reason, `TryCountReschedule("refetch", …)` on the same 16× budget (F-M117/F-M368), log line, `return`. The marker is **not** consumed — the slot stays recoverable and fires on the first tick at which nothing is open any more.
- **The check stands BEFORE the lock test.** A running postprocessing is itself the lock holder; behind the lock test the line would have read "run holds the lock" and named the wrong culprit.
- **The reason stands in the operator's words**, readable without code knowledge: `upload postprocessing pending until <Ortszeit>` or `upload postprocessing is running`. In the log (`Refetch fire (<Anker>) deferred — <Grund>; retrying at <Ortszeit> local`) and in `_refetchFireWhy`, which `GetCycleDeferral` emits unchanged as the reason of the line "Next cycle … — <Grund> (attempt n/m)".
- **No deferral when the anchor cannot fire or has already had its turn**. Three cases, each deliberate: upload direction **off** (the postprocessing only runs with it, F-M291; a deadline left lying must not lock the refetch forever), `UploadPostprocessDelayMinutes = 0` (inactive, no anchor arises at all, F-M445 — the refetch runs as today). And a deadline that is **already due** (the postprocessing block had its appearance in this tick; waiting for a consumed deadline would be a loop). A **running** postprocessing holds even at delay 0 — a manually started run is a real run, not an anchor.
- Unchanged: the lock deferral with `AnchorJobGate.DeferralElapsed`, the 16× budget, the marker rule and the F-M445 anchor logic itself.

Checked in `anchorgate` (22 checks: an open anchor holds the refetch, the reason names postprocessing and local time and carries neither requirement number nor code token, a due and an overdue deadline do not hold, delay 0 does not hold). A running postprocessing holds and has precedence over the anchor, a switched-off upload direction does not hold despite a lying deadline. Plus the seam in the source text: the refetch asks the function, reads `IsPostprocessingRunning` and `UploadPostprocessDelayMinutes`. The check sits before the lock test and before the consumption of the marker, the deferral writes the same UTC deadline and the same budget, and the log line is pinned.

**F-M729 [D]:** **A source's account-ledger contract is a second, narrow contract beside the four jobs — not a fifth job.** It has exactly three tasks: list its own open submissions, list the published submissions, withdraw a submission. The contract with the four jobs stays at four; the account-ledger contract stands beside it, and the pipeline keeps using only the four for its work. (1) **Only sources with their own account fulfil it.** A source without an account is missing from the list; it does not answer "cannot do". (2) **A submission carries two handles:** the handle for withdrawing and the handle of its published entry. The two number circles are not the same; an open submission carries only the first. (3) **The state is coarser than any source:** open, accepted, rejected — on a rejection the source's plain text stands beside it.

(4) **The outcome of the withdrawal is that of the send:** accepted, rejected, busy, failed. (5) **The question carries what the bookkeeping needs:** which list, how deep (at most a number of pages), from which moment (only entries younger than the high-water mark of the last catch-up) and from which page. (6) **The access comes from the source's settings file**, not from the general configuration, and is set at first use, not at build time. (7) **The diagnosis at the account uses the same contract page by page** — one page per question, outcome in the three words; its answer format stays unchanged.

**F-M730 [D]:** **A source's inquiry is the third, narrow contract (F-M730), and the access stands in the source's file here too.** Beside the source's name it has exactly one task: how the source stands. (1) **Every source fulfils it**, even one without an account — "am I reachable" it knows best itself; it is therefore not part of the account-ledger contract, because "who still owes me something" and "are you reachable" are two questions. (2) **The source sets its lights itself:** key, label, light (green, yellow, red), "access deposited" and plain text. The caller names no source by name and only paints what comes back; for a missing light there is a red substitute.

(3) **The answer is neutral:** no access datum, no address, none of our identifiers, no sentence about the plugin database. (4) **The access is set at ONE place from the source's settings file** — the same place for the four jobs, the account-ledger contract and the inquiry. The general configuration no longer carries it; **outside the sources no file reads an access datum from it any more**, the quota included. (5) **The status page asks the sources** via a passed-in network connection with a 15 second cap and only paints.

**F-M731 [D]:** **A source's short name stands in its capabilities, not as a condition in the pipeline.** Beside the full key and the display name every source carries a field for the short name; empty means "take the full key". The series address carries "tvst", the others carry their key — that leaves all existing rows unchanged. The pipeline no longer decides by a source name when it labels a line.

**F-M732 [D]:** **Account bookkeeping in the pipeline goes through the second contract — outside the catch-up too.** The submission identifiers caught up at run end and the identifier of the own season pack read the lists through the account-ledger contract and work with neutral fields: both handles, name, year, season, episode, moment in UTC and language in a uniform spelling. Because the boundary carries a uniform language spelling and the group key carries the source's code, both sides fold to the same spelling and are then compared. No call of the submission lists remains in the pipeline.

**F-M733 [D]:** **The LIVE quota is part of the inquiry, not of the account-ledger contract and not of the send.** On a throttling answer the run must tell whether a short-term throttling or the daily limit is present (F-M238), and may not use a cached number for that. The contract therefore carries a task "how much is still free today": outcome free, exhausted or unreadable, plus limit, remainder and moment of reset. (1) **Only the source whose capabilities promise a quota is asked** — if none names one, the outcome is "unreadable". (2) **A source without an account answers "I keep no quota"**, instead of inventing numbers. (3) **The outcome is called differently from that of the site**, so that the boundary does not carry two vocabularies for the same information; the mapping happens once at the caller.

**F-M734 [D]:** **Title creation is part of the send job — not of a lookup that knows our plugin database.** Before either of the two sites accepts a subtitle, it needs its own identifier for film or series. (1) **The send job may require that the source searches for its title and, if it is missing, creates it**; it carries the title, the year and the identifiers of the record for that, and it carries the dry run. (2) **The answer reports neutrally:** ready, created, created without a readable identifier, carries the title already without identifier, not findable, not askable, without verdict — together with the identifier when the site names one. "Ready" means the send may go on; "without identifier" means the pair is decided without claiming an upload. (3) **The pipeline holds the plugin database:** it passes the stored identifier in the job and writes the new one itself; the source never reads or writes it. (4) **A dry run creates nothing and decides nothing** — the answer reads "not askable", so the pipeline closes no pair.

(5) **A source that finds titles via its identifiers says "not askable"** and does not hold up the send. (6) **On the path that procures the title itself, the source asks before sending whether the identifier can be played** (F-M606). (7) **It is nevertheless built only once:** the pack arises from the episodes and does not carry the title number; a finally failed title creation stops the next run, via the lock (F-M608), BEFORE the build, and a dry run builds nothing. (8) **The retry ladder stays with the pipeline** — pauses, fourth failure, permanent lock (F-M608) — and retries only as long as nothing was sent; the source aborts before sending if the title does not stand.

**F-M735 [D]:** **Which languages a source accepts, the SOURCE says — in both directions, and "I keep no list" is an answer of its own.** The language knowledge lay as a static language mapping in the two fetch clients, that is, where nothing changes when the SITE changes. (1) **The source produces its language information itself:** what an ISO identifier is called on its site, what an abbreviation of its site means in the plugin database (F-M557), and whether it carries supported languages. (2) **Three states, not two:** supported languages — the series and the film site say no to the unknown —, **open without a list** (SubDL takes what comes) and unknown (a key this build does not know). An **empty** list is none of these states; it would be the claim "can do nothing" and would close sends that were open before.

(3) **It is never a client that is asked by name, but the source** — in the chain, in the enqueuer and in the settings view; the mappings themselves live with the source, not in the fetch. (4) **The spelling of the plugin database arises at the boundary:** the return path delivers the ISO identifier; if none is evidenced, the answer reads empty, and nothing is guessed. (5) **The one neutral conversion stays above the sources** (F-M557), because the plugin database and the pack need it too.

**F-M736 [D]:** **Names are normalised at input at ONE place, namely as a function above the packages.** Filename, archive entry, entry of a source list and row name in the plugin database come from four directions. Before, every place prepared them a little differently (trimmed once, not another time, double spaces now yes, now no), and a comparison of two names was thereby a question of the call path. (1) **The normalisation is a public function** — fold (F-M724), trim at the ends, collapse inner whitespace to one space — and the routines call it instead of folding or trimming themselves. (2) **The comparison normalises BOTH sides** via the same function; there is no second way to compare two names. (3) **The normalised version is never stored** — it is a reading aid, not a correction of anybody's filenames; if nothing is to be done, the same string comes back. (4) **Unicode normalisation in the sense of the runtime environment stays forbidden**, because without ICU it silently does nothing and thereby produces two answers for the same name, depending on the host.
