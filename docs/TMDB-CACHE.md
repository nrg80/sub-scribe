# TMDb cache — one shelf for every question, facts instead of responses

Jellyfin, TMDb and the sources count differently, and every conversation between them needs an identifier, so the plugin asks TMDb constantly. Measured over one day: **1.221 requests, 305 different addresses, 916 repeats**, with the same series twenty-one times and the same film nineteen times, sometimes twice in one second. Before the first shelf existed, a single day of restarts was enough to make **283 of 555 requests** repeats. Every question is small and the answer is almost always the same as yesterday, which is the whole case for a shelf.

## What is built

**Built in 12.3.8.72, with one rule still open.** The operator approved the shape on 2026-11-27: own file, facts and raw answers side by side, a ceiling per kind, no hash work for a lookup and a lock that is no longer driven by a cached answer. In force are F-M772, F-M773, F-M775, F-M776, F-M777 and F-M778. **F-M774 is not built**, because the call sites still ask with a language and still append blocks, so the shelf is small today but not as small as this paper wants it.

## What stands today

Three things keep answers and they do not know about each other. The resolver memoises the questions it asked, keyed by the question and whether it meant a series, in the plugin database and in an unbounded dictionary (F-M542). The HTTP handler answers every GET to TMDb from one store, keyed by the address without the key, and stores the whole response as it came (F-M555), which is the good idea because no future call site can forget the shelf. A season index keeps the last thirty-five series and seasons, computed in the run. So there are two stores, one table and two habits: the table is `(Id, Value, Written)`, with one column for an identifier and one for a whole response, no kind, no size, no last use and no hit count. Two lifetimes rule everything: 180 days for an answer that named an identifier, 7 days for one that found nothing. One address slips past all of it, because the status page builds its own HTTP clients without the handler and asks for the TMDb configuration every time.

## One shelf, in its own file

The shelf lives on disk in its own file beside the plugin database, and the reason that decides it is that a ceiling has to be a fact. Removing rows from SQLite does not shrink the file it lives in, so the ceiling would be a calculation there while the file stays large, and in its own file the ceiling is the file, so a trim can be followed by a real shrink. Clearing is then a delete, because a switch at `0` means keep nothing and that is a file to remove. And the plugin database stays lean, since eleven megabytes of rows the pipeline needs should not carry a cache the pipeline can rebuild. Facts are also mirrored in memory, capped, so a run over a season does not read the disk for every episode, while raw answers are never held in memory.

## Facts first, raw answers as a net

The same answer measured twice on 2026-11-27:

| Address | As sent | As a fact |
| --- | --- | --- |
| search, one hit | 2.327 B | 72 B (five candidates, name, year, kind) |
| film detail | 1.749 B | 64 B (identifier, IMDb number, runtime, year) |
| film detail with external identifiers | 1.904 B | 64 B |
| the same with release dates | 19.823 B | 64 B |
| series with external identifiers and one season | 55.963 B | 66 B |

A response carries between fifteen and thirty-three fields, and the plugin reads five of them. Keeping the response costs up to **875 times** what keeping the fact costs, and a 32 MB shelf would hold about **570** such addresses before it starts throwing away what it needs. The words are those of `TERMS.md`: a **fact** is an answer and a **raw answer** is a response. The handler therefore cuts the fact out of the answer in the same pass in which it stores it, so the raw answer is the source and the fact is the harvest, and the fact lives far longer than the answer it came from.

| Table | Fields | What it holds |
| --- | --- | --- |
| `tmdb_fact` | `Kind`, `Subject`, `Value`, `Written` | the kind names the statement (`external_id`, `runtime`, `year`, `season_count`, `episode_count`, `air_date`, `search`), the subject names the thing it is about (`movie:<id>`, `tv:<id>`, `tv:<id>:s<season>`), and an empty value means asked and there was nothing |
| `tmdb_body` | `Key`, `Body`, `Bytes`, `Hits`, `Written` | the raw answer, for addresses that carry no rule yet and for the burst of repeats inside one run |

## Which questions are worth keeping

Five decisions are about meaning rather than plumbing. Language leaves the key where it changes nothing, because everything the plugin needs is structural: identifiers, runtime, year, season and episode counts, air dates. None of that depends on language, yet every question is asked with one, so the same fact is paid for two or three times. Structural questions are asked without a language and the fact is stored once. No block is appended either, because a response with appended parts is a separate address for every combination and each combination is paid for on its own, so the plugin asks for one block at a time. A search answer is kept for **one day**, because it is a guess about a title and TMDb's answer improves as titles are added. Nothing volatile is kept: popularity, vote counts, poster and backdrop paths and overviews are read by nothing, cut out before storage and never part of a key. And nothing negative is kept for long, because an answer that found nothing lives **one day**, so a title that appears on TMDb is picked up tomorrow.

## Two kinds of identifier, two lifetimes

An identifier TMDb states stays **365 days**, because TMDb is asked by number and answers which identifier belongs to it, which is a statement of the source about itself. An identifier the plugin guessed is kept **30 days** and carries its evidence, the title and the year that produced it, because a guess can be old or wrong: a 2024 series once matched its 2003 namesake. If a question asked later does not match that evidence, the entry is a miss and TMDb is asked again, so a wrong guess repairs itself instead of sitting in the shelf for a year.

## Lifetimes and ceilings

| What | Lifetime | Ceiling |
| --- | --- | --- |
| identifier, stated by TMDb | 365 days | 16 MB together with the other facts |
| identifier, guessed from title and year | 30 days | |
| any other fact | 180 days | |
| an answer that found nothing | 1 day | |
| a raw answer to a search | 1 day | 16 MB |
| any other raw answer | 3 days | |

Eviction is by age first and then oldest first until the ceiling is met. The memory mirror holds facts only and is capped by count. Both ceilings are readable at any time, because the file is.

## What a miss means for the two directions

A missing answer is not an error, and both directions already behave. Download skips the item with `SKIP — no id resolvable (TMDb ladder failed) — the next run tries again`, and nothing is fetched or written. Upload skips it too, counts a `not-found` and takes the item into the next round. The dangerous case is a wrong answer rather than a missing one, and the two directions carry different risk. A wrong identifier in the download direction fetches subtitles for the wrong title, and the run-time check usually rejects them. A wrong identifier in the upload direction sends under the wrong title, and an upload cannot be taken back, so both directions keep their identifier test before they act.

## The lock is not driven by the shelf

A title is closed after four failed attempts and a closed title is not tried again, so a shelf answer that counted as four attempts would close a title with pauses of two, four and eight seconds and without a single question asked. The retry ladder therefore runs against the network only, a shelf hit counts as one attempt, and a fresh nothing found closes nothing.

## What the cache says

Every operation writes a Debug line, the level the archive cache reports on, switched by the plugin's own log mode, so nothing appears at Normal. All lines carry one prefix, so one search finds them all, and the diagnostics endpoint names the state of both tables:

- on start — file, both ceilings, rows and bytes per kind;
- a hit — kind, subject, age, and whether it came from memory or from disk;
- a miss — kind, subject, reason (absent, expired, evidence did not match);
- a store — kind, subject, bytes;
- a trim — how many expired, how many evicted, bytes against the ceiling;
- the diagnostics — rows and bytes per kind, so a ceiling can be checked from outside.

A hit names its kind and subject and its age, a miss names its reason and a trim names both counts, which is the same shape as the cache lines of the download shelf, so one reading habit covers both. No key, no identifier and no body excerpt appears in a line, because a question is named by its kind and its subject and never by its content.

## What it replaces

The resolver's question store (F-M542) becomes facts plus short-lived "nothing found" entries, and the unbounded dictionary beside it becomes the capped memory mirror. The address entries of the HTTP handler (F-M555) become raw answers. The season index stays as it is, because it is computed per run and is not an answer of TMDb, and the status page asks for the configuration through the same shelf once its client is built like every other.

## The requirements it needs

These are proposals, and each one enters `SPECIFICATION.md` only when the code that carries it exists. F-M772 gives one shelf on disk in its own file, with one ceiling per kind and a switch at `0` that removes the file. F-M773 keeps the fact the plugin reads and a raw answer only as a fallback, for a shorter time and under its own ceiling, both carrying their size. F-M774 keeps the language out of the key where it changes nothing and appends no block. F-M775 separates a stated identifier at 365 days from a guessed one at 30 days with its evidence. F-M776 gives an answer that found nothing one day, a search answer one day and every other raw answer three days. F-M777 keeps the shelf out of the lock, so a hit counts as one attempt and the ladder runs against the network. F-M778 reports every operation at Debug under one prefix and names rows and bytes per kind in the diagnostics, with no key, identifier or excerpt in a line.

## Where the limits are

A fact can outlive the title it describes, because a removed film leaves its facts and only the lifetime removes them. A guessed identifier is only as good as its evidence, so a renamed title or one re-released under the same name and year is not caught by evidence and the identifier test in front of the send is what catches it. A raw answer is a snapshot of fields the plugin does not own, so a field that disappears from TMDb takes its fact with it until the lifetime runs out. The file can be larger than the ceiling once, between a trim and a shrink, and that window is named in the log rather than hidden.
