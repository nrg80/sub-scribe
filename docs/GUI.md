# The GUI — the settings page and what it shows

*Derived document: the rules stand in §13 of `SPECIFICATION.md`, the per-source fields in §22. This paper
describes the page as the operator meets it — the tabs, the fields, the switches, the readouts, the
colours and the wording. Every statement carries its `F-M` tag; where the rule itself is meant, the
chapter is named. The words are those of `TERMS.md`.*

**Owner: the page.** What the operator sees and may press. The normative rules stand in §13 (page and
readouts) and §22 (fields per source); the wording of a log line stands in `LOGGING.md`, the data model
in `DATA-MODEL.md`.

## Contents

- [1. What it is](#1-what-it-is)
- [2. The tabs](#2-the-tabs)
- [3. The required fields](#3-the-required-fields)
- [4. The switches](#4-the-switches)
- [5. The fields of a source](#5-the-fields-of-a-source)
- [6. The readouts](#6-the-readouts)
- [7. Colour and wording](#7-colour-and-wording)
- [8. The plugin database on the page](#8-the-plugin-database-on-the-page)
- [9. What the page never shows](#9-what-the-page-never-shows)
- [10. The checks](#10-the-checks)

## 1. What it is

One settings page, one tab per concern, and no second place for the same setting (F-M18, F-M53). The
page is the only surface the operator works on: it switches the directions on and off, picks the
libraries, collects the credentials, and shows what the last runs did. Everything it displays comes from
the plugin — no readout is computed in the page itself.

## 2. The tabs

- **General** carries the libraries, the manual run buttons and the statistics reset (F-M18, F-M53).
- **The two direction tabs** — upload and download — carry the switches of their direction, and only
  those (F-M479). There is no provider tab of its own.
- **Expert** carries the lists that are edited by hand: the skip directories and the file-name patterns,
  one entry per line in a textarea, with the defaults pre-filled (F-M36).
- **Every section** begins with an intro block that says what the section does. Measurements never
  appear on the settings page (F-M299).

## 3. The required fields

Four inputs are required, and each is marked **once**, in parentheses after the field name, together
with where the value comes from (F-M222):

- **Libraries** — the opt-in list of Jellyfin libraries (F-M213). It is opt-in *and* required: nothing is
  processed until a library is picked, and the description says so in one line —
  `Libraries to work in. Empty = no run.` (F-M230).
- **The SubDL API key** — it stands in the source card, not in the page's own form: the engine declares
  it as a secret field and marks it required in its own schema (F-M222, F-M497, F-M688). Its short
  description says where the key comes from: a free key from the account panel, documented at
  `subdl.com/api-doc` (F-M745 — a field may carry one line of at most 80 characters).
- **The TMDb API key** (F-M203).
- **The target languages** (F-M42).

The marker reads `Libraries (opt-in, Required)` and `TMDb (Required)`. It is rendered in the accent
colour `#00a4dc`, without extra spacing (F-M222, F-M228). The same blue applies to the marker inside a
heading: it is the same statement, only larger, and without its own style rule a heading marker falls
back to the heading's text colour and reads as just another word. **Red is reserved** for destructive and
failing states — a required marker in red would compete with a real warning (F-M228). Links use the same
accent blue as the rest of the page (F-M229).

## 4. The switches

- **One switch per source and direction** (F-M477). Four switches replace the two old global ones, and
  the old switch remains the fallback for a source that has no switch of its own.
- **A switch whose source has no built-in capability is visible, switched off, and says why** (F-M478).
  Hiding it would suggest the capability exists.
- **The page shows the effective state**, not the stored field: an unset field is displayed as ticked
  when its default is on (F-M503).
- **The defaults answer only „never written"**: a stored `false` stays off and is overwritten by no
  default. The tvst download is on in the unset state, the tvst upload stays off (F-M503).
- **A quality-gate switch names its own action**, and the two directions do not share a wording
  (F-M385).

## 5. The fields of a source

A source declares its own fields, and the page renders what it declares (F-M222 with §22). The field
kinds are `text`, `secret`, `number`, `switch`, `choice` and `url`; a `secret` field shows its state but
never its value, and `VisibleWhen` shows a field only while another field holds the expected value.

| Source | Fields | Note |
|---|---|---|
| `subdl` | `apiKey` (secret, required), `account` (text, visible while a key is stored), `baseUrl` (URL), `preferSeasonZip` (switch, on) | the account is the login the key belongs to |
| `tvsubtitles` | `baseUrl` (URL) | the site demands no access data |
| `moviesubtitles` | `baseUrl` (URL) | the access field fell out — it was empty from the start |

A source that cannot take part reads **`off`** — in the card head and as a hollow dot on its tab (F-M744).
The word comes from the endpoint's own answer (`participates`), so a source without its access data, or one
whose directions are both closed, never looks like a running one; the hint under the name says what is
missing. The state is grey, not red — red stays reserved for destructive and failing states (F-M228).

The three cards stand behind **one tab per source**, not stacked one below the other (F-M743). Each tab carries the source's abbreviation — `SubDL`, `tvst`, `mst` (F-M746) — and the full name in its tooltip and in the card head. Exactly one card is visible; the first tab is open when the page is drawn. The strip
carries the source name and a dot when the source is configured, so one glance says which sources run at
all. Switching hides the other cards but keeps them in the document. A card's values, its `VisibleWhen`,
its own `Save` and its result line keep working. The endpoint order stands directly under the `Endpoints`
heading, above the strip (F-M698, F-M703).

The values live in **one file per source**, beside the plugin data — not in the page and not in the
plugin database (§22). The addresses come from the constants of the clients, never from a second
hand-typed copy.

## 6. The readouts

- **One page-wide „Refresh status" button**, at the end of the General tab, reloads every live readout at
  once (F-M270). It sits inside the general tab, directly below the workers list, and it is **not** a
  form action: it never stands next to Save, and no tab carries a second refresh button.
- **The refresh timestamp has exactly one writer** — the button's own click handler — and it names the
  manual act: `Manually refreshed <time>`, rendered from the click moment in the viewer's timezone, with
  seconds (F-M271). Resolution is the feedback: without seconds a refresh inside the same minute looks
  like nothing happened.
- **A worker row marks a dry run** with the word `dry`, in grey, directly next to the result word
  (F-M398). The same grey carries `cancel` (`#767676`); two shades of grey side by side claimed two
  different things (F-M409).
- **A GUI call to a plugin endpoint that answers `application/json` declares `dataType: "json"`**
  (F-M216) — without it the Jellyfin client buffers the answer as text and the readout stays empty.

## 7. Colour and wording

- The accent colour `#00a4dc` marks required fields, the marker in a heading and links; grey `#767676`
  marks `dry` and `cancel`; red is for destructive and failing states only (F-M228, F-M229, F-M409).
- **One state, one word** — the page uses the same word as the log and the statistics, with no synonym
  and no second spelling (`LOGGING.md`, rule 3.7).
- A switch or button names the act it performs, not the thing it touches (F-M385).

## 8. The plugin database on the page

The page exposes exactly two actions on the plugin database — **Reset** and **Restore** (F-M90). Nothing
else touches the file from the GUI.

- **Reset** creates a timestamped copy first, then starts from an empty plugin database.
- **Restore** takes a copy back.
- **Reset is the only path to an empty plugin database.** Structural changes are never migrated from the
  page; the state is rebuilt from the media files.
- **Single backup retention:** after a reset all older copies are deleted, and only the copy that reset
  created remains (F-M181).

## 9. What the page never shows

- **No credential value.** A secret field shows that it is set, never what it holds.
- **No measurement and no path of the operator** on the settings page (F-M299).
- **No second place for the same setting** — a setting that exists in two controls is a defect, not a
  convenience (F-M479).
- **No live value on the Expert, Upload or Download tab** beyond the switches: the live readouts live in
  the General tab (F-M270).

## 10. The checks

The suite `scripts/tests/gui-structure` holds the page's shape: it reads the built page and fails when a
section, a field, a label or the required marker drifts. 32 of its cases are bound to their own test
entries in `docs/TESTS.md` (`gui-structure`), and the binding of a test to its suite stands in
`scripts/tests/counts.json`.
