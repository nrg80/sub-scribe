# subdl.com — the API source

subdl.com carries film and series subtitles in many languages, including machine-translated ones, and it
is the only one of the three sources that offers an API instead of a page to read. The contract view of
the same source stands in [engines/subdl.md](engines/subdl.md).

## What it is

The site answers on `api.subdl.com` and hands files out from `dl.subdl.com` (F-M682). A search returns
candidates, each with an opaque handle, a language, a release name and whether it belongs to a pack. A
fetch returns the bytes as a ZIP, either one subtitle or the whole season, together with a file name, the
site's own identifier and a content hash. Films and series both live here, so this is the source that
serves a film when the other two have nothing.

## The switches

The card carries four switches: `Download` (on by default), `Upload` (off by default), `Packs` and
`Report` (both on by default). `Packs` is what puts the source into a season-pack slot and allows a season to go up as one ZIP.
`Prefer season pack` sits on the same card under that switch, but it works in the other direction: it
makes a download ask for an archive before a single. `Report` allows a broken entry to be reported back
to the site.

## The account

subdl.com is the only source with an account we can read, and therefore the only one that answers the
ledger contract (F-M729). One call lists what the account holds — our own submissions and the published
entries — and one call pulls a single submission back. A submission is withdrawn and sent again, never
edited. Two number spaces have to be kept apart: the upload id of a submission and the entry id of the
published subtitle are different numbers (F-M435).

## Languages

The site publishes no list of the languages it accepts, so there is nothing to hold a language against
(F-M735). What the plugin asks is only which identifier this source uses for a language it already
knows, and that answer is a two-letter upper-case code in the source's own spelling. A language the site
does not carry comes back as an empty answer, never as a refusal of the mapping.

## What it costs

The free account has a daily number of searches and a daily number of downloads, and both are read live
rather than assumed (F-M733). At the limit the site answers `api_download_limit_exceeded` with the time
it resets, and three shapes are told apart because they need three different reactions: the daily
allowance is spent, the site is briefly overloaded, or a rate header arrives inside the answer (F-M238).
One switch per direction decides whether a run continues after the limit.

## When it says no

A spent download allowance stops the run asking this source and leaves the other sources working. A rate
limit makes the login state say so in plain text instead of reporting a wrong password. A candidate
without an acceptable subtitle for the track is rejected before the fetch, so the refusal is not counted
as a failure. An answer to a send is one of four things — accepted, rejected, busy or failed — and a
refused pack comes back with a reason, not with an exception (F-M685).

## What it cannot do

It creates no title, it cannot edit a submission, it cannot publish a draft, and it cannot say why
somebody else rejected one; the plugin passes the site's own text through as written. It also cannot name
a closed list of languages, so a language refusal by this source never comes from a mapping table. It
answers questions about the allowance but not about its own health: how the site is doing is only what
the address and the login state say (F-M730).

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M238, F-M435, F-M682, F-M685, F-M729, F-M730, F-M733,
F-M735 and F-M799. Guarded by `sources`, `endpoints` and `archivecache`.
