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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Language;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources.Msub;

/// <summary>
/// F-M682: die Film-Adresse der zweiten Quelle hinter der Grenze. Dünner Adapter auf
/// <see cref="MovieSubtitlesClient"/>.
/// <para>
/// Diese Quelle kennt nur Filme: sie sucht über den Namen (und vergleicht Titel UND Jahr eng), sie
/// kennt keine Staffel-Behälter, und sie hat keinen Meldeweg — <see cref="SourceCapabilities.CanReport"/>
/// ist deshalb falsch, und <see cref="ReportAsync"/> antwortet nur, es gehe nicht.
/// </para>
/// </summary>
public sealed class MsubEngine : ISourceEngine
{
    /// <summary>Der Schlüssel dieser Quelle — derselbe Wert, den der Bestand führt.</summary>
    public const string SourceKey = "moviesubtitles";

    /// <summary>Der Griff trägt seine Herkunft: <c>m:</c> + Filmnummer + <c>:</c> + Eintrags-Kennung.</summary>
    private const string HandlePrefix = "m:";

    /// <summary>F-M691: der Download-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldDownloadEnabled = SourceSwitches.DownloadField;

    /// <summary>F-M683: der Schlüssel der Adresse in der eigenen Datei dieser Quelle.</summary>
    public const string FieldBaseUrl = "baseUrl";

    /// <summary>F-M683: der Schlüssel des Zugangs in der eigenen Datei dieser Quelle.</summary>
    public const string FieldAccess = "access";

    /// <summary>
    /// F-M683: die Felder, die DIESE Quelle erklärt — in dieser Reihenfolge. Die Adresse mit der
    /// Vorgabe, die der Client dieses Builds wirklich benutzt (<see cref="MovieSubtitlesClient.BaseUrl"/>),
    /// und ein Geheimfeld „Zugang", das HEUTE LEER IST: die Seite verlangt keinen (gemessen 15.11.2026 —
    /// <c>/login.php</c>, <c>/panel.php</c> und <c>/upload.php</c> antworten mit 302 auf <c>/</c>).
    /// </summary>
    public static IReadOnlyList<SourceField> Declared { get; } = new SourceField[]
    {
        // F-M691: hier steht NUR der Download-Schalter. Einen Upload-Schalter zu zeigen, der nichts tun
        // darf, wäre eine Behauptung: der Upload dieser Adresse ist bis zur belegten Sprachabbildung
        // gesperrt (F-M508, Betreiber: „Bevor wir falsche Sprachen hochladen, laden wir nix hoch.").
        new(FieldDownloadEnabled, "Download", SourceFieldKind.Switch, Default: "true", Group: SourceField.Badges,
            Help: "Search and fetch subtitles from this site."),
        // F-M692: die Adresse ist kein Feld mehr — sie steht fest im Client dieses Builds.
        // F-M695 (Betreiberbefund 22.11.2026: „Not sure what access means for mst."): auch das Feld
        // "Access" ist weg. Es war von Anfang an leer und sein Hilfetext sagte selbst, dass die Seite
        // keinen Zugang verlangt (gemessen 15.11.2026: /login.php, /panel.php und /upload.php antworten
        // mit 302 auf /). Ein Feld, das nichts abfragt und nichts bewirkt, ist eine Frage ohne Antwort.
    };

    private readonly MovieSubtitlesClient _client;
    private readonly SourceEngineContext _context;

    /// <summary>
    /// F-M686: der Such- und Holweg dieser Quelle — SIE hält ihn, nicht mehr der Ablauf. Die Instanz
    /// lebt so lange wie die Engine, damit ihr Seitenmerker (15 Minuten, F-M507) für den ganzen Lauf
    /// gilt und eine Seite je Film EINMAL geholt wird, auch wenn sechs Sprachen fehlen.
    /// </summary>
    private readonly Api.Endpoints.MovieSubtitlesDownloadEndpoint _weg;

    /// <summary>
    /// Initializes a new instance of the <see cref="MsubEngine"/> class.
    /// </summary>
    /// <param name="client">Der Film-Client des Laufs, oder null — dann baut ihn die Engine über den
    /// gereichten HttpClient.</param>
    /// <param name="context">Was das Plugin reicht (HttpClient, Logger, Zeitgeber).</param>
    /// <param name="settings">F-M683: die Einstellungen aus der eigenen Datei dieser Quelle.</param>
    public MsubEngine(MovieSubtitlesClient? client, SourceEngineContext context, SourceSettings? settings = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _client = client ?? new MovieSubtitlesClient(context.Http);
        _weg = new Api.Endpoints.MovieSubtitlesDownloadEndpoint(_client, context.Logger);
        Settings = settings ?? SourceSettings.Empty(SourceKey, Declared);
        // F-M695: die Seite verlangt keinen Zugang — es gibt nichts zu lesen.
        HasAccess = false;

        // F-M692: kein Adressvergleich mehr — die Adresse ist kein Feld. `Settings.Url` läse sonst den
        // Vorgabewert eines nicht erklärten Feldes ("") und meldete bei jedem Aufruf eine leere Adresse.

        Capabilities = new SourceCapabilities
        {
            Key = SourceKey,
            DisplayName = "moviesubtitles.org",

            // F-M692: KEIN Upload. Die Quelle könnte formal eine Abgabe annehmen, aber dieser Build
            // bietet sie nicht an — die Sprachabbildung der Seite ist gegen die echte Seite nicht belegt,
            // und der Betreiber hat gesagt, was dann gilt (F-M508: „Bevor wir falsche Sprachen hochladen,
            // laden wir nix hoch."). Eine Fähigkeit, die niemand anbieten darf, ist keine Fähigkeit; die
            // Karte zeigte sonst „upload" neben einem Schalter, den es nicht gibt.
            CanUpload = false,
            CanReport = false,
            CanHearingImpaired = false,
            CanOfferSeasonPacks = false,
            CanSeasonPacks = false,
            CanCreateTitle = true,
            SearchesByImdb = false,
            SearchesByTmdb = false,
            SearchesByName = true,
            SearchesByHash = false,
            Pace = new SourcePace(),
            LanguageCode = MovieSubtitlesClient.SiteLanguage,
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

    /// <summary>Gets F-M683: ist ein Zugang abgelegt? (Die Seite verlangt heute keinen — der Wert wird nur geführt.)</summary>
    public bool HasAccess { get; }

    /// <inheritdoc />
    public async Task<SourceSearchAnswer> SearchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Settings.IsConfigured)
        {
            // F-M683: die eigene Datei gilt nicht — diese Quelle ist nicht eingerichtet.
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(Settings.Problem));
        }

        if (request.IsSeries)
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                "moviesubtitles.org holds films only"));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return SourceSearchAnswer.Because(SourceFaultInfo.NoSearchBase(
                "moviesubtitles.org is searched by name — the request carries none"));
        }

        // F-M686: EIN Auftrag, je gewünschter Sprache EIN Abruf über den Weg dieser Quelle. Die Nummer
        // des Films, wenn der Bestand sie schon kennt (<see cref="SourceSearchRequest.MediaNumber"/>),
        // führt — dann wird nicht gesucht; sonst löst der Weg sie über Titel und Jahr auf.
        var kandidaten = new List<SourceCandidate>();
        foreach (string sprache in request.Languages.Count > 0 ? request.Languages : new[] { string.Empty })
        {
            string? code = MovieSubtitlesClient.SiteLanguage(sprache);
            if (code is null)
            {
                // Führt die Seite die Sprache nicht, hat sie nichts anzubieten — ein Ergebnis, kein Fehler.
                continue;
            }

            IReadOnlyList<Api.Endpoints.SubtitleHit> treffer;
            try
            {
                treffer = await _weg.SearchAsync(
                    new Api.Endpoints.SubtitleQuery(
                        request.Name,
                        null,
                        request.TmdbId,
                        Season: 0,
                        Episode: 0,
                        sprache,
                        Year: request.Year ?? 0,
                        MovieId: request.MediaNumber),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SourceSearchAnswer.Because(SourceFaultInfo.Transient(ex.Message));
            }

            foreach (Api.Endpoints.SubtitleHit h in treffer)
            {
                kandidaten.Add(new SourceCandidate(
                    h.Id,
                    SourceNeutral.CandidateLanguage(h.Language),
                    false,
                    h.Release,
                    false)
                {
                    EntryId = h.Id,
                    Address = h.Id,
                    ReportId = h.Id,
                });
            }
        }

        if (kandidaten.Count == 0 && request.MediaNumber is null)
        {
            // Ohne Nummer und ohne Treffer: die Quelle kennt den Film nicht. Der Grund ist eine Aussage
            // über die Quelle, keine über die Anfrage.
            return SourceSearchAnswer.Because(SourceFaultInfo.NotFound(
                "no film record for '" + request.Name + "' at moviesubtitles.org"));
        }

        return new SourceSearchAnswer(kandidaten);
    }

    /// <inheritdoc />
    public async Task<SourceFetchAnswer> FetchAsync(SourceFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Handle))
        {
            return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("no handle of this source"));
        }

        // F-M686: der Griff ist die Kennung, die die Suche ausgegeben hat (die Eintragsnummer der
        // Seite). Die alte Schreibweise <c>m:film:eintrag</c> wird weiter gelesen — sie nennt dieselbe
        // Nummer, und ein Griff von vorher soll nicht plötzlich unlesbar sein.
        string kennung = request.Handle;
        if (request.Handle.StartsWith(HandlePrefix, StringComparison.Ordinal))
        {
            string[] teile = request.Handle[HandlePrefix.Length..].Split(':');
            kennung = teile.Length == 2 ? teile[1] : string.Empty;
        }

        if (!int.TryParse(kennung, NumberStyles.Integer, CultureInfo.InvariantCulture, out int eintrag)
            || eintrag <= 0)
        {
            return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("unreadable handle"));
        }

        try
        {
            // F-M686: derselbe Holweg wie bisher — die Filmseite braucht die Eintragsnummer, nicht die
            // Filmnummer.
            Api.Endpoints.SubtitlePayload? payload = await _weg.FetchAsync(
                new Api.Endpoints.SubtitleHit(
                    Configuration.SubtitleProvider.MovieSubtitles,
                    kennung,

                    // F-M686: die Sprache der Frage reist mit — bei einer geschnittenen Datei wählt die
                    // Quelle danach ihren Teil (F-M507).
                    request.Language,
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

        if (request.AsPack)
        {
            return SourceSendAnswer.Rejected("moviesubtitles.org takes no season containers");
        }

        string? sprache = MovieSubtitlesClient.SiteLanguage(request.Language);
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
            // F-M687: die Filmnummer, die der Lauf schon aufgelöst hat (F-M508), führt — der Versand
            // sucht nicht ein zweites Mal und legt hier nichts an. Nur ohne Nummer (direkter Aufruf)
            // wird über Titel und Jahr gesucht, genau wie im Endpunkt zuvor.
            int film = int.TryParse(
                request.Target.MediaId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int vorhandene)
                && vorhandene > 0
                ? vorhandene
                : 0;

            if (film <= 0)
            {
                film = await _client
                    .FindMovieAsync(request.Target.Title, request.Target.Year, ct)
                    .ConfigureAwait(false)
                    ?? 0;
            }

            if (film <= 0)
            {
                return SourceSendAnswer.Rejected("no film entry for '" + request.Target.Title + "'");
            }

            long? tmdb = long.TryParse(request.Target.TmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long zahl)
                ? zahl
                : null;

            MovieSubtitlesUpload payload = MovieSubtitlesClient.BuildUpload(
                film,
                sprache,
                request.ReleaseName,
                request.FileName,
                request.Bytes,

                // F-M511: die Seite kennt ihre neun Rip-Werte und sonst nichts — die Abbildung von
                // unserem Qualitätsnamen auf ihr Auswahlfeld ist ihre eigene Tabelle (F-M506) und steht
                // deshalb HIER, nicht im Ablauf.
                rip: MovieSubtitlesClient.RipFromQuality(request.Quality),
                author: null,
                tmdbId: tmdb,
                imdbId: request.Target.ImdbId,

                // Der Kommentar ist der TEXT DES PLUGINS — die Kennungszeile baut der Client.
                commentExtra: request.Comment,
                movieTitle: request.Target.Title,
                year: request.Target.Year ?? 0);

            MovieSubtitlesUploadResult ergebnis = await _client
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
        await Task.CompletedTask.ConfigureAwait(false);

        // Diese Seite hat keinen Meldeweg: es gibt keine Antwort, also kein Urteil — Failed.
        return SourceReportAnswer.Failed("moviesubtitles.org has no reporting channel");
    }

    /// <summary>
    /// F-M685: das Urteil der Seite in den EINEN Ausgang der Grenze. Am echten Verhalten der Seite:
    /// „Film nicht hochladbar" heißt abgelehnt (im Wortlaut der Seite), ein 429 heißt beschäftigt, ein
    /// Anmeldefehler oder Serverfehler heißt fehlgeschlagen.
    /// </summary>
    /// <param name="ergebnis">Die Antwort der Seite.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    private static SourceSendAnswer Verdict(MovieSubtitlesUploadResult ergebnis)
        => Translate(ergebnis);

    /// <summary>
    /// F-M685: die Übersetzung selbst — als reine Funktion, damit sie ohne Netz prüfbar ist.
    /// </summary>
    /// <param name="ergebnis">Die Antwort der Seite.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    public static SourceSendAnswer Translate(MovieSubtitlesUploadResult ergebnis)
    {
        ArgumentNullException.ThrowIfNull(ergebnis);

        if (!ergebnis.Sent)
        {
            // Nichts ging hinaus: keine Antwort der Seite, also kein Ausgang.
            return SourceSendAnswer.Failed("nothing was sent");
        }

        if (ergebnis.Status is 429)
        {
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
    /// F-M685: die Sätze, mit denen die Seite eine Abgabe ABLEHNT („Film nicht hochladbar" und seine
    /// englischen Fassungen). Steht einer davon in der Antwort, ist es eine Absage, kein Netzfehler.
    /// </summary>
    private static readonly string[] Refusals =
    {
        "nicht hochladbar",
        "could not be uploaded",
        "not uploaded",
        "already exist",
        "duplicate",
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
