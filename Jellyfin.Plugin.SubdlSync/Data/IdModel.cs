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
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M671: die einmalige Übernahme des Altbestands in das Kennungs-Modell (Schema 22) — und das
/// Niederlegen der vier Alt-Tabellen in derselben Transaktion.
/// <para>
/// WAS HIER PASSIERT: <c>media</c> → <c>file</c>, <c>embeds</c> → <c>track</c>, <c>sidecars</c> →
/// <c>sidecar</c>, <c>packs</c> → <c>pack</c>. Jede Zeile bekommt ihre zufällige, unveränderliche Kennung
/// (<see cref="ScribeIds"/>), jede Datei und Spur das Pack ihrer Staffel, und jede Spur — eingebettet wie
/// Seitenwagen — ihre Spurnummer aus EINEM Nummernkreis je Datei. Vorher wird die ganze Datei unter einem
/// Zeitstempel gesichert; danach fallen die Alt-Tabellen IN DERSELBEN Transaktion. Es gibt also keinen
/// doppelten Bestand: was übernommen ist, steht nur noch in den neuen Tabellen, und der alte Stand lebt
/// allein in der Sicherungsdatei.
/// </para>
/// <para>
/// WANN SIE LÄUFT: wenn die Datei existiert, ein Store ist (<c>meta</c> vorhanden) und noch EINE der vier
/// Alt-Tabellen darin steht. Ein abgebrochener Lauf rollt zurück und wird beim nächsten Start nachgeholt;
/// eine übernommene Datei hat keine Alt-Tabelle mehr und tut nichts (idempotent). Der Schema-Marker ist
/// NICHT die Bedingung — dieselbe Regel wie bei der Marken-Übernahme.
/// </para>
/// <para>
/// WARUM SQL UND NICHT DAS MODELL: der Lauf arbeitet auf einer Datei, die ein ÄLTERES Schema hat. Jede
/// Spalte wird erst gefragt (<c>PRAGMA table_info</c>) und nur gelesen, wenn sie da ist; alles andere ist
/// <c>NULL</c>. Ein Lauf, der das Modell benutzte, würde an jeder fehlenden Spalte scheitern.
/// </para>
/// </summary>
public static class IdModel
{
    /// <summary>Was der Übernahmelauf getan hat — die Zahlen, mit denen er sich prüfen lässt.</summary>
    /// <param name="Ran">True when the four tables were filled by this call.</param>
    /// <param name="BackupPath">Path of the timestamped copy taken before the change, else null.</param>
    /// <param name="Read">Old table → number of rows read.</param>
    /// <param name="Written">New table → number of rows written.</param>
    /// <param name="TrackNumbers">How many tracks and sidecars received a number from their file's circle.</param>
    /// <param name="PacksOnFiles">How many files were given the id of the pack covering their season.</param>
    /// <param name="OrphanFiles">Files created because a track pointed at a hash no media row carried.</param>
    public sealed record Outcome(
        bool Ran,
        string? BackupPath,
        IReadOnlyDictionary<string, int> Read,
        IReadOnlyDictionary<string, int> Written,
        int TrackNumbers,
        int PacksOnFiles,
        int OrphanFiles);

    /// <summary>
    /// Legt die vier Tabellen an, falls sie fehlen. Der zweite, harmlose Weg für eine Datei, die ohne
    /// Migration geöffnet wurde (ausdrückliche Datei) oder deren Übernahme abgebrochen ist.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="logger">Optional logger.</param>
    public static void EnsureTables(ScribeSqliteContext db, ILogger? logger = null)
    {
        try
        {
            db.Database.ExecuteSqlRaw(ScribeDbContext.FileTableDdl);
            db.Database.ExecuteSqlRaw(ScribeDbContext.TrackTableDdl);
            db.Database.ExecuteSqlRaw(ScribeDbContext.SidecarTableDdl);
            db.Database.ExecuteSqlRaw(ScribeDbContext.PackTableDdl);
            foreach (string index in ScribeDbContext.IdModelIndexDdl)
            {
                db.Database.ExecuteSqlRaw(index);
            }

        }
        catch (Exception ex)
        {
            logger?.LogWarning("[Sub Scribe] could not create the id-model tables (continuing): {Msg}", ex.Message);
        }

        // F-M642 (bleibt gültig): eine Pack-Zeile aus der Zeit VOR der Quellen-Spalte trägt ihren
        // Schlüssel ohne fünften Teil. Der Schritt läuft bei jedem Öffnen — er ist billig, holt eine
        // Zeile nach, die zwischen zwei Läufen geschrieben wurde, und ist beliebig oft wiederholbar.
        // Er steht in einem EIGENEN Versuch: ein Fehlschlag hier darf die Tabellen nicht mitnehmen.
        try
        {
            int umgeschrieben = db.Database.ExecuteSqlRaw(
                "UPDATE pack SET SeasonKey = SeasonKey || '|' || COALESCE(NULLIF(Source, ''), 'subdl') "
                + "WHERE length(SeasonKey) - length(replace(SeasonKey, '|', '')) = 3 "
                + "AND NOT EXISTS (SELECT 1 FROM pack AS p2 WHERE p2.SeasonKey = pack.SeasonKey || '|' "
                + "|| COALESCE(NULLIF(pack.Source, ''), 'subdl'));");
            if (umgeschrieben > 0)
            {
                logger?.LogInformation(
                    "[Sub Scribe] data file: {Count} pack row(s) keyed to their source (F-M642).", umgeschrieben);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[Sub Scribe] could not key the pack rows to their source (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Führt die Übernahme aus. Läuft VOR dem Öffnen des Stores, weil sie Tabellen liest, die das neue
    /// Modell nicht mehr kennt.
    /// </summary>
    /// <param name="dbPath">Path of the live data file.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="backup">
    /// True to copy the file aside first. False only when this same start already took a safety copy (the
    /// marks takeover runs first and its copy is the state before both steps) or when the file was just
    /// built by the format switch (whose predecessor is kept by F-M638).
    /// </param>
    /// <returns>What was done, with the real counts.</returns>
    public static Outcome Run(string dbPath, ILogger? logger, bool backup = true)
    {
        var leer = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            return new Outcome(false, null, leer, leer, 0, 0, 0);
        }

        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            conn.Open();

            if (!TableExists(conn, "meta"))
            {
                return new Outcome(false, null, leer, leer, 0, 0, 0);
            }

            var alt = new[] { "media", "embeds", "sidecars", "packs" }.Where(t => TableExists(conn, t)).ToList();
            if (alt.Count == 0)
            {
                return new Outcome(false, null, leer, leer, 0, 0, 0);
            }

            // SICHERUNG ZUERST — vor der ersten Änderung an der Datei, mit dem VOLLSTÄNDIGEN Altbestand
            // darin. Ein WAL-Rest wird vorher eingefaltet, sonst fehlt der Kopie alles seit dem letzten
            // Checkpoint (F-M326).
            Checkpoint(conn);
            string? sicherung = null;
            if (backup)
            {
                sicherung = SourceMatrixMigration.BackupPathFor(dbPath);
                File.Copy(dbPath, sicherung, overwrite: true);
            }

            var gelesen = new Dictionary<string, int>(StringComparer.Ordinal);
            var geschrieben = new Dictionary<string, int>(StringComparer.Ordinal);

            using var tx = conn.BeginTransaction();

            using (var ddl = conn.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = ScribeDbContext.FileTableDdl;
                ddl.ExecuteNonQuery();
            }

            foreach (string ddlText in new[]
                     {
                         ScribeDbContext.TrackTableDdl,
                         ScribeDbContext.SidecarTableDdl,
                         ScribeDbContext.PackTableDdl,
                     })
            {
                using var ddl = conn.CreateCommand();
                ddl.Transaction = tx;
                ddl.CommandText = ddlText;
                ddl.ExecuteNonQuery();
            }

            foreach (string index in ScribeDbContext.IdModelIndexDdl)
            {
                using var ix = conn.CreateCommand();
                ix.Transaction = tx;
                ix.CommandText = index;
                ix.ExecuteNonQuery();
            }

            // ------------------------------------------------------------------ packs → pack
            var packRows = ReadPacks(conn, tx);
            gelesen["packs"] = packRows.Count;

            var packIds = new List<string>();
            foreach (var p in packRows)
            {
                string packId = NeuesPackSegment(packIds);
                packIds.Add(packId);
                p.PackId = packId;
            }

            // Die Mitglieder-Signatur (F-M466/F-M650) steht seit Schema 19 in `source_matrix` (Kind = pack).
            // Sie gehört an den Behälter, das Urteil der Quelle nicht — deshalb wird sie hier mitgenommen,
            // wenn die Matrix sie kennt.
            var signatures = ReadPackSignatures(conn, tx);

            foreach (var p in packRows)
            {
                string matrixKey = SourceMatrix.KeyOf(
                    SourceMatrix.AnchorOf(null, p.ImdbId, p.Season), p.Source, MarkKind.Pack, p.Language, p.HearingImpaired == true);
                InsertPack(conn, tx, p, signatures.TryGetValue(matrixKey, out var sig) ? sig : (null, null));
            }

            geschrieben["pack"] = packRows.Count;

            // ------------------------------------------------------------------ media → file
            var fileRows = ReadFiles(conn, tx);
            gelesen["media"] = fileRows.Count;

            var fileIds = new List<string>();
            int packsOnFiles = 0;
            foreach (var f in fileRows)
            {
                f.FileId = NeuesDateiSegment(fileIds);
                fileIds.Add(f.FileId);

                var treffer = FindPack(packRows, f);
                if (treffer != null)
                {
                    f.PackId = treffer.PackId;
                    packsOnFiles++;
                }
            }

            var byHash = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fileRows)
            {
                if (!string.IsNullOrEmpty(f.OsHash) && !byHash.ContainsKey(f.OsHash))
                {
                    byHash[f.OsHash] = f;
                }
            }

            int orphans = 0;

            // Eine Spur, deren Hash keine Medienzeile trägt, bekommt hier eine eigene Datei — „keine Marke
            // ohne Subjekt". Sie ist eine Datei ohne Pfad, aber mit Hash, also auffindbar und mit einer
            // echten Kennung.
            FileRow FileFor(string? hash)
            {
                string h = hash ?? string.Empty;
                if (byHash.TryGetValue(h, out var vorhanden))
                {
                    return vorhanden;
                }

                var neue = new FileRow
                {
                    FileId = NeuesDateiSegment(fileIds),
                    OsHash = string.IsNullOrEmpty(h) ? null : h,
                    Created = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow,
                };
                fileIds.Add(neue.FileId);
                fileRows.Add(neue);
                orphans++;
                if (!string.IsNullOrEmpty(h))
                {
                    byHash[h] = neue;
                }

                return neue;
            }

            // ------------------------------------------------------------------ embeds → track
            var trackRows = ReadTracks(conn, "embeds", tx);
            gelesen["embeds"] = trackRows.Count;

            var sidecarRowsAlt = ReadSidecars(conn, tx);
            gelesen["sidecars"] = sidecarRowsAlt.Count;

            // Die Spurnummern: je Datei ein Kreis, eingebettete Spuren zuerst und in ihrer beobachteten
            // Reihenfolge (SubPos), danach die Seitenwagen. Vergeben wird fortlaufend; eingebettet heißt
            // „hat einen Container-Index", Seitenwagen nicht.
            foreach (var gruppe in trackRows
                         .Select(t => new { Zeile = t, Datei = FileFor(t.MediaHash) })
                         .GroupBy(x => x.Datei.FileId, StringComparer.Ordinal))
            {
                int nummer = 0;
                var ids = new List<string>();
                foreach (var eintrag in gruppe.OrderBy(x => x.Zeile.SubPos).ThenBy(x => x.Zeile.ContentHash, StringComparer.Ordinal))
                {
                    var datei = eintrag.Datei;
                    var t = eintrag.Zeile;
                    t.FileId = datei.FileId;
                    t.PackId = datei.PackId;
                    t.TrackNumber = nummer++;
                    t.TrackId = NeuesSpurSegment(ids);
                    ids.Add(t.TrackId);
                }
            }

            foreach (var gruppe in sidecarRowsAlt
                         .Select(s => new { Zeile = s, Datei = FileFor(s.MediaHash) })
                         .GroupBy(x => x.Datei.FileId, StringComparer.Ordinal))
            {
                int nummer = trackRows.Count(t => string.Equals(t.FileId, gruppe.Key, StringComparison.Ordinal));
                var ids = trackRows.Where(t => string.Equals(t.FileId, gruppe.Key, StringComparison.Ordinal))
                    .Select(t => t.TrackId).ToList();
                foreach (var eintrag in gruppe.OrderBy(x => x.Zeile.ContentHash, StringComparer.Ordinal))
                {
                    var datei = eintrag.Datei;
                    var s = eintrag.Zeile;
                    s.FileId = datei.FileId;
                    s.TrackNumber = nummer++;
                    s.TrackId = NeuesSpurSegment(ids);
                    ids.Add(s.TrackId);
                }
            }

            foreach (var f in fileRows)
            {
                InsertFile(conn, tx, f);
            }

            geschrieben["file"] = fileRows.Count;

            foreach (var t in trackRows)
            {
                InsertTrack(conn, tx, t);
            }

            geschrieben["track"] = trackRows.Count;

            foreach (var s in sidecarRowsAlt)
            {
                InsertSidecar(conn, tx, s);
            }

            geschrieben["sidecar"] = sidecarRowsAlt.Count;

            // ------------------------------------------------------- Alt-Tabellen niederlegen
            foreach (string tabelle in alt)
            {
                using var drop = conn.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = "DROP TABLE IF EXISTS " + tabelle + ";";
                drop.ExecuteNonQuery();
            }

            using (var marker = conn.CreateCommand())
            {
                marker.Transaction = tx;
                marker.CommandText = "UPDATE meta SET SchemaVersion = 22 WHERE Id = 'db';";
                marker.ExecuteNonQuery();
            }

            tx.Commit();

            logger?.LogInformation(
                "[Sub Scribe] data file: id model taken over — {Files} file(s), {Tracks} track(s), {Sidecars} sidecar(s), "
                + "{Packs} pack(s); {Numbers} number(s) from the per-file circle, {OnFiles} file(s) with their pack, "
                + "{Orphans} file(s) created for an orphan track; old tables dropped"
                + (sicherung != null ? ", safety copy {Backup}." : "."),
                fileRows.Count, trackRows.Count, sidecarRowsAlt.Count, packRows.Count,
                trackRows.Count + sidecarRowsAlt.Count, packsOnFiles, orphans,
                sicherung ?? "-");

            return new Outcome(true, sicherung, gelesen, geschrieben,
                trackRows.Count + sidecarRowsAlt.Count, packsOnFiles, orphans);
        }
        catch (Exception ex)
        {
            // Kein halber Zustand: die Transaktion ist zurückgerollt, die Datei ist unverändert, und der
            // nächste Start holt den Lauf nach.
            logger?.LogWarning(
                "[Sub Scribe] could not take the id model over from the old tables (continuing, the run is retried at the next start): {Msg}",
                ex.Message);
            return new Outcome(false, null, leer, leer, 0, 0, 0);
        }
    }

    // ---------------------------------------------------------------- das Segment-Register

    private static string NeuesPackSegment(IEnumerable<string> vergeben)
    {
        var besetzt = vergeben.ToHashSet(StringComparer.Ordinal);
        return ScribeIds.NewPackId(id => besetzt.Contains(id));
    }

    private static string NeuesDateiSegment(IEnumerable<string> vergeben)
    {
        var besetzt = vergeben.ToHashSet(StringComparer.Ordinal);
        return ScribeIds.NewFileId(id => besetzt.Contains(id));
    }

    private static string NeuesSpurSegment(IList<string> vergeben)
        => ScribeIds.NewTrackId(id => vergeben.Contains(id, StringComparer.Ordinal));

    // ---------------------------------------------------------------- Alt-Bestand lesen

    private sealed class PackRow
    {
        public string PackId { get; set; } = string.Empty;
        public string SeasonKey { get; set; } = string.Empty;
        public string Source { get; set; } = "subdl";
        public string ImdbId { get; set; } = string.Empty;
        public string? TmdbId { get; set; }
        public int Season { get; set; }
        public string Language { get; set; } = string.Empty;
        public bool? HearingImpaired { get; set; }
        public string? ReleaseName { get; set; }
        public string Episodes { get; set; } = string.Empty;
        public string? AbsorbedIds { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Updated { get; set; } = DateTime.UtcNow;
    }

    private sealed class FileRow
    {
        public string FileId { get; set; } = string.Empty;
        public string? PackId { get; set; }
        public string? OsHash { get; set; }
        public string? FileName { get; set; }
        public long? Size { get; set; }
        public long? MtimeUnix { get; set; }
        public string? Path { get; set; }
        public string? JellyfinItemId { get; set; }
        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }
        public string? TvstShowId { get; set; }
        public string? MsubMovieId { get; set; }
        public string? SdId { get; set; }
        public bool IsSeries { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? LanguagesAvailable { get; set; }
        public DateTime? SubtitlesUploadedAt { get; set; }
        public DateTime? LastSearchUtc { get; set; }
        public string? LastSearchLanguages { get; set; }
        public DateTime? LastSearchEmptyUtc { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    private sealed class TrackRow
    {
        public string TrackId { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public string? PackId { get; set; }
        public int TrackNumber { get; set; }
        public string MediaHash { get; set; } = string.Empty;
        public int SubPos { get; set; }
        public string Language { get; set; } = string.Empty;
        public bool? HearingImpaired { get; set; }
        public bool? Forced { get; set; }
        public string? ContentHash { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public DateTime StatusAt { get; set; } = DateTime.UtcNow;
        public string? Origin { get; set; }
        public string? OriginId { get; set; }
        public DateTime? DetectionAttemptedAt { get; set; }
        public string? DetectionOutcome { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    private sealed class SidecarRow
    {
        public string TrackId { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public int TrackNumber { get; set; }
        public string MediaHash { get; set; } = string.Empty;
        public string? ContentHash { get; set; }
        public string? FileName { get; set; }
        public string? Path { get; set; }
        public string Language { get; set; } = string.Empty;
        public bool? HearingImpaired { get; set; }
        public bool? Forced { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public DateTime StatusAt { get; set; } = DateTime.UtcNow;
        public string? Origin { get; set; }
        public string? OriginId { get; set; }
        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }
        public bool IsSeries { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    private static List<PackRow> ReadPacks(SqliteConnection conn, SqliteTransaction tx)
    {
        var liste = new List<PackRow>();
        if (!TableExists(conn, "packs"))
        {
            return liste;
        }

        // Jede Spalte wird ERST GEFRAGT, dann gelesen: ein Bestand aus Schema 17 kennt weder `Source`
        // (F-M642, kam spaeter) noch die breiten Marken-Spalten. Fehlt eine, liefert die Abfrage NULL
        // und die Zeile bekommt ihren Vorgabewert — statt dass der ganze Lauf an ihr scheitert.
        string Spalte(string name) => ColumnExists(conn, "packs", name) ? name : "NULL";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT Id, " + Spalte("Source") + ", " + Spalte("ImdbId") + ", " + Spalte("Season") + ", "
            + Spalte("Language") + ", " + Spalte("HearingImpaired") + ", " + Spalte("ReleaseName") + ", "
            + Spalte("Episodes") + ", " + Spalte("AbsorbedIds") + ", " + Spalte("Created") + ", "
            + Spalte("Updated") + " FROM packs;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string schluessel = Text(r, 0) ?? string.Empty;
            string quelle = Text(r, 1) ?? "subdl";

            // F-M642: ein Bestand von VOR der Quellen-Spalte trägt den Schlüssel ohne fünften Teil
            // ("…|S01|EN|plain"). Ohne das Nachziehen wäre die Zeile unter ihrem heutigen Schlüssel
            // unerreichbar und der Lauf läse jede gepackte Staffel als „nie gepackt".
            if (schluessel.Count(c => c == '|') == 3)
            {
                schluessel = schluessel + "|" + quelle;
            }

            liste.Add(new PackRow
            {
                SeasonKey = schluessel,
                Source = Text(r, 1) ?? "subdl",
                ImdbId = Text(r, 2) ?? string.Empty,
                Season = Int(r, 3) ?? 0,
                Language = Text(r, 4) ?? string.Empty,
                HearingImpaired = Bool(r, 5),
                ReleaseName = Text(r, 6),
                Episodes = Text(r, 7) ?? string.Empty,
                AbsorbedIds = Text(r, 8),
                Created = Moment(Text(r, 9)) ?? DateTime.UtcNow,
                Updated = Moment(Text(r, 10)) ?? DateTime.UtcNow,
            });
        }

        return liste;
    }

    private static Dictionary<string, (string? Fingerprint, long? MtimeUnix)> ReadPackSignatures(
        SqliteConnection conn, SqliteTransaction tx)
    {
        var map = new Dictionary<string, (string?, long?)>(StringComparer.Ordinal);
        if (!TableExists(conn, "source_matrix"))
        {
            return map;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, Fingerprint, MtimeUnix FROM source_matrix WHERE Kind = 'pack';";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Der Anker einer Pack-Marke ist "i:<imdb>#S<nn>" — daraus wird der Staffelschlüssel nicht
            // rekonstruiert, sondern über den Anker der Zeile gesucht (siehe InsertPack).
            map[Text(r, 0) ?? string.Empty] = (Text(r, 1), Int64(r, 2));
        }

        return map;
    }

    private static List<FileRow> ReadFiles(SqliteConnection conn, SqliteTransaction tx)
    {
        var liste = new List<FileRow>();
        if (!TableExists(conn, "media"))
        {
            return liste;
        }

        string Spalte(string name) => ColumnExists(conn, "media", name) ? name : "NULL";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT Id, " + Spalte("Path") + ", " + Spalte("JellyfinItemId") + ", " + Spalte("ImdbId") + ", "
            + Spalte("TmdbId") + ", " + Spalte("TvstShowId") + ", " + Spalte("MsubMovieId") + ", "
            + Spalte("SdId") + ", " + Spalte("IsSeries") + ", " + Spalte("Season") + ", " + Spalte("Episode") + ", "
            + Spalte("LanguagesAvailable") + ", " + Spalte("SubtitlesUploadedAt") + ", " + Spalte("LastSearchUtc") + ", "
            + Spalte("LastSearchLanguages") + ", " + Spalte("LastSearchEmptyUtc") + ", " + Spalte("LastSeen") + " FROM media;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string? pfad = Text(r, 1);
            var zeile = new FileRow
            {
                OsHash = Text(r, 0),
                Path = pfad,
                FileName = string.IsNullOrEmpty(pfad) ? null : Path.GetFileName(pfad),
                JellyfinItemId = Text(r, 2),
                ImdbId = Text(r, 3),
                TmdbId = Text(r, 4),
                TvstShowId = Text(r, 5),
                MsubMovieId = Text(r, 6),
                SdId = Text(r, 7),
                IsSeries = Bool(r, 8) ?? false,
                Season = Int(r, 9),
                Episode = Int(r, 10),
                LanguagesAvailable = Text(r, 11),
                SubtitlesUploadedAt = Moment(Text(r, 12)),
                LastSearchUtc = Moment(Text(r, 13)),
                LastSearchLanguages = Text(r, 14),
                LastSearchEmptyUtc = Moment(Text(r, 15)),
                LastSeen = Moment(Text(r, 16)) ?? DateTime.UtcNow,
                Created = DateTime.UtcNow,
            };
            liste.Add(zeile);
        }

        return liste;
    }

    private static List<TrackRow> ReadTracks(SqliteConnection conn, string table, SqliteTransaction tx)
    {
        var liste = new List<TrackRow>();
        if (!TableExists(conn, table))
        {
            return liste;
        }

        string Spalte(string name) => ColumnExists(conn, table, name) ? name : "NULL";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT " + Spalte("MediaHash") + ", " + Spalte("SubPos") + ", " + Spalte("Language") + ", "
            + Spalte("HearingImpaired") + ", " + Spalte("Forced") + ", " + Spalte("ContentHash") + ", "
            + Spalte("Status") + ", " + Spalte("Reason") + ", " + Spalte("StatusAt") + ", " + Spalte("Origin") + ", "
            + Spalte("OriginId") + ", " + Spalte("DetectionAttemptedAt") + ", " + Spalte("DetectionOutcome")
            + " FROM " + table + ";";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            liste.Add(new TrackRow
            {
                MediaHash = Text(r, 0) ?? string.Empty,
                SubPos = Int(r, 1) ?? 0,
                Language = Text(r, 2) ?? string.Empty,
                HearingImpaired = Bool(r, 3),
                Forced = Bool(r, 4),
                ContentHash = Text(r, 5),
                Status = Text(r, 6) ?? string.Empty,
                Reason = Text(r, 7),
                StatusAt = Moment(Text(r, 8)) ?? DateTime.UtcNow,
                Origin = Text(r, 9),
                OriginId = Text(r, 10),
                DetectionAttemptedAt = Moment(Text(r, 11)),
                DetectionOutcome = Text(r, 12),
                Created = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
            });
        }

        return liste;
    }

    private static List<SidecarRow> ReadSidecars(SqliteConnection conn, SqliteTransaction tx)
    {
        var liste = new List<SidecarRow>();
        if (!TableExists(conn, "sidecars"))
        {
            return liste;
        }

        string Spalte(string name) => ColumnExists(conn, "sidecars", name) ? name : "NULL";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT " + Spalte("MediaHash") + ", " + Spalte("ContentHash") + ", " + Spalte("FileName") + ", "
            + Spalte("Path") + ", " + Spalte("Language") + ", " + Spalte("HearingImpaired") + ", "
            + Spalte("Forced") + ", " + Spalte("Status") + ", " + Spalte("Reason") + ", " + Spalte("StatusAt") + ", "
            + Spalte("Origin") + ", " + Spalte("OriginId") + ", " + Spalte("ImdbId") + ", " + Spalte("TmdbId") + ", "
            + Spalte("IsSeries") + ", " + Spalte("Season") + ", " + Spalte("Episode") + " FROM sidecars;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            liste.Add(new SidecarRow
            {
                MediaHash = Text(r, 0) ?? string.Empty,
                ContentHash = Text(r, 1),
                FileName = Text(r, 2),
                Path = Text(r, 3),
                Language = Text(r, 4) ?? string.Empty,
                HearingImpaired = Bool(r, 5),
                Forced = Bool(r, 6),
                Status = Text(r, 7) ?? string.Empty,
                Reason = Text(r, 8),
                StatusAt = Moment(Text(r, 9)) ?? DateTime.UtcNow,
                Origin = Text(r, 10),
                OriginId = Text(r, 11),
                ImdbId = Text(r, 12),
                TmdbId = Text(r, 13),
                IsSeries = Bool(r, 14) ?? false,
                Season = Int(r, 15),
                Episode = Int(r, 16),
                Created = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
            });
        }

        return liste;
    }

    /// <summary>
    /// Das Pack einer Datei: dieselbe Serie, dieselbe Staffel, und — wenn die Zeile die Folge nennt — die
    /// Folge unter den Mitgliedern. Ein Pack trägt EINE Sprache und EINE Quelle; eine Datei gehört zu genau
    /// einem Pack, deshalb entscheidet bei mehreren Treffern eine feste Reihenfolge (Quelle, Sprache,
    /// Variante), damit zwei Läufe dasselbe Ergebnis liefern.
    /// </summary>
    private static PackRow? FindPack(List<PackRow> packs, FileRow file)
    {
        if (string.IsNullOrEmpty(file.ImdbId) || file.Season == null)
        {
            return null;
        }

        return packs
            .Where(p => string.Equals(p.ImdbId, file.ImdbId, StringComparison.Ordinal) && p.Season == file.Season)
            .Where(p => file.Episode == null || EnthaeltFolge(p.Episodes, file.Episode.Value))
            .OrderBy(p => p.Source, StringComparer.Ordinal)
            .ThenBy(p => p.Language, StringComparer.Ordinal)
            .ThenBy(p => p.HearingImpaired == true)
            .FirstOrDefault();
    }

    private static bool EnthaeltFolge(string? episodes, int episode)
    {
        if (string.IsNullOrWhiteSpace(episodes))
        {
            // Ein Pack ohne Folgenliste ist die ältere Form: er gilt für die ganze Staffel.
            return true;
        }

        foreach (string teil in episodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(teil, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n == episode)
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- neue Zeilen schreiben

    private static void InsertPack(SqliteConnection conn, SqliteTransaction tx, PackRow p, (string? Fingerprint, long? MtimeUnix) sig)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO pack (PackId, SeasonKey, Source, ImdbId, TmdbId, Season, Language, HearingImpaired, ReleaseName, "
            + "Signature, MtimeUnix, EpisodeCount, Episodes, AbsorbedIds, Created, Updated) "
            + "VALUES ($pack, $key, $source, $imdb, NULL, $season, $lang, $hi, $release, $sig, $mtime, $count, $eps, $absorbed, $created, $updated);";
        cmd.Parameters.AddWithValue("$pack", p.PackId);
        cmd.Parameters.AddWithValue("$key", p.SeasonKey);
        cmd.Parameters.AddWithValue("$source", p.Source);
        cmd.Parameters.AddWithValue("$imdb", p.ImdbId);
        cmd.Parameters.AddWithValue("$season", p.Season);
        cmd.Parameters.AddWithValue("$lang", p.Language);
        cmd.Parameters.AddWithValue("$hi", p.HearingImpaired.HasValue ? (p.HearingImpaired.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$release", (object?)p.ReleaseName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sig", (object?)sig.Fingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mtime", sig.MtimeUnix.HasValue ? sig.MtimeUnix.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$count", p.Episodes.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
        cmd.Parameters.AddWithValue("$eps", p.Episodes);
        cmd.Parameters.AddWithValue("$absorbed", (object?)p.AbsorbedIds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(p.Created));
        cmd.Parameters.AddWithValue("$updated", Iso(p.Updated));
        cmd.ExecuteNonQuery();
    }

    private static void InsertFile(SqliteConnection conn, SqliteTransaction tx, FileRow f)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO file (FileId, PackId, OsHash, FileName, Size, MtimeUnix, Path, JellyfinItemId, ImdbId, TmdbId, "
            + "TvstShowId, MsubMovieId, SdId, IsSeries, Season, Episode, LanguagesAvailable, SubtitlesUploadedAt, "
            + "LastSearchUtc, LastSearchLanguages, LastSearchEmptyUtc, Created, LastSeen) "
            + "VALUES ($file, $pack, $hash, $name, NULL, NULL, $path, $item, $imdb, $tmdb, $tvst, $msub, $sd, $series, "
            + "$season, $episode, $langs, $uploaded, $search, $searchLang, $searchEmpty, $created, $seen);";
        cmd.Parameters.AddWithValue("$file", f.FileId);
        cmd.Parameters.AddWithValue("$pack", (object?)f.PackId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)f.OsHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)f.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)f.Path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$item", (object?)f.JellyfinItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imdb", (object?)f.ImdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tmdb", (object?)f.TmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tvst", (object?)f.TvstShowId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$msub", (object?)f.MsubMovieId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sd", (object?)f.SdId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$series", f.IsSeries ? 1 : 0);
        cmd.Parameters.AddWithValue("$season", (object?)f.Season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$episode", (object?)f.Episode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$langs", (object?)f.LanguagesAvailable ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uploaded", NullableIso(f.SubtitlesUploadedAt));
        cmd.Parameters.AddWithValue("$search", NullableIso(f.LastSearchUtc));
        cmd.Parameters.AddWithValue("$searchLang", (object?)f.LastSearchLanguages ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$searchEmpty", NullableIso(f.LastSearchEmptyUtc));
        cmd.Parameters.AddWithValue("$created", Iso(f.Created));
        cmd.Parameters.AddWithValue("$seen", Iso(f.LastSeen));
        cmd.ExecuteNonQuery();
    }

    private static void InsertTrack(SqliteConnection conn, SqliteTransaction tx, TrackRow t)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO track (TrackId, FileId, PackId, TrackNumber, MediaHash, SubPos, Language, HearingImpaired, "
            + "Forced, ContentHash, Status, Reason, StatusAt, Origin, OriginId, DetectionAttemptedAt, DetectionOutcome, Created, LastSeen) "
            + "VALUES ($track, $file, $pack, $number, $hash, $pos, $lang, $hi, $forced, $content, $status, $reason, "
            + "$statusAt, $origin, $originId, $detectAt, $detectOutcome, $created, $seen);";
        cmd.Parameters.AddWithValue("$track", t.TrackId);
        cmd.Parameters.AddWithValue("$file", t.FileId);
        cmd.Parameters.AddWithValue("$pack", (object?)t.PackId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$number", t.TrackNumber);
        cmd.Parameters.AddWithValue("$hash", t.MediaHash);
        cmd.Parameters.AddWithValue("$pos", t.SubPos);
        cmd.Parameters.AddWithValue("$lang", t.Language);
        cmd.Parameters.AddWithValue("$hi", t.HearingImpaired.HasValue ? (t.HearingImpaired.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$forced", t.Forced.HasValue ? (t.Forced.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$content", (object?)t.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", t.Status);
        cmd.Parameters.AddWithValue("$reason", (object?)t.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$statusAt", Iso(t.StatusAt));
        cmd.Parameters.AddWithValue("$origin", (object?)t.Origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$originId", (object?)t.OriginId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detectAt", NullableIso(t.DetectionAttemptedAt));
        cmd.Parameters.AddWithValue("$detectOutcome", (object?)t.DetectionOutcome ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(t.Created));
        cmd.Parameters.AddWithValue("$seen", Iso(t.LastSeen));
        cmd.ExecuteNonQuery();
    }

    private static void InsertSidecar(SqliteConnection conn, SqliteTransaction tx, SidecarRow s)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            // F-M671: kein PackId — ein Seitenwagen ist `0000-yyyyy-zzz`.
            "INSERT INTO sidecar (TrackId, FileId, TrackNumber, MediaHash, ContentHash, FileName, Path, Language, "
            + "HearingImpaired, Forced, Status, Reason, StatusAt, Origin, OriginId, ImdbId, TmdbId, IsSeries, Season, Episode, Created, LastSeen) "
            + "VALUES ($track, $file, $number, $hash, $content, $name, $path, $lang, $hi, $forced, $status, $reason, "
            + "$statusAt, $origin, $originId, $imdb, $tmdb, $series, $season, $episode, $created, $seen);";
        cmd.Parameters.AddWithValue("$track", s.TrackId);
        cmd.Parameters.AddWithValue("$file", s.FileId);
        cmd.Parameters.AddWithValue("$number", s.TrackNumber);
        cmd.Parameters.AddWithValue("$hash", s.MediaHash);
        cmd.Parameters.AddWithValue("$content", (object?)s.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", (object?)s.FileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)s.Path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lang", s.Language);
        cmd.Parameters.AddWithValue("$hi", s.HearingImpaired.HasValue ? (s.HearingImpaired.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$forced", s.Forced.HasValue ? (s.Forced.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.Parameters.AddWithValue("$reason", (object?)s.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$statusAt", Iso(s.StatusAt));
        cmd.Parameters.AddWithValue("$origin", (object?)s.Origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$originId", (object?)s.OriginId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imdb", (object?)s.ImdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tmdb", (object?)s.TmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$series", s.IsSeries ? 1 : 0);
        cmd.Parameters.AddWithValue("$season", (object?)s.Season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$episode", (object?)s.Episode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(s.Created));
        cmd.Parameters.AddWithValue("$seen", Iso(s.LastSeen));
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- Werkzeug

    private static string? Text(SqliteDataReader r, int i)
        => i < 0 || r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);

    private static int? Int(SqliteDataReader r, int i)
        => i < 0 || r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);

    private static long? Int64(SqliteDataReader r, int i)
        => i < 0 || r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);

    private static bool? Bool(SqliteDataReader r, int i)
        => i < 0 || r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture) != 0;

    private static DateTime? Moment(string? text)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static object NullableIso(DateTime? moment)
        => moment.HasValue
            ? moment.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)
            : DBNull.Value;

    private static string Iso(DateTime? moment)
        => moment.HasValue
            ? moment.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)
            : string.Empty;

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $n;";
        cmd.Parameters.AddWithValue("$n", table);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(" + table + ");";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Checkpoint(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }
}
