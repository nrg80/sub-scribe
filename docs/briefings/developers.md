# Sub Scribe for developers — brief on the state of the tree

Written for a new assistant or a new pair of hands that is going to change this plugin. It holds what
you need to be useful without first reading several hundred pages. The repository is
<https://github.com/nrg80/sub-scribe> (public, GPL-3.0); everything below was taken from the tree, not
from memory.

## Branches and versions

- `develop` — the line that runs in production. Every release here is a GitHub prerelease tagged
  `v<version>-dev`.
- `main` — the stable line. A release here is tagged `v<version>` and is what the plugin catalogue
  offers.

At the time of writing: `develop` = `12.3.8.60`, `main` = `12.3.8.60`, and the author's own Jellyfin
runs `12.3.8.60`. The plugin is roughly 137 C# files and 79,000 lines.

## The engine contract

A source is one subtitle site. The plugin reaches it only through `ISourceEngine`, which has exactly
**four methods**: `SearchAsync`, `FetchAsync`, `SendAsync`, `ReportAsync`. Beside them an engine
declares `Capabilities` and `Fields`.

- `SourceCapabilities` is asked, never inferred from a name: `Key`, `DisplayName`, `TabName`,
  `CanUpload`, `CanReport`, `CanHearingImpaired`, `CanOfferSeasonPacks`, `CanSeasonPacks`,
  `CanCreateTitle`, `CanQuota`.
- `Fields` is the engine's own schema, and the settings page draws the source cards from it. A new
  setting is therefore added in the engine, not in the page. Kinds: switch, text, secret, choice,
  number.
- Per-source settings live in one file per source under `<plugin data folder>/sources/<key>.json`
  (`uploadEnabled`, `downloadEnabled`, plus the declared fields). Precedence: source file → old plugin
  configuration → off.
- The boundary is strict: an engine never sees the plugin database, its identifiers or its entities.
  Neutral data crosses; nothing else.

Three sources exist: `subdl` (tab `SubDL`, TV and movies, upload, report, season packs, daily quota),
`tvsubtitles` (tab `tvst`, TV, upload) and `moviesubtitles` (tab `mst`, movies only, upload switch off
by default).

Around the engines the plugin runs a seeder, an upload pipeline and a download pipeline. Quality gates
stand before a subtitle is saved or sent. A content registry holds packs, files, tracks and sidecars
with random unchangeable identifiers, a SQLite store keeps them, and maintenance tasks refresh the
database and the file hashes.

## The settings page

One page, four tabs: **General, Download, Upload, Expert**. The General tab reads from the inside out:
libraries → target languages → TMDb key → endpoint order → one card per source → refetch interval →
media files → statistics → workers → status → refresh button. Rules, each of them deliberate:

- A stored secret is never written into its field. The field shows a fixed eight-dot placeholder, an
  eye appears only while a value is stored, and it shows the value for the length of the press. An
  empty field on save means *leave it alone*.
- A card field may carry one short help line, 80 characters at most — a place or a unit, never a
  reason. Fields on the other tabs keep the same habit; a field that needs two lines is the documented
  exception.
- State is shown as a traffic light on the tab, not as numbers scattered over the page.
- The card is the only place where a source is described; nothing about a source appears twice.

## Documentation system

`docs/SPECIFICATION.md` is the master: about 680 numbered requirement definitions with `F-M` ids in 23
chapters, referring to about 419 tests with `T` ids. Around it sit `TERMS.md` (one word per concept),
`CONTRACTS.md`, `DATA-MODEL.md`, `ENDPOINTS.md`, `GUI.md`, `LOGGING.md`, `TESTS.md`, `RELEASE.md`,
`AUTOSYNC.md`, `MULTI-SOURCE.md` and one paper per source under `docs/engines/`.

Rules for those papers, all measured rather than felt:

- **English only.** No German in the repository.
- **No credentials, no e-mail addresses, no API keys, no library names, no host paths.** Field names
  yes, field values never.
- Readability is enforced by a script: 45 words per sentence, 200 per paragraph, 400 per list block,
  at most one em dash per 200 words.
- An operator's demand becomes a `Concept decision:` sentence without a date; the literal demand
  survives as an HTML comment at the place in the code or page it produced.
- The specification states a rule, a constant and a reference — never an implementation name.

## Tests and gates

Twenty suites with roughly 2,261 individual checks, identified `T1` … `T419`. They are ordinary
programs under `scripts/tests/`; `scripts/tests/counts.json` declares which requirement is covered by
which suite with how many checks, and the `counts` suite compares that declaration against reality.

```
dotnet build Jellyfin.Plugin.SubdlSync/Jellyfin.Plugin.SubdlSync.csproj -c Release
export PluginDir=$PWD/Jellyfin.Plugin.SubdlSync/bin/Release/net10.0
python3 scripts/tests/counts/check.py $PWD
python3 scripts/tests/spec-doc/check.py .
python3 scripts/tests/gui-structure/check.py
python3 scripts/tests/readability/check.py docs/*.md docs/engines/*.md
python3 scripts/tests/doc-hygiene/check.py .
```

**Trap:** the suites load the plugin assembly from `$(PluginDir)`. Unset, they fall back to a different
checkout on the same machine and report failures that do not exist. Export it. A green run of all six
commands is the only state from which a release may start.

## Release and deployment

1. Bump the version in `build.yaml` (`version` and a one-line `changelog`), in the `.csproj` (`Version`,
   `AssemblyVersion`, `FileVersion`) and in the status line of the specification.
2. Commit and push the branch.
3. `PluginDir=<…>/bin/Release/net10.0 ./scripts/release.sh <version>` builds the zip, writes
   `manifest.json` and creates the GitHub release; the channel follows the branch. **`main` must be
   pushed first**, or the script aborts on purpose.
4. To install: refresh the plugin catalogue, install `Sub Scribe`, restart Jellyfin. Success is the
   plugin reporting the new version as `Active` with no pending restart.
5. A superseded stable release keeps its place until the new one is published; only then are its
   release and tag removed.

## Invariants — breaking one of these is a bug, not a preference

- The engine contract stays at four methods.
- Upload is off by default and is a per-source switch.
- Deletions on a source are irreversible: uploads are stopped before anything is withdrawn, and a bulk
  "clean up uploads" scope is never used as a shortcut.
- A secret never reaches a log line — not its length, not an excerpt; only `set` / `empty`.
- The plugin database and its identifiers never cross into an engine.
- Both directions are built symmetrically: what exists for download exists for upload.
- No fixed wall-clock times for the long-running work; the rhythm is derived and anchored once per
  installation at random.

## Where the work stands

Behind us, in the order it happened:

- the multi-source refactor: engines, one file per source, cards drawn from the schema;
- a log line for every written setting, never printing a secret;
- the TMDb key field moved onto the card pattern;
- the refetch interval placed directly above the media-files block;
- one name and one help line for the season-pack switch on every card;
- the runtime-tolerance field, which now says what it measures before it names its default and its
  floor. The project
screenshots were retaken for all three tabs with the upload switches off and both the e-mail address
and the server identifier obscured.

A known rough edge, verified in the code and not yet fixed: the scheduled upload task logs
`Upload inactive (UploadEnabled=false)` although its condition is "no source has upload enabled". The
message names one source's switch while the code asks about all of them.
