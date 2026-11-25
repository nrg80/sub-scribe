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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Jellyfin.Plugin.SubScribe.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Configuration;

/// <summary>
/// F-M750: jede Einstellung, die geschrieben wird, nennt sich selbst — welche Datei, welches Feld,
/// welcher Wert vorher und nachher.
/// <para>
/// Betreiberwunsch 25.11.2026: „Evtl sollte das ins debug. Welche konfi wann geändert wird". Der Anlass
/// war eine Frage, die sich aus den Protokollen nicht beantworten liess: um 12:15:23 urteilte die
/// Upload-Aufgabe „keine Quelle hat Upload an", 32 Sekunden später lief der Zyklus — weil in der
/// Zwischenzeit gespeichert worden war. Ohne eine Zeile je Schreibung ist im Nachhinein nicht zu
/// entscheiden, ob ein Schalter falsch gelesen wurde oder der Betreiber ihn gerade umgestellt hat.
/// </para>
/// <para>
/// F-M751 (Betreiberanweisung 25.11.2026: „Passwörter und api keys jetzt vielleicht nicht ins log
/// schreiben, auch nicht als debug"): in der Zeile steht NUR der Wert von Schaltern, Zahlen und
/// Auswahlen. Jede Zeichenkette — Kennwort, Schlüssel, Adresse, Pfad, Bibliotheksname — erscheint als
/// „gesetzt" oder „leer", ohne Länge und ohne Auszug. Geändert heisst geändert: steht ein anderer Wert
/// da als vorher, sagt die Zeile „gesetzt -> gesetzt (neu)", damit ein ausgetauschter Schlüssel nicht
/// wie „nichts passiert" aussieht.
/// </para>
/// </summary>
public static class SettingsChangeLog
{
    /// <summary>
    /// F-M750: eine Zeile für ein geändertes Feld der Quelldatei. Unveränderte Felder schweigen, damit
    /// ein Speichern ohne Änderung keine Zeile kostet.
    /// </summary>
    /// <param name="logger">Der Logger des Plugins.</param>
    /// <param name="sourceKey">Der Quellenschlüssel, z. B. <c>subdl</c>.</param>
    /// <param name="field">Das Feld, wie die Quelle es erklärt (Art entscheidet über die Anzeige).</param>
    /// <param name="oldValue">Der Wert, der in der Datei stand, oder null.</param>
    /// <param name="newValue">Der Wert, der jetzt in der Datei steht.</param>
    public static void SourceField(ILogger? logger, string sourceKey, SourceField field, string? oldValue, string newValue)
    {
        if (logger is null || field is null)
        {
            return;
        }

        if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        string alt = Anzeige(field.Kind, oldValue);
        string neu = Anzeige(field.Kind, newValue);
        if (string.Equals(alt, neu, StringComparison.Ordinal))
        {
            neu += " (neu)";
        }

        // Der Marker gehoert in den Text (F-M226), und der Pegel ist echtes Debug: eine seltene
        // Zeile, die der Betreiber sucht — der Server-Pegel entscheidet, ob sie im Protokoll steht.
        logger.LogDebug(
            LogUtil.DebugTag + "[Sub Scribe] setting written: {Source}/{Field} {Old} -> {New}",
            sourceKey,
            field.Key,
            alt,
            neu);
    }

    /// <summary>
    /// F-M750: die Zeilen für ein geschriebenes Plugin-Einstellungsobjekt — eine je geändertem
    /// Eigenschaftswert, Vergleiche in der Reihenfolge der Eigenschaften.
    /// </summary>
    /// <param name="logger">Der Logger des Plugins.</param>
    /// <param name="before">Der Stand vor dem Schreiben, oder null bei der ersten Schreibung.</param>
    /// <param name="after">Der Stand, der jetzt gilt.</param>
    public static void PluginSettings(ILogger? logger, PluginConfiguration? before, PluginConfiguration? after)
    {
        if (logger is null || after is null)
        {
            return;
        }

        foreach (PropertyInfo eigenschaft in typeof(PluginConfiguration)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            object? alt;
            object? neu;
            try
            {
                alt = before is null ? null : eigenschaft.GetValue(before);
                neu = eigenschaft.GetValue(after);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (Gleich(alt, neu))
            {
                continue;
            }

            string anzeigeAlt = Anzeige(alt);
            string anzeigeNeu = Anzeige(neu);
            if (string.Equals(anzeigeAlt, anzeigeNeu, StringComparison.Ordinal))
            {
                anzeigeNeu += " (neu)";
            }

            logger.LogDebug(
                LogUtil.DebugTag + "[Sub Scribe] setting written: {Property} {Old} -> {New}",
                eigenschaft.Name,
                anzeigeAlt,
                anzeigeNeu);
        }
    }

    private static string Anzahl(IEnumerable reihe)
    {
        int n = 0;
        foreach (object? _ in reihe)
        {
            n++;
        }

        return n.ToString(CultureInfo.InvariantCulture) + " value(s)";
    }

    private static string Anzeige(SourceFieldKind art, string? wert)
    {
        if (string.IsNullOrEmpty(wert))
        {
            return "(nichts)";
        }

        return art switch
        {
            SourceFieldKind.Switch or SourceFieldKind.Number or SourceFieldKind.Choice => wert,
            _ => "gesetzt",
        };
    }

    private static string Anzeige(object? wert) => wert switch
    {
        null => "(nichts)",
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        string s => s.Length == 0 ? "leer" : "gesetzt",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable reihe => Anzahl(reihe),
        _ => "gesetzt",
    };

    private static bool Gleich(object? alt, object? neu)
    {
        if (alt is null || neu is null)
        {
            return alt is null && neu is null;
        }

        if (alt is string a && neu is string b)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        if (alt is IEnumerable x && neu is IEnumerable y)
        {
            return Anzahl(x) == Anzahl(y);
        }

        return alt.Equals(neu);
    }
}
