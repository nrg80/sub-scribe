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

    /// <summary>F-M691: der Upload-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldUploadEnabled = SourceSwitches.UploadField;

    /// <summary>F-M691: der Download-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldDownloadEnabled = SourceSwitches.DownloadField;

    /// <summary>F-M693: der Staffelpack-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldPacksEnabled = SourceSwitches.PacksField;

    /// <summary>F-M693: der Melde-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldReportEnabled = SourceSwitches.ReportField;

    /// <summary>F-M693: Staffel-ZIPs bevorzugen — auch diese Seite liefert sie (Stufe 1 der Suche).</summary>
    public const string FieldPreferSeasonZip = "preferSeasonZip";

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
        // F-M693: die vier Möglichkeiten als Reihe oben auf der Karte, jede mit ihrem eigenen Schalter.
        // Die Vorgaben sind die des Betreibers vom 15.11.2026 (F-M503): Download an, Upload aus — ein
        // Upload verlässt den Rechner und ist deshalb opt-in.
        new(FieldUploadEnabled, "Upload", SourceFieldKind.Switch, Default: "false", Group: SourceField.Badges,
            Help: null),
        new(FieldDownloadEnabled, "Download", SourceFieldKind.Switch, Default: "true", Group: SourceField.Badges,
            Help: null),
        new(FieldPacksEnabled, "Packs", SourceFieldKind.Switch, Default: "true", Group: SourceField.Badges,
            Help: null),
        new(FieldReportEnabled, "Report", SourceFieldKind.Switch, Default: "true", Group: SourceField.Badges,
            Help: null),

        // F-M693 (Betreiberansage 22.11.2026: „Seadon zip toggle also for tvst"): Stufe 1 der Suche ist
        // die Staffel-ZIP — EIN Abruf für die ganze Staffel statt einer je Folge. Der Schalter sagt, ob
        // dieser Weg zuerst gegangen wird; aus heißt: nur der Einzelweg der Folgenseite.
        new(FieldPreferSeasonZip, "Prefer season ZIPs", SourceFieldKind.Switch, Default: "true",
            Help: null),
        // F-M692: die Adresse ist kein Feld mehr — sie steht fest im Client dieses Builds.
    };

    private readonly TvSubtitlesClient _client;
    private readonly SourceEngineContext _context;

    /// <summary>
    /// F-M686: der Such- und Holweg dieser Quelle — SIE hält ihn, nicht mehr der Ablauf. Die Instanz
    /// lebt so lange wie die Engine, damit ihr Merker (die Staffel-ZIP, F-M663) und ihre Zähler für den
    /// ganzen Lauf gelten; der Ablauf sieht sie nie.
    /// </summary>
    private readonly Api.Endpoints.TvSubtitlesDownloadEndpoint _weg;

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
        _weg = new Api.Endpoints.TvSubtitlesDownloadEndpoint(_client);
        Settings = settings ?? SourceSettings.Empty(SourceKey, Declared);
        // F-M719: der Schalter "Packs" gilt fuer beide Richtungen — steht er aus, wird auch beim
        // Holen kein Staffel-Archiv verlangt (die Vorliebe bleibt zusaetzlich ihr eigener Schalter).
        PreferSeasonZip = (SourceSwitches.Flag(SourceKey, FieldPacksEnabled) ?? seasonPacks)
            && Settings.Switch(FieldPreferSeasonZip);

        // F-M692: kein Adressvergleich mehr — die Adresse ist kein Feld.

        Capabilities = new SourceCapabilities
        {
            Key = SourceKey,
            DisplayName = "tvsubtitles.net",
            TabName = "tvst",   // F-M746: Beschriftung des Reiters
            ShortName = "tvst",   // F-M731: der Kurzname der Zeile je Paar (F-M648)
            CanUpload = true,
            // F-M696: tvsubtitles.net fuehrt ausschliesslich Serien.
            ServesSeries = true,
            ServesMovies = false,

            // F-M693: Meldung und Staffelpack sind Schalter DIESER Quelle — die Datei antwortet zuerst.
            CanReport = SourceSwitches.Flag(SourceKey, FieldReportEnabled) ?? true,
            CanHearingImpaired = true,
            CanOfferSeasonPacks = true,
            CanSeasonPacks = SourceSwitches.Flag(SourceKey, FieldPacksEnabled) ?? seasonPacks,

            // F-M687: der Staffel-Slot dieser Seite nimmt EIN Archiv — die Behälterform ist eine
            // Eigenschaft der Quelle, keine Frage an ihren Namen.
            TakesPrebuiltPack = true,
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
            Language = SourceLanguages.Voice(SourceKey),
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

    /// <summary>Gets F-M693: Staffel-ZIPs bevorzugen (Vorgabe an).</summary>
    public bool PreferSeasonZip { get; private set; } = true;

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

        // F-M686: EIN Auftrag, je gewünschter Sprache EIN Abruf — und zwar durch den Weg, den diese
        // Quelle schon hatte (Staffel-ZIP, Folgenseite, HI; F-M663/F-M664). Die Reihenfolge der Sprachen
        // ist die der Anfrage, und innerhalb einer Sprache bleibt die Reihenfolge der Seite. Was der
        // Ablauf je Sprache bekommt, hängt an GENAU dieser Sprache: er fragt, er gruppiert, und keine
        // Sprache füllt die Liste einer anderen.
        var kandidaten = new List<SourceCandidate>();
        foreach (string sprache in request.Languages.Count > 0 ? request.Languages : new[] { string.Empty })
        {
            string code = SourceNeutral.MapLanguages(new[] { sprache }, TvSubtitlesClient.SiteLanguage)
                .FirstOrDefault() ?? string.Empty;
            if (code.Length == 0)
            {
                // Führt die Seite die Sprache nicht, ist das ein ERGEBNIS und kein Fehler — genau der
                // Satz, den der Weg dieser Quelle schon wirft.
                return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                    "tvsubtitles.net has no language '" + sprache + "'"));
            }

            IReadOnlyList<Api.Endpoints.SubtitleHit> treffer;
            try
            {
                treffer = await _weg.SearchAsync(
                    new Api.Endpoints.SubtitleQuery(
                        request.Name, null, request.TmdbId, staffel, nummer, code,
                        Year: request.Year ?? 0,
                        PreferSeasonZip: PreferSeasonZip),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // F-M591: ein Ausfall DIESER Quelle ist ihre eigene Zeile; der Ablauf führt die
                // Fehlerart weiter und fragt die anderen.
                return SourceSearchAnswer.Because(SourceFaultInfo.Transient(ex.Message));
            }

            foreach (Api.Endpoints.SubtitleHit h in treffer)
            {
                // F-M686: der Releasename mit demselben Rückfall wie bisher (Rip), die Sprache in
                // unserer Schreibweise, die Behälter-Marke aus der Kennung selbst.
                string release = h.Release.Length > 0
                    ? h.Release
                    : Api.Endpoints.Provenance.Name(Configuration.SubtitleProvider.TvSubtitles) + " " + h.Rip;

                kandidaten.Add(new SourceCandidate(
                    h.Id,
                    SourceNeutral.CandidateLanguage(h.Language, IsoOf),
                    h.HearingImpaired,
                    release,
                    Api.Endpoints.TvSubtitlesDownloadEndpoint.TryParseSeasonPackId(
                        h.Id, out _, out _, out _, out _))
                {
                    Season = staffel,
                    Episode = nummer,
                    EntryId = h.Id,
                    Address = h.Id,
                    ReportId = h.Id,
                });
            }
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

        // F-M686: der Griff ist die Kennung, die die Suche ausgegeben hat — ein Eintrag der Folgenseite
        // oder ein Mitglied der Staffel-ZIP. Beide gehen durch DENSELBEN Weg wie bisher; die alte
        // Schreibweise mit <c>s:</c> wird weiter gelesen, damit ein Griff von vorher nicht plötzlich
        // unlesbar ist.
        string kennung = request.Handle.StartsWith(HandlePrefix, StringComparison.Ordinal)
            ? request.Handle[HandlePrefix.Length..]
            : request.Handle;

        try
        {
            Api.Endpoints.SubtitlePayload? payload = await _weg.FetchAsync(
                new Api.Endpoints.SubtitleHit(
                    Configuration.SubtitleProvider.TvSubtitles,
                    kennung,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false),
                ct).ConfigureAwait(false);

            if (payload is null || payload.Content.Length == 0)
            {
                return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("the download delivered nothing"));
            }

            return new SourceFetchAnswer(
                payload.Content,
                payload.FileName,
                kennung,
                SourceNeutral.ContentHash(payload.Content))
            {
                Status = _client.LastStatus,
            };
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
            // F-M687: die Nummer, die der LAUF schon aufgelöst hat, führt (F-M501) — der Versand sucht
            // nicht ein zweites Mal. Nur ohne Nummer (direkter Aufruf, ältere Zeile) wird der Name
            // gesucht, und dann mit demselben engen Vergleich wie dort.
            int? showId = int.TryParse(
                request.Target.MediaId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int vorhandene)
                && vorhandene > 0
                ? vorhandene
                : null;

            // F-M734: steht „leg den Titel an" im Auftrag, ist das die Serien-Frage der Seite — Liste,
            // anlegen, nachsehen. Der Ablauf schreibt die Kennung in den Bestand; hier wird sie benutzt
            // und zurückgemeldet.
            SourceTitleAnswer titel = SourceTitleAnswer.NotAsked;
            if (request.EnsureTitle)
            {
                if (showId is not null)
                {
                    titel = new SourceTitleAnswer(
                        SourceTitleState.Ready, showId.Value.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    titel = await new Api.Endpoints.TvstShowLookup(_client, _context.Logger)
                        .FindOrCreateAsync(
                            request.Target.Title,
                            request.Target.Year,
                            request.Target.Season,
                            request.Target.MediaId ?? request.ReleaseName,
                            request.DryRun,
                            ct)
                        .ConfigureAwait(false);

                    showId = int.TryParse(titel.TitleId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int neu)
                             && neu > 0
                        ? neu
                        : null;

                    if (!titel.Resolved)
                    {
                        return SourceSendAnswer
                            .Rejected(titel.Reason ?? "the show is not on the page: " + request.Target.Title)
                            with { Title = titel };
                    }
                }
            }
            else if (showId is null)
            {
                showId = await ResolveShowAsync(request.Target.Title, ct).ConfigureAwait(false);
                if (showId is null)
                {
                    return SourceSendAnswer
                        .Rejected("the show is not on the page: " + request.Target.Title)
                        with { Title = titel };
                }
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
                    rip: request.Quality,
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
                    rip: request.Quality,
                    release: request.ReleaseName,
                    tmdbId: tmdb,
                    imdbId: request.Target.ImdbId,

                    // Der Kommentar ist der TEXT DES PLUGINS; die Kennungszeile der Seite baut der
                    // Client, weil das Formular sie in dieser Form erwartet.
                    commentExtra: request.Comment,
                    hearingImpaired: request.HearingImpaired,
                    showTitle: request.Target.Title,

                    // F-M572: der Folgentitel steht im Kommentar — den Dateinamen baut die Seite aus
                    // ihrem eigenen Folgeneintrag, das ist kein Feld, das wir senden.
                    episodeTitle: request.EpisodeTitle,
                    year: request.Target.Year ?? 0);

            TvSubtitlesUploadResult ergebnis = await _client
                .UploadAsync(payload, dryRun: false, ct)
                .ConfigureAwait(false);

            return Verdict(ergebnis) with { Title = titel };
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
