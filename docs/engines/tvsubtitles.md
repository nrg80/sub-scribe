# tvsubtitles.net — the page-based source

The site's own material — paths, tables, peculiarities, methods — stands in `../TVSUBTITLES.md`.

## 1. What it is

tvsubtitles.net carries subtitles for series only, and it has no API: the site is opened page by page,
the way a browser opens it (F-M480). It keeps no account we can read, so nothing of our own filings can
be listed there. Its language enumeration is small and closed, and it needs its own title record before
it accepts anything, which is why it is one of the two sources that can be asked to create one.

## 2. Which contracts it answers

It answers all four jobs: search over the site's own pages, fetch the subtitle file, send a subtitle as
a single or as a season pack, and report a broken entry (F-M682, F-M685). It answers no account-ledger
contract, because the source keeps no account we can read (F-M729). It answers the inquiry contract
with one light, the address itself, and no quota (F-M730).

## 3. The account ledger

The source is absent from the ledger list rather than answering "cannot", because an absent source is
never asked while a source that answered "cannot" would have to be remembered as a refusal (F-M729).
Nothing here can be listed, nothing withdrawn, and no state of our own submissions is known from this
side.

## 4. Capabilities

It uploads, reports behind the `Report` switch, takes season packs behind the `Packs` switch and
`Prefer season pack`, and takes hearing-impaired marks; quota is no and the short name is `tvst`. Title
creation is yes: the engine looks up a series the site does not know, creates it there, and the answer
says whether the identifier is readable (F-M734).

## 5. Languages

Supported languages are a closed enumeration: the mapping holds 24 site codes in 29 entries, because
five ISO identifiers (`el`, `cs`, `zh`, `ja`, `uk`) point at the same site codes as their aliases, so the
site is addressed with 24 distinct values. A language outside that set is a language refusal decided
before anything is extracted or sent, and nothing is uploaded in its place (F-M735). A missing code is
never sent as a guess, because the form falls back to its first entry and would publish the file under
the wrong language.

## 6. How we talk to it

| Path | What it is |
| --- | --- |
| `/add.html` | the upload form, and the only place a filing starts |
| `/tvshow-<id>.html` | one series and its seasons |
| `/tvshow-<id>-<season>.html` | one season and its episodes |
| `/episode-<page>.html` | one episode and its subtitle entries |
| `/subtitle-<id>.html` | one entry and its file |

There is no API (F-M480): `/api*`, `/xmlrpc.php` and the API host answer nothing, and `/rss.xml` carries
no episode data, so a search costs several page reads. The run fetches the series list once and keeps
it, but never keeps an empty answer, so a glitch cannot poison the rest of the run.

## 7. What it costs and what it limits

There is no allowance to read and no quota to report; the cost is page reads. The run takes the series
list once and then reads per episode, and a season pack is built and offered as one container, which
the site accepts the same way it accepts a single file.

## 8. Where it fails and how we answer

A series the site does not know is created first (F-M734); until it exists the send answers "not
askable" and sends nothing, so repeating the run cannot produce a second upload. A language the site
does not carry is a refusal decided from the mapping before the engine extracts anything (F-M735), and
nothing is sent. An episode the search does not name stays open for the other source, and a broken
entry can be reported (F-M685).

## 9. The boundary

No plugin database, no identifier shape of ours, no row of our storage: the engine receives connection,
archive cache, logger and settings from outside and writes nothing itself (F-M682). The pipeline does
not name it; it reads the short name `tvst` as a data value (F-M731) and asks the source whether it
carries a language instead of consulting a table in the client (F-M735). What comes back is a proposal
with evidence, and the pipeline decides what enters the store.

## 10. What it cannot do

It has no API, so there is no machine-readable allowance and no bulk path: every answer is a page. It
cannot answer an account question, and it cannot take a film, because TMDb decides the media kind
(F-M507).
