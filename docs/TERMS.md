# Terms — one word per concept

**Owner: the vocabulary.** One word per concept, for every document.

This file is the vocabulary of the documentation. **English is the master** (operator decision),
and the German wording is retired. Every document — `REQUIREMENTS.md`, `CONTRACTS.md`, `AUTOSYNC.md`,
`DATA-MODEL.md`, `TESTS.md`, `GUI.md`, `LOGGING.md`, `ENDPOINTS.md`, `MULTI-SOURCE.md`, `TVSUBTITLES.md`,
`MOVIESUBTITLES.md`, `RELEASE.md`, `README.md` and the papers under `engines/` — uses these words and
no synonyms.

**The rule that makes the file worth having:** one concept, one word. A second word for the same thing
is not a style question, it hides a second meaning. Where the documentation used to carry two words,
the retired one is named below so it cannot creep back in.

---

## The parties

- **source** — a site a subtitle can be fetched from or sent to, with its own access data and its own
  behaviour. In the wiring talk (§21 and the legacy type names) the same thing is called an
  **endpoint**: *adding another endpoint* means *attaching another source*. Retired: Quelle.
- **engine** — the part that speaks for one source and answers its contracts. Everything
  source-specific lives here. Retired: —
- **client** — the part of an engine that speaks HTTP to one site. A client transports; it decides
  nothing and reports no state. Retired: —
- **pipeline** — the part that owns the plugin database, our identifiers and every decision. It knows
  no source by name. Retired: Ablauf.

## What we keep

- **plugin database** — our own database and its rows: identifiers, marks, locks, counters. Retired:
  Bestand, Store.
- **files area** — where files live on disk: media, saved subtitles, archives, backups and evidence.
  Retired: Ablage.

## What we handle

- **pack** — one container holding a whole season's subtitles for one language. Retired: Behälter.
- **season** — Retired: Staffel.
- **episode** — Retired: Folge.
- **track** — one subtitle stream inside a media file. Retired: Spur.
- **mark** — our stamp on a track for one source (uploaded there, rejected there, origin known).
  Retired: Marke.
- **lock** — pipeline knowledge that a title stays closed after repeated failure. Retired: Sperre.
- **loose subtitle** — a subtitle file lying loose in the library next to the media file, as opposed
  to one embedded in it. Retired: Beiwerk. **The plugin database keeps the old name as a LEVEL: the
  table is called `sidecar` and its identifiers read `#yyyyy&zzz`** (F-M671, F-M674). That is one
  double word with two homes and it stays: the level name is a stored key, renaming it would be a
  migration for a word, while the concept in every text is `loose subtitle`. A document that means
  the concept writes `loose subtitle`; a document that means the table writes `sidecar`.
- **identifier** — a value that names something. Short form `id` in code only. Retired: Kennung.
- **media container** — the file that carries the media itself and its streams (Matroska, MP4): its
  per-stream language tags are what the seeder writes and what `0:s:N` counts. Not a pack. Retired: —
- **handle** — an opaque string a source hands out to name one of its own records later; we never
  parse it. Retired: Griff.
- **verdict** — our judgement about a pair of source and track, or about a pack for one language.
  Retired: Urteil.

## What we report

- **capability** — what a source says about what it can do (upload, report, accept packs, create a
  title, keep a quota, its short name). Retired: Fähigkeit.
- **light** — one line of a source's self-report: key, label, health state, plain text. Retired:
  Ampel.
- **tile** — one source's block on the settings page. Retired: Kachel.
- **inquiry** — a question put to a source about how it stands. The two directions are the
  **inquiry request** and the **inquiry answer**. Retired: Auskunft.
- **language mapping** — the translation table of one site: which code the site uses, which ISO
  identifier and which spelling of the plugin database belong to it. A data value of the source.
  Retired: Tafel, Sprachtafel.
- **supported languages** — a source's statement that its enumeration of accepted languages is
  complete. The alternatives are **open without a list** (it takes what comes) and **unknown** (a key
  this build does not know). An **empty** list is none of the three: it would claim "can do nothing"
  and close sends that were open. Retired: geschlossene Liste.
- **language refusal** — a source does not carry the language, so nothing is sent to it. Distinct
  from **rejection**, which is the site's answer to something sent. Retired: Sprachabsage.

## The contracts

- **contract** — an interface one source may fulfil. Retired: Vertrag.
- **job contract** — exactly four jobs: **search**, **fetch**, **send**, **report**. Mandatory for
  every source.
- **account-ledger contract** — **two calls, three tasks**: one call lists (our own submissions, or the
  published ones) and one withdraws a single submission. Voluntary, only sources with an account.
- **inquiry contract** — two tasks: how the source stands, and how much quota is left today.
  Voluntary in name, fulfilled by every source. Retired: Selbstauskunftsvertrag, self-report contract.
- **send outcomes** — exactly four: **accepted**, **rejected**, **busy**, **failed**. Withdrawing
  uses the same four, never a second vocabulary.
- **dry run** — a run that creates nothing and decides nothing; its answer is "not askable", never
  "no".

## Counting

- **counter** — a number in a contents list. It counts definition lines in its block, sub-headings
  included, retired entries included. Retired: Zähler.
- **note** — a short piece of plain text attached to a row or a log line. Retired: Vermerk.
- **run** — one pass of a task. Retired: Lauf.
- **boundary** — the line a contract may not cross. Retired: Grenze.
- **step**, **path**, **target**, **rule** — Retired: Schritt, Weg, Ziel, Regel.

---

**Where a document needs a word that is not here,** the word is added here first and then used —
not invented in the document that needed it.
