# moviesubtitles.org — test suite

Here is the record of the third source: `docs/MOVIESUBTITLES.md` (URL chain, forms,
value lists, open points) and the pages the site actually delivered on 15.11.2026.

**The code for it is `Jellyfin.Plugin.SubdlSync/Api/MovieSubtitlesClient.cs` (F-M506), run by
`MovieSubtitlesRun.cs` in this directory: 93 checks offline against `fixtures/`, `--live` as a
read probe.** Call:

```
dotnet run --project scripts/tests/moviesubtitles/moviesubtitles.csproj -c Release -- <repo>
```

**State: 15.11.2026, recorded read-only throughout.** No upload was made, no movie
created, no POST sent to `add.php` or `add-movie.php`. Everything that stands in
`docs/MOVIESUBTITLES.md` under "What is NOT proven" is therefore unverified — not
"probably so".

## `fixtures/` — real pages, unchanged

Raw bytes as they arrived: not rewritten, not repaired, not re-encoded. That is why the
cp1251 fragments (LiveInternet counter, `admin.php`) in the files are still broken, exactly as on
the site. Fetched on 15.11.2026 between 20:18 and 20:26 UTC with
`User-Agent: Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36`.
`fixtures/MANIFEST.sha256` holds the checksums (`sha256sum -c MANIFEST.sha256` must pass).

| File | Source (GET) | HTTP | Bytes | What hangs on it |
|---|---|---|---|---|
| `home.html` | `/` | 200 | 19.401 | home page; "Latest subtitles" with flag + rip in the link; charset declaration in `<meta>` |
| `movies-O.html` | `/movies-O.html` | 200 | 200.017 | **movie list**, letter O, 487 movies, per row name/year/downloads/flags |
| — *(measured only)* | `/movies-0.html` | 200 | 177.831 | letter "#"; size measured, content not kept as a fixture |
| `add-let-O.html` | `/add.php?let=O` | 200 | 45.377 | the same list in the **upload** view: 492 movies, `add.php?tvid=<id>` + year |
| `add-filter-oppenheimer.html` | `/add.php?filter=oppenheimer` | 200 | 9.640 | filter search (GET!) — exactly one hit, field stays filled |
| — *(measured only)* | `/add.html` | 200 | 113.143 | letter A of the same upload list, 1.407 movies; without `let`/`filter` A is the default |
| `add-tvid-24811.html` | `/add.php?tvid=24811` | 200 | 10.191 | **upload form** for Oppenheimer (2023): `lang`, `rip` selection, `UFile`/`UFile2`/`UFile3` |
| `add-tvid-unknown.html` | `/add.php?tvid=99999999` | 200 | 10.188 | the same form for an id that does not exist: "Movie:  ()" — no error message |
| `add-movie.html` | `/add-movie.php` | 200 | 8.399 | form "Add Movie": `name`, `year`, `go` |
| `movie-4.html` | `/movie-4.html` | 200 | 104.870 | **movie page** "Matrix, The (1999)": 54 entries, 12 language sections, `parts` 1/2/3, rip free (`1080p`, `720p.BluRay` …) |
| `movie-239.html` | `/movie-239.html` | 200 | 100.247 | "Ocean's Thirteen (2007)": 51 entries, **all 13** language sections incl. Ukrainian |
| `movie-24811.html` | `/movie-24811.html` | 200 | 14.149 | small movie page, Oppenheimer (2023), 3 entries, 2 sections (en, Portugese(br)) |
| `movie-16856.html` | `/movie-16856.html` | 200 | 10.430 | year doubled in the heading: "O Beautiful (2002) (2002)" |
| `movie-6928.html` | `/movie-6928.html` | 200 | 8.458 | movie **without** subtitles: "0 subtitles downloaded  times" (two spaces) |
| `subtitle-132989.html` | `/subtitle-132989.html` | 200 | 11.461 | **download page**: rip, release, `filename:`, size, date, compatible releases, link `download-132989.html` |
| `subtitle-2058.html` | `/subtitle-2058.html` | 200 | 11.254 | Russian entry, `parts`=2 in the list, `filename: Matrix The.part1.ru.srt` |
| `subtitle-unknown.html` | `/subtitle-999999.html` | 200 | 10.848 | id does not exist: **HTTP 200** with empty title, empty language and "size: 9140 kb" — no error |
| `search-dune.html` | `/search.php?q=dune` | **500** | 3.311 | search hit list (5 movies with year); body ends after the movie list in the middle of the markup |
| `download-132989.headers.txt` | `/download-132989.html` | 302 | 374 | the headers with `Location: files/Oppenheimer_2023.WEB-DL.MA.en.zip` |

Not kept: the zips themselves. The bytes are evidence, but no page content; the measurements stand
in `docs/MOVIESUBTITLES.md` (Oppenheimer zip 92.522 B, one `.srt`; Matrix `KiSS` zip 36.385 B, three
`.partN` files). Whoever needs them fetches them again with `download-<id>.html` — that is a GET.

## What these pages are meant to prove

- **Movies, not series.** The movie page has no `/tvshow-…`/`/episode-…` addresses (both 302 to
  `/`), no season/episode notion and no series in the list (`add.php?filter=…` → 0 hits
  for "game of thrones", "the office", "chernobyl", "stranger things", "better call saul").
  See the section "Assignment" in the record.
- **`tvid` is the movie id.** `movie-<id>.html`, `add.php?tvid=<id>` and the way back from the
  subtitle page (`Back to <Film>` → `movie-<id>.html`) carry the same number; one number, global.
- **The download runs over a redirect**, not over JavaScript as with tvst:
  `download-<id>.html` → 302 → `files/…zip`.
- **The language appears three times** (section heading, flag, word in the title) and agrees in
  all 108 checked entries — unlike tvst, where only the flag counts.
