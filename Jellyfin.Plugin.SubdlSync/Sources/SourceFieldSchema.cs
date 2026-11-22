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
using System.Linq;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M683: die Art eines Einstellungsfelds. Die Art entscheidet, wie das Feld angezeigt und wie sein
/// Wert geprüft wird — und ob der Wert jemals zurückgegeben werden darf (<see cref="SourceFieldKind.Secret"/>
/// nie).
/// </summary>
public enum SourceFieldKind
{
    /// <summary>Freier Text.</summary>
    Text,

    /// <summary>
    /// Ein Geheimnis (Schlüssel, Kennwort, Zugang). Der Wert wird gespeichert, aber NIE ausgeliefert —
    /// die Oberfläche erfährt nur, ob eines abgelegt ist.
    /// </summary>
    Secret,

    /// <summary>Eine Zahl, mit Grenzen (<see cref="SourceField.Min"/>/<see cref="SourceField.Max"/>).</summary>
    Number,

    /// <summary>Ein Schalter (wahr/falsch).</summary>
    Switch,

    /// <summary>Eine Auswahl aus <see cref="SourceField.Options"/>.</summary>
    Choice,

    /// <summary>Eine Adresse (http/https).</summary>
    Url,
}

/// <summary>
/// F-M683: „dieses Feld nur zeigen, wenn jenes Feld diesen Wert hat" — der Grund, warum eine Engine
/// ihre Felder FLEXIBEL einblenden kann, statt alle gleichzeitig zu zeigen.
/// <para>
/// Verglichen wird der WIRKSAME Wert des genannten Felds als Text (gespeicherter Wert, sonst die
/// Vorgabe). Bei einem Geheimfeld ist der Wert nie lesbar; dort bedeutet <c>gesetzt</c> „ein Geheimnis
/// ist abgelegt" und jeder andere Wert „keines".
/// </para>
/// </summary>
/// <param name="Field">Der stabile Schlüssel des anderen Felds.</param>
/// <param name="Value">Der erwartete Wert, als Text.</param>
public sealed record SourceFieldVisibility(string Field, string Value);

/// <summary>
/// F-M683: EIN Feld einer Quelle, so wie die Engine es erklärt — stabiler Schlüssel (der Speichername),
/// deutsche Beschriftung, Art, Vorgabe, Pflicht, Hilfetext, Grenzen, Auswahlmöglichkeiten und die
/// Bedingung, unter der es überhaupt sichtbar ist.
/// <para>
/// Die REIHENFOLGE der erklärten Felder ist die Reihenfolge, in der sie erscheinen — es gibt keine
/// zweite Sortierung und keine Nummer, die daneben laufen könnte.
/// </para>
/// <para>
/// Der Feldbeschreiber ist ein DATEN-Träger und keine Logik: die Engine erklärt hier nur, was sie
/// braucht. Gelesen wird über <see cref="SourceSettings"/>, geschrieben über
/// <see cref="SourceSettingsFile"/>.
/// </para>
/// </summary>
/// <param name="Key">Der stabile Schlüssel — zugleich der Name in der Datei der Engine.</param>
/// <param name="Label">Die deutsche Beschriftung, kurz und sachlich.</param>
/// <param name="Kind">Die Art des Felds.</param>
/// <param name="Default">Die Vorgabe als Text (bei <see cref="SourceFieldKind.Switch"/> „true"/„false").</param>
/// <param name="Required">Muss das Feld einen Wert haben, damit die Quelle einsatzbereit ist?</param>
/// <param name="Help">Ein optionaler Hilfetext.</param>
/// <param name="Min">Untere Grenze bei <see cref="SourceFieldKind.Number"/>.</param>
/// <param name="Max">Obere Grenze bei <see cref="SourceFieldKind.Number"/>.</param>
/// <param name="Options">Die Möglichkeiten bei <see cref="SourceFieldKind.Choice"/>.</param>
/// <param name="VisibleWhen">Nur zeigen, wenn ein anderes Feld einen bestimmten Wert hat — oder null.</param>
public sealed record SourceField(
    string Key,
    string Label,
    SourceFieldKind Kind,
    string Default = "",
    bool Required = false,
    string? Help = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Options = null,
    SourceFieldVisibility? VisibleWhen = null)
{
    /// <summary>Gets a value indicating whether this is a secret — its value never leaves the plugin.</summary>
    public bool IsSecret => Kind == SourceFieldKind.Secret;

    /// <summary>
    /// F-M683: der Name der Art, wie er in der Antwort des Endpunkts steht (<c>text</c>, <c>secret</c>,
    /// <c>number</c>, <c>switch</c>, <c>choice</c>, <c>url</c>).
    /// </summary>
    /// <param name="kind">Die Art.</param>
    /// <returns>Der Name in Kleinschreibung.</returns>
    public static string KindName(SourceFieldKind kind) => kind switch
    {
        SourceFieldKind.Secret => "secret",
        SourceFieldKind.Number => "number",
        SourceFieldKind.Switch => "switch",
        SourceFieldKind.Choice => "choice",
        SourceFieldKind.Url => "url",
        _ => "text",
    };

    /// <summary>
    /// F-M683: prüft EINEN vom Aufrufer gelieferten Wert gegen die Erklärung dieses Felds.
    /// <para>
    /// Geprüft wird, was die Erklärung hergibt: Pflicht (nicht leer), Grenzen einer Zahl, die
    /// Möglichkeiten einer Auswahl, das Schema einer Adresse. Ein leerer Text ist bei einem Feld ohne
    /// Pflicht ein gültiger Wert.
    /// </para>
    /// </summary>
    /// <param name="raw">Der Wert als Text, wie ihn die Oberfläche schickt.</param>
    /// <param name="clean">Der zu speichernde Wert (getrimmt, Zahl in invariantischer Schreibweise).</param>
    /// <returns>Der Grund, warum der Wert nicht gilt — oder null.</returns>
    public string? Validate(string? raw, out string clean)
    {
        clean = (raw ?? string.Empty).Trim();
        if (Required && clean.Length == 0)
        {
            return "Pflichtfeld '" + Key + "' fehlt";
        }

        switch (Kind)
        {
            case SourceFieldKind.Number:
                if (clean.Length == 0)
                {
                    return null;
                }

                if (!double.TryParse(clean, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double zahl))
                {
                    return "Feld '" + Key + "' ist keine Zahl";
                }

                if (Min is { } min && zahl < min)
                {
                    return "Feld '" + Key + "' liegt unter " + min.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (Max is { } max && zahl > max)
                {
                    return "Feld '" + Key + "' liegt über " + max.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                clean = zahl.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                return null;

            case SourceFieldKind.Switch:
                if (clean.Length == 0)
                {
                    return null;
                }

                if (bool.TryParse(clean, out bool an))
                {
                    clean = an ? "true" : "false";
                    return null;
                }

                if (clean is "1" or "0")
                {
                    clean = clean == "1" ? "true" : "false";
                    return null;
                }

                return "Feld '" + Key + "' ist kein Schalter (true/false)";

            case SourceFieldKind.Choice:
                if (clean.Length == 0 || (Options is { } optionen && optionen.Contains(clean, StringComparer.Ordinal)))
                {
                    return null;
                }

                return "Feld '" + Key + "' kennt '" + clean + "' nicht";

            case SourceFieldKind.Url:
                if (clean.Length == 0)
                {
                    return null;
                }

                if (!Uri.TryCreate(clean, UriKind.Absolute, out Uri? adresse)
                    || (adresse.Scheme != Uri.UriSchemeHttps && adresse.Scheme != Uri.UriSchemeHttp))
                {
                    return "Feld '" + Key + "' ist keine http(s)-Adresse";
                }

                clean = clean.TrimEnd('/');
                return null;

            default:
                return null;
        }
    }
}
