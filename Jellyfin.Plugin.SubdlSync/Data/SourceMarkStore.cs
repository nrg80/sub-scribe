// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M628/F-M631: ein Beleg — und zwar genau einer je Paar aus Spur und Quelle.
/// <para>
/// WARUM DIESE TABELLE: die Quellenbelege lagen als feste Spaltenpaare am Datensatz
/// (<c>SubdlUploadId</c>, <c>TvSubtitlesUploadId</c>, <c>MoviesubtitlesUploadId</c> samt Zeitpunkten und
/// Abschluss-Stempeln, vierzehn Spalten). Eine vierte Quelle hätte das Schema geändert und jeden
/// Lesepfad mitgezogen. Hier ist eine Quelle ein WERT im Feld <see cref="Source"/>, kein Feld —
/// N Quellen erzeugen N Zeilen, nicht N Spalten (F-M632).
/// </para>
/// <para>
/// WAS „KEIN BELEG" HEISST: fehlt die Zeile, ist die Spur für diese Quelle offen. Es gibt kein Feld
/// „gehalten von" (F-M631) — eine Spur darf für eine Quelle belegt und für eine andere offen sein,
/// und genau das ist der Normalfall.
/// </para>
/// </summary>
public sealed class SourceMarkEntity
{
    /// <summary>Schlüssel der Zeile: <c>OwnerKey|Source</c>. Ein Beleg ist eindeutig adressierbar.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Der Schlüssel des Datensatzes, an dem der Beleg hängt (Spur, Sidecar oder Datei).</summary>
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>Die Quelle, für die dieser Beleg gilt — ein Wert, kein Spaltenname.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Kennung des Abrufs bei dieser Quelle, sofern die Quelle sie vergibt.</summary>
    public string? DownloadId { get; set; }

    /// <summary>Kennung der Einsendung bei dieser Quelle, sofern die Quelle sie vergibt.</summary>
    public string? UploadId { get; set; }

    /// <summary>Zeitpunkt der Einsendung bei dieser Quelle.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>Zeitpunkt, zu dem diese Quelle die Datei als abgeschlossen gemeldet hat.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Wann die Zeile zuletzt geschrieben wurde.</summary>
    public DateTime Written { get; set; }
}

/// <summary>
/// Zugriff auf die Belege je Quelle (F-M628). Der Ablauf fragt hier nach Quellen und nennt nie eine
/// quellenspezifische Spalte.
/// <para>
/// Angehängt wird der Store einmal beim Aufbau des Datenkontexts (<see cref="Attach"/>); ohne ihn
/// antwortet jede Abfrage „kein Beleg", und der Ablauf verhält sich wie vor der Einführung — offen
/// statt stillschweigend fertig.
/// </para>
/// </summary>
public static class SourceMarkStore
{
    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>Attaches the store.</summary>
    /// <param name="db">The plugin's data context.</param>
    public static void Attach(ScribeSqliteContext? db)
    {
        lock (Gate)
        {
            _db = db;
        }
    }

    /// <summary>
    /// Legt die Tabelle an, falls sie fehlt. Additiv, damit ein Bestand weiterlesbar bleibt.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="logger">Optional logger.</param>
    public static void EnsureTable(ScribeSqliteContext db, ILogger? logger = null)
    {
        try
        {
            db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS source_marks ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "OwnerKey TEXT NOT NULL DEFAULT '', "
                + "Source TEXT NOT NULL DEFAULT '', "
                + "DownloadId TEXT NULL, "
                + "UploadId TEXT NULL, "
                + "UploadedAt TEXT NULL, "
                + "CompletedAt TEXT NULL, "
                + "Written TEXT NOT NULL DEFAULT '');");
            db.Database.ExecuteSqlRaw(
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_source_marks_owner_source "
                + "ON source_marks (OwnerKey, Source);");
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[Sub Scribe] could not create the source_marks table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>Zeilen-Schlüssel eines Belegs.</summary>
    /// <param name="ownerKey">Key of the record the mark belongs to.</param>
    /// <param name="source">The source.</param>
    /// <returns><c>OwnerKey|Source</c>.</returns>
    public static string KeyOf(string ownerKey, string source)
        => string.Concat(ownerKey, "|", source);

    /// <summary>Der Beleg einer Quelle für einen Datensatz, oder null.</summary>
    /// <param name="ownerKey">Key of the record.</param>
    /// <param name="source">The source.</param>
    /// <returns>The mark, or null when this source holds nothing for this record.</returns>
    public static SourceMarkEntity? Get(string ownerKey, string source)
    {
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(source))
        {
            return null;
        }

        string id = KeyOf(ownerKey, source);
        try
        {
            lock (Gate)
            {
                return db.SourceMarkRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
            }
        }
        catch (Exception)
        {
            // Ein Store, der nicht antwortet, heißt „kein Beleg" — offen, nicht fertig.
            return null;
        }
    }

    /// <summary>Alle Belege eines Datensatzes, eine Zeile je Quelle (F-M632).</summary>
    /// <param name="ownerKey">Key of the record.</param>
    /// <returns>The marks, empty when none.</returns>
    public static IReadOnlyList<SourceMarkEntity> ForOwner(string ownerKey)
    {
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrEmpty(ownerKey))
        {
            return Array.Empty<SourceMarkEntity>();
        }

        try
        {
            lock (Gate)
            {
                return db.SourceMarkRows.AsNoTracking().Where(r => r.OwnerKey == ownerKey).ToList();
            }
        }
        catch (Exception)
        {
            return Array.Empty<SourceMarkEntity>();
        }
    }

    /// <summary>
    /// Schreibt den Beleg EINER Quelle und lässt die Belege anderer Quellen unberührt (F-M634).
    /// </summary>
    /// <param name="ownerKey">Key of the record.</param>
    /// <param name="source">The source the mark belongs to.</param>
    /// <param name="downloadId">Fetch id at that source, or null.</param>
    /// <param name="uploadId">Submission id at that source, or null.</param>
    /// <param name="uploadedAt">When the submission happened, or null.</param>
    /// <param name="completedAt">When that source reported the file done, or null.</param>
    public static void Set(
        string ownerKey,
        string source,
        string? downloadId = null,
        string? uploadId = null,
        DateTime? uploadedAt = null,
        DateTime? completedAt = null)
    {
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrEmpty(ownerKey) || string.IsNullOrEmpty(source))
        {
            return;
        }

        string id = KeyOf(ownerKey, source);
        try
        {
            lock (Gate)
            {
                var row = db.SourceMarkRows.FirstOrDefault(r => r.Id == id);
                if (row == null)
                {
                    row = new SourceMarkEntity { Id = id, OwnerKey = ownerKey, Source = source };
                    db.SourceMarkRows.Add(row);
                }

                if (downloadId != null)
                {
                    row.DownloadId = downloadId;
                }

                if (uploadId != null)
                {
                    row.UploadId = uploadId;
                }

                if (uploadedAt != null)
                {
                    row.UploadedAt = uploadedAt;
                }

                if (completedAt != null)
                {
                    row.CompletedAt = completedAt;
                }

                row.Written = DateTime.UtcNow;
                db.SaveChanges();
            }
        }
        catch (Exception)
        {
            // Ein Schreibfehler darf den Lauf nicht anhalten; der nächste Lauf schreibt erneut.
        }
    }

    /// <summary>
    /// Zählt die Belege je Quelle. Die Zahl ist die Prüfgrundlage beim Formatwechsel (F-M637).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>Source → number of marks.</returns>
    public static Dictionary<string, int> CountBySource(ScribeSqliteContext db)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gruppe in db.SourceMarkRows.AsNoTracking().GroupBy(r => r.Source).Select(g => new { Source = g.Key, N = g.Count() }).ToList())
        {
            zaehler[gruppe.Source] = gruppe.N;
        }

        return zaehler;
    }

    // F-M668 (21.11.2026): `BackfillFromColumns` und `CountColumnsBySource` sind entfallen. Beide lasen
    // die breiten Quellen-Spalten an `embeds`/`sidecars`/`media`, die es nicht mehr gibt — die Marken
    // stehen in `source_matrix` (SourceMatrix), und diese Tabelle ist der Beleg je (Medium, Quelle, Art).
}
