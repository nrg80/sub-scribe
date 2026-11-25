# Engines — one whitepaper per source

Three sources are attached today. Each has its own paper here, and all three follow the same
skeleton so they can be read side by side:

| Paper | Source | Short name |
| --- | --- | --- |
| `subdl.md` | subdl.com | `subdl` |
| `tvsubtitles.md` | tvsubtitles.net | `tvst` |
| `moviesubtitles.md` | moviesubtitles.org | `moviesubtitles` (key stands in) |

## The skeleton — identical in all three papers

1. **What it is** — the site in one paragraph: what it carries, how one talks to it, what its
   allowance model is.
2. **Which contracts it answers** — job contract (search, fetch, send, report), account-ledger
   contract, inquiry contract.
3. **The account ledger** — the account's own record: the two jobs, the two handles of a published
   submission, the coarser state, and the four outcomes of withdrawing. A source without an account is
   absent from it rather than answering "I cannot".
4. **Capabilities** — upload, report, season packs, title creation, quota, hearing impaired, short
   name.
5. **Languages** — the language mapping and the statement about supported languages.
6. **How we talk to it** — the paths and what each answer carries.
7. **What it costs and what it limits** — allowance and throttling, and what happens at the limit.
8. **Where it fails and how we answer** — the known failure shapes, each with our reaction.
9. **The boundary** — what this engine never does.
10. **What it cannot do** — the open points.

## The rule these papers follow

- **They are derived, not sovereign.** Every statement carries its `F-M` tag. The requirements state
  the rule, `../CONTRACTS.md` states the boundary, `../TERMS.md` states the words. A paper that
  contradicts them is wrong and gets fixed, not defended.
- **English, in the words of `../TERMS.md`** — pack, season, episode, track, mark, identifier,
  handle, verdict, capability, inquiry, language mapping, supported languages, language refusal.

## What never appears in these papers

The papers are published with the repository, so they carry the public surface of a site and our
behaviour towards it, and nothing that belongs to an operator or an account:

- no credentials, no API keys, no access tokens, no cookies, no password;
- no account name, no e-mail address, no account identifier of any kind;
- no library data: no film or series titles from the library, no paths, no file names, no counts of
  a library;
- no metadata dump: no identifiers from an operator's library, no TMDb or IMDb values of it;
- no operator host, no operator path, no local directory.

**Field names of a source file may be named** (`baseUrl`, `preferSeasonZip`, `apiKey`) because they
are the configuration surface; a field value never appears. Where an example is needed, a placeholder
stands in: `<repo>`, `<dotnet>`, `<library>`, `<scratch>`.

The rule is checked, not promised. **NF-10 / T403** reads every document under `docs/` and the
README, and fails on a credential value, a provider key shape, a hex string of 32 characters or more,
a foreign e-mail address, a private host address, an IBAN, or an absolute operator path.
