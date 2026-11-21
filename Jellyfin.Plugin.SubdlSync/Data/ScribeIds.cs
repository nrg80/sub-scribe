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
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M671 (Betreiber-Entscheidung 21.11.2026): die eigenen Kennungen — drei Felder, ein Anzeige-String.
/// <para>
/// WARUM EIGENE KENNUNGEN: bis Schema 21 war die Kennung eines Mediums sein OSHash und die einer Spur
/// „&lt;hash&gt;|&lt;position&gt;". Beides ist eine Aussage über den INHALT, nicht über das DING: ein
/// neu getaggter Container bekommt einen neuen Hash, also eine neue Identität, und jede Spur darunter
/// eine neue Kennung — obwohl es dieselbe Datei und dieselbe Spur ist. Eine Kennung, die sich ändert,
/// während das Ding bleibt, ist keine Kennung. Ab Schema 22 ist sie zufällig, einmalig und unveränderlich.
/// </para>
/// <para>
/// DER ANZEIGE-STRING TRÄGT DIE EBENE: <c>xxxx-yyyyy-zzz</c>. <c>xxxx</c> ist der Pack (4 Zeichen, leer
/// als <c>0000</c>), <c>yyyyy</c> die Datei (5 Zeichen, leer als <c>00000</c>), <c>zzz</c> die Spur
/// (3 Zeichen, leer als <c>000</c>). Die drei Segmente sind die drei SPALTEN <see cref="PackSegment"/>,
/// <see cref="FileSegment"/> und <see cref="TrackSegment"/>; der String ist nur ihre Anzeige und wird
/// nirgends gespeichert. Kapazität: 61^4 = 13 845 841 Packs, 61^5 = 844 596 301 Dateien, 61^3 = 226 981
/// Spuren je Datei.
/// </para>
/// <para>
/// DAS ALPHABET: <c>1-9a-zA-Z</c> — 61 Zeichen. Die <c>0</c> fehlt ABSICHTLICH: sie ist reiner Füller für
/// ein leeres Segment. Damit ist „leer" von „vergeben" unterscheidbar, ohne dass eine Null in einem
/// echten Segment stehen kann, und ein Anzeige-String lässt sich eindeutig zurücklesen.
/// </para>
/// </summary>
public static class ScribeIds
{
    /// <summary>Das Alphabet der echten Segmente: <c>1-9a-zA-Z</c> (61 Zeichen), ohne die Null.</summary>
    public const string Alphabet = "123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Länge des Pack-Segments (<c>xxxx</c>).</summary>
    public const int PackSegment = 4;

    /// <summary>Länge des Datei-Segments (<c>yyyyy</c>).</summary>
    public const int FileSegment = 5;

    /// <summary>Länge des Spur-Segments (<c>zzz</c>).</summary>
    public const int TrackSegment = 3;

    /// <summary>Der Füller eines leeren Segments (ein ganzes Segment aus Nullen).</summary>
    public const char Filler = '0';

    /// <summary>Kapazität des Pack-Segments (61^4 = 13 845 841).</summary>
    public static readonly long PackCapacity = Capacity(PackSegment);

    /// <summary>Kapazität des Datei-Segments (61^5 = 844 596 301).</summary>
    public static readonly long FileCapacity = Capacity(FileSegment);

    /// <summary>Kapazität des Spur-Segments (61^3 = 226 981) — je Datei.</summary>
    public static readonly long TrackCapacity = Capacity(TrackSegment);

    private static long Capacity(int length)
    {
        long n = 1;
        for (int i = 0; i < length; i++)
        {
            n *= Alphabet.Length;
        }

        return n;
    }

    /// <summary>Wahr, wenn das Segment die richtige Länge hat und nur Zeichen des Alphabets trägt.</summary>
    /// <param name="segment">The segment to test.</param>
    /// <param name="length">Expected length.</param>
    /// <returns>True for a real (non-empty) segment.</returns>
    public static bool IsSegment(string? segment, int length)
    {
        if (segment == null || segment.Length != length)
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Wahr, wenn das Segment leer ist (<c>""</c> oder der Füller des Segments).</summary>
    /// <param name="segment">The segment to test.</param>
    /// <param name="length">Length of the segment's level.</param>
    /// <returns>True for an empty segment.</returns>
    public static bool IsEmptySegment(string? segment, int length)
        => string.IsNullOrEmpty(segment) || segment == new string(Filler, length);

    /// <summary>
    /// Der Anzeige-String aus den drei Segmenten. Leere Segmente werden gefüllt, damit die Form immer
    /// <c>xxxx-yyyyy-zzz</c> ist und ein Leser die Ebene an der Position erkennt.
    /// </summary>
    /// <param name="packId">Pack segment, or null/empty.</param>
    /// <param name="fileId">File segment, or null/empty.</param>
    /// <param name="trackId">Track segment, or null/empty.</param>
    /// <returns>The display string.</returns>
    public static string Display(string? packId, string? fileId, string? trackId)
        => Fill(packId, PackSegment) + "-" + Fill(fileId, FileSegment) + "-" + Fill(trackId, TrackSegment);

    /// <summary>Füllt ein Segment auf seine Breite; ein leeres wird zur Nullenfolge.</summary>
    /// <param name="segment">The segment.</param>
    /// <param name="length">Its width.</param>
    /// <returns>The padded segment.</returns>
    public static string Fill(string? segment, int length)
        => string.IsNullOrEmpty(segment) ? new string(Filler, length) : segment;

    /// <summary>
    /// Liest einen Anzeige-String zurück in seine drei Segmente. Leere Segmente kommen als leerer
    /// String zurück (nicht als Nullenfolge), damit ein Aufrufer „kein Pack" nicht als Kennung nimmt.
    /// </summary>
    /// <param name="display">The display string.</param>
    /// <param name="packId">The pack segment, empty when absent.</param>
    /// <param name="fileId">The file segment, empty when absent.</param>
    /// <param name="trackId">The track segment, empty when absent.</param>
    /// <returns>True when the string has the right shape.</returns>
    public static bool TryParse(string? display, out string packId, out string fileId, out string trackId)
    {
        packId = string.Empty;
        fileId = string.Empty;
        trackId = string.Empty;
        if (string.IsNullOrEmpty(display))
        {
            return false;
        }

        var teile = display.Split('-');
        if (teile.Length != 3
            || teile[0].Length != PackSegment
            || teile[1].Length != FileSegment
            || teile[2].Length != TrackSegment)
        {
            return false;
        }

        for (int i = 0; i < teile.Length; i++)
        {
            int width = i == 0 ? PackSegment : i == 1 ? FileSegment : TrackSegment;
            if (teile[i] != new string(Filler, width) && !IsSegment(teile[i], width))
            {
                return false;
            }
        }

        packId = IsEmptySegment(teile[0], PackSegment) ? string.Empty : teile[0];
        fileId = IsEmptySegment(teile[1], FileSegment) ? string.Empty : teile[1];
        trackId = IsEmptySegment(teile[2], TrackSegment) ? string.Empty : teile[2];
        return true;
    }

    /// <summary>Ein zufälliges Segment der genannten Breite aus dem Alphabet (kryptografisch).</summary>
    /// <param name="length">Segment width.</param>
    /// <returns>A fresh segment.</returns>
    public static string NewSegment(int length)
    {
        var sb = new StringBuilder(length);
        Span<byte> puffer = stackalloc byte[length];
        RandomNumberGenerator.Fill(puffer);
        for (int i = 0; i < length; i++)
        {
            // 256 ist kein Vielfaches von 61; der Modulo neigt die ersten Zeichen leicht. Für eine
            // Kennung, die nur eindeutig sein muss, ist das ohne Belang — und der Aufrufer prüft
            // ohnehin gegen den Bestand.
            sb.Append(Alphabet[puffer[i] % Alphabet.Length]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Eine frische Pack-Kennung, geprüft gegen den Bestand. Die Prüfung ist kein Formalismus: 4 Zeichen
    /// sind 13,8 Millionen Möglichkeiten, und eine doppelte Kennung wäre zwei Packs unter einem Namen.
    /// </summary>
    /// <param name="besetzt">Prüft, ob eine Kennung schon vergeben ist.</param>
    /// <returns>A fresh, unused pack segment.</returns>
    public static string NewPackId(Func<string, bool> besetzt)
    {
        for (int versuch = 0; versuch < 1000; versuch++)
        {
            string id = NewSegment(PackSegment);
            if (!besetzt(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("no free pack id after 1000 attempts");
    }

    /// <summary>Eine frische Datei-Kennung, geprüft gegen den Bestand.</summary>
    /// <param name="besetzt">Prüft, ob eine Kennung schon vergeben ist.</param>
    /// <returns>A fresh, unused file segment.</returns>
    public static string NewFileId(Func<string, bool> besetzt)
    {
        for (int versuch = 0; versuch < 1000; versuch++)
        {
            string id = NewSegment(FileSegment);
            if (!besetzt(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("no free file id after 1000 attempts");
    }

    /// <summary>
    /// Eine frische Spur-Kennung INNERHALB einer Datei, geprüft gegen den Bestand dieser Datei. Der
    /// Nummernkreis ist je Datei; 226 981 Möglichkeiten stehen für die Spuren und Seitenwagen EINER
    /// Datei zur Verfügung, und keine Kennung wird je wiederverwendet.
    /// </summary>
    /// <param name="besetzt">Prüft, ob eine Kennung in dieser Datei schon vergeben ist.</param>
    /// <returns>A fresh, unused track segment.</returns>
    public static string NewTrackId(Func<string, bool> besetzt)
    {
        for (int versuch = 0; versuch < 1000; versuch++)
        {
            string id = NewSegment(TrackSegment);
            if (!besetzt(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("no free track id after 1000 attempts");
    }

    /// <summary>Die Kapazität eines Segments als Text, für Doku und Diagnose.</summary>
    /// <param name="length">Segment width.</param>
    /// <returns>The capacity.</returns>
    public static string CapacityText(int length)
        => Capacity(length).ToString(CultureInfo.InvariantCulture);
}
