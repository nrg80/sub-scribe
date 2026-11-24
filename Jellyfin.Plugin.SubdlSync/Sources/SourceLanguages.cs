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

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M735: die Sprachauskunft je Quelle, an EINER Stelle zusammengestellt — hier gibt jede Quelle ihre
/// Tafel und ihre beiden Umrechnungen her. Wer sie braucht (Engine, Ablauf, Einstellungsansicht), fragt
/// über den Schlüssel der Quelle und nennt keinen Client und keinen Engine-Namensraum.
/// </summary>
public static class SourceLanguages
{
    /// <summary>
    /// Gets die Sprachauskunft einer Quelle, oder <c>null</c> für einen unbekannten Schlüssel.
    /// </summary>
    /// <param name="key">Der Schlüssel der Quelle, wie ihn der Bestand führt.</param>
    /// <returns>Die Auskunft, oder null.</returns>
    public static SourceLanguageVoice? Voice(string? key) => key switch
    {
        Tvst.TvstEngine.SourceKey => new SourceLanguageVoice(
            Tvst.TvstLanguages.ToSite, Tvst.TvstLanguages.FromSite, Tvst.TvstLanguages.Supported),
        Msub.MsubEngine.SourceKey => new SourceLanguageVoice(
            Msub.MsubLanguages.ToSite, Msub.MsubLanguages.FromSite, Msub.MsubLanguages.Supported),
        Subdl.SubdlEngine.SourceKey => new SourceLanguageVoice(
            Subdl.SubdlLanguages.ToSite, Subdl.SubdlLanguages.FromSite, Subdl.SubdlLanguages.Supported),
        _ => null,
    };

    /// <summary>
    /// F-M735: führt diese Quelle diese Sprache? Ein unbekannter Schlüssel zählt als „führt sie" — die
    /// Frage darf eine Sendung nie schließen, die vorher offen war.
    /// </summary>
    /// <param name="key">Der Schlüssel der Quelle.</param>
    /// <param name="language">Das Sprachkennzeichen, in beliebiger Schreibweise.</param>
    /// <returns>True, wenn die Quelle es führt oder keine Liste führt.</returns>
    public static bool NimmtSprache(string? key, string? language)
    {
        SourceLanguageVoice? stimme = Voice(key);
        return stimme is null || stimme.ToSite(language) is not null;
    }
}
