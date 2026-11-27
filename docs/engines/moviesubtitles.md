# moviesubtitles.org — the film source

The site's own material — paths, tables, peculiarities — stands in `../MOVIESUBTITLES.md`.

## 1. What it is

moviesubtitles.org carries subtitles for films only; whether a medium of ours is a film or a series is
decided by TMDb, not by the file name (F-M507). It is page-based like tvsubtitles, has no API and keeps
no account we can read. Its contract is the smallest of the three because of what it does not have: no
season packs, no report path, no quota.

## 2. Which contracts it answers

It answers three of the four jobs — search, fetch and send — and has no report path, so the report job
is not answered and the source does not pretend it could be (F-M682, F-M685). It answers no
account-ledger contract, because no account is read (F-M729). It answers the inquiry contract with one
light, the address itself, and no quota (F-M730).

## 3. The account ledger

The source is absent from the ledger list rather than answering "cannot", because an absent source is
never asked while a source that answered "cannot" would have to be remembered as a refusal (F-M729).
Nothing here can be listed, nothing withdrawn, and no state of our own submissions is known from this
side.

## 4. Capabilities

It uploads; it does not report, does not take season packs (neither offered nor accepted), takes no
hearing-impaired mark and answers no quota. Title creation is yes: the engine looks up a film the site
does not know and creates it there (F-M734). No short name is set, so the key `moviesubtitles` stands in
(F-M731).

## 5. Languages

Supported languages are a deliberately short closed list: only what the site actually carries is
accepted. A language outside it is a language refusal the engine decides before it sends anything, and
nothing is uploaded in its place (F-M735).

## 6. How we talk to it

The engine resolves a film record first — the site's own film identifier, derived from the film's
identifiers and its year — and without it there is nothing to ask. It reads the subtitle entries of that
film from the film page, with their language and their file, and a download returns the file itself. An
upload answers with an HTTP status, and when the status is a success the site's own identifier of our
filing may still be unreadable; the engine passes the answer text through unchanged rather than
inventing one (F-M685).

## 7. What it costs and what it limits

There is no allowance to read and no quota to report; the cost is page reads, one per film. Cut films — a
film split into several parts — are recognised on the site's side and handled as their own case instead
of being mistaken for a broken entry.

## 8. Where it fails and how we answer

A missing or unreadable leading film record (`not-found`) skips the candidate with that reason and the
run keeps going with the other sources (F-M507). Without a film record on our side there is no leading
identifier, so nothing is asked (F-M507). A film record that does not answer is a timeout, and the run
continues with the other sources instead of failing as a whole. A language the site does not carry is a
refusal before anything is read on the site (F-M735). An upload accepted with an unknown identifier
passes the answer text through.

## 9. The boundary

No plugin database, no identifier shape of ours, no row of our storage: the engine receives connection,
archive cache, logger and settings from outside and writes nothing itself (F-M682). The pipeline names
it nowhere, reads the capabilities as the answer and receives a proposal with evidence. Its film
resolution uses the media identifiers the pipeline hands in, never a lookup into our store.

## 10. What it cannot do

It takes no season pack: a series never reaches this source, and a request for a pack is refused with a
reason instead of attempted. It has no report path, so a broken entry cannot be reported here. It marks
no hearing-impaired track, answers no quota and knows no account, so nothing of ours can be listed or
withdrawn on this site.
