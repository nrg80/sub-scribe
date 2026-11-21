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
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M671: der Spurnummernkreis EINER Datei und die Ableitung des ffmpeg-Index daraus.
/// <para>
/// WAS DER NUMMERNKREIS IST: jede Spur einer Datei — eingebettet oder als Seitenwagen — bekommt beim
/// ersten Sehen die nächste freie Nummer. Die Nummer wird nie neu vergeben und nie wiederverwendet: eine
/// verschwundene Spur hinterlässt eine Lücke, und die nächste Spur bekommt die nächste Zahl darüber. Damit
/// sind eingebettete Spur und Seitenwagen derselben Datei kollisionsfrei, und die Herkunft einer Zahl
/// ändert sich nie.
/// </para>
/// <para>
/// WARUM DIE NUMMER NICHT DER INDEX IST: ffmpeg zählt nur, was gerade IM Container liegt (0:s:0, 0:s:1 …).
/// Ein Seitenwagen ist keine Container-Spur, belegt aber eine Nummer im gemeinsamen Kreis. Der Index einer
/// eingebetteten Spur ist deshalb die Anzahl der eingebetteten Spuren mit KLEINERER Nummer — Seitenwagen
/// zählen dabei nicht mit (F-M658: ein ffmpeg-Lauf je Datei, Spuren nur über Container-Spuren). Die
/// Zuordnung wird zur Laufzeit gerechnet, nie gespeichert: sie hängt vom aktuellen Container ab.
/// </para>
/// </summary>
public static class TrackNumbers
{
    /// <summary>
    /// Die nächste freie Spurnummer einer Datei — über BEIDE Arten (eingebettet und Seitenwagen) und über
    /// den gesamten Bestand, nicht nur über die gerade sichtbaren Spuren.
    /// </summary>
    /// <param name="tracks">Die eingebetteten Spuren der Datei.</param>
    /// <param name="sidecars">Die Seitenwagen der Datei.</param>
    /// <returns>Die nächste freie Nummer (0, wenn die Datei noch keine Spur hat).</returns>
    public static int NextFree(IEnumerable<TrackEntity> tracks, IEnumerable<SidecarEntity> sidecars)
    {
        int hoechste = -1;
        foreach (var t in tracks)
        {
            if (t.TrackNumber > hoechste)
            {
                hoechste = t.TrackNumber;
            }
        }

        foreach (var s in sidecars)
        {
            if (s.TrackNumber > hoechste)
            {
                hoechste = s.TrackNumber;
            }
        }

        return hoechste + 1;
    }

    /// <summary>Wahr, wenn die Nummer im Kreis dieser Datei schon vergeben ist.</summary>
    /// <param name="tracks">Die eingebetteten Spuren der Datei.</param>
    /// <param name="sidecars">Die Seitenwagen der Datei.</param>
    /// <param name="number">Die zu prüfende Nummer.</param>
    /// <returns>True when the number is taken.</returns>
    public static bool IsTaken(IEnumerable<TrackEntity> tracks, IEnumerable<SidecarEntity> sidecars, int number)
        => tracks.Any(t => t.TrackNumber == number) || sidecars.Any(s => s.TrackNumber == number);

    /// <summary>
    /// Der AKTUELLE ffmpeg-Index einer eingebetteten Spur: die Anzahl der eingebetteten Spuren derselben
    /// Datei mit kleinerer Spurnummer. Seitenwagen stehen im gemeinsamen Nummernkreis, zählen hier aber
    /// nicht — sie liegen nicht im Container.
    /// </summary>
    /// <param name="tracks">Die eingebetteten Spuren der Datei.</param>
    /// <param name="trackNumber">Die Spurnummer der gesuchten Spur.</param>
    /// <returns>The ffmpeg subtitle-stream index, or -1 when the number is not an embedded track.</returns>
    public static int FfmpegIndex(IEnumerable<TrackEntity> tracks, int trackNumber)
    {
        var liste = tracks.ToList();
        if (!liste.Any(t => t.TrackNumber == trackNumber))
        {
            return -1;
        }

        return liste.Count(t => t.TrackNumber < trackNumber);
    }

    /// <summary>
    /// Der ffmpeg-Index einer Spur, die über ihre Kennung adressiert ist.
    /// </summary>
    /// <param name="tracks">Die eingebetteten Spuren der Datei.</param>
    /// <param name="trackId">Die Spurkennung.</param>
    /// <returns>The index, or -1 when the track is not embedded.</returns>
    public static int FfmpegIndex(IEnumerable<TrackEntity> tracks, string trackId)
    {
        var liste = tracks.ToList();
        var treffer = liste.FirstOrDefault(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));
        return treffer == null ? -1 : liste.Count(t => t.TrackNumber < treffer.TrackNumber);
    }

    /// <summary>
    /// Die Spur, die heute an dieser ffmpeg-Position liegt — der Rückweg von Index zu Zeile.
    /// </summary>
    /// <param name="tracks">Die eingebetteten Spuren der Datei.</param>
    /// <param name="ffmpegIndex">Der beobachtete Container-Index.</param>
    /// <returns>The track at that index, or null.</returns>
    public static TrackEntity? AtFfmpegIndex(IEnumerable<TrackEntity> tracks, int ffmpegIndex)
        => tracks.OrderBy(t => t.TrackNumber).Skip(ffmpegIndex).FirstOrDefault();
}
