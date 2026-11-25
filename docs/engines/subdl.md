# subdl.com — the API source

**Owner: the source at a glance** — contracts, capabilities, languages, transport, cost, failure shapes, the ledger, the boundary. The detailed site material stands in `../MULTI-SOURCE.md` and `../SPECIFICATION.md`.

## 1. What it is

subdl.com is the only one of the three that offers an API (F-M682). It carries film and series
subtitles in many languages, including AI-translated ones, and it is the only source that keeps an
account we can read: our own submissions, their state, and, for one of them, the way back out.
Downloads arrive as a ZIP container, either one subtitle or a whole season.

## 2. Which contracts it answers

- **Job contract — all four jobs.** Search by title and identifiers, fetch one candidate by its
  handle, send a subtitle single or as a season pack, report a broken entry (F-M682, F-M685).
- **Account-ledger contract — yes, and it is the only source that does.** List our own open
  submissions, list published submissions with both handles, withdraw one submission (F-M729,
  F-M435).
- **Inquiry contract — yes.** Two lights: the API address itself, and the login state of the account
  (F-M730). The engine reads the allowance live as part of the same answer (F-M733).

## 3. The account ledger

**This is the only source that answers it** — and it is the reason the account-ledger contract exists
at all (F-M729).

- **Two calls, three tasks.** One call reads what the account holds, our own submissions or the
  published ones. The other pulls one submission back (`WithdrawAsync`);
  `Key` names the source in the same value the plugin database carries. There is no
  third route and no edit: a submission is withdrawn and sent again, never patched.
- **Two lists, two handles.** A published submission carries the handle to withdraw it **and** next to
  it the handle of its published entry. The two number spaces are not the same number (F-M435). An
  open submission carries only the first, because nothing has been published yet.
- **The state is coarser than any source.** Open, accepted, rejected: when rejecting, with the
  source's own plain text alongside. What only one side knows does not cross the boundary (F-M729).
- **Withdrawing ends in the same four outcomes as sending:** accepted, rejected, busy, failed. One
  vocabulary, not two (F-M685).
- **It answers no reachability and no quota question.** It keeps a record; it does not give an inquiry
  answer (F-M730). The lights and the allowance belong to the inquiry contract.

**What it never does:** it knows no row of the plugin database, no identifier shape of ours and no
media. The pipeline hands it the query (which list, how deep, from which water mark), and it answers
with entries plus the number of pages it spent; the pipeline decides what that means for a row.

**What it cannot do:** it cannot edit a submission, cannot publish a draft, and cannot tell why
somebody else rejected it. It passes the source's text through as the source wrote it.

## 4. Capabilities

- upload — yes
- report — yes, behind the `Report` switch
- season packs — yes, behind the `Packs` switch and `Prefer season pack`
- title creation — **no** (F-M734 applies to the two page-based sources, not to this one)
- quota — **yes**, the only source that answers it
- hearing impaired — yes
- short name — `subdl`

## 5. Languages

**Open without a list.** The source does not enumerate what it accepts; it takes what comes
(F-M735). Its language spelling is therefore a mapping in one direction only: it answers which
identifier it uses for a language we name, and there is no set to hold a language against. A
language it does not carry shows up as an empty answer from the site, not as a refusal of the
mapping.

## 6. How we talk to it

- The API base is `api.subdl.com`, the download host is `dl.subdl.com` (F-M682).
- A search returns candidates with an opaque handle, a language, a release name and whether the
  candidate belongs to a pack.
- A fetch returns bytes, a file name, the site's own identifier and a content hash.
- A send answers with one of the four outcomes: accepted, rejected, busy, failed (F-M685).
- The account-ledger lists published entries with two number spaces: the entry id and the upload id.
  They are not the same number (F-M435).

## 7. What it costs and what it limits

The free allowance is a daily number of searches and a daily number of downloads; both limits are
**read from the site, live**: never cached, never assumed (F-M733). At the limit the site answers
`api_download_limit_exceeded` with a reset timestamp. Three shapes are told apart, because they need
three different reactions: the daily allowance spent (stop, the reset is known), a transient overload
(retry), and a rate header inside the answer (F-M238). Two switches decide whether a run continues
after the limit, one per direction.

## 8. Where it fails and how we answer

- **Daily download limit reached** — the run stops asking this source and keeps going with the
  others; the inquiry light reports the address as reachable, the allowance lives in the bars.
- **Rate-limited** — the login light says so in plain text instead of reporting a wrong password.
- **No acceptable subtitle for a track** — the candidate is rejected before the fetch, so a rejection
  is not counted as a failure.
- **Pack refused** — an unknown language, a missing release name or a non-UTF-8 body is answered with
  a reason, not with an exception (F-M685).

## 9. The boundary

This engine knows no plugin database, no identifier shape of ours and no row of our storage; it
receives its network connection, its archive cache, a logger and its settings from outside and writes
nothing itself (F-M682). The pipeline names it nowhere: it asks the registry for its capabilities and
reads the short name `subdl` as a data value (F-M731). Everything it returns is a proposal with
evidence: an identifier, a state, a plain text. The pipeline decides where that lands.

## 10. What it cannot do

- It cannot create a title: a film or series that the site does not know is not created by us there
  (F-M734 is not part of this contract).
- It cannot tell us a closed list of languages, so a language refusal by this source never comes from
  a mapping. It comes from an empty answer.
- It answers quota, but not health: how the site itself is doing is only what the address and the
  login say.
