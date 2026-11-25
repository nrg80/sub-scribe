# moviesubtitles.org — the film source

## 1. What it is

moviesubtitles.org carries subtitles for **films only**; which of our media is a film and which is a
series is decided by TMDb, not by the file name (F-M507). Like tvsubtitles it is page-based — there is
no API — and it keeps no account we can read. What makes it the smallest of the three contracts is
what it does *not* have: no season packs, no report path, no quota.

## 2. Which contracts it answers

- **Job contract — three of four jobs.** Search, fetch and send. It has **no report path**, so the
  report job is not answered — it does not pretend it could (F-M682, F-M685).
- **Account-ledger contract — no.** No account is read, so it is absent from that list (F-M729).
- **Inquiry contract — yes.** One light: the address itself (F-M730). It answers no quota.

## 3. Capabilities

- upload — yes
- report — **no**
- season packs — **no**, neither offered nor accepted
- title creation — **yes**: a film the site does not know is looked up and created there (F-M734)
- quota — no
- hearing impaired — **no**
- short name — `moviesubtitles`: no short name is set, so the key stands in (F-M731)

## 4. Languages

**Supported languages — a deliberately short closed list.** The mapping is small on purpose: only
what the site actually carries is accepted. A language outside it is a **language refusal** decided
before anything is sent (F-M735); nothing is uploaded in its place.

## 5. How we talk to it

- A film record is resolved first: the site's own film identifier, derived from the film's identifiers
  and year. Without it there is nothing to ask.
- The subtitle entries of that film are read from its page, with their language and their file.
- A download returns the file itself.
- An upload answers with an HTTP status. When the status is a success the site's own id of our filing
  may still be unreadable; the answer text is passed through unchanged rather than invented (F-M685).

## 6. What it costs and what it limits

There is no allowance to read and no quota to report — the cost is page reads, one per film. Cut films
(a film split into several parts) are recognised on the site's side and handled as their own case
instead of being mistaken for a broken entry.

## 7. Where it fails and how we answer

- **No film id** — the leading film record is missing or unreadable (`not-found`): the candidate is
  skipped with that reason and the run keeps going with the other sources (F-M507).
- **No TMDb record** — without a film record on our side there is no leading identifier, so nothing is
  asked (F-M507).
- **The film record does not answer** — a timeout; the run continues with the other sources instead of
  failing as a whole.
- **Language not carried** — a language refusal before anything is read on the site (F-M735).
- **Upload accepted, id unknown** — accepted with an unknown identifier: the answer text is passed
  through and no id is invented.

## 8. The boundary

No plugin database, no identifier shape of ours, no row of our storage; the engine receives
connection, archive cache, logger and settings from outside and writes nothing itself (F-M682). The
pipeline names it nowhere — the capabilities say what it can do, and the answer carries a proposal
with evidence. Its film resolution uses the media identifiers the pipeline hands in, never a lookup
into our store.

## 9. What it cannot do

- No season packs: a series never reaches this source, and asking for a pack there is refused with a
  reason instead of being attempted.
- No report path: a broken entry cannot be reported here.
- No hearing-impaired marking and no quota answer.
- No account: nothing of ours can be listed or withdrawn on this site.
