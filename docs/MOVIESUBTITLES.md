# moviesubtitles.org — the film source

moviesubtitles.org carries subtitles for films only, and whether a medium of ours is a film or a series is
decided by TMDb, not by its name (F-M507).

## What it is

The site has **no API** (F-M506): `/api.php`, `/sitemap.xml` and `/rss.xml` answer with 302 to `/`,
`/admin.php` is an operator login in Ukrainian (cp1251), and `/login.php`, `/panel.php` and `/upload.php`
answer 302 to `/`. Everything runs over HTML and the forms of the site, so the client reads a page and writes
a form. Layout and form wording are those of tvsubtitles.net, and the header carries the reference „TV Show
subtitles" to the sister: the same build with different data. The site keeps no account we could read — no
login, no panel, no page of our own submissions.

## Which media reach it

**TMDb decides which source is asked — not the file name, not the folder name, not the library name**
(F-M507). There are two sources, SubDL and the **tvst family**, and the family has two addresses:
tvsubtitles.net for series, moviesubtitles.org for films. The order setting ranks the family against SubDL
(`tvst first` / `SubDL first`), not the addresses against each other.

| TMDb record | Source |
|---|---|
| `movie` | **moviesubtitles.org** — and only that one; tvsubtitles.net is not asked for films |
| `tv` | **tvsubtitles.net** — and only that one; moviesubtitles.org is not asked for series |
| both | **SubDL** stays responsible for both; SubDL does not distinguish |

The resolution runs through TMDb and not through the name, because **the same name means different works on
the two sites**. One word names a film from 1984 here (`movie-607.html`). There it names a television
miniseries from 2000, next to two sequels built on the same word.

**A hit is known to be a film by five signs** (F-M506).

- **The series addresses are dead.** `/tvshow-512.html` and `/episode-31911.html` answer with **302 to `/`**.
- **No series stands in the list.** `add.php?filter=` returns 0 hits for four series names, and one of their words
  alone hits only a television film.
- **Every row carries a four-digit year and nothing else.** No season or episode field exists anywhere, and
  the upload form has none either (`tvid`, `lang`, `rip`, `release`, `author`, `UFile`, `comments`).
- **The site calls itself a film site.** The `<title>` of a film page ends in `… movie` and the heading reads
  `Download <Titel> (<Jahr>) subtitles`, where tvsubtitles.net writes `… season 9` or `… episode 2x24`.
- **The self-promotion does not count.** „movies and TV films" on the home page means television films.

The site does not say *which* film is meant when title and year are ambiguous: the identifier is unambiguous,
the name is not.

## The path (download)

| Step | Address | What comes out of it |
|---|---|---|
| search | `GET /search.php?q=<titel>` | hit list `Titel (Jahr)` → `/movie-<id>.html` — **answers with HTTP 500**, body still usable |
| or filter | `GET /add.php?filter=<titel>` | the same selection in the upload view: `add.php?tvid=<id>` + year |
| or page | `GET /movies-<A…Z\|0>.html` | the film list per initial letter (487–1.395 films, one page, no paging) |
| film page | `GET /movie-<id>.html` | all entries of the film, grouped into sections by language |
| entry | `GET /subtitle-<id>.html` | rip, release, `filename:`, size, date, compatible releases, link `download-<id>.html` |
| file | `GET /download-<id>.html` | **302** → `Location: files/<…>.zip` (relative) |
| bytes | `GET /files/<…>.zip` | `application/zip`, 1–3 `.srt` inside |

**Ten defects and what follows from them** (F-M506).

- **`search.php` answers 500** on all seven checked fetches, with and without hits. The body is still the
  correct hit list and only breaks off where the secondary list begins, so the status code is no error sign
  and the content is.
- **Titles stand inverted** (`Sample Film, The (1999)`), and 52 of 492 O films end in `, The/A/An`. `filter=The
  Matrix` returns 0 hits, `filter=Matrix` five.
- **The filter is a pure substring**, anywhere in the title: `filter=LOTR` hits `Allotria (Allegria)`,
  `filter=dark` 162 films, `filter=the` 6.674 rows in 548 KB. An appended year does not help.
- **`tvid` is the film identifier**, one global number (Sample Film 24811, Sample Film, The 4, Ocean's Thirteen
  239), with no season or episode level.
- **The download answers with a relative 302** that can carry parentheses (`files/Sample_Film_JJJJ.ru(2).zip`).
- **Unknown identifiers look like success**: `download-999999.html` → 302 to `files/` with no name,
  `subtitle-999999.html` → 200 with „size: 9140 kb". Both are checked before a row comes out.
- **The zip holds 1 to 3 files and `parts` is only a hint** (Sample Film: 92.522 B, one entry of 246.829 B in UTF-8 with BOM): parts=2 on entry 2058 delivers one file, and
  with several parts the files are segments that only joined make a film.
- **The name inside the zip follows no pattern** (`<Titel>.<Rip>.<Release>.<lang>.srt` or
  `<Titel> (<Jahr>).<lang>.srt`) and differs from the name of the zip.
- **The year can stand twice** in the heading (`O Beautiful (2002) (2002)`), so the last four-digit bracket
  counts.
- **A film without subtitles is no 404**: `movie-6928.html` answers 200 with „0 subtitles". That is why the
  upload list carries 492 O films against 487 in the film list, and 1.407 against 1.395 for A.

**Ambiguity.** There is no search by year, no requested-identifier parameter and no disambiguation page:
one filter word gives five films: three adaptations of the same work (1984, 2021, 2024), a
documentary about it (2013) and a film whose title carries the same word (1964). The decision falls in the client —
collect candidates, compare the year next to the link, compare the title narrowly when two carry the same
year, and otherwise **not** guess (F-M506).

The film address is asked next to SubDL, never instead of it, and a film is never handed to the series
address (F-M507).

## The path (upload) and creating a film

`GET /add.php?tvid=<id>` shows the form for exactly this film; `POST /add.php`,
`enctype="multipart/form-data"`, carries the fields of the form (F-M506):

| Field | Type | Content |
|---|---|---|
| `tvid` | hidden | film identifier; already stands in the form (`24811`) |
| `lang` | select | the site's language code, 13 values (see *Languages*) |
| `rip` | select | `DVDRip BluRay BrRip BDRip HDRip WEB WEBRip WEB-DL` or empty |
| `release` | text | free text (release group), e.g. `MA`, `YTS`, `KiSS` |
| `author` | text | free text — **mandatory or not: unchecked**, see *What is not proven* |
| `UFile` | file | file 1 |
| `UFile2` | file | „only if 2 and more CD" |
| `UFile3` | file | „only if 3 CD" |
| `comments` | textarea | free text |
| `go` | hidden | `1` |

**No `accept=` on the file fields, no `MAX_FILE_SIZE`, no token, no captcha and no forced referer.** The
three file fields are declared as „CD" parts, so the site thinks in parts, as the `parts` column shows.
Whether `UFile` alone suffices and whether `author`/`comments` are mandatory can be settled only by a real
upload (see *What is not proven*).

### How the upload payload is built (F-M508)

The payload carries the fields in the order of the form: `tvid lang rip release`, author **only when set**,
`UFile` (`UFile2`/`UFile3` only for a multi-part film), comments, `go=1`. `release` carries the release
**group**, never the file name, and comments carries the identifiers (TMDb, IMDb) and the client. The check
runs **before** the body: `tvid` > 0, language in the mapping, rip in the mapping or empty, at least one file,
not empty and ending `.srt` or `.zip`. Anything else is a refusal with **one** line and **no** request. A dry
run is decided by the caller: it builds the same body, byte for byte, and puts nothing on the wire.

**The quality reaches the form as a rip value of this site, once, in the endpoint and before the form**
(F-M511): `web` → `WEB`, `1080p BluRay x264` → `BluRay`, `WEB-DL` → `WEB-DL`, `HDTV` → empty, `x265` → empty,
empty → empty. These mappings stay separate from tvsubtitles.net, which writes `WEB` where this page keeps
`WEB-DL` (F-M506, F-M511).

### The answer without an identifier (F-M488)

The client invents neither an identifier nor a moderation. HTTP 2xx reads as accepted with the identifier
unknown, the answer text passes unchanged, and the pipeline assigns `own:moviesubtitles:<time>:<8 hex>`. No
2xx reads as failed with `HTTP <status>` and the site's text along. A dry run reads as accepted with nothing
sent and the size of the body that would have gone out.

### The lock

The upload switch is unset by default, and unset means **off**, which the run says instead of staying silent:

```
[SubDL] DRY-RUN … — source moviesubtitles not sent: that source is switched off
```

The lock is the operator's switch and opens only on proven language mapping (see *What is not proven*). While
it is closed the client makes no POST, and the suite exercises the upload path against recorded pages and a
counting handler alone (F-M508).

### Creating a film

`GET /add-movie.php` delivers a form to `/add-movie.php` (POST, multipart) with `name`, `year` (`maxlength=4`),
`go=1` and the submit `Send` (F-M515). The payload is name, year, `go=1` in that order, checked before
sending: an empty name or a year outside 1888..current year+3 is a refusal **without a request** (1887 stands
just outside), while a one-letter title is permitted (F-M513). The site warns itself „Before adding new Movie please check if it
already exist" — a hint, no technical protection. So the order is resolve first, then create, and only if the
search answered cleanly.

**The answer is read at exactly two markers** (F-M515): `Movie has been added. Thank you.` reads as created,
`This Movie already exist` (the site writes „exist") as already there. Everything else — including the
unchanged form returned to a POST without `go=1` — is a rejection with the text excerpt as reason. A 2xx alone
is **no** creation, and the answer carries no `Location`, no identifier and no link.

### How the duplicate guard decides (F-M513)

Three cases, checked once, before creating. A title with the same year in exactly one row → take that number
and create nothing. A title with another year, an unknown year or more than one row → create nothing and
upload nothing, with the found year and the hit count in the decision (`… found year 2021, 1 hit; nothing
created`). No hit with at least one readable answer → create. If the search answers not readable at all,
„does not exist" does not apply and nothing is created either (`unclear`, answer count 0).

### How the new identifier is read

After a 2xx the same narrow search runs once more, over title, title without the article and the inverted
form. If it names no film, the result reads **created, identifier not readable**, without a number and
without an upload, since without tvid there is no form (F-M513, F-M515); an HTTP error is a failure with the
status as reason.

```
[moviesubtitles] 'Sample Film' (2026) newly created — id 4711
[moviesubtitles] 'Another Film' (2026): the site already carries this title — found year 2021, 1 hit; nothing created (F-M513)
[moviesubtitles] 'X' (2026) was created, but the follow-up search named no id — nothing stored (F-M513)
```

**And the site does not carry the created film afterwards** (F-M515): the search stays at 0 hits, the letter
list is unchanged, `add.php?filter=` delivers 0 rows, the `Movies` counter does not move, and the newest film
page carries the year `(1994)` with an **empty** name. Creation succeeds and the subtitle cannot follow; the
client says that and a second attempt never falls.

### How the `tvid` of a film is found (F-M506)

```
GET /add.php?filter=oppenheimer   →  add.php?tvid=24811   (exactly one hit)
GET /add.php?tvid=24811           →  <b>Movie:</b> Sample Film (2023)   … form
```

`filter=The Matrix` (1999) → 0 hits, `filter=Matrix` → 5 hits, of which `Sample Film, The (1999)` = `tvid=4`. The
path leads over the **core** of the title and then over the year comparison, not over the full TMDb title.

## Languages

### The 13 values of `<select name="lang">`

Wording of the site, in the order of the form; there are **exactly 13**, and the list carries no `multiple`,
so one language per upload. The number in the last column is the site's own self-report, not counted by us.

| Value | Display | Flag | Entries (site statistics) |
|---|---|---|---|
| `en` | english | `images/flags/en.gif` | 46.909 |
| `es` | spanish | `es.gif` | 6.982 |
| `fr` | french | `fr.gif` | 9.148 |
| `de` | german | `de.gif` | 1.102 |
| `br` | portugese(br) | `br.gif` | 7.772 |
| `ru` | russian | `ru.gif` | 2.876 |
| `ua` | ukrainian | `ua.gif` | 174 |
| `it` | italian | `it.gif` | 1.599 |
| `gr` | greek | `gr.gif` | 9.976 |
| `ar` | arabic | `ar.gif` | 5.280 |
| `hu` | hungarian | `hu.gif` | 3.804 |
| `pl` | polish | `pl.gif` | 3.320 |
| `tr` | turkish | `tr.gif` | 7.082 |

**These 13 codes stand in the tvsubtitles list in identical spelling**, which has 23, so the mapping covers
them completely, deviations included: `gr` instead of `el`, `ua` instead of `uk`, `br` for Brazilian
Portuguese (F-M502).

**A deliberately closed list** (F-M506, F-M735). What is missing here and exists there — `nl pt sv da fi ko cn
jp bg cz ro` — is **not** offered, so a language outside these 13 has no language on this site. It is a
**language refusal**: nothing sent in its place, no fallback to English, no guess. `pt`/`por` falls under
that, because the site carries only the Brazilian version. Case does not count, and a region only counts
where it **is** the language: `pt-BR`/`pt_BR` → `br`, while `pt` and `pt-PT` stay outside.

The section headings of the film page are the display names of the same list. They appear **only for
languages in which the film has entries**: `movie-4.html` has 12 sections (no Ukrainian), `movie-239.html`
all 13.

### The values of `<select name="rip">` (F-M506)

`(leer)` stands in the form **twice**, as first and as last entry, next to `DVDRip`, `BluRay`, `BrRip`,
`BDRip`, `HDRip`, `WEB`, `WEBRip` and `WEB-DL`: nine different values with the empty one included. **This is
not the list of tvsubtitles.net** (`WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay`), common are only `WEB`,
`DVDRip`, `BDRip` and `BluRay`, so a rip table is kept per source. The list is only a recommendation for new
uploads, and existing entries carry free values next to it (`1080p`, `720p`, `1080p.BlueRay`, `720p.BlueRay`,
`BrRip`, `BDRip`, `DVDRip`, empty on `movie-4.html`): whoever reads must expect everything, whoever writes
sticks to the nine.

### Season and episode

There is none. The site knows **only** `tvid`, with no field, no column and no address (F-M506).

### Columns of an entry on the film page (F-M506)

Per entry (`<div class="subtitle">`): flag (`images/flags/<code>.gif`, `alt` = code), link
`/subtitle-<id>.html`, title `<Titel> <Jahr> <sprache> subtitles (<Rip>.<Release>)`, rating, and a table with
rip, upload date (`DD.MM.YYYY HH:MM`), size (`90.35 kb`), release, `parts` (1–3) and downloads. The language
stands there three times — section heading, flag and word in the title — and the three agree, which
tvsubtitles.net does not offer.

## The four jobs and what this source can do

The source answers three of the four jobs — search, fetch and send — and never pretends that the fourth one
exists: there is no report path, so a broken entry cannot be reported here (F-M682, F-M685). A fetch needs the
film record first, because the site's own identifier leads to the form and to the entries. A send answers
with an HTTP status whose identifier may stay unreadable (F-M685). Upload is yes, report is no, season packs
are neither offered nor accepted, there is no hearing-impaired marker and no quota, title creation is yes
(F-M734), and no short name is set, so the key `moviesubtitles` stands in (F-M731). The source is absent from
the ledger list rather than answering „cannot", because an absent source is never asked while an answer
„cannot" would have to be remembered as a refusal (F-M729).

## What it costs, and where it fails

It answers the inquiry contract with one light, the address itself, and no quota (F-M730): the cost is page
reads, one per film, and a letter list of 200–570 KB arrives uncompressed. The site does not document
throttling and none was observed, so the general pause of the plugin applies (`MinCallPauseSec`). A film cut
into several parts is recognised on the site's side and handled as its own case instead of being mistaken for
a broken entry (F-M506). A missing or unreadable film record (`not-found`) skips the candidate and the run
keeps going with the other sources (F-M507), as it does on a timeout, and a language the site does not carry
is refused before anything is read (F-M735).

## Site peculiarities

The body is UTF-8 — `<meta … charset=utf-8>` says so — **except** individual fragments in **cp1251**: the
LiveInternet counter and `/admin.php`. Since the HTTP header sets no `charset`, whoever decodes the body
strictly as UTF-8 fails on exactly these bytes, while whoever takes cp1252 breaks all titles. The subtitle
files are untouched: **UTF-8, CRLF**, with BOM only in the newer one. `http://` answers 301 to `https://www.`,
`/movies.html` is no redirect but the film list (569 KB for A), `robots.txt` answers 200 with 0 bytes, and
there is no login, so an upload is read back from `movie-<id>.html` or `/new.html` alone (F-M506). The
`User-Agent` is not checked, yet the client sends a browser agent on every fetch, and several third-party
scripts stand in the middle of the content — so a parser may rely on **no** position in the document, only on
classes and the table structure.

## What is not proven

Everything here is unchecked, because a write access would have been necessary.

- **Whether `add.php` accepts at all** — no moderation hint, no captcha, but also no proof that an upload
  without login goes through. **What the answer looks like** — status, text, redirect, identifier — is unknown.
- **Whether `UFile` alone suffices**, whether `UFile2`/`UFile3` carry only multi-part films, whether three files
  appear as one entry with `parts`=3, and **which extensions the site takes**: `.srt`/`.zip` are proven for the
  *downloadable* files only.
- **Whether `author` and `comments` are mandatory**, whether the site prevents a second entry of the same film,
  language, rip and release, and **whether the file name of the upload is taken over** — the names in the zip
  look built server-side.
- **Whether the 500 on `search.php` is permanent**, **how the site folds umlauts** (`Amélie` vs `Amelie`) and
  **whether a rate limit exists over a longer time**: six requests say nothing about one.
- **Whether the language mapping works for every language** — shown for **EN**, where an upload with the site's
  code appeared publicly with `Rip WEB`. The other twelve codes are a table, and the lock stays until that
  mapping is proven.
- **Whether a created film immediately has entries and pages** — the newest film page carries the year `(1994)`
  with an empty name. A third sentence of `add-movie.php` is not proven either.

## The boundary

**What this source does not answer** (F-M507, F-M508). It keeps no account, so nothing of ours can be listed or
withdrawn, and there is no ledger and no inquiry about one. There is no report path, no pack, no quota, no
hearing-impaired marker and no short name of its own. A series never reaches this source, and the client
refuses a pack with a reason instead of attempting it (F-M682).

**What the client never asserts.** Not an identifier from an answer: where the site names none, the text passes
through and the pipeline assigns `own:moviesubtitles:<time>:<8 hex>` (F-M488). Not an upload without a film
identifier: without `tvid` the candidate is skipped with `no film id (not-found)` (F-M507). Not a film the site
does not carry: creation happens only in the upload path, with the switch on and after a clean search, while
the download path never creates (F-M513). Not a second entry, and not a moderation, since no state between sent
and accepted is claimed (F-M508). A known origin is not uploaded elsewhere (F-M507).

**What is kept of the identifier.** One column of the plugin database carries the film identifier, addressed by
the media hash or, when no hash is known, by the path of the media file. No hash and no path row, or several
rows with the same path, means **nothing is written** (F-M517):

```
[moviesubtitles] ?: 'Sample Film' (2015) is film 13392 — not stored: no media hash known — the number applies only for this run.
```

The note names the address it took, `stored (via the media hash).` or `stored via the path (no media hash
known).`, and a number without one is read back over the path as well, otherwise it would be invisible to
every later run (F-M517).

**Where the engine stops** (F-M682): it opens no connection of its own, receives connection, archive cache,
logger and settings from outside and writes nothing itself. It knows no plugin database and no row of our
storage, and its film resolution uses the media identifiers the pipeline hands in.

## The checks

The suite `scripts/tests/moviesubtitles/` drives the client and the upload endpoint against pages the site
really delivered, and `--live` only reads. It counts **123** checks:

| Rule | Bound as | Checks |
|---|---|---|
| F-M506 — the client against the recorded pages, and F-M508 — the upload endpoint against a counting handler | T228 | 84 |
| F-M510 — the bracketed year separated off before the TMDb title comparison | T232 | 9 |
| F-M511 — the raw quality mapped onto the site's rip value in the endpoint | T233 | 5 |
| F-M513 — creating a film, the duplicate guard, the identifier looked up | T235 | 15 |
| F-M515 — the two answer markers of `add-movie.php` | T237 | 6 |
| F-M517 — the film number written over the path without a media hash | T239 | 4 |

Walked in full are the two tables, and neither 13 language code nor one of the nine rip values silently
becomes something else. Read as pages are the film list with 487 rows, the upload list with 492, the search
with 5 rows despite HTTP 500, the film page with 54 entries in 12 sections and 51 in 13, and the junk page of an
unknown identifier. The download follows the **relative** 302, keeps parentheses in the target (`ru(2).zip`) and
refuses a target `files/`. Refused **without a single request**: a `tvid` of 0, an unknown language, a rip
outside the table, missing identifiers, an empty file, a wrong extension, a third extra, and four tickets
carrying season or episode, a season pack, a foreign language or a missing number. The creation carries
exactly `name`, `year` and `go=1` and lets 1888, the current year+3 and the one-letter title `X` through. A row
created via its hash receives the number, and without a hash and without a path row the plugin database keeps
**0** rows. The suite never posts to the site.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M488, F-M502, F-M506, F-M507, F-M508, F-M510, F-M511, F-M513,
F-M515, F-M517, F-M682, F-M685, F-M729, F-M730, F-M731, F-M734 and F-M735. Guarded by `moviesubtitles`,
`endpoints` and `sources`.
