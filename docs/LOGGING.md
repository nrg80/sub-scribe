# The log — the standard for every line and every text

## 1. What it is

Every line the plugin writes, and every text the operator reads on the settings pages, follows this
standard. It is not a derived paper: it is the rule the log code implements, and a requirement that owns a
rule is cited with its `F-M` tag. The words are those of `TERMS.md`.

The standard leans on three things that exist outside this repository. Serilog Message Templates
(<https://messagetemplates.org>) give a fixed text frame with named fields, so the same line looks the same
on every run and stays machine-countable. The OpenTelemetry Semantic Conventions for Logs
(<https://opentelemetry.io/docs/specs/semconv/>) require time, severity, event name and subject, in that
order. The Google SRE Book, „Monitoring Distributed Systems", is written for the one who is woken at night:
the severity says what the reader is to do, not how interesting the author found the spot.

Every plugin line belongs to exactly one level and carries its marker as text — `[N]` Normal, `[V]` Verbose
and up, `[D]` Detail (F-M226). The statistics never depend on logging (F-M225).

## 2. The yardstick

**Can the operator decide from a single line whether he has to do something?** If not, the line is wrong —
not too long, not too short, but wrongly built.

## 3. The rules

Twelve rules, each with its reason.

- **3.1 One line, one event.** No intermediate step, no intention, no addendum about work that happens
  later, because an announcement is not an event. The SubDL id is read back once, at the end of the run, and that read-back is an event of its own
  (F-M588).
- **3.2 Fixed frame.** An event word in capitals first — `UPLOADED`, `REJECTED`, `SKIP`, `SEED`, `PACK`,
  `DEFER` — then the subject, then numbers, then the result, always in that order.
- **3.3 The source that answered belongs in the line.** The prefix `[Sub Scribe]` names the plugin, not the
  source;
  whoever reads `UPLOADED` has to see where it went without prior knowledge. If no source answered, the
  line says `no source answered`, never a gap that looks like SubDL.
- **3.4 Numbers instead of adjectives.** Every summary names the number and the distribution, not an
  evaluation: `0 uploaded | 295 rejected (275 duplicate, 20 duplicate-HI) | 97 skipped | 14 min`.
- **3.5 The severity says what is to be done.** The level names the reaction the reader owes the line, not
  how interesting the author found the spot; which line stands where is a policy, and chapter 4 holds it.
- **3.6 No cancellation without a subject.** A word like `cancelled` says what was cancelled — the run, the
  extraction, an attempt — and a real run cancellation is written only where the run really ends (F-M596).
- **3.7 One state, one word.** `ok`, `fail`, `skip`, `cancel` and `defer` stand in the log as in the GUI,
  with no synonym, no second spelling and no paraphrase; a state has exactly one colour and one word.
- **3.8 Summarise repetitions.** Never the same line N times at Normal: one line carries the number, and
  the single cases stand at Verbose.
- **3.9 Only what happened.** No intention („will", „shall", „read back once at the end"), no guess, no
  passive without an actor where the actor is known; a line that announces a later action is a promise in
  the log, and promises go stale.
- **3.10 The event code stands at the end, exactly one.** `F-M###` is a stable identifier for the finding
  site, so tests can pin it down; one code per line, in brackets, at the end, and two codes in one line
  mean two events.
- **3.11 No broken characters.** The writer puts quote marks around every string value and doubles existing
  ones, so a placeholder carries no quote marks of its own and no empty string argument stands as a suffix.
  A template that writes `\"{Name}\"` prints the value quoted already; a suffix that is an empty
  string prints as `""`, so attach it only when it has content or give it its own line. A line with such an
  artefact makes the reader take it for broken and look for the fault in the wrong place.
- **3.12 The lower case of existing keywords stays.** `upload:`, `download:`, `season pack:`, `LAUF` and
  `TAKT` are grown markers that filters and tests hang on; new lines follow 3.2, and an old marker is not
  renamed without the tests coming along.

## 4. The levels — what belongs where

The plugin's own log mode is the only authority: everything is written at the normal level and the detail
is gated inside the log helper, the per-item level from Verbose upward and the detail level from Debug
upward (F-M224). `Normal` stays lifecycle and summary, warnings and errors are never gated, and no plugin
line is written at Trace.

### Normal

Must the operator know it without being asked? One event per file and direction stands here, one line per
success — uploaded, downloaded, saved, packed, aligned — and one run line per direction, with `RUN START`
and `RUN END` carrying all counters including the zeros. Every abort or cancellation names its subject,
every refusal that ends a hunt stands here too, and so do the source summaries, the statistics block and
`POSTPROCESS` armed or not armed. The dry-run line does the same work and reports it, but contributes
nothing to the statistics (F-M247). An error that logs does not replace the success line it explains.

### Verbose

Does someone asking „where exactly?" need it, and only then? The per-item work: the reason a single item
was skipped or a verdict fell, as in `SKIP "…" — duplicate-self-echo (HI=False)`, a language missing, no
film identifier, a refusal cause. Here too stands the per-track detail of a rewrite or an alignment, and
one line per title where the Normal line carries only the count.

### Debug

Is it only for us while diagnosing? Internals and diagnostics (F-M225): the position in a list
(„item 429/1417") and every other progress counter, timing, durations and the cost of a step, resolved tool
paths, decoder arguments, database statements, cache hits, the reschedule budget and seed pre-check
timestamps. A line that names only a position belongs here, not at Normal. So does anything needed to
reproduce a defect but not to decide anything.

### What never stands at Normal

A diagnostic — a resolved path, a budget, a timestamp of a pre-check. A progress counter or a per-case
repetition (3.8). An intention or a promise (3.9). Anything a statistic depends on, because the counters
are written by the statistics writer from the run summaries and never read out of a log line (F-M225), so
a `Normal` run counts exactly like a `Debug` run.

### The gate and the marker

One gate per message: a call site picks the lowest level that must carry its message and calls the matching
gate, and `Normal < Verbose < Debug` is cumulative, so a second call would print the message twice
(F-M245). The marker matches the gate: `[N]`, `[V]` and `[D]` stand as text at the normal level (F-M226), so
a line whose marker disagrees with its gate is a defect the reader sees. Changing a level is a change of
the contract between us and the reader and needs the test that pins it down in the same commit (chapter 6).

## 5. The run report

At most ten Normal lines per run and direction, from which the run can be retold in words; everything else
is Verbose.

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

Every line begins with the same keyword as the scaffold, in a fixed order. `RUN END` is mandatory and
carries all counters, the zeros included. `POSTPROCESS` is mandatory, because „nothing uploaded" and
„nothing scheduled" are two different states that would otherwise look the same. A cancelled run writes
the same scaffold, with `RUN END` cancelled and what it still managed.

## 6. The check list

Before every release, for every new line, seven questions:

1. Does the line say what happened, on which object, with which number, where to?
2. Does it have exactly one event, one code, one source?
3. Is it at the level where the reader needs it, and not missing at Normal where it describes a success?
4. Does it use the word the GUI uses for the same state?
5. Does a number stand where an adjective stood?
6. Would a stranger read it correctly without knowing the code?
7. Does it stand in the run report in its place, or does it make the report longer than ten lines?

Whoever changes a line adds the test that pins it down in `scripts/tests/`, otherwise the next cleanup
round is rid of the old line again.

## 7. What the standard never does

It never lets statistics depend on a log line (F-M225, F-M247): a dry run contributes nothing, and no
counter is read out of a message. It never carries a credential, an address or a path of the operator,
which is the rule of every other document too (NF-10). It never quotes a value twice and never writes an
empty suffix (3.11). It never announces work that has not happened yet (3.9): the line appears when the
event does.
