# tvsubtitles.net — the series source

tvsubtitles.net carries subtitles for series only, and it is opened page by page the way a browser opens it
(F-M480). It keeps no account we can read, its language enumeration is small and closed, and it needs its own
title record before it accepts anything, which is why it is one of the two sources that can be asked to create
one.

## What it is

The site has no API: `/api*`, `/xmlrpc.php` and `api.tvsubtitles.net` answer with 404 or do not resolve, and
`/rss.xml` exists as RSS 2.0 but carries no episode data (F-M480–F-M482, F-M516). The client therefore reads
HTML and writes a form.

## Which media reach it

Series only — a film has no page here — and the source answers one episode at a time, while season packs and
the hearing-impaired pool are SubDL's business. TMDb decides whether a medium is a series at all, not this
site, and a film is never handed to the series address (F-M507).

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

Five defects follow from this path, and each is answered by the client:

- **The language is not in the text.** Only `images/flags/<code>.gif` is reliable. The client reads it per
  block, from one subtitle anchor to the next, so that no entry inherits the flag of the following one. It
  skips an entry without a flag, because that language cannot be assigned.
- **Page ids are not episode numbers.** The season list runs descending, so the number comes from the heading
  of the episode page (`<h2>Sample Series 2x24</h2>`), with the first `NxM` of the page as a fallback for changed
  markup.
- **The file name is not in the link.** The download page builds it in JavaScript from numbered fragments
  (`var s1='files/Sample Series_2x24'; var s2='_HDTV.GRP.en.zip';`), and the client joins them in order.
- **The address answers either the „one moment" page or the ZIP.** The client decides on the bytes
  (`PK\x03\x04`), not on a Content-Type the site does not always set.
- **The bytes arrive in several encodings.** They pass on untouched, UTF-8 next to cp1250, cp1251, cp1253,
  cp1254 and cp1256. Decoding happens in the pipeline, because `cp1252` practically never fails and once made
  Russian, Greek and Arabic unreadable.

## The path (upload)

`POST /add1.php` takes `multipart/form-data` with the referer `/add.html`, and the client sends the fields
verbatim, exactly those of the form on `/add.html`:

| Field | Content |
|---|---|
| `tvshow` | show id from the selection list; `0` = no series — refused |
| `season` | 1–22 |
| `episode` | 0–100 |
| `double` | `1` for a double episode (only set when applicable) |
| `lang` | language code **of the site**, not ISO (see *Languages*) |
| `rip` | `WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay` or empty |
| `release` | free text (release group, e.g. `FEVER`) |
| `hi` | `1` for a hearing-impaired version (only set when applicable) |
| `upfile` | file, `.srt` or `.zip` |
| `comment` | **carries the identifiers and the client here**: `TMDB 1422 / IMDb tt1442464 / Sub Scribe 12.1.12.380` |
| `go` | hidden, `1` |
| `author` | **is not sent along** — a missing part is the only way not to overwrite a prefill of the site |

A language outside the site's list is refused before the body is built, so the form's fallback to its first
entry can never publish a file as English.

## Creating a series

`GET /add-tvshow.php` delivers the form (F-M516). Like the upload form it sits in a broken nesting
(`<table><form …>`), and the field names still apply:

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

There is no captcha, no token, no account, no comment field and no external identifier; the name is the only
key, hence the duplicate protection by a strict name comparison. `enctype` is **`multipart/form-data`**, and
the client does not even build a URL-encoded body.

### What the site answers

The site accepted a creation of `Weissensee` (2010, 4 seasons) like this:

```
POST /add-tvshow1.php            → HTTP 302, body empty
   Location: https://www.tvsubtitles.net/add-tvshow.php?done=yes
GET  /add-tvshow.php?done=yes    → HTTP 200, in the article:
   <div class="subtitle2"><b>TV-Show has been added. Thank you.</b></div>
```

Success is the 302 to `?done=yes` or the sentence when the caller follows the redirect, and the client
recognises **only** this marker, target URL or text. The answer carries no id, in no form, and the client
claims only success: everything else is `unclear` (no marker) or `refused` (error status).

## How the client finds the series number afterwards

| Path | Call | What it says |
|---|---|---|
| Search | `GET /search2.php?qs=<name>` | `<a href="/tvshow-4242.html">Weissensee (2010-2026)</a>` — id **and** name |
| Selection list | `GET /add.html`, `<select name="tvshow">` | `4242` → `Weissensee` |
| All series | `GET /tvshows.html` | rank · `tvshow-<id>-<staffeln>.html` · name · seasons · episodes · subtitles · years |

The search is the shortest path and shows a fresh series immediately, but it is **soft**: `Pagan Peak` finds
*Twin Peaks* and *Peaky Blinders*, so its result runs through the same strict comparison as the list and „no
hit" is no proof. The selection field of `/add.html` carried the new series immediately and is the fallback.
`/tvshows.html` did **not** carry it yet, because that catalogue is not rewritten with every creation.

## Who creates, and what is stored

The rules fix the page of the title inquiry, and creation happens **only** with the TMDb title, the TMDb
first-broadcast year and the TMDb season count, limited to 1..22 (F-M501). The number of the series stands
afterwards on the same level as the other title identifiers, and the client reads it instead of searching it
the next time.

After the creation the client searches the number — search first, then the list — and **stores it only on a
hit** (F-M516). If the site confirms the creation but the series is not in the list, the result is honestly
`created-id-unknown` and the client stores **nothing**. If the site does not confirm and the series is there
anyway, it was already present (`existed`), and the client stores the number without a second entry arising.
The plugin database holds the decision, not the wording, and the counter „TV Shows" in the sidebar does not
move, so reading a creation off it is no evidence.

## Languages

The site's codes are `en es fr de br ru ua it gr ar hu pl tr nl pt sv da fi ko cn jp bg cz ro` — 24 values, four
of them not ISO (`gr`, `cz`, `cn`, `jp`). A code the selection list does not know makes the form fall back to
its first entry, and the upload then lands as English; the client therefore checks against the table and sends
no unknown code.

ISO is mapped, and only here (F-M502): the client pulls an incoming code onto the site's code, so
`el`/`ell`/`gre` → `gr`, `cs`/`ces`/`cze` → `cz`, `zh`/`zho`/`chi` → `cn`, `ja`/`jpn` → `jp` (the four deviations
in both spellings). To that come the ISO 639-2 forms of the remaining site languages (`deu`/`ger` → `de`,
`fra`/`fre` → `fr`, `spa` → `es`, `pob` → `br`, `ukr` → `ua`, `nld`/`dut` → `nl`, `ron`/`rum` → `ro` …).

Case does not count, and the client drops region and script (`zh-Hans` → `cn`, `ja-JP` → `jp`) — **except**
`pt-BR`/`pt_BR`, which is Brazilian Portuguese (`br`), while `pt` and `pt-PT` stay at `pt`. Unknown means `null`
(no guessing), and the client rejects it with a line. The same bridge runs before the body is built, and so do
the field `lang` and the read-back. The file name keeps its ISO3 suffix from the table (`gre`, `cze`, `chi`,
`jpn`, `pob`, `rum`). **A common table with SubDL does not exist**; SubDL maps in its own client.

## Supported languages

The enumeration is closed: the mapping holds 24 site codes in 29 entries, because five ISO identifiers (`el`,
`cs`, `zh`, `ja`, `uk`) point at the same site codes as their aliases, so the site is addressed with 24 distinct
values (F-M735). A language outside that set is a language refusal decided before anything is extracted or
sent, and nothing is uploaded in its place. A missing code is never sent as a guess, because the form falls
back to its first entry and would publish the file under the wrong language.

## The jobs it answers

| Job | Question | Answer of this source |
| --- | --- | --- |
| search | which subtitles do you have for this title? | entries read off the episode page, with flag, release and date |
| fetch | give me this subtitle | the bytes of a `.srt` or a ZIP, and a file name built on the download page |
| send | take this subtitle, single or as a pack | accepted, rejected, busy or failed (F-M685) |
| report | this entry is broken | accepted, rejected, busy or failed (F-M685) |

It answers all four jobs over the site's own pages (F-M682). A send is a POST of the form above, and a pack is
offered as one container that the site accepts the same way it accepts a single file.

## Capabilities

| Capability | Answer of this source |
| --- | --- |
| short name | `tvst` (F-M731) |
| upload | yes |
| report | yes, behind the `Report` switch |
| season packs | yes, behind `Packs` and `Prefer season pack` |
| hearing-impaired marks | yes |
| quota | no |
| title creation | yes — a series the site does not know is looked up and created there (F-M734) |

## The account ledger

The source is absent from the ledger list rather than answering „cannot", because an absent source is never
asked while a source that answered „cannot" would have to be remembered as a refusal (F-M729). Nothing here can
be listed, nothing withdrawn, and no state of our own submissions is known from this side. No login is
visible either: `/login.html`, `/upload.html` and `/panel.html` answer with 404, and creating a series works
without an account, while the upload stays unverified.

## What it costs and what it limits

There is no allowance to read and no quota to report; the cost is page reads (F-M730). The run fetches the
series list once and keeps it, but never keeps an empty answer, so a glitch cannot poison the rest of the run.
After that the client reads per episode, one page for the entries and one for the detail of an entry.

The site names no quota and documents no throttling, so the general pause of the plugin applies when it is
bound in. Throttling was never observed, and a limit was never reached, so a quota is unproven in either
direction.

## Where it fails and how we answer

A series the site does not know is created first (F-M734); until it exists the send answers „not askable" and
sends nothing, so repeating the run cannot produce a second upload. A language the site does not carry is a
refusal decided from the mapping before the engine extracts anything, and nothing is sent (F-M735). An episode
the search does not name stays open for the other source, and a broken entry can be reported (F-M685).

## What is not proven

An upload without an account is not proven: an empty POST answers with HTTP 200 and an empty body, and no
captcha was visible, so nothing beyond that is claimed. The marker for „it already exists" is not proven either,
because a creation is not deletable on the site and the experiment was not run, and a plain rejection of a
creation is unproven as well.

## The boundary

The client is bound in (F-M485): the two switches really control fetch and upload. It records the origin of
every find with the identifier of the source in the plugin database (F-M486), so that a find from this source
is never uploaded to SubDL (F-M487). Season packs and the hearing-impaired pool stay SubDL's business; here
there are single episodes only.

The engine knows no plugin database, no identifier shape of ours and no row of our storage; it receives
connection, archive cache, logger and settings from outside and writes nothing itself (F-M682). The pipeline
does not name it: it reads the short name `tvst` as a data value (F-M731) and asks the source whether it
carries a language instead of consulting a table in the client. What comes back is a proposal with evidence,
and the pipeline decides what enters the store.

## The checks

All parsers are `static` and take HTML as a string, and the suite `scripts/tests/tvsubtitles` drives them with
the pages the site really delivered, from `fixtures/`, not with the live site: **139 checks** run there.

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 <dotnet>/dotnet run \
  --project scripts/tests/tvsubtitles/tvsubtitles.csproj -c Release -- <repo> --live
```

The probe above only reads and is not part of the checks. It shows the series list, with 2.774 entries in the
selection field including the placeholder `0`, and Sample Series = 512. Then come the result of the site search
for „Sample Series", seasons 1–9, 24 episode pages in season 2, page 31911 = 2x24 with 3 entries, and the download
of an English entry. It **creates nothing**; the creation is expressly not part of the probe.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M480, F-M481, F-M482, F-M485, F-M486, F-M487, F-M501, F-M502,
F-M507, F-M516, F-M682, F-M685, F-M729, F-M730, F-M731, F-M734 and F-M735. Guarded by `tvsubtitles`, `endpoints`
and `sources`.
