// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Language;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources.Subdl;

/// <summary>
/// F-M682: die Quelle SubDL hinter der Grenze. Dünner Adapter auf <see cref="ScribeApiClient"/> —
/// er übersetzt die neutralen Aufträge in die Aufrufe, die es schon gibt, und deren Antworten zurück.
/// Kein eigener Netzaufruf, kein eigener HttpClient, keine eigene Kennungsform nach außen.
/// <para>
/// Diese Quelle sucht über Kennungen (IMDb, TMDb), nicht über einen Namen: <c>SearchesByName</c> ist
/// deshalb falsch, und eine Anfrage ohne Kennung endet als
/// <see cref="SourceFault.NoSearchBase"/> statt in einer geratenen Suche.
/// </para>
/// </summary>
public sealed class SubdlEngine : ISourceEngine
{
    /// <summary>Der Schlüssel dieser Quelle — derselbe Wert, den der Bestand führt.</summary>
    public const string SourceKey = "subdl";

    /// <summary>Der Griff trägt seine Herkunft: <c>n:</c> + Eintrags-Kennung.</summary>
    private const string HandlePrefix = "n:";

    /// <summary>F-M450: die Seitenzahl-Obergrenze der Suche (Sicherheit, nicht Takt).</summary>
    private const int MaxSearchPages = 20;

    /// <summary>F-M683: der Schlüssel des API-Schlüssels in der eigenen Datei dieser Quelle.</summary>
    public const string FieldApiKey = "apiKey";

    /// <summary>F-M683: der Schlüssel des Kontonamens in der eigenen Datei dieser Quelle.</summary>
    public const string FieldAccount = "account";

    /// <summary>F-M683: der Schlüssel der Adresse in der eigenen Datei dieser Quelle.</summary>
    public const string FieldBaseUrl = "baseUrl";

    /// <summary>F-M683: der Schlüssel des Staffel-ZIP-Schalters in der eigenen Datei dieser Quelle.</summary>
    public const string FieldPreferSeasonZip = "preferSeasonZip";

    /// <summary>
    /// F-M683: die Felder, die DIESE Quelle erklärt — in der Reihenfolge, in der sie erscheinen.
    /// <para>
    /// Vier Felder, und jedes hat einen Grund: der Schlüssel ist Pflicht, weil ohne ihn weder Suche noch
    /// Abruf geht; der Kontoname gehört zur Anmeldung; die Adresse ist die, die dieser Build wirklich
    /// benutzt (<see cref="ScribeApiClient.ApiBase"/>, nicht eine abgetippte zweite Fassung); der
    /// Schalter ist die Betreiberansage vom 22.11.2026 („Staffel zips bevorzugen ist ebenfalls eine
    /// eigenschaft von subdl\") und steht auf AN, weil ein Staffelpack eine Folge je Lauf bedient und
    /// damit weniger Abrufe kostet als die Einzeldateien derselben Staffel.
    /// </para>
    /// <para>
    /// <c>account</c> ist nur sichtbar, solange ein Schlüssel abgelegt ist — ohne Schlüssel gibt es
    /// keine Anmeldung, zu der ein Konto gehören könnte (<see cref="SourceFieldVisibility"/>).
    /// </para>
    /// </summary>
    public static IReadOnlyList<SourceField> Declared { get; } = new SourceField[]
    {
        new(FieldApiKey, "API-Schlüssel", SourceFieldKind.Secret, Default: string.Empty, Required: true,
            Help: "Wird als Bearer-Token gesendet. Ohne Schlüssel sucht und lädt SubDL nicht."),
        new(FieldAccount, "Konto", SourceFieldKind.Text,
            Help: "Benutzername des SubDL-Kontos — er wird zur Anmeldung und für den Meldetext gebraucht.",
            VisibleWhen: new SourceFieldVisibility(FieldApiKey, "gesetzt")),
        new(FieldBaseUrl, "Adresse", SourceFieldKind.Url, Default: ScribeApiClient.ApiBase,
            Help: "Die API-Adresse. Dieser Build benutzt sie fest eingebaut; ein abweichender Wert wird gemeldet."),
        new(FieldPreferSeasonZip, "Staffel-ZIPs bevorzugen", SourceFieldKind.Switch, Default: "true",
            Help: "Bei gewünschtem Behälter zuerst die Staffel-ZIPs anbieten (weniger Abrufe je Staffel)."),
    };

    private readonly ScribeApiClient _client;
    private readonly SourceEngineContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlEngine"/> class.
    /// </summary>
    /// <param name="client">Der SubDL-Client des Laufs — er trägt seinen Zugang bereits.</param>
    /// <param name="context">Was das Plugin reicht (Logger, Zeitgeber, Archiv-Cache).</param>
    /// <param name="seasonPacks">Steht diese Quelle in diesem Lauf im Pack-Slot?</param>
    /// <param name="settings">
    /// F-M683: die Einstellungen aus der EIGENEN Datei dieser Quelle. Fehlt der Parameter, gelten die
    /// Vorgaben (kein Datenordner bekannt) — dann bleibt der Zugang des gereichten Clients maßgeblich.
    /// </param>
    public SubdlEngine(
        ScribeApiClient client, SourceEngineContext context, bool seasonPacks, SourceSettings? settings = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Settings = settings ?? SourceSettings.Empty(SourceKey, Declared);

        // F-M683: die Datei ist die Quelle der Wahrheit, sobald sie einen Schlüssel trägt. Die
        // Konfiguration hat ihn beim ersten Start EINMAL hineingeschrieben (SourceSettingsSeed); danach
        // liest die Engine hier. Ist die Datei kaputt, wird NICHTS übernommen — die Quelle gilt dann als
        // nicht eingerichtet und sagt das (SearchAsync), statt mit einem halben Zugang zu laufen.
        if (Settings.IsConfigured && Settings.Secret(FieldApiKey) is { Length: > 0 } schluessel)
        {
            _client.ApiKey = schluessel;
        }

        Account = Settings.Text(FieldAccount);
        BaseUrl = Settings.Url(FieldBaseUrl);
        PreferSeasonZip = Settings.Switch(FieldPreferSeasonZip);

        if (!string.Equals(BaseUrl, ScribeApiClient.ApiBase, StringComparison.OrdinalIgnoreCase))
        {
            // Die Adresse wird gelesen und ausgewiesen, aber die Clients dieses Builds tragen ihre
            // Adresse als Konstante. Ein anderer Wert ist deshalb eine Meldung und keine stille Lüge.
            _context.Logger.LogWarning(
                "[Sub Scribe] sources/subdl.json names the address {Address}; this build talks to {Built}.",
                BaseUrl,
                ScribeApiClient.ApiBase);
        }

        Capabilities = new SourceCapabilities
        {
            Key = SourceKey,
            DisplayName = "SubDL",
            CanUpload = true,
            CanReport = true,
            CanHearingImpaired = true,
            CanOfferSeasonPacks = true,
            CanSeasonPacks = seasonPacks,
            CanCreateTitle = false,
            CanQuota = true,
            SearchesByImdb = true,
            SearchesByTmdb = true,
            SearchesByName = false,
            SearchesByHash = false,

            // F-M72/F-M649: die Seite nennt ihre Grenze selbst — 500 Untertitel je Stunde. Einen
            // Mindestabstand nennt sie nicht; den hält der Takt des Laufs (GlobalRateLimiter).
            Pace = new SourcePace(UploadsPerHour: 500, MinCallPauseSeconds: 0),

            // F-M10/F-M557: die Sprachbrücke dieser Quelle steht in LanguageMapper, nicht hier.
            LanguageCode = LanguageMapper.MapToSubdl,
        };
    }

    /// <inheritdoc />
    public SourceCapabilities Capabilities { get; }

    /// <inheritdoc />
    public IReadOnlyList<SourceField> Fields => Declared;

    /// <summary>Gets F-M683: die Einstellungen, die diese Engine aus ihrer Datei gelesen hat (Vorgaben, wo nichts steht).</summary>
    public SourceSettings Settings { get; }

    /// <summary>Gets F-M683: der Kontoname aus der eigenen Datei (leer, wenn keiner eingetragen ist).</summary>
    public string Account { get; }

    /// <summary>Gets F-M683: die Adresse aus der eigenen Datei.</summary>
    public string BaseUrl { get; }

    /// <summary>Gets F-M683: Staffel-ZIPs bevorzugen (Vorgabe an).</summary>
    public bool PreferSeasonZip { get; }

    /// <inheritdoc />
    public async Task<SourceSearchAnswer> SearchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Settings.IsConfigured)
        {
            // F-M683: die eigene Datei gilt nicht — diese Quelle ist nicht eingerichtet. Der Grund
            // steht im Klartext, ohne jeden Wert aus der Datei.
            return SourceSearchAnswer.Because(SourceFaultInfo.AuthFailed(Settings.Problem));
        }

        if (string.IsNullOrWhiteSpace(request.ImdbId) && string.IsNullOrWhiteSpace(request.TmdbId))
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                "SubDL is searched by id — the request names neither an IMDb id nor a TMDb number"));
        }

        if (string.IsNullOrWhiteSpace(_client.ApiKey))
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.AuthFailed("no SubDL API key configured"));
        }

        var sprachen = SourceNeutral.MapLanguages(request.Languages, LanguageMapper.MapToSubdl);
        List<SubtitleCandidate>? gefunden;
        try
        {
            gefunden = await _client.SearchSubtitlesAsync(
                request.ImdbId,
                request.TmdbId,
                request.Season ?? 0,
                request.Episode ?? 0,
                sprachen.Count == 0 ? null : string.Join(",", sprachen),
                MaxSearchPages,
                ct,
                0,
                request.HearingImpaired).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // Der Client wirft, wenn die Kennung fehlt — das ist ein Zugangsfehler, kein Netzfehler.
            return SourceSearchAnswer.Because(SourceFaultInfo.AuthFailed(ex.Message));
        }
        catch (Exception ex)
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.Transient(ex.Message));
        }

        if (gefunden is null)
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.Transient("the search answered nothing usable"));
        }

        var kandidaten = new List<SourceCandidate>(gefunden.Count);
        foreach (SubtitleCandidate c in gefunden)
        {
            if (string.IsNullOrWhiteSpace(c.NId))
            {
                // Ohne Kennung ist der Kandidat nicht abholbar — er wird nicht angeboten.
                continue;
            }

            kandidaten.Add(new SourceCandidate(
                HandlePrefix + c.NId,
                SourceNeutral.CandidateLanguage(c.Language),
                c.HearingImpaired,
                c.ReleaseName,
                c.FullSeason || c.UnpackFiles.Count > 0));
        }

        // F-M683: „Staffel zips bevorzugen" ist eine Eigenschaft DIESER Quelle. Gefragt ist ein
        // Behälter, der Schalter steht an und die Quelle hat Staffel-ZIPs angeboten: sie stehen vorn,
        // die Reihenfolge der übrigen bleibt die Reihenfolge der Quelle (stabile Sortierung).
        if (PreferSeasonZip && request.WantsPack && kandidaten.Any(k => k.IsPack))
        {
            kandidaten = kandidaten.OrderByDescending(k => k.IsPack).ToList();
        }

        return new SourceSearchAnswer(kandidaten);
    }

    /// <inheritdoc />
    public async Task<SourceFetchAnswer> FetchAsync(SourceFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Handle))
        {
            return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("no handle"));
        }

        string nId = request.Handle.StartsWith(HandlePrefix, StringComparison.Ordinal)
            ? request.Handle[HandlePrefix.Length..]
            : request.Handle;

        DownloadOutcome outcome;
        try
        {
            outcome = await _client.DownloadV2FileAsync(nId, ct, 0, request.Episode ?? 0, request.MemberName)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SourceFetchAnswer.Because(SourceFaultInfo.Transient(ex.Message));
        }

        if (!outcome.HasContent || outcome.Content is not { Length: > 0 } inhalt)
        {
            return SourceFetchAnswer.Because(
                SourceNeutral.FaultForStatus(outcome.Status, _client.RateLimitResetUtc, _context.Clock)
                ?? SourceFaultInfo.NotFound(outcome.Label));
        }

        string name = string.IsNullOrWhiteSpace(outcome.Member)
            ? nId + ".srt"
            : outcome.Member!;

        return new SourceFetchAnswer(inhalt, name, nId, SourceNeutral.ContentHash(inhalt));
    }

    /// <inheritdoc />
    public async Task<SourceSendAnswer> SendAsync(SourceSendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sprache = LanguageMapper.MapToSubdl(request.Language) ?? string.Empty;
        if (sprache.Length == 0)
        {
            return new SourceSendAnswer(
                SourceSendStatus.Rejected,
                "language unknown to SubDL: " + request.Language,
                Fault: SourceFaultInfo.Rejected("language unknown to the source"));
        }

        if (request.Bytes.Length == 0)
        {
            return new SourceSendAnswer(
                SourceSendStatus.Rejected,
                "no bytes to send",
                Fault: SourceFaultInfo.Rejected("empty payload"));
        }

        try
        {
            UploadResult ergebnis = request.AsPack
                ? await SendPackAsync(request, sprache, ct).ConfigureAwait(false)
                : await _client.UploadSubtitleAsync(
                    Encoding.UTF8.GetString(request.Bytes),
                    request.FileName,
                    sprache,
                    request.ReleaseName,

                    // Die Qualität ist eine Angabe des Laufs; die Grenze trägt sie nicht, und ein
                    // geratener Wert wäre schlechter als keiner.
                    string.Empty,
                    request.Target.ImdbId,
                    request.Target.TmdbId,
                    request.Target.IsSeries,
                    request.Target.Season ?? 0,
                    request.Target.Episode,
                    ct,
                    request.HearingImpaired).ConfigureAwait(false);

            return Verdict(ergebnis);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceSendAnswer(
                SourceSendStatus.NoVerdict, ex.Message, Fault: SourceFaultInfo.Transient(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<SourceReportAnswer> ReportAsync(SourceReportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RemoteId))
        {
            return new SourceReportAnswer(
                SourceReportStatus.Failed, Fault: SourceFaultInfo.NotFound("no remote id"));
        }

        try
        {
            ScribeReportOutcome ergebnis = await _client
                .ReportBrokenSubtitleAsync(request.RemoteId, Category(request.Kind), request.Text, ct)
                .ConfigureAwait(false);

            return new SourceReportAnswer(ergebnis switch
            {
                ScribeReportOutcome.Accepted => SourceReportStatus.Accepted,
                ScribeReportOutcome.Deferred => SourceReportStatus.Deferred,
                _ => SourceReportStatus.Failed,
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceReportAnswer(
                SourceReportStatus.Failed, Fault: SourceFaultInfo.Transient(ex.Message));
        }
    }

    /// <summary>
    /// F-M450: die vier Kategorien der Seite, aus der neutralen Art des Defekts. Die Seite kennt die
    /// Worte <c>not_a_subtitle</c>, <c>wrong_release_info</c>, <c>other</c> (und <c>stolen</c>, das
    /// dieser Lauf nie benutzt).
    /// </summary>
    /// <param name="kind">Die neutrale Art.</param>
    /// <returns>Die Kategorie der Seite.</returns>
    private static string Category(SourceReportKind kind) => kind switch
    {
        SourceReportKind.Missing => "not_a_subtitle",
        SourceReportKind.Unplayable => "not_a_subtitle",
        SourceReportKind.WrongContent => "wrong_release_info",
        SourceReportKind.WrongLanguage => "wrong_release_info",
        _ => "other",
    };

    /// <summary>Der Behälter: ein Aufruf, die Mitglieder als Dateien.</summary>
    /// <param name="request">Die Abgabe.</param>
    /// <param name="sprache">Der Sprachcode der Seite.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Das Urteil des Clients.</returns>
    private Task<UploadResult> SendPackAsync(SourceSendRequest request, string sprache, CancellationToken ct)
    {
        var dateien = request.Members
            .Select(m => (m.FileName, Encoding.UTF8.GetString(m.Bytes)))
            .ToList();

        int von = request.Members.Count == 0 ? request.Target.Episode ?? 0 : request.Members.Min(m => m.Episode);
        int bis = request.Members.Count == 0 ? request.Target.Episode ?? 0 : request.Members.Max(m => m.Episode);

        return _client.UploadSeasonPackAsync(
            dateien,
            request.ReleaseName,
            sprache,
            new[] { request.ReleaseName },
            string.Empty,
            request.Target.ImdbId,
            request.Target.TmdbId,
            request.Target.Season ?? 0,
            von,
            bis,
            ct,
            request.HearingImpaired);
    }

    /// <summary>F-M609: das Urteil des Clients in die drei Fälle der Grenze.</summary>
    /// <param name="ergebnis">Das Ergebnis des Uploads.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    private static SourceSendAnswer Verdict(UploadResult ergebnis)
    {
        if (ergebnis.Ok || ergebnis.SourceAccepted)
        {
            return new SourceSendAnswer(SourceSendStatus.Accepted, ergebnis.Reason, ergebnis.SourceId);
        }

        return ergebnis.Verdict switch
        {
            UploadVerdict.NoVerdict => new SourceSendAnswer(
                SourceSendStatus.NoVerdict, ergebnis.Reason, Fault: SourceFaultInfo.Transient(ergebnis.Reason)),
            _ => new SourceSendAnswer(
                SourceSendStatus.Rejected,
                ergebnis.Reason ?? "refused",
                Fault: SourceFaultInfo.Rejected(ergebnis.Reason ?? "refused")),
        };
    }
}
