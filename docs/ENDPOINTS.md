# Endpoints — two sources, one path

How another source is attached, in detail: the catalogue, the marks, the order. The checklist and its pitfalls stand normatively in §21 of `SPECIFICATION.md`, the boundary in `CONTRACTS.md`.

Applies to the endpoints of the client (F-M483, F-M484).

**The order:** „2 download endpoints and 2 upload endpoints, and leave the rest of the plugin
as it is."

So there are four endpoints, one per source and direction:

| Direction | SubDL | tvsubtitles.net |
|---|---|---|
| Download | `ScribeDownloadEndpoint` — **wired** | `TvSubtitlesDownloadEndpoint` — in place, **not wired** |
| Upload | `ScribeUploadEndpoint` — **wired** | `TvSubtitlesUploadEndpoint` — in place, **not wired** |

## Contents

- [The seam](#the-seam)
- [The catalog](#the-catalog)
- [What the endpoints do](#what-the-endpoints-do)
- [Checks](#checks)
- [Origin in the plugin database and no cross-contamination (F-M486/F-M487)](#origin-in-the-plugin-database-and-no-cross-contamination-f-m486f-m487)
- [Four marks, one per source and direction (F-M488)](#four-marks-one-per-source-and-direction-f-m488)
- [Order and notion of success (F-M491, F-M494)](#order-and-notion-of-success-f-m491-f-m494)
- [„Uploaded" per source (F-M490)](#uploaded-per-source-f-m490)
- [No re-up, per source (F-M489)](#no-re-up-per-source-f-m489)
- [Upload: both endpoints one after the other (F-M495)](#upload-both-endpoints-one-after-the-other-f-m495)
- [What comes after this paper](#what-comes-after-this-paper)

## The seam

```csharp
public interface IDownloadEndpoint
{
    SubtitleProvider Provider { get; }
    bool Wired { get; }
    Task<IReadOnlyList<SubtitleHit>> SearchAsync(SubtitleQuery query, CancellationToken ct);
    Task<SubtitlePayload?> FetchAsync(SubtitleHit hit, CancellationToken ct);
}

public interface IUploadEndpoint
{
    SubtitleProvider Provider { get; }
    bool Wired { get; }
    Task<EndpointAnswer> SendAsync(UploadTicket ticket, CancellationToken ct);
}
```

`SubtitleQuery`, `SubtitleHit`, `SubtitlePayload`, `UploadTicket` and `EndpointAnswer` are neutral:
no SubDL fields, no tvsubtitles fields. What a source cannot do is stated by **its** endpoint with
a reason — not by the caller through guessing.

**The rest of the plugin stays as it is.** `DownloadPipeline`, `UploadPipeline`, the plugin database, QA gates,
the scheduler and the GUI layout are untouched; the SubDL endpoint is a thin translation around exactly
the calls the pipeline made itself before. The seam claims no wiring that does not
exist — see `Wired`.

## The catalog

```csharp
var catalog = new SubtitleEndpointCatalog(downloads, uploads);
catalog.AllDownloads            // all four, whether switched on or not
catalog.EnabledDownloads(config) // switch ON and Wired
catalog.Refused(config, SubtitleDirection.Download) // what does not run, with reason
```

`Enabled…` requires **both**: the switch from F-M477 **and** `Wired`. That is why
tvsubtitles.net stays out even with the switch on, as long as the run does not call it.

`Refused` delivers the sentence that the configuration page also shows under the switch — one statement,
not three. If an endpoint is on `Wired = false`, it reads:

> tvsubtitles.net — endpoints are in place (fetch and upload), but not yet wired into the run:
> the switch has no effect yet.

## What the endpoints do

**`ScribeDownloadEndpoint`** calls `SearchSubtitlesAsync` (one page) and `DownloadSubtitleFileAsync`;
`Map(SubtitleCandidate)` is a pure function and is checked by the suite without network. SubDL has
no rip cell — the hit invents none, the rip is in the release name.

**`ScribeUploadEndpoint`** covers the **single upload**. It refuses with a reason instead of sending halfway:
season pack (the pipeline builds that, F-M372), missing IMDb identifier (`no-imdb`), **bytes that are not
UTF-8** — SubDL takes text, and reading a cp1250 file as UTF-8 breaks Hungarian
(F-M482). Without a session a readable rejection comes back, not an exception.

**`TvSubtitlesDownloadEndpoint`** resolves the series via the **name** (the source has no
id index), walks the episode pages of the season until one says `Staffel x Folge`, and filters the
entries down to the wanted language. The language mapping (`SiteLanguage`) is the only place that knows
both vocabularies (`de → de`, `pt → pt`, `xx → null`).

**`TvSubtitlesUploadEndpoint`** builds the form (F-M481) and sends multipart. Dry run is the
default and is set from outside (the plugin's `DryRun`), not read internally — that keeps the
endpoint checkable. Season pack, unknown language, missing series name and a series the site does
not carry: one reason each, no send attempt.

## Checks

Suite `scripts/tests/endpoints` (T204, **28 checks**, no network):

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 <dotnet>/dotnet run \
  --project scripts/tests/endpoints/endpoints.csproj -c Release -- <repo>
```

It drives the tvsubtitles side over a faked HTTP handler against the real pages from
`scripts/tests/tvsubtitles/fixtures` — from the series list to the zip — and checks the SubDL side
through its pure mapping and its rejections.

## Origin in the plugin database and no cross-contamination (F-M486/F-M487)

Every subtitle has carried two fields in the plugin database since schema 10 (`sidecars` **and** `embeds`):

| Field | Content |
|---|---|
| `Origin` | `subdl` or `tvsubtitles` |
| `OriginId` | the **source's own id** — tvsubtitles subtitle id (e.g. `170678`) or SubDL entry id |

**Is a tvst find unambiguously identifiable?** Yes. The id names the entry on the site, the
content hash names the text, both together are unambiguous — the id is stored in the plugin database, not only
in the log. `null` means „this row says nothing" (old row, hand copy, embedded track) and is never
guessed; a writer without knowledge does not delete an existing statement.

**The cross rule** (`Provenance.MayUploadTo`, one place, not two):

- Origin **known** → only the **own** source takes the upload. A tvst find never goes to SubDL,
  a SubDL find never to tvst.
- Origin **unknown** → every endpoint allows it. Otherwise the operator could no longer
  upload their own files.
- The rejection lands in the log: `cross-source: subtitle came from tvsubtitles, not uploading it to subdl`.

The upload reads the origin before choosing the endpoint, via the content hash (`Registry.GetOrigin`), the
download writes it along when saving (`Provenance.Name(cand.Provider)` plus the source's id).

## Four marks, one per source and direction (F-M488)

| Field | When it is filled | Where the value comes from |
|---|---|---|
| `ScribeDownloadId` | Download from SubDL | the SubDL entry id of the hit |
| `ScribeUploadId` | Upload to SubDL | the id from the upload's answer |
| `TvSubtitlesDownloadId` | Download from tvsubtitles.net | the subtitle id of the site (e.g. `170678`) |
| `TvSubtitlesUploadId` | Upload to tvsubtitles.net | **read back** from the episode page; if missing, an **own** id (`own:…`) |

Nothing new was needed for SubDL — the id is already in the answer, it is only additionally written into its
own field. tvsubtitles.net answers the upload **without** an id, which is why it is read from the episode
page afterwards (language and release must match). **If the read-back finds nothing,
the plugin assigns an own id:** `own:<quelle>:<Zeitstempel>:<8 Hex>`. The prefix makes it
unmistakable against an id of the site, and the plugin database can thereby say „this upload happened,
its target is our own mark" instead of „unknown". Only the field of the source that
really took the upload is set.

Schema 11, in `sidecars` **and** `embeds`, added by `ALTER TABLE`; only what is known is written, and an
empty value deletes no existing statement.

## Order and notion of success (F-M491, F-M494)

Under Downloads the order is set: **tvsubtitles.net first** (default) or **SubDL first**. Both
endpoints are **asked in that order**, and their candidates for the same language are
**merged** — the second endpoint gets its own attempt even if the first delivered a hit,
and does not displace it. The ranking leads with the source in the configured
order; behind that, pack and text score decide as before. The abort „no other candidate can change this
rejection" applies **per source**: as long as a candidate of the other source is untried,
the search continues for it (and counts its rejection budget anew). Before the start of every endpoint — search,
main pass, SDH variant, fetch — the switch of the **own source** is additionally checked
(`SourceAllowed`); a candidate of a switched-off source is skipped, not the run ended.
Ranking, gates, fit, files area and origin are the same code for both orders.

**Successfully downloaded** means, with Auto-Sync on: the version has **passed
significance** (`alignedForNaming`). A kept original version is **downloaded, not successful**
(`DownloadedWithoutSignificance`) — the file is there, the pair stays open (F-M452). With Auto-Sync off
there is no fit and therefore no difference: every fetch counts.

## „Uploaded" per source (F-M490)

Two own timestamps, `ScribeUploadedAt` and `TvSubtitlesUploadedAt` (schema 12, in `sidecars` **and**
`embeds`). A point in time instead of a switch — „uploaded" without „when" answers half the question,
and one row can carry **both** sources: in March to SubDL, in November to tvsubtitles.

Only the timestamp of the source that really took the upload is set; a timestamp is
never overwritten with an empty value. `IsUploadedTo` reads the timestamp first, then the source's mark
(F-M488) — a row with an id but without a timestamp (schema 11 old data) still counts as
uploaded. F-M489 therefore no longer depends on whether an id was present.

## No re-up, per source (F-M489)

„Already uploaded" is read from the **source's own mark**, not from `Status=Uploaded`
(that only means „uploaded somewhere"). Hence: no re-up from SubDL to SubDL, no re-up from tvst to tvst —
and still no source blocks the other.

The decision falls in one place, `UploadGate.Decide(Schalter, Herkunft, hat-SubDL-ihn, hat-tvst-ihn)`,
and is used in three places: collection step for embedded tracks, collection step for loose subtitles, the upload
itself. Reasons in the log: `already-uploaded:subdl`, `already-uploaded:tvsubtitles`,
`already-uploaded:subdl+tvsubtitles`, `cross-source`, `no-upload-source`.

Old rows without their own mark count as uploaded for SubDL if `ScribeId` **and**
`Status=Uploaded` are present — otherwise every old upload would go out a second time after the upgrade.

## Upload: both endpoints one after the other (F-M495)

The upload no longer chooses its target at the start, but walks a **chain**: source A is tried,
then source B — in the configured download order, a separate upload order does not exist.
**Before the start of every endpoint** its toggle is checked (`SourceAllowed`); off means: skip this source
and name it in the log, not end the chain. The two gates remain and decide **per
source**: „this source already has it" via its own id/timestamp fields, and the origin via
`Provenance.MayUploadTo` — known means only to the own source, unknown means anywhere; a forbidden target
is skipped with `cross-source` or `already-uploaded:<Quelle>`. An error at A does not prevent B
(priority always wins). Afterwards **both** fields carry their mark, as far as the respective source took
it — before it was exactly one.

## What comes after this paper

Setting `Wired = true` for tvsubtitles means: the pipelines call the catalog instead of the SubDL client.
That is the wiring into the run — deliberately not done yet, because the order was „leave the rest
as it is."
