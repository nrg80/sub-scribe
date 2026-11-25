# tvsubtitles.net — protocol and methods

As of 16.11.2026. Applies to `Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs` (F-M480–F-M482, F-M516).

The site has **no API**. Checked on 15.11.2026: `/api*`, `/xmlrpc.php` and `api.tvsubtitles.net`
answer with 404 or do not resolve; `/rss.xml` exists (RSS 2.0), but it carries no
episode data. Everything therefore runs through the pages that the site's own navigation offers —
HTML is read, a form is written.

## The path (download)

```
/search2.php?qs=<name>          Search: /tvshow-<id>.html including the period (F-M516 — soft!)
/add.html                       All series selection list  → Show id
/tvshows.html                   Table of all series: tvshow-<id>-<staffeln>.html (not always up to date)
/tvshow-<show>.html             Seasons of the series      → season numbers
/tvshow-<show>-<staffel>.html   Episode pages per season   → page ids  (NO episode numbers)
/episode-<seite>.html           "2x24" in the heading      → season/episode
                                + the entries (per entry language, rip, release, date)
/subtitle-<id>.html             Detail page of the entry     (must be fetched first)
/download-<id>.html             Zip — or a page that carries the file name in the JavaScript
```

**What hurts about this path, and how it is solved:**

- **The language is not in the text.** Only the flag image is reliable
  `images/flags/<code>.gif`. It must be read **per block** (from one subtitle anchor to the
  next one), otherwise an entry inherits the flag of the following one and is sorted wrongly.
  Entries without a flag are skipped: an unknown language cannot be assigned to a
  target language.
- **Page ids are not episode numbers**, and the season list runs descending. The number
  comes from the heading of the episode page (`<h2>The Middle 2x24</h2>`), with fallback to the first
  `NxM` of the page — the markup has changed once before.
- **The file name is not in the link.** The download page builds it in JavaScript from numbered
  fragments (`var s1='files/The Middle_2x24'; var s2='_HDTV.FEVER.en.zip';`), which are joined
  in order.
- **The download address answers in two forms** (measured 15.11.2026): sometimes with the
  „one moment" page, sometimes with the **zip directly**. The client decides on the bytes that
  actually arrived (`PK\x03\x04` = zip, otherwise parse HTML) — not on a Content-Type that the
  site does not always set.
- **The bytes are passed on untouched.** The site delivers, per language, UTF-8 next to cp1250,
  cp1251, cp1253, cp1254 and cp1256. Exactly here, on 15.11.2026, the codepage error arose that
  made Russian, Greek and Arabic unreadable: `cp1252` practically never fails and swallows
  everything. Decoding therefore happens in the pipeline, not in the transport.

## The path (upload)

`POST /add1.php`, `multipart/form-data`, referer `/add.html`. The fields are **verbatim** those of the
form on `/add.html`:

| Field | Content |
|---|---|
| `tvshow` | show id from the selection list; `0` = no series — refused |
| `season` | 1–22 |
| `episode` | 0–100 |
| `double` | `1` for a double episode (only set when applicable) |
| `lang` | language code **of the site**, not ISO (see below) |
| `rip` | `WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay` or empty |
| `release` | free text (release group, e.g. `FEVER`) |
| `hi` | `1` for a hearing-impaired version (only set when applicable) |
| `upfile` | file, `.srt` or `.zip` |
| `comment` | **carries the identifiers and the client here**: `TMDB 1422 / IMDb tt1442464 / Sub Scribe 12.1.12.380` |
| `go` | hidden, `1` |
| `author` | **is not sent along** — a missing part is the only way not to overwrite a prefill of the site |

Example from the site itself: `Supernatural.S04E09.HDTV.XviD-NoTV.avi` → series Supernatural,
season 4, episode 9, rip HDTV, release NoTV.

**Language codes are the site's:** `en es fr de br ru ua it gr ar hu pl tr nl pt sv da fi ko cn jp
bg cz ro` — four of them are **not ISO** (`gr`, `cz`, `cn`, `jp`). A code that the selection list
does not know makes the form fall back to its first entry — the upload then lands as
English. The client therefore checks against the table and sends no unknown code.

**ISO is mapped, and only here (F-M502):** `TvSubtitlesClient.SiteLanguage` pulls an
incoming code onto the site's code — `el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`,
`zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp` (the four deviations in both spellings), plus the
ISO 639-2 forms of the remaining site languages (`deu`/`ger` → `de`, `fra`/`fre` → `fr`, `spa` → `es`,
`pob` → `br`, `ukr` → `ua`, `nld`/`dut` → `nl`, `ron`/`rum` → `ro` …). Case does not
count, region and script are dropped (`zh-Hans` → `cn`, `ja-JP` → `jp`) — **except** `pt-BR`/`pt_BR`:
that is Brazilian Portuguese (`br`), while `pt` and `pt-PT` stay at `pt`. Unknown
means `null` (no guessing) and is rejected with a line. The same bridge is used by the check in
`BuildUpload`, the field `lang` and the read-back `FindUploadedIdAsync`; the file name keeps its
ISO3 suffix from the table (`gre`, `cze`, `chi`, `jpn`, `pob`, `rum`). A common table with
SubDL does **not** exist — SubDL maps in `ScribeApiClient`.

**Creating a new series (F-M516, evidenced on 16.11.2026):** `GET /add-tvshow.php` delivers the form —
and it sits, like the upload form, in a broken nesting (`<table><form …>`), the
field names still apply:

```html
<form action="/add-tvshow1.php" method="post" enctype="multipart/form-data">
  <input name="name"    type="text"  style="width:230px">
  <input name="year_s"  type="text"  maxlength=4 size=4>
  <input name="seasons" type="text"  maxlength=2 size=2>
  <input type="submit"  value="Send">          <!-- without name: is NOT sent -->
  <input name="go"      type="hidden" value="1"> <!-- behind the submit line, still inside the form -->
</form>
```

| Field | Kind | Content |
|---|---|---|
| `name` | text | series name as it should appear in the list |
| `year_s` | text, `maxlength=4` | year of first broadcast |
| `seasons` | text, `maxlength=2` | season count — two digits, so 1..22 (the same boundary as `MaxSeason`) |
| `go` | hidden | `1` |
| (submit button) | submit | **no `name`** → no field in the body |

No captcha, no token, no account, no comment field, no external identifier — the name is the
only key, hence the duplicate-entry protection via the strict name comparison. `enctype` is
**`multipart/form-data`**; a URL-encoded body would be the same error here that the movie site
(`add-movie.php`) has just shown, and is therefore not even built.

**What the site answers — measured on a real creation** (`Weissensee`, 2010, 4 seasons,
16.11.2026):

```
POST /add-tvshow1.php            → HTTP 302, body empty
   Location: https://www.tvsubtitles.net/add-tvshow.php?done=yes
GET  /add-tvshow.php?done=yes    → HTTP 200, in the article:
   <div class="subtitle2"><b>TV-Show has been added. Thank you.</b></div>
```

- **Success** is the 302 to `?done=yes` (or the sentence, when the caller follows the redirect).
  `TvSubtitlesClient.ClassifyAddShow` recognises **only** this marker — target URL or text.
- **The answer carries no id**, in no form.
- **„It already exists" and a rejection are NOT evidenced.** A second POST would have either
  created a duplicate entry (a creation is not deletable on the site) or proven nothing. Therefore
  the client claims only success; everything else is `unclear` (no marker) or `refused`
  (error status), and the decision falls on the plugin database, not on the wording.
- **The counter „TV Shows" in the sidebar does not move** (before and after the creation 2768) —
  reading a creation off the counter is no evidence.

**How to get to the series number afterwards:**

| Path | Call | What it says |
|---|---|---|
| Search | `GET /search2.php?qs=<name>` | `<a href="/tvshow-4242.html">Weissensee (2010-2026)</a>` — id **and** name |
| Selection list | `GET /add.html`, `<select name="tvshow">` | `4242` → `Weissensee` (the client: `ShowsAsync`) |
| All series | `GET /tvshows.html` | rank · `tvshow-<id>-<staffeln>.html` · name · seasons · episodes · subtitles · years |

The search is the shortest path and shows the fresh series immediately — but it is **soft**
(`Pagan Peak` → *Twin Peaks*, *Peaky Blinders*), so its result runs through the same strict
comparison as the list, and „no hit" is no proof. The selection field of `/add.html` contained
the new series immediately afterwards as well (4242) — it is the fallback. `/tvshows.html` carried it
**not yet** (2.772 lines before and after the creation): this catalogue is not rewritten with every creation
and cannot serve as evidence. The season count was taken over: `/tvshow-4242.html`
answers 302 to `tvshow-4242-4.html`, the page is called „… season 4" and links seasons 1–4.

**It is fixed who creates (F-M501):** the page of the title inquiry (`TvstShowLookup`) — and **only** with the TMDb title, the
TMDb first-broadcast year and the TMDb season count (limited to 1..22). The number of the series stands
afterwards in `MediaEntity.TvstShowId` on the same level as `TmdbId`/`ImdbId` and is
read instead of searched the next time. Creation happens **at most once per medium and run**, and only if the site
answered cleanly before; never in a dry run. After the creation (F-M516) applies: search the number
(search, then list) and **store only on a hit**; if the site confirms the creation but the
series is not in the list, the result is honestly `created-id-unknown` and **nothing** is
stored; if it does not confirm and the series is there anyway, it was already present
(`existed`) — the number is stored, a second entry does not arise.

## Boundaries and open points

- **No quota, no documented throttling.** The site names no limit; the client keeps to
  the general pause of the plugin (`MinCallPauseSec`) when it is bound in.
- **No login visible.** `/login.html`, `/upload.html`, `/panel.html` → 404. **Creating series works
  without an account** (evidenced 16.11.2026: the POST was accepted, the series was there immediately); for the
  upload it remains unverified: an empty POST answers with HTTP 200 and an empty body, a
  captcha was not visible.
- **The client is bound in by now** (F-M485): the two switches really control fetch and upload,
  and the origin of every find is recorded with the id of the source in the plugin database (F-M486), so that a
  tvst find is never uploaded to SubDL (F-M487). Boundaries remain: **season packs and the HI pool
  are SubDL's business**, this source can only do single episodes.

## Methods

All parsers are `static` and take HTML as a string — the suite `scripts/tests/tvsubtitles`
drives them with the pages that the site really delivered on 15.11.2026 (`fixtures/`), not
with the live site.

| Method | Purpose |
|---|---|
| `ShowsAsync()` | `id → Name` from the selection box of `/add.html` (~2.770 series) |
| `SearchShowsAsync(query)` / `ParseShowSearch(html)` | site search: `id → Name` (year period cut off) — **soft**, compare the result strictly (F-M516) |
| `ResolveShow(shows, name)` | id for a name, **compared strictly** (F-M501): upper/lower case, `&` = `and` = `und`, whitespace — otherwise equality. No substring, no similarity measure |
| `NormalizeShowName(name)` | the comparison key itself (F-M501) — the three equalisations, verifiable |
| `SeasonsAsync(showId)` | season numbers that have a page |
| `EpisodePagesAsync(showId, season)` | page ids of a season, without duplicates |
| `EpisodeLabelAsync(pageId)` | `(season, episode)` from the heading of the episode page |
| `EntriesAsync(pageId)` | entries: `SubtitleId`, `Language`, `Rip`, `Release`, `Age` |
| `DownloadAsync(subtitleId)` | `(file name, bytes)` — first `.srt` from the zip |
| `Comment(tmdbId, imdbId, extra)` | comment line `TMDB … / IMDb … / Sub Scribe <Version>` |
| `BuildUpload(...)` | check and build the field list; sends nothing |
| `BuildUploadBody(upload)` | multipart body and boundary, byte-exact |
| `UploadAsync(upload, dryRun)` | send; **without `dryRun: false` nothing leaves the process** |
| `BuildAddShow(name, year, seasons)` | the four form fields (`name`, `year_s`, `seasons`, `go`), `seasons` 1–22 (F-M501/F-M516) |
| `AddShowAsync(addShow, dryRun)` | send (multipart, referer `/add-tvshow.php`); dry run is the default; returns status, `Location`, target URL, text and the **classified** outcome (F-M516) |
| `ClassifyAddShow(status, location, url, text)` | `created` only on `done=yes`; `refused` on error status; otherwise `unclear` — more is not evidenced (F-M516) |
| `AddShowConfirmed(html)` | does „TV-Show has been added." stand in the page? (F-M516) |
| `IsZip(bytes)` | zip or HTML — the site answers in both forms |

Live probe (read only, not part of the checks):

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 <dotnet>/dotnet run \
  --project scripts/tests/tvsubtitles/tvsubtitles.csproj -c Release -- <repo> --live
```

It shows: the series list (on 16.11.2026: 2.774 entries in the selection field, including the
placeholder `0`), The Middle = 512, the result of the site search for „The Middle", seasons 1–9,
24 episode pages in season 2, page 31911 = 2x24 with 3 entries and the download of an English
entry. It **creates nothing** — the creation is expressly not part of the probe.
