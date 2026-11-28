# Releasing Sub Scribe

From the version bump to the published asset.

## The release

A release is one version, one channel and one asset. The version stands in `build.yaml`, in the csproj and in
`docs/SPECIFICATION.md`, and the changelog carries one line for the change. `scripts/release.sh` builds,
verifies, tags and publishes it, and it reads the version from `build.yaml` alone: an argument that disagrees
aborts the run. `--dry-run` builds and verifies without committing, tagging or uploading. `--fast` serves a
change that only touches the settings page or the papers: it runs spec-doc, gui-structure, readability and
doc-hygiene, leaves the forty code suites standing, and still builds, tags and publishes. Measured, that is
30 seconds instead of five minutes, and it aborts the moment a file outside `configPage.html`, `docs/`,
`README.md`, `build.yaml` and `manifest.json` appears in the diff.

## The order

The script commits and pushes the manifest first, then sets the tag, then creates the release, then downloads
the asset and compares its md5 with the built one. The manifest carries the new version, the release URL and
that md5 as its single version entry. In this order the release never points at a tag the server does not
know, and exactly one asset ends up attached to it.

## What is checked before it publishes

- version: `build.yaml` == command argument; the csproj matches with or without the `-dev` suffix
- `category` is exactly `Subtitles`
- a changelog entry exists for this version
- the plugin description is identical in `build.yaml` and in the plugin card, is at most 260 characters,
  and names both required keys
- tree clean, branch is `develop` or `main`, and that branch is pushed
- the tag does not exist yet
- the ZIP carries the plugin DLL, the one dependency it ships (`LanguageDetection`) and `build.yaml`, and
  no `meta.json`
- version and category inside the ZIP, not in the working tree
- exactly one asset after upload
- the md5 of the downloaded asset equals the built one
- the **tagged** `build.yaml` carries the version and `Subtitles`

## The changelog entry is one line per change

One bullet per change, one line, no prose: the entry names what changed, not why it was hard, what was
measured on the way or which alternative was rejected. Several distinct changes in one version become
several bullets, still one line each, in the vocabulary the UI uses, and with no measurement tables, no
„calibrated on 46 files", no code paths and no commit-style prefixes. The reasoning and the rejected
variants belong in the requirement entry in `docs/SPECIFICATION.md`, where the next person looks when the
behaviour surprises them; long prose in the changelog is not read but skimmed.

## One branch, one channel

| branch | tag | release |
|---|---|---|
| `develop` | `v<ver>-dev` | prerelease |
| `main` | `v<ver>` | stable |

`develop` is the repository's default branch, and every release there is a prerelease, so a build cannot be
handed out as a finished one by accident. The channel comes from the branch rather than from a flag, so it
cannot drift from where the release was cut, and the tag name always says which channel a ZIP came from.
Jellyfin reads the manifest of the branch it subscribes to, `main` for users and `develop` for the test
instances. `develop` releases freely and often while `main` moves deliberately and skips versions as it
likes, and nothing carries `main` along: no CI workflow touches it, no scheduled job runs `release.sh`, and
the script pushes only the branch it runs on.

## The traps

- `main` serves the released line, so its `manifest.json` must stay its own: `git branch -f main develop`
  would hand the prerelease URL to stable users. Take the content from `develop` and keep `main`'s manifest.
- After any touch of `main`, read the manifest the branch actually serves and check that the version carries
  no `-dev`.

  ```bash
  curl -sS https://raw.githubusercontent.com/nrg80/sub-scribe/main/manifest.json \
    | python3 -c "import json,sys; m=json.load(sys.stdin); v=m[0]['versions'][0]; print(v['version'], v['sourceUrl'])"
  ```

- Never rehearse the flow on a clone: `REPO_SLUG` is fixed and the token lies outside the tree, so a
  rehearsal publishes for real. Use `--dry-run`, or point `REPO_SLUG` at a different repository.
- On the test instance the order is stop, verify the package, remove every other `Sub Scribe_*` folder,
  unpack, write `meta.json`, start. A DLL replaced under a running Jellyfin dies at shutdown with
  `BadImageFormatException: Bad IL range`, because .NET compiles methods lazily.
- Verify the extraction as well, and abort before removing anything when the assembly is missing: `unzip` is
  not on every host, so the fallback to Python's `zipfile` turns a silent failure into a named one. A ZIP
  without the plugin assembly is the negative control for it.
- All three version fields move together. `release.sh` compares only `<Version>`, so a missed
  `AssemblyVersion` ships a package whose `meta.json` promises more than the log reports.
- A readiness line ending on `HTTP 000` can belong to a healthy instance, because the loop re-probes once
  after the first 200 and a slow start times that probe out. Confirm with your own call.

## Installing

Dashboard → plugins → all → Sub Scribe → install → restart Jellyfin. Versions older than the current one are
installable from their release page only.
