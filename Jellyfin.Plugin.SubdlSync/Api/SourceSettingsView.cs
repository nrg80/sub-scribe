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
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Sources;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Configuration;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>F-M683: ein erklärtes Feld, so wie es der Endpunkt ausliefert.</summary>
/// <param name="Key">Der stabile Schlüssel (Speichername).</param>
/// <param name="Label">Die deutsche Beschriftung.</param>
/// <param name="Kind">Die Art (text, secret, number, switch, choice, url).</param>
/// <param name="Required">Ist das Feld Pflicht?</param>
/// <param name="Help">Der Hilfetext, oder null.</param>
/// <param name="Default">Die Vorgabe — bei einem Geheimnis IMMER null.</param>
/// <param name="Value">Der wirksame Wert — bei einem Geheimnis IMMER null.</param>
/// <param name="Gesetzt">Ist ein Wert abgelegt? (Bei einem Geheimnis die EINZIGE Auskunft über seinen Inhalt.)</param>
/// <param name="Min">Untere Grenze einer Zahl, oder null.</param>
/// <param name="Max">Obere Grenze einer Zahl, oder null.</param>
/// <param name="Options">Die Möglichkeiten einer Auswahl.</param>
/// <param name="VisibleWhen">Die Bedingung, unter der das Feld sichtbar ist, oder null.</param>
public sealed record SourceFieldView(
    string Key,
    string Label,
    string Kind,
    bool Required,
    string? Help,
    string? Default,
    string? Value,
    bool Gesetzt,
    double? Min,
    double? Max,
    IReadOnlyList<string> Options,
    SourceFieldVisibility? VisibleWhen,
    string? Group,
    string? HelpUrl,
    string? HelpLink);

/// <summary>F-M683: die Taktgrenzen einer Quelle, wie sie sie selbst nennt.</summary>
/// <param name="UploadsPerHour">Abgaben je Stunde, 0 wenn die Quelle keine nennt.</param>
/// <param name="MinCallPauseSeconds">Mindestabstand, 0 wenn keiner bekannt ist.</param>
public sealed record SourcePaceView(int UploadsPerHour, double MinCallPauseSeconds);

/// <summary>
/// F-M683: die Fähigkeiten einer Quelle, wie <see cref="SourceCapabilities"/> sie heute führt — ohne die
/// Sprachbrücke als Funktion, sondern mit ihren Ergebnissen für die Sprachen, die der Lauf benutzt.
/// </summary>
/// <param name="CanUpload">Nimmt die Quelle eigene Untertitel an?</param>
/// <param name="CanReport">Hat sie einen Meldeweg?</param>
/// <param name="CanHearingImpaired">Trennt sie die HI-Fassung?</param>
/// <param name="CanOfferSeasonPacks">Kann sie Behälter überhaupt annehmen?</param>
/// <param name="CanSeasonPacks">Steht sie in DIESEM Lauf im Pack-Slot?</param>
/// <param name="CanCreateTitle">Legt sie einen fehlenden Titel selbst an?</param>
/// <param name="CanQuota">Führt sie ein Kontingent?</param>
/// <param name="SearchesByImdb">Sucht sie über IMDb?</param>
/// <param name="SearchesByTmdb">Sucht sie über TMDb?</param>
/// <param name="SearchesByName">Sucht sie über den Namen?</param>
/// <param name="SearchesByHash">Sucht sie über den Inhalts-Hash?</param>
/// <param name="Pace">Ihre Taktgrenzen.</param>
/// <param name="Languages">Ihre Sprachbrücke für DEU, ENG, FRA und SPA (ISO → Code der Quelle, null = kennt sie nicht).</param>
public sealed record SourceCapabilityView(
    bool CanUpload,
    bool CanReport,
    bool CanHearingImpaired,
    bool CanOfferSeasonPacks,
    bool CanSeasonPacks,
    bool CanCreateTitle,
    bool CanQuota,

    /// <summary>F-M696: bedient die Quelle Fernsehserien?</summary>
    bool ServesSeries,

    /// <summary>F-M696: bedient die Quelle Filme?</summary>
    bool ServesMovies,
    bool SearchesByImdb,
    bool SearchesByTmdb,
    bool SearchesByName,
    bool SearchesByHash,
    SourcePaceView Pace,
    IReadOnlyDictionary<string, string?> Languages);

/// <summary>F-M683: EINE Quelle, so wie der Endpunkt sie ausliefert.</summary>
/// <param name="Key">Der Quellenschlüssel.</param>
/// <param name="DisplayName">Der Anzeigename.</param>
/// <param name="Configured">Darf diese Quelle mitlaufen? (Falsch bei kaputter Datei oder fehlendem Pflichtfeld.)</param>
/// <param name="Hint">Der Klartext-Hinweis, warum sie nicht eingerichtet ist — oder null.</param>
/// <param name="File">Die Datei dieser Quelle, relativ zum Datenordner.</param>
/// <param name="Capabilities">Ihre Fähigkeiten.</param>
/// <param name="Fields">Ihre erklärten Felder in Erklärungsreihenfolge, mit den aktuellen Werten.</param>
/// <param name="Counters">
/// F-M684: was DIESE Quelle im laufenden Betrieb getan hat — Aufträge, Abrufe, Seiten, Fehlschläge nach
/// Art, letzter Erfolg und letzter Fehler samt Grund. Die Zahlen leben im Speicher und werden nirgends
/// abgelegt; sie entstehen am Ort des Geschehens, nicht durch das Lesen eines Logs.
/// </param>
public sealed record SourceEntryView(
    string Key,
    string DisplayName,

    // F-M746 (Betreiberwunsch 25.11.2026: "Wir koennen fuer die Reiter Abkuerzungen einfuehren SubDL, tvst, mst"):
    // der Reiter traegt die Abkuerzung der Quelle, der Kartenkopf weiter den vollen Namen. Sie kommt aus
    // den Faehigkeiten der Quelle (SourceCapabilities.Tab), nicht aus einer Liste in der Seite.
    string TabName,
    bool Configured,

    // F-M708 (Betreiberbefund 22.11.2026: "Das on unter change order geht nie off"): die Ampel der
    // Reihenfolge zeigte "configured" — also nur, ob die Datei vollstaendig ist. Wer beide Richtungen
    // einer Quelle ausschaltet, liest die Datei weiter als vollstaendig, und die Ampel blieb auf "on".
    // "Participates" ist die richtige Auskunft: eingerichtet UND mindestens eine Richtung offen.
    bool Participates,
    string? Hint,
    string? File,
    SourceCapabilityView Capabilities,
    IReadOnlyList<SourceFieldView> Fields,
    SourceCountersView Counters);

/// <summary>F-M683: das Ergebnis eines Schreibvorgangs.</summary>
/// <param name="Ok">Wurde gespeichert?</param>
/// <param name="Error">Der Grund, warum nicht, oder null.</param>
/// <param name="UnknownFields">Die abgewiesenen, unbekannten Felder.</param>
/// <param name="Written">Die wirklich geschriebenen Felder.</param>
/// <param name="Entry">Der Stand der Quelle nach dem Schreiben (null bei einem Fehler).</param>
public sealed record SourceSaveResult(
    bool Ok,
    string? Error,
    IReadOnlyList<string> UnknownFields,
    IReadOnlyList<string> Written,
    SourceEntryView? Entry);

/// <summary>
/// F-M683: die Antwortform des Einstellungs-Endpunkts — eine reine Rechnung über Registry und Datei,
/// damit sie ohne HTTP prüfbar ist (dieselbe Trennung wie bei den Endpunkt-Adaptern: der Controller
/// bleibt eine dünne Hülle).
/// <para>
/// ZWEI REGELN, DIE HIER STEHEN UND NIRGENDS SONST:
/// </para>
/// <para>
/// (1) <b>Ein Geheimnis verlässt das Plugin nie.</b> Für ein Geheimfeld ist <c>wert</c> und
/// <c>vorgabe</c> immer null; geantwortet wird nur <c>gesetzt</c>. Das gilt für den Lese- UND den
/// Schreibweg — auch die Antwort auf ein Schreiben zeigt den Wert nicht.
/// </para>
/// <para>
/// (2) <b>Geschrieben wird genau eine Datei.</b> <see cref="Save"/> liest die Datei der genannten
/// Quelle, mischt die übergebenen Felder darüber und schreibt sie zurück. Kein anderer Pfad wird
/// angefasst — die anderen Quellen merken von einem Schreibvorgang nichts.
/// </para>
/// </summary>
public static class SourceSettingsView
{
    /// <summary>F-M683: die Sprachen, für die die Fähigkeiten die Sprachbrücke mitliefern.</summary>
    private static readonly string[] BridgeLanguages = { "DEU", "ENG", "FRA", "SPA" };

    /// <summary>F-M683: alle Quellen der Registry, in Frage-Reihenfolge, mit Feldern und Werten.</summary>
    /// <param name="registry">Die gebaute Registry.</param>
    /// <param name="dataDir">Der Datenordner des Plugins.</param>
    /// <returns>Je Quelle ein Eintrag.</returns>
    public static IReadOnlyList<SourceEntryView> Build(SourceRegistry registry, string? dataDir)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Engines.Select(e => Entry(registry, e, dataDir)).ToList();
    }

    /// <summary>
    /// F-M683: speichert die übergebenen Felder GENAU EINER Quelle. Leere Geheimfelder bedeuten
    /// „unverändert lassen", unbekannte Felder werden abgewiesen, und die Pflichtfelder werden gegen den
    /// Stand NACH dem Schreiben geprüft.
    /// </summary>
    /// <param name="registry">Die gebaute Registry.</param>
    /// <param name="dataDir">Der Datenordner des Plugins.</param>
    /// <param name="key">Die Quelle.</param>
    /// <param name="updates">Feldname → Wert; null heißt „nichts übergeben".</param>
    /// <param name="logger">
    /// F-M684: der Logger, der eine ABWEISUNG meldet. Eine abgewiesene Eingabe war bis F-M683 nur an der
    /// Antwort des Endpunkts zu sehen — im Log stand nichts, und ein Betreiber, der eine Einstellung
    /// „gespeichert" glaubte, hatte keinen Beleg. Gemeldet wird der Grund, nie der Wert.
    /// </param>
    /// <returns>Das Ergebnis samt neuem Stand.</returns>
    public static SourceSaveResult Save(
        SourceRegistry registry,
        string? dataDir,
        string key,
        IReadOnlyDictionary<string, string?>? updates,
        ILogger? logger = null,
        LogLevelMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ISourceEngine? engine = registry.ByKey(key);
        if (engine is null)
        {
            return Fail("unbekannte Quelle '" + (key ?? string.Empty) + "'", key, logger);
        }

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            return Fail("no data folder — nothing can be stored", key, logger);
        }

        IReadOnlyList<SourceField> schema = engine.Fields;
        var unknown = (updates ?? new Dictionary<string, string?>())
            .Keys
            .Where(name => schema.All(f => !string.Equals(f.Key, name, StringComparison.Ordinal)))
            .ToList();
        if (unknown.Count > 0)
        {
            return new SourceSaveResult(false, "unbekannte Felder", unknown, Array.Empty<string>(), null);
        }

        // Der Stand, der gilt: was die Datei trägt — darüber die übergebenen Felder.
        SourceSettings current = SourceSettingsFile.Load(dataDir, key, schema);
        var merged = new Dictionary<string, string>(current.Stored, StringComparer.Ordinal);
        var geschrieben = new List<string>();

        foreach (KeyValuePair<string, string?> paar in updates ?? new Dictionary<string, string?>())
        {
            SourceField feld = schema.First(f => string.Equals(f.Key, paar.Key, StringComparison.Ordinal));

            // Ein leeres Geheimnis ist „unverändert lassen" — nicht „lösche den Schlüssel". Wer den
            // Schlüssel wirklich loswerden will, schreibt ihn über die Datei oder setzt einen neuen.
            if (feld.IsSecret && string.IsNullOrWhiteSpace(paar.Value))
            {
                continue;
            }

            if (feld.Validate(paar.Value, out string clean) is { } fehler)
            {
                return Fail(fehler, key, logger);
            }

            merged[feld.Key] = clean;
            geschrieben.Add(feld.Key);
        }

        foreach (SourceField feld in schema.Where(f => f.Required))
        {
            string wert = merged.TryGetValue(feld.Key, out string? vorhanden) ? vorhanden : feld.Default;
            if (wert.Length == 0)
            {
                return Fail("Pflichtfeld '" + feld.Label + "' fehlt", key, logger);
            }
        }

        SourceSettingsFile.Save(dataDir, key, schema, merged);

        // F-M750: was geschrieben wurde, sagt die Zeile selbst — Quelle, Feld, alt -> neu.
        foreach (string feldKey in geschrieben)
        {
            SourceField feld = schema.First(f => string.Equals(f.Key, feldKey, StringComparison.Ordinal));
            Configuration.SettingsChangeLog.SourceField(
                logger,
                key,
                feld,
                current.Stored.TryGetValue(feldKey, out string? vorher) ? vorher : null,
                merged[feldKey],
                mode);
        }

        return new SourceSaveResult(true, null, Array.Empty<string>(), geschrieben, Entry(registry, engine, dataDir));
    }

    /// <summary>
    /// F-M748: den gespeicherten Wert EINES Geheimfelds lesen — für die Dauer eines Drucks auf das Auge.
    /// Nur ein echtes Geheimfeld einer bekannten Quelle geht; alles andere wird abgewiesen, damit dieser
    /// Weg kein allgemeines Lesetor in die Datei wird. Der Wert steht in keiner Antwort der Liste.
    /// </summary>
    /// <param name="engine">Die Quelle.</param>
    /// <param name="dataDir">Der Datenordner des Plugins.</param>
    /// <param name="fieldKey">Der Feldschlüssel.</param>
    /// <returns>Der Wert (null = nichts abgelegt) und der Grund, falls es keinen gibt.</returns>
    public static (string? Value, string? Error) RevealSecret(ISourceEngine engine, string? dataDir, string? fieldKey)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            return (null, "no data folder — nothing to read");
        }

        SourceField? feld = engine.Fields
            .FirstOrDefault(f => string.Equals(f.Key, fieldKey, StringComparison.Ordinal));
        if (feld is null)
        {
            return (null, "unknown field '" + (fieldKey ?? string.Empty) + "'");
        }

        if (!feld.IsSecret)
        {
            return (null, "'" + feld.Key + "' is not a secret field — the list already carries its value");
        }

        SourceSettings settings = SourceSettingsFile.Load(dataDir, engine.Capabilities.Key, engine.Fields);
        string wert = settings.Text(feld.Key);
        return wert.Length == 0 ? (null, "nothing stored for '" + feld.Key + "'") : (wert, null);
    }

    private static SourceEntryView Entry(SourceRegistry registry, ISourceEngine engine, string? dataDir)
    {
        string key = engine.Capabilities.Key;
        SourceSettings settings = SourceSettingsFile.Load(dataDir, key, engine.Fields);

        string? hinweis = null;
        bool eingerichtet = settings.IsConfigured;
        if (!settings.IsConfigured)
        {
            // F-M697 (Betreiberansage 22.11.2026: „Too much talk in sources gui description."): kurz,
            // englisch, ohne Belehrung. Die Seite ist englisch; ein deutscher Satz mitten in der Karte war
            // der auffälligste Rest der alten Umsetzung.
            hinweis = "Not configured: " + settings.Problem + ".";
        }
        else
        {
            SourceField? fehlend = engine.Fields
                .FirstOrDefault(f => f.Required && (settings.Has(f.Key) ? settings.Text(f.Key) : f.Default).Length == 0);
            if (fehlend is not null)
            {
                eingerichtet = false;
                hinweis = "Not configured: '" + fehlend.Label + "' is empty.";
            }
        }

        // F-M708: nimmt diese Quelle am Lauf teil? Die Datei sagt es (Schalter fragen die Datei zuerst
        // und fallen auf den Alt-Wert zurueck) — genau so entscheidet auch der Lauf.
        // Die Datei antwortet je Quellenschluessel; sagt sie nichts, gilt der Alt-Wert — der ist fuer
        // alle drei Quellen "an", also nimmt die Quelle teil.
        bool richtung = (SourceSwitches.Flag(key, SourceSwitches.UploadField) ?? true)
            || (SourceSwitches.Flag(key, SourceSwitches.DownloadField) ?? true);

        return new SourceEntryView(
            key,
            engine.Capabilities.DisplayName,
            engine.Capabilities.Tab,
            eingerichtet,
            eingerichtet && richtung,
            hinweis,
            RelativeFile(key),
            Capabilities(engine.Capabilities),
            Fields(settings),
            SourceTraffic.For(key).Snapshot());
    }

    private static SourceCapabilityView Capabilities(SourceCapabilities capabilities)
    {
        var sprachen = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string iso in BridgeLanguages)
        {
            sprachen[iso] = capabilities.Language?.ToSite(iso);
        }

        return new SourceCapabilityView(
            capabilities.CanUpload,
            capabilities.CanReport,
            capabilities.CanHearingImpaired,
            capabilities.CanOfferSeasonPacks,
            capabilities.CanSeasonPacks,
            capabilities.CanCreateTitle,
            capabilities.CanQuota,

            // F-M696: die Art der Quelle steht als Variable in der Antwort — der Ablauf kann sie lesen,
            // statt Quellennamen zu vergleichen, und die Karte schreibt daraus ihr Kennzeichen.
            capabilities.ServesSeries,
            capabilities.ServesMovies,
            capabilities.SearchesByImdb,
            capabilities.SearchesByTmdb,
            capabilities.SearchesByName,
            capabilities.SearchesByHash,
            new SourcePaceView(capabilities.Pace.UploadsPerHour, capabilities.Pace.MinCallPauseSeconds),
            sprachen);
    }

    private static IReadOnlyList<SourceFieldView> Fields(SourceSettings settings)
        => settings.Schema.Select(feld =>
        {
            // DIE Regel: ein Geheimnis wird nicht ausgeliefert — nur, ob eines abgelegt ist.
            bool gesetzt = settings.Has(feld.Key);
            return new SourceFieldView(
                feld.Key,
                feld.Label,
                SourceField.KindName(feld.Kind),
                feld.Required,
                feld.Help,
                feld.IsSecret ? null : feld.Default,
                feld.IsSecret ? null : settings.Text(feld.Key),
                gesetzt,
                feld.Kind == SourceFieldKind.Number ? feld.Min : null,
                feld.Kind == SourceFieldKind.Number ? feld.Max : null,
                feld.Kind == SourceFieldKind.Choice ? feld.Options ?? Array.Empty<string>() : Array.Empty<string>(),
                feld.VisibleWhen,
                feld.Group,
                feld.HelpUrl,
                feld.HelpLink);
        }).ToList();

    private static string RelativeFile(string key)
        => SourceSettingsFile.FolderName + "/" + key + SourceSettingsFile.Extension;

    /// <summary>
    /// F-M684: eine Abweisung — mit einer Logzeile und einem Zähler, damit sie nicht nur in der Antwort
    /// des Endpunkts steht. Die Zeile nennt Quelle und Grund, nie einen Wert.
    /// </summary>
    /// <param name="grund">Der Klartext-Grund.</param>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="logger">Der Logger, oder null.</param>
    /// <returns>Das abweisende Ergebnis.</returns>
    private static SourceSaveResult Fail(string grund, string? key, ILogger? logger)
    {
        logger?.LogWarning(
            "[Sub Scribe-Q] {Source} Einstellungen abgewiesen: {Reason}",
            string.IsNullOrWhiteSpace(key) ? "?" : key,
            grund);
        SourceTraffic.For(key).CountFault(SourceFault.Rejected, grund);
        return new(false, grund, Array.Empty<string>(), Array.Empty<string>(), null);
    }
}
