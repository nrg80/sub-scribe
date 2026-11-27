# Sub Scribe

Sub Scribe keeps your Jellyfin library subtitled. It works with three subtitle sites —
[SubDL](https://subdl.com), [tvsubtitles.net](https://www.tvsubtitles.net) and
[moviesubtitles.org](https://www.moviesubtitles.org). It **downloads** the subtitles that are missing for
the languages and libraries you pick, **synchronizes them to the audio track**, and **uploads** the
subtitle tracks that already sit inside your own media files. Every source has its own download and
upload switch, and you decide in which order the sources are asked.

Written by an **AI agent** under the maintainer's direction.

Licensed under **GPL-3.0-or-later** — see [LICENSE](LICENSE).

## What it looks like

The general tab is the whole page in one piece. It shows the libraries you have opted into, the target
languages, the refetch interval, quota and connection status. Below that comes the endpoints section,
with the order in which the sources are asked and one card per source: its direction switches, what it
serves and its credentials. At the bottom stand the statistics and the last run of every worker. The
e-mail address in the SubDL card is blurred here; the plugin keeps it in that source's own file.

![The Sub Scribe settings page](docs/images/config-general.png?v=12.3.8.63)

The download tab says what gets fetched, when a run happens, and which quality gates a subtitle has to
pass before it is saved.

![Download settings](docs/images/config-download.png?v=12.3.8.63)

The upload tab has the same shape, with its own gates. It is off by default.

![Upload settings](docs/images/config-upload.png?v=12.3.8.63)

## Requirements

- Jellyfin **12.1.x** (targetAbi `12.1.0.0`)
- A **TMDb API key** — the only mandatory credential
- Optional, per source: a [SubDL](https://subdl.com) account (API key for downloads, username and
  password for uploads). Without it, SubDL is skipped and the remaining source keeps running.

## Installation

### Via the plugin catalog

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Add this repository URL:

   ```
   https://raw.githubusercontent.com/nrg80/sub-scribe/main/manifest.json
   ```

3. Open **Dashboard → Plugins → All**, find **Sub Scribe** and install it.
4. Restart Jellyfin.

### Manual

1. Download the ZIP of the latest release from the [releases page](https://github.com/nrg80/sub-scribe/releases).
2. Create a folder for the plugin under your Jellyfin `plugins/` directory and extract the ZIP into it:

   ```
   /var/lib/jellyfin/plugins/Sub Scribe/
   ├── Jellyfin.Plugin.SubdlSync.dll
   ├── LanguageDetection.dll
   ├── build.yaml
   └── licenses/
       ├── THIRD-PARTY.txt
       ├── LanguageDetection-Apache-2.0.txt
       └── Sub-Scribe-GPL-3.0.txt
   ```

3. Restart Jellyfin.

## Quick start

Open **Dashboard → Plugins → Sub Scribe** and fill in:

1. **TMDb API key** — required, everything else is optional.
2. **Libraries** to scan.
3. **Target languages** for the download side.
4. **Source switches** — one download and one upload switch per source.
5. **Download order** — which source is asked first; the second one gets its own attempt.
6. **SubDL API key** and **SubDL username/password** — *required for SubDL only*. Without them that
   source simply drops out of the chain.

Save.

## Upload (off by default)

The upload side sends the subtitle tracks that are embedded in your media files. You turn it on under
**Dashboard → Plugins → Sub Scribe → Upload → Upload enabled**. Both endpoints are then tried in turn,
in the source order you configured. A source is skipped — and the next one still runs — when it is
switched off, not configured, already holding that subtitle, or not allowed to take it. A subtitle that
came out of a download never travels to another source. The quality gates and the duplicate protection
work here exactly as they do on the download side.

## Build

```bash
./build.sh    # reads the version from build.yaml, writes out/*.zip
```

Releasing is a single command. It builds, verifies the artifact, tags, creates the release and checks
that exactly one asset was uploaded:

```bash
scripts/release.sh --dry-run   # build + verify, touch nothing
scripts/release.sh             # the real thing
```

Releases come from `main`, and the repository URL above serves that line. So what the catalog offers is
always what is on `main`.

Do not release by tagging and pushing alone: a release must carry exactly one asset. See
[docs/RELEASE.md](docs/RELEASE.md).

## Documentation

- How the documentation is written: [docs/STYLE.md](docs/STYLE.md)
- Specification and acceptance criteria: [docs/SPECIFICATION.md](docs/SPECIFICATION.md)
- The test catalogue, one entry per test: [docs/TESTS.md](docs/TESTS.md)
- The settings page as the operator meets it: [docs/GUI.md](docs/GUI.md)
- How a season pack is formed, and when it may be formed: [docs/SEASON-PACK.md](docs/SEASON-PACK.md)
- How a single upload is formed: [docs/SINGLE-UPLOAD.md](docs/SINGLE-UPLOAD.md)
- How a season pack download runs: [docs/SEASON-PACK-DOWNLOAD.md](docs/SEASON-PACK-DOWNLOAD.md)
- How a single download runs: [docs/SINGLE-DOWNLOAD.md](docs/SINGLE-DOWNLOAD.md)
- How the two subtitle sources work together (marks, completion, parallel download and upload): [docs/MULTI-SOURCE.md](docs/MULTI-SOURCE.md)
- Release process: [docs/RELEASE.md](docs/RELEASE.md)
- The plugin on one page, written for a person or an assistant answering one:
  [docs/briefings/short.md](docs/briefings/short.md).
- Was das Plugin tut, in einfachen deutschen Worten:
  [docs/briefings/wie-ein-untertitel-laeuft.md](docs/briefings/wie-ein-untertitel-laeuft.md).
