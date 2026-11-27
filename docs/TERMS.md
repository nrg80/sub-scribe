# Terms — one word per concept

One concept, one word, in every document: a second word for the same thing is not a style question, it
hides a second meaning, so the retired word is named below and cannot creep back in. English is the master
and the German wording is retired; `SPECIFICATION.md`, `CONTRACTS.md`, `AUTOSYNC.md`, `DATA-MODEL.md`,
`TESTS.md`, `GUI.md`,
`LOGGING.md`, `ENDPOINTS.md`, `MULTI-SOURCE.md`, `TVSUBTITLES.md`, `SEASON-PACK-CACHING.md`,
`UPLOAD-EXTRACTION-CACHE.md`, `TMDB-CACHE.md`, `MOVIESUBTITLES.md`, `RELEASE.md`, `README.md` and the papers
under `engines/` use these words and no synonyms.

## The parties

- **source** — a site a subtitle can be fetched from or sent to, with its own access data and behaviour. In
  the wiring talk (§21 and the legacy type names) it is called an **endpoint**. Retired: Quelle.
- **engine** — the part that speaks for one source and answers its contracts; everything source-specific
  lives here.
- **client** — the part of an engine that speaks HTTP to one site; it transports, decides nothing and
  reports no state.
- **pipeline** — the part that owns the plugin database, our identifiers and every decision; it knows no
  source by name. Retired: Ablauf.

## What we keep

- **fact** — one small, stable statement about a title or an episode, taken from a source and kept: which
  identifier belongs to what, a runtime, a year, the count of seasons and episodes, a first airing. An
  answer, not a response.
- **raw answer** — the complete response a source sent to one address, kept for a short while as a fallback
  for addresses that carry no rule yet.
- **plugin database** — our own database and its rows: identifiers, marks, locks, counters. Retired:
  Bestand, Store.
- **files area** — where files live on disk: media, saved subtitles, archives, backups and evidence.
  Retired: Ablage.

## What we handle

- **pack** — one container holding a whole season's subtitles for one language. Retired: Behälter.
- **season** — a block of a title's episodes. Retired: Staffel.
- **episode** — one installment of a season. Retired: Folge.
- **track** — one subtitle stream inside a media file. Retired: Spur.
- **mark** — our stamp on a track for one source: uploaded there, rejected there, origin known. Retired:
  Marke.
- **lock** — pipeline knowledge that a title stays closed after repeated failure. Retired: Sperre.
- **loose subtitle** — a subtitle file lying loose in the library next to the media file, as opposed to one
  embedded in it. Retired: Beiwerk. The plugin database keeps the old name as a level: the table is
  `sidecar` and its identifiers read `#yyyyy&zzz` (F-M671, F-M674). That double word has two homes and
  stays, because renaming a stored key is a migration for a word; a document that means the concept writes
  `loose subtitle`, one that means the table writes `sidecar`.
- **identifier** — a value that names something; short form `id` in code only. Retired: Kennung.
- **media container** — the file that carries the media itself and its streams (Matroska, MP4); its
  per-stream language tags are what the seeder writes and what `0:s:N` counts. Not a pack.
- **handle** — an opaque string a source hands out to name one of its own records later; we never parse it.
  Retired: Griff.
- **verdict** — our judgement about a pair of source and track, or about a pack for one language. Retired:
  Urteil.

## What we report

- **capability** — what a source says about what it can do: upload, report, accept packs, create a title,
  keep a quota, its short name. Retired: Fähigkeit.
- **light** — one line of a source's self-report: key, label, health state, plain text. Retired: Ampel.
- **tile** — one source's block on the settings page. Retired: Kachel.
- **inquiry** — a question put to a source about how it stands; the two directions are the **inquiry
  request** and the **inquiry answer**. Retired: Auskunft.
- **language mapping** — the translation table of one site: which code the site uses, which ISO identifier
  and which spelling of the plugin database belong to it. A data value of the source. Retired: Tafel,
  Sprachtafel.
- **supported languages** — a source's statement that its enumeration of accepted languages is complete.
  The alternatives are **open without a list**, which takes what comes, and **unknown**, a key this build
  does not know. An empty list is none of the three: it would claim "can do nothing" and close sends that
  were open. Retired: geschlossene Liste.
- **language refusal** — a source does not carry the language, so nothing is sent to it; distinct from
  **rejection**, the site's answer to something sent. Retired: Sprachabsage.

## The contracts

- **contract** — an interface one source may fulfil. Retired: Vertrag.
- **job contract** — exactly four jobs: **search**, **fetch**, **send**, **report**; mandatory for every
  source.
- **account-ledger contract** — two calls, three tasks: one call lists our own submissions or the published
  ones, one withdraws a single submission. Voluntary, only for sources with an account.
- **inquiry contract** — two tasks: how the source stands, and how much quota is left today. Voluntary in
  name and fulfilled by every source. Retired: Selbstauskunftsvertrag, self-report contract.
- **send outcomes** — exactly four: **accepted**, **rejected**, **busy**, **failed**; withdrawing uses the
  same four, never a second vocabulary.
- **dry run** — a run that creates nothing and decides nothing; its answer is "not askable", never "no".

## Counting

- **counter** — a number in a contents list; it counts definition lines in its block, sub-headings and
  retired entries included. Retired: Zähler.
- **note** — a short piece of plain text attached to a row or a log line. Retired: Vermerk.
- **run** — one pass of a task. Retired: Lauf.
- **boundary** — the line a contract may not cross. Retired: Grenze.
- **step**, **path**, **target**, **rule** — Retired: Schritt, Weg, Ziel, Regel.

---

A word a document needs but this file lacks is added here first and then used, never invented in the
document that needed it.
