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
}
