// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Sources.Msub;
using Jellyfin.Plugin.SubScribe.Sources.Subdl;
using Jellyfin.Plugin.SubScribe.Sources.Tvst;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M682: die EINE Stelle, die die Engines baut. Kein Reflection-Scan, kein DI-Container, kein
/// Service-Locator — die Liste steht hier im Quelltext, damit ein Leser sie in einer Zeile sieht.
/// <para>
/// Die Registrierung ist die einzige Stelle, die Quellennamen kennen darf: jede Engine bekommt ihren
/// Schlüssel von ihrer eigenen Klasse, und der Ablauf fragt danach über
/// <see cref="PackSlot"/> beziehungsweise <see cref="With"/>. Eine neue Quelle anbinden heißt:
/// eine Engine schreiben und hier eine Zeile ergänzen.
/// </para>
/// <para>
/// Die Engines werden aus der Konfiguration gebaut, also MIT den Schaltern des Betreibers: sie
/// bekommen den <see cref="SourceEngineContext"/> (HttpClient, Archiv-Cache, Logger, Zeitgeber und die
/// bereits mit Zugang versehenen Clients) gereicht und legen sich nichts davon selbst an
/// (F-M682, Grenzregel b).
/// </para>
/// </summary>
public sealed class SourceRegistry
{
    private readonly IReadOnlyList<ISourceEngine> _engines;
    private readonly IReadOnlyList<ISourceMaintenance> _maintenance;
    private readonly IReadOnlyList<ISourceStatus> _status;

    private SourceRegistry(
        IReadOnlyList<ISourceEngine> engines,
        IReadOnlyList<ISourceMaintenance> maintenance,
        IReadOnlyList<ISourceStatus> status)
    {
        _engines = engines;
        _maintenance = maintenance;
        _status = status;
    }

    /// <summary>Gets die Engines in der Reihenfolge, in der gefragt wird.</summary>
    public IReadOnlyList<ISourceEngine> Engines => _engines;

    /// <summary>
    /// F-M729: die Quellen, die ihre Konto-Pflege anbieten (Einsendungen auflisten, zurückziehen) — ein
    /// ZWEITER, optionaler Vertrag neben den vier Aufträgen. Wer ihn nicht implementiert, fehlt hier
    /// einfach; der Ablauf fragt keine Quelle nach ihrem Namen.
    /// </summary>
    public IReadOnlyList<ISourceMaintenance> Maintenance => _maintenance;

    /// <summary>
    /// F-M730: die Selbstauskunft jeder Quelle — der DRITTE, schmale Vertrag. Jede Quelle kann ihn
    /// erfüllen (auch eine ohne Konto), und die Statusseite malt nur noch, was hier zurückkommt.
    /// </summary>
    public IReadOnlyList<ISourceStatus> Status => _status;

    /// <summary>Gets die Schlüssel der Engines, in derselben Reihenfolge.</summary>
    public IReadOnlyList<string> Keys
        => _engines.Select(e => e.Capabilities.Key).ToList();

    /// <summary>
    /// F-M682: baut die Engines dieser Fassung aus der Konfiguration.
    /// <para>
    /// Der SubDL-Client kommt vom Plugin und trägt seinen Zugang bereits; fehlt er, wird einer über den
    /// gereichten HttpClient gebaut. tvst und die Film-Adresse ebenso — der gereichte Client, wenn es
    /// ihn gibt, sonst einer über denselben HttpClient und denselben Archiv-Cache.
    /// </para>
    /// </summary>
    /// <param name="context">Was das Plugin der Engine reicht.</param>
    /// <param name="config">Die Einstellungen dieses Laufs (Schalter des Betreibers).</param>
    /// <returns>Die Registrierung.</returns>
    public static SourceRegistry Build(SourceEngineContext context, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);

        // F-M683: die Übernahme läuft EINMAL (Marke in `sources/`), danach lesen die Engines aus ihren
        // eigenen Dateien. Sie steht hier, weil hier ohnehin jede Quelle genau einmal gebaut wird —
        // ein zweiter Ort, an dem jemand daran denken müsste, wäre ein Ort, an dem es vergessen wird.
        SourceSettingsSeed.Ensure(context.DataFolder, config);

        // F-M691: die Richtungsschalter nachtragen, wo sie noch in der Plugin-Konfiguration stehen.
        // Eine eigene Marke, damit der Nachtrag auch auf einer Instanz ankommt, deren Übernahme
        // (F-M683) längst gelaufen ist.
        var richtungen = SourceSettingsSeed.EnsureDirectionSwitches(context.DataFolder, config);
        _ = SourceSettingsSeed.EnsureSubdlSwitches(context.DataFolder, config);   // F-M711
        if (richtungen.Count > 0)
        {
            context.Logger?.LogInformation(
                "[Sub Scribe] the upload/download switch of {Count} source(s) now lives in their own file "
                + "(F-M691): {Keys}",
                richtungen.Count,
                string.Join(", ", richtungen));
        }

        // F-M694: das Kontopasswort nachtragen (Alt-Wert aus der Plugin-Konfiguration), eigene Marke.
        var zugang = SourceSettingsSeed.EnsureCredentials(context.DataFolder, config);
        if (zugang.Count > 0)
        {
            context.Logger?.LogInformation(
                "[Sub Scribe] the account password of {Count} source(s) now lives in their own file "
                + "(F-M694): {Keys}",
                zugang.Count,
                string.Join(", ", zugang));
        }

        // F-M693: derselbe Nachtrag für Packs und Meldung, eigene Marke.
        var faehigkeiten = SourceSettingsSeed.EnsureCapabilitySwitches(context.DataFolder, config);
        if (faehigkeiten.Count > 0)
        {
            context.Logger?.LogInformation(
                "[Sub Scribe] the packs and report switch of {Count} source(s) now lives in their own file "
                + "(F-M693): {Keys}",
                faehigkeiten.Count,
                string.Join(", ", faehigkeiten));
        }

        // F-M654: SubDL steht immer im Pack-Slot — der Schalter dieser Quelle entscheidet eine Etage
        // höher, ob der Lauf überhaupt packt, nicht diese Liste. Das ist der Stand vor F-M682 und bleibt
        // er: die Umstellung ändert die Antwort, nicht den Umfang.
        // F-M683/F-M729: die Datei dieser Quelle wird EINMAL gelesen und von beiden Vertraegen benutzt —
        // die vier Auftraege und die Konto-Pflege sehen damit dieselben Zugangsdaten.
        var subdlSettings = Settings(context, SubdlEngine.SourceKey, SubdlEngine.Declared);
        var subdlClient = context.Subdl ?? new Api.ScribeApiClient(context.Http);

        var subdl = new SubdlEngine(subdlClient, context, seasonPacks: true, subdlSettings);

        // F-M495: die zweite Quelle hängt an IHREM Upload-Schalter — die Frage wird hier aus der
        // Konfiguration beantwortet, nicht im Ablauf am Namen.
        var tvst = new TvstEngine(
            context.Tvst,
            context,
            seasonPacks: config.IsUploadEnabled(SubtitleProvider.TvSubtitles),
            Settings(context, TvstEngine.SourceKey, TvstEngine.Declared));

        var msub = new MsubEngine(
            context.Msub, context, Settings(context, MsubEngine.SourceKey, MsubEngine.Declared));

        // F-M684: jede Engine geht EINMAL durch die sprechende Hülle — eine Debug-Zeile je Auftrag, eine
        // sichtbare Zeile je Fehlerfall, ein Zähler je Quelle. Die Hülle gibt Fähigkeiten und Felder
        // unverändert weiter; der Ablauf merkt nichts davon.
        var gehoert = new ISourceEngine[]
        {
            new AuditedSourceEngine(subdl, context.Logger, config.LogMode),
            new AuditedSourceEngine(tvst, context.Logger, config.LogMode),
            new AuditedSourceEngine(msub, context.Logger, config.LogMode),
        };

        // F-M729: die Konto-Pflege steht neben den vier Auftraegen. Heute kann sie nur SubDL —
        // tvsubtitles fuehrt keine Einsendungsliste, moviesubtitles hat keinen Upload.
        var pflege = new ISourceMaintenance[]
        {
            new Subdl.SubdlMaintenance(context, subdlSettings),
        };

        return new SourceRegistry(gehoert, pflege, StatusEngines(context, subdlSettings));
    }

    /// <summary>
    /// F-M729: baut NUR die Konto-Pflege — für Aufrufer, die keinen Lauf fahren und deshalb weder die
    /// Suchclients der Quellen noch die vier Aufträge brauchen (der Upload-Nachlauf). Die Grenzregeln
    /// gelten unverändert: der HttpClient wird gereicht, nicht angelegt.
    /// </summary>
    /// <param name="context">Was das Plugin reicht (HttpClient, Logger, Zeitgeber).</param>
    /// <param name="config">Die Einstellungen (wird heute nicht gelesen, bleibt in der Signatur für den Ruf).</param>
    /// <returns>Die Registrierung mit leerer Auftragsliste.</returns>
    public static SourceRegistry BuildMaintenance(SourceEngineContext context, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subdlSettings = Settings(context, SubdlEngine.SourceKey, SubdlEngine.Declared);
        var pflege = new ISourceMaintenance[]
        {
            new Subdl.SubdlMaintenance(context, subdlSettings),
        };

        _ = config;
        return new SourceRegistry(Array.Empty<ISourceEngine>(), pflege, StatusEngines(context, subdlSettings));
    }

    /// <summary>
    /// F-M733: das Kontingent, LIVE — gefragt wird die Quelle, deren Fähigkeiten <c>CanQuota</c> sagen.
    /// Der Koordinator nennt dabei keine Quelle beim Namen; er fragt „wer führt ein Kontingent".
    /// </summary>
    /// <param name="forDownload">True für den Download-Zähler, false für Suche/Upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Der Ausgang samt Zahlen, wenn die Quelle welche nennt.</returns>
    public async Task<SourceQuotaAnswer> QuotaAsync(bool forDownload, CancellationToken ct)
    {
        foreach (var engine in _engines)
        {
            if (!engine.Capabilities.CanQuota)
            {
                continue;
            }

            foreach (var status in _status)
            {
                if (string.Equals(status.Key, engine.Capabilities.Key, StringComparison.Ordinal))
                {
                    return await status.QuotaAsync(forDownload, ct).ConfigureAwait(false);
                }
            }
        }

        return new SourceQuotaAnswer(SourceQuotaState.Unreadable, Reason: "no source offers a quota");
    }

    /// <summary>
    /// F-M730: baut NUR die Selbstauskunft — für die Statusseite, die keinen Lauf fährt.
    /// </summary>
    /// <param name="context">Was das Plugin reicht (HttpClient, Logger, Zeitgeber).</param>
    /// <param name="config">Die Plugin-Einstellungen (heute nicht gelesen: der Zugang steht in den Quelldateien).</param>
    /// <returns>Die Registrierung mit leerer Auftragsliste.</returns>
    public static SourceRegistry BuildStatus(SourceEngineContext context, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subdlSettings = Settings(context, SubdlEngine.SourceKey, SubdlEngine.Declared);
        _ = config;
        return new SourceRegistry(
            Array.Empty<ISourceEngine>(),
            Array.Empty<ISourceMaintenance>(),
            StatusEngines(context, subdlSettings));
    }

    /// <summary>
    /// F-M730: die Selbstauskunft aller drei Quellen. SubDL weiß am meisten (Konto und Kontingent), die
    /// beiden anderen beantworten nur die Frage nach der Erreichbarkeit — die einzige, die sie selbst
    /// beantworten können.
    /// </summary>
    /// <param name="context">Was das Plugin reicht.</param>
    /// <param name="subdlSettings">Die gelesenen Einstellungen von SubDL.</param>
    /// <returns>Die Liste in Anzeigereihenfolge.</returns>
    private static ISourceStatus[] StatusEngines(SourceEngineContext context, SourceSettings subdlSettings) =>
        new ISourceStatus[]
        {
            new Subdl.SubdlStatus(context, subdlSettings),
            new ReachabilityStatus(
                Tvst.TvstEngine.SourceKey, "tvst", "tvst (tvsubtitles.net)", Api.TvSubtitlesClient.BaseUrl, context),
            new ReachabilityStatus(
                Msub.MsubEngine.SourceKey, "mst", "mst (moviesubtitles.org)", Api.MovieSubtitlesClient.BaseUrl, context),
        };

    /// <summary>
    /// F-M683: die Einstellungen EINER Quelle aus ihrer eigenen Datei. Kein Zwischenspeicher: die Datei
    /// ist klein, sie wird je Lauf einmal gelesen, und ein Zwischenspeicher wäre die Stelle, an der eine
    /// gerade gespeicherte Änderung erst beim nächsten Neustart ankäme.
    /// </summary>
    /// <param name="context">Der Kontext des Laufs (trägt den Datenordner).</param>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="schema">Die Erklärung dieser Quelle.</param>
    /// <returns>Die Einstellungen; ohne Datenordner die Vorgaben.</returns>
    public static SourceSettings Settings(SourceEngineContext context, string key, IReadOnlyList<SourceField> schema)
        => SourceSettingsFile.Load(context.DataFolder, key, schema, context.Logger);

    /// <summary>
    /// F-M684: die Engine HINTER der Hülle, nach ihrem Typ — für Prüfungen und für Aufrufer, die die
    /// Eigenheiten einer Quelle brauchen. Der Ablauf benutzt sie nicht; er kennt nur den Kontrakt.
    /// </summary>
    /// <typeparam name="T">Der Typ der Engine.</typeparam>
    /// <returns>Die Engine, oder null wenn diese Fassung sie nicht baut.</returns>
    public T? Engine<T>()
        where T : class, ISourceEngine
        => _engines
            .Select(e => e is AuditedSourceEngine huelle ? huelle.Inner : e)
            .OfType<T>()
            .FirstOrDefault();

    /// <summary>F-M682: schlägt eine Engine nach.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <returns>Die Engine, oder null wenn die Quelle nicht angebunden ist.</returns>
    public ISourceEngine? ByKey(string? key)
        => string.IsNullOrWhiteSpace(key)
            ? null
            : _engines.FirstOrDefault(e => string.Equals(e.Capabilities.Key, key, StringComparison.Ordinal));

    /// F-M682: die Engines, die eine Fähigkeit melden — in Frage-Reihenfolge. Der Ablauf arbeitet diese
    /// Liste ab, statt je Quelle einen Zweig zu haben.
    /// </summary>
    /// <param name="capability">Die gefragte Fähigkeit.</param>
    /// <returns>Die Engines, die sie melden.</returns>
    public IReadOnlyList<ISourceEngine> With(Func<SourceCapabilities, bool> capability)
        => _engines.Where(e => capability(e.Capabilities)).ToList();

    /// <summary>
    /// F-M682: die Quellen, die in DIESEM Lauf in den Pack-Slot gehören — die Fähigkeit „Staffelpack"
    /// aus der Registry, nicht zwei fest verdrahtete Namen im Ablauf.
    /// <para>
    /// Die Antwort ist dieselbe wie vor der Umstellung: mit eingeschaltetem tvst-Upload-Schalter
    /// <c>subdl, tvsubtitles</c>, sonst <c>subdl</c> allein (F-M680 verlangt dieselbe Antwort für jeden
    /// Lauf, in dem überhaupt gepackt wird). Die Reihenfolge ist die Reihenfolge der Engines.
    /// </para>
    /// </summary>
    /// <returns>Die Schlüssel der Quellen des Slots, in Frage-Reihenfolge.</returns>
    public IReadOnlyList<string> PackSlot()
        => _engines.Where(e => e.Capabilities.CanSeasonPacks)
            .Select(e => e.Capabilities.Key)
            .ToList();

    /// <summary>
    /// F-M682: die Quellen, die Behälter annehmen KÖNNTEN, in diesem Lauf aber nicht im Slot stehen —
    /// weil ihr Schalter aus ist. Der Ablauf nennt damit den Grund, ohne einen Quellennamen zu kennen
    /// (vorher stand hier ein Vergleich auf <c>tvsubtitles</c>).
    /// </summary>
    /// <returns>Die Schlüssel der Quellen, die der Schalter aus dem Slot hält.</returns>
    public IReadOnlyList<string> PackSlotExcluded()
        => _engines.Where(e => e.Capabilities.CanOfferSeasonPacks && !e.Capabilities.CanSeasonPacks)
            .Select(e => e.Capabilities.Key)
            .ToList();
}
