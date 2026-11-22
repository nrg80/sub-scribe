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
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M683: die EINE kleine gemeinsame Hilfe für die Einstellungsdateien der Quellen. Sie kennt den
/// Pfad, das Lesen (fehlende Datei = leere Werte), das atomare Schreiben (temporäre Datei + Verschieben),
/// die Rechte (0600) und die Vorgaben einer Erklärung — sonst nichts.
/// <para>
/// WARUM DIE REGELN HIER STEHEN UND NICHT DREIMAL. Drei Engines, drei Dateien: läge das Schreiben in
/// jeder Engine, gäbe es drei Fassungen von „atomar" und drei Chancen, eine Datei halb zu schreiben.
/// Die Rechte 0600 sind keine Zierde — die Datei führt Zugänge (SubDL-API-Schlüssel), und der
/// Datenordner eines Jellyfin-Servers ist nicht zwingend nur für den Dienst lesbar.
/// </para>
/// <para>
/// WARUM EINE KAPUTTE DATEI NUR EINE QUELLE TRIFFT. <see cref="Load"/> wirft nie. Es liefert entweder
/// Werte oder einen Klartext-Hinweis; der Aufrufer macht daraus „diese Quelle ist nicht eingerichtet".
/// Die anderen Quellen lesen ihre eigenen Dateien und merken davon nichts.
/// </para>
/// </summary>
public static class SourceSettingsFile
{
    /// <summary>Der Unterordner im Datenordner, in dem die Dateien der Quellen liegen.</summary>
    public const string FolderName = "sources";

    /// <summary>Die Form der Datei. Steht als <c>schema</c> in der Datei; eine andere Zahl gilt nicht.</summary>
    public const int SchemaVersion = 1;

    /// <summary>F-M683: dieselbe Zahl als Text — sie steht im Namen der Übernahme-Marke (<c>.seed-1</c>).</summary>
    public const string SchemaNumber = "1";

    /// <summary>Die Endung der Dateien.</summary>
    public const string Extension = ".json";

    /// <summary>F-M683: der Ordner der Quellen-Einstellungen in einem Datenordner.</summary>
    /// <param name="dataDir">Der Datenordner des Plugins.</param>
    /// <returns>Der Ordner <c>&lt;Datenordner&gt;/sources</c>.</returns>
    public static string DirIn(string dataDir) => Path.Combine(dataDir, FolderName);

    /// <summary>
    /// F-M683: die Datei EINER Quelle — <c>&lt;Datenordner&gt;/sources/&lt;key&gt;.json</c>. Der Schlüssel
    /// wird geprüft, damit kein Pfad aus ihm herausführen kann und zwei Quellen nie dieselbe Datei
    /// bekommen.
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins.</param>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <returns>Der volle Pfad der Datei.</returns>
    /// <exception cref="ArgumentException">Der Schlüssel ist leer oder trägt Zeichen, die kein Schlüssel sind.</exception>
    public static string PathIn(string dataDir, string key)
    {
        if (!IsSafeKey(key))
        {
            throw new ArgumentException("not a source key: '" + (key ?? string.Empty) + "'", nameof(key));
        }

        return Path.Combine(DirIn(dataDir), key + Extension);
    }

    /// <summary>F-M683: ist der Schlüssel als Dateiname brauchbar (Buchstaben, Ziffern, <c>-</c>, <c>_</c>)?</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <returns>Wahr, wenn er als Dateiname zulässig ist.</returns>
    public static bool IsSafeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
        {
            return false;
        }

        foreach (char c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// F-M683: liest die Datei einer Quelle. Fehlt sie, sind die Werte leer und die Vorgaben gelten; ist
    /// sie unlesbar, kaputt oder gehört sie einer anderen Quelle, steht der Grund im Klartext in
    /// <see cref="SourceSettings.Problem"/> — geworfen wird nie.
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins (null/leer = kein Ordner: leere Werte).</param>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="schema">Die erklärten Felder dieser Quelle.</param>
    /// <param name="logger">
    /// F-M684: der Logger, der einen LESEFEHLER meldet. Bis F-M683 war ein unlesbares File still — der
    /// Hinweis stand nur in der Antwort des Endpunkts, und im Log sah eine kaputte Quelldatei wie eine
    /// nicht eingerichtete Quelle aus.
    /// </param>
    /// <returns>Die Einstellungen dieser Quelle.</returns>
    public static SourceSettings Load(
        string? dataDir, string key, IReadOnlyList<SourceField> schema, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            return SourceSettings.Empty(key, schema);
        }

        string path = PathIn(dataDir, key);
        if (!File.Exists(path))
        {
            return SourceSettings.Empty(key, schema, path);
        }

        try
        {
            string text = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Broken(key, schema, path, logger, "die Datei ist kein JSON-Objekt");
            }

            if (!root.TryGetProperty("schema", out JsonElement schemaElement)
                || !schemaElement.TryGetInt32(out int gelesen))
            {
                return Broken(key, schema, path, logger, "die Datei nennt keine Schema-Zahl");
            }

            if (gelesen != SchemaVersion)
            {
                return Broken(key, schema, path, logger, "Schema " + gelesen.ToString(CultureInfo.InvariantCulture)
                    + " statt " + SchemaVersion.ToString(CultureInfo.InvariantCulture));
            }

            if (!root.TryGetProperty("key", out JsonElement keyElement)
                || keyElement.ValueKind != JsonValueKind.String)
            {
                return Broken(key, schema, path, logger, "die Datei nennt ihren Quellenschlüssel nicht");
            }

            string dateiKey = keyElement.GetString() ?? string.Empty;
            if (!string.Equals(dateiKey, key, StringComparison.Ordinal))
            {
                // Zwei Quellen dürfen nie dieselbe Datei benutzen: eine kopierte Datei gilt nicht.
                return Broken(key, schema, path, logger, "die Datei gehört zur Quelle '" + dateiKey + "'");
            }

            var werte = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SourceField feld in schema)
            {
                if (!root.TryGetProperty(feld.Key, out JsonElement element))
                {
                    continue;
                }

                string? wert = AsText(element);
                if (wert is null)
                {
                    return Broken(key, schema, path, logger, "Feld '" + feld.Key + "' hat keine einfache Form");
                }

                werte[feld.Key] = wert;
            }

            return new SourceSettings(key, schema, werte, null, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Der Hinweis nennt Art und Grund, NIE den Inhalt: eine Meldung über eine Datei mit
            // Zugängen darf keinen Zugang tragen (eine JsonException-Zeile kann Text zitieren).
            return Broken(key, schema, path, logger, "die Datei ist nicht lesbar (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>
    /// F-M683: schreibt die Werte EINER Quelle — atomar (temporäre Datei im selben Ordner, dann
    /// Verschieben) und mit Rechten 0600. Andere Dateien werden nicht angefasst.
    /// </summary>
    /// <param name="dataDir">Der Datenordner des Plugins.</param>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="schema">Die erklärten Felder — ihre Reihenfolge ist die Reihenfolge in der Datei.</param>
    /// <param name="values">Die zu schreibenden Werte als Text; fehlende Felder werden nicht geschrieben.</param>
    /// <returns>Der Pfad der geschriebenen Datei.</returns>
    public static string Save(
        string dataDir, string key, IReadOnlyList<SourceField> schema, IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        string path = PathIn(dataDir, key);
        string ordner = DirIn(dataDir);
        Directory.CreateDirectory(ordner);
        RestrictFolderToOwner(ordner);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = SchemaVersion,
            ["key"] = key,
        };

        foreach (SourceField feld in schema)
        {
            if (!values.TryGetValue(feld.Key, out string? roh))
            {
                continue;
            }

            // Ein leeres Geheimnis ist KEIN Wert — es wird nicht geschrieben, damit „nicht gesetzt"
            // nicht als leerer Eintrag in der Datei steht.
            if (feld.IsSecret && string.IsNullOrEmpty(roh))
            {
                continue;
            }

            payload[feld.Key] = Typed(feld, roh);
        }

        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
            RestrictToOwner(tmp);
            File.Move(tmp, path, overwrite: true);
            RestrictToOwner(path);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }

        return path;
    }

    /// <summary>
    /// F-M683: die Vorgaben einer Erklärung als Werte-Map — die Grundlage der Übernahme beim ersten
    /// Start. Ein Geheimnis trägt keine Vorgabe und steht deshalb nicht darin.
    /// </summary>
    /// <param name="schema">Die erklärten Felder.</param>
    /// <returns>Feldname → Vorgabe.</returns>
    public static Dictionary<string, string> Defaults(IReadOnlyList<SourceField> schema)
    {
        var werte = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SourceField feld in schema ?? Array.Empty<SourceField>())
        {
            if (!feld.IsSecret)
            {
                werte[feld.Key] = feld.Default;
            }
        }

        return werte;
    }

    /// <summary>
    /// F-M683: setzt die Rechte des Ordners auf 0700 — nur der Eigentümer betritt ihn. 0600 wäre hier
    /// falsch: ohne das Ausführungsrecht kommt niemand mehr an die Dateien darin.
    /// </summary>
    /// <param name="path">Der Ordner.</param>
    public static void RestrictFolderToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Siehe RestrictToOwner: das Speichern darf daran nicht scheitern.
        }
    }

    /// <summary>F-M683: setzt die Rechte einer Datei auf 0600 (nur der Eigentümer liest und schreibt).</summary>
    /// <param name="path">Die Datei oder der Ordner.</param>
    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Kein Grund, das Speichern scheitern zu lassen: die Datei ist geschrieben. Der Aufrufer
            // kann die Rechte prüfen (die Suite tut es).
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>F-M683: ein Wert in der Form seiner Art — Schalter als wahr/falsch, Zahl als Zahl, sonst Text.</summary>
    private static object Typed(SourceField feld, string roh) => feld.Kind switch
    {
        SourceFieldKind.Switch => string.Equals(roh, "true", StringComparison.OrdinalIgnoreCase)
            || roh == "1"
            || string.Equals(roh, "an", StringComparison.OrdinalIgnoreCase),
        SourceFieldKind.Number => decimal.TryParse(roh, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal zahl)
            ? (object)zahl
            : 0m,
        _ => roh,
    };

    /// <summary>F-M683: ein JSON-Wert als Text — Objekte und Listen sind kein Einstellungswert.</summary>
    private static string? AsText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.Null => string.Empty,
        _ => null,
    };

    /// <summary>
    /// F-M684: ein unlesbares File — der Klartext-Grund geht in die Einstellungen, in eine Logzeile und
    /// in den Zähler dieser Quelle. Die Zeile nennt Quelle und Grund, NIE einen Wert aus der Datei.
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="schema">Die erklärten Felder.</param>
    /// <param name="path">Der Pfad der Datei.</param>
    /// <param name="logger">Der Logger, oder null.</param>
    /// <param name="grund">Der Klartext-Grund.</param>
    /// <returns>Die Einstellungen mit Hinweis.</returns>
    private static SourceSettings Broken(
        string key, IReadOnlyList<SourceField> schema, string path, ILogger? logger, string grund)
    {
        logger?.LogWarning(
            "[Sub Scribe] sources/{Key}: {Reason} — diese Quelle gilt als nicht eingerichtet, die anderen laufen weiter.",
            key,
            grund);
        SourceTraffic.NoteConfigFault(key, grund);
        return new(key, schema, null, grund, path);
    }
}
