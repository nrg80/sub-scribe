# Engines — one paper per source

Three sources are attached today, and each has its own paper here:

| Paper | Source | Short name |
| --- | --- | --- |
| `subdl.md` | subdl.com | `subdl` |
| `tvsubtitles.md` | tvsubtitles.net | `tvst` |
| `moviesubtitles.md` | moviesubtitles.org | `moviesubtitles` (key stands in) |

All three follow one skeleton: what it is; which contracts it answers; the account ledger; capabilities;
languages; how we talk to it; what it costs and what it limits; where it fails and how we answer; the
boundary; what it cannot do.

## The rule these papers follow

Every statement carries its `F-M` tag, because the requirements state the rule, `../CONTRACTS.md` states
the boundary and `../TERMS.md` states the words; a paper that contradicts them is wrong and gets fixed,
not defended. They are written in English in the words of `../TERMS.md`: pack, season, episode, track,
mark, identifier, handle, verdict, capability, inquiry, language mapping, supported languages, language
refusal.

## What never appears

The papers are published with the repository, so they carry the public surface of a site and our
behaviour towards it. They carry no credential, no key, no token, no cookie and no password, no account
name, no e-mail address and no account identifier. No film or series title, path, file name or count of a
library appears, and no metadata from an operator's library, no operator host and no operator path. A
field name of a source's configuration (`baseUrl`, `preferSeasonZip`, `apiKey`) may be named, a field
value never, and a placeholder stands in as `<repo>`, `<dotnet>`, `<library>` or `<scratch>`. NF-10 / T403
reads every document under `docs/` and the README, and fails on a credential value, a provider key shape,
a hex string of 32 characters or more, a foreign e-mail address, a private host address, an IBAN or an
absolute operator path.
