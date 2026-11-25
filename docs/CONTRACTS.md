# The contracts of the sources — where the boundary runs

This document fixes **which contract answers which question** and **what none of them may do**. It
complements the requirements (F-M682, F-M729 to F-M734) and does not repeat them: the rule lives
there, the boundary between the contracts lives here.

**Counting convention.** This document defines no `F-M` entries of its own — the rules live in
`docs/REQUIREMENTS.md`, and this document cites their tags instead of repeating them. Its contents
list therefore carries anchors only, no counters; the per-section counters live where the definitions
live.

## Contents

- [1. Terms](#1-terms)
- [2. Scope](#2-scope)
- [3. The job contract — the work on one subtitle](#3-the-job-contract-the-work-on-one-subtitle)
- [4. The account-ledger contract — the account's own record](#4-the-account-ledger-contract-the-accounts-own-record)
- [5. The self-report contract — how the source stands](#5-the-self-report-contract-how-the-source-stands)
- [6. Shared boundary rules](#6-shared-boundary-rules)
- [7. Who writes to the store](#7-who-writes-to-the-store)
- [8. Allocation: which question belongs where](#8-allocation-which-question-belongs-where)
- [9. Work that lives in no contract](#9-work-that-lives-in-no-contract)
- [10. Checks that hold the boundary](#10-checks-that-hold-the-boundary)

## 1. Terms

Four words carry the whole document, so they are pinned once:

- **Source** — a site a subtitle can be fetched from or sent to, with its own access data.
- **Engine** — the part that speaks for one source and answers its contracts. The engine is where
  everything source-specific lives.
- **Client** — the part of an engine that speaks HTTP to one site. A client transports; it never
  decides and never reports a state.
- **Pipeline** — the part that owns our store, our identifiers and every decision. It knows no source
  by name.
- **Store** — our own database and files: identifiers, marks, locks, counters.
- **Handle** — an opaque string a source hands out so it can name one of its own records later. We
  never parse it.
- **Pack** — one container holding a whole season's subtitles for one language.
- **Light** — one line of a source's self-report: a key, a label, a health state and plain text.

## 2. Scope

- **There are three contracts.** There is no fourth; a new task is assigned to one of the three or it
  is not built.
- **A job contract** with exactly four jobs: search, fetch, send, report (F-M682).
- **An account-ledger contract** with three tasks: list our own submissions, list published
  submissions, withdraw one submission (F-M729).
- **A self-report contract** with two tasks: how the source stands, and how much quota is left today
  (F-M730, F-M733).
- **Every source may fulfil one, two or all three of them.** The job contract is mandatory, the other
  two are voluntary. Whoever does not fulfil them is absent from the respective list — there is no
  empty obligatory answer.
- **The rules themselves live in `docs/REQUIREMENTS.md`** — §22 for one contract per source, §§3 and 4 for the two directions, §10 for quota and timing. This document states where the boundary runs; it cites the tags instead of repeating the rules.
- **The contracts are independent of each other.** No contract presupposes another; no task is
  answered across a foreign contract.

## 3. The job contract — the work on one subtitle

| Job | Question | Answer |
| --- | --- | --- |
| Search | Which subtitles do you have for this title? | Candidates with an opaque handle, language, release name, pack membership |
| Fetch | Give me this subtitle. | Bytes, file name, foreign identifier, content hash |
| Send | Take this subtitle (single or as a pack). | accepted · rejected · busy · failed (F-M685) |
| Report | This entry is broken. | accepted · rejected · busy · failed |

- **The send job carries the title question with it** (F-M734): if the source lacks its own title
  record, the job may require the source to look it up on its site and create it. The answer reports
  what happened — ready, created, created without a readable identifier, already carries the title
  without an identifier, not findable, not askable, no verdict — and names the identifier if there is
  one.
- **A dry run creates nothing and decides nothing.** If the title is missing in a dry run, the answer
  is "not askable"; it is not a no.
- **The send job sends nothing as long as the title does not stand.** That is why the pipeline may
  repeat it without producing a second upload.
- **The job contract knows no account.** Listing and withdrawing are not part of it, not even as a
  fifth job.

## 4. The account-ledger contract — the account's own record

- **Only sources with their own account fulfil it.** A source without an account is absent from the
  list; it does not answer "I cannot do that".
- **Two lists, two handles.** A published submission carries the handle to withdraw it and next to it
  the handle of its published entry; the two number spaces are not the same (F-M435). An open
  submission carries only the first.
- **The state is coarser than any source:** open, accepted, rejected — when rejecting, with the
  source's plain text alongside. States only one side knows do not cross the boundary.
- **The outcome of withdrawing is the outcome of sending.** Four outcomes, no second vocabulary
  (F-M685).
- **The account-ledger contract answers no reachability and no quota question.** It keeps a record,
  it does not give a self-report.

## 5. The self-report contract — how the source stands

- **Every source fulfils it**, including one without an account: whether it is reachable is something
  it knows best itself.
- **The source sets its own lights.** Key, label, health state and plain text come from it; the caller
  knows no source by name and only paints what comes back (F-M730).
- **Quota is read live, not cached.** The run tells a short-term throttle apart from the daily limit
  by it (F-M238, F-M733).
- **"I keep no quota" is a valid answer** — no invented numbers, no silence.

## 6. Shared boundary rules

1. **No contract knows the store.** No database, no directory, none of our identifier shapes, no row
   of our storage.
2. **What is handed in is handed in.** Network connection, archive cache, log and settings come from
   outside; no engine opens its own network connection.
3. **Only neutral data crosses the boundary:** language in one uniform spelling, timestamps in UTC,
   the source's plain text, and opaque handles. No addresses, no access data, no foreign identifier
   shape.
4. **The source name is a data value, not a switch.** It stands in the store, in the origin line and
   on the tile; nothing is decided by it.
5. **Capabilities instead of name comparisons.** Whether a source can upload, report, accept packs,
   create a title or keep a quota is said by its capabilities — not by its name (F-M682, F-M734).
6. **A contract is a promise, not an obligation to be complete.** Whoever cannot do a task does not
   fulfil the contract; it does not pretend it could.
7. **Which language a source accepts, the source says** — in both directions and with the information
   whether it keeps a closed list. "I keep no list" is its own answer and **not** the same as an empty
   list, which would mean "I can do nothing" (F-M735).

## 7. Who writes to the store

- **The pipeline writes, the source never.** Stored identifiers, marks, locks and counters belong to
  the pipeline.
- **What the source returns is a proposal with evidence:** an identifier, a state, a plain text. Only
  the pipeline decides whether and where it enters the store (F-M508, F-M734).
- **A lock is pipeline knowledge.** That a title stays closed for good after four failed attempts
  (F-M608) is known only to the pipeline; the source never sees any of it.

## 8. Allocation: which question belongs where

| Question | Responsible | Never responsible |
| --- | --- | --- |
| Which subtitles do you have for this title? | Job contract (search) | Account ledger, self-report |
| Take this subtitle | Job contract (send), including title creation | Account ledger |
| What else lies with my account? | Account ledger | Job contract |
| Withdraw this entry | Account ledger | Job contract |
| Are you reachable, which light applies? | Self-report | Job contract |
| How much is left today? | Self-report (live) | Account ledger, job contract |
| What is your name in one line? | Capabilities | all three contracts |
| What happened to the title? | Job contract (answer of the send) | Account ledger |
| How many pages has my account? | Account ledger (depth of the question) | Self-report |

## 9. Work that lives in no contract

- **Creating a title** is not a job of its own but a field of sending (F-M734).
- **Quota** is not account bookkeeping but a self-report (F-M733).
- **Diagnosis on the account** (paging through one's own submissions) uses the account ledger with a
  page depth of one — it invents no path of its own (F-M729).
- **Status lights** use the self-report; they open no connection of their own to the source (F-M730).
- **A source's short name** is a capability, not a condition in the pipeline (F-M731).
- **A site's language table** is a data value of the source, not a matter of fetching; the one neutral
  spelling of the store stays above the sources (F-M557, F-M735).

## 10. Checks that hold the boundary

- **F-M682:** no file of a source names the store or the pipeline; no file of the pipeline names a
  source; every source is built explicitly, not looked up.
- **T392:** the account-ledger contract carries exactly its three tasks, stands next to the four jobs,
  and only the source with an account fulfils it.
- **T393:** the self-report contract carries exactly its tasks, every source fulfils it, and outside
  the source no file reads an access datum from the general configuration.
- **T394:** the pipeline no longer names a source — short name from the capabilities, lists from the
  account ledger, quota from the self-report.
- **T395:** the send job carries the title question; both sides that create titles do so without our
  store, and without a verdict in a dry run.
- **T396:** every source yields its language answer — the two sites with a closed list, the third
  open; the return path delivers the store's spelling, and the pipeline no longer asks a client for
  its table.
