# Logging — the standard for every line and every text

**Owner: the log and text standard.** Every line the plugin writes and every text the operator reads follows it.

*Paper: this document states the house standard for the log and for every text the operator reads in
the GUI. It is not derived — it is the rule the log code implements, and where the requirements own a
rule they are cited with their `F-M` tag. The words are those of `TERMS.md`.*

## Contents

- [1. What it is](#1-what-it-is)
- [2. The yardstick](#2-the-yardstick)
- [3. The rules](#3-the-rules)
  - [3.1 One line = one event](#31-one-line-one-event)
  - [3.2 Fixed frame: event, subject, numbers, result](#32-fixed-frame-event-subject-numbers-result)
  - [3.3 The source that answered belongs in the line](#33-the-source-that-answered-belongs-in-the-line)
  - [3.4 Numbers instead of adjectives](#34-numbers-instead-of-adjectives)
  - [3.5 The severity says what is to be DONE](#35-the-severity-says-what-is-to-be-done)
  - [3.6 No cancellation without a subject](#36-no-cancellation-without-a-subject)
  - [3.7 One state, one word](#37-one-state-one-word)
  - [3.8 Summarise repetitions](#38-summarise-repetitions)
  - [3.9 Only what happened](#39-only-what-happened)
  - [3.10 The event code stands at the end, exactly one](#310-the-event-code-stands-at-the-end-exactly-one)
  - [3.11 No broken characters](#311-no-broken-characters)
  - [3.12 The lower case of existing keywords stays](#312-the-lower-case-of-existing-keywords-stays)
- [4. The run report](#4-the-run-report)
- [5. The check list](#5-the-check-list)
- [6. What the standard never does](#6-what-the-standard-never-does)

## 1. What it is

Every line the plugin writes, and every text the operator reads on the settings pages, follows the
standard in this document. It leans on three things that already exist outside this repository,
instead of inventing a school of its own:

- **Serilog Message Templates** (<https://messagetemplates.org>) — a fixed text frame with named
  fields, so that the same line looks the same on every run and stays machine-countable.
- **OpenTelemetry Semantic Conventions for Logs** (<https://opentelemetry.io/docs/specs/semconv/>) —
  a log entry needs time, severity, event name and subject, in that order.
- **Google SRE Book, „Monitoring Distributed Systems"** — write for the one who is woken at night:
  the severity says what the reader is to DO, not how interesting the author found the spot.

The one level that decides a line's existence is the marker `[N]` (Normal), `[V]` (Verbose and up),
`[D]` (Detail): every plugin line belongs to exactly one level and carries its marker as text
(F-M226), and the statistics never depend on logging (F-M225).

## 2. The yardstick

**Can the operator decide from a single line whether he has to do something?** If not, the line is
wrong — not too long, not too short, but wrongly built.

## 3. The rules

Every rule names its reason and gives an example. The examples are real lines from this plugin.

### 3.1 One line = one event

One event per line, no intermediate step, no intention, no addendum about work that happens later.

- Wrong: `upload saved for "X.mkv" [UK] — the SubDL id is read back once at the end of the run (F-M588).`
  Two things stand in one line here, and the second is an announcement, not an event.
- Right: `UPLOAD SAVED "X.mkv" [UK] → subdl`, and when the identifier really is assigned:
  `UPLOAD ID "X.mkv" [UK] = 421844 (mySubtitles)`.

### 3.2 Fixed frame: event, subject, numbers, result

The order in the sentence is always the same. An event word in capitals first (`UPLOADED`, `REJECTED`,
`SKIP`, `SEED`, `PACK`, `DEFER`), then the subject, then numbers, then the result.

- `UPLOADED "Doctor.Who.2023.S02E01.1080p.x265-ELiTE.mkv" [EN] → tvsubtitles` ✓
- `SKIP "War.2026.S01E05…mkv" — item settled before extraction (every text stream has its end state)` ✓

### 3.3 The source that answered belongs in the line

The prefix `[Sub Scribe]` is the name of the plugin, not of the source. Whoever reads `UPLOADED` must,
without prior knowledge, see where it went.

- Wrong: `[Sub Scribe] UPLOADED "X.mkv" ["EN"]`
- Right: `UPLOADED "X.mkv" [EN] → tvsubtitles`
- If no source answered, that is written there: `no source answered` — not a gap that looks like SubDL.

### 3.4 Numbers instead of adjectives

Every summary names the number and the distribution, not an evaluation.

- Wrong: `upload: finished — everything rejected`
- Right: `RUN END upload — 0 uploaded | 295 rejected (275 duplicate, 20 duplicate-HI) | 97 skipped | 14 min`

### 3.5 The severity says what is to be DONE

| Level | Meaning | Example |
|---|---|---|
| Normal (`[N]`) | The operator must know it. One event per file and direction, plus one run line each. | `UPLOADED …`, `RUN START`, `RUN END` |
| Verbose (`[V]`) | Where exactly it happened. Only when someone asks. | `SKIP "…" — duplicate-self-echo (HI=False)` |
| Detail/Debug (`[D]`) | Internal steps, timing, database. | `LAUF upload — item 429/1417` |

- A **success** needs a Normal line. An error that logs does not replace it.
- A line that at Normal level names only the position („item 429/1417"), belongs on Debug.

### 3.6 No cancellation without a subject

A word like `cancelled` must say WHAT was cancelled. The run, the extraction, an attempt?

- Wrong: `ffmpeg for "Ali G….mkv" was stopped — the run was cancelled (F-M596)`
- Right: `EXTRACT CANCELLED "Ali G….mkv" — the item settled first`, and a real run cancellation only at
  the place where the run really ends.

### 3.7 One state, one word

`ok`, `fail`, `skip`, `cancel`, `defer` — the same words in the log as in the GUI. No synonym, no
second spelling, no paraphrase. A state has exactly one colour and exactly one word.

### 3.8 Summarise repetitions

Do not put the same lines N times at Normal. One line with the number, the single cases at Verbose.

- Normal: `SECOND SOURCE moviesubtitles — 66 title(s) closed, 18 id(s) unusable, 5 language(s) missing`
- Verbose: one line per title with the reason.

### 3.9 Only what happened

No intention („will", „shall", „read back once at the end"), no guess, no passive without an actor,
when the actor is known. A line that announces a later action is a promise in the log — and promises
in the log go stale.

### 3.10 The event code stands at the end, exactly one

`F-M###` is a stable identifier for the finding site, so that tests can pin it down. One code per
line, in brackets, at the end. Two codes in one line mean: there are two events.

### 3.11 No broken characters

The log writer itself puts quote marks around every **string value** and doubles existing ones.
Therefore: **no quote marks of your own around a placeholder** and **no empty string argument as a
suffix**.

- Wrong: `LAUF upload — item 1/1417 ""Back to the Suture""` — the template writes `\"{Name}\"`, the
  writer quotes on top of that. Right: `LAUF upload — item 1/1417 {Name}` (the value arrives quoted).
- Wrong: `upload: started""` — the suffix `{Dry}` is an **empty** string and is printed as `""`.
  Right: attach the suffix only when it has content, or give it a line of its own.
- Wrong: `"sent"""` — the same mistake on the success message of a send.

A line with such an artefact is not a cosmetic problem: the reader takes the line for broken and looks
for the fault in the wrong place.

### 3.12 The lower case of existing keywords stays

`upload:`, `download:`, `season pack:`, `LAUF`, `TAKT` are grown markers on which filters and tests
hang. New lines follow 3.2, old markers are not renamed without the tests coming along.

## 4. The run report

Goal: **at most ten Normal lines per run and direction**, from which the run can be retold in words.
Everything else is Verbose.

```
[N] RUN START upload — 1417 item(s), sources: tvsubtitles, subdl, moviesubtitles
[N] SEED upload — 192 open (tvsubtitles 173, subdl 20, moviesubtitles 18)
[N] PACK upload — 125 season(s) on disk, 12 examined, 3 uploaded, 9 skipped (2 incomplete, 7 one source)
[N] DECIDE upload — 155 settled, 275 duplicate, 20 duplicate-HI, 97 stream(s) skipped
[N] UPLOADED "<file>" [EN] → tvsubtitles            (one line per file really uploaded)
[N] REJECTED "<file>" [AR] → subdl: duplicate (upload #385190)   (one line per source and file)
[N] SOURCE moviesubtitles — 66 title(s) closed, 18 id(s) unusable, 5 language(s) missing
[N] RUN END upload — 0 uploaded | 295 rejected | 0 refused | 0 without answer | 97 skipped | 14 min 27 s
[N] POSTPROCESS — armed for 21:13 UTC ("upload run ended (9 uploaded)") / not armed (nothing uploaded)
```

Rules for this scaffold:

- Every line begins with the same keyword as in the example. The order is fixed.
- `RUN END` is mandatory and carries **all** counters, including the zeros.
- `POSTPROCESS` is mandatory, because „nothing uploaded" and „nothing scheduled" are two different
  states that would otherwise look the same.
- A cancelled run writes the same scaffold, `RUN END` with `cancelled` and what it still managed.

## 5. The check list

Before every release, for every new line:

1. Does the line say **what** happened, **on which object**, **with which number**, **where to**?
2. Does it have exactly **one** event, **one** code, **one** source?
3. Is it at the level where the reader needs it — and is it not missing at Normal when it describes a
   success?
4. Does it use the word the GUI uses for the same state?
5. Does a number stand where an adjective stood?
6. Would a stranger read it correctly without knowing the code?
7. Does it stand in the run report in its place — or does it make the report longer than ten lines?

Whoever changes a line adds the test that pins it down (`scripts/tests/`), otherwise the next cleanup
round will be rid of the old line again.

## 6. What the standard never does

- **It never lets statistics depend on a log line** (F-M225, F-M247): a dry run contributes nothing,
  and no counter is read out of a message.
- **It never carries a credential, an address or a path of the operator** — the same rule as every
  other document (NF-10).
- **It never quotes a value twice** and never writes an empty suffix (3.11).
- **It never announces work that has not happened yet** (3.9); the line appears when the event does.
