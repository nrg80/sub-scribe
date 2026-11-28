# Releasing Sub Scribe

From the version bump to the published asset.

## The release

```bash
# 1. bump the version in build.yaml, the csproj and docs/SPECIFICATION.md
# 2. write the changelog entry at the top of build.yaml's changelog block
# 3. build, verify, tag, release and check the result
scripts/release.sh
```

`scripts/release.sh --dry-run` builds and verifies without committing, tagging or uploading.

`scripts/release.sh <version> --fast` is for a change that only touches the settings page or the papers. It diffs against the newest release tag and runs the checks that can see such a change — `spec-doc`, `gui-structure`, readability, doc-hygiene — while the forty code suites and the full metric run stand still (measured: 30 s instead of about five minutes). The lane is narrow on purpose: the moment a file outside `configPage.html`, `docs/`, `README.md`, `build.yaml` and `manifest.json` appears in the diff, the script aborts and demands the full gate list. It never skips the artifact check, the tag or the publish steps.

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

`develop` is the repository's default branch, and every release there is a prerelease, so a build cannot
be handed out as a finished one by accident. The channel is derived from the branch rather than from a
flag, so it cannot drift from where it was cut, and the tag name always says which channel a ZIP came
from; `main` publishes the stable line, see [Branches](#branches).

The script aborts when the tree is dirty, when the branch is neither `develop` nor `main`, when the
version argument does not match `build.yaml`, when that branch is not pushed, when the tag already exists,
or when `build.yaml` and the csproj disagree.

## What the script verifies

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

The order is manifest commit and push, then tag, then release, so the release never points at a tag the
server does not know.

## Catalog manifest

`manifest.json` is committed on the branch the release runs on and read by Jellyfin from
`https://raw.githubusercontent.com/nrg80/sub-scribe/main/manifest.json` for the released line, or from
the same path on `develop` for the prerelease line. The script writes the new version, the release URL
and the md5 of the uploaded ZIP, then commits and pushes the manifest before creating the release, and
the manifest carries exactly one version entry.

## Branches

`develop` carries the prereleases (`v<ver>-dev`), and the two test instances subscribe to it; `main`
carries the released line (`v<ver>`) and its catalog URL is the one the README hands to users. Both
branches exist and each serves its own version — neither is a promotion of the other — because
`scripts/release.sh` writes and pushes the manifest to the branch it runs on, so a release is cut on the
branch whose channel is meant.

Not every version reaches `main`, only the ones picked for it: `develop` releases freely and often, while
`main` moves deliberately, one chosen version at a time, and skipping versions is normal (`main` went from
`12.1.12.169` straight to `12.1.12.175`). Nothing carries `main` along automatically: no CI workflow
touches it, no scheduled job runs `release.sh`, and the script pushes only the branch it runs on.

`main` was absent for part of a day. It was deleted after serving exactly one release, `12.1.12.169`,
and restored the same day for `12.1.12.175`. It came back by re-creating it at the tip it had
(`a12b8b4d`, a docs-only commit) and merging `develop` into it, not by pointing it at `develop`. A
fast-forward would carry `develop`'s `manifest.json` along, and a stable-looking branch would serve the
prerelease URL — the one mistake this paper exists to prevent.

A docs-only change that has to appear on `main` carries the same trap, because `git branch -f main
develop` takes `develop`'s `manifest.json` with it. Take the code and docs from `develop` but keep
`main`'s own manifest:

```bash
git checkout main
git checkout develop -- README.md docs/RELEASE.md   # the content you want
git checkout main -- manifest.json                  # keep main's released manifest
git commit -m "docs: …" && git push origin main
```

Whenever `main` is touched, check the manifest it serves, which is the cheap thing that catches this
class of mistake:

```bash
curl -sS https://raw.githubusercontent.com/nrg80/sub-scribe/main/manifest.json \
  | python3 -c "import json,sys; m=json.load(sys.stdin); v=m[0]['versions'][0]; print(v['version'], v['sourceUrl'])"
# must print a released version and a tag WITHOUT -dev
```

Never rehearse the release flow against a cloned copy without cutting the API off: `REPO_SLUG` is
hardcoded to `nrg80/sub-scribe` and the token is read from a secrets file outside the repository, so a
`release.sh` run in a throwaway clone creates real releases on GitHub. In a sandbox run it found the
existing release and tried to re-upload its asset; GitHub rejected the duplicate name and the asset
stayed intact, but that was luck, not containment. For a rehearsal use `--dry-run`, or point the clone's
`origin` at a local bare repository and still expect the API calls to reach GitHub; a real test release
must go to a different `REPO_SLUG`.

## CI

`build.yaml` and `test.yaml` compile and test every push to `develop`, and `scan-codeql.yaml` runs weekly
and on push; none of them publishes a release.

## Installing

Dashboard → plugins → all → Sub Scribe → install → restart Jellyfin. Versions older than the current one
are installable from their release page only.

## Deploying to the test bench

`scripts/deploy-jf-test.sh <version> <zip>` stops the test Jellyfin, removes every other
`Sub Scribe_*` folder (one GUID — two copies would both load), unpacks the ZIP, writes `meta.json` and
starts the instance again, and the order is mandatory. Replacing the DLL while Jellyfin runs leaves the
process holding a file that no longer matches the loaded assembly, and .NET compiles methods lazily, so a
method first reached during SHUTDOWN is read off disk and the runtime dies with
`BadImageFormatException: Bad IL range`.

The package is verified before anything is removed, and the extraction after it, because a deploy that
cannot unpack must not leave an install holding nothing but the logo while reporting success. `unzip` is
not on this host's PATH, so the extraction failed silently, the old version had already been deleted, and
Jellyfin answered 503 on a deploy that printed no error. The script now falls back to Python's `zipfile`
and aborts with `extract failed — no DLL in <path>` if the assembly is still missing. A negative control
is part of the change: a ZIP without the plugin assembly must abort before the running instance is
touched.

`AssemblyVersion` is bumped by hand, because `release.sh` verifies only the `<Version>` element against
`build.yaml`; a manual bump that misses `<AssemblyVersion>`/`<FileVersion>` ships a package whose
`meta.json` says the new version while Jellyfin's log reports the old assembly version. Bump all three
together.

The readiness line of the script can end on `HTTP 000` while the instance is fine: the loop breaks on the
first 200 and re-probes once more, and a slow start makes that second probe time out. Verify with your own
call; the script printed `bereit nach ~3s` with `HTTP 000`, and the same endpoint answered 200 eight
seconds later.
