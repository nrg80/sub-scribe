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
using Jellyfin.Plugin.SubScribe.Api;

namespace Jellyfin.Plugin.SubScribe.Sources.Msub;

/// <summary>
/// F-M735: die Sprachen DIESER Seite — dreizehn Werte, wie sie in ihrem Formular stehen, mit ISO-Kennzeichen
/// und Datei-Endung, dazu die Umrechnung in beide Richtungen.
/// <para>
/// Der Ort ist die Quelle, nicht der Abruf: die Tafel ist ein Datenwert der Seite. Der Abruf bekommt sie
/// über <see cref="MovieSubtitlesClient.SiteLanguage"/> gereicht (F-M735, Brücke).
/// </para>
/// </summary>
public static class MsubLanguages
{
    /// <summary>Gets die geschlossene Liste der Kennzeichen, die diese Seite führt — leer gibt es nicht.</summary>
    public static IReadOnlyList<string> Supported => Site.Keys.ToList();

    /// <summary>
    /// F-M506: the language table of THIS site — thirteen values, taken verbatim from
    /// <c>&lt;select name="lang"&gt;</c> on <c>add.php</c> (measured 15.11.2026). It is NOT the
    /// tvsubtitles.net table: that one carries 24 codes, this one 13, and the two disagree in the parts
    /// that matter (no <c>cz</c>, no <c>cn</c>, no <c>jp</c>, no European Portuguese, no Dutch, no
    /// Swedish, no Danish, no Finnish, no Korean, no Bulgarian, no Romanian).
    /// <para>
    /// The value carries (a) the display name exactly as the form spells it — <c>portugese(br)</c>, with
    /// the site's own typo — and (b) the code the site writes into the download name. That code is the
    /// select value itself (measured: <c>Sample_Film_JJJJ.WEB-DL.GRP.en.zip</c> and
    /// <c>Sample_Film_JJJJ.ru(2).zip</c>), not an ISO-639-2/B form — which is exactly the difference to
    /// <see cref="TvSubtitlesClient.Languages"/>, whose third column is an ISO3 code.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, MovieSubtitlesLanguage> Site =
        new Dictionary<string, MovieSubtitlesLanguage>(StringComparer.Ordinal)
        {
            ["en"] = new("en", "english", "en"),
            ["es"] = new("es", "spanish", "es"),
            ["fr"] = new("fr", "french", "fr"),
            ["de"] = new("de", "german", "de"),
            ["br"] = new("br", "portugese(br)", "br"),
            ["ru"] = new("ru", "russian", "ru"),
            ["ua"] = new("ua", "ukrainian", "ua"),
            ["it"] = new("it", "italian", "it"),
            ["gr"] = new("gr", "greek", "gr"),
            ["ar"] = new("ar", "arabic", "ar"),
            ["hu"] = new("hu", "hungarian", "hu"),
            ["pl"] = new("pl", "polish", "pl"),
            ["tr"] = new("tr", "turkish", "tr"),
        };

    /// <summary>
    /// F-M506: die Bruecke von ISO auf die Kuerzel DIESER Seite — und nur hier (dieselbe Trennung wie
    /// F-M502 fuer tvsubtitles.net: kein gemeinsames Mapping mit SubDL, keins mit der Schwester).
    /// <para>
    /// Die Liste ist absichtlich KURZ. Aufgenommen ist nur, was die 13er-Liste wirklich kennt:
    /// <c>eng spa fra/fre deu/ger pob rus uk/ukr ita el/ell/gre ara hun pol tur</c> und die Region, die
    /// die Sprache ist (<c>pt-BR</c>/<c>pt_BR</c> → <c>br</c>). NICHT aufgenommen — und damit
    /// verweigert — sind die Codes, die die Seite nicht fuehrt: <c>pt</c>/<c>por</c> (europaeisches
    /// Portugiesisch; die Seite kennt NUR die brasilianische Fassung, und <c>pt</c> auf <c>br</c> zu
    /// ziehen waere eine falsche Kennzeichnung), <c>nl dut nld sv swe da dan fi fin ko kor cn zh zho chi
    /// jp ja jpn bg bul cs ces cze ro ron rum</c>.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LanguageAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eng"] = "en",
            ["spa"] = "es",
            ["fra"] = "fr",
            ["fre"] = "fr",
            ["deu"] = "de",
            ["ger"] = "de",
            ["pob"] = "br",
            ["rus"] = "ru",
            ["uk"] = "ua",
            ["ukr"] = "ua",
            ["ita"] = "it",
            ["el"] = "gr",
            ["ell"] = "gr",
            ["gre"] = "gr",
            ["ara"] = "ar",
            ["hun"] = "hu",
            ["pol"] = "pl",
            ["tur"] = "tr",
        };

    /// <summary>
    /// F-M506: der Sprachcode, den die Seite versteht — aus einem ISO-Code, einem Seitenkuerzel oder
    /// einer Angabe mit Region/Skript (<c>pt-BR</c>, <c>zh-Hans</c>).
    /// <para>
    /// Gross-/Kleinschreibung zaehlt nicht. Die Region entscheidet nur dort, wo die Seite die Sprache
    /// danach trennt — und das tut sie nur bei Portugiesisch: <c>pt-BR</c> ist <c>br</c>. Bei allen
    /// anderen Sprachen faellt die Region weg; hilft das nicht weiter, ist die Antwort <c>null</c>.
    /// </para>
    /// </summary>
    /// <param name="code">Der ankommende Code, in beliebiger Schreibweise.</param>
    /// <returns>Ein Kuerzel aus <see cref="Site"/>, oder null — dann ist die Sprache der Seite
    /// unbekannt und wird NICHT auf Englisch zurueckgefallen und nicht geraten.</returns>
    public static string? ToSite(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var text = code.Trim().ToLowerInvariant().Replace('_', '-');

        // Die eine Stelle, an der die REGION die Sprache ist: brasilianisches Portugiesisch.
        // Muss vor der Grundcode-Reduktion stehen, sonst faellt "pt-br" auf "pt" und damit ins Leere.
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

        // Das Kuerzel der Seite selbst (en, de, br, gr, ua …) ist bereits das Ziel.
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

        string code = siteCode.Trim().ToLowerInvariant();
        if (!Site.ContainsKey(code))
        {
            return null;
        }

        // Das ISO-Kennzeichen steht in den Aliasen, die auf dieses Kuerzel zeigen — die Dreierform ist die
        // des Bestandes (F-M557), die Zweierform nur die Schreibweise der Seite.
        foreach (var pair in LanguageAliases)
        {
            if (pair.Value == code && pair.Key.Length == 3)
            {
                return pair.Key.ToUpperInvariant();
            }
        }

        // Kein Alias: dann ueber die eine neutrale Umrechnung, und nur wenn sie ein Dreierkennzeichen
        // ergibt. Ein Zweierbuchstabe waere eine Behauptung, die niemand deckt — dann lieber null.
        string? iso = Language.LanguageMapper.Canonical(code);
        return iso is { Length: 3 } ? iso.ToUpperInvariant() : null;
    }
}
