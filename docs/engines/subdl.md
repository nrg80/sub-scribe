# subdl.com — the API source

The detailed site material stands in `../MULTI-SOURCE.md` and `../SPECIFICATION.md`.

## 1. What it is

subdl.com is the only one of the three sources that offers an API (F-M682). It carries film and series
subtitles in many languages, including AI-translated ones, and it is the only source that keeps an
account we can read: our own submissions, their state, and for one of them the way back out. A download
arrives as a ZIP, either one subtitle or a whole season.

## 2. Which contracts it answers

It answers all four jobs: search by title and identifiers, fetch one candidate by its handle, send a
subtitle as a single or as a season pack, and report a broken entry (F-M682, F-M685). It answers the
account-ledger contract, and it is the only source that does (F-M729, F-M435). It answers the inquiry
contract with two lights — the API address and the login state of the account — and reads the allowance
live as part of the same answer (F-M730, F-M733).

## 3. The account ledger

**This is the only source that answers the ledger**, and the reason the account-ledger contract exists
at all (F-M729). Two calls do three tasks: one reads what the account holds, our own submissions or the
published ones, and one pulls a single submission back (`WithdrawAsync`), where `Key` names the source
in the same value the plugin database carries. There is no third route and no edit: a submission is
withdrawn and sent again, never patched. A published submission carries the handle to withdraw it and,
next to it, the handle of its published entry, and those are two number spaces (F-M435); an open
submission carries only the first. The state is coarser than any source — open, accepted, rejected, and
on a rejection the source's own plain text alongside — and what only one side knows does not cross the
boundary (F-M729). Withdrawing ends in the same four outcomes as sending: accepted, rejected, busy,
failed (F-M685). It answers no reachability and no quota question (F-M730).

## 4. Capabilities

It uploads, reports behind the `Report` switch, takes season packs behind the `Packs` switch and
`Prefer season pack`, and takes hearing-impaired marks; the short name is `subdl`. Quota is yes, the only
source that answers it, and title creation is no: F-M734 covers the two page-based sources, not this one.

## 5. Languages

The source is open without a list: it does not enumerate what it accepts, it takes what comes
(F-M735). Its language spelling is therefore a mapping in one direction only — it answers which
identifier it uses for a language we name, and there is no set to hold a language against. A language it
does not carry shows up as an empty answer from the site, not as a refusal of the mapping.

## 6. How we talk to it

| What | Answer |
| --- | --- |
| API base and download host | `api.subdl.com` and `dl.subdl.com` (F-M682) |
| a search | candidates with an opaque handle, a language, a release name and whether the candidate belongs to a pack |
| a fetch | bytes, a file name, the site's own identifier and a content hash |
| a send | one of the four outcomes: accepted, rejected, busy, failed (F-M685) |
| the account ledger | published entries with two number spaces, the entry id and the upload id, which are not the same number (F-M435) |

## 7. What it costs and what it limits

The free allowance is a daily number of searches and a daily number of downloads, both read from the
site live and never cached or assumed (F-M733). At the limit the site answers
`api_download_limit_exceeded` with a reset timestamp, and three shapes are told apart because they need
three different reactions: the daily allowance spent (stop, the reset is known), a transient overload
(retry), and a rate header inside the answer (F-M238). Two switches decide whether a run continues after
the limit, one per direction.

## 8. Where it fails and how we answer

A spent download limit stops the run asking this source and keeps the other sources going; the inquiry
light reports the address as reachable, and the allowance lives in the bars. A rate limit makes the login
light say so in plain text instead of reporting a wrong password. A candidate with no acceptable
subtitle for the track is rejected before the fetch, so the rejection is not counted as a failure. A
refused pack — unknown language, missing release name or non-UTF-8 body — is answered with a reason, not
with an exception (F-M685).

## 9. The boundary

This engine knows no plugin database, no identifier shape of ours and no row of our storage; it receives
its network connection, its archive cache, a logger and its settings from outside and writes nothing
itself (F-M682). The pipeline names it nowhere: it asks the registry for the capabilities and reads the
short name `subdl` as a data value (F-M731). The pipeline hands in the query — which list, how deep, from
which water mark — and the engine answers with entries plus the number of pages it spent. Everything it
returns is a proposal with evidence: an identifier, a state or a plain text.

## 10. What it cannot do

It creates no title, cannot edit a submission, cannot publish a draft and cannot tell why somebody else
rejected it; it passes the source's text through as the source wrote it (F-M734 is not part of this
contract). It cannot name a closed list of languages, so a language refusal by this source never comes
from a mapping, only from an empty answer. It answers quota but not health: how the site itself is doing
is only what the address and the login say.
