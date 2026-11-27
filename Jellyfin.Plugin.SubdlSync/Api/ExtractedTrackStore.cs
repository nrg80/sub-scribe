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
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M784 bis F-M788: der Zwischenspeicher für die ausgepackten Untertitelspuren — eine Spur, ein ZIP,
/// im selben Ordner und unter derselben Decke wie die Archive.
/// <para>
/// WARUM: eine Spur muss den Medienbehälter verlassen, bevor sie irgendwohin geht. Bis 12.3.8.75 geschah
/// das in jedem Lauf neu, und was dabei herauskam, wurde am Laufende weggeworfen
/// (<see cref="RunWorkFiles"/>). Teuer ist daran nicht das Entpacken und nicht das Packen, sondern das
/// LESEN des Behälters von der Platte — auf einem Wirt, dessen Platte länger steht als sein Speicher je
/// belegt war, ist das genau der Posten, den man wegnimmt.
/// </para>
/// <para>
/// WAS HIER NICHT PASSIERT: es wird kein Hash über den Behälter gerechnet. Ein Inhaltshash kostet ein
/// vollständiges Lesen der Datei — dieselbe Arbeit, die der Speicher ersparen soll; ihn vor dem
/// Nachschlagen zu zahlen machte jeden Treffer so teuer wie einen Fehltreffer. Der Name entsteht aus
/// Werten, die ein Blick auf die Datei ohnehin liefert.
/// </para>
/// </summary>
public static class ExtractedTrackStore
{
    /// <summary>Prefix of every key, so a stored track never answers for a stored archive.</summary>
    public const string KeyPrefix = "track-";

    /// <summary>The one member name every entry carries — fixed, so the ZIP is deterministic.</summary>
    public const string MemberName = "track.srt";

    /// <summary>
    /// The stamp every entry carries. A fixed point in time, so two writes of the same text give the same
    /// bytes and a hit cannot be told from a fresh extraction (F-M787).
    /// </summary>
    public static readonly DateTimeOffset FixedStamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds the store key from what one look at the file gives: the item, the position of the track
    /// inside the container, the size, the last write and the creation of the container.
    /// </summary>
    /// <param name="itemId">Identifier of the media item in the library.</param>
    /// <param name="mediaPath">Path of the media container — only read for its numbers, never stored.</param>
    /// <param name="position">Position of the track as ffmpeg counts it.</param>
    /// <returns>The key, or null when the file cannot be looked at.</returns>
    public static string? KeyFor(string? itemId, string? mediaPath, int position)
    {
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(mediaPath))
        {
            return null;
        }

        try
        {
            var datei = new FileInfo(mediaPath);
            if (!datei.Exists)
            {
                return null;
            }

            return string.Concat(
                KeyPrefix,
                Sanitize(itemId),
                "-",
                position.ToString(CultureInfo.InvariantCulture),
                "-",
                datei.Length.ToString(CultureInfo.InvariantCulture),
                "-",
                datei.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                "-",
                datei.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a stored track. The ZIP is unpacked in memory and nothing about it survives the call.
    /// </summary>
    /// <param name="store">The shared store (the download cache's own instance).</param>
    /// <param name="itemId">Identifier of the media item.</param>
    /// <param name="mediaPath">Path of the media container.</param>
    /// <param name="position">Position of the track.</param>
    /// <param name="text">The subtitle text when found.</param>
    /// <returns>True when a usable entry answered.</returns>
    public static bool TryGet(ArchiveDiskCache? store, string? itemId, string? mediaPath, int position, out string? text)
    {
        text = null;
        string? key = KeyFor(itemId, mediaPath, position);
        if (store == null || key == null || !store.TryGet(key, out byte[]? zip) || zip == null)
        {
            return false;
        }

        text = Unpack(zip);
        if (text == null)
        {
            // Ein Eintrag, der sich nicht mehr auspacken lässt, ist kein Treffer: er wird entfernt, nicht
            // repariert (F-M787). Der nächste Lauf entpackt die Spur neu.
            store.Remove(key, "unreadable");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stores one track as a ZIP with a single member.
    /// </summary>
    /// <param name="store">The shared store.</param>
    /// <param name="itemId">Identifier of the media item.</param>
    /// <param name="mediaPath">Path of the media container.</param>
    /// <param name="position">Position of the track.</param>
    /// <param name="text">The subtitle text, exactly as ffmpeg produced it.</param>
    /// <returns>True when it was written.</returns>
    public static bool Put(ArchiveDiskCache? store, string? itemId, string? mediaPath, int position, string? text)
    {
        if (store == null || string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? key = KeyFor(itemId, mediaPath, position);
        if (key == null)
        {
            return false;
        }

        store.Put(key, Pack(text));
        return true;
    }

    /// <summary>
    /// Packs one subtitle into a ZIP with one member and a fixed stamp.
    /// </summary>
    /// <param name="text">The subtitle text.</param>
    /// <returns>The archive body.</returns>
    public static byte[] Pack(string text)
    {
        byte[] inhalt = Encoding.UTF8.GetBytes(text);
        using var puffer = new MemoryStream();
        using (var zip = new ZipArchive(puffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var eintrag = zip.CreateEntry(MemberName, CompressionLevel.Optimal);
            eintrag.LastWriteTime = FixedStamp;
            using var ziel = eintrag.Open();
            ziel.Write(inhalt, 0, inhalt.Length);
        }

        return puffer.ToArray();
    }

    /// <summary>
    /// Unpacks the single member of an entry into text. Anything else is not an entry.
    /// </summary>
    /// <param name="zip">The archive body.</param>
    /// <returns>The text, or null when the body is not one readable member.</returns>
    public static string? Unpack(byte[]? zip)
    {
        if (zip == null || zip.Length == 0)
        {
            return null;
        }

        try
        {
            using var puffer = new MemoryStream(zip, writable: false);
            using var archiv = new ZipArchive(puffer, ZipArchiveMode.Read);
            if (archiv.Entries.Count != 1)
            {
                return null;
            }

            using var quelle = archiv.Entries[0].Open();
            using var leser = new StreamReader(quelle, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return leser.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Keeps a key readable and free of anything but letters, digits and dashes.</summary>
    /// <param name="value">The raw part of the key.</param>
    /// <returns>The cleaned part.</returns>
    private static string Sanitize(string value)
    {
        var bauer = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            bool gut = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
            if (gut)
            {
                bauer.Append(c);
            }

            if (bauer.Length >= 48)
            {
                break;
            }
        }

        return bauer.Length > 0 ? bauer.ToString() : "item";
    }
}
