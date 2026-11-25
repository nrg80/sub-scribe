# moviesubtitles.org — the film source

**Owner: the source at a glance.** The detailed site material stands in `../MOVIESUBTITLES.md`: paths, tables, peculiarities.

## 1. What it is

moviesubtitles.org carries subtitles for **films only**; which of our media is a film and which is a
series is decided by TMDb, not by the file name (F-M507). Like tvsubtitles it is page-based. There is
no API, and it keeps no account we can read. What makes it the smallest of the three contracts is
what it does *not* have: no season packs, no report path, no quota.

## 2. Which contracts it answers

- **Job contract — three of four jobs.** Search, fetch and send. It has **no report path**, so the
  report job is not answered. It does not pretend it could (F-M682, F-M685).
- **Account-ledger contract — no.** No account is read, so it is absent from that list (F-M729).
- **Inquiry contract — yes.** One light: the address itself (F-M730). It answers no quota.

## 3. The account ledger

**This source does not answer it, and it does not say "I cannot".** It keeps no account we can read,
so it is absent from the ledger list (F-M729). The difference matters: an absent source is never
asked, while a source that answered "cannot" would have to be remembered as a refusal.

What follows from that: nothing here can be listed, nothing can be withdrawn, and no state of our
own submissions is known from this side. Whether a filing of ours still exists is a question only the
account's own record can answer, and this source has no such record.

## 4. Capabilities

- upload — yes
- report — **no**
- season packs — **no**, neither offered nor accepted
- title creation — **yes**: the engine looks up a film the site does not know and creates it there (F-M734)
- quota — no
- hearing impaired — **no**
- short name — `moviesubtitles`: no short name is set, so the key stands in (F-M731)

## 5. Languages

**Supported languages — a deliberately short closed list.** The mapping is small on purpose: only
what the site actually carries is accepted. A language outside it is a **language refusal** the
engine decides before it sends anything (F-M735); nothing is uploaded in its place.

## 6. How we talk to it

- The engine resolves a film record first: the site's own film identifier, derived from the film's
  identifiers and year. Without it there is nothing to ask.
- It reads the subtitle entries of that film from its page, with their language and their file.
- A download returns the file itself.
- An upload answers with an HTTP status. When the status is a success the site's own id of our filing
  may still be unreadable; the engine passes the answer text through unchanged rather than inventing
  it (F-M685).

## 7. What it costs and what it limits

There is no allowance to read and no quota to report. The cost is page reads, one per film. Cut films
(a film split into several parts) are recognised on the site's side and handled as their own case
instead of being mistaken for a broken entry.

## 8. Where it fails and how we answer

- **No film id** — the leading film record is missing or unreadable (`not-found`): the run skips the
  candidate with that reason and keeps going with the other sources (F-M507).
- **No TMDb record** — without a film record on our side there is no leading identifier, so nothing is
  asked (F-M507).
- **The film record does not answer** — a timeout; the run continues with the other sources instead of
  failing as a whole.
- **Language not carried** — a language refusal before anything is read on the site (F-M735).
- **Upload accepted, id unknown** — accepted with an unknown identifier: the engine passes the answer
  text through and invents no id.

## 9. The boundary

No plugin database, no identifier shape of ours, no row of our storage; the engine receives
connection, archive cache, logger and settings from outside and writes nothing itself (F-M682). The
pipeline names it nowhere. The capabilities say what it can do, and the answer carries a proposal
with evidence. Its film resolution uses the media identifiers the pipeline hands in, never a lookup
into our store.

## 10. What it cannot do

- No season packs: a series never reaches this source, and asking for a pack there is refused with a
  reason instead of being attempted.
- No report path: a broken entry cannot be reported here.
- No hearing-impaired marking and no quota answer.
- No account: nothing of ours can be listed or withdrawn on this site.
