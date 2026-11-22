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
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Sources.Msub;
using Jellyfin.Plugin.SubScribe.Sources.Subdl;
using Jellyfin.Plugin.SubScribe.Sources.Tvst;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M683: die Übernahme beim ersten Start — die heutigen Werte wandern EINMAL in die jeweilige Datei
/// der Quelle, danach liest die Engine aus ihrer Datei. Bis hierher stand alles in der Plugin-Konfiguration
/// (Api-Schlüssel, Konto) oder in den Clients (drei Adressen).
/// <para>
/// WARUM ÜBERHAUPT EINE ÜBERNAHME UND KEIN NEUANFANG. Ein Betreiber, der nach dem Umbau einen leeren
/// Schlüssel vorfindet, hat einen toten Lauf und keinen Hinweis, warum. Die Übernahme schreibt deshalb
/// genau das, was dieser Build vorher benutzt hat: den API-Schlüssel und den Kontonamen aus der
/// Konfiguration, die Adressen aus den Clients (nicht abgetippt — dieselben Konstanten, die die Anfragen
/// tragen) und für alles andere die Vorgabe der Erklärung.
/// </para>
/// <para>
/// WARUM GENAU EINMAL. Nach der Übernahme liegt eine Marke (<c>sources/.seed-1</c>). Eine gelöschte
/// Datei einer Quelle bleibt damit gelöscht: „diese Quelle ist nicht eingerichtet" ist eine gültige
/// Entscheidung und darf nicht beim nächsten Start von einer alten Konfiguration überschrieben werden.
/// Eine VORHANDENE Datei wird nie angefasst, auch nicht mit dem Inhalt der Konfiguration.
/// </para>
/// <para>
/// Die übergeordneten Einstellungen (Sprachen, Takt, Richtungen, Packs, HI) bleiben, wo sie sind: in der
/// Plugin-Konfiguration. Hier wandern nur die Felder, die eine EINZELNE Quelle betreffen.
/// </para>
/// </summary>
public static class SourceSettingsSeed
{
    /// <summary>F-M683: die Marke, die sagt „die Übernahme ist gelaufen" (eine je Dateiform).</summary>
    public const string MarkerName = ".seed-" + SourceSettingsFile.SchemaNumber;

    /// <summary>
    /// F-M683: schreibt die heutigen Werte EINMAL in die Datei jeder Quelle, sofern noch keine Übernahme
    /// gelaufen ist und die Datei fehlt. Mehrfaches Aufrufen tut nach dem ersten Mal nichts mehr.
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins (null/leer = nichts tun).</param>
    /// <param name="config">Die Konfiguration, aus der die heutigen Werte kommen.</param>
    /// <returns>Die Schlüssel der Quellen, deren Datei in DIESEM Aufruf geschrieben wurde (leer, wenn keine).</returns>
    public static IReadOnlyList<string> Ensure(string? dataDir, PluginConfiguration? config)
    {
        var geschrieben = new List<string>();
        if (string.IsNullOrWhiteSpace(dataDir) || config is null)
        {
            return geschrieben;
        }

        string ordner = SourceSettingsFile.DirIn(dataDir);
        string marke = Path.Combine(ordner, MarkerName);
        if (File.Exists(marke))
        {
            return geschrieben;
        }

        Directory.CreateDirectory(ordner);
        SourceSettingsFile.RestrictFolderToOwner(ordner);

        foreach ((string key, IReadOnlyList<SourceField> schema, Dictionary<string, string> legacy) in Plan(config))
        {
            string datei = SourceSettingsFile.PathIn(dataDir, key);
            if (File.Exists(datei))
            {
                // Eine vorhandene Datei ist eine Entscheidung des Betreibers (oder eine frühere
                // Übernahme) — sie wird nicht überschrieben.
                continue;
            }

            var werte = SourceSettingsFile.Defaults(schema);
            foreach (KeyValuePair<string, string> paar in legacy)
            {
                werte[paar.Key] = paar.Value;
            }

            SourceSettingsFile.Save(dataDir, key, schema, werte);
            geschrieben.Add(key);
        }

        File.WriteAllText(marke, "schema " + SourceSettingsFile.SchemaNumber);
        SourceSettingsFile.RestrictToOwner(marke);
        return geschrieben;
    }

    /// <summary>
    /// F-M683: die Felder, für die es HEUTE schon einen Wert gibt — und woher er kommt. Genau drei
    /// Quellen: der API-Schlüssel und der Kontoname aus der Konfiguration, die Adresse aus der Konstante,
    /// die die Anfragen dieses Builds tragen.
    /// </summary>
    /// <param name="config">Die Konfiguration dieses Laufs.</param>
    /// <returns>Je Quelle: Schlüssel, Erklärung und die übernommenen Werte.</returns>
    private static IReadOnlyList<(string Key, IReadOnlyList<SourceField> Schema, Dictionary<string, string> Legacy)>
        Plan(PluginConfiguration config) => new[]
        {
            (SubdlEngine.SourceKey, SubdlEngine.Declared, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SubdlEngine.FieldApiKey] = config.ApiKey ?? string.Empty,
                [SubdlEngine.FieldAccount] = config.Username ?? string.Empty,
                [SubdlEngine.FieldBaseUrl] = ScribeApiClient.ApiBase,

                // „Staffel zips bevorzugen" ist neu; es gibt keinen Alt-Wert. Die Vorgabe der Erklärung
                // ist der Wert, den der Betreiber genannt hat (an) — sie steht deshalb schon hier und
                // nicht erst, wenn jemand die Datei liest.
                [SubdlEngine.FieldPreferSeasonZip] = "true",
            }),
            (TvstEngine.SourceKey, TvstEngine.Declared, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TvstEngine.FieldBaseUrl] = TvSubtitlesClient.BaseUrl,
            }),
            (MsubEngine.SourceKey, MsubEngine.Declared, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MsubEngine.FieldBaseUrl] = MovieSubtitlesClient.BaseUrl,
            }),
        };

    /// <summary>
    /// F-M691: die Marke, die sagt „die Richtungsschalter stehen jetzt in den Quelldateien". Eine eigene
    /// Marke, weil die Übernahme von F-M683 auf prod längst gelaufen ist — ohne sie käme der Nachtrag dort
    /// nie an.
    /// </summary>
    public const string DirectionMarkerName = ".seed-directions-1";

    /// <summary>
    /// F-M691: schreibt die beiden Richtungsschalter EINMAL in die vorhandene Datei jeder Quelle, die sie
    /// noch nicht führt. Betreiberansage 22.11.2026: die Schalter gehören „ins source feld" — bis dahin
    /// standen sie in der Plugin-Konfiguration. Ohne diesen Nachtrag zeigte die Karte die Vorgabe der
    /// Engine, während der Alt-Wert weiter galt: die Oberfläche würde über eine laufende Richtung lügen.
    /// <para>
    /// Der übernommene Wert ist der, der HEUTE gilt (<see cref="PluginConfiguration.IsUploadEnabled"/>/
    /// <see cref="PluginConfiguration.IsDownloadEnabled"/>) — der Nachtrag ändert kein Verhalten, er zieht
    /// nur den Wert an die Stelle, an der er ab jetzt gepflegt wird. Eine fehlende oder kaputte Datei wird
    /// nicht angelegt und nicht überschrieben; ein bereits gespeicherter Wert bleibt unangetastet.
    /// </para>
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins (null/leer = nichts tun).</param>
    /// <param name="config">Die Konfiguration dieses Laufs.</param>
    /// <returns>Die Schlüssel der Quellen, deren Datei in DIESEM Aufruf ergänzt wurde.</returns>
    public static IReadOnlyList<string> EnsureDirectionSwitches(string? dataDir, PluginConfiguration? config)
    {
        var ergaenzt = new List<string>();
        if (string.IsNullOrWhiteSpace(dataDir) || config is null)
        {
            return ergaenzt;
        }

        string ordner = SourceSettingsFile.DirIn(dataDir);
        string marke = Path.Combine(ordner, DirectionMarkerName);
        if (File.Exists(marke))
        {
            return ergaenzt;
        }

        foreach ((SubtitleProvider provider, string key, IReadOnlyList<SourceField> schema) in DirectionPlan)
        {
            string datei = SourceSettingsFile.PathIn(dataDir, key);
            if (!File.Exists(datei))
            {
                // Keine Datei: die Vorgabe der Engine gilt, und die ist genau der heutige Wert.
                continue;
            }

            SourceSettings vorhanden = SourceSettingsFile.Load(dataDir, key, schema);
            if (vorhanden.Problem is not null)
            {
                // Eine kaputte Datei ist kein Rohling — sie wird nicht überschrieben (F-M683).
                continue;
            }

            var werte = new Dictionary<string, string>(StringComparer.Ordinal);
            bool fehltEtwas = false;
            foreach (KeyValuePair<string, string> paar in vorhanden.Stored)
            {
                werte[paar.Key] = paar.Value;
            }

            if (!vorhanden.Has(SourceSwitches.UploadField)
                && schema.Any(f => f.Key == SourceSwitches.UploadField))
            {
                werte[SourceSwitches.UploadField] = config.IsUploadEnabled(provider) ? "true" : "false";
                fehltEtwas = true;
            }

            if (!vorhanden.Has(SourceSwitches.DownloadField)
                && schema.Any(f => f.Key == SourceSwitches.DownloadField))
            {
                werte[SourceSwitches.DownloadField] = config.IsDownloadEnabled(provider) ? "true" : "false";
                fehltEtwas = true;
            }

            if (!fehltEtwas)
            {
                continue;
            }

            SourceSettingsFile.Save(dataDir, key, schema, werte);
            ergaenzt.Add(key);
        }

        Directory.CreateDirectory(ordner);
        SourceSettingsFile.RestrictFolderToOwner(ordner);
        File.WriteAllText(marke, "directions " + SourceSettingsFile.SchemaNumber);
        SourceSettingsFile.RestrictToOwner(marke);
        return ergaenzt;
    }

    /// <summary>F-M694: die eigene Marke des Nachtrags für das Kontopasswort.</summary>
    public const string CredentialMarkerName = ".seed-credentials-1";

    /// <summary>
    /// F-M694: schreibt das Kontopasswort EINMAL in die Datei der Quelle, die es erklärt und noch nicht
    /// führt — aus dem Alt-Wert der Plugin-Konfiguration. Betreiberansage 22.11.2026: „Subdl... Api key,
    /// email, password." Seit F-M688 schreibt die Seite die alten Zugangsfelder nicht mehr; ohne diesen
    /// Nachtrag stünde auf der Karte „not set", obwohl der Lauf sehr wohl mit einem Passwort arbeitet —
    /// die Oberfläche würde über den Zugang lügen. Ist kein Alt-Wert da, wird nichts geschrieben.
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins (null/leer = nichts tun).</param>
    /// <param name="config">Die Konfiguration dieses Laufs.</param>
    /// <returns>Die Schlüssel der Quellen, deren Datei in DIESEM Aufruf ergänzt wurde.</returns>
    public static IReadOnlyList<string> EnsureCredentials(string? dataDir, PluginConfiguration? config)
    {
        var ergaenzt = new List<string>();
        if (string.IsNullOrWhiteSpace(dataDir) || config is null)
        {
            return ergaenzt;
        }

        string ordner = SourceSettingsFile.DirIn(dataDir);
        string marke = Path.Combine(ordner, CredentialMarkerName);
        if (File.Exists(marke))
        {
            return ergaenzt;
        }

        foreach ((SubtitleProvider provider, string key, IReadOnlyList<SourceField> schema) in DirectionPlan)
        {
            if (!schema.Any(f => f.Key == SubdlEngine.FieldPassword))
            {
                // Nur die Quelle, die ein Passwort erklärt (heute SubDL).
                continue;
            }

            string datei = SourceSettingsFile.PathIn(dataDir, key);
            if (!File.Exists(datei))
            {
                continue;
            }

            SourceSettings vorhanden = SourceSettingsFile.Load(dataDir, key, schema);
            if (vorhanden.Problem is not null)
            {
                continue;
            }

            if (vorhanden.Has(SubdlEngine.FieldPassword))
            {
                continue;
            }

            string? alt = config.Password;
            if (string.IsNullOrWhiteSpace(alt))
            {
                continue;
            }

            var werte = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> paar in vorhanden.Stored)
            {
                werte[paar.Key] = paar.Value;
            }

            werte[SubdlEngine.FieldPassword] = alt;
            SourceSettingsFile.Save(dataDir, key, schema, werte);
            ergaenzt.Add(key);
        }

        Directory.CreateDirectory(ordner);
        SourceSettingsFile.RestrictFolderToOwner(ordner);
        File.WriteAllText(marke, "credentials " + SourceSettingsFile.SchemaNumber);
        SourceSettingsFile.RestrictToOwner(marke);
        return ergaenzt;
    }

    /// <summary>F-M693: die eigene Marke des Nachtrags für Packs und Meldung.</summary>
    public const string CapabilityMarkerName = ".seed-badges-1";

    /// <summary>
    /// F-M693: schreibt die beiden Fähigkeitsschalter — Staffelpack und Meldung — EINMAL in die vorhandene
    /// Datei jeder Quelle, die sie erklärt und noch nicht führt. Betreiberansage 22.11.2026: „Badges upload
    /// /download /packs / report. Each with it's own toogle."
    /// <para>
    /// Der übernommene Wert ist der, der HEUTE gilt: Packs stand bisher im Pack-Slot des Laufs (SubDL
    /// immer, tvsubtitles an seinem Upload-Schalter), Meldung war für beide an. Der Nachtrag ändert kein
    /// Verhalten — er zieht nur den Wert dorthin, wo er ab jetzt gepflegt wird. Eine fehlende oder kaputte
    /// Datei wird nicht angelegt und nicht überschrieben; ein gespeicherter Wert bleibt stehen.
    /// </para>
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins (null/leer = nichts tun).</param>
    /// <param name="config">Die Konfiguration dieses Laufs.</param>
    /// <returns>Die Schlüssel der Quellen, deren Datei in DIESEM Aufruf ergänzt wurde.</returns>
    public static IReadOnlyList<string> EnsureCapabilitySwitches(string? dataDir, PluginConfiguration? config)
    {
        var ergaenzt = new List<string>();
        if (string.IsNullOrWhiteSpace(dataDir) || config is null)
        {
            return ergaenzt;
        }

        string ordner = SourceSettingsFile.DirIn(dataDir);
        string marke = Path.Combine(ordner, CapabilityMarkerName);
        if (File.Exists(marke))
        {
            return ergaenzt;
        }

        foreach ((SubtitleProvider provider, string key, IReadOnlyList<SourceField> schema) in DirectionPlan)
        {
            string datei = SourceSettingsFile.PathIn(dataDir, key);
            if (!File.Exists(datei))
            {
                continue;
            }

            SourceSettings vorhanden = SourceSettingsFile.Load(dataDir, key, schema);
            if (vorhanden.Problem is not null)
            {
                continue;
            }

            var werte = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> paar in vorhanden.Stored)
            {
                werte[paar.Key] = paar.Value;
            }

            bool fehltEtwas = false;

            if (!vorhanden.Has(SourceSwitches.PacksField)
                && schema.Any(f => f.Key == SourceSwitches.PacksField))
            {
                // Vor F-M693 stand SubDL immer im Pack-Slot, tvsubtitles nur an seinem Upload-Schalter
                // (F-M654/F-M495) — genau dieser Stand wird übernommen.
                bool imSlot = provider == SubtitleProvider.Subdl
                    || config.IsUploadEnabled(SubtitleProvider.TvSubtitles);
                werte[SourceSwitches.PacksField] = imSlot ? "true" : "false";
                fehltEtwas = true;
            }

            if (!vorhanden.Has(SourceSwitches.ReportField)
                && schema.Any(f => f.Key == SourceSwitches.ReportField))
            {
                werte[SourceSwitches.ReportField] = "true";
                fehltEtwas = true;
            }

            if (!fehltEtwas)
            {
                continue;
            }

            SourceSettingsFile.Save(dataDir, key, schema, werte);
            ergaenzt.Add(key);
        }

        Directory.CreateDirectory(ordner);
        SourceSettingsFile.RestrictFolderToOwner(ordner);
        File.WriteAllText(marke, "badges " + SourceSettingsFile.SchemaNumber);
        SourceSettingsFile.RestrictToOwner(marke);
        return ergaenzt;
    }

    /// <summary>
    /// F-M691: welche Quelle welchen Schlüssel führt und welchen Schalter sie erklärt — die Grundlage des
    /// Nachtrags. Die Zuordnung steht hier, weil <see cref="SourceSwitches"/> sie schon hält.
    /// </summary>
    private static readonly (SubtitleProvider Provider, string Key, IReadOnlyList<SourceField> Schema)[] DirectionPlan =
    {
        (SubtitleProvider.Subdl, SubdlEngine.SourceKey, SubdlEngine.Declared),
        (SubtitleProvider.TvSubtitles, TvstEngine.SourceKey, TvstEngine.Declared),
        (SubtitleProvider.MovieSubtitles, MsubEngine.SourceKey, MsubEngine.Declared),
    };
}
