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
/// F-M676 (Betreiber-Korrektur 22.11.2026): DIE ID IST EINE LESBARE TEXTSÄULE, und ihre MARKE sagt die
/// Ebene — <c>[xxxx]</c> Pack, <c>#yyyyy</c> Datei, <c>#yyyyy#zzz</c> eingebettete Spur,
/// <c>#yyyyy&amp;zzz</c> Seitenwagen. Die Datei-Id steckt in der Spur-Id, ein zweites Feld daneben wäre
/// eine zweite Aussage über dieselbe Sache. Kapazität: 61^4 = 13 845 841 Packs, 61^5 = 844 596 301
/// Dateien, 61^3 = 226 981 Spuren je Datei.
/// </para>
/// <para>
/// DAS ALPHABET: <c>1-9a-zA-Z</c> — 61 Zeichen. Die <c>0</c> fehlt ABSICHTLICH: sie ist reiner Füller für
/// ein leeres Segment. Damit ist „leer" von „vergeben" unterscheidbar, ohne dass eine Null in einem
/// echten Segment stehen kann, und eine Id lässt sich eindeutig zurücklesen.
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

    /// <summary>
    /// F-M676: die drei Marken, die eine Id traegt. Keine von ihnen steht im Alphabet <c>1-9a-zA-Z</c>,
    /// also ist keine Form mit einer anderen verwechselbar.
    /// </summary>
    public const char PackOpen = '[';

    /// <summary>F-M676: die schliessende Klammer einer Pack-Id.</summary>
    public const char PackClose = ']';

    /// <summary>F-M676: die Marke der Datei-Ebene und der eingebetteten Spur.</summary>
    public const char FileMark = '#';

    /// <summary>F-M676: das Zeichen, das im dritten Feld eine Spur als SEITENWAGEN ausweist.</summary>
    public const char SidecarMark = '&';

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
    /// F-M676 (Betreiber-Korrektur 22.11.2026): DIE ID IST DER TEXT — vier Formen, und die Marken
    /// <c>[ ] # &amp;</c> sagen die Ebene:
    /// <list type="bullet">
    /// <item><description>Pack: <c>[xxxx]</c> — vier Zeichen in eckigen Klammern.</description></item>
    /// <item><description>Datei: <c>#yyyyy</c> — fünf Zeichen mit vorangestelltem <c>#</c>.</description></item>
    /// <item><description>Eingebettete Spur: <c>#yyyyy#zzz</c> — Datei-Id, <c>#</c>, drei Zeichen.</description></item>
    /// <item><description>Seitenwagen: <c>#yyyyy&amp;zzz</c> — Datei-Id, <c>&amp;</c>, drei Zeichen.</description></item>
    /// </list>
    /// <para>
    /// WARUM DIE FORM SELBST SPRICHT: eine Kennung, die man nicht lesen kann, braucht ein zweites Feld,
    /// das sie erklärt — und das zweite Feld ist eine zweite Wahrheit über dieselbe Sache. Hier steht die
    /// Ebene in der Id, die Datei-Id steckt in Spur- und Seitenwagen-Id, und ein <c>FileId</c>-Feld daneben
    /// wäre eine Wiederholung. <c>#</c> und <c>&amp;</c> sind nicht im Alphabet <c>1-9a-zA-Z</c>, also
    /// können sie nie mit einem echten Zeichen verwechselt werden.
    /// </para>
    /// </summary>
    /// <param name="packId">The four-character pack segment.</param>
    /// <returns>The pack id, <c>[xxxx]</c>.</returns>
    public static string PackIdOf(string? packId) => "[" + Fill(packId, PackSegment) + "]";

    /// <summary>F-M676: die Id einer DATEI — <c>#yyyyy</c>.</summary>
    /// <param name="fileId">The five-character file segment, or a full file id.</param>
    /// <returns>The file id.</returns>
    public static string FileIdOf(string? fileId) => "#" + FilePart(fileId);

    /// <summary>F-M676: die Id einer eingebetteten SPUR — <c>#yyyyy#zzz</c>.</summary>
    /// <param name="fileId">Das Datei-Segment oder eine volle Datei-Id.</param>
    /// <param name="trackId">Das Spur-Segment oder eine volle Spur-Id (dann wird ihr Segment genommen).</param>
    /// <returns>The track id.</returns>
    public static string TrackIdOf(string? fileId, string? trackId)
        => "#" + FilePart(fileId) + "#" + TrackPart(trackId);

    /// <summary>F-M676: die Id eines SEITENWAGENS — <c>#yyyyy&amp;zzz</c>.</summary>
    /// <param name="fileId">Das Datei-Segment oder eine volle Datei-Id.</param>
    /// <param name="trackId">Das Spur-Segment oder eine volle Id.</param>
    /// <returns>The sidecar id.</returns>
    public static string SidecarIdOf(string? fileId, string? trackId)
        => "#" + FilePart(fileId) + "&" + TrackPart(trackId);

    /// <summary>
    /// Das DATEI-SEGMENT einer Angabe, die ein Segment oder schon eine volle Id sein darf. Wer eine Id
    /// hineingibt, bekommt ihr Segment — so entsteht aus <c>#yyyyy</c> nicht <c>##yyyyy</c>.
    /// </summary>
    /// <param name="fileId">Segment or full file id.</param>
    /// <returns>The five-character segment.</returns>
    private static string FilePart(string? fileId)
    {
        string wert = fileId ?? string.Empty;
        if (wert.Length == FileSegment + 1 && wert[0] == FileMark)
        {
            wert = wert.Substring(1);
        }

        return Fill(wert, FileSegment);
    }

    /// <summary>Das SPUR-SEGMENT einer Angabe, die ein Segment oder schon eine volle Id sein darf.</summary>
    /// <param name="trackId">Segment or full track id.</param>
    /// <returns>The three-character segment.</returns>
    private static string TrackPart(string? trackId)
    {
        string wert = trackId ?? string.Empty;
        string teil = TrackPartOf(wert);
        return Fill(string.IsNullOrEmpty(teil) ? wert : teil, TrackSegment);
    }

    /// <summary>
    /// F-M676: die DATEI-ID, die in einer Spur- oder Seitenwagen-Id steckt (<c>#yyyyy</c>) — oder die Id
    /// selbst, wenn sie schon eine Datei-Id ist. Leer, wenn die Form nicht passt.
    /// </summary>
    /// <param name="id">A track, sidecar or file id.</param>
    /// <returns>The file id, or an empty string.</returns>
    public static string FileOf(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < FileSegment + 1 || id[0] != '#')
        {
            return string.Empty;
        }

        return id.Substring(0, FileSegment + 1);
    }

    /// <summary>
    /// F-M676: das SPUR-SEGMENT einer Spur- oder Seitenwagen-Id (<c>zzz</c>), leer für eine Datei-Id.
    /// </summary>
    /// <param name="id">A track or sidecar id.</param>
    /// <returns>The three-character segment, or an empty string.</returns>
    public static string TrackPartOf(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 2 + FileSegment + TrackSegment || id[0] != '#')
        {
            return string.Empty;
        }

        char marke = id[FileSegment + 1];
        return marke == '#' || marke == SidecarMark ? id.Substring(FileSegment + 2) : string.Empty;
    }

    /// <summary>F-M676: wahr, wenn die Id die Form eines Seitenwagens hat (<c>#yyyyy&amp;zzz</c>).</summary>
    /// <param name="id">A track or sidecar id.</param>
    /// <returns>True for a sidecar id.</returns>
    public static bool IsSidecarId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length == 2 + FileSegment + TrackSegment
           && id[0] == '#' && id[FileSegment + 1] == SidecarMark;

    /// <summary>F-M676: wahr, wenn die Id die Form einer eingebetteten Spur hat (<c>#yyyyy#zzz</c>).</summary>
    /// <param name="id">A track or sidecar id.</param>
    /// <returns>True for an embedded track id.</returns>
    public static bool IsTrackId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length == 2 + FileSegment + TrackSegment
           && id[0] == '#' && id[FileSegment + 1] == '#';

    /// <summary>F-M676: wahr, wenn die Id die Form eines Packs hat (<c>[xxxx]</c>).</summary>
    /// <param name="id">A pack id.</param>
    /// <returns>True for a pack id.</returns>
    public static bool IsPackId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length == PackSegment + 2
           && id[0] == '[' && id[^1] == ']'
           && IsSegment(id.Substring(1, PackSegment), PackSegment);

    /// <summary>F-M676: wahr, wenn die Id die Form einer Datei hat (<c>#yyyyy</c>).</summary>
    /// <param name="id">A file id.</param>
    /// <returns>True for a file id.</returns>
    public static bool IsFileId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length == FileSegment + 1
           && id[0] == '#' && IsSegment(id.Substring(1), FileSegment);

    /// <summary>
    /// F-M676: liest eine Spur- oder Seitenwagen-Id in ihre Teile. Eine Datei-Id liefert Datei und eine
    /// leere Spur; ein Pack liefert nichts.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="fileId">The file part, empty when the id is not of that shape.</param>
    /// <param name="trackId">The track part, empty for a file id.</param>
    /// <param name="isSidecar">True when the id carried the sidecar mark.</param>
    /// <returns>True when the id has one of the four shapes.</returns>
    public static bool TryParse(string? id, out string fileId, out string trackId, out bool isSidecar)
    {
        fileId = string.Empty;
        trackId = string.Empty;
        isSidecar = false;

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        if (IsFileId(id))
        {
            fileId = id.Substring(1);
            return true;
        }

        if (!IsTrackId(id) && !IsSidecarId(id))
        {
            return false;
        }

        fileId = id.Substring(1, FileSegment);
        trackId = id.Substring(FileSegment + 2);
        isSidecar = id[FileSegment + 1] == SidecarMark;
        return true;
    }

    /// <summary>Füllt ein Segment auf seine Breite; ein leeres wird zur Nullenfolge.</summary>
    /// <param name="segment">The segment.</param>
    /// <param name="length">Its width.</param>
    /// <returns>The padded segment.</returns>
    public static string Fill(string? segment, int length)
        => string.IsNullOrEmpty(segment) ? new string(Filler, length) : segment;

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
