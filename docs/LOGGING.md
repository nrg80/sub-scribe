# Log and text — the standard for this plugin

As of 18.11.2026. Applies to every line the plugin writes and to every text the operator
reads in the GUI.

**The request:** „with the log and the texts I struggle anyway. Is there no concept or a
best practice paper one could apply to it?"

This paper is the answer. It leans on three things that already exist outside this repo,
instead of inventing a school of its own:

- **Serilog Message Templates** (<https://messagetemplates.org>) — a fixed text frame with named
  fields, so that the same line looks the same on every run and stays machine-countable.
- **OpenTelemetry Semantic Conventions for Logs** (<https://opentelemetry.io/docs/specs/semconv/>) —
  a log entry needs time, severity, event name and subject, in that order.
- **Google SRE Book, „Monitoring Distributed Systems"** — write for the one who is woken at night:
  the severity says what the reader is to DO, not how interesting the author found the spot.

The yardstick is one question: **Can the operator decide from a single line whether he has to do
something?** If not, the line is wrong — not too long, not too short, but wrongly built.

## 1. Rules

Every rule names reason and example. The examples are real, from the prod run of 18.11.2026.

### 1.1 One line = one event

One event per line, no intermediate step, no intention, no addendum about work that
happens later.

- Wrong: `upload saved for "X.mkv" [UK] — the SubDL id is read back once at the end of the run (F-M588).`
  Two things stand in one line here, and the second is an announcement, not an event.
- Right: `UPLOAD SAVED "X.mkv" [UK] → subdl`, and when the identifier really is assigned:
  `UPLOAD ID "X.mkv" [UK] = 421844 (mySubtitles)`.

### 1.2 Fixed frame: event, subject, numbers, result

The order in the sentence is always the same. An event word in capitals first (`UPLOADED`,
`REJECTED`, `SKIP`, `SEED`, `PACK`, `DEFER`), then the subject, then numbers, then the result.

- `UPLOADED "Doctor.Who.2023.S02E01.1080p.x265-ELiTE.mkv" [EN] → tvsubtitles` ✓
- `SKIP "War.2026.S01E05…mkv" — item settled before extraction (every text stream has its end state)` ✓

### 1.3 The source that answered belongs in the line

The prefix `[Sub Scribe]` is the name of the plugin, not of the source. Whoever reads `UPLOADED` must, without
prior knowledge, see where it went.

- Wrong: `[Sub Scribe] UPLOADED "X.mkv" ["EN"]`
- Right: `UPLOADED "X.mkv" [EN] → tvsubtitles`
- If no source answered, that is written there: `no source answered` — not a gap that
  looks like SubDL.

### 1.4 Numbers instead of adjectives

Every summary names the number and the distribution, not an evaluation.

- Wrong: `upload: finished — everything rejected`
- Right: `RUN END upload — 0 uploaded | 295 rejected (275 duplicate, 20 duplicate-HI) | 97 skipped | 14 min`

### 1.5 The severity says what is to be DONE

| Level | Meaning | Example |
|---|---|---|
| Normal (`[N]`) | The operator must know it. One event per file and direction, plus one run line each. | `UPLOADED …`, `RUN START`, `RUN END` |
| Verbose (`[V]`) | Where exactly it happened. Only when someone asks. | `SKIP "…" — duplicate-self-echo (HI=False)` |
| Detail/Debug (`[D]`) | Internal steps, timing, database. | `LAUF upload — item 429/1417` |

- A **success** needs a Normal line. An error that logs does not replace it.
- A line that at Normal level names only the position („item 429/1417"), belongs on Debug.

### 1.6 No cancellation without a subject

A word like `cancelled` must say WHAT was cancelled. The run, the extraction, an attempt?

- Wrong: `ffmpeg for "Ali G….mkv" was stopped — the run was cancelled (F-M596)`
  (in the run of 18.11.2026 it stood there four times, and the run then went on for another 300 positions)
- Right: `EXTRACT CANCELLED "Ali G….mkv" — the item settled first`, and a real run cancellation only
  at the place where the run really ends.

### 1.7 One state, one word

`ok`, `fail`, `skip`, `cancel`, `defer` — the same words in the log as in the GUI. No synonym, no
second spelling, no paraphrase. A state has exactly one colour and exactly one word.

### 1.8 Summarise repetitions

Do not put the same lines N times at Normal. One line with the number, the single cases at Verbose.

- Normal: `SECOND SOURCE moviesubtitles — 66 title(s) closed, 18 id(s) unusable, 5 language(s) missing`
- Verbose: one line per title with the reason.

### 1.9 Only what happened

No intention („will", „shall", „read back once at the end"), no guess, no passive without an actor,
when the actor is known. A line that announces a later action is a promise in
the log — and promises in the log go stale.

### 1.10 The event code stands at the end, exactly one

`F-M###` is a stable identifier for the finding site, so that tests can pin it down. One code per
line, in brackets, at the end. Two codes in one line mean: there are two events.

### 1.11 No broken characters

The log writer itself puts quote marks around every **string value** and doubles existing ones.
Therefore: **no quote marks of your own around a placeholder** and **no empty
string argument as a suffix**.

- Wrong (real): `LAUF upload — item 1/1417 ""Back to the Suture""` — the template writes
  `\"{Name}\"`, the writer quotes on top of that.
  Right: `LAUF upload — item 1/1417 {Name}` (the value arrives quoted).
- Wrong (real): `upload: started""` — the suffix `{Dry}` is an **empty** string and is printed as `""`.
  Right: attach the suffix only when it has content, or a line of its own for it.
- Wrong (real): `"sent"""` — the same mistake on the success message of a send.

Measured on the prod run 18.11.2026: **1.424 of 7.021 lines** carried such artefacts. That is not a
cosmetic problem — a reader takes the line for broken and looks for the fault in the wrong place.

### 1.12 The lower case of existing keywords stays

`upload:`, `download:`, `season pack:`, `LAUF`, `TAKT` are grown markers on which filters and
tests hang. New lines follow 1.2, old markers are not renamed without the tests coming along.

## 2. The run report (scaffold)

Goal: **at most ten Normal lines per run and direction**, from which the run can be retold
in words. Everything else is Verbose.

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
- A cancelled run writes the same scaffold, `RUN END` with `cancelled` and what it still
  managed.

## 3. Check list (before every release, for every new line)

1. Does the line say **what** happened, **on which object**, **with which number**, **where to**?
2. Does it have exactly **one** event, **one** code, **one** source?
3. Is it at the level where the reader needs it — and is it not missing at Normal when it
   describes a success?
4. Does it use the word the GUI uses for the same state?
5. Does a number stand where an adjective stood?
6. Would a stranger read it correctly without knowing the code?
7. Does it stand in the run report in its place — or does it make the report longer than ten lines?

Whoever changes a line adds the test that pins it down (`scripts/tests/`), otherwise the
next cleanup round will be rid of the old line again.

## 4. Appendix: measurement on the prod run 18.11.2026, 22:08–22:22

Counted on `log_20261118.log` (upload direction, 1.417 positions):

- **7.021 lines** in total, of which **125 Normal**, 1.301 Verbose, 5.254 Debug. So what can be read is
  **1,8 %** of the run — that is the whole reason for „with the log I struggle".
- The 125 Normal lines are **34 different templates**, of which **around 90 single cases** from the
  season pack path (`season pack: "<id>" S<n> — …`). By rule 1.8 those belong on Verbose; the
  report shrinks to about 30 lines with that.
- **1.424 lines** with quote-mark artefacts (rule 1.11), **94 lines** with two `F-M` codes
  (rule 1.10), **4 lines** with a wrong cancellation subject (rule 1.6: „the run was cancelled", while
  the run went on).

These four points are the backlog for the next cleanup round — in this order, because
every following point without the previous one only re-sorts lines again.
