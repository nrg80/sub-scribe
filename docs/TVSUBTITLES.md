# tvsubtitles.net — the series source

*Paper: the site's own paths, tables and peculiarities, derived from the rules in `SPECIFICATION.md`
and `CONTRACTS.md`, every statement carrying its `F-M` tag. The words are those of `TERMS.md`.*

**Owner: the site.** Paths, tables, peculiarities and methods of tvsubtitles.net; the contract view of
the same source stands in `engines/tvsubtitles.md`.

## Contents

- [1. What it is](#1-what-it-is)
- [2. Which media reach it](#2-which-media-reach-it)
- [3. The path (download)](#3-the-path-download)
- [4. The path (upload) and creating a series](#4-the-path-upload-and-creating-a-series)
- [5. Tables — language mapping and supported languages](#5-tables-language-mapping-and-supported-languages)
- [6. Site peculiarities](#6-site-peculiarities)
- [7. What is not proven](#7-what-is-not-proven)
- [8. The boundary](#8-the-boundary)
- [9. The checks](#9-the-checks)

## 1. What it is

A site for **series subtitles**, read and written through its own pages (F-M480–F-M482, F-M516).
It has **no API**. `/api*`, `/xmlrpc.php` and `api.tvsubtitles.net` answer with 404 or do not resolve.
`/rss.xml` exists as RSS 2.0, but it carries no episode data. Everything therefore runs through the
pages the site's own navigation offers: the client reads HTML and writes a form.

## 2. Which media reach it

Series only: a film has no page here. The source answers the episode slot, one episode at a time:
**season packs and the hearing-impaired pool are SubDL's business** (chapter 8). The library metadata
decides whether a medium is a series at all, not this site.

## 3. The path (download)

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

**What hurts about this path, and how the client solves it:**

- **The language is not in the text.** Only the flag image is reliable: `images/flags/<code>.gif`. The
  client must read it **per block**, from one subtitle anchor to the next. Otherwise an entry inherits
  the flag of the following one and is sorted wrongly. The client skips entries without a flag, because
  an unknown language cannot be assigned to a target language.
- **Page ids are not episode numbers**, and the season list runs descending. The client takes the
  number from the heading of the episode page (`<h2>The Middle 2x24</h2>`), and falls back to the first
  `NxM` of the page, because the markup has changed once before.
- **The file name is not in the link.** The download page builds it in JavaScript from numbered
  fragments (`var s1='files/The Middle_2x24'; var s2='_HDTV.FEVER.en.zip';`), and the client joins them
  in order.
- **The download address answers in two forms.** Sometimes the site sends the „one moment" page,
  sometimes the **zip directly**. The client decides on the bytes that actually arrived
  (`PK\x03\x04` = zip, otherwise parse HTML), not on a Content-Type the site does not always set.
- **The client passes the bytes on untouched.** Per language the site delivers UTF-8 next to cp1250,
  cp1251, cp1253, cp1254 and cp1256. A codepage error made Russian, Greek and Arabic unreadable once,
  because `cp1252` practically never fails and swallows everything. Decoding therefore happens in the
  pipeline, not in the transport.

## 4. The path (upload) and creating a series

`POST /add1.php`, `multipart/form-data`, referer `/add.html`. The client sends the fields **verbatim**,
exactly those of the form on `/add.html`:

| Field | Content |
|---|---|
| `tvshow` | show id from the selection list; `0` = no series — refused |
| `season` | 1–22 |
| `episode` | 0–100 |
| `double` | `1` for a double episode (only set when applicable) |
| `lang` | language code **of the site**, not ISO (chapter 5) |
| `rip` | `WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay` or empty |
| `release` | free text (release group, e.g. `FEVER`) |
| `hi` | `1` for a hearing-impaired version (only set when applicable) |
| `upfile` | file, `.srt` or `.zip` |
| `comment` | **carries the identifiers and the client here**: `TMDB 1422 / IMDb tt1442464 / Sub Scribe 12.1.12.380` |
| `go` | hidden, `1` |
| `author` | **is not sent along** — a missing part is the only way not to overwrite a prefill of the site |

Example from the site itself: `Supernatural.S04E09.HDTV.XviD-NoTV.avi` → series Supernatural, season 4,
episode 9, rip HDTV, release NoTV.

### Creating a series (F-M516)

`GET /add-tvshow.php` delivers the form. Like the upload form it sits in a broken nesting
(`<table><form …>`), but the field names still apply:

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

There is no captcha, no token, no account, no comment field and no external identifier. The name is the
only key, hence the duplicate-entry protection by a strict name comparison. `enctype` is
**`multipart/form-data`**; a URL-encoded body would be the same error the film site has just shown, and
the client does not even build it.

**What the site answers.** The site accepted a creation of `Weissensee` (2010, 4 seasons) like this:

```
POST /add-tvshow1.php            → HTTP 302, body empty
   Location: https://www.tvsubtitles.net/add-tvshow.php?done=yes
GET  /add-tvshow.php?done=yes    → HTTP 200, in the article:
   <div class="subtitle2"><b>TV-Show has been added. Thank you.</b></div>
```

- **Success** is the 302 to `?done=yes`, or the sentence when the caller follows the redirect. The
  client recognises **only** this marker, target URL or text.
- **The answer carries no id**, in no form.
- Neither „it already exists" nor a plain rejection is evidenced (chapter 7). A second POST would
  either have created a duplicate entry, since a creation is not deletable on the site, or proven
  nothing. The client therefore claims only success; everything else is `unclear` (no marker) or
  `refused` (error status), and the plugin database holds the decision, not the wording.
- **The counter „TV Shows" in the sidebar does not move.** Reading a creation off the counter is no
  evidence.

**How the client finds the series number afterwards:**

| Path | Call | What it says |
|---|---|---|
| Search | `GET /search2.php?qs=<name>` | `<a href="/tvshow-4242.html">Weissensee (2010-2026)</a>` — id **and** name |
| Selection list | `GET /add.html`, `<select name="tvshow">` | `4242` → `Weissensee` |
| All series | `GET /tvshows.html` | rank · `tvshow-<id>-<staffeln>.html` · name · seasons · episodes · subtitles · years |

The search is the shortest path and shows a fresh series immediately — but it is **soft**: `Pagan
Peak` finds *Twin Peaks* and *Peaky Blinders*. Its result therefore runs through the same strict
comparison as the list, and „no hit" is no proof. The selection field of `/add.html` carried the new
series immediately, and it is the fallback. `/tvshows.html` did **not** carry it yet: that catalogue is
not rewritten with every creation and cannot serve as evidence. The client takes the season count over:
the show page answers 302 to the season page, and that page links seasons 1 to 4.

**The rules fix who creates (F-M501):** the page of the title inquiry, and **only** with the TMDb
title, the TMDb first-broadcast year and the TMDb season count, limited to 1..22. The number of the
series stands afterwards on the same level as the other title identifiers, and the client reads it
instead of searching it the next time. Creation happens **at most once per medium and run**, and only
if the site answered cleanly before, never in a dry run. After the creation (F-M516) the client
searches the number (search, then list) and **stores it only on a hit**. If the site confirms the
creation but the series is not in the list, the result is honestly `created-id-unknown` and the client
stores **nothing**. If the site does not confirm and the series is there anyway, it was already present
(`existed`); the client stores the number and a second entry does not arise.

## 5. Tables — language mapping and supported languages

**The site's codes are:** `en es fr de br ru ua it gr ar hu pl tr nl pt sv da fi ko cn jp bg cz ro`.
Four of them are **not ISO** (`gr`, `cz`, `cn`, `jp`). A code the selection list does not know makes the
form fall back to its first entry, and the upload then lands as English. The client therefore checks
against the table and sends no unknown code.

**ISO is mapped, and only here (F-M502):** the client pulls an incoming code onto the site's code, so
`el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`, `zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp` (the four
deviations in both spellings). To that come the ISO 639-2 forms of the remaining site languages
(`deu`/`ger` → `de`, `fra`/`fre` → `fr`, `spa` → `es`, `pob` → `br`, `ukr` → `ua`, `nld`/`dut` → `nl`,
`ron`/`rum` → `ro` …).

Case does not count, and the client drops region and script (`zh-Hans` → `cn`, `ja-JP` → `jp`) —
**except** `pt-BR`/`pt_BR`: that is Brazilian Portuguese (`br`), while `pt` and `pt-PT` stay at `pt`.
Unknown means `null` (no guessing), and the client rejects it with a line. The check uses the same
bridge before it builds the body, and so do the field `lang` and the read-back. The file name keeps its
ISO3 suffix from the table (`gre`, `cze`, `chi`, `jpn`, `pob`, `rum`). **A common table with SubDL does
not exist**; SubDL maps in its own client.

## 6. Site peculiarities

- **No quota, no documented throttling.** The site names no limit; the client keeps to the general pause
  of the plugin when it is bound in.
- **No login visible.** `/login.html`, `/upload.html` and `/panel.html` answer with 404. Creating a
  series works without an account; for the upload it remains unverified (chapter 7).
- **The download address is a page or a file** (chapter 3); the client decides on the bytes.
- **The catalogue pages lag.** `/tvshows.html` is not rewritten with every creation, and the sidebar
  counter does not move. Neither is evidence of anything.
- **The search is soft.** It returns what it thinks is close, so its result must pass the strict
  comparison.
- **One episode at a time.** Season packs and the hearing-impaired pool are not offered here.

## 7. What is not proven

- **An upload without an account** is not proven: an empty POST answers with HTTP 200 and an empty
  body, and no captcha was visible. Nothing beyond that was tried, so nothing beyond that is claimed.
- **The marker for „it already exists"** is not proven. A creation is not deletable on the site, so the
  experiment was not run.
- **A plain rejection** of a creation is not proven either.
- **A quota** is not proven in either direction, because the site names none and a limit was never
  reached.

## 8. The boundary

- **The client is bound in** (F-M485): the two switches really control fetch and upload.
- **The client records the origin of every find** with the identifier of the source in the plugin
  database (F-M486), so that a find from this source is never uploaded to SubDL (F-M487).
- **What this source cannot do stays SubDL's business:** season packs and the hearing-impaired pool.
  Here there are single episodes only.

## 9. The checks

All parsers are `static` and take HTML as a string. The suite `scripts/tests/tvsubtitles` drives them
with the pages the site really delivered, from `fixtures/`, not with the live site. **139 checks** run
there (`tvsubtitles`).

The probe below only reads, and it is not part of the checks:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 <dotnet>/dotnet run \
  --project scripts/tests/tvsubtitles/tvsubtitles.csproj -c Release -- <repo> --live
```

It shows the series list, with 2.774 entries in the selection field, including the placeholder `0`, and
The Middle = 512. Then the result of the site search for „The Middle", seasons 1–9, 24 episode pages in
season 2, page 31911 = 2x24 with 3 entries, and the download of an English entry. It **creates
nothing**; the creation is expressly not part of the probe.
