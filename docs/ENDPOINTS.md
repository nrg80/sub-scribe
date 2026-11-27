# Endpoints — two sources, one path

Four endpoints carry one per source and direction, and the rest of the plugin stays as it is; this paper applies to the endpoints of the client. The checklist and its pitfalls stand normatively in §21 of `SPECIFICATION.md`, the boundary in `CONTRACTS.md`.

| Direction | SubDL | tvsubtitles.net |
|---|---|---|
| Download | `ScribeDownloadEndpoint` — **wired** | `TvSubtitlesDownloadEndpoint` — in place, **not wired** |
| Upload | `ScribeUploadEndpoint` — **wired** | `TvSubtitlesUploadEndpoint` — in place, **not wired** |

`DownloadPipeline`, `UploadPipeline`, the plugin database, QA gates, the scheduler and the GUI layout are untouched. The SubDL endpoint is a thin translation around exactly the calls the pipeline made itself before, and the seam claims no wiring that does not exist — see `Wired`.

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

`SubtitleQuery`, `SubtitleHit`, `SubtitlePayload`, `UploadTicket` and `EndpointAnswer` are neutral: no SubDL fields, no tvsubtitles fields. What a source cannot do is stated by its own endpoint with a reason, not by the caller through guessing.

## The catalog

```csharp
var catalog = new SubtitleEndpointCatalog(downloads, uploads);
catalog.AllDownloads            // all four, whether switched on or not
catalog.EnabledDownloads(config) // switch ON and Wired
catalog.Refused(config, SubtitleDirection.Download) // what does not run, with reason
```

`Enabled…` requires both, the switch and `Wired`, which is why tvsubtitles.net stays out even with the switch on as long as the run does not call it. `Refused` delivers the sentence the configuration page shows under the switch — one statement, not three. An endpoint on `Wired = false` reads: `tvsubtitles.net — endpoints are in place (fetch and upload), but not yet wired into the run: the switch has no effect yet.`

## ScribeDownloadEndpoint

It calls `SearchSubtitlesAsync` for one page and `DownloadSubtitleFileAsync`. `Map(SubtitleCandidate)` is a pure function and the suite checks it without network. SubDL has no rip cell, so the hit invents none and the rip stays in the release name.

## ScribeUploadEndpoint

It covers the single upload and refuses with a reason instead of sending halfway: season pack, because the pipeline builds that; missing IMDb identifier as `no-imdb`; bytes that are not UTF-8, because SubDL takes text and reading a cp1250 file as UTF-8 breaks Hungarian. Without a session a readable rejection comes back, not an exception.

## TvSubtitlesDownloadEndpoint

It resolves the series via the name, because the source has no id index, walks the episode pages of the season until one says `Staffel x Folge`, and filters the entries down to the wanted language. `SiteLanguage` is the only place that knows both vocabularies: `de → de`, `pt → pt`, `xx → null`.

## TvSubtitlesUploadEndpoint

It builds the form and sends multipart. Dry run is the default and is set from outside, from the plugin's `DryRun`, not read internally, which keeps the endpoint checkable. Season pack, unknown language, missing series name and a series the site does not carry: one reason each, no send attempt.

## Origin in the plugin database

| Field | Content |
|---|---|
| `Origin` | `subdl` or `tvsubtitles` |
| `OriginId` | the source's own id — tvsubtitles subtitle id (e.g. `170678`) or SubDL entry id |

Both fields have been carried since schema 10, in `sidecars` and in `embeds`. A tvst find is unambiguously identifiable: the id names the entry on the site and the content hash names the text, so both together are unambiguous and stored in the plugin database, not only in the log. `null` means "this row says nothing" — old row, hand copy, embedded track — and is never guessed, because a writer without knowledge does not delete an existing statement.

## A find is never uploaded (F-M487)

`Provenance.MayUploadTo` holds the cross rule in one place. A known origin allows only the own source, so a tvst find never goes to SubDL and a SubDL find never to tvst. An unknown origin allows every endpoint, otherwise the operator could no longer upload their own files. The rejection lands in the log as `cross-source: subtitle came from tvsubtitles, not uploading it to subdl`. The upload reads the origin before choosing the endpoint, via the content hash in `Registry.GetOrigin`, and the download writes it when saving with `Provenance.Name(cand.Provider)` plus the source's id.

## Four marks, one per source and direction (F-M488)

| Field | When it is filled | Where the value comes from |
|---|---|---|
| `ScribeDownloadId` | Download from SubDL | the SubDL entry id of the hit |
| `ScribeUploadId` | Upload to SubDL | the id from the upload's answer |
| `TvSubtitlesDownloadId` | Download from tvsubtitles.net | the subtitle id of the site (e.g. `170678`) |
| `TvSubtitlesUploadId` | Upload to tvsubtitles.net | **read back** from the episode page; if missing, an **own** id (`own:…`) |

Nothing new was needed for SubDL, whose answer already carries the id; it is only written into its own field as well. tvsubtitles.net answers the upload without an id, so the plugin reads it from the episode page afterwards, where language and release must match. If the read-back finds nothing, the plugin assigns an own id, `own:<quelle>:<Zeitstempel>:<8 Hex>`; the prefix makes it unmistakable against an id of the site, and the row then says "this upload happened, its target is our own mark" instead of "unknown". Only the field of the source that really took the upload is set. Schema 11 added the fields by `ALTER TABLE` in `sidecars` and `embeds`; only what is known is written, and an empty value deletes no existing statement.

## Order

Under Downloads the order is set: tvsubtitles.net first as the default, or SubDL first. Both endpoints are asked in that order, and their candidates for the same language are merged: the second endpoint gets its own attempt even if the first delivered a hit, and displaces nothing. The ranking leads with the source in the configured order, and pack and text score decide behind it. The abort "no other candidate can change this rejection" applies per source, so the search continues while a candidate of the other source is untried and counts its rejection budget anew. Before search, main pass, SDH variant and fetch, the switch of the own source is checked as `SourceAllowed`, and a candidate of a switched-off source is skipped instead of ending the run. Ranking, gates, fit, files area and origin are the same code for both orders.

## What counts as downloaded

With Auto-Sync on, a download counts as successful when the version passed significance (`alignedForNaming`). A kept original version is downloaded, not successful (`DownloadedWithoutSignificance`): the file is there and the pair stays open. With Auto-Sync off there is no fit and therefore no difference, so every fetch counts.

## Uploaded per source

Two own timestamps, `ScribeUploadedAt` and `TvSubtitlesUploadedAt`, in `sidecars` and `embeds` since schema 12. A point in time instead of a switch, because "uploaded" without "when" answers half the question, and one row can carry both sources: in March to SubDL, in November to tvsubtitles. Only the timestamp of the source that really took the upload is set, and no timestamp is overwritten with an empty value. `IsUploadedTo` reads the timestamp first and the source's mark second, so a row with an id but without a timestamp from schema 11 still counts as uploaded.

## No re-up, per source

"Already uploaded" is read from the source's own mark, not from `Status=Uploaded`, which only says "uploaded somewhere": no re-up from SubDL to SubDL, none from tvst to tvst, and still no source blocks the other. `UploadGate.Decide(Schalter, Herkunft, hat-SubDL-ihn, hat-tvst-ihn)` falls in one place and is used in three: the collection step for embedded tracks, the collection step for loose subtitles and the upload itself. The log names the reason as `already-uploaded:subdl`, `already-uploaded:tvsubtitles`, `already-uploaded:subdl+tvsubtitles`, `cross-source` or `no-upload-source`. Old rows without their own mark count as uploaded for SubDL when `ScribeId` and `Status=Uploaded` are present, otherwise every old upload would go out a second time after the upgrade.

## Upload: both endpoints in a chain

The upload no longer chooses its target at the start but walks a chain: source A is tried, then source B, in the configured download order, and a separate upload order does not exist. Before every endpoint its toggle is checked; off means skip this source and name it in the log, not end the chain. Two gates decide per source: "this source already has it" via its own id and timestamp fields, and the origin via `Provenance.MayUploadTo`, where known means the own source only and unknown means anywhere, so a forbidden target is skipped as `cross-source` or `already-uploaded:<Quelle>`. An error at A does not prevent B, because priority always wins. Afterwards both fields carry their mark, as far as the respective source took the upload; before, exactly one did.

## What is not wired yet

`Wired = true` for tvsubtitles means the pipelines call the catalog instead of the SubDL client, which wires it into the run. That is deliberately not done yet, because the order was "leave the rest as it is."

## Checks

Suite `scripts/tests/endpoints`, 28 checks, no network:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 <dotnet>/dotnet run \
  --project scripts/tests/endpoints/endpoints.csproj -c Release -- <repo>
```

It drives the tvsubtitles side over a faked HTTP handler against the real pages from `scripts/tests/tvsubtitles/fixtures`, from the series list to the ZIP, and checks the SubDL side through its pure mapping and its rejections.

## Where the rules live

[SPECIFICATION.md](SPECIFICATION.md), mainly F-M372, F-M452, F-M477, F-M481, F-M482, F-M483, F-M484, F-M486, F-M487, F-M488, F-M489, F-M490, F-M491, F-M494 and F-M495; T204 runs in `endpoints`.
