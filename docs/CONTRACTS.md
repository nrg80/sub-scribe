# The contracts — where the boundary runs

A source answers through three contracts, and there is no fourth: a new task is assigned to one of the three or it is not built. The job contract carries four jobs, the account-ledger contract three tasks, the inquiry contract two tasks. Every source may fulfil one, two or all three; the job contract is mandatory and the other two are voluntary. Whoever does not fulfil one is absent from its list instead of returning an empty obligatory answer, and no contract presupposes another.

## 1. Terms

The vocabulary is [TERMS.md](TERMS.md) — source, pipeline, plugin database, pack, handle, verdict, capability, light, inquiry — and it applies here too. Three words are used in this document only. The three contracts are the job contract, the account-ledger contract and the inquiry contract; a boundary rule is one of the seven in §6; an allocation assigns a question to exactly one contract in §8.

## 2. Scope

The job contract covers search, fetch, send and report. The account-ledger contract lists our own submissions, lists published submissions and withdraws one. The inquiry contract says how the source stands and how much quota is left today. The rules themselves live in `SPECIFICATION.md` — §22 for one contract per source, §§3 and 4 for the two directions, §10 for quota and timing — and this document states where the boundary runs.

## 3. The job contract — the work on one subtitle

| Job | Question | Answer |
| --- | --- | --- |
| Search | Which subtitles do you have for this title? | Candidates with an opaque handle, language, release name, pack membership |
| Fetch | Give me this subtitle. | Bytes, file name, foreign identifier, content hash |
| Send | Take this subtitle (single or as a pack). | accepted · rejected · busy · failed |
| Report | This entry is broken. | accepted · rejected · busy · failed |

The send job carries the title question with it: if the source lacks its own title record, the job may require it to look the title up on its site and create it. The answer reports what happened — ready, created, created without a readable identifier, already carries the title without an identifier, not findable, not askable, no verdict — and names the identifier if there is one. A dry run creates nothing and decides nothing, so a missing title there is "not askable" and not a no. As long as the title does not stand, the send job sends nothing, which is why the pipeline may repeat it without a second upload. The job contract knows no account: listing and withdrawing are not part of it, not even as a fifth job.

## 4. The account-ledger contract — the account's own record

Only sources with their own account fulfil it; a source without an account is absent from the list instead of answering "I cannot do that". A published submission carries two handles, one to withdraw it and one of its published entry, and the two number spaces differ; an open submission carries only the first. The state is coarser than any source: open, accepted, rejected, the last with the source's plain text alongside, and states only one side knows do not cross the boundary. Withdrawing has the outcome of sending, four outcomes and no second vocabulary. The contract answers no reachability and no quota question.

## 5. The inquiry contract — how the source stands

Every source fulfils it, including one without an account, because whether it is reachable is something it knows best itself. The source sets its own lights: key, label, health state and plain text come from it, the caller knows no source by name and only paints what comes back. Quota is read live, not cached, and the run tells a short-term throttle apart from the daily limit by it (F-M238). "I keep no quota" is a valid answer: no invented numbers, no silence.

## 6. Shared boundary rules

1. No contract knows the plugin database: no database, no directory, none of our identifier shapes, no row of our storage.
2. What is handed in is handed in: network connection, archive cache, log and settings come from outside, and no engine opens its own network connection.
3. Only neutral data crosses the boundary: language in one uniform spelling, timestamps in UTC, the source's plain text and opaque handles, no addresses, no access data, no foreign identifier shape.
4. The source name is a data value, not a switch: it stands in the plugin database, in the origin line and on the tile, and nothing is decided by it.
5. Capabilities replace name comparisons: whether a source can upload, report, accept packs, create a title or keep a quota, its capabilities say, not its name.
6. A contract is a promise, not an obligation to be complete: whoever cannot do a task does not fulfil the contract and does not pretend it could.
7. Which language a source accepts, the source says, in both directions and with the information whether it states supported languages; "I keep no list" is its own answer and not an empty list, which would mean "I can do nothing".

## 7. Who writes to the plugin database

The pipeline writes, the source never: stored identifiers, marks, locks and counters belong to the pipeline. What a source returns is a proposal with evidence — an identifier, a state, a plain text — and only the pipeline decides whether and where it enters the plugin database. A lock is pipeline knowledge: a title stays closed after four failed attempts, for seven days, then open for exactly one probe, and the source never sees any of it.

## 8. Allocation: which question belongs where

| Question | Responsible | Never responsible |
| --- | --- | --- |
| Which subtitles do you have for this title? | Job contract (search) | Account ledger, inquiry answer |
| Take this subtitle | Job contract (send), including title creation | Account ledger |
| What else lies with my account? | Account ledger | Job contract |
| Withdraw this entry | Account ledger | Job contract |
| Are you reachable, which light applies? | Self-report | Job contract |
| How much is left today? | Self-report (live) | Account ledger, job contract |
| What is your name in one line? | Capabilities | all three contracts |
| What happened to the title? | Job contract (answer of the send) | Account ledger |
| How many pages has my account? | Account ledger (depth of the question) | Self-report |

## 9. Work that lives in no contract

Creating a title is no job of its own but a field of sending. Quota is not account bookkeeping but an inquiry answer. Diagnosis on the account pages through one's own submissions with the account ledger at a page depth of one, and invents no path of its own. Status lights use the inquiry answer and open no connection of their own to the source. A source's short name is a capability, not a condition in the pipeline. A site's language mapping is a data value of the source and no matter of fetching, while the one neutral spelling of the plugin database stays above the sources.

## 10. Checks that hold the boundary

| Check | What it holds |
| --- | --- |
| F-M682 | no file of a source names the plugin database or the pipeline, no file of the pipeline names a source, every source is built explicitly and not looked up |
| T392 | the account-ledger contract carries exactly its three tasks, stands next to the four jobs, and only the source with an account fulfils it |
| T393 | the inquiry contract carries exactly its tasks and every source fulfils it; outside the source no file reads an access datum from the general configuration |
| T394 | the pipeline names no source: short name from the capabilities, lists from the account ledger, quota from the inquiry answer |
| T395 | the send job carries the title question; both sides that create titles do so without our plugin database and without a verdict in a dry run |
| T396 | every source yields its language answer, the two sites with supported languages and the third open without a list; the return path delivers the plugin database's spelling |

## 11. Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), §22 with §§3, 4 and 10, mainly F-M435, F-M508, F-M557, F-M608, F-M682, F-M685, F-M729, F-M730, F-M731, F-M733, F-M734, F-M735 and F-M771. Guarded by the checks above.
