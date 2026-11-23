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

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M683: die eingelesenen Einstellungen EINER Quelle — die Werte ihrer eigenen Datei, gelesen über
/// ihre eigene Erklärung, mit den Lesehelfern, die eine Engine braucht (Text, Geheimnis, Zahl,
/// Schalter, Adresse — jeweils mit Vorgabe).
/// <para>
/// Der Zustand „nicht eingerichtet" ist ein ERGEBNIS und keine Ausnahme: eine fehlende Datei liefert
/// leere Werte und <see cref="Problem"/> = null (die Vorgaben gelten), eine unlesbare oder kaputte
/// Datei liefert <see cref="Problem"/> im Klartext, <see cref="IsConfigured"/> = falsch und sonst
/// nichts. Der Aufrufer entscheidet dann über DIESE eine Quelle; die anderen laufen weiter, weil jede
/// Quelle ihre eigene Datei liest (F-M683).
/// </para>
/// </summary>
public sealed class SourceSettings
{
    private readonly Dictionary<string, string> _stored;
    private readonly Dictionary<string, SourceField> _fields;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceSettings"/> class.
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="schema">Die erklärten Felder dieser Quelle (Reihenfolge = Erklärungsreihenfolge).</param>
    /// <param name="stored">Die gespeicherten Werte als Text; leer, wenn es keine Datei gibt.</param>
    /// <param name="problem">Der Klartext-Hinweis, warum die Datei nicht gilt — oder null.</param>
    /// <param name="path">Der Pfad der Datei, oder null wenn kein Datenordner bekannt ist.</param>
    public SourceSettings(
        string key,
        IReadOnlyList<SourceField> schema,
        IReadOnlyDictionary<string, string>? stored,
        string? problem,
        string? path)
    {
        Key = key ?? string.Empty;
        Schema = schema ?? Array.Empty<SourceField>();
        _stored = stored is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(stored, StringComparer.Ordinal);
        Problem = string.IsNullOrWhiteSpace(problem) ? null : problem;
        Path = path;
        _fields = Schema.ToDictionary(f => f.Key, f => f, StringComparer.Ordinal);
    }

    /// <summary>Gets der Quellenschlüssel, dem diese Einstellungen gehören.</summary>
    public string Key { get; }

    /// <summary>Gets die erklärten Felder dieser Quelle.</summary>
    public IReadOnlyList<SourceField> Schema { get; }

    /// <summary>Gets die Datei, aus der gelesen wurde (null, wenn kein Datenordner bekannt ist).</summary>
    public string? Path { get; }

    /// <summary>
    /// Gets der Klartext-Hinweis, warum diese Quelle als NICHT eingerichtet gilt — oder null, wenn die
    /// Datei fehlt (dann gelten die Vorgaben) oder gelesen werden konnte.
    /// </summary>
    public string? Problem { get; }

    /// <summary>Gets a value indicating whether this source may take part (its file is usable).</summary>
    public bool IsConfigured => Problem is null;

    /// <summary>Gets die gespeicherten Werte als Text — nur was die Datei wirklich trägt.</summary>
    public IReadOnlyDictionary<string, string> Stored => _stored;

    /// <summary>
    /// F-M683: trägt die Datei dieses Feld? (Ein leerer Wert zählt nicht — er ist „nicht gesetzt".)
    /// </summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Wahr, wenn ein nicht-leerer Wert gespeichert ist.</returns>
    public bool Has(string key)
        => _stored.TryGetValue(key, out string? wert) && wert.Length > 0;

    /// <summary>F-M683: ein Textfeld, mit seiner Vorgabe, wenn nichts gespeichert ist.</summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Der wirksame Wert.</returns>
    public string Text(string key)
        => _stored.TryGetValue(key, out string? wert) && wert.Length > 0
            ? wert
            : Field(key)?.Default ?? string.Empty;

    /// <summary>
    /// F-M683: ein Geheimnis. Es gibt keine Vorgabe — ein Geheimnis, das niemand eingetragen hat, ist
    /// leer und nicht etwa „der Wert aus dem Quelltext".
    /// </summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Der gespeicherte Wert, oder leer.</returns>
    public string Secret(string key)
        => _stored.TryGetValue(key, out string? wert) ? wert : string.Empty;

    /// <summary>F-M683: eine Zahl, mit ihrer Vorgabe (0, wenn weder Wert noch Vorgabe eine Zahl ist).</summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Der wirksame Wert.</returns>
    public double Number(string key)
        => double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double zahl) ? zahl : 0;

    /// <summary>
    /// F-M683: ein Schalter, mit seiner Vorgabe. Gespeichert wird „true"/„false"; <c>1</c>/<c>0</c> und
    /// <c>an</c>/<c>aus</c> werden gelesen, damit eine von Hand geschriebene Datei nicht stillschweigend
    /// als „aus" gilt.
    /// </summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Der wirksame Wert.</returns>
    public bool Switch(string key)
    {
        string roh = Text(key).Trim();
        if (roh.Length == 0)
        {
            return false;
        }

        if (bool.TryParse(roh, out bool an))
        {
            return an;
        }

        return roh is "1" || string.Equals(roh, "an", StringComparison.OrdinalIgnoreCase)
            || string.Equals(roh, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F-M683: eine Adresse, mit ihrer Vorgabe. Ein abschließender Schrägstrich fällt weg, damit zwei
    /// Schreibweisen derselben Adresse nicht als zwei Adressen gelten.
    /// </summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Die wirksame Adresse, ohne abschließenden Schrägstrich.</returns>
    public string Url(string key) => Text(key).TrimEnd('/');

    /// <summary>F-M683: die Erklärung eines Felds, oder null wenn diese Quelle es nicht erklärt.</summary>
    /// <param name="key">Der Feldschlüssel.</param>
    /// <returns>Die Erklärung.</returns>
    public SourceField? Field(string key)
        => _fields.TryGetValue(key, out SourceField? feld) ? feld : null;

    /// <summary>
    /// F-M683: Einstellungen ohne Datei — die Vorgaben gelten, nichts ist gespeichert. Das ist der
    /// Zustand eines Laufs, dem kein Datenordner gereicht wurde (und der einer frischen Installation,
    /// bevor die Übernahme geschrieben hat).
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="schema">Die erklärten Felder.</param>
    /// <param name="path">Der Pfad, der gelten würde — oder null.</param>
    /// <returns>Die leeren Einstellungen.</returns>
    public static SourceSettings Empty(string key, IReadOnlyList<SourceField> schema, string? path = null)
        => new(key, schema, null, null, path);
}
