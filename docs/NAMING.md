# The names on disk

A subtitle beside a video is a file, and its name is the only label every reader sees: Jellyfin's file name
reader, the uploader, the downloader's presence check and the database refresh. This paper follows one name
from the writer to the reader.

## The shape of a written name

A sidecar this plugin writes reads `<base>.<lang>[.sdh][.forced].<NN>.srt`. The base is the media name
without extension. The token names the language; `sdh` marks the hearing-impaired file, `forced` the
foreign-scenes one. The slot is two digits, last. One function composes the name and the reader that parses
it sits beside it, so writer and reader agree by construction (SidecarNaming.cs:162-192).

## The language token

The token is ISO 639-2/T in three lower-case letters: `deu`, never `ger` and never `de`. Jellyfin's reader
accepts two letters, both three-letter forms and the display name, so the choice was free and fell on the
form the store speaks (F-M790). The bibliographic form stays an input and is translated on reading
(LanguageMapper.cs:142-156); a code the table does not know comes back unchanged (SidecarNaming.cs:190).

## The slot number

The slot is always written, always two digits (F-M316). Slots 01 to 49 hold corrected files, and 51 to 99
are reserved for the unsynchronized originals a correction keeps, 49 numbers to each block (F-M315); slot
50 is read but not handed out (SidecarNaming.cs:127). Two digits carry the track order: Jellyfin lists
external tracks as their names sort, one digit puts `.10` before `.2`, and a bare name sorts after `.99`
because `s` follows `9`. On 08.10.2026 that put a kept original above its corrected file (175-183).

## Reading a name back

`Parse` splits at the dots and reads backwards: it steps over a one- or two-digit token, takes `sdh` and
`forced` in either order, then resolves the token before them (SidecarNaming.cs:405-443). A name that
yields nothing is no sidecar: the token is not two or three characters, or a marker has no language before
it, and the listing drops it (F-M239). A name without a language token is skipped, not read as English.

## The name inside a pack

Inside a pack the name serves a site: no base name, no slot, but the title identifier, the full MD5 of the
text and the code of the site it goes to, as in `<imdb>.S01E02.<hash>.EN.srt` (SourceContract.cs:999-1009).
It names no library, machine or person (F-M29). The code is the site's own: SubDL writes `CS`, the second
site `cz` (F-M799). Each SubDL member names its season and episode, with the title when Jellyfin knows one,
since SubDL reads both numbers from it (SeasonPack.cs:241-256, F-M378); the second site repeats the member
name a single upload sends (F-M643).

## A rename keeps the content

The seeder names an unlabelled file once its text names a language (F-M278): it hashes the text, moves the
file, then points its row at the new name (ScribeSeeder.cs:1641-1671). The hash is the row key and a rename
leaves the content alone, so key and verdict stand (ContentHashRegistry.cs:1916-1936). The refresh forgets
a row whose path is gone, so a stale path drops a row still there (1926-1929). The planner gives out the
first free slot up to 49 from the names on disk, because writes overwrite (DownloadPipeline.cs:5448-5452).

## Two writers, one shape

A name is a contract between one writer and four readers, and a disagreement never shows on disk: the file
stays right and readers are wrong. Three places once parsed names by their own rules; only one knew `sdh`,
and a mapper read unknown tags by two letters, so the refresh took a hearing-impaired file for `SD` and
refetched it every run (SidecarNaming.cs:22-36). The writer had the same defect: four places composed names
inline (148-154), and one upload name went to three sites (SourceContract.cs:984-990).

## Where the rules live

`SPECIFICATION.md` states them: F-M29 and F-M30 for the upload name, F-M239 for the one reader, F-M278 for
the rename, F-M315 to F-M317 for the slots, F-M378 and F-M643 for the pack members, F-M790 and F-M799 for
the code in a name. The `coverage` and `uploadname` suites hold the slots and the one builder.
