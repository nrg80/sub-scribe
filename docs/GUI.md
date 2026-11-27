# The settings page

One settings page, one tab per concern, no second place for the same setting (F-M18, F-M53). The page is
the only surface the operator works on: it switches the directions, picks the libraries, collects the
credentials and shows what the last runs did. Everything it displays comes from the plugin, so no readout
is computed in the page itself. The normative rules stand in §13 (page and readouts) and §22 (fields per
source), the log wording in `LOGGING.md`, the data model in `DATA-MODEL.md`.

## The tabs

| Tab | Carries |
|---|---|
| General | the libraries, the manual run buttons and the statistics reset (F-M18, F-M53); at its end the Status block, the refresh control, then the statistics with their reset button (F-M757) |
| Upload, Download | the switches of their own direction, and only those (F-M479); no provider tab of its own |
| Expert | the lists edited by hand: the skip directories and the file-name patterns, one entry per line in a textarea, defaults pre-filled (F-M36) |

Every section begins with an intro block that says what the section does. Both controls at the end of the
General tab share one footprint: full width, same grey (F-M758). Measurements never appear on the page
(F-M299).

## The required fields

Four inputs are required, and each is marked once, in parentheses after the field name, together with
where the value comes from (F-M222). The marker reads `Libraries (opt-in, Required)` and `TMDb (Required)`
in the accent colour `#00a4dc`, without extra spacing (F-M228).

| Field | Where it stands | What its text says |
|---|---|---|
| Libraries (F-M213) | the page's own form, as the opt-in list of Jellyfin libraries | opt-in and required: nothing is processed until a library is picked — `Libraries to work in. Empty = no run.` (F-M230) |
| SubDL API key (F-M497, F-M688) | the source card; the engine declares it a secret field and marks it required in its schema | a free key from the account panel, documented at `subdl.com/api-doc`; a field may carry one line of at most 80 characters (F-M745) |
| TMDb API key (F-M203) | the source card, as any secret field of a card (F-M753) | `Free API key after registration at themoviedb.org.`, the address as the link word; the stored key is never written into it, it shows the same dot pattern, and an empty field means leave it as it is (F-M755) |
| Target languages (F-M42) | the page's own form | — |

## The switches

One switch per source and direction; four of them replace the two old global switches, and the old switch
stays the fallback for a source without one of its own (F-M477). A switch whose source lacks a built-in capability
is visible, switched off, and says why, because hiding it would suggest the capability exists (F-M478).
exists (F-M478). The page shows the effective state, not the stored field, so an unset field appears ticked when its default
is on (F-M503). A stored `false` stays off and no default overwrites it: the tvst download is on unset, the tvst upload
stays off.
quality-gate switch names its own action, and the two directions do not share a wording (F-M385).

## The fields of a source

A source declares its own fields and the page renders them: `text`, `secret`, `number`, `switch`, `choice`
and `url`. A `secret` field shows its state, never its value, and `VisibleWhen` shows a field only while
another field holds the expected value (F-M222 with §22).

| Source | Fields |
|---|---|
| `subdl` | `apiKey` (secret, required), `account` (text, visible while a key is stored — the login the key belongs to), `baseUrl` (URL), `preferSeasonZip` (switch *Prefer season pack*, on, with a help line as on every card — F-M754) |
| `tvsubtitles` | `baseUrl` (URL); the site demands no access data |
| `moviesubtitles` | `baseUrl` (URL); the access field fell out, it was empty from the start |

A source that cannot take part shows a dead lamp on its tab, one per source: filled green when it takes
part, hollow grey when not (F-M744). The state belongs where the choice between sources is made, and no
card repeats it as a word. The endpoint's own `participates` answers, so a source without access data, or
with both directions closed, never looks like a running one; the hint under the name says what is missing,
and grey marks the state, not red (F-M228).

The three cards stand behind one tab per source, not stacked one below the other (F-M743), each tab
carrying the abbreviation `SubDL`, `tvst` or `mst` with the full name in its tooltip and the card head
(F-M746). Exactly one card is visible, the first tab open when the page is drawn, and the strip carries the
source name and a dot once the source is configured, so one glance says which sources run at all. Switching hides the other cards but keeps them in the document, with their values, their
`VisibleWhen`, their own `Save` and their result line. The endpoint order stands under the `Endpoints`
heading, above the strip (F-M698, F-M703).

The values live in one file per source beside the plugin data, not in the page and not in the plugin
database, and the addresses come from the clients' constants, never from a second hand-typed copy (§22).

## The readouts

One page-wide `Refresh status` button at the end of the General tab reloads every live readout at once
(F-M270). It stands below the workers list, never next to Save, is no form action, and no tab carries a
second refresh button.

The button's click handler is the only writer of the refresh timestamp, and it names the manual act:
`Manually refreshed <time>`, from the click moment in the viewer's timezone, with seconds (F-M271).
The resolution is the feedback: without the seconds a refresh inside the same minute looks like nothing
happened.

A worker row marks a dry run with the word `dry`, in grey, next to the result word (F-M398), and the same
grey carries `cancel` (`#767676`), because two shades side by side claimed two different things (F-M409).
A GUI call to an endpoint that answers `application/json` declares `dataType: "json"` (F-M216), or the
Jellyfin client buffers the answer as text and the readout stays empty.

## Colour and wording

The accent colour `#00a4dc` marks required fields, the marker in a heading and links. That marker in a
heading is the same statement, only larger, and needs its own style rule, or it takes the heading's text
colour and reads as just another word (F-M228, F-M229). Red stays reserved for destructive and failing states, because a required marker in red competes
with a real warning.

One state, one word: the page uses the word of the log and the statistics, with no synonym and no second
spelling (`LOGGING.md`, rule 3.7). A switch or button names the act, not the thing it touches (F-M385).

A help line may carry one address as a link, and the source names both the address and the word that
becomes the link (F-M745). The page builds the anchor from document nodes, so no markup crosses the source
boundary, and the line keeps its 80 characters, the address counting as its visible word and not as the
URL. The access fields point at the provider: the API key line names registration, the email line the
login credentials, the password field stays without one.

One line per switch, with no reason: `Waits for the daily quota reset, then carries on.` stands on both
continue switches, `Takes the season pack instead of single episodes.` on the pack switch, and the
machine-translation switch names the account it concerns.

## The plugin database on the page

The page exposes two actions on the plugin database, `Reset` and `Restore` (F-M90); nothing else touches the
file from the GUI. `Reset` creates a timestamped copy and then starts from an empty database, `Restore`
takes a copy back.

Reset is the only path to an empty plugin database: structural changes are never migrated from the page,
and the state is rebuilt from the media files. Only one backup survives a reset: all older copies are
deleted, and only the copy that reset created remains (F-M181).

## What the page never shows

No credential value stands in the list, only its state. The eye at the right edge asks for the stored value
one field at a time, holds it only while the button is pressed, and the field keeps its width (F-M748);
both API keys and the password carry it.

No measurement and no path of the operator appears on the settings page (F-M299). No setting exists in two
controls, which is a defect and not a convenience (F-M479). No live value stands on the Expert, Upload or
Download tab beyond the switches; the live readouts live in the General tab (F-M270).

## The checks

The suite `scripts/tests/gui-structure` reads the built page and fails when a section, a field, a label or
the required marker drifts. 35 cases are bound to their entries in `docs/TESTS.md`, and
`scripts/tests/counts.json` holds the binding.
