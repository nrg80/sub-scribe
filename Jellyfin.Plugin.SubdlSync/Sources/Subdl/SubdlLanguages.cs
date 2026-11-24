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
using System.Collections.Generic;
using Jellyfin.Plugin.SubScribe.Language;

namespace Jellyfin.Plugin.SubScribe.Sources.Subdl;

/// <summary>
/// F-M735: die Sprachen dieser Quelle — und der eine Fall, in dem es KEINE geschlossene Liste gibt.
/// <para>
/// tvsubtitles und moviesubtitles führen je ein festes Formular mit ihren Kürzeln; was nicht darin steht,
/// können sie nicht annehmen, und sie sagen das. SubDL führt kein solches Formular: die Seite nimmt
/// Zwei-Buchstaben-Codes, und welche Sprachen es dort gibt, zeigt erst die Suche. Deshalb ist
/// <see cref="Supported"/> hier <c>null</c> — „ich habe keine Liste" ist eine andere Auskunft als „ich kann
/// nichts" (leere Liste), und nur die erste darf das Senden nicht aufhalten.
/// </para>
/// <para>
/// Die Umrechnung selbst bleibt in <see cref="LanguageMapper"/>: das ist die eine neutrale Stelle, an der
/// die Schreibweisen des Bestandes und der Quellen auseinandergehalten werden (F-M557). Sie hier zu
/// wiederholen hieße, sie zweimal zu führen.
/// </para>
/// </summary>
public static class SubdlLanguages
{
    /// <summary>
    /// Gets die geschlossene Liste der Kennzeichen — hier <c>null</c>, weil diese Quelle keine führt.
    /// </summary>
    public static IReadOnlyList<string>? Supported => null;

    /// <summary>
    /// Gets die eine Umrechnung auf den Code dieser Quelle, oder <c>null</c> bei leerer Eingabe.
    /// </summary>
    /// <param name="iso">Ein ISO-Kennzeichen, in beliebiger Schreibweise.</param>
    /// <returns>Der Zwei-Buchstaben-Code, den die Seite versteht.</returns>
    public static string? ToSite(string? iso) => LanguageMapper.MapToSubdl(iso);

    /// <summary>
    /// Gets die Gegenrichtung — vom Code der Seite auf die Schreibweise des Bestandes (F-M557).
    /// </summary>
    /// <param name="siteCode">Ein Code, wie die Seite ihn führt.</param>
    /// <returns>Das ISO-639-2-Kennzeichen in Großbuchstaben, oder null.</returns>
    public static string? FromSite(string? siteCode) => LanguageMapper.Canonical(siteCode);
}
