# TMDb Cache — one shelf for every question, facts instead of responses

Every question the plugin asks TMDb is answered from one
shelf on disk. What is kept is a fact; what is kept beside it, for a short while, is the raw answer as a
fallback. This paper replaces the three half-caches of today with one.

**Status: built in 12.3.8.72, one rule still open.** The shape was approved on 2026-11-27: own file, facts
and raw answers side by side, ceiling per kind, no hash work for a lookup, and a lock that is no longer driven by
a cached answer. In force are F-M772, F-M773, F-M775, F-M776, F-M777 and F-M778 (§11). **F-M774 is not built**:
the call sites still ask with a language and still append blocks, so the shelf is small today but not as small
as this paper wants it.

*The words are those of `TERMS.md`: a **fact** is an answer, a **raw answer** is a response.*

## Contents

- [1. What this is about](#1-what-this-is-about)
- [2. What stands today](#2-what-stands-today)
- [3. One shelf, in its own file](#3-one-shelf-in-its-own-file)
- [4. Facts first, raw answers as a net](#4-facts-first-raw-answers-as-a-net)
- [5. Which questions are worth keeping](#5-which-questions-are-worth-keeping)
- [6. Two kinds of identifier, two lifetimes](#6-two-kinds-of-identifier-two-lifetimes)
- [7. Lifetimes and ceilings](#7-lifetimes-and-ceilings)
- [8. What a miss means for the two directions](#8-what-a-miss-means-for-the-two-directions)
- [9. What the cache says](#9-what-the-cache-says)
- [10. What it replaces](#10-what-it-replaces)
- [11. The requirements it needs](#11-the-requirements-it-needs)
- [12. Where the limits are](#12-where-the-limits-are)

## 1. What this is about

Jellyfin, TMDb and the sources count differently, and every conversation between them needs an
identifier. So the plugin asks TMDb constantly. Measured over one day: **1.221 requests, 305 different
addresses, 916 repeats** — the same series twenty-one times, the same film nineteen times, sometimes twice
in one second. Before the first shelf existed, a single day of restarts was enough to make **283 of 555
requests** repeats.

Every question is small and the answer is almost always the same as yesterday. That is the whole case for
a shelf.

## 2. What stands today

Three things keep answers, and they do not know about each other:

- **the resolver's own questions.** It memoises the answers it asked for, keyed by the question and
  whether it meant a series, in the plugin database and in an unbounded dictionary (F-M542).
- **the HTTP chain.** A handler answers **every** GET to TMDb from the same store, keyed by the address
  without the key, and stores the whole response as it came (F-M555). This is the good idea: no future call
  site can forget the shelf.
- **a season index.** A small dictionary of the last thirty-five series and seasons, computed in the run.

Two stores, one table, two habits. The table is `(Id, Value, Written)` — one column for "an identifier"
and one column for "a whole response", no kind, no size, no last use, no hit count. Two lifetimes rule
everything: **180 days** for an answer that named an identifier, **7 days** for one that found nothing.

One address slips past all of it: the status page builds its own HTTP clients without the handler, so its
question for the TMDb configuration is asked every time.

## 3. One shelf, in its own file

The shelf lives on disk in **its own file** beside the plugin database (operator decision). Three reasons,
and the first one is the one that decides it:

- **a ceiling has to be a fact.** Removing rows from SQLite does not shrink the file it lives in. In the
  plugin database the ceiling would be a calculation while the file stays large. In its own file the
  ceiling is the file, and a trim can be followed by a real shrink.
- **clearing is a delete.** A switch at `0` means "keep nothing", and that is a file to remove.
- **the plugin database stays lean.** Eleven megabytes of rows the pipeline needs should not carry a
  cache the pipeline can rebuild.

Facts are also mirrored in memory, capped, so a run over a season does not read the disk for every
episode. Raw answers are never held in memory.

## 4. Facts first, raw answers as a net

The same answer measured twice, on 2026-11-27:

| Address | As sent | As a fact |
| --- | --- | --- |
| search, one hit | 2.327 B | 72 B (five candidates, name, year, kind) |
| film detail | 1.749 B | 64 B (identifier, IMDb number, runtime, year) |
| film detail with external identifiers | 1.904 B | 64 B |
| the same with release dates | 19.823 B | 64 B |
| series with external identifiers and one season | 55.963 B | 66 B |

A response carries between fifteen and thirty-three fields. The plugin reads five of them. Keeping the
response costs up to **875 times** what keeping the fact costs, and a 32 MB shelf would hold about **570**
such addresses before it starts throwing away what it needs.

So the shelf has two tables:

- **`tmdb_fact`** — `Kind`, `Subject`, `Value`, `Written`. The kind names the statement (`external_id`,
  `runtime`, `year`, `season_count`, `episode_count`, `air_date`, `search`), the subject names the thing it
  is about (`movie:<id>`, `tv:<id>`, `tv:<id>:s<season>`). An **empty value** means "asked, and there was
  nothing".
- **`tmdb_body`** — `Key`, `Body`, `Bytes`, `Hits`, `Written`. The raw answer, for addresses that carry no
  rule yet and for the burst of repeats inside one run.

The raw answer is the source, the fact is the harvest: the handler cuts the fact out of the answer in the
same pass in which it stores it. The fact then lives far longer than the answer it came from.

## 5. Which questions are worth keeping

Five decisions, each of them about meaning rather than plumbing:

1. **Language leaves the key where it changes nothing.** Everything the plugin needs is structural:
   identifiers, runtime, year, season and episode counts, air dates. None of that depends on language, yet
   every question is asked with one, so the same fact is paid for two or three times. Structural questions
   are asked without a language, and the fact is stored once.
2. **No appended blocks.** A response with appended parts is a separate address for every combination of
   parts, and each combination is paid for on its own. Ask for one block at a time.
3. **A search is kept short.** A search answer is a guess about a title, and TMDb's answer improves as
   titles are added. It is kept for **one day** — that short life is the repair.
4. **Nothing volatile.** Popularity, vote counts, poster and backdrop paths, overviews: the plugin reads
   none of them. They are cut out before storage and never enter a key.
5. **Nothing negative for long.** An answer that found nothing is kept for **one day**, so a title that
   appears on TMDb is picked up tomorrow.

## 6. Two kinds of identifier, two lifetimes

The two look alike and are not:

- **An identifier TMDb states.** Asked by number, TMDb answers which identifier belongs to it. That is a
  statement of the source about itself, and it stays true: **365 days**.
- **An identifier the plugin guessed.** A title and a year are searched, and one of the candidates is
  taken. That guess can be old or wrong — a 2024 series once matched its 2003 namesake. It is kept for
  **30 days**, and it carries its **evidence**: the title and the year that produced it. If the question
  asked later does not match that evidence, the entry is a miss and TMDb is asked again. A wrong guess
  therefore repairs itself instead of sitting in the shelf for a year.

## 7. Lifetimes and ceilings

| What | Lifetime | Ceiling |
| --- | --- | --- |
| identifier, stated by TMDb | 365 days | 16 MB together with the other facts |
| identifier, guessed from title and year | 30 days | |
| any other fact | 180 days | |
| an answer that found nothing | 1 day | |
| a raw answer to a search | 1 day | 16 MB |
| any other raw answer | 3 days | |

Eviction is by age first and then oldest first until the ceiling is met. The memory mirror holds facts
only and is capped by count. Both ceilings are readable at any time, because the file is (see §9).

## 8. What a miss means for the two directions

A missing answer is not an error, and the two directions behave differently — both of them already do:

- **Download** skips the item: `SKIP — no id resolvable (TMDb ladder failed) — the next run tries again`.
  Nothing is fetched, nothing is written.
- **Upload** skips it too, counts a `not-found` and takes the item into the next round.

The dangerous case is not a missing answer but a **wrong** one, and the two directions carry different
risk. A wrong identifier in the download direction fetches subtitles for the wrong title; the run-time
check usually rejects them. A wrong identifier in the upload direction sends under the wrong title, and an
upload cannot be taken back. Both directions therefore keep their identifier test before they act.

**The lock must not be driven by the shelf.** A title is closed after four failed attempts, and a closed
title is not tried again. If an answer from the shelf counted as four attempts, one cached "nothing found"
would close a title with pauses of two, four and eight seconds and without a single question asked. So the
retry ladder runs against the network only, a shelf hit counts as one attempt, and a fresh "nothing found"
closes nothing.

## 9. What the cache says

**Every operation writes a Debug line** — the same level the archive cache reports on, switched by the
plugin's own log mode, so nothing appears at Normal. All lines carry one prefix, so one search finds them
all. The diagnostics endpoint names the state of both tables:

- **on start** — file, both ceilings, rows and bytes per kind;
- **a hit** — kind, subject, age, and whether it came from memory or from disk;
- **a miss** — kind, subject, reason (absent, expired, evidence did not match);
- **a store** — kind, subject, bytes;
- **a trim** — how many expired, how many evicted, bytes against the ceiling;
- **the diagnostics** — rows and bytes per kind, so a ceiling can be checked from outside.

A hit names its kind and subject and its age; a miss names the reason; a trim names both counts. That is
the same shape as the cache lines of the download shelf, so one reading habit covers both.

No key, no identifier and no body excerpt appears in a line. A question is named by its kind and its
subject, never by its content.

## 10. What it replaces

- the resolver's question store (F-M542) becomes facts plus short-lived "nothing found" entries;
- the unbounded dictionary beside it becomes the capped memory mirror;
- the address entries of the HTTP handler (F-M555) become raw answers;
- the season index stays as it is, because it is computed per run and is not an answer of TMDb;
- the status page's own question for the configuration goes through the same shelf once its client is
  built like every other.

## 11. The requirements it needs

These are proposals. Each one enters `SPECIFICATION.md` only when the code that carries it exists.

- **F-M772 — one shelf, on disk, in its own file.** Every answer from TMDb is stored in one file beside the
  plugin database, with one ceiling per kind; a switch at `0` removes the file.
- **F-M773 — facts, not responses.** What is kept is the fact the plugin reads; a raw answer is kept only
  as a fallback, for a shorter time and under its own ceiling. Both carry their size.
- **F-M774 — the key carries no language where language changes nothing,** and no appended blocks: one
  question, one block, one entry.
- **F-M775 — two kinds of identifier.** A stated identifier lives 365 days; a guessed one lives 30 days and
  carries its evidence, and a question that does not match the evidence is a miss.
- **F-M776 — an answer that found nothing lives one day,** and a search answer one day; every other raw
  answer three days.
- **F-M777 — the shelf never closes a title.** A hit counts as one attempt, and the retry ladder runs
  against the network only.
- **F-M778 — the shelf reports itself at Debug level:** one line per operation — start, hit, miss, store,
  trim — under one prefix and switched by the plugin's log mode, plus rows and bytes per kind in the
  diagnostics. No line carries a key, an identifier or an excerpt.

## 12. Where the limits are

- **A fact can outlive the title it describes.** A removed film leaves its facts; only the lifetime removes
  them.
- **A guessed identifier is only as good as its evidence.** A renamed title, or one re-released under the
  same name and year, is not caught by evidence; the identifier test in front of the send is what catches
  it.
- **A raw answer is a snapshot of fields the plugin does not own.** A field that disappears from TMDb takes
  its fact with it, until the lifetime runs out.
- **The file can be larger than the ceiling once,** between a trim and a shrink. That window is named in
  the log rather than hidden.
