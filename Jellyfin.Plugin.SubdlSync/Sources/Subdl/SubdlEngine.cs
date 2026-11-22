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

    /// <summary>F-M686: der zweite Griff — <c>u:</c> + Adresse des Eintrags (Archiv-Weg, F-M471).</summary>
    private const string AddressPrefix = "u:";

    /// <summary>F-M450: die Seitenzahl-Obergrenze der Suche (Sicherheit, nicht Takt).</summary>
    private const int MaxSearchPages = 20;

    /// <summary>F-M691: der Upload-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldUploadEnabled = SourceSwitches.UploadField;

    /// <summary>F-M691: der Download-Schalter in der eigenen Datei dieser Quelle.</summary>
    public const string FieldDownloadEnabled = SourceSwitches.DownloadField;

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
        // F-M691: die beiden Richtungen stehen zuerst — sie sind die gröbste Frage an eine Quelle
        // („nimmst du überhaupt teil?"), alles andere gilt innerhalb einer eingeschalteten Richtung.
        new(FieldUploadEnabled, "Upload", SourceFieldKind.Switch, Default: "false",
            Help: "Offer our own subtitles to this source. Off unless you switch it on."),
        new(FieldDownloadEnabled, "Download", SourceFieldKind.Switch, Default: "true",
            Help: "Search and fetch subtitles from this source."),
        new(FieldApiKey, "API key", SourceFieldKind.Secret, Default: string.Empty, Required: true,
            Help: "Sent as a bearer token. Without a key SubDL neither searches nor downloads."),
        new(FieldAccount, "Account", SourceFieldKind.Text,
            Help: "The SubDL account name — used to sign in and in the report text.",
            VisibleWhen: new SourceFieldVisibility(FieldApiKey, "set")),
        new(FieldBaseUrl, "Address", SourceFieldKind.Url, Default: ScribeApiClient.ApiBase,
            Help: "The API address. This build uses the built-in one; a differing value is reported."),
        new(FieldPreferSeasonZip, "Prefer season ZIPs", SourceFieldKind.Switch, Default: "true",
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

                // F-M686: die Suchbreite kommt aus der Anfrage (F-M95) — sie ist der eine Einstellwert,
                // der Suche UND Auswahl begrenzt.
                request.EarlyStop,
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
            // F-M686: kein Kandidat fällt hier weg. Zwei Griffe gibt es: der Eintrags-Griff
            // (<c>n:</c> + Kennung, der v2-Weg) und der Adress-Griff (<c>u:</c> + Adresse, der
            // Archiv-Weg über die alte Adresse). Ein Eintrag ohne Kennung ist NICHT verschwunden —
            // er wird angeboten und beim Holen an seiner Adresse geholt, genau wie vorher.
            string griff = !string.IsNullOrWhiteSpace(c.NId)
                ? HandlePrefix + c.NId
                : AddressPrefix + c.Url;

            kandidaten.Add(new SourceCandidate(
                griff,

                // F-M686: die Sprache in der Schreibweise, die der BESTAND für diese Quelle führt — das
                // Kürzel der API, groß geschrieben. Sie hier auf ISO umzuschreiben hiesse, jede
                // Sprachangabe im Bestand umzuschreiben: die Suche des Laufs fragt „EN", und genau
                // dieser Wert steht heute an jeder Zeile.
                (c.Language ?? string.Empty).Trim().ToUpperInvariant(),
                c.HearingImpaired,
                c.ReleaseName,
                c.FullSeason || c.UnpackFiles.Count > 0)
            {
                // F-M686: was der Ablauf zum Ranken, Melden und Buchen braucht — gefüllt von der
                // Quelle, neutral benannt. EntryId ist die Marke des Bestands, Address die Adresse
                // des Eintrags, ReportId die Kennung des Meldewegs.
                Season = c.Season,
                Episode = c.Episode,
                EpisodeFrom = c.EpisodeFrom,
                EpisodeEnd = c.EpisodeEnd,
                Fps = c.Fps,
                Downloads = c.DownloadCount,
                Author = c.Author,
                OriginalName = c.OriginalName,
                EntryId = c.SubdlId,
                Address = c.Url,
                ReportId = c.NId,
                Members = c.UnpackFiles
                    .Select(m => new SourceMember(m.Name, m.Season, m.Episode, m.HearingImpaired))
                    .ToList(),
            });
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

        // F-M686: der Adress-Griff holt aus dem Archiv, das hinter SEINER Adresse liegt (F-M386/F-M471)
        // — derselbe Cache, derselbe Weg wie der Eintrags-Griff.
        if (request.Handle.StartsWith(AddressPrefix, StringComparison.Ordinal))
        {
            string adresse = request.Handle[AddressPrefix.Length..];
            try
            {
                byte[]? mitglied = await _client
                    .ExtractArchiveMemberAsync(adresse, request.MemberName, ct).ConfigureAwait(false);
                return mitglied is { Length: > 0 } mitgliedInhalt
                    ? new SourceFetchAnswer(
                        mitgliedInhalt,
                        request.MemberName ?? string.Empty,
                        adresse,
                        SourceNeutral.ContentHash(mitgliedInhalt))
                    : SourceFetchAnswer.Because(SourceFaultInfo.NotFound("the archive delivered no such member"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return SourceFetchAnswer.Because(SourceFaultInfo.Transient(ex.Message));
            }
        }

        string nId = request.Handle.StartsWith(HandlePrefix, StringComparison.Ordinal)
            ? request.Handle[HandlePrefix.Length..]
            : request.Handle;

        if (string.IsNullOrWhiteSpace(nId))
        {
            return SourceFetchAnswer.Because(SourceFaultInfo.NotFound("no entry id"));
        }

        DownloadOutcome outcome;
        try
        {
            outcome = await _client.DownloadV2FileAsync(
                nId, ct, request.Season, request.Episode ?? 0, request.MemberName)
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
            // F-M686: der Status der Quelle reist mit — ein echtes 200 ohne Inhalt (GenuineEmpty) ist
            // etwas anderes als eine Absage, und der Ablauf wiederholt genau diesen Fall (F-M455).
            return SourceFetchAnswer.Because(
                SourceNeutral.FaultForStatus(outcome.Status, _client.RateLimitResetUtc, _context.Clock)
                ?? SourceFaultInfo.NotFound(outcome.Label)) with { Status = outcome.Status };
        }

        // F-M686: der Name, den die Quelle nennt — und KEIN erfundener, wenn sie keinen nennt. Ein
        // „<kennung>.srt" aus unserer Hand landete sonst als Dateiname in einem Bericht an die Quelle.
        string name = outcome.Member ?? string.Empty;

        return new SourceFetchAnswer(inhalt, name, nId, SourceNeutral.ContentHash(inhalt))
        {
            Status = outcome.Status,
        };
    }

    /// <inheritdoc />
    public async Task<SourceSendAnswer> SendAsync(SourceSendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sprache = LanguageMapper.MapToSubdl(request.Language) ?? string.Empty;
        if (sprache.Length == 0)
        {
            return SourceSendAnswer.Rejected("language unknown to SubDL: " + request.Language);
        }

        if (!request.AsPack && request.Bytes.Length == 0)
        {
            return SourceSendAnswer.Rejected("no bytes to send");
        }

        if (request.AsPack && request.Members.Count == 0)
        {
            return SourceSendAnswer.Rejected("no files to send");
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

                    // F-M687: die Qualität und die Bildrate sind Angaben des LAUFS — er hat sie aus dem
                    // Release-Namen gelesen (F-M344/F-M350) und reicht sie mit dem Auftrag herüber. Eine
                    // hier geratene wäre schlechter als keine.
                    request.Quality,
                    request.Target.ImdbId,
                    request.Target.TmdbId,
                    request.Target.IsSeries,
                    request.Target.Season ?? 0,
                    request.Target.Episode,
                    ct,
                    request.HearingImpaired,
                    Framerate(request)).ConfigureAwait(false);

            return Verdict(ergebnis);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Netz: kein Urteil der Quelle, aber auch kein „beschäftigt" — Failed (F-M685).
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
                .ReportBrokenSubtitleAsync(request.RemoteId, Category(request.Kind), request.Text, ct)
                .ConfigureAwait(false);

            return ReportVerdict(ergebnis);
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

        // F-M687: die Release-Namen ALLER Folgen reisen mit (das Feld ist eine Liste) — der Lauf hat sie
        // aus seinen Mitgliedern zusammengestellt. Fehlt die Liste, gilt der eine Name des Behälters.
        IReadOnlyList<string> releases = request.Releases.Count > 0
            ? request.Releases
            : new[] { request.ReleaseName };

        return _client.UploadSeasonPackAsync(
            dateien,
            request.ReleaseName,
            sprache,
            releases,
            request.Quality,
            request.Target.ImdbId,
            request.Target.TmdbId,
            request.Target.Season ?? 0,
            von,
            bis,
            ct,
            request.HearingImpaired,
            Framerate(request));
    }

    /// <summary>
    /// F-M687: der Bildraten-Code des Auftrags, oder SubDLs dokumentierter Vorgabewert <c>0</c>
    /// („unbekannt", F-M350), wenn der Lauf keinen kennt — derselbe Wert, den der Lauf vorher selbst
    /// hineingereicht hat.
    /// </summary>
    /// <param name="request">Die Abgabe.</param>
    /// <returns>Der Code.</returns>
    private static string Framerate(SourceSendRequest request)
        => string.IsNullOrEmpty(request.Framerate) ? "0" : request.Framerate;

    /// <summary>
    /// F-M685: das Urteil des Clients in den EINEN Ausgang der Grenze — am echten Verhalten der Quelle
    /// gemessen: <c>{"success": true}</c> heißt angenommen, <c>already-uploaded</c> und
    /// „duplicate upload: identical file already published" heißen abgelehnt (im Wortlaut der Quelle),
    /// ein 429 bzw. der Stundendeckel heißt beschäftigt (mit der Reset-Zeit, wenn der Client sie kennt),
    /// und alles Unbeantwortete — Timeout, Netz, Abbruch — heißt fehlgeschlagen.
    /// </summary>
    /// <param name="ergebnis">Das Ergebnis des Uploads.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    private SourceSendAnswer Verdict(UploadResult ergebnis)
        => Translate(ergebnis, _client.RateLimitResetUtc, _context.Clock);

    /// <summary>
    /// F-M685: die Übersetzung selbst — als reine Funktion, damit sie ohne Netz prüfbar ist. Sie ist
    /// die EINZIGE Stelle, an der die Antwort des SubDL-Clients zu einem Ausgang der Grenze wird.
    /// </summary>
    /// <param name="ergebnis">Das Ergebnis des Uploads.</param>
    /// <param name="resetUtc">Die Reset-Zeit der Quelle, wenn sie eine genannt hat.</param>
    /// <param name="clock">Der Zeitgeber des Laufs.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    public static SourceSendAnswer Translate(UploadResult ergebnis, DateTime? resetUtc, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(ergebnis);

        if (ergebnis.Ok || ergebnis.SourceAccepted)
        {
            return SourceSendAnswer.Accepted(ergebnis.SourceId, ergebnis.Reason);
        }

        // „vorübergehend nicht möglich" ist ein Ausgang, kein Nebenfeld: 429 und der Stundendeckel
        // kommen hier an, und die Wartezeit steht AM AUSGANG.
        if (ergebnis.TemporarilyBusy)
        {
            return SourceSendAnswer.Busy(SourceNeutral.SecondsUntil(resetUtc, clock), ergebnis.Reason);
        }

        // Niemand hat geantwortet (Timeout, Netz, Abbruch): kein Urteil über den Untertitel.
        if (ergebnis.Unanswered)
        {
            return SourceSendAnswer.Failed(ergebnis.Reason);
        }

        // Es gibt eine Entscheidung. Der Klartext der Quelle gewinnt gegen unseren kurzen Grund.
        return SourceSendAnswer.Rejected(ergebnis.SourceText ?? ergebnis.Reason ?? "refused");
    }

    /// <summary>
    /// F-M685: dasselbe für die Meldung — der Client kennt drei Ausgänge, die Grenze vier, und
    /// „beschäftigt" heißt hier wie dort dasselbe.
    /// </summary>
    /// <param name="ergebnis">Das Ergebnis der Meldung.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    private SourceReportAnswer ReportVerdict(ScribeReportOutcome ergebnis)
        => TranslateReport(ergebnis, _client.RateLimitResetUtc, _context.Clock);

    /// <summary>
    /// F-M685: die Übersetzung einer Meldung, als reine Funktion (ohne Netz prüfbar).
    /// </summary>
    /// <param name="ergebnis">Das Ergebnis der Meldung.</param>
    /// <param name="resetUtc">Die Reset-Zeit der Quelle, wenn sie eine genannt hat.</param>
    /// <param name="clock">Der Zeitgeber des Laufs.</param>
    /// <returns>Die Antwort der Grenze.</returns>
    public static SourceReportAnswer TranslateReport(ScribeReportOutcome ergebnis, DateTime? resetUtc, TimeProvider clock)
        => ergebnis switch
        {
            ScribeReportOutcome.Accepted => SourceReportAnswer.Accepted(),
            ScribeReportOutcome.Busy => SourceReportAnswer.Busy(
                SourceNeutral.SecondsUntil(resetUtc, clock), "HTTP 429"),
            _ => SourceReportAnswer.Failed("the source did not confirm the report"),
        };
}
