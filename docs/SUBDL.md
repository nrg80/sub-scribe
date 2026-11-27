# subdl.com — the API source

subdl.com carries film and series subtitles in many languages, including machine-translated ones, and it is
the only one of the three sources that offers an API instead of a page to read. It answers on
`api.subdl.com` and hands files out from `dl.subdl.com` (F-M682). Films and series both live here, so this is
the source that serves a film when the other two have nothing.

## What a search returns

A search returns candidates, each with an opaque handle, a language, a release name and whether it belongs to
a pack. The question is put by title and by identifiers — never by file name or folder name. The handle is
opaque and is never parsed: it names one candidate, and it is the only thing a fetch asks for. A candidate
without an acceptable subtitle for the track is rejected before the fetch, so that refusal costs no request.

## What a fetch returns

A fetch returns the bytes as a ZIP, either one subtitle or the whole season, together with a file name, the
site's own identifier and a content hash. The archive cache sits in front of the call, so a season fetched
once is not paid for twice, and the engine receives that cache from outside instead of opening one of its own.

## The four jobs

| Job | Question | Answer of this source |
| --- | --- | --- |
| search | which subtitles do you have for this title? | candidates with a handle, a language, a release name, pack membership |
| fetch | give me this subtitle | bytes as a ZIP, a file name, the site's identifier, a content hash |
| send | take this subtitle, single or as a pack | accepted, rejected, busy or failed (F-M685) |
| report | this entry is broken | accepted, rejected, busy or failed (F-M685) |

It answers all four, and it is the only one of the three that answers the report job at all (F-M682). Sending a
single and sending a season are the same job with a different body: one subtitle, or one ZIP for one season in
one language and one variant.

## The pack as the site receives it

A pack is one ZIP for one season, one language and one variant, and the pipeline builds it before the engine
sees it. The site takes it through the same send call as a single and answers in the same vocabulary, so a
season is one request and not one request per episode. A pack that is refused comes back with a reason, and
the single file of the same language may still be sent afterwards.

## The report job

A broken entry is reported back to the site behind the `Report` switch, and the answer is one of the same four
outcomes (F-M685). The plugin passes the site's own text through unchanged, so a refusal reads as the site
wrote it. The report changes nothing in the plugin database: it is a statement about the site's entry, not a
verdict about a track.

## The title question inside a send

The send job carries the title question with it, and the answer says which of seven things happened: ready,
created, created without a readable identifier, the site already carries the title without an identifier, not
findable, not askable, or no verdict. Creating a title is no job of its own and no part of this contract
(F-M734): the two page-based sources create, this one does not, and a medium the site does not know stays not
askable instead of being invented. A dry run creates nothing and decides nothing, so a missing title there is
not askable and not a no.

## The switches

The card carries four switches: `Download` (on by default), `Upload` (off by default), `Packs` and `Report`
(both on by default). `Packs` is what puts the source into a season-pack slot and allows a season to go up as
one ZIP. `Prefer season pack` sits on the same card under that switch, but it works in the other direction: it
makes a download ask for an archive before a single. `Report` allows a broken entry to be reported back to the
site. A switch is a permission only, and the checks decide whether the request happens.

## The account ledger

subdl.com is the only source with an account we can read, and therefore the only one that answers the
account-ledger contract (F-M729). Two calls do three tasks: one reads what the account holds — our own
submissions or the published entries — and one pulls a single submission back. `Key` names the source in the
same value the plugin database carries, so no caller has to know which source it is talking to.

There is no third route and no edit. A submission is withdrawn and sent again, never patched, and a draft
cannot be published from here. Diagnosis walks the account pages at a page depth of one, through our own
submissions, and invents no path of its own.

## Two number spaces

The upload id of a submission and the entry id of the published subtitle are different numbers (F-M435). A
published submission carries the handle to withdraw it and, next to it, the handle of its published entry; an
open submission carries only the first. The state is coarser than any source — open, accepted, rejected, and
on a rejection the site's own plain text alongside — and what only one side knows does not cross the boundary
(F-M729). Withdrawing ends in the same four outcomes as sending, and the ledger answers no reachability and no
quota question (F-M730).

## Capabilities

| Capability | Answer of this source |
| --- | --- |
| short name | `subdl` (F-M731) |
| upload | yes |
| report | yes, behind the `Report` switch |
| season packs | yes, behind `Packs` and `Prefer season pack` |
| hearing-impaired marks | yes |
| quota | yes — the only source that answers it (F-M733) |
| title creation | no (F-M734) |

## Languages

The site publishes no list of the languages it accepts, so its enumeration is open without a list and it takes
what comes (F-M735). There is nothing to hold a language against, and an empty list would have been the wrong
claim: it would have meant the source can take nothing.

What the plugin asks is only which identifier this source uses for a language it already knows, and the answer
is a two-letter upper-case code in the source's own spelling. A language the site does not carry comes back as
an empty answer, never as a refusal of the mapping. The bridge translates in one direction only, and the
plugin database keeps the ISO spelling above every source.

## How we talk to it

| What | Answer |
| --- | --- |
| API base and download host | `api.subdl.com` and `dl.subdl.com` (F-M682) |
| a search | candidates with a handle, a language, a release name and pack membership |
| a fetch | bytes, a file name, the site's own identifier and a content hash |
| a send | one of the four outcomes: accepted, rejected, busy, failed (F-M685) |
| a report | one of the four outcomes, for an entry that is broken (F-M685) |
| the ledger | published entries with two number spaces, the entry id and the upload id (F-M435) |

A refused pack — unknown language, missing release name or non-UTF-8 body — is answered with a reason, not
with an exception, and the reason travels back as the site wrote it (F-M685).

## What a send refuses before it starts

Three things never leave the client. A season pack is refused because the pipeline builds that container
elsewhere, a missing IMDb identifier is refused as `no-imdb`, and bytes that are not UTF-8 are refused because
the site takes text: reading a cp1250 file as UTF-8 turns Hungarian into noise. Without a session the answer
is a readable rejection rather than an exception.

## What a hit does not carry

The site has no rip cell. The hit therefore invents none, and the rip stays where the site put it, inside the
release name. Whoever needs a rip value reads it there, and no source-invented field is written into the
plugin database.

## What it costs

The free account has a daily number of searches and a daily number of downloads, and both are read live
rather than assumed (F-M733). The allowance is two allowances: a run can be out of downloads while its
searches are still free, so each is judged on its own. A download spends the download number, a search spends
the search number, and a send spends the search number too, because an upload is a request like a search.

It is read, not counted. The plugin keeps no budget of its own; the client files the limit, the remainder and
the reset from the answer's rate headers away beside the numbers, and no other code reads them. One unit is
paid per archive and not per subtitle, so an archive already on disk pays nothing.

## Three refusal shapes

At the limit the site answers `api_download_limit_exceeded` with the time it resets. Three shapes are told
apart because they need three different reactions: the daily allowance is spent and the reset is known, the
site is briefly overloaded, or a rate header arrives inside the answer (F-M238). A spent allowance earns a
stop and one fire per direction, and the fire takes the site's own reset as its anchor when the answer carries
one.

## The hourly ceiling

The upload carries a ceiling of its own, five hundred subtitles in an hour, and that ceiling is not the daily
allowance. It arrives as a 200 with a refusal in the body rather than as a 429, and it takes the source out of
the run the same way a spent allowance does.

## When it says no

A spent download allowance stops the run asking this source and leaves the other sources working; the item
stays queued and the direction reports a deferral instead of a green run. A rate limit makes the login state
say so in plain text instead of reporting a wrong password. A candidate without an acceptable subtitle for the
track is rejected before the fetch, so the refusal is not counted as a failure. An answer to a send is one of
four things — accepted, rejected, busy or failed — and a refused pack comes back with a reason, not with an
exception (F-M685).

An identical file the site already holds comes back as `Duplicate upload: identical file already published`.
That is a rejection of one request and no verdict about the track, and the same file may still go to the other
source of the slot.

## What the answer leaves behind

Two marks belong to this source and nothing new was needed for either, because the site's answers carry them
(F-M488). The download mark is the entry id of the hit, the upload mark is the id from the upload's answer,
and a timestamp stands beside each mark, since „uploaded" without „when" answers half the question. Only the
field of the source that really took the upload is written, and an empty value never overwrites a statement
that is already there.

## A find from here is not sent there

An origin that is known allows the own source only, so a find from this source is never uploaded to the tvst
family and a find from the family never to this one (F-M487). The rule sits in one place and the upload reads
the origin before it chooses a target; a forbidden target is skipped with a line, not with an exception:

```
cross-source: subtitle came from tvsubtitles, not uploading it to subdl
```

## Where it stands in the order

The order setting ranks the family against this source, and both are asked in that order, each with its own
attempt: the second source gets its turn even when the first already delivered a hit, and it displaces
nothing. The ranking leads with the source order and lets pack and text score decide behind it, so a second
source never silently replaces the answer of the first.

## What it cannot do

It creates no title, it cannot edit a submission, it cannot publish a draft, and it cannot say why somebody
else rejected one; the plugin passes the site's own text through as written. It also cannot name a closed list
of languages, so a language refusal by this source never comes from a mapping table.

It answers questions about the allowance but not about its own health: how the site is doing is only what the
address and the login state say (F-M730). It reports no quota before a refusal, because it is asked only after
one, and it cannot tell a spent allowance from one it failed to read — both take the same road.

## The boundary

The engine knows no plugin database, no identifier shape of ours and no row of our storage. It receives its
network connection, its archive cache, a logger and its settings from outside and writes nothing itself
(F-M682). The pipeline names it nowhere: it asks the registry for the capabilities and reads the short name
`subdl` as a data value (F-M731).

The pipeline hands in the query — which list, how deep, from which water mark — and the engine answers with
entries plus the number of pages it spent. Everything it returns is a proposal with evidence: an identifier,
a state or a plain text, and only the pipeline decides what enters the plugin database.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M238, F-M435, F-M682, F-M685, F-M729, F-M730, F-M733, F-M735 and
F-M799. Guarded by `sources`, `endpoints` and `archivecache`.
