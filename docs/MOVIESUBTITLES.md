# moviesubtitles.org — protocol and methods

**Owner: the site.** Paths, tables, peculiarities and methods of moviesubtitles.org; the contract view of the same source stands in `engines/moviesubtitles.md`.

As of 15.11.2026. Recorded **read-only throughout**: no POST to `add.php`, `add-movie.php` or
`rate.php`, no upload, no film created, no login attempted. Nothing was written —
not even the search, although it submits its form by POST and creates nothing thereby (checked,
see below). Everything that stays uncertain anyway is listed under
[What is NOT proven](#what-is-not-proven) — not disguised as a guess, but open.

The site has **no API**: `/api.php`, `/sitemap.xml`, `/rss.xml` answer with 302 to `/`;
`/admin.php` is an operator login (Ukrainian, cp1251), not a user account. `/login.php`,
`/panel.php`, `/upload.php` → 302 to `/`. Everything runs over HTML and the forms of the site.

Layout, CSS classes (`left_articles`, `description`, `stat`, `footer`) and form wording are those
of tvsubtitles.net, and the header carries the reference „TV Show subtitles" to the sister. It is
the same family — the same build, different data.

## Contents

- [Assignment](#assignment)
- [The path (download)](#the-path-download)
- [The path (upload)](#the-path-upload)
- [Tables](#tables)
- [Site peculiarities](#site-peculiarities)
- [What is NOT proven](#what-is-not-proven)
- [Implemented in stage 1](#implemented-in-stage-1)
- [Implemented in stage 2b — the upload path (locked)](#implemented-in-stage-2b-the-upload-path-locked)
- [Implemented in stage 3 — creating a film (F-M513)](#implemented-in-stage-3-creating-a-film-f-m513)
- [Live proof 16.11.2026 — the answer to `add-movie.php` (F-M515)](#live-proof-16112026-the-answer-to-add-moviephp-f-m515)
- [F-M517 — the number survives without a media hash too](#f-m517-the-number-survives-without-a-media-hash-too)
- [What a client will need here (seam: stage 2)](#what-a-client-will-need-here-seam-stage-2)

## Assignment

**The rule: TMDb decides which source is asked — not the file name, not the
folder name, not the library name.**

**And that within ONE source.** At the top level there are **two** sources: SubDL and the
**tvst family**. The family has **two addresses** — tvsubtitles.net for series, moviesubtitles.org
for films —, and this site is the film address of it. The order setting ranks the
family against SubDL („tvst first" / „SubDL first"), not the addresses against each other; it stays
two-valued for that reason, and for a film „tvst first" means **moviesubtitles.org first**.

| TMDb record | Source |
|---|---|
| `movie` | **moviesubtitles.org** — and only that one; tvsubtitles.net is not asked for films |
| `tv` | **tvsubtitles.net** — and only that one; moviesubtitles.org is not asked for series |
| both | **SubDL** stays responsible for both; SubDL does not distinguish |

The reason the resolution has to run through TMDb and not through the name: **the same name
exists on both sites and means different works there.** `Dune` is on moviesubtitles.org
the film from 1984 (`movie-607.html`), in the series catalogue of tvsubtitles.net the television miniseries from
2000 (`Dune`, next to it `Children of Dune` and `Dune: Prophecy`). From „Dune" alone no source follows.

**How one can tell for sure on the film page that a hit is a film** (everything measured, not
assumed):

- **There are no series addresses.** `/tvshow-512.html` and `/episode-31911.html` — the addresses that
  designate a series and an episode on tvsubtitles.net — answer here with **302 to `/`**.
- **No series stands in the list.** `add.php?filter=` returns 0 hits for „game of thrones",
  „the office", „the simpsons", „chernobyl", „stranger things", „peaky blinders", „succession",
  „better call saul", „breaking bad s01". „breaking bad" only hits `El Camino: A Breaking Bad
  Movie (2019)` — a film.
- **Every row carries a four-digit year and nothing else.** No season field, no
  episode field, nowhere on the site — not in the upload form either (`tvid`, `lang`, `rip`,
  `release`, `author`, `UFile`…, `comments`).
- **The site itself says „movie".** The `<title>` of a film page ends in `… movie`, the heading
  reads `Download <Titel> (<Jahr>) subtitles`. On tvsubtitles.net the same place carries
  `… season 9` or `… episode 2x24` — so the two sister sites already differ in the
  page title.
- **The self-promotion does not count as proof.** „movies and TV films" on the home page means
  television films; a series offering cannot be derived from it and was not found either.

What the site does **not** deliver with that: it does not say *which* film is meant when title and
year are ambiguous (see „Ambiguity" below). The film id is unambiguous, the name is not.

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

Example, carried through completely on 15.11.2026:

```
search.php?q=oppenheimer      →  movie-24811.html      „Oppenheimer (2023)"
movie-24811.html              →  subtitle-132989.html  „english … (WEB-DL.MA)"
subtitle-132989.html          →  download-132989.html
download-132989.html          →  302 files/Oppenheimer_2023.WEB-DL.MA.en.zip
files/…zip                    →  92.522 B, one entry: Oppenheimer.WEB-DL.MA.en.srt (246.829 B, UTF-8 with BOM, CRLF)
```

**What hurts on this path, and what follows from it:**

- **`search.php` answers with 500.** On all seven checked fetches (five search terms, GET
  and POST, with and without referer), with and without hits. The body is still the correct
  hit list; it only breaks off behind the film list — there, where the secondary subtitle list begins
  (`</ul><br><ul style="margin-left:2em">` and then nothing). The status code is therefore **not**
  usable here as an error sign, the content is.
- **Titles stand inverted.** The site stores `Matrix, The (1999)`, `Lord of the Rings: The Two
  Towers, The (2002)`, `Old Man and the Sea, The (1999)` — 52 of 492 O films end in `, The/A/An`.
  `filter=The Matrix` therefore returns **0 hits**, `filter=Matrix, The` exactly one. A resolver
  must try both forms (or fall back to the core without the article: `filter=Matrix` → 5
  hits, then check year and name).
- **The filter is a pure substring, anywhere in the title.** `filter=LOTR` hits `Allotria
  (Allegria)`; `filter=dark` hits 162 films; an appended year does not help (`Oppenheimer 2023`
  → 0 hits). Case does not count (`oppenheimer`, `dune`, `matrix` each hit).
  There is **no** upper limit: `filter=the` returns 6.674 rows in one response (548 KB).
- **`tvid` and the film id are the same number.** `movie-<id>.html`, `add.php?tvid=<id>` and the
  way back from the subtitle page (`Back to <Film>` → `movie-<id>.html`) carry it; it is global
  and stable (Oppenheimer 24811, Matrix, The 4, Ocean's Thirteen 239). There is no second
  identifier and no season/episode level.
- **The download address answers with 302, not with bytes.** `Location` is **relative**
  (`files/…zip`) and can carry parentheses — measured: `files/Matrix_The_1999.ru(2).zip`, because the
  name already existed. A client must resolve the address and take the parentheses along.
- **Unknown ids look like success.** `download-999999.html` → 302 to `files/` (empty, no
  file name) — that is the error signature. `subtitle-999999.html` → **HTTP 200** with empty title,
  empty language and „size: 9140 kb"; that is not an answer, that is junk. Both must be checked
  before an entry in the plugin database comes out of it.
- **The zip contains 1 to 3 files, and `parts` is only a hint.** `parts`=3 (entry 9077)
  → three files `Matrix The.DVDRip.KiSS.part1/2/3.en.srt`; `parts`=2 (entry 2058, Russian)
  → **one** file `Matrix The (1999).ru.srt`. A complete film subtitle is therefore not
  automatically „the file in the zip": with several parts they are segments that only joined together
  make a film — and each segment carries its own timings.
- **The file name in the zip follows no fixed pattern.** Both forms occurred:
  `<Titel>.<Rip>.<Release>.<lang>.srt` (Oppenheimer) and `<Titel> (<Jahr>).<lang>.srt`
  (Matrix, Russian). The name in the zip is **not** identical to the name of the zip.
- **The year can stand twice in the heading.** `movie-16856.html` is called „O Beautiful (2002)
  (2002)", because the title already contained the year. Whoever reads the year from the heading
  takes the **last** bracket with four digits, not „everything in the last bracket".
- **A film without subtitles is no 404.** `movie-6928.html` (Old Man and the Sea, The, 1999)
  answers 200 with „0 subtitles downloaded  times" — two spaces where the number otherwise stands.
  Films without subtitles are in the upload list, but **not** in the film list (492 vs. 487 for
  the letter O, 1.407 vs. 1.395 for A): `movies-<L>.html` shows only films with at least one
  entry, `add.php?let=<L>` all of them.

**Ambiguity.** There is no search by year, no requested-id parameter and no
disambiguation page. `filter=dune` gives five films: three „Dune" adaptations (1984, 2021, 2024),
the documentary `Jodorowsky's Dune` (2013) and `Suna no onna (Woman of the Dunes)` (1964). The
decision therefore falls in the client: collect candidates, compare the year from the row next to the link,
and when two candidates carry the same year, compare the title narrowly (not
similarly) and otherwise **not** guess. Exactly the rule that the series path already
applies.

## The path (upload)

`GET /add.php?tvid=<id>` shows the form for exactly this film; `POST /add.php`,
`enctype="multipart/form-data"`, the target is `add.php` (relative, so `/add.php`). The fields are
literally those of the form:

| Field | Type | Content |
|---|---|---|
| `tvid` | hidden | film id; already stands in the form (`24811`) |
| `lang` | select | the site's language code, 13 values (see table) |
| `rip` | select | `DVDRip BluRay BrRip BDRip HDRip WEB WEBRip WEB-DL` or empty |
| `release` | text | free text (release group), e.g. `MA`, `YTS`, `KiSS` |
| `author` | text | free text — **mandatory or not: unchecked**, see below |
| `UFile` | file | file 1 |
| `UFile2` | file | „only if 2 and more CD" |
| `UFile3` | file | „only if 3 CD" |
| `comments` | textarea | free text |
| `go` | hidden | `1` |

**No `accept=` on the file fields, no `MAX_FILE_SIZE`, no token, no captcha, no
forced referer detectable.** The form is a bare multipart form — unlike
tvsubtitles.net even the `hi` marker for hearing-impaired versions is missing here.

**What the form reveals about acceptance: nothing.** There is no hint about permitted extensions,
no size specification, no moderation text. The three file fields are declared as „CD" parts
(„only if 2 and more CD") — the site therefore thinks in parts, as the `parts` column of the entries
shows. Whether `UFile` alone suffices, whether `.srt` and `.zip` both work and whether `author`/`comments` are
mandatory can be clarified **only by a real upload**, and that was
excluded for this task.

**Create a new film:** `GET /add-movie.php` → form to `/add-movie.php` (POST, multipart) with
`name`, `year` (`maxlength=4`), `go=1`. **Built since F-M513** (stage 3), **proven live since F-M515**
(16.11.2026): the client builds exactly this payload, and it reads the answer at two proven
markers — see the section „Live proof 16.11.2026" further below.
The site warns itself: „Before adding new Movie please
check if it already exist" — that is a hint, **no** technical duplicate protection; whether the
site refuses a second entry with the same name and year is unchecked. The same reservation
as with tvsubtitles.net: resolve first, then create, and only if the search answered cleanly.

**How one finds the `tvid` of a Jellyfin film** — proven on the example „Oppenheimer (2023)":

```
GET /add.php?filter=oppenheimer
  → <li><a href="add.php?tvid=24811"><b>Oppenheimer</b></a> 2023</li>      (exactly one hit)
GET /add.php?tvid=24811
  → <b>Movie:</b> Oppenheimer (2023)   … form
```

Counter-check „The Matrix" (1999): `filter=The Matrix` → 0 hits; `filter=Matrix` → 5 hits, of which
`Matrix, The (1999)` = `tvid=4`. The path therefore leads over the **core** of the title and then over the
year comparison, not over the full TMDb title.

## Tables

### Language — the 13 values of `<select name="lang">`

Wording of the site, in the order of the form. There are **exactly 13** — the selection list is
shorter than that of tvsubtitles.net, not longer, and it contains no `select`-`multiple`: one
language per upload.

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

**These 13 codes stand in the tvsubtitles list in identical spelling** (there it is 23).
The bridge `TvSubtitlesClient.SiteLanguage` (F-M502) therefore covers them completely — also the
deviations that are no ISO: `gr` instead of `el`, `ua` instead of `uk`, `br` for Brazilian
Portuguese. What is missing here and exists there (`nl pt sv da fi ko cn jp bg cz ro`) is **not**
offered; a target code outside these 13 has no language on this site and must not
be uploaded. The number in the last column is the site's self-report (`add.php`,
block „Site statistics"), not counted by us.

The section headings of the film page are the display names of the same list, only with two
spaces before `subtitles:` — `English`, `Portugese(br)` (written like that), `Ukrainian` etc. They
appear **only for languages in which the film has entries**; `movie-4.html` has 12 sections
(no Ukrainian), `movie-239.html` all 13.

### Rip — the values of `<select name="rip">`

`(leer)` — in the form **twice**, as first and as last entry —, `DVDRip`, `BluRay`,
`BrRip`, `BDRip`, `HDRip`, `WEB`, `WEBRip`, `WEB-DL`. Nine values, plus the empty case.

**This is not the list of tvsubtitles.net** (`WEB HDTV HEVC DVDRip DVDSCR BDRip BluRay`).
Common are only `WEB`, `DVDRip`, `BDRip`, `BluRay`. A rip table must therefore be kept per source
— `HDRip`, `BrRip`, `WEBRip`, `WEB-DL` do not exist there, `HDTV`, `HEVC`, `DVDSCR` not here.
And: the selection list is **only a recommendation for new uploads**. Existing entries
carry free values next to it — on `movie-4.html` stand `1080p`, `720p`, `1080p.BlueRay`,
`720p.BlueRay`, `BrRip`, `BDRip`, `DVDRip` and empty. Whoever reads must therefore expect everything; whoever
writes sticks to the nine.

### Season/episode

There is none. The site knows **only** `tvid`. No field, no column, no address.

### Columns of an entry on the film page

Per entry (`<div class="subtitle">`): flag (`images/flags/<code>.gif`, `alt` = code), link
`/subtitle-<id>.html`, title `<Titel> <Jahr> <sprache> subtitles (<Rip>.<Release>)`, rating
(red/green), and a table with rip, upload date (`07.08.2024 02:48`), size (`90.35 kb`),
release, `parts` (1–3), downloads. **The rip therefore stands as its own column in the list**, the release
as well — but the release is often empty, while it stands in parentheses in the title. Reliable is the
title: it carries `(<Rip>.<Release>)` completely, if need be as `()`.

**The language stands there three times** — section heading, flag, word in the title — and in all 108
checked entries (`movie-4`, `movie-239`, `movie-24811`) all three agreed. Unlike
tvsubtitles.net, where the flag alone counts and the block boundary decides the assignment,
there is therefore redundancy here for cross-checking.

## Site peculiarities

- **Character set:** `<meta … charset=utf-8>` and the body is also UTF-8 — **except** individual
  fragments in **cp1251**: the LiveInternet counter (an `escape("Жж"+…)` in the JavaScript) and
  `/admin.php` (Ukrainian). The HTTP header sets **no** `charset` (`Content-Type: text/html`).
  Whoever decodes the whole body strictly as UTF-8 fails on exactly these bytes (position 10.803
  in `subtitle-2058.html`); whoever takes cp1252 breaks all titles instead. The bytes of the
  subtitle files are untouched by that: **UTF-8, CRLF** — with BOM only in the newer file
  (Oppenheimer 2024), without BOM in the three older ones (Matrix 2007/2008).
- **Redirects:** `http://` → 301 → `https://www.`; `moviesubtitles.org` without `www` → 301 →
  `https://www.moviesubtitles.org/`. `/movies.html` is **no** redirect, but the film list
  (569 KB for A) — the earlier note „redirects to the home page" could not be reproduced on 15.11.2026;
  with a browser user agent came 200 and 569.197 bytes of content. `/tvshow-…`, `/episode-…`,
  `/login.php`, `/panel.php`, `/upload.php`, `/sitemap.xml`, `/rss.xml`, `/api.php` → 302 to `/`.
- **`robots.txt`: HTTP 200, 0 bytes.** No rule, no prohibition — and therefore also no permission
  to pull more than necessary.
- **Throttling:** not observed, but also not excluded. Six requests to the same
  film page in a row: all 200, 0,57–0,88 s. The site is slow and delivers uncompressed
  (a letter list 200–570 KB), that is the actual cost factor. For the client the
  general pause of the plugin applies (`MinCallPauseSec`).
- **No login, no account.** There is nothing to log into and consequently also no „my uploads"
  page. What was uploaded is read **back from the film page** (`movie-<id>.html`) or
  at most on `/new.html` (latest subtitles) — there is no further read-back option.
- **Without a browser user agent** the home page and film page delivered **the same bytes** as with
  (`19401` to `19401`, `104870` to `104870`; the only difference was a two-digit counter
  deep inside a script). The site therefore does not check the `User-Agent`. The task nevertheless required
  the browser user agent, and it was sent on every fetch.
- **Advertising:** several third-party scripts (`cognatesyringe.com`, `acint.net`, LiveInternet) in the middle of the
  content. A parser may rely on **no** position in the document, only on classes and
  the table structure.

## What is NOT proven

Everything here is **unchecked**, because a write access would have been necessary for it — and that was
excluded for this task.

- **Whether `add.php` accepts at all.** There is no hint of moderation, no thank-you text, no
  captcha — but also no proof that an upload without login goes through. With tvsubtitles.net
  the suspicion reads „accepts, perhaps moderates"; here not even that is checked.
- **What the answer to an upload looks like.** Whether HTTP 200 with text, whether a redirect, whether
  a new subtitle id comes back (with tvst none comes), whether there is a confirmation text like
  „Thank you. Your subtitles will be placed on site very soon." — **unknown**.
- **Whether `UFile` alone suffices**, whether `UFile2`/`UFile3` may be filled only for multi-part films,
  and whether an upload with three files appears as one entry with `parts`=3. The `parts` column
  suggests it; it is not proven.
- **Which extensions the site takes.** The form says nothing (no `accept=`). `.srt` and `.zip`
  are proven for the *downloadable* files, not for uploading.
- **Whether `author` and `comments` are mandatory** and what happens when they are missing. No `required`,
  no asterisk — one knows more only after a POST.
- **Whether the file name of the upload is taken over.** The files in the zip are named
  `<Titel>.<Rip>.<Release>.<lang>.srt` or `<Titel> (<Jahr>).<lang>.srt`, so **built server-side from
  title, rip, release and language**. It looks as if the own file name
  is discarded — that is not proven.
- **Whether the site prevents duplicate entries** (same film, same language, same rip, same
  release). `add-movie.php` warns only about creating a duplicate *film*; for subtitles there
  is no visible hint.
- **Whether the 500 on `search.php` is permanent** or a broken installation that changes with an
  update. It was there on 15.11.2026 with every query.
- **How the site reacts to umlauts and diacritics** (`Amélie` vs `Amelie`) — only
  case is proven to be independent, not accent folding.
- **Whether there is a rate limit over a longer time.** Six requests are no measurement.

## Implemented in stage 1

**`Jellyfin.Plugin.SubdlSync/Api/MovieSubtitlesClient.cs`** — the client of the third source, build like
`TvSubtitlesClient`: all parsers `static` and without network, plus the async fetches. Checked by the suite
**`scripts/tests/moviesubtitles/`** (`MovieSubtitlesRun.cs`, meanwhile 84 checks against the fixtures in
`fixtures/`, `--live` only reads), bound as **T228** and described as **F-M506** in
`docs/REQUIREMENTS.md`. **Addendum F-M510/T232:** the same suite has since also checked the TMDb title-year
comparison (bracket year split off, only for the comparison) — 9 checks more, **93** in total.

Included are: the two own tables (13 languages, nine rip values), the language bridge
`SiteLanguage` (refuses what the 13 do not know — including `pt`/`por`, because the site carries only the
Brazilian version), `RipFromQuality` (source before codec, **no** codec fallback), `ParseMovieList`
for all three list views, `ResolveMovie`/`TitleKey` (article inversion, year, no guessing),
`ParseMoviePage` (language from the flag), `ParseSubtitlePage`, `ParseDownloadTarget`/`DownloadUrl`,
`DownloadAsync` (302, bytes decide, multi-part marked) as well as `BuildUpload`,
`BuildUploadBody` and `UploadAsync` (dry run as default, `dryRun` comes from the caller).

**Not included and explicitly stage 2:** any wiring — no configuration field, no
plugin database column, no endpoint, no entry in the source catalogue. `BuildAddMovie` and the creation came only
with F-M513 (stage 3). The site was **not** accessed in a writing way.

**Two findings from the build that sharpen the protocol above:**

- **The rip selection field carries eight named values, not nine** — the empty entry stands twice
  (first and last option), so nine *different* values result. The table in the client and
  the check in the suite count the empty one in; the line „Nine values, plus the empty case" above
  counts it twice.
- **`x265` yields the empty rip here, not `HEVC`.** `HEVC` is a value of tvsubtitles.net; this
  selection field knows no codec. The source in the same name still applies (`BluRay.x265` → `BluRay`),
  a codec alone stays empty — the site then stores no rip, and that is a permitted value.

## Implemented in stage 2b — the upload path (locked)

**The whole path is built, it is not live.** Operator requirement 15.11.2026: „Before we upload
wrong languages, we upload nothing." The path is therefore built, checked and shipped **locked**;
it is released **only after proven language mapping**.

### What is built

| Piece | Where | What it does |
|---|---|---|
| Endpoint | `Api/Endpoints/MovieSubtitlesEndpoints.cs`, `MovieSubtitlesUploadEndpoint` | `IUploadEndpoint`, `Provider = MovieSubtitles`, `Wired = true`. Takes the ticket, refuses season/episode and season pack, checks the language against the 13 codes, takes `MsubMovieId` from the ticket or resolves over title **and** year |
| Payload | `MovieSubtitlesClient.BuildUpload` (already in stage 1) | fields in the order of the form: `tvid lang rip release`, `author` **only when set**, `UFile` (`UFile2`/`UFile3` only for multi-part), `comments`, `go=1`. `release` is the release **group** from the release name, never the file name |
| Body | `MovieSubtitlesClient.BuildUploadBody` | multipart with its own boundary; the bytes of the file stand unchanged in it |
| Sending | `MovieSubtitlesClient.UploadAsync(upload, dryRun)` | dry run is the **default**: the same body, not a byte on the wire |
| Resolution | `MsubMovieResolver` (stage 2a) | the stored number leads; otherwise a narrow search over TMDb title and TMDb year; the number lands in `MediaEntity.MsubMovieId` |
| Chain | `Pipeline/UploadPipeline.cs` | **one** loop over the responsible sources (series: tvsubtitles.net, film: moviesubtitles.org — each next to SubDL), per source its own switch, its own gate, its own sending, its own result, its **own mark** and **own timestamp**. The old build „SubDL, otherwise everything else" is gone — it would have sent a film to the series address |
| Gate | `Api/Endpoints/UploadGate.cs` | `Decide(...)` knows the film address (`MovieSubtitles`, `AllowedCount`), `May(provider)` is the single read point of the chain |
| Plugin database | `Registry/ContentHashRegistry.cs` | `IsUploadedTo`/`IsContentUploadedFor` read the columns of the film address, `RecordSkippedContent` writes its timestamp, `MarkEmbed`/`MarkSidecar` take `moviesubtitlesUploadId`/`moviesubtitlesUploadedAt` |
| Switch | `Configuration/PluginConfiguration.cs` | `MovieSubtitlesDownloadEnabled` (null → **on**), `MovieSubtitlesUploadEnabled` (null → **off**) |
| GUI | `Configuration/configPage.html` | the tvst family stands as **one source with two addresses**: a shared heading, below it a pair of switches per site (download/upload). The selection field of the order stays **two-valued** (SubDL first / tvst first) |

### What the endpoint does on an answer without an id

What the site says on acceptance is **not proven** (see the section „What is NOT proven"). The
endpoint therefore invents neither an id nor a moderation:

- **HTTP 2xx** → `Ok = true`, `Id = null`, the text of the answer passes **unchanged**
  (only tags/whitespace collapsed). It counts as „**accepted, id unknown**". The chain then assigns
  its own identifier `own:moviesubtitles:<Zeit>:<hex>` (F-M488) — „uploaded, id unknown"
  is provable, „id 12345" would be an assertion.
- **no 2xx** → `Ok = false`, `Reason = "HTTP <Status>"`, the text of the site goes along. A rejection
  that we invent would be as wrong as an acceptance that we invent.
- **dry run** → `Ok = true`, `DryRun = true`, `Id = null`, `Answer = null`, but `Bytes` = the size
  of the body that **would** be sent.

### The lock

- `MovieSubtitlesUploadEnabled` is `bool?` and **off** in the unset case; `IsUploadEnabled` reads that
  as the single place.
- If the switch is off, a line stands in the run: `source moviesubtitles skipped: that source is
  switched off, nothing sent` (in the dry run: `[SubDL] DRY-RUN … — source moviesubtitles not sent:
  that source is switched off`). The source therefore does not go missing silently.
- **No POST against the site, not even as a test.** The endpoint is exercised exclusively against fixtures
  and a counting handler; no film was created, no login was made anywhere.

### What is still not proven now either

The lock stays until **the language mapping** is proven against the real site — not only the
table, but the effect: that an upload with the site's code appears as that language.
**Proof 16.11.2026:** the film upload (Paddington 2, film 23716, entry 133347) stands publicly on
the site, with `Rip WEB` and the comment `IMDb tt4468740 / Sub Scribe 12.1.12.397` — for EN the
effect is thereby shown. The lock itself remains the switch: it is still off in the unset case.
Still unproven remain: whether `add.php` accepts at all without login, what the answer text on
acceptance reads, whether moderation happens, whether `author`/`comments` are mandatory, which extensions the site
takes, whether the file name is taken over and whether duplicate entries are prevented. **Release only after proven
language mapping.**

## Implemented in stage 3 — creating a film (F-M513)

**Operator order 16.11.2026 (verbatim): „Then test once with creating new".** A film that the site
does not carry is created on upload and the subtitle for it uploaded. Built and checked offline
is the path; **no POST against the site was made in this task** — the
live test runs that.

### What is built

| Piece | Where | What it does |
|---|---|---|
| Payload | `MovieSubtitlesClient.BuildAddMovie(name, year)` | fields `name`, `year`, `go=1` in the order of the form; **check before sending**: empty name or year outside 1888..current year+3 → `ArgumentException`, no request |
| Body | `MovieSubtitlesClient.BuildAddMovieBody` | multipart with its own boundary — dry run and real case build **the same** body |
| Sending + reading back | `MovieSubtitlesClient.AddMovieAsync(payload, dryRun)` | `POST /add-movie.php` (referer: the form); on 2xx the id is **looked up**, not guessed |
| Duplicate protection | `MovieSubtitlesClient.CheckForAddAsync` / `ResolveForAdd` | **one** search round (title, title without article, inverted form) decides whether creation is allowed |
| Creation in the upload path | `MsubMovieResolver.ResolveOrCreateForMediaAsync` | the only version that may create; the download version (`ResolveForMediaAsync`) never creates |
| Chain | `Pipeline/UploadPipeline.cs` | calls the creating version **only** in the film branch, so only with the source's upload switch on; the dry run builds and reports, sends nothing |
| Plugin database | `MsubMovieId` | the new `tvid` goes **immediately** in through the same method (`Persist`) as a found number |

**Checks:** the suite `scripts/tests/moviesubtitles` now counts **123** — 84 from stage 1, 9 from
F-M510 (T232), 5 from F-M511 (T233), 15 from F-M513 (T235), 6 from F-M515 (T237) and **4 from F-M517
(T239)**. It runs exclusively against fixtures, counting handlers and since F-M517 a throwaway plugin database in the
temp directory; `--live` still only reads.

### How the duplicate protection decides

Three cases, and only these — checked **once**, before creating:

1. **Title with the same year present** (exactly one row) → take this number, **create nothing**.
2. **Title present, but a different year** (or year unknown, or more than one row) → **not**
   create and **not** upload; the log line names the found year
   (`… die Seite führt den Titel schon — gefundenes Jahr 2019, 1 Treffer; nichts angelegt, kein
   Upload`). The operator wants no duplicates — so rather no upload than a second entry.
3. **No hit, but at least one readable answer** → create.

If the search answers **not readable at all**, „does not exist" does not apply: then nothing is
created either (`unclear`). The decision (`take`/`refuse`/`create`/`unclear`, found year,
hit count) stands in the result object, not only in the log.

### How the new id is read — and what „id not readable" means

What the site **answers to the POST is unproven**. Therefore the answer is not interpreted: after
a 2xx the same narrow search runs once more, and the number comes from its hit list. If
the follow-up search does not name the film, the result reads **`angelegt, Id nicht lesbar`** — with the text of the
answer next to it, without number and without upload (without `tvid` there is no form). An HTTP error is
`Ok = false` with the status as reason. The log lines:

```
[moviesubtitles] 'Paddington in Peru' (2026) newly created — id 4711
[moviesubtitles] 'Dune' (2026): the site already carries this title — found year 2021, 1 hit; nothing created, no upload (F-M513)
[moviesubtitles] 'X' (2026) was created, but the follow-up search named no id — nothing stored, no upload (F-M513)
[moviesubtitles] DRY-RUN: 'X' (2026) would be created — 341 bytes of form body, nothing sent (F-M513)
```

### What still remains unproven — and the assumption in the code

- **The answer to `POST /add-movie.php`** — **proven since F-M515** (16.11.2026, see the
  live proof below): `Movie has been added. Thank you.` resp. `This Movie already exist`. A third
  sentence (e.g. „year invalid") is **not** proven; the client therefore treats it as a rejection.
- **The assumption on which the read-back stands:** that the site carries an accepted film **immediately** and
  under **the same title and year** in its search. **Disproven live** (16.11.2026): the site
  created the film and carried it nowhere — search, letter list and `add.php?filter=` stayed
  empty. The client therefore reports „created, id not readable" and uploads nothing.
- **Whether the site technically refuses a duplicate** — **proven**: it answers `This Movie already exist`
  and does not create a second time. But the sentence also comes for a film that it carries nowhere else.
- **Whether a created film immediately has entries/pages** (so `movie-<id>.html` answers) — the site
  answers to `movie-26230.html` with the year `(1994)` and an **empty name**; whether that is the created
  film is **not proven by title**.

## Live proof 16.11.2026 — the answer to `add-movie.php` (F-M515)

Recorded with `curl`, browser UA, `Referer: /add-movie.php`, no `-L`. The answers lie
unchanged as fixtures: `add-movie-added.html` (success), `add-movie-exists.html` (refused);
`add-movie.html` (8399 bytes) is the form itself.

| Case | Request | Answer |
|---|---|---|
| Form | `GET /add-movie.php` | **200, 8399 bytes**; `<form action="/add-movie.php" method="post" enctype="multipart/form-data">`, fields `name`, `year` (`maxlength=4`), `go=1`, submit `Send` |
| wrong path | `POST`, `application/x-www-form-urlencoded`, `go=Add movie` | 200, **the same form**, no message, no entry — **no** creation |
| success | `POST`, multipart, `name=The Little Rascals`, `year=1994`, `go=1` | **200, 8467 bytes**, `<div class="subtitle2"><b>Movie has been added. Thank you.</b></div>`, **no `Location`**, no id |
| refused | the same POST a second time | **200, 8459 bytes**, `<div class="subtitle2"><b>This Movie already exist</b></div>` |

**The chosen film** was `The Little Rascals (1994)` — previously proven missing: `POST search.php`
with `q=Little Rascals` and `q=Rascals` → 0 hits, `movies-L.html` and `movies-R.html` without
„Rascal", `add.php?filter=Rascals` → 0 rows; on the machine it lies as
`<library>/The.Little.Rascals.1994/The.Little.Rascals.1994.1080p.BluRay.x265-RARBG.mp4`
(1379629713 bytes).

**And after that?** The follow-up search did **not** find it (`q=Little Rascals` → 0), `movies-L.html` stayed
byte-identical (478353 bytes, only the „Page generation" time changed), `add.php?filter=Rascals`
stayed at 0 rows, the statistics at `Movies: 23368`, and `movie-26231.html` and following
do not exist — the newest page `movie-26230.html` carries `(1994)` with an **empty** name. The
duplicate protection of the site therefore sees the film (second POST → „already exist"), the public
lists do not. **An id is thereby not proven by title** — the client claims none and uploads nothing
up. The same picture with the film `Project Hail Mary (2026)`: „This Movie already exist", but neither in
`search.php` nor in `movies-P.html` (443 KB) nor in `add.php?filter=`.

**What that means for the chain:** creation succeeds (message proven), but the subtitle can
**not** follow as long as no `tvid` is readable. The client says that and does nothing further; a
second creation attempt never falls.

## F-M517 — the number survives without a media hash too

**Proof (real run 16.11.2026, 04:13, jf-test, `jf-run.log`):**

```
UploadPipeline: [moviesubtitles] ?: 'Hotel Transylvania 2' (2015) is film 13392 — not stored:
no media hash known — the number applies only for this run.
```

The upload then went through — only the **number** was gone.

**Why.** The media hash comes from `ContentHashRegistry.GetMediaHash(mediaPath)`; the method
answers `null` as soon as the file is not readable. Exactly that was the case here: the path
`media/movies-test/Hotel Transylvania 2 (2015)/Hotel Transylvania 2 (2015).mkv` is a **symlink without
target** (`readlink -f` → `<library>/…ShAaNiG.mkv`, `test -e` → not present) — so no hash,
although the row would be addressable over `Path`. `MsubMovieResolver.Persist` returned
**silently** (the column `MsubMovieId` was never reached), and the log line said that as
„not stored". The same path also wrote the series number (`TvstShowId`) — the series case had
the same hole.

**What applies now.**

| Piece | Where | What it does |
|---|---|---|
| Write path | `ContentHashRegistry.SetMsubMovieId` / `SetTvstShowId` (a shared `SetMediaNumber`) | hash first (row by `Id`); otherwise the row over its **path** (`MediaEntity.Path`) — the same column, only a different address |
| Uniqueness | the same method | no hash **and** no path row → `None` (nothing written); **several** rows with the path → also `None` (no guessing, no choice) |
| Read path | `MsubMovieIdAtPath` / `TvstShowIdAtPath` | without a hash the stored number is **read** over the path — otherwise it would be invisible to every follow-up run |
| Resolver | `MsubMovieResolver` / `TvstShowLookup` (new parameter `mediaPath`) | passes the path through; `Persist` returns **how** it was written |
| Log line | `Api/Endpoints/MediaNumberNote` | `gespeichert (über den Medien-Hash).` / `gespeichert über den Pfad (kein Medien-Hash bekannt).` / `nicht gespeichert: kein Medien-Hash und keine eindeutige Pfad-Zeile im Store — die Nummer gilt nur für diesen Lauf.` |
| Chain | `UploadPipeline` (film and series address), `DownloadPipeline` | pass the path of the media file along |

**Proof on the plugin database image** (copy of the live plugin database, `<scratch>/fm517-proof/store`, only the
copy written):

| Step | Result |
|---|---|
| copy unchanged | **31** media rows, **0** rows with the path of the film — this file has no row there at all (without a hash none was ever created), the write attempt yields `None`, 31 → **31** |
| row present, hash not (the described case) | `ByPath`, the row then carries `MsubMovieId = 13392`, rows **32 → 32** (the +1 came from creating the row for the proof, not from the write) |
| the same write once more | `ByPath`, rows **32 → 32** (idempotent) |
| reading | over the path **13392**, over the hash **13392** |
| resolver run for real (without hash, row present) | status `found`, id 13392, rows 32 → 32, log line verbatim: `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — gespeichert über den Pfad (kein Medien-Hash bekannt).` |

**Honestly about it:** for *this* film it stays at „not stored" on the test instance as long as
no row with its path exists — the symlink points into nothing, so no hash arises either. The
fix takes effect where the row is there (from an earlier run, from the enqueuer, from another
source) and only the hash is missing; without both the log line stays true.

## What a client will need here (seam: stage 2)

So that the seam `IDownloadEndpoint`/`IUploadEndpoint` fits here exactly as with
`TvSubtitlesEndpoints`, these are the pieces. **After stage 2a/2b/3 this applies: the download is wired
(F-M507), the upload built (F-M508), the creation built (F-M513) — see the sections above.
Stage 1 built the methods, stage 2 the seam — endpoint, configuration fields, plugin database column,
source catalogue —, stage 3 the creation:**

| Piece | Purpose |
|---|---|
| `SearchAsync(query)` | `search.php?q=` **and** `add.php?filter=`; do not rate 500 as an error, parse the body |
| `ResolveMovie(candidates, title, year)` | core title + year, take article inversion (`Matrix, The`) into account, compare narrowly, otherwise `null` |
| `MoviesAsync(letter)` | `movies-<L>.html` → `id, Name, Jahr, Sprachen` (only films with entries) |
| `EntriesAsync(movieId)` | `movie-<id>.html` → `SubtitleId, Language, Rip, Release, Parts, Age`, section by section per language |
| `DownloadAsync(subtitleId)` | `download-<id>.html` → evaluate 302, fetch `files/…zip`, check `PK\x03\x04`, return all `.srt` |
| `BuildUpload(...)` | fields `tvid lang rip release author UFile UFile2 UFile3 comments go` |
| `BuildAddMovie(name, year)` | `add-movie.php`; resolve first, then create — built with F-M513 |
| Language bridge | the 13 codes; reuse what `SiteLanguage` already knows (subset) |
| Rip table | its own, nine values, **not** those of tvst |

The client must also know that **a film with `parts`>1** delivers **several files**: that is
no pack, that is a cut film, and the segments are not interchangeable.
