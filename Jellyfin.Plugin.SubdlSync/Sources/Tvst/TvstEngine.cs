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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Language;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources.Tvst;

/// <summary>
/// F-M682: die Serie-Adresse der zweiten Quelle hinter der Grenze. Dünner Adapter auf
/// <see cref="TvSubtitlesClient"/>.
/// <para>
/// Diese Quelle kennt KEINE IMDb-Kennung: gesucht wird über den Namen, und deshalb darf EIN
/// Suchauftrag hier mehrere Abrufe machen — Serie finden, Staffel, Folgenseite, Einträge. Genau das ist
/// der Grund, warum die Grenze Aufträge zählt und nicht Anfragen.
/// </para>
/// <para>
/// Den Client baut die Engine NICHT selbst: er kommt aus dem <see cref="SourceEngineContext"/> (der
/// bereits aufgebaute des Laufs, sonst einer über den gereichten HttpClient und denselben
/// Archiv-Cache). Ein eigener Client mit eigenem Cookie-Container war genau der Fehler, an dem der
/// Staffel-Abruf scheiterte.
/// </para>
/// </summary>
public sealed class TvstEngine : ISourceEngine
{
    /// <summary>Der Schlüssel dieser Quelle — derselbe Wert, den der Bestand führt.</summary>
    public const string SourceKey = "tvsubtitles";

    /// <summary>Der Griff trägt seine Herkunft: <c>s:</c> + Eintrags-Kennung der Seite.</summary>
    private const string HandlePrefix = "s:";

    /// <summary>F-M683: der Schlüssel der Adresse in der eigenen Datei dieser Quelle.</summary>
    public const string FieldBaseUrl = "baseUrl";

    /// <summary>
    /// F-M683: die Felder, die DIESE Quelle erklärt. Es ist GENAU EINES — die Adresse, und ihre Vorgabe
    /// ist die Adresse, die der Client dieses Builds wirklich benutzt
    /// (<see cref="TvSubtitlesClient.BaseUrl"/>). Die Seite verlangt keinen Zugang: ein Geheimfeld wäre
    /// hier eine Behauptung über die Quelle, die niemand belegen kann.
    /// </summary>
    public static IReadOnlyList<SourceField> Declared { get; } = new SourceField[]
    {
        new(FieldBaseUrl, "Adresse", SourceFieldKind.Url, Default: TvSubtitlesClient.BaseUrl,
            Help: "Die Seitenadresse. Dieser Build benutzt sie fest eingebaut; ein abweichender Wert wird gemeldet."),
    };

    private readonly TvSubtitlesClient _client;
    private readonly SourceEngineContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvstEngine"/> class.
    /// </summary>
    /// <param name="client">Der tvst-Client des Laufs, oder null — dann baut ihn die Engine über den
    /// gereichten HttpClient.</param>
    /// <param name="context">Was das Plugin reicht (HttpClient, Archiv-Cache, Logger, Zeitgeber).</param>
    /// <param name="seasonPacks">Steht diese Quelle in diesem Lauf im Pack-Slot?</param>
    /// <param name="settings">F-M683: die Einstellungen aus der eigenen Datei dieser Quelle.</param>
    public TvstEngine(
        TvSubtitlesClient? client, SourceEngineContext context, bool seasonPacks, SourceSettings? settings = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _client = client ?? new TvSubtitlesClient(context.Http, context.ArchiveCache);
        Settings = settings ?? SourceSettings.Empty(SourceKey, Declared);
        BaseUrl = Settings.Url(FieldBaseUrl);

        if (!string.Equals(BaseUrl, TvSubtitlesClient.BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            _context.Logger.LogWarning(
                "[Sub Scribe] sources/tvsubtitles.json names the address {Address}; this build talks to {Built}.",
                BaseUrl,
                TvSubtitlesClient.BaseUrl);
        }

        Capabilities = new SourceCapabilities
        {
            Key = SourceKey,
            DisplayName = "tvsubtitles.net",
            CanUpload = true,
            CanReport = true,
            CanHearingImpaired = true,
            CanOfferSeasonPacks = true,
            CanSeasonPacks = seasonPacks,
            CanCreateTitle = true,
            SearchesByImdb = false,
            SearchesByTmdb = false,
            SearchesByName = true,
            SearchesByHash = false,

            // Die Seite nennt keine Zahl; den Takt hält der Lauf. Eine erfundene Grenze wäre eine
            // Behauptung über die Quelle, die niemand gemessen hat.
            Pace = new SourcePace(),

            // F-M502: die Brücke von ISO auf die Kürzel der Seite steht im Client — hier wird sie nur
            // durchgereicht, damit es keine zweite Tabelle gibt.
            LanguageCode = TvSubtitlesClient.SiteLanguage,
        };
    }

    /// <inheritdoc />
    public SourceCapabilities Capabilities { get; }

    /// <inheritdoc />
    public IReadOnlyList<SourceField> Fields => Declared;

    /// <summary>Gets F-M683: die Einstellungen, die diese Engine aus ihrer Datei gelesen hat (Vorgaben, wo nichts steht).</summary>
    public SourceSettings Settings { get; }

    /// <summary>Gets F-M683: die Adresse aus der eigenen Datei.</summary>
    public string BaseUrl { get; }

    /// <inheritdoc />
    public async Task<SourceSearchAnswer> SearchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Settings.IsConfigured)
        {
            // F-M683: die eigene Datei gilt nicht — diese Quelle ist nicht eingerichtet.
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(Settings.Problem));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                "tvsubtitles.net is searched by name — the request carries none"));
        }

        if (request.Season is not { } staffel || staffel < 1 || staffel > TvSubtitlesClient.MaxSeason)
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                "tvsubtitles.net is addressed per season; the request names none in 1.." +
                TvSubtitlesClient.MaxSeason.ToString(CultureInfo.InvariantCulture)));
        }

        int? folge = request.Episode;
        if (folge is not { } nummer || nummer < 1 || nummer > TvSubtitlesClient.MaxEpisode)
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                "tvsubtitles.net is addressed per episode; the request names none in 1.." +
                TvSubtitlesClient.MaxEpisode.ToString(CultureInfo.InvariantCulture)));
        }

        try
        {
            // Ein Auftrag, mehrere Abrufe: die Serie über die Liste (und, wenn die Liste sie nicht
            // nennt, über die Seitensuche), dann die Folgenseite, dann ihre Einträge.
            int? showId = await ResolveShowAsync(request.Name, ct).ConfigureAwait(false);
            if (showId is null)
            {
                return SourceSearchAnswer.Because(SourceFaultInfo.NotFound(
                    "'" + request.Name + "' is not a show on tvsubtitles.net"));
            }

            int? page = await _client.FindEpisodePageAsync(showId.Value, staffel, nummer, ct)
                .ConfigureAwait(false);
            if (page is null)
            {
                return SourceSearchAnswer.Because(SourceFaultInfo.NotFound(
                    "no episode page for season " + staffel.ToString(CultureInfo.InvariantCulture) +
                    ", episode " + nummer.ToString(CultureInfo.InvariantCulture)));
            }

            IReadOnlyList<TvSubtitlesEntry> eintraege = await _client.EntriesAsync(page.Value, ct)
                .ConfigureAwait(false);

            var kandidaten = new List<SourceCandidate>(eintraege.Count);
            foreach (TvSubtitlesEntry e in eintraege)
            {
                if (string.IsNullOrWhiteSpace(e.SubtitleId))
                {
                    continue;
                }

                kandidaten.Add(new SourceCandidate(
                    HandlePrefix + e.SubtitleId,
                    SourceNeutral.CandidateLanguage(e.Language, IsoOf),
                    e.HearingImpaired,
                    e.Release,
                    false));
            }

            return new SourceSearchAnswer(kandidaten);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.Transient(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<SourceFetchAnswer> FetchAsync(SourceFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Handle))
        {
            return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("no handle"));
        }

        if (!request.Handle.StartsWith(HandlePrefix, StringComparison.Ordinal))
        {
            // Behälter kommen bei dieser Quelle nicht aus der Suche — den Pack baut der Ablauf selbst,
            // und ein Griff, den diese Engine nicht ausgegeben hat, wird nicht gedeutet.
            return SourceFetchAnswer.Because(SourceFaultInfo.NotFound(
                "this source's search yields no container handles"));
        }

        string kennung = request.Handle[HandlePrefix.Length..];
        try
        {
            (string name, byte[] daten) = await _client.DownloadAsync(kennung, ct).ConfigureAwait(false);
            if (daten.Length == 0)
            {
                return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("the download delivered nothing"));
            }

            return new SourceFetchAnswer(
                daten,
                name,
                kennung,
                SourceNeutral.ContentHash(daten));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SourceFetchAnswer.Because(
                SourceNeutral.FaultForStatus(_client.LastStatus, null, _context.Clock)
                ?? SourceFaultInfo.Transient(ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<SourceSendAnswer> SendAsync(SourceSendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? sprache = TvSubtitlesClient.SiteLanguage(request.Language);
        if (sprache is null)
        {
            return SourceSendAnswer.Rejected("language unknown to the page: " + request.Language);
        }

        if (request.Bytes.Length == 0)
        {
            return SourceSendAnswer.Rejected("no bytes to send");
        }

        try
        {
            int? showId = await ResolveShowAsync(request.Target.Title, ct).ConfigureAwait(false);
            if (showId is null)
            {
                return SourceSendAnswer.Rejected("the show is not on the page: " + request.Target.Title);
            }

            long? tmdb = long.TryParse(request.Target.TmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long zahl)
                ? zahl
                : null;

            TvSubtitlesUpload payload = request.AsPack
                ? TvSubtitlesClient.BuildSeasonPack(
                    showId.Value,
                    request.Target.Season ?? 0,
                    sprache,
                    request.FileName,
                    request.Bytes,
                    rip: null,
                    release: request.ReleaseName,
                    tmdbId: tmdb,
                    imdbId: request.Target.ImdbId,
                    hearingImpaired: request.HearingImpaired,
                    showTitle: request.Target.Title,
                    year: request.Target.Year ?? 0)
                : TvSubtitlesClient.BuildUpload(
                    showId.Value,
                    request.Target.Season ?? 0,
                    request.Target.Episode ?? 0,
                    sprache,
                    request.FileName,
                    request.Bytes,
                    rip: null,
                    release: request.ReleaseName,
                    tmdbId: tmdb,
                    imdbId: request.Target.ImdbId,

                    // Der Kommentar ist der TEXT DES PLUGINS; die Kennungszeile der Seite baut der
                    // Client, weil das Formular sie in dieser Form erwartet.
                    commentExtra: request.Comment,
                    hearingImpaired: request.HearingImpaired,
                    showTitle: request.Target.Title,
                    year: request.Target.Year ?? 0);

            TvSubtitlesUploadResult ergebnis = await _client
                .UploadAsync(payload, dryRun: false, ct)
                .ConfigureAwait(false);

            return Verdict(ergebnis);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            // Die Seite lehnt den Payload selbst ab (Staffel, Folge, Endung, Kennungen) — das ist eine
            // Absage, kein Netzfehler. „season must be 1..22" kommt genau hier an.
            return SourceSendAnswer.Rejected(ex.Message);
        }
        catch (Exception ex)
        {
            return SourceSendAnswer.Failed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<SourceReportAnswer> ReportAsync(SourceReportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RemoteId))
        {
            return SourceReportAnswer.Failed("no remote id");
        }

        try
        {
            ScribeReportOutcome ergebnis = await _client
                .ReportBadAsync(request.RemoteId, request.Text, ct)
                .ConfigureAwait(false);

            return ergebnis switch
            {
                ScribeReportOutcome.Accepted => SourceReportAnswer.Accepted(),
                ScribeReportOutcome.Busy => SourceReportAnswer.Busy(null, "HTTP 429"),
                _ => SourceReportAnswer.Failed("the page did not confirm the report"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SourceReportAnswer.Failed(ex.Message);
        }
    }

    /// <summary>
    /// F-M480: die Serie zu einem Namen — erst über die Serienliste, dann über die Seitensuche; der
    /// Vergleich ist beide Male derselbe enge (Groß-/Kleinschreibung und Leerraum zählen nicht).
    /// </summary>
    /// <param name="name">Der Name, wie er geschrieben steht.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Seriennummer der Seite, oder null.</returns>
    private async Task<int?> ResolveShowAsync(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var liste = await _client.ShowsAsync(ct).ConfigureAwait(false);
        if (TvSubtitlesClient.ResolveShow(liste, name) is { } treffer)
        {
            return treffer;
        }

        // F-M480: die Seitensuche ist weich — ihr Ergebnis geht durch denselben engen Vergleich.
        var gesucht = await _client.SearchShowsAsync(name, ct).ConfigureAwait(false);
        return TvSubtitlesClient.ResolveShow(gesucht, name);
    }

    /// <summary>F-M482/F-M502: der ISO-Code zu einem Kürzel der Seite, wenn die Tabelle es kennt.</summary>
    /// <param name="siteCode">Das Kürzel der Seite.</param>
    /// <returns>Der ISO-639-2/B-Code, oder null.</returns>
    private static string? IsoOf(string? siteCode)
        => !string.IsNullOrWhiteSpace(siteCode) && TvSubtitlesClient.Languages.TryGetValue(siteCode, out var sprache)
            ? sprache.Iso3
            : null;

    /// <summary>
    /// F-M685: das Urteil der Seite in den EINEN Ausgang der Grenze. Am echten Verhalten der Seite
    /// gemessen: eine Seite mit „thank" heißt angenommen, „Your request could not be served … cookies
    /// disabled" und „season must be 1..22" heißen abgelehnt (im Wortlaut der Seite), ein 429 heißt
    /// beschäftigt, ein Anmeldefehler (401/403) oder ein Serverfehler heißt fehlgeschlagen.
    /// </summary>
    /// <param name="ergebnis">Die Antwort der Seite.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    private static SourceSendAnswer Verdict(TvSubtitlesUploadResult ergebnis)
        => Translate(ergebnis);

    /// <summary>
    /// F-M685: die Übersetzung selbst — als reine Funktion, damit sie ohne Netz prüfbar ist.
    /// </summary>
    /// <param name="ergebnis">Die Antwort der Seite.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    public static SourceSendAnswer Translate(TvSubtitlesUploadResult ergebnis)
    {
        ArgumentNullException.ThrowIfNull(ergebnis);

        if (!ergebnis.Sent)
        {
            // Nichts ging hinaus: es gibt keine Antwort der Seite, also auch keinen Ausgang.
            return SourceSendAnswer.Failed("nothing was sent");
        }

        if (ergebnis.Status is 429)
        {
            // Die Seite nennt keine Wartezeit — dann steht am Ausgang keine (F-M685).
            return SourceSendAnswer.Busy(null, "HTTP 429");
        }

        if (ergebnis.Status is 401 or 403)
        {
            return SourceSendAnswer.Failed("HTTP " + ergebnis.Status?.ToString(CultureInfo.InvariantCulture));
        }

        if (ergebnis.Status is null or 0 or >= 500)
        {
            return SourceSendAnswer.Failed("HTTP " + (ergebnis.Status?.ToString(CultureInfo.InvariantCulture) ?? "0"));
        }

        if (Refusal(ergebnis.Response) is { } absage)
        {
            return SourceSendAnswer.Rejected(absage);
        }

        if (ergebnis.Status is >= 200 and < 300)
        {
            return SourceSendAnswer.Accepted();
        }

        return SourceSendAnswer.Rejected(Text(ergebnis.Response, ergebnis.Status));
    }

    /// <summary>
    /// F-M685: die Sätze, mit denen die Seite eine Abgabe ABLEHNT — gemessen bzw. belegt. Steht einer
    /// davon in der Antwort, ist es eine Absage und kein Netzfehler; der Satz ist der Grund.
    /// </summary>
    private static readonly string[] Refusals =
    {
        "could not be served",
        "cookies disabled",
        "season must be 1..22",
        "not accepted",
        "refused",
        "denied",
    };

    /// <summary>Der Ablehnungssatz der Seite, wenn einer in der Antwort steht.</summary>
    /// <param name="response">Der Text der Antwort.</param>
    /// <returns>Der Satz der Seite, oder null.</returns>
    private static string? Refusal(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        foreach (string satz in Refusals)
        {
            if (response.Contains(satz, StringComparison.OrdinalIgnoreCase))
            {
                return response.Trim();
            }
        }

        return null;
    }

    /// <summary>Der Grund einer Absage: der Text der Seite, sonst der Status.</summary>
    /// <param name="response">Der Text der Antwort.</param>
    /// <param name="status">Der HTTP-Status.</param>
    /// <returns>Der Grund.</returns>
    private static string Text(string? response, int? status)
        => string.IsNullOrWhiteSpace(response)
            ? "HTTP " + status?.ToString(CultureInfo.InvariantCulture)
            : response.Trim();
}
