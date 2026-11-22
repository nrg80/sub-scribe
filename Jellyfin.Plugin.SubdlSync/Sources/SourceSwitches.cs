// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Sources.Msub;
using Jellyfin.Plugin.SubScribe.Sources.Subdl;
using Jellyfin.Plugin.SubScribe.Sources.Tvst;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M691: die beiden Richtungsschalter einer Quelle — <c>uploadEnabled</c> und
/// <c>downloadEnabled</c> —, gelesen aus der EIGENEN Datei dieser Quelle.
/// <para>
/// Betreiberansage 22.11.2026: „Enable upload/download pro source dann bitte auch ins source feld".
/// Bis dahin standen sie als <c>SubdlUploadEnabled</c>/<c>TvSubtitlesUploadEnabled</c>/… in der
/// Plugin-Konfiguration — also an einem Ort, der keiner Quelle gehört. Jetzt erklärt jede Engine die
/// beiden Schalter selbst (<see cref="UploadField"/>, <see cref="DownloadField"/>), und die Karte in der
/// Oberfläche zeigt sie wie jedes andere Feld.
/// </para>
/// <para>
/// DIESE KLASSE ANTWORTET NUR, WAS IN DER DATEI STEHT: <c>null</c> heißt „die Datei sagt nichts", nicht
/// „aus". Was dann gilt, entscheidet <see cref="PluginConfiguration.IsUploadEnabled"/> — dort steht der
/// Alt-Wert als Rückfall, damit eine Datei ohne diese Felder (Bestand von vor F-M691) weiter genau das
/// tut, was sie vorher getan hat.
/// </para>
/// </summary>
public static class SourceSwitches
{
    /// <summary>Der Feldname des Upload-Schalters in der Datei einer Quelle.</summary>
    public const string UploadField = "uploadEnabled";

    /// <summary>Der Feldname des Download-Schalters in der Datei einer Quelle.</summary>
    public const string DownloadField = "downloadEnabled";

    /// <summary>
    /// Gets or sets wo die Dateien der Quellen liegen. Vorbelegt mit dem Datenordner der laufenden
    /// Plugin-Instanz; die Prüfungen setzen hier ein temporäres Verzeichnis.
    /// </summary>
    public static Func<string?> DataFolder { get; set; } = () => Plugin.Instance?.DataFolderPath;

    /// <summary>
    /// F-M691: der Schlüssel, unter dem die Engine dieser Quelle ihre Datei führt. Die Zuordnung steht
    /// hier und nicht beim Aufrufer, damit die Konfiguration keinen Quellennamen aussprechen muss.
    /// </summary>
    /// <param name="provider">Die Quelle.</param>
    /// <returns>Der Quellenschlüssel.</returns>
    public static string KeyOf(SubtitleProvider provider) => provider switch
    {
        SubtitleProvider.TvSubtitles => TvstEngine.SourceKey,
        SubtitleProvider.MovieSubtitles => MsubEngine.SourceKey,
        _ => SubdlEngine.SourceKey,
    };

    /// <summary>F-M691: der Upload-Schalter aus der Datei dieser Quelle, oder null.</summary>
    /// <param name="provider">Die Quelle.</param>
    /// <returns>Der gespeicherte Wert, oder null wenn die Datei nichts sagt.</returns>
    public static bool? Upload(SubtitleProvider provider) => Read(provider, UploadField);

    /// <summary>F-M691: der Download-Schalter aus der Datei dieser Quelle, oder null.</summary>
    /// <param name="provider">Die Quelle.</param>
    /// <returns>Der gespeicherte Wert, oder null wenn die Datei nichts sagt.</returns>
    public static bool? Download(SubtitleProvider provider) => Read(provider, DownloadField);

    private static bool? Read(SubtitleProvider provider, string fieldKey)
    {
        if (!PluginConfiguration.HasProvider(provider))
        {
            return null;
        }

        return SourceSettingsFile.ReadFlag(DataFolder(), KeyOf(provider), fieldKey);
    }
}
