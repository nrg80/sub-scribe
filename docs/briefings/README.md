# Briefings — three sheets about Sub Scribe, for three different readers

Sub Scribe is described in a specification of several hundred pages. Nobody reads that to ask a
question. These three sheets exist so that a person — or an assistant answering a person — can start
from a correct picture in one page instead of in eight.

They are part of the repository on purpose. A public file has a public address, and an assistant with
a browser can read it without anyone copying text around.

## The three sheets

- **[users.md](users.md)** — for people who run Jellyfin and want subtitles handled. Plain words, no
  code, with the install path, what the plugin needs, what it deliberately does not do, and where to
  report a problem.
- **[developers.md](developers.md)** — for someone who is going to change the plugin: the engine
  contract, the settings model, the documentation and test system, the release and deployment steps,
  and the invariants that must not be broken.
- **[short.md](short.md)** — the one-paragraph version, for a chat instruction field, a forum answer
  or the first message of a conversation.

## Handing a sheet to an assistant

Any of these works, from most reliable to most convenient:

1. **Paste it** as the first message of a new chat, then ask your question. Nothing to set up.
2. **Upload the file**, if your client accepts attachments — a PDF or a plain-text copy of the same
   text is fine.
3. **Put it in an instruction field** (a custom assistant, a saved prompt). Use `short.md` there; the
   field is limited, and the long sheets do not belong in it.
4. **Give the address** — the raw link to the file in this repository — to an assistant that is
   allowed to browse. That is the only route in which the assistant fetches the sheet itself.

Whatever you pick: an assistant that knows this repository does not need the text. An assistant that
does not will invent plausible settings if it has to guess, which is why the sheets name the place in
the page instead of the name in the code.

## Keeping them true

These sheets are part of the release work, not decoration. When a released version changes something a
user can see — a switch, a help line, an install step — the affected sheet is corrected in the same
commit. The version named in each sheet is the version that was current when it was written; if that
number lags behind the release, the sheet is stale and should be treated as suspect rather than
quoted.
