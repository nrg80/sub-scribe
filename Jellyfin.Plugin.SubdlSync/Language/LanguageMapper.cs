// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubScribe.Language;

/// <summary>
/// Die Sprachbrücke des Clients (F-M10, F-M790). Sie trennt drei Aufgaben sauber: den Kanon bilden
/// (<see cref="Canonical"/>, ISO 639-2/T in Großbuchstaben), an der Naht übersetzen
/// (<see cref="MapToSubdl"/>, <see cref="ToIso6392"/>) und sagen, ob ein Kennzeichen überhaupt
/// bekannt ist (<see cref="KnowsTwoLetter"/>, <see cref="KnowsThreeLetter"/>).
/// </summary>
/// <remarks>
/// <para>
/// F-M797: die Listen sind nicht getippt, sondern aus der amtlichen ISO-639-2-Liste der
/// Registrierungsstelle erzeugt (<see cref="LanguageTable"/>, Erzeuger
/// <c>scripts/tools/gen-language-table.py</c>). Damit kennt der Client 183 zweistellige und 486
/// dreistellige Kennzeichen statt der 73 und 106, die von Hand zusammengetragen waren.
/// </para>
/// <para>
/// F-M798: eine unbekannte Kennung wird nicht mehr geraten. Bis 12.3.8.86 schnitt
/// <see cref="MapToSubdl"/> die ersten zwei Buchstaben ab — aus <c>FIJ</c> (Fidschi) wurde <c>FI</c>
/// (Finnisch), aus <c>und</c> wurde <c>UN</c>. Eine gültige Kennung für die falsche Sprache ist
/// schlimmer als keine: die Suche läuft, findet nichts und meldet nichts.
/// </para>
/// </remarks>
public static class LanguageMapper
{
    /// <summary>
    /// Die vier Kennzeichen, die keine Sprache benennen, sondern deren Fehlen: unbestimmt, mehrere
    /// Sprachen, kein sprachlicher Inhalt, nicht erfasst (F-M798).
    /// </summary>
    private static readonly HashSet<string> NoLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "und", "mul", "mis", "zxx"
    };

    /// <summary>
    /// Kennzeichen der amtlichen Liste, nach zweistelliger Form (183 Stück).
    /// </summary>
    private static readonly Dictionary<string, LanguageTable.Row> ByTwoLetter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kennzeichen der amtlichen Liste, nach beiden dreistelligen Formen (B und T).
    /// </summary>
    private static readonly Dictionary<string, LanguageTable.Row> ByThreeLetter = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Englischer Name aus der amtlichen Liste, kleingeschrieben, auf die zweistellige Form; der
    /// erste Treffer gilt, sollte ein Name zweimal vorkommen.
    /// </summary>
    private static readonly Dictionary<string, string> ByEnglishName = new(StringComparer.Ordinal);

    /// <summary>
    /// F-M797: was die Quelle selbst anders führt als die Norm. SubDL hat seine eigene Sprachliste,
    /// und an dieser Naht gilt sie. Jeder Eintrag nennt den Grund.
    /// </summary>
    private static readonly Dictionary<string, string> SeamOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // Filipino hat in ISO 639-1 keinen eigenen Zweisteller; SubDL führt die Sprache unter dem
        // Tagalog-Kennzeichen TL, und Sprecher beider suchen dieselben Untertitel.
        ["fil"] = "TL",

        // Norwegisch ist EINE Sprache (F-M371): Bokmål und Nynorsk werden nicht getrennt, die Norm
        // gäbe NB beziehungsweise NN — die Quelle führt nur NO.
        ["nob"] = "NO",
        ["nno"] = "NO",

        // SCC ist das 2017 zurückgezogene Kennzeichen für Serbisch; alte Bestände tragen es weiter.
        ["scc"] = "SR"
    };

    /// <summary>
    /// F-M797: Sprachpaare, die die amtliche Liste nicht mehr führt, die aber in Beständen stehen.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyBtoT = new(StringComparer.OrdinalIgnoreCase)
    {
        // Moldauisch ist 2008 in Rumänisch aufgegangen; das Kennzeichen steht noch in alten Tags.
        ["mol"] = "ron"
    };

    /// <summary>
    /// F-M797: Sprachnamen, die die Quelle führt und die die amtliche Liste so nicht kennt.
    /// </summary>
    private static readonly Dictionary<string, string> SeamNameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Filipino"] = "TL",
        ["Brazilian Portuguese"] = "PT",
        ["Portuguese (Brazil)"] = "PT",
        ["Latin Spanish"] = "ES",
        ["Spanish (Latin America)"] = "ES"
    };

    static LanguageMapper()
    {
        foreach (LanguageTable.Row row in LanguageTable.Rows)
        {
            if (row.TwoLetter.Length == 2)
            {
                ByTwoLetter[row.TwoLetter] = row;
                ByEnglishName.TryAdd(row.English.ToLowerInvariant(), row.TwoLetter);
            }

            ByThreeLetter[row.Bibliographic] = row;
            ByThreeLetter[row.Terminologic] = row;
        }

        // Die amtliche Liste führt Bokmål und Nynorsk als eigene Zeilen; für den Kanon bleiben sie
        // Norwegisch, weil der Store Norwegisch als eine Sprache führt (F-M371).
        if (ByThreeLetter.TryGetValue("nor", out LanguageTable.Row norwegisch))
        {
            foreach (string form in new[] { "nob", "nno" })
            {
                if (ByThreeLetter.TryGetValue(form, out LanguageTable.Row teil))
                {
                    ByThreeLetter[teil.Bibliographic] = norwegisch;
                    ByThreeLetter[teil.Terminologic] = norwegisch;
                }
            }
        }
    }

    /// <summary>
    /// F-M790: die EINE Schreibweise, in der der Store eine Sprache führt — ISO 639-2/T in
    /// Großbuchstaben (<c>ENG</c>, <c>DEU</c>). Die bibliografische Form ist Eingabe und wird hier
    /// übersetzt; ein unbekanntes Kennzeichen wird nicht erfunden, sondern unverändert
    /// großgeschrieben zurückgegeben und vom Startlauf benannt (F-M791, F-M798).
    /// </summary>
    /// <param name="code">Ein Sprachkennzeichen, gleich in welcher Schreibweise.</param>
    /// <returns>Das ISO-639-2/T-Kennzeichen in Großbuchstaben, oder null bei leerer Eingabe.</returns>
    public static string? Canonical(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        string trimmed = code.Trim();
        if (Knowledge(trimmed) is { } zeile)
        {
            return zeile.Terminologic.ToUpperInvariant();
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// F-M791: kennt die Tabelle diesen zweistelligen Code?
    /// </summary>
    /// <param name="code">Zweistelliger Code, z. B. <c>de</c>.</param>
    /// <returns>True, wenn die amtliche Liste ihn führt.</returns>
    public static bool KnowsTwoLetter(string? code)
        => !string.IsNullOrWhiteSpace(code) && code.Trim().Length == 2 && ByTwoLetter.ContainsKey(code.Trim());

    /// <summary>
    /// F-M797: kennt die Tabelle dieses dreistellige Kennzeichen, in bibliografischer oder
    /// terminologischer Form?
    /// </summary>
    /// <param name="code">Dreistelliges Kennzeichen, z. B. <c>deu</c> oder <c>ger</c>.</param>
    /// <returns>True, wenn die amtliche Liste es führt.</returns>
    public static bool KnowsThreeLetter(string? code)
        => !string.IsNullOrWhiteSpace(code)
           && code.Trim().Length == 3
           && ByThreeLetter.ContainsKey(code.Trim());

    /// <summary>
    /// F-M798: benennt dieses Kennzeichen eine Sprache — oder gerade deren Fehlen?
    /// </summary>
    /// <param name="code">Ein Sprachkennzeichen.</param>
    /// <returns>True bei <c>und</c>, <c>mul</c>, <c>mis</c> und <c>zxx</c>.</returns>
    public static bool IsNoLanguage(string? code)
        => !string.IsNullOrWhiteSpace(code) && NoLanguageCodes.Contains(code.Trim());

    /// <summary>
    /// F-M790: eine gespeicherte Sprachliste ("AR, EN, DE") in die eine Form bringen — jedes Glied
    /// einzeln, Dubletten fallen weg, die Reihenfolge ist die alte (OrdinalIgnoreCase).
    /// </summary>
    /// <param name="list">Komma-getrennte Liste, darf leer oder null sein.</param>
    /// <returns>Dieselbe Liste im Kanon, komma-getrennt.</returns>
    public static string CanonicalList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
        {
            return string.Empty;
        }

        return string.Join(
            ",",
            list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Canonical)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// F-M261: übersetzt ein Kennzeichen in die Form, die ein Matroska-Tag trägt — die
    /// bibliografische (<c>ger</c>), weil ffprobe sie so zurückgibt.
    /// </summary>
    /// <param name="code">Zweistelliges Kennzeichen oder eine dreistellige Form.</param>
    /// <returns>
    /// Die bibliografische Form in Kleinbuchstaben; bei unbekannter Eingabe die Eingabe
    /// kleingeschrieben — ein brauchbares Tag ist besser als keines. Null nur bei leerer Eingabe.
    /// </returns>
    public static string? ToIso6392(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        string key = code.Trim();
        if (Knowledge(key) is { } zeile)
        {
            return zeile.Bibliographic.ToLowerInvariant();
        }

        return key.Length is >= 2 and <= 3 && char.IsLetter(key[0])
            ? key.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Übersetzt ein Sprachkennzeichen in den zweistelligen Code der Quelle (F-M10).
    /// </summary>
    /// <remarks>
    /// F-M798: unbekannt heißt unbekannt. Ein Kennzeichen, das die amtliche Liste nicht führt, eine
    /// Sprache ohne zweistelligen Code und die vier „keine Sprache"-Kennzeichen ergeben null — der
    /// Aufrufer entscheidet, was er damit tut (SubDL lehnt den Auftrag mit „language unknown to
    /// SubDL" ab, die Suche lässt die Sprache weg). Geraten wird nichts mehr.
    /// </remarks>
    /// <param name="code">Dreistelliges Kennzeichen, gleich in welcher Form, oder ein Zweisteller.</param>
    /// <returns>Der zweistellige Code der Quelle, oder null wenn es keinen gibt.</returns>
    public static string? MapToSubdl(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        string kanon = Canonical(code) ?? string.Empty;
        if (kanon.Length == 0 || NoLanguageCodes.Contains(kanon))
        {
            return null;
        }

        string key = kanon.ToLowerInvariant();
        if (SeamOverrides.TryGetValue(key, out string? eigene))
        {
            return eigene;
        }

        if (ByThreeLetter.TryGetValue(key, out LanguageTable.Row zeile))
        {
            return zeile.TwoLetter.Length == 2 ? zeile.TwoLetter.ToUpperInvariant() : null;
        }

        return ByTwoLetter.TryGetValue(key, out LanguageTable.Row zwei)
            ? zwei.TwoLetter.ToUpperInvariant()
            : null;
    }

    /// <summary>
    /// F-M17y: der umgekehrte Weg — vom englischen Sprachnamen der Quelle auf ihren zweistelligen
    /// Code. Unbekannte Namen ergeben null (F-M798).
    /// </summary>
    /// <param name="languageName">Der Name, den die Quelle nennt, z. B. <c>English</c>.</param>
    /// <returns>Der zweistellige Code, oder null.</returns>
    public static string? MapToSubdlName(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return null;
        }

        string name = languageName.Trim();
        if (SeamNameOverrides.TryGetValue(name, out string? eigener))
        {
            return eigener;
        }

        return ByEnglishName.TryGetValue(name.ToLowerInvariant(), out string? code)
            ? code.ToUpperInvariant()
            : null;
    }

    /// <summary>
    /// Die Zeile der amtlichen Liste zu einem Kennzeichen, gleich in welcher Länge und Form.
    /// </summary>
    /// <param name="code">Zwei- oder dreistelliges Kennzeichen; die B/T-Wanderung steckt in der Tafel.</param>
    /// <returns>Die Zeile, oder null.</returns>
    private static LanguageTable.Row? Knowledge(string code)
    {
        if (code.Length == 2)
        {
            return ByTwoLetter.TryGetValue(code, out LanguageTable.Row zwei) ? zwei : null;
        }

        if (code.Length == 3)
        {
            if (ByThreeLetter.TryGetValue(code, out LanguageTable.Row drei))
            {
                return drei;
            }

            if (LegacyBtoT.TryGetValue(code, out string? terminologisch))
            {
                return ByThreeLetter.TryGetValue(terminologisch, out LanguageTable.Row alt) ? alt : null;
            }
        }

        return null;
    }
}
