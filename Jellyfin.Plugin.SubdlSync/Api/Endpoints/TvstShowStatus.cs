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
namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M516: die Zusagen der Serien-Anlage. Sie sind Datenwerte — kein Code, der etwas entscheidet;
/// entschieden wird über sie in <see cref="TvstShowLookup.SettleStatus"/>.
/// <para>
/// Bis F-M734 stand in dieser Datei ein Nachschlageweg, der die Serie auch anlegen konnte und dabei
/// selbst in den Bestand schrieb. Der Ablauf ruft ihn nicht mehr: die Titel-Frage ist ein Feld des
/// Sende-Auftrags, gelesen und geschrieben wird der Bestand vom Ablauf. Die Zusagen bleiben, weil die
/// Antwort der Seite und die Selbstauskunft sie nennen.
/// </para>
/// </summary>
public static class TvstShowStatus
{
    /// <summary>Die Seite hat die Anlage bestätigt und die Nummer steht fest.</summary>
    public const string Created = "created";

    /// <summary>Angelegt, aber die Nummer ist nicht lesbar.</summary>
    public const string CreatedIdUnknown = "created-id-unknown";

    /// <summary>Die Serie stand schon da — die Nummer ist übernommen, nichts angelegt.</summary>
    public const string Existed = "existed";

    /// <summary>Ein inhaltliches Nein der Seite.</summary>
    public const string Refused = "refused";

    /// <summary>Kein Urteil: der Ausgang ist unklar.</summary>
    public const string Unclear = "unclear";
}
