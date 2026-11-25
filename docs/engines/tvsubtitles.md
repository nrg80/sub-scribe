# tvsubtitles.net — the page-based source

## 1. What it is

tvsubtitles.net carries subtitles for **series only**, and it has no API: the site is opened the way a
browser opens it, page by page (F-M480). It has no account we read, so nothing about our own filings
can be listed there. Its language enumeration is small and closed, and it needs its own title record
before it accepts anything — which is why it is one of the two sources that can be asked to create
one.

## 2. Which contracts it answers

- **Job contract — all four jobs.** Search over the site's own pages, fetch the subtitle file, send a
  subtitle single or as a season pack, report a broken entry (F-M682, F-M685).
- **Account-ledger contract — no.** The source keeps no account we can read, so it is absent from that
  list rather than answering "cannot" (F-M729).
- **Inquiry contract — yes.** One light: the address itself (F-M730). It answers no quota.

## 3. The account ledger

**This source does not answer it, and it does not say "I cannot".** It keeps no account we can read,
so it is absent from the ledger list (F-M729) — the difference matters: an absent source is never
asked, while a source that answered "cannot" would have to be remembered as a refusal.

What follows from that: nothing here can be listed, nothing can be withdrawn, and no state of our
own submissions is known from this side. Whether a filing of ours still exists is a question only the
account's own record can answer, and this source has no such record.

## 4. Capabilities

- upload — yes
- report — yes, behind the `Report` switch
- season packs — yes, behind the `Packs` switch and `Prefer season ZIPs`
- title creation — **yes**: a series the site does not know is looked up and created there, and the
  answer says whether the identifier is readable (F-M734)
- quota — no
- hearing impaired — yes
- short name — `tvst`

## 5. Languages

**Supported languages — a closed enumeration.** The mapping holds **24 site codes in 29 entries**;
five ISO identifiers (`el`, `cs`, `zh`, `ja`, `uk`) point at the same site codes as their aliases, so
the site is addressed with 24 distinct values. A language outside that set is a **language refusal**
before anything is extracted or sent — the site is not asked and nothing is uploaded in its place,
which is the whole point of the closed statement (F-M735). A missing code is never sent as a guess:
the site's form falls back to its first entry, and a fallback would publish the file under the wrong
language.

## 6. How we talk to it

The path is the path of the site itself (F-M480):

- `/add.html` — the upload form, and the only place a filing starts.
- `/tvshow-<id>.html` — one series and its seasons.
- `/tvshow-<id>-<season>.html` — one season and its episodes.
- `/episode-<page>.html` — one episode and its subtitle entries.
- `/subtitle-<id>.html` — one entry and its file.

There is no API: `/api*`, `/xmlrpc.php` and the API host answer nothing, and `/rss.xml` carries no
episode data. A search therefore costs several page reads, and the series list is fetched once per run
and kept — an empty answer is not kept, so a glitch cannot poison the rest of the run.

## 7. What it costs and what it limits

There is no allowance to read and no quota to report — the cost is page reads. The run takes the
series list once and then reads per episode; a season pack is built by us and offered as one
container, which the site accepts the same way it accepts a single file.

## 8. Where it fails and how we answer

- **Series not known** — the title is created first (F-M734); until it exists, the send answers "not
  askable" and sends nothing, so repeating the run cannot produce a second upload.
- **Language not carried** — a language refusal, decided from the mapping before anything is
  extracted (F-M735). Nothing is sent, and the pair is closed with that reason.
- **Episode not on the site** — the search yields nothing for that position; the episode stays open
  for the other source.
- **Entry broken** — the report job is available (F-M685).

## 9. The boundary

No plugin database, no identifier shape of ours, no row of our storage; the engine receives connection,
archive cache, logger and settings from outside and writes nothing itself (F-M682). The pipeline does
not name it — it reads the short name `tvst` as a data value (F-M731) and asks the source whether it
carries a language instead of consulting a table in the client (F-M735). What comes back is a proposal
with evidence, and the pipeline decides what enters the store.

## 10. What it cannot do

- It has no API, so there is no machine-readable allowance and no bulk path: every answer is a page.
- It cannot answer an account question: there is no account-ledger contract here.
- It cannot take a film: the site is a series site, and the media kind is decided by TMDb (F-M507).
