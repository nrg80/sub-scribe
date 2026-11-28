# The settings page

One settings page, one tab per concern, no second place for the same setting (F-M18, F-M53). The page is the only surface the operator works
on: it switches the directions, picks the libraries, collects the credentials and shows what the last runs did. Everything it displays comes
from the plugin, and no readout is computed in the page. The normative rules stand in §13 (page and readouts) and §22 (fields per source),
the log wording in `LOGGING.md`, the data model in `DATABASE.md`.

## The tabs

| Tab | Carries |
|---|---|
| General | the libraries, the manual run buttons and the statistics reset (F-M18, F-M53); the Connections block and the refresh control right after the settings, then the Workers list and finally the statistics with their reset button (F-M757) |
| Upload, Download | the switches of their own direction, and only those (F-M479); no provider tab of its own |
| Expert | the lists edited by hand: the skip directories and the file-name patterns, one entry per line in a textarea, defaults pre-filled (F-M36) |

Every section begins with an intro block that says what the section does. Both controls at the end of the General tab share one footprint:
full width, same grey (F-M758). No measurement appears on the page (F-M299).

## The required fields

Four inputs are required, each marked once, in parentheses after the field name, together with where the value comes from (F-M222). The
marker reads `Libraries (opt-in, Required)` and `TMDb (Required)` in the accent colour `#00a4dc`, without extra spacing (F-M228).

| Field | Where it stands | What its text says |
|---|---|---|
| Libraries (F-M213) | the page's own form, as the opt-in list of Jellyfin libraries | opt-in and required: nothing is processed until a library is picked — `Libraries to work in. Empty = no run.` (F-M230) |
| SubDL API key (F-M497, F-M688) | the source card; the engine declares it a secret field and marks it required in its schema | a free key from the account panel, documented at `subdl.com/api-doc`; a field may carry one line of at most 80 characters (F-M745) |
| TMDb API key (F-M203) | the source card, as any secret field of a card (F-M753) | `Free API key after registration at themoviedb.org.`, the address as the link word; the stored key is never written into it, it shows the same dot pattern, and an empty field means leave it as it is (F-M755) |
| Target languages (F-M42) | the page's own form | — |

## The switches

One switch per source and direction; four of them replace the two old global switches, and the old switch stays the fallback for a source
without one of its own (F-M477). A switch whose source lacks a built-in capability is visible, switched off, and says why, because hiding it
would suggest the capability exists (F-M478). The page shows the effective state, not the stored field: an unset field appears ticked when
its default is on (F-M503), a stored `false` stays off, the tvst download is on unset and the tvst upload stays off. The quality-gate switch
names its own action, and the two directions do not share a wording (F-M385).

## The fields of a source

A source declares its own fields and the page renders them: `text`, `secret`, `number`, `switch`, `choice` and `url`. A `secret` field shows
its state, never its value, and `VisibleWhen` shows a field only while another field holds the expected value (F-M222 with §22).

| Source | Fields |
|---|---|
| `subdl` | `apiKey` (secret, required), `account` (text, visible while a key is stored — the login the key belongs to), `baseUrl` (URL), `preferSeasonZip` (switch *Prefer season pack*, on, with a help line as on every card — F-M754) |
| `tvsubtitles` | `baseUrl` (URL); the site demands no access data |
| `moviesubtitles` | `baseUrl` (URL); the access field fell out, it was empty from the start |

A source that cannot take part shows a dead lamp on its tab: filled green when it takes part, hollow grey when not (F-M744). The state
belongs where the choice between sources is made, and no card repeats it as a word. The endpoint's own `participates` answers, so a source
without access data, or with both directions closed, never looks like a running one; the hint under the name says what is missing, and grey
marks the state, not red (F-M228).

The three cards stand behind one tab per source, not stacked one below the other (F-M743), each tab carrying the abbreviation `SubDL`,
`tvst` or `mst` with the full name in its tooltip and the card head (F-M746). Exactly one card is visible, the first tab open when the page
is drawn, and the strip carries the source name and a dot once the source is configured, so one glance says which sources run. Switching
hides the other cards but keeps them in the document, with their values, their `VisibleWhen`, their own `Save` and their result line. The
endpoint order stands under the `Endpoints` heading, above the strip (F-M698, F-M703). The values live in one file per source beside the
plugin data, not in the page and not in the plugin database, and the addresses come from the clients' constants, never from a second
hand-typed copy (§22).

## The worker rows

Seven rows stand in the Workers block of the General tab, one per worker: the display name, the moment the run began, and one outcome word
with its colour. The answer behind them also carries the run's one-line detail, and the row never prints it. The rows are read from the
plugin database, not from memory, so a Jellyfin restart neither resets them nor turns a run of two minutes ago into "never run" (F-M621); a
worker that never ran reads `never` with an empty start (F-M365, F-M366).

| Row | What it owns | Who writes it |
| --- | --- | --- |
| `Seeder` | the scan of the selected libraries and the queue it fills | the event dispatcher, before each direction runs |
| `Download` | the download run's own work | the download run, from the moment its queue is prepared |
| `Auto-Sync` | the alignment of the fetched files onto their audio | the download run that feeds it |
| `Upload` | the upload run's own work | the upload run, from the moment its queue is prepared |
| `Upl. Postproc.` | moving stored upload ids to the published entry ids | the postprocessing task |
| `Database` | checking the stored state against the filesystem | the database refresh task |
| `OSHash` | recomputing expired or changed hashes | the hash refresh task |

The order and the names live in one list in the registry, so the section is complete before any worker has run, and a rename reaches rows of
an older build without a migration. A row whose key is gone from that list is dropped.

## One row, one result

Every worker reports only itself (F-M322, F-M328): the download row says what the download did, the upload row what the upload did, and the
seeder's counts stay on the seeder's row. A direction row is written by that direction's own run, start to finish, and the waiting task
marks nothing. A direction must never light up while the seeder is still scanning, because the light means work in flight and the scan is
the seeder's work (F-M289). One cycle started at 16:16, the seeder scanned until 17:12, and the page read `ok`, because the waiting task had
reported success for work that was not its own. A full scan of 748 items took 56 minutes on the measured host, and that hour belongs to the
seeder's row and to no other. A direction that did nothing gets no row at all — switched off, allowance spent, or a cycle that ended before
it began — and the log carries the line instead (F-M365, F-M366).

## Who marks a row and who closes it

A row turns `run` when its own work begins, not when the cycle does: the seeder just before its scan, a direction once its queue is
prepared, the alignment when a download run with its switch on opens it. `Finish` closes it with the end stamp and the outcome, keeping the
start stamp it finds, so the duration is the run's real one. The start writes whether the run is dry, so the grey `dry` stands beside the
word while it runs (F-M398, F-M401). A cancellation stamps both ends with the moment and reads `cancel` in the same grey as `skip`
(`#767676`), since nothing went wrong either way (F-M353, F-M409). Every write is wrapped, so a failing record never breaks the run.

Only `Finish` and the cancellation close a row, and both need the living process, so a restart in mid-run left `run` without an end while
the stop button answered that no cycle was active. The start sweeps every open row, writes the moment it was found as the end, `failed` as
the outcome and `process ended mid-run (closed at the next start)` as the detail; it is no cancellation, because nobody pressed a button
(F-M621, F-M293).

## A pending fire and a deferral

A recovery fire is one shot per direction, armed whenever that direction has to come back later — a quota stop, an overload, a busy lock, a
library scan in the way — and it survives a restart in its own file (F-M65, F-M116). While one is pending, the row reads `defer` in yellow
whichever outcome it stored, including `failed`, because the retry is what happens next; only `run` is stronger (F-M436). The moment and
cause stand on the line under the list: the worker's name, the server's wording and `next attempt` with day and time, and the armed
postprocessing has its own line there (F-M445). An elapsed deadline is not a pending fire, so row and line fall back to the record (F-M368).

A job that meets a busy run lock does not wait: it returns at once and reschedules in job-spacing steps (F-M152), and the two maintenance
rows write `deferred` with `lock busy — rescheduled`. The cycle owns no row, so its deferral carries its own due moment, reason and attempt
against the limit on the cycle's line below the list (F-M623); the reason belongs to the moment, so a fresh deferral cannot wear the
previous run's cause (F-M475).

## The readouts

One page-wide `Refresh status` button at the end of the General tab reloads every live readout at once (F-M270). It stands below the workers
list, never next to Save, is no form action, and no tab carries a second refresh button. Its click handler is the only writer of the refresh
timestamp, and it names the manual act: `Manually refreshed <time>`, from the click moment in the viewer's timezone, with seconds (F-M271).
The resolution is the feedback: without the seconds a refresh inside the same minute looks like nothing happened. A GUI call to an endpoint
that answers `application/json` declares `dataType: "json"` (F-M216), or the Jellyfin client buffers the answer as text and the readout
stays empty.

## Colour and wording

The accent colour `#00a4dc` marks required fields, the marker in a heading and links. That marker in a heading is the same statement, only
larger, and needs its own style rule, or it takes the heading's text colour and reads as just another word (F-M228, F-M229). Red stays
reserved for destructive and failing states, because a required marker in red competes with a real warning. One state, one word: the page
uses the word of the log and the statistics, with no synonym and no second spelling (`LOGGING.md`, rule 3.7). A switch or button names the
act, not the thing it touches (F-M385).

A help line may carry one address as a link, and the source names both the address and the word that becomes the link (F-M745). The page
builds the anchor from document nodes, so no markup crosses the source boundary, and the line keeps its 80 characters, the address counting
as its visible word and not as the URL. The access fields point at the provider: the API key line names registration, the email line the
login credentials, the password field stays without one.

One line per switch, with no reason: `Waits for the daily quota reset, then carries on.` stands on both continue switches,
`Takes the season pack instead of single episodes.` on the pack switch, and the machine-translation switch names the account it concerns.

## The plugin database on the page

The page exposes two actions on the plugin database, `Reset` and `Restore` (F-M90); nothing else touches the file from the GUI. `Reset`
creates a timestamped copy and then starts from an empty database, `Restore` takes a copy back. Reset is the only path to an empty plugin
database: structural changes are never migrated from the page, and the state is rebuilt from the media files. Only one backup survives a
reset: the older copies are deleted and the copy that reset created remains (F-M181).

## What the page never shows

No credential value stands in the list, only its state. The eye at the right edge asks for the stored value one field at a time, holds it
only while the button is pressed, and the field keeps its width (F-M748); both API keys and the password carry it. No measurement and no
path of the operator appears on the settings page (F-M299). No setting exists in two controls, which is a defect and not a convenience
(F-M479). No live value stands on the Expert, Upload or Download tab beyond the switches; the live readouts live in the General tab
(F-M270).

## The checks

The suite `scripts/tests/gui-structure` reads the built page and fails when a section, a field, a label or the required marker drifts. 35
cases are bound to their entries in `docs/TESTS.md`, and `scripts/tests/counts.json` holds the binding.

## Who may call the page's routes

Every route the page calls needs a signed-in user, and the routes that write (stopping and resuming,
resetting state or one path, adopting uploads, writing a source) need the administrator role — the
page runs in the administrator's session and carries the token on EVERY request — through
`ApiClient.ajax` or the one `subdlAuthHeaders` helper, never a bare `fetch()`, which would answer 401 and
show `failed` in all lights (F-M807) — so nothing changes for the operator
(F-M805, checked by T449). The address never decides: a policy on the connection address is worthless
behind a reverse proxy, because every request then arrives from a local address.

## Where the rules live

The endpoint is `Plugins/SubdlSync/WorkerRuns`, and the registry holds the seven keys, the outcome words and the single write path. The due
times behind the fires stand in [RUNS.md](RUNS.md), the six statuses of the alignment in [AUTOSYNC.md](AUTOSYNC.md). The worker
requirements are F-M65, F-M116, F-M152, F-M268, F-M289, F-M293, F-M294, F-M322, F-M328, F-M340, F-M353, F-M359, F-M365, F-M366, F-M368,
F-M401, F-M436, F-M445, F-M475, F-M621 and F-M623.
