// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubScribe.Sources.Tvst;

/// <summary>
/// F-M735: die Sprachen DIESER Seite — welche Kürzel sie führt, wie sie heißen, welches ISO-Kennzeichen
/// dahintersteht und wie ein ankommendes Kennzeichen darauf abgebildet wird.
/// <para>
/// Der Ort ist die Quelle, nicht der Abruf: die Tafel ist ein Datenwert der Seite und ändert sich, wenn
/// die Seite sich ändert — sie ist keine Angelegenheit des HTTP-Codes. Der Abruf bekommt sie über
/// <see cref="TvSubtitlesClient.SiteLanguage"/> gereicht (F-M735, Brücke), gerechnet wird hier.
/// </para>
/// </summary>
public static class TvstLanguages
{
    /// <summary>Gets die geschlossene Liste der Kennzeichen, die diese Seite führt — leer gibt es nicht.</summary>
    public static IReadOnlyList<string> Supported => Site.Keys.ToList();

    /// <summary>
    /// F-M480: the language codes are the SITE's abbreviations, not ISO 639-1 — Portuguese is <c>pt</c>,
    /// Brazilian Portuguese is <c>br</c>, Chinese is <c>cn</c>, Japanese is <c>jp</c>, Greek is <c>gr</c>,
    /// Czech is <c>cz</c>. Sending an ISO code the select does not offer makes the form fall back to its
    /// first entry (the upload lands as English), so the codes are kept verbatim from the page.
    /// The value is (name as written on the page, three-letter code used in file names).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Iso3)> Site =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["en"] = ("english", "eng"),
            ["es"] = ("spanish", "spa"),
            ["fr"] = ("french", "fre"),
            ["de"] = ("german", "ger"),
            ["br"] = ("portuguese(br)", "pob"),
            ["ru"] = ("russian", "rus"),
            ["ua"] = ("ukrainian", "ukr"),
            ["it"] = ("italian", "ita"),
            ["gr"] = ("greek", "gre"),
            ["ar"] = ("arabic", "ara"),
            ["hu"] = ("hungarian", "hun"),
            ["pl"] = ("polish", "pol"),
            ["tr"] = ("turkish", "tur"),
            ["nl"] = ("dutch", "dut"),
            ["pt"] = ("portuguese", "por"),
            ["sv"] = ("swedish", "swe"),
            ["da"] = ("danish", "dan"),
            ["fi"] = ("finnish", "fin"),
            ["ko"] = ("korean", "kor"),
            ["cn"] = ("chinese", "chi"),
            ["jp"] = ("japanese", "jpn"),
            ["bg"] = ("bulgarian", "bul"),
            ["cz"] = ("czech", "cze"),
            ["ro"] = ("romanian", "rum"),
        };

    /// <summary>
    /// F-M502 (Operator-Anweisung 15.11.2026: „Aber halt tvst spezifisch. Das andere mapping ist subdl
    /// spezifisch."): die Bruecke von ISO- auf die Kuerzel der Seite, und zwar AUSSCHLIESSLICH hier.
    /// <para>
    /// Gemessen am 15.11.2026: die Tabelle oben ist vollstaendig — die Seite kennt genau diese 24
    /// Kuerzel —, aber vier davon sind NICHT ISO: <c>gr</c> statt <c>el</c>/<c>ell</c>/<c>gre</c>
    /// (Griechisch), <c>cz</c> statt <c>cs</c>/<c>ces</c>/<c>cze</c> (Tschechisch), <c>cn</c> statt
    /// <c>zh</c>/<c>zho</c>/<c>chi</c> (Chinesisch), <c>jp</c> statt <c>ja</c>/<c>jpn</c> (Japanisch).
    /// Jellyfin liefert Sprachen als DREI-Buchstaben-Codes (<c>gre</c>, <c>cze</c>, <c>chi</c>,
    /// <c>jpn</c>), die Konfiguration und die Zielsprachen teils als ZWEI-Buchstaben-Codes
    /// (<c>el</c>, <c>cs</c>, <c>zh</c>, <c>ja</c>) — beide Formen landeten vorher in der Pruefung von
    /// <see cref="BuildUpload"/> und der Upload wurde mit „language must be one of the site's codes"
    /// abgelehnt. Deshalb die vier Abweichungen in beiden Formen, dazu die gaengigen
    /// ISO-639-2-Formen (/B und /T) der uebrigen Seiten-Sprachen.
    /// </para>
    /// <para>
    /// Was hier ABSICHTLICH NICHT passiert: keine gemeinsame Tabelle mit SubDL (Operator-Anweisung
    /// oben — SubDL bildet in <see cref="ScribeApiClient"/> ab), kein Raten ausserhalb der Liste
    /// (unbekannt heisst null), und die Seiten-Kuerzel selbst bleiben unveraendert gueltig.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Die vier Abweichungen der Seite, in jeder gaengigen ISO-Schreibweise.
            ["el"] = "gr",
            ["ell"] = "gr",
            ["gre"] = "gr",
            ["cs"] = "cz",
            ["ces"] = "cz",
            ["cze"] = "cz",
            ["zh"] = "cn",
            ["zho"] = "cn",
            ["chi"] = "cn",
            ["ja"] = "jp",
            ["jpn"] = "jp",

            // Die uebrigen Seiten-Sprachen in ISO-639-2/B und /T.
            ["uk"] = "ua",
            ["eng"] = "en",
            ["spa"] = "es",
            ["fra"] = "fr",
            ["fre"] = "fr",
            ["deu"] = "de",
            ["ger"] = "de",
            ["pob"] = "br",
            ["rus"] = "ru",
            ["ukr"] = "ua",
            ["ita"] = "it",
            ["ara"] = "ar",
            ["hun"] = "hu",
            ["pol"] = "pl",
            ["tur"] = "tr",
            ["nld"] = "nl",
            ["dut"] = "nl",
            ["por"] = "pt",
            ["swe"] = "sv",
            ["dan"] = "da",
            ["fin"] = "fi",
            ["kor"] = "ko",
            ["bul"] = "bg",
            ["ron"] = "ro",
            ["rum"] = "ro",
        };

    /// <summary>
    /// F-M502: der Sprachcode, den die Seite versteht — aus einem ISO-Code, einem Seiten-Kuerzel oder
    /// einer Angabe mit Region/Skript (<c>pt-BR</c>, <c>zh-Hans</c>).
    /// <para>
    /// Gross-/Kleinschreibung zaehlt nicht. Die Region entscheidet nur dort, wo die Seite die Sprache
    /// danach trennt: <c>pt-BR</c> ist brasilianisches Portugiesisch (<c>br</c>), waehrend <c>pt-PT</c>
    /// und <c>pt</c> europaesisches Portugiesisch (<c>pt</c>) bleiben. Bei allen anderen Sprachen
    /// faellt die Region weg (<c>zh-Hans</c>/<c>zh-CN</c> → <c>cn</c>).
    /// </para>
    /// </summary>
    /// <param name="code">Der ankommende Code, in beliebiger Schreibweise.</param>
    /// <returns>Ein Kuerzel aus <see cref="Site"/>, oder null — dann ist die Sprache der Seite
    /// unbekannt und wird nicht geraten.</returns>
    public static string? ToSite(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        // Ein Schraegstrich kommt in Tags vor ("pt_BR"); die Seite kennt nur den Bindestrich.
        var text = code.Trim().ToLowerInvariant().Replace('_', '-');

        // Die eine Stelle, an der die REGION die Sprache ist: brasilianisches Portugiesisch.
        // Muss vor der Grundcode-Reduktion stehen, sonst faellt "pt-br" auf europaesisches "pt".
        if (string.Equals(text, "pt-br", StringComparison.Ordinal))
        {
            return "br";
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var baseCode = dash > 0 ? text[..dash] : text;

        if (LanguageAliases.TryGetValue(baseCode, out var mapped))
        {
            return mapped;
        }

        // Das Kuerzel der Seite selbst (en, de, br, gr, cz, cn, jp, ua …) ist bereits das Ziel.
        return Site.ContainsKey(baseCode) ? baseCode : null;
    }

    /// <summary>
    /// F-M735: die Gegenrichtung — vom Kürzel der Seite auf das ISO-639-2-Kennzeichen, das der Bestand
    /// führt (F-M557). Unbekannt heißt null; geraten wird nicht.
    /// </summary>
    /// <param name="siteCode">Ein Kürzel der Seite, wie es in ihren Einträgen steht.</param>
    /// <returns>Das ISO-Kennzeichen in Großbuchstaben, oder null.</returns>
    public static string? FromSite(string? siteCode)
    {
        if (string.IsNullOrWhiteSpace(siteCode))
        {
            return null;
        }

        return Site.TryGetValue(siteCode.Trim().ToLowerInvariant(), out var entry)
            ? entry.Iso3.ToUpperInvariant()
            : null;
    }
}
