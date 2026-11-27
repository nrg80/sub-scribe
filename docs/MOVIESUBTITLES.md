# moviesubtitles.org — the film source

moviesubtitles.org carries subtitles for films only, and whether a medium of ours is a film or a series is
decided by TMDb, not by its name (F-M507).

## What it is

The site has **no API** (F-M506): `/api.php`, `/sitemap.xml` and `/rss.xml` answer with 302 to `/`;
`/admin.php` is an operator login in Ukrainian (cp1251), not a user account; `/login.php`, `/panel.php`
and `/upload.php` answer 302 to `/`. Everything runs over HTML and the forms of the site, so the client
reads a page and writes a form.

Layout, CSS classes (`left_articles`, `description`, `stat`, `footer`) and form wording are those of
tvsubtitles.net, and the header carries the reference „TV Show subtitles" to the sister: the same family,
the same build with different data. The site carries **films alone** — a series never reaches it, and the
film address of the family is never asked for a series (F-M507) — and it keeps no account we could read:
no login, no panel, no page of our own submissions.

## Which media reach it

**TMDb decides which source is asked — not the file name, not the folder name, not the library name**
(F-M507), and that within **one** source. At the top level there are **two** sources: SubDL and the
**tvst family**, and the family has **two addresses**: tvsubtitles.net for series, moviesubtitles.org for
films. The order setting ranks the family against SubDL (`tvst first` / `SubDL first`), not the addresses
against each other, so it stays two-valued, and for a film `tvst first` means **moviesubtitles.org
first**.

| TMDb record | Source |
|---|---|
| `movie` | **moviesubtitles.org** — and only that one; tvsubtitles.net is not asked for films |
| `tv` | **tvsubtitles.net** — and only that one; moviesubtitles.org is not asked for series |
| both | **SubDL** stays responsible for both; SubDL does not distinguish |

The resolution runs through TMDb and not through the name, because **the same name exists on both sites
and means different works there**. `Dune` is on moviesubtitles.org the film from 1984
(`movie-607.html`), and in the series catalogue of tvsubtitles.net the television miniseries from 2000,
next to `Children of Dune` and `Dune: Prophecy`. From „Dune" alone no source follows.

**A hit is known to be a film by five signs** (F-M506):

- **There are no series addresses.** `/tvshow-512.html` and `/episode-31911.html`, which designate a
  series and an episode on tvsubtitles.net, answer here with **302 to `/`**.
- **No series stands in the list.** `add.php?filter=` returns 0 hits for „game of thrones", „the office",
  „the simpsons", „chernobyl", „stranger things", „peaky blinders", „succession", „better call saul" and
  „breaking bad s01"; „breaking bad" hits only `El Camino: A Breaking Bad Movie (2019)`, a film.
- **Every row carries a four-digit year and nothing else.** No season field, no episode field, nowhere on
  the site and not in the upload form either (`tvid`, `lang`, `rip`, `release`, `author`, `UFile`…,
  `comments`).
- **The site itself says „movie".** The `<title>` of a film page ends in `… movie`, the heading reads
  `Download <Titel> (<Jahr>) subtitles`, where tvsubtitles.net carries `… season 9` or `… episode 2x24`.
- **The self-promotion does not count.** „movies and TV films" on the home page means television films; a
  series offering cannot be derived from it and was not found either.

The site does not say *which* film is meant when title and year are ambiguous (see „Ambiguity" under this
heading); the film identifier is unambiguous, the name is not.

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

```
search.php?q=oppenheimer      →  movie-24811.html      „Oppenheimer (2023)"
movie-24811.html              →  subtitle-132989.html  „english … (WEB-DL.MA)"
subtitle-132989.html          →  download-132989.html
download-132989.html          →  302 files/Oppenheimer_2023.WEB-DL.MA.en.zip
files/…zip                    →  92.522 B, one entry: Oppenheimer.WEB-DL.MA.en.srt (246.829 B, UTF-8 with BOM, CRLF)
```

**Ten defects, and what follows from them** (F-M506):

- **`search.php` answers with 500** on every one of seven checked fetches (five search terms, GET and
  POST, with and without referer), with and without hits. The body is still the correct hit list and only
  breaks off where the secondary subtitle list begins (`</ul><br><ul style="margin-left:2em">`, then
  nothing), so the status code is **not** usable as an error sign, the content is.
- **Titles stand inverted.** The site stores `Matrix, The (1999)`, `Lord of the Rings: The Two Towers,
  The (2002)`, `Old Man and the Sea, The (1999)`, and 52 of 492 O films end in `, The/A/An`.
  `filter=The Matrix` therefore returns **0 hits**, `filter=Matrix, The` exactly one. A resolver must try
  both forms or fall back to the core without the article: `filter=Matrix` → 5 hits, then check year and
  name.
- **The filter is a pure substring, anywhere in the title.** `filter=LOTR` hits `Allotria (Allegria)`;
  `filter=dark` hits 162 films; an appended year does not help (`Oppenheimer 2023` → 0 hits); case does
  not count. There is **no** upper limit: `filter=the` returns 6.674 rows in one response (548 KB).
- **`tvid` and the film identifier are the same number.** `movie-<id>.html`, `add.php?tvid=<id>` and the
  way back from the subtitle page (`Back to <Film>` → `movie-<id>.html`) carry it, and it is global and
  stable (Oppenheimer 24811, Matrix, The 4, Ocean's Thirteen 239). There is no second identifier and no
  season or episode level.
- **The download address answers with 302, not with bytes.** `Location` is **relative** (`files/…zip`)
  and can carry parentheses, `files/Matrix_The_1999.ru(2).zip`, because the name already existed; a client
  must resolve the address and take the parentheses along.
- **Unknown identifiers look like success.** `download-999999.html` → 302 to `files/` (empty, no file
  name), which is the error signature; `subtitle-999999.html` → **HTTP 200** with empty title, empty
  language and „size: 9140 kb", which is junk. Both are checked before a row comes out of it.
- **The zip contains 1 to 3 files, and the `parts` column is only a hint.** parts=3 (entry 9077) → three
  files `Matrix The.DVDRip.KiSS.part1/2/3.en.srt`; parts=2 (entry 2058, Russian) → **one** file `Matrix
  The (1999).ru.srt`. With several parts the files are segments that only joined make a film, and each
  segment carries its own timings.
- **The file name in the zip follows no fixed pattern.** Both forms occur:
  `<Titel>.<Rip>.<Release>.<lang>.srt` (Oppenheimer) and `<Titel> (<Jahr>).<lang>.srt` (Matrix, Russian),
  and the name in the zip is **not** identical to the name of the zip.
- **The year can stand twice in the heading.** `movie-16856.html` is called „O Beautiful (2002) (2002)",
  because the title already contained the year, so take the **last** bracket with four digits.
- **A film without subtitles is no 404.** `movie-6928.html` (Old Man and the Sea, The, 1999) answers 200
  with „0 subtitles downloaded  times", two spaces where the number otherwise stands. Such films are in
  the upload list but **not** in the film list (492 vs. 487 for O, 1.407 vs. 1.395 for A):
  `movies-<L>.html` shows only films with at least one entry, `add.php?let=<L>` all of them.

**Ambiguity.** There is no search by year, no requested-identifier parameter and no disambiguation page.
`filter=dune` gives five films: three „Dune" adaptations (1984, 2021, 2024), the documentary
`Jodorowsky's Dune` (2013) and `Suna no onna (Woman of the Dunes)` (1964). The decision falls in the
client: collect candidates, compare the year from the row next to the link, and when two candidates carry
the same year, compare the title narrowly (not similarly) and otherwise **not** guess — the rule the
series path already applies (F-M506).

The download is one source of the chain: the film address is asked next to SubDL, never instead of it,
and a film is never handed to the series address (F-M507).

## The path (upload) and creating a film

`GET /add.php?tvid=<id>` shows the form for exactly this film; `POST /add.php`,
`enctype="multipart/form-data"`, the target is `add.php` (relative, so `/add.php`). The fields are
literally those of the form (F-M506):

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
form is a bare multipart form, and unlike tvsubtitles.net even the `hi` marker for hearing-impaired
versions is missing; it names no permitted extension, no size specification and no moderation text. The
three file fields are declared as „CD" parts, so the site thinks in parts, as the `parts` column shows.
Whether `UFile` alone suffices and whether `author`/`comments` are mandatory can be settled **only by a real
upload** (see *What is not proven*).

### How the upload payload is built (F-M508)

The payload carries the fields in the order of the form: `tvid lang rip release`, author **only when
set**, `UFile` (`UFile2`/`UFile3` only for a multi-part film), comments, `go=1`. `release` carries the
release **group** from the release name, never the file name, and comments carries the identifiers
(TMDb, IMDb) and the client. The body is multipart with its own boundary, and the bytes of the file stand
unchanged in it.

The client runs the check **before** it builds the body: `tvid` > 0, language in the mapping (an ISO code
is mapped), rip in the mapping or empty, at least one file, the file not empty and ending `.srt` or
`.zip`. Anything else is a refusal with **one** line and **no** request. A dry run is decided by the
caller, never read from the configuration: it builds the same body, byte for byte, and puts nothing on
the wire (F-M508).

**The quality from the release name reaches the form as a rip value of this site, once, in the endpoint
and before the form** (F-M511). The mapping reads: `web` → `WEB`, `1080p BluRay x264` → `BluRay`, `WEB-DL` → `WEB-DL`,
`HDTV` → empty (this site does not know it), `x265` → empty (no codec fallback), empty → empty. These
mappings stay separate from tvsubtitles.net: this page keeps `WEB-DL` where the series page writes `WEB`
(F-M506, F-M511).

### The answer without an identifier (F-M488)

What the site says on acceptance is not proven (see *What is not proven*), so the client invents neither an
identifier nor a moderation:

- **HTTP 2xx** → accepted, identifier unknown; the text of the answer passes **unchanged**, only tags and
  whitespace collapsed. The pipeline then assigns `own:moviesubtitles:<time>:<8 hex>`, because
  „uploaded, identifier unknown" is provable and „identifier 12345" would be an assertion.
- **no 2xx** → failed, the reason is `HTTP <status>`, the text of the site goes along. A rejection that we
  invent would be as wrong as an acceptance that we invent.
- **dry run** → accepted, nothing sent, no text, but the size of the body that **would** be sent.

### The lock

The source's upload switch is unset by default, and unset means **off**. While it is off, a line stands in
the run instead of silence:

```
source moviesubtitles skipped: that source is switched off, nothing sent
[SubDL] DRY-RUN … — source moviesubtitles not sent: that source is switched off
```

The lock is the operator's switch, not a promise about the site, and it opens only on proven language
mapping (see *What is not proven*). The client makes no POST against the site while it is closed, and the suite
exercises the upload path against recorded pages and a counting handler alone (F-M508).

### Creating a film

`GET /add-movie.php` delivers a form to `/add-movie.php` (POST, multipart) with `name`, `year`
(`maxlength=4`), `go=1` and the submit `Send` (F-M515). The payload is name, year, `go=1` in the order of
the form, checked before sending: an empty name or a year outside 1888..current year+3 is a refusal
**without a request**, and a one-letter title is permitted. Body and boundary are the same in the dry run
and in the real case (F-M513).

The site warns itself: „Before adding new Movie please check if it already exist". That is a hint,
**no** technical duplicate protection for films, so the order is resolve first, then create, and only if
the search answered cleanly (F-M513).

**The answer is read at exactly two markers** (F-M515):

| Answer of the site | Reads as |
|---|---|
| `Movie has been added. Thank you.` | created |
| `This Movie already exist` (the site writes „exist") | already exists, nothing created |
| everything else — including the unchanged form returned to a POST without `go=1` | a rejection, with the text excerpt as reason |

A 2xx alone is **no** creation. The answer carries **no** `Location`, no film identifier and no link, so
the site's own identifier never comes from it, and the second sentence also comes for a film that the
site carries nowhere else: it proves the refusal, not the findability.

### How the duplicate guard decides (F-M513)

Three cases, and only these, checked **once**, before creating:

1. **Title with the same year present** (exactly one row) → take this number, **create nothing**.
2. **Title present, but a different year**, or a year unknown, or more than one row → **not** create and
   **not** upload; the decision carries the found year and the hit count, so the log line can name them
   (`… the site already carries this title — found year 2021, 1 hit; nothing created, no upload`). The
   operator wants no duplicates, so rather no upload than a second entry.
3. **No hit, but at least one readable answer** → create.

If the search answers **not readable at all**, „does not exist" does not apply and nothing is created
either (`unclear`, with the answer count 0). The decision — take, refuse, create or unclear — stands in
the result object, not only in the log, together with the found year and the hit count.

### How the new identifier is read

The answer names no identifier and is not interpreted. After a 2xx the same narrow search runs once more,
over title, title without the article and the inverted form, and the number comes from its hit list. If
the follow-up search does not name the film, the result reads **created, identifier not readable**, with
the text of the answer next to it, without a number and without an upload, since without tvid there is no
form (F-M513, F-M515). An HTTP error is a failure with the status as reason.

```
[moviesubtitles] 'Paddington in Peru' (2026) newly created — id 4711
[moviesubtitles] 'Dune' (2026): the site already carries this title — found year 2021, 1 hit; nothing created, no upload (F-M513)
[moviesubtitles] 'X' (2026) was created, but the follow-up search named no id — nothing stored, no upload (F-M513)
[moviesubtitles] DRY-RUN: 'X' (2026) would be created — 341 bytes of form body, nothing sent (F-M513)
```

**And the site does not carry the created film afterwards** (F-M515): the search stays at 0 hits, the
letter list stays unchanged, `add.php?filter=` delivers 0 rows, the statistics counter `Movies` does not
move, and the newest film page carries the year with an **empty** name. Creation therefore succeeds and
the subtitle **cannot** follow, because no tvid is readable; the client says that, does nothing further,
and a second creation attempt never falls.

### How the `tvid` of a film is found (F-M506)

```
GET /add.php?filter=oppenheimer
  → <li><a href="add.php?tvid=24811"><b>Oppenheimer</b></a> 2023</li>      (exactly one hit)
GET /add.php?tvid=24811
  → <b>Movie:</b> Oppenheimer (2023)   … form
```

`filter=The Matrix` (1999) → 0 hits; `filter=Matrix` → 5 hits, of which `Matrix, The (1999)` =
`tvid=4`. The path therefore leads over the **core** of the title and then over the year comparison, not
over the full TMDb title.

## Languages

### The 13 values of `<select name="lang">`

Wording of the site, in the order of the form; there are **exactly 13**, and the list carries no
`multiple`, so one language per upload.

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

**These 13 codes stand in the tvsubtitles list in identical spelling**, where that list has 23, so the
mapping of this site covers them completely, the deviations included: `gr` instead of `el`, `ua` instead
of `uk`, `br` for Brazilian Portuguese (F-M502). The number in the last column is the site's own
self-report (block „Site statistics"), not counted by us.

**Supported languages: a deliberately closed list** (F-M506, F-M735). What is missing here and exists there —
`nl pt sv da fi ko cn jp bg cz ro` — is **not** offered; a language outside these 13 has no language on
this site and is a **language refusal**: nothing is sent in its place, no fallback to English, no guess.
`pt`/`por` falls under that because the site carries only the Brazilian version. Case does not count, and
a region only counts where it **is** the language: `pt-BR`/`pt_BR` → `br`, while `pt` and `pt-PT` stay
outside the list.

The section headings of the film page are the display names of the same list, with two spaces before
`subtitles:`: `English`, `Portugese(br)` (written like that), `Ukrainian`. They appear **only for
languages in which the film has entries**: `movie-4.html` has 12 sections (no Ukrainian), `movie-239.html`
all 13.

### The values of `<select name="rip">` (F-M506)

`(leer)` stands in the form **twice**, as first and as last entry, next to `DVDRip`, `BluRay`, `BrRip`,
`BDRip`, `HDRip`, `WEB`, `WEBRip` and `WEB-DL`: nine different values with the empty one included, so the
empty entry stands twice and the named values are eight.

**This is not the list of tvsubtitles.net** (`WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay`). Common are only
`WEB`, `DVDRip`, `BDRip` and `BluRay`, so a rip table is kept per source: `HDRip`, `BrRip`, `WEBRip` and
`WEB-DL` do not exist there, `HDTV`, `HEVC` and `DVDSCR` not here. The list is **only a recommendation for
new uploads**, and existing entries carry free values next to it: `movie-4.html` stands `1080p`, `720p`,
`1080p.BlueRay`, `720p.BlueRay`, `BrRip`, `BDRip`, `DVDRip` and empty. Whoever reads must expect
everything; whoever writes sticks to the nine.

### Season and episode

There is none. The site knows **only** `tvid`, with no field, no column and no address (F-M506).

### Columns of an entry on the film page (F-M506)

Per entry (`<div class="subtitle">`): flag (`images/flags/<code>.gif`, `alt` = code), link
`/subtitle-<id>.html`, title `<Titel> <Jahr> <sprache> subtitles (<Rip>.<Release>)`, rating (red/green),
and a table with rip, upload date (`DD.MM.YYYY HH:MM`), size (`90.35 kb`), release, `parts` (1–3) and
downloads. **The rip therefore stands as its own column**, the release as well though that one is often
empty; reliable is the title, which carries `(<Rip>.<Release>)` completely, if need be as `()`.

**The language stands there three times**: section heading, flag and word in the title, and the three
agree. Unlike tvsubtitles.net, where the flag alone counts and the block boundary decides the assignment,
there is redundancy here for cross-checking.

## The four jobs

The source answers three of the four jobs — search, fetch and send — and it never pretends that the fourth
one exists: there is no report path, so a broken entry cannot be reported here (F-M682, F-M685). A fetch
needs the film record first, because the site's own film identifier leads to the form and to the entries,
and without it there is nothing to ask. A send answers with an HTTP status, and when the status is a success
the site's own identifier of our filing may still be unreadable; the engine passes the answer text through
unchanged rather than inventing one (F-M685).

## Capabilities

| Capability | This source |
|---|---|
| short name | none set, so the key `moviesubtitles` stands in (F-M731) |
| upload | yes |
| report | no |
| season packs | no, neither offered nor accepted |
| hearing-impaired mark | no, the form has no `hi` field |
| title creation | yes — a film the site does not know is looked up and created there (F-M734) |
| quota | no |

## The account ledger

The source is absent from the ledger list rather than answering „cannot", because an absent source is never
asked while a source that answered „cannot" would have to be remembered as a refusal (F-M729). Nothing here
can be listed, nothing withdrawn, and no state of our own submissions is known from this side.

## What it costs and what it limits

It answers the inquiry contract with one light, the address itself, and no quota (F-M730): there is no
allowance to read, the cost is page reads, one per film, and a letter list of 200–570 KB arrives uncompressed.
The site does not document throttling and none was observed, so the general pause of the plugin applies to
the client as well (`MinCallPauseSec`).

A film cut into several parts is recognised on the site's side and handled as its own case instead of being
mistaken for a broken entry (F-M506).

## Where it fails and how we answer

A missing or unreadable leading film record (`not-found`) skips the candidate with that reason, and the run
keeps going with the other sources (F-M507). A film record that does not answer is a timeout, and the run
continues with the other sources instead of failing as a whole. A language the site does not carry is a
refusal before anything is read on the site (F-M735).

## Site peculiarities

The body is UTF-8 — `<meta … charset=utf-8>` says so — **except** individual fragments in **cp1251**: the
LiveInternet counter (an `escape("Жж"+…)` in the JavaScript) and `/admin.php` (Ukrainian). The HTTP
header sets **no** `charset` (`Content-Type: text/html`), so whoever decodes the whole body strictly as
UTF-8 fails on exactly these bytes, while whoever takes cp1252 breaks all titles. The bytes of the
subtitle files are untouched by that: **UTF-8, CRLF**, with BOM only in the newer file (Oppenheimer),
without BOM in the three older ones (Matrix).

`http://` → 301 → `https://www.`, and `moviesubtitles.org` without `www` → 301 →
`https://www.moviesubtitles.org/`. `/movies.html` is **no** redirect but the film list (569 KB for A),
while `/tvshow-…`, `/episode-…`, `/login.php`, `/panel.php`, `/upload.php`, `/sitemap.xml`, `/rss.xml`
and `/api.php` answer 302 to `/`. `robots.txt` answers HTTP 200 with 0 bytes: no rule, no prohibition and
therefore no permission to pull more than necessary.

There is no login and no account, hence no „my uploads" page: what was uploaded is read **back from the
film page** (`movie-<id>.html`) or at most on `/new.html` (latest subtitles), and there is no further
read-back option (F-M506). The site does not check the `User-Agent` — home page and film page deliver the
same bytes with and without a browser user agent — but the client sends a browser user agent on every
fetch. Several third-party scripts (`cognatesyringe.com`, `acint.net`, LiveInternet) stand in the middle
of the content, so a parser may rely on **no** position in the document, only on classes and the table
structure.

## What is not proven

Everything here is **unchecked**, because a write access would have been necessary for it.

- **Whether `add.php` accepts at all.** There is no hint of moderation, no thank-you text and no captcha,
  but also no proof that an upload without login goes through. With tvsubtitles.net the suspicion reads
  „accepts, perhaps moderates"; here not even that is checked.
- **What the answer to an upload looks like.** Whether HTTP 200 with text, whether a redirect, whether a
  new subtitle identifier comes back (with tvst none comes), whether a confirmation text appears:
  **unknown**.
- **Whether `UFile` alone suffices**, whether `UFile2`/`UFile3` may be filled only for multi-part films,
  and whether an upload with three files appears as one entry with `parts`=3. The parts column suggests
  it; it is not proven.
- **Which extensions the site takes.** The form says nothing (no `accept=`), and `.srt` and `.zip` are
  proven for the *downloadable* files, not for uploading.
- **Whether `author` and `comments` are mandatory** and what happens when they are missing. No
  `required`, no asterisk; one knows more only after a POST.
- **Whether the file name of the upload is taken over.** The files in the zip are named
  `<Titel>.<Rip>.<Release>.<lang>.srt` or `<Titel> (<Jahr>).<lang>.srt`, so built server-side from title,
  rip, release and language; it looks as if the site discards our own file name, but that is not proven.
- **Whether the site prevents duplicate entries** (same film, same language, same rip, same release).
  `add-movie.php` warns only about creating a duplicate *film*; for subtitles there is no visible hint.
- **Whether the 500 on `search.php` is permanent** or a broken installation that changes with an update.
  It was there with every query that was sent.
- **How the site reacts to umlauts and diacritics** (`Amélie` vs `Amelie`): only case is proven to be
  independent, not accent folding.
- **Whether there is a rate limit over a longer time.** Six requests say nothing about one.
- **Whether the language mapping works in effect for every language.** It is shown for **EN**: an upload
  with the site's code appeared publicly on the site as that language, with `Rip WEB` and a comment line
  carrying the identifiers and the client. The other twelve codes are a table, not a proven effect, and
  the lock stays until that mapping is proven against the real site.
- **Whether a created film immediately has entries and pages**, so that `movie-<id>.html` answers: the
  newest film page carries the year `(1994)` with an **empty** name, and whether that is the created film
  is **not proven by title**.
- **A third sentence of `add-movie.php`** (e.g. „year invalid") is not proven; the client treats
  everything outside the two markers as a rejection.

## The boundary

**What this source does not answer** (F-M507, F-M508): it keeps no account, so nothing of ours can be
listed or withdrawn and no state of our own submissions is known from this side. No login exists either,
so there is no account ledger and no inquiry about one. No report path, no pack, no quota, no
hearing-impaired marker and no short name of its own: a series never reaches this source, and the client
refuses a request for a pack with a reason instead of attempting it. No language outside the 13 — a language
refusal before anything is read on the site, with nothing sent in its place.

**What the client never asserts.** Not an identifier from an answer: where the site names none, the
answer text passes through and the pipeline assigns `own:moviesubtitles:<time>:<8 hex>`, never a number
that nobody read (F-M488). Not an upload without a film identifier: without `tvid` there is no form, and
the candidate is skipped with `no film id (not-found)` rather than invented (F-M507). Not a film that the
site does not carry: creation happens only in the upload path, only with the upload switch on and only
after the search answered cleanly, while the download path never creates (F-M513). Not a second entry:
when the title is there with another year, nothing is created and nothing uploaded (F-M513). Not a
moderation: no state between sent and accepted is claimed (F-M508).

**What is kept of the site's identifier.** One column of the plugin database carries the film identifier,
addressed by the media hash or, when no hash is known, by the path of the media file — the same column,
only a different address. No hash and no path row, or several rows with the same path, means **nothing is
written** and no row is chosen; the number then holds for this run only, and the note says so (F-M517):

```
[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) is film 13392 — not stored: no media hash known — the number applies only for this run.
```

The note names the address it took: `stored (via the media hash).`, `stored via the path (no media hash
known).` or `not stored: no media hash and no unambiguous path line in the plugin database — the number
holds only for this run.` Without a readable path the stored number is read back over the path as well,
otherwise it would be invisible to every later run (F-M517).

**A known origin is not uploaded elsewhere** (F-M507): what was found here is not sent to another source,
and what was found elsewhere is not sent here as a second entry.

**Where the engine stops** (F-M682): it opens no connection of its own, receives network connection, archive
cache, logger and settings from outside and writes nothing itself. It knows no plugin database, no
identifier shape of ours and no row of our storage, and its film resolution uses the media identifiers the
pipeline hands in. The pipeline names it nowhere; it reads the capabilities as an answer and receives a
proposal with evidence — an identifier, a state, a plain text.

## The checks

The suite `scripts/tests/moviesubtitles/` drives the client and the upload endpoint against pages that
the site really delivered, recorded as fixtures, and `--live` only reads. It counts **123** checks:

| Rule | Bound as | Checks |
|---|---|---|
| F-M506 — the client against the recorded pages, and F-M508 — the upload endpoint against a counting handler | T228 | 84 |
| F-M510 — the bracketed year separated off before the TMDb title comparison | T232 | 9 |
| F-M511 — the raw quality mapped onto the site's rip value in the endpoint | T233 | 5 |
| F-M513 — creating a film, the duplicate guard, the identifier looked up | T235 | 15 |
| F-M515 — the two answer markers of `add-movie.php` | T237 | 6 |
| F-M517 — the film number written over the path without a media hash | T239 | 4 |

**The tables and the mapping** (F-M506): 13 language codes with display name and file code, none of which
silently becomes English. The ISO forms of the 13 are mapped; `pt`, `por`, `nl`, `sv`, `da`, `fi`, `ko`,
`zh`, `ja`, `bg`, `cs`, `ro`, an unknown code, an empty value and `null` all yield nothing, and the upload
refuses them. The rip table carries the selection field's nine values in its order and is not the
tvsubtitles one: `WEB-DL` stays `WEB-DL`, `BluRay.x265` becomes `BluRay`, while `x265`, `HEVC` or `H.265`
alone yield the empty value, `HDTV` likewise.

**The resolution** (F-M506, F-M510): `Dune` (1984) and `Dune: Part One` (2021) are found via the TMDb
title, a wrong year yields nothing, the inverted spelling is found (`The Opposite of Sex` → `Opposite of
Sex, The`), a partial hit is no hit and two identical candidates are no hit either. A trailing bracketed
year is split off before the comparison and only there: `Oppenheimer (2023)` → `Oppenheimer`, the
doubling `O Beautiful (2002) (2002)` loses exactly one year, and a bracketed title or a year inside the
title stays untouched.

**The three list views** (F-M506): the film list with 487 rows, the upload list with 492 and the search
with 5 rows despite HTTP 500, including a repeated year in the name. **The film page** holds 54 entries
in 12 sections and 51 in 13, with the language from the flag and from the section heading as the
counter-check, free rip values kept, the doubled year bracket and the film **without** entries.

**The entry page and a junk page** (F-M506): the entry page with rip, release, `filename:` and `parts`,
and the junk page of an unknown identifier yielding nothing. **The download** follows the **relative**
302, keeps parentheses in the target (`ru(2).zip`), takes the file with the language code out of the zip,
marks multi-parters, refuses a target `files/` and returns bare `.srt` bytes unchanged.

**The upload payload** (F-M506, F-M508): the form's order `tvid lang rip release author UFile UFile2
UFile3 comments go` with `go=1`, no `author` when empty, the release group instead of the file name,
identifiers and client in `comments`, and `UFile2`/`UFile3` only when filled. A `tvid` of 0, an unknown
language, a rip outside the table, missing identifiers, an empty file, a wrong extension and a third
extra are refused **without a single request**. Four tickets are refused before any request arises: one
with season or episode, one with a season pack, one with a foreign language and one with a missing number
and no year. The dry run reports the body and sends nothing; a 2xx reads as „accepted, identifier
unknown", a 500 as a failure carrying the page's text.

**The quality mapping** (F-M511): six raw values — `web`, `1080p BluRay x264`, `WEB-DL`, `HDTV`, `x265`
and empty — are accepted, and each produces a payload byte-identical to the payload of its mapped value,
while the foreign mapping is not borrowed. **The creation** (F-M513, F-M515) carries exactly `name`,
`year` and `go=1` in that order, neither a file name nor tvid. An empty name, 1887 and a year above the
bound are refused without a request, the bounds themselves (1888, current year+3) and the one-letter
title `X` go through. The identifier comes from the lookup, never from the answer, and a 500 is a failure
without a lookup; an accepted POST without a hit yields „created, identifier not readable" with the
answer text. The guard decides take, refuse (found year 1999), create, and `unclear` with an answer count
of 0; two rows with the same title and year are a refusal with a hit count of 2. The two markers are read
literally as the page writes them („exist", not „exists"), and the unchanged form and empty text are a
rejection with **0** lookups.

**The film number without a hash** (F-M517): a row that was created via its hash and carries the path
receives the number, the row count stays the same, and the second call reads instead of searching
(exactly one GET). Without a hash and without a path row the number is found and the plugin database
keeps **0** rows, with the note „holds for this run only"; a path carried by two rows is no address, so
nothing is chosen. The suite never posts to the site: the upload and the creation run against recorded
pages and a counting handler, and the plugin database of the last block is a throwaway one in the
scratch area.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M488, F-M506, F-M507, F-M508, F-M510, F-M511, F-M513,
F-M515, F-M517, F-M682, F-M685, F-M729, F-M730 and F-M735. Guarded by `moviesubtitles`, `endpoints` and
`sources`.
