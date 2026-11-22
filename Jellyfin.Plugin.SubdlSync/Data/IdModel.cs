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
/// F-M671/F-M672: die einmalige Übernahme des Altbestands in das Kennungs-Modell und die
/// Quellen-Tabellen (Schema 23) — in EINER Transaktion, nach EINER Sicherung.
/// <para>
/// WAS HIER PASSIERT: <c>media</c> → <c>file</c>, <c>embeds</c> → <c>track</c>, <c>sidecars</c> →
/// <c>sidecar</c>, <c>packs</c> → <c>pack</c>. Jede Zeile bekommt ihre zufällige, unveränderliche Kennung
/// (<see cref="ScribeIds"/>), jede Spur und jeder Seitenwagen seine Nummer aus EINEM Nummernkreis je
/// Datei, jede Spur das Pack ihrer Sprache und Variante (F-M673) und alle vier Ebenen ihren stabilen
/// Anzeige-String (F-M675). Danach wandern die Quellen-Urteile und -Kennungen aus <c>source_matrix</c>,
/// <c>rejected_candidates</c> und der Spalte <c>OriginId</c> in <c>upload</c>/<c>reference</c>/
/// <c>badapple</c> (F-M672), und die Alt-Tabellen und Alt-Spalten fallen — in DERSELBEN Transaktion.
/// </para>
/// <para>
/// ZWEI WEGE, EIN ZIEL: eine Datei aus Schema 17 hat die vier Alt-Tabellen und die breiten
/// Quellen-Spalten; eine aus Schema 22 hat die vier Tabellen, aber noch die Matrix, die verworfenen
/// Kandidaten, <c>file.PackId</c>, ein falsch zugeordnetes <c>track.PackId</c> und <c>OriginId</c>.
/// Beide Wege enden bei Schema 23, und beide Schritte hängen am BESTAND, nicht am Marker: was schon da
/// ist, wird nicht neu angelegt, was fehlt, wird nachgeholt.
/// </para>
/// <para>
/// WANN SIE LÄUFT: wenn die Datei existiert, ein Store ist (<c>meta</c> vorhanden) und noch irgendetwas
/// aus einem älteren Stand darin steht — eine der vier Alt-Tabellen, <c>source_matrix</c>,
/// <c>rejected_candidates</c>, die Spalte <c>file.PackId</c>, <c>track.OriginId</c>/<c>sidecar.OriginId</c>
/// oder ein fehlendes <c>Display</c>. Ein abgebrochener Lauf rollt zurück und wird beim nächsten Start
/// nachgeholt; eine übernommene Datei tut nichts (idempotent).
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
    /// <param name="Ran">True when the tables were filled by this call.</param>
    /// <param name="BackupPath">Path of the timestamped copy taken before the change, else null.</param>
    /// <param name="Read">Old table → number of rows read.</param>
    /// <param name="Written">New table → number of rows written.</param>
    /// <param name="TrackNumbers">How many tracks and sidecars received a number from their file's circle.</param>
    /// <param name="PacksOnTracks">How many tracks were given the id of the pack covering their language.</param>
    /// <param name="OrphanFiles">Files created because a track pointed at a hash no media row carried.</param>
    /// <param name="References">How many reference rows were written (ids taken over).</param>
    /// <param name="BadApples">How many bad-apple rows were written.</param>
    /// <param name="Unplaced">Old marks that had no subject — they live on in the backup only.</param>
    public sealed record Outcome(
        bool Ran,
        string? BackupPath,
        IReadOnlyDictionary<string, int> Read,
        IReadOnlyDictionary<string, int> Written,
        int TrackNumbers,
        int PacksOnTracks,
        int OrphanFiles,
        int References = 0,
        int BadApples = 0,
        int Unplaced = 0);

    /// <summary>
    /// Legt die vier Entity-Tabellen und die drei Quellen-Tabellen an, falls sie fehlen, und zieht ein
    /// fehlendes <c>Display</c> nach. Der harmlose Weg für eine Datei, die ohne Migration geöffnet wurde
    /// (ausdrückliche Datei) oder deren Übernahme abgebrochen ist.
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

            // F-M677: die Mitgliedschaft — nach den vier Tabellen, deren Kennungen sie nennt.
            db.Database.ExecuteSqlRaw(ScribeDbContext.PackTrackTableDdl);
            foreach (string index in ScribeDbContext.PackTrackIndexDdl)
            {
                db.Database.ExecuteSqlRaw(index);
            }

            SourceStore.EnsureTables(db, logger);
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
    /// Führt die Übernahme aus. Läuft VOR dem Öffnen des Stores, weil sie Tabellen und Spalten liest, die
    /// das neue Modell nicht mehr kennt.
    /// </summary>
    /// <param name="dbPath">Path of the live data file.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="backup">
    /// True to copy the file aside first. False only when this same start already took a safety copy (the
    /// marks takeover runs first and its copy is the state before both steps) or when the file was just
    /// built by the format switch (its predecessor is kept by F-M638).
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
            bool altEntities = alt.Count > 0;
            bool quellenArbeit = TableExists(conn, "source_matrix")
                                 || TableExists(conn, "rejected_candidates")
                                 || (TableExists(conn, "file") && ColumnExists(conn, "file", "PackId"))
                                 || (TableExists(conn, "track") && ColumnExists(conn, "track", "OriginId"))
                                 || (TableExists(conn, "sidecar") && ColumnExists(conn, "sidecar", "OriginId"))
                                 || (TableExists(conn, "track") && ColumnExists(conn, "track", "PackId"))
                                 || (TableExists(conn, "track") && ColumnExists(conn, "track", "FileId"))
                                 || (TableExists(conn, "sidecar") && ColumnExists(conn, "sidecar", "FileId"))
                                 || !TableExists(conn, "pack_track")
                                 || (TableExists(conn, "file") && ColumnExists(conn, "file", "Display"))
                                 || (TableExists(conn, "track") && ColumnExists(conn, "track", "Display"));

            if (!altEntities && !quellenArbeit)
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
            var referenzen = new List<ReferenceRow>();
            var badApples = new List<BadAppleRow>();
            int packsOnTracks = 0;
            int orphans = 0;
            int nummern = 0;

            using var tx = conn.BeginTransaction();

            foreach (string ddlText in new[]
                     {
                         ScribeDbContext.FileTableDdl,
                         ScribeDbContext.TrackTableDdl,
                         ScribeDbContext.SidecarTableDdl,
                         ScribeDbContext.PackTableDdl,
                     })
            {
                Execute(conn, tx, ddlText);
            }

            foreach (string index in ScribeDbContext.IdModelIndexDdl)
            {
                Execute(conn, tx, index);
            }

            // Die drei Quellen-Tabellen entstehen, bevor die Urteile hineingehen — und in jedem Fall,
            // auch wenn es nichts zu übernehmen gibt.
            Execute(conn, tx, ScribeDbContext.UploadTableDdl);
            Execute(conn, tx, ScribeDbContext.ReferenceTableDdl);
            Execute(conn, tx, ScribeDbContext.BadAppleTableDdl);
            foreach (string index in ScribeDbContext.SourceTableIndexDdl)
            {
                Execute(conn, tx, index);
            }

            var packRows = ReadPacks(conn, tx);
            var fileRows = new List<FileRow>();
            var trackRows = new List<TrackRow>();
            var sidecarRows = new List<SidecarRow>();

            if (altEntities)
            {
                gelesen["packs"] = packRows.Count;

                var packIds = new List<string>();
                foreach (var p in packRows)
                {
                    // F-M676: die Id ist der lesbare Text `[xxxx]`.
                    p.PackId = ScribeIds.PackIdOf(NeuesPackSegment(packIds));
                    packIds.Add(p.PackId);
                }

                // Die Mitglieder-Signatur (F-M466/F-M650) steht bis Schema 22 in `source_matrix`
                // (Kind = pack). Sie gehört an den Behälter, das Urteil der Quelle nicht.
                var signatures = ReadPackSignatures(conn, tx);

                foreach (var p in packRows)
                {
                    string matrixKey = LegacyMatrixKeys.KeyOf(
                        LegacyMatrixKeys.AnchorOf(null, p.ImdbId, p.Season), p.Source, MarkKind.Pack,
                        p.Language, p.HearingImpaired == true);
                    InsertPack(conn, tx, p, signatures.TryGetValue(matrixKey, out var sig) ? sig : (null, null));
                }

                geschrieben["pack"] = packRows.Count;

                // -------------------------------------------------------------- media → file
                fileRows = ReadFiles(conn, tx);
                gelesen["media"] = fileRows.Count;

                var fileIds = new List<string>();
                foreach (var f in fileRows)
                {
                    // F-M676: `#yyyyy`.
                    f.FileId = ScribeIds.FileIdOf(NeuesDateiSegment(fileIds));
                    fileIds.Add(f.FileId);
                }

                var byHash = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in fileRows)
                {
                    if (!string.IsNullOrEmpty(f.OsHash) && !byHash.ContainsKey(f.OsHash))
                    {
                        byHash[f.OsHash] = f;
                    }
                }

                // Eine Spur, deren Hash keine Medienzeile trägt, bekommt hier eine eigene Datei — „keine
                // Marke ohne Subjekt". Sie ist eine Datei ohne Pfad, aber mit Hash, also auffindbar und
                // mit einer echten Kennung.
                FileRow FileFor(string? hash)
                {
                    string h = hash ?? string.Empty;
                    if (byHash.TryGetValue(h, out var vorhanden))
                    {
                        return vorhanden;
                    }

                    var neue = new FileRow
                    {
                        FileId = ScribeIds.FileIdOf(NeuesDateiSegment(fileIds)),
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

                // -------------------------------------------------------------- embeds → track
                trackRows = ReadTracks(conn, "embeds", tx);
                gelesen["embeds"] = trackRows.Count;

                sidecarRows = ReadSidecars(conn, tx);
                gelesen["sidecars"] = sidecarRows.Count;

                // Die Spurnummern: je Datei ein Kreis, eingebettete Spuren zuerst und in ihrer
                // beobachteten Reihenfolge (SubPos), danach die Seitenwagen.
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
                        t.TrackNumber = nummer++;
                        string segment = NeuesSpurSegment(ids);
                        t.TrackId = ScribeIds.TrackIdOf(datei.FileId, segment);
                        ids.Add(segment);
                    }
                }

                foreach (var gruppe in sidecarRows
                             .Select(s => new { Zeile = s, Datei = FileFor(s.MediaHash) })
                             .GroupBy(x => x.Datei.FileId, StringComparer.Ordinal))
                {
                    int nummer = trackRows.Count(t => string.Equals(t.FileId, gruppe.Key, StringComparison.Ordinal));
                    var ids = trackRows.Where(t => string.Equals(t.FileId, gruppe.Key, StringComparison.Ordinal))
                        .Select(t => ScribeIds.TrackPartOf(t.TrackId)).ToList();
                    foreach (var eintrag in gruppe.OrderBy(x => x.Zeile.ContentHash, StringComparer.Ordinal))
                    {
                        var datei = eintrag.Datei;
                        var s = eintrag.Zeile;
                        s.FileId = datei.FileId;
                        s.TrackNumber = nummer++;
                        string segment = NeuesSpurSegment(ids);
                        s.TrackId = ScribeIds.SidecarIdOf(datei.FileId, segment);
                        ids.Add(segment);
                    }
                }

                nummern = trackRows.Count + sidecarRows.Count;

                // F-M673: das Pack hängt an der SPUR und wird über (Serie, Staffel, Sprache, Variante)
                // zugeordnet. Eine Datei kann Spuren in vielen Sprachen tragen; sie trägt kein Pack.
                var mitglieder = new List<PackTrackRow>();
                var dateiVon = fileRows.ToDictionary(f => f.FileId, f => f, StringComparer.Ordinal);
                var packEntities = packRows
                    .Select(p => new PackEntity
                    {
                        PackId = p.PackId,
                        SeasonKey = p.SeasonKey,
                        Source = p.Source,
                        ImdbId = p.ImdbId,
                        Season = p.Season,
                        Language = p.Language,
                        HearingImpaired = p.HearingImpaired,
                        Episodes = p.Episodes,
                    })
                    .ToList();

                foreach (var t in trackRows)
                {
                    dateiVon.TryGetValue(t.FileId, out var datei);
                    var dateiZeile = datei == null
                        ? null
                        : new FileEntity
                        {
                            FileId = datei.FileId,
                            ImdbId = datei.ImdbId,
                            Season = datei.Season,
                            Episode = datei.Episode,
                        };
                    // F-M677: die Mitgliedschaft hält der Behälter — eine Zeile je (Pack, Spur).
                    string? packId = SourceStore.PackForTrack(dateiZeile, t.Language, t.HearingImpaired == true, packEntities);
                    if (!string.IsNullOrEmpty(packId))
                    {
                        mitglieder.Add(new PackTrackRow { PackId = packId!, TrackId = t.TrackId });
                        packsOnTracks++;
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

                foreach (var s in sidecarRows)
                {
                    InsertSidecar(conn, tx, s);
                }

                geschrieben["sidecar"] = sidecarRows.Count;

                // F-M677: das Array des Behälters, zeilenweise. Erst JETZT, weil beide Seiten ihre Kennung
                // brauchen — die Fremdschlüssel lassen keine Zeile zu, die auf nichts zeigt.
                Execute(conn, tx, ScribeDbContext.PackTrackTableDdl);
                foreach (string index in ScribeDbContext.PackTrackIndexDdl)
                {
                    Execute(conn, tx, index);
                }

                foreach (var m in mitglieder)
                {
                    InsertPackTrack(conn, tx, m);
                }

                geschrieben["pack_track"] = mitglieder.Count;

                // ------------------------------------------------- Alt-Tabellen niederlegen
                foreach (string tabelle in alt)
                {
                    Execute(conn, tx, "DROP TABLE IF EXISTS " + tabelle + ";");
                }
            }
            else
            {
                // Schema 22 → 23: die vier Tabellen stehen schon, aber in der alten FORM — `track` und
                // `sidecar` tragen ihre Datei als eigene Spalte, und die steht im Primärschlüssel. Sie
                // müssen deshalb neu gebaut werden (SQLite kann eine Schlüsselspalte nicht entfernen),
                // nicht bloß beschnitten.
                //
                // Reihenfolge: erst die MITGLIEDSCHAFT lesen (sie hängt an `track.PackId`, das gleich
                // fällt), dann die Tabellen neu bauen, dann `pack_track` füllen.
                var mitglieder22 = ReadMembership22(conn, tx);
                referenzen.AddRange(RebuildEntityTables(conn, tx, gelesen, logger));
                Execute(conn, tx, ScribeDbContext.PackTrackTableDdl);
                foreach (string index in ScribeDbContext.PackTrackIndexDdl)
                {
                    Execute(conn, tx, index);
                }

                foreach (var (packId, trackId) in mitglieder22)
                {
                    InsertPackTrack(conn, tx, new PackTrackRow { PackId = packId, TrackId = trackId });
                    packsOnTracks++;
                }

                if (mitglieder22.Count > 0)
                {
                    logger?.LogInformation(
                        "[Sub Scribe] data file: {Count} track(s) assigned to the pack of their language (F-M677).",
                        mitglieder22.Count);
                }
            }

            // -------------------------------------------------- die Zeilen für die Quellen-Tabellen
            referenzen.AddRange(ReadOriginIds(conn, tx, trackRows, sidecarRows, gelesen));

            var ausMatrix = TableExists(conn, "source_matrix")
                ? ReadMatrix(conn, tx, gelesen)
                : new List<MatrixRow>();

            var (uploadRows, referenceRows, unplaced) = ResolveMatrix(conn, tx, ausMatrix, referenzen, gelesen);

            var ausKandidaten = TableExists(conn, "rejected_candidates")
                ? ReadRejectedCandidates(conn, tx, gelesen)
                : new List<CandidateRow>();

            int ohneSubjekt = unplaced;
            badApples.AddRange(ResolveBadApples(conn, tx, ausKandidaten, gelesen, ref ohneSubjekt));

            // Die Felder, die es nicht mehr gibt: `file.PackId`, `track.PackId`, `track.FileId` und
            // `sidecar.FileId` (die Datei steht in der Id). Indizes weichen vorher, sonst verweigert
            // SQLite das Entfernen.
            foreach (var (tabelle, spalte, index) in new[]
                     {
                         ("file", "PackId", "IX_file_PackId"),
                         ("file", "Display", null),
                         ("track", "PackId", "IX_track_PackId"),
                         ("track", "FileId", "IX_track_FileId"),
                         ("track", "Display", null),
                         ("sidecar", "FileId", "IX_sidecar_FileId"),
                         ("sidecar", "Display", null),
                         ("pack", "Display", null),
                     })
            {
                if (!TableExists(conn, tabelle) || !ColumnExists(conn, tabelle, spalte))
                {
                    continue;
                }

                if (index != null)
                {
                    Execute(conn, tx, "DROP INDEX IF EXISTS " + index + ";");
                }

                Execute(conn, tx, "ALTER TABLE " + tabelle + " DROP COLUMN " + spalte + ";");
            }

            foreach (var row in uploadRows)
            {
                InsertUpload(conn, tx, row);
            }

            foreach (var row in referenceRows)
            {
                InsertReference(conn, tx, row);
            }

            foreach (var row in badApples)
            {
                InsertBadApple(conn, tx, row);
            }

            geschrieben["upload"] = uploadRows.Count;
            geschrieben["reference"] = referenceRows.Count;
            geschrieben["badapple"] = badApples.Count;

            // ------------------------------------------------------- Alt-Tabellen und -Spalten weg
            if (TableExists(conn, "source_matrix"))
            {
                Execute(conn, tx, "DROP TABLE IF EXISTS source_matrix;");
            }

            if (TableExists(conn, "rejected_candidates"))
            {
                Execute(conn, tx, "DROP TABLE IF EXISTS rejected_candidates;");
            }

            foreach (string tabelle in new[] { "track", "sidecar" })
            {
                if (ColumnExists(conn, tabelle, "OriginId"))
                {
                    Execute(conn, tx, "ALTER TABLE " + tabelle + " DROP COLUMN OriginId;");
                }
            }

            Execute(conn, tx, "UPDATE meta SET SchemaVersion = 23 WHERE Id = 'db';");

            tx.Commit();

            logger?.LogInformation(
                "[Sub Scribe] data file: schema 23 taken over — {Files} file(s), {Tracks} track(s), {Sidecars} sidecar(s), "
                + "{Packs} pack(s); {Numbers} number(s) from the per-file circle, {OnTracks} track(s) with their pack, "
                + "{Orphans} file(s) created for an orphan track; {Uploads} verdict(s), {References} reference(s), "
                + "{BadApples} bad apple(s), {Unplaced} old mark(s) without a subject; old tables and columns dropped"
                + (sicherung != null ? ", safety copy {Backup}." : "."),
                fileRows.Count, trackRows.Count, sidecarRows.Count, packRows.Count,
                nummern, packsOnTracks, orphans, uploadRows.Count, referenceRows.Count, badApples.Count,
                ohneSubjekt, sicherung ?? "-");

            return new Outcome(true, sicherung, gelesen, geschrieben, nummern, packsOnTracks, orphans,
                referenceRows.Count, badApples.Count, ohneSubjekt);
        }
        catch (Exception ex)
        {
            // Kein halber Zustand: die Transaktion ist zurückgerollt, die Datei ist unverändert, und der
            // nächste Start holt den Lauf nach.
            logger?.LogWarning(
                "[Sub Scribe] could not take schema 23 over from the old structure (continuing, the run is retried at the next start): {Msg}",
                ex.ToString());
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

        /// <summary>F-M676: abgeleitet aus <see cref="TrackId"/> — die Datei steht in der Id.</summary>
        public string FileId
        {
            get => ScribeIds.FileOf(TrackId);
            set => FileSegment = value;
        }

        /// <summary>Das Datei-Segment, wie es der Lauf vergibt (die Id wird daraus gebaut).</summary>
        public string FileSegment { get; set; } = string.Empty;

        /// <summary>Die alte Datei-Spalte einer 22er-Zeile (Teil ihres Primärschlüssels).</summary>
        public string DateiSpalte { get; set; } = string.Empty;

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

        // F-M672: die fremde Id lebt bis zum Ende dieses Laufs hier und wandert dann nach `reference`.
        public string? OriginId { get; set; }

        public DateTime? DetectionAttemptedAt { get; set; }
        public string? DetectionOutcome { get; set; }
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    private sealed class SidecarRow
    {
        public string TrackId { get; set; } = string.Empty;

        /// <summary>F-M676: abgeleitet aus <see cref="TrackId"/>.</summary>
        public string FileId
        {
            get => ScribeIds.FileOf(TrackId);
            set => FileSegment = value;
        }

        /// <summary>Das Datei-Segment, wie es der Lauf vergibt.</summary>
        public string FileSegment { get; set; } = string.Empty;

        /// <summary>Die alte Datei-Spalte einer 22er-Zeile.</summary>
        public string DateiSpalte { get; set; } = string.Empty;

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

    private sealed class PackTrackRow
    {
        public string PackId { get; set; } = string.Empty;
        public string TrackId { get; set; } = string.Empty;
    }

    private sealed class ReferenceRow
    {
        public string SubjectId { get; set; } = string.Empty;
        public string Source { get; set; } = "subdl";
        public string? DownloadSourceId { get; set; }
        public string? UploadedId { get; set; }
        public string? ItemId { get; set; }
        public string? SdId { get; set; }
        public string? Url { get; set; }

        public string Key => SubjectId + "|" + Source;
    }

    private sealed class UploadRow
    {
        public string SubjectId { get; set; } = string.Empty;
        public string Source { get; set; } = "subdl";
        public DateTime? UploadedAt { get; set; }
        public DateTime? RefusedAt { get; set; }
        public string? RefusedReason { get; set; }
        public string? RefusalSignature { get; set; }
        public string? Fingerprint { get; set; }
        public long? MtimeUnix { get; set; }

        public string Key => SubjectId + "|" + Source;
    }

    private sealed class BadAppleRow
    {
        public string SubjectId { get; set; } = string.Empty;
        public string Source { get; set; } = "subdl";
        public string SourceId { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Finding { get; set; } = string.Empty;
        public string NId { get; set; } = string.Empty;
        public string ReleaseName { get; set; } = string.Empty;
        public string Kind { get; set; } = "qa";
        public DateTime? ReportedAt { get; set; }

        public string Key => SubjectId + "|" + Source + "|" + SourceId;
    }

    private sealed class MatrixRow
    {
        public string Id { get; set; } = string.Empty;
        public string MediaHash { get; set; } = string.Empty;
        public string ImdbId { get; set; } = string.Empty;
        public string Source { get; set; } = "subdl";
        public string Kind { get; set; } = MarkKind.Track;
        public string Language { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? SubPos { get; set; }
        public bool? HearingImpaired { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? Fingerprint { get; set; }
        public long? MtimeUnix { get; set; }
        public string? SourceId { get; set; }
        public string? UploadId { get; set; }
        public string? RefusalReason { get; set; }
        public DateTime? RefusedAt { get; set; }
        public string? RefusalSignature { get; set; }
    }

    private sealed class CandidateRow
    {
        public string ItemId { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string SubdlId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string NId { get; set; } = string.Empty;
        public string ReleaseName { get; set; } = string.Empty;
        public string Finding { get; set; } = string.Empty;
        public string Kind { get; set; } = "qa";
        public DateTime? ReportedUtc { get; set; }
        public DateTime Updated { get; set; } = DateTime.UtcNow;
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
                Source = quelle,
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
            + Spalte("LastSearchLanguages") + ", " + Spalte("LastSearchEmptyUtc") + ", " + Spalte("LastSeen")
            + " FROM media;";
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
        => ReadTracks(conn, table, tx, out _);

    /// <summary>
    /// Liest die Spuren einer Tabelle. <paramref name="dateiSpalte"/> meldet, ob die alte Datei-Spalte
    /// <c>FileId</c> noch existiert (Schema 22) — sie ist Teil des Primärschlüssels und muss beim Neubau
    /// gelesen werden, bevor sie fällt.
    /// </summary>
    private static List<TrackRow> ReadTracks(
        SqliteConnection conn, string table, SqliteTransaction tx, out string dateiSpalte)
    {
        var liste = new List<TrackRow>();
        dateiSpalte = string.Empty;
        if (!TableExists(conn, table))
        {
            return liste;
        }

        string Spalte(string name) => ColumnExists(conn, table, name) ? name : "NULL";
        if (ColumnExists(conn, table, "FileId"))
        {
            dateiSpalte = "FileId";
        }

        int spalten = 13;
        int idxDatei = ColumnExists(conn, table, "FileId") ? spalten++ : -1;
        int idxId = ColumnExists(conn, table, "TrackId") ? spalten++ : -1;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT " + Spalte("MediaHash") + ", " + Spalte("SubPos") + ", " + Spalte("Language") + ", "
            + Spalte("HearingImpaired") + ", " + Spalte("Forced") + ", " + Spalte("ContentHash") + ", "
            + Spalte("Status") + ", " + Spalte("Reason") + ", " + Spalte("StatusAt") + ", " + Spalte("Origin") + ", "
            + Spalte("OriginId") + ", " + Spalte("DetectionAttemptedAt") + ", " + Spalte("DetectionOutcome")
            + (idxDatei >= 0 ? ", " + Spalte("FileId") : string.Empty)
            + (idxId >= 0 ? ", " + Spalte("TrackId") : string.Empty)
            + " FROM " + table + ";";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var zeile = new TrackRow
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
            };
            if (idxDatei >= 0)
            {
                zeile.DateiSpalte = Text(r, idxDatei) ?? string.Empty;
            }

            if (idxId >= 0)
            {
                zeile.TrackId = Text(r, idxId) ?? string.Empty;
            }

            liste.Add(zeile);
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
    /// Liest die Seitenwagen-Tabelle <c>sidecar</c> in ihrer ALTEN Form (Schema 22: eigene Datei-Spalte,
    /// eigene Id-Spalte, <c>OriginId</c>).
    /// </summary>
    private static List<SidecarRow> ReadSidecars(SqliteConnection conn, SqliteTransaction tx, out string dateiSpalte)
    {
        var liste = new List<SidecarRow>();
        dateiSpalte = string.Empty;
        if (!TableExists(conn, "sidecar"))
        {
            return liste;
        }

        string Spalte(string name) => ColumnExists(conn, "sidecar", name) ? name : "NULL";
        bool mitDatei = ColumnExists(conn, "sidecar", "FileId");
        if (mitDatei)
        {
            dateiSpalte = "FileId";
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT TrackId, " + Spalte("MediaHash") + ", " + Spalte("ContentHash") + ", " + Spalte("FileName") + ", "
            + Spalte("Path") + ", " + Spalte("Language") + ", " + Spalte("HearingImpaired") + ", "
            + Spalte("Forced") + ", " + Spalte("Status") + ", " + Spalte("Reason") + ", " + Spalte("StatusAt") + ", "
            + Spalte("Origin") + ", " + Spalte("OriginId") + ", " + Spalte("ImdbId") + ", " + Spalte("TmdbId") + ", "
            + Spalte("IsSeries") + ", " + Spalte("Season") + ", " + Spalte("Episode") + ", " + Spalte("Created") + ", "
            + Spalte("LastSeen") + ", " + Spalte("TrackNumber")
            + (mitDatei ? ", " + Spalte("FileId") : string.Empty) + " FROM sidecar;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var zeile = new SidecarRow
            {
                TrackId = Text(r, 0) ?? string.Empty,
                MediaHash = Text(r, 1) ?? string.Empty,
                ContentHash = Text(r, 2),
                FileName = Text(r, 3),
                Path = Text(r, 4),
                Language = Text(r, 5) ?? string.Empty,
                HearingImpaired = Bool(r, 6),
                Forced = Bool(r, 7),
                Status = Text(r, 8) ?? string.Empty,
                Reason = Text(r, 9),
                StatusAt = Moment(Text(r, 10)) ?? DateTime.UtcNow,
                Origin = Text(r, 11),
                OriginId = Text(r, 12),
                ImdbId = Text(r, 13),
                TmdbId = Text(r, 14),
                IsSeries = Bool(r, 15) ?? false,
                Season = Int(r, 16),
                Episode = Int(r, 17),
                Created = Moment(Text(r, 18)) ?? DateTime.UtcNow,
                LastSeen = Moment(Text(r, 19)) ?? DateTime.UtcNow,
                TrackNumber = Int(r, 20) ?? 0,
            };
            if (mitDatei)
            {
                zeile.DateiSpalte = Text(r, 21) ?? string.Empty;
            }

            liste.Add(zeile);
        }

        return liste;
    }

    /// <summary>
    /// F-M672: die fremde Id, die bis Schema 22 am Track beziehungsweise Seitenwagen stand, als Beleg
    /// lesen. Sie wird NICHT gelöscht, bevor sie hier gelesen ist — „vorher prüfen, dass kein Wert
    /// verloren geht".
    /// </summary>
    private static List<ReferenceRow> ReadOriginIds(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<TrackRow> neuGelesen,
        List<SidecarRow> neuGelesenWagen,
        Dictionary<string, int> gelesen)
    {
        var liste = new List<ReferenceRow>();
        int gefunden = 0;
        int ohneQuelle = 0;

        // Der Weg 17/21 → 23 liest die Ids aus den Zeilen, die dieser Lauf gerade übernommen hat.
        foreach (var (subject, origin, originId) in neuGelesen
                     .Select(t => (t.TrackId, t.Origin, t.OriginId))
                     .Concat(neuGelesenWagen.Select(s => (s.TrackId, s.Origin, s.OriginId))))
        {
            if (string.IsNullOrWhiteSpace(originId))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(origin))
            {
                // Ohne Quellenname ist die Id nicht zuzuordnen; sie lebt in der Sicherung weiter und
                // wird gezählt, nicht geraten.
                ohneQuelle++;
                continue;
            }

            gefunden++;
            liste.Add(new ReferenceRow
            {
                SubjectId = subject,
                Source = QuelleName(origin),
                DownloadSourceId = originId,
            });
        }

        // Der Weg 22 → 23 liest sie aus der Datei selbst.
        foreach (string tabelle in new[] { "track", "sidecar" })
        {
            if (!TableExists(conn, tabelle) || !ColumnExists(conn, tabelle, "OriginId"))
            {
                continue;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT TrackId, Origin, OriginId FROM " + tabelle + ";";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? subject = Text(r, 0);
                string? origin = Text(r, 1);
                string? id = Text(r, 2);
                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                // Auf dem 17er-Weg sind diese Zeilen die eben geschriebenen — dieselbe Aussage, die
                // oben schon gelesen wurde. Ein zweites Mal gezählt wäre sie doppelt.
                if (neuGelesen.Any(t => string.Equals(t.TrackId, subject, StringComparison.Ordinal))
                    || neuGelesenWagen.Any(s => string.Equals(s.TrackId, subject, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(origin))
                {
                    ohneQuelle++;
                    continue;
                }

                gefunden++;
                liste.Add(new ReferenceRow
                {
                    SubjectId = subject,
                    Source = QuelleName(origin),
                    DownloadSourceId = id,
                });
            }
        }

        gelesen["OriginId"] = gefunden;
        if (ohneQuelle > 0)
        {
            gelesen["OriginId ohne Quelle"] = ohneQuelle;
        }

        return liste;
    }

    private static string QuelleName(string origin)
    {
        if (origin.Contains("tvsub", StringComparison.OrdinalIgnoreCase))
        {
            return SourceStore.TvSubtitles;
        }

        if (origin.Contains("movie", StringComparison.OrdinalIgnoreCase)
            || origin.Contains("msub", StringComparison.OrdinalIgnoreCase))
        {
            return SourceStore.MovieSubtitles;
        }

        return SourceStore.Subdl;
    }

    private static List<MatrixRow> ReadMatrix(SqliteConnection conn, SqliteTransaction tx, Dictionary<string, int> gelesen)
    {
        var rows = new List<MatrixRow>();
        string Spalte(string name) => ColumnExists(conn, "source_matrix", name) ? name : "NULL";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT " + Spalte("Id") + ", " + Spalte("MediaHash") + ", " + Spalte("ImdbId") + ", "
            + Spalte("Source") + ", " + Spalte("Kind") + ", " + Spalte("Language") + ", " + Spalte("Season") + ", "
            + Spalte("SubPos") + ", " + Spalte("HearingImpaired") + ", " + Spalte("ClosedAt") + ", "
            + Spalte("Fingerprint") + ", " + Spalte("MtimeUnix") + ", " + Spalte("SourceId") + ", "
            + Spalte("UploadId") + ", " + Spalte("RefusalReason") + ", " + Spalte("RefusedAt") + ", "
            + Spalte("RefusalSignature") + " FROM source_matrix;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new MatrixRow
            {
                Id = Text(r, 0) ?? string.Empty,
                MediaHash = Text(r, 1) ?? string.Empty,
                ImdbId = Text(r, 2) ?? string.Empty,
                Source = Text(r, 3) ?? SourceStore.Subdl,
                Kind = Text(r, 4) ?? MarkKind.Track,
                Language = SourceStore.Normalise(Text(r, 5)),
                Season = Int(r, 6),
                SubPos = Int(r, 7),
                HearingImpaired = Bool(r, 8),
                ClosedAt = Moment(Text(r, 9)),
                Fingerprint = Text(r, 10),
                MtimeUnix = Int64(r, 11),
                SourceId = Text(r, 12),
                UploadId = Text(r, 13),
                RefusalReason = Text(r, 14),
                RefusedAt = Moment(Text(r, 15)),
                RefusalSignature = Text(r, 16),
            });
        }

        gelesen["source_matrix"] = rows.Count;
        return rows;
    }

    private static List<CandidateRow> ReadRejectedCandidates(
        SqliteConnection conn, SqliteTransaction tx, Dictionary<string, int> gelesen)
    {
        var liste = new List<CandidateRow>();
        string Spalte(string name) => ColumnExists(conn, "rejected_candidates", name) ? name : "NULL";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT " + Spalte("ItemId") + ", " + Spalte("Language") + ", " + Spalte("SubdlId") + ", "
            + Spalte("Reason") + ", " + Spalte("NId") + ", " + Spalte("ReleaseName") + ", " + Spalte("Finding") + ", "
            + Spalte("Kind") + ", " + Spalte("ReportedUtc") + ", " + Spalte("Updated") + " FROM rejected_candidates;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            liste.Add(new CandidateRow
            {
                ItemId = Text(r, 0) ?? string.Empty,
                Language = Text(r, 1) ?? string.Empty,
                SubdlId = Text(r, 2) ?? string.Empty,
                Reason = Text(r, 3) ?? string.Empty,
                NId = Text(r, 4) ?? string.Empty,
                ReleaseName = Text(r, 5) ?? string.Empty,
                Finding = Text(r, 6) ?? string.Empty,
                Kind = Text(r, 7) ?? "qa",
                ReportedUtc = Moment(Text(r, 8)),
                Updated = Moment(Text(r, 9)) ?? DateTime.UtcNow,
            });
        }

        gelesen["rejected_candidates"] = liste.Count;
        return liste;
    }

    /// <summary>
    /// Die Marken der alten Matrix in Urteile und Belege auflösen. Das SUBJEKT ist die eigene Kennung:
    /// eine Spur über ihre Datei und Position, eine Datei über ihren Hash, ein Pack über
    /// (Serie, Staffel, Sprache, Variante, Quelle).
    /// </summary>
    private static (List<UploadRow> Uploads, List<ReferenceRow> References, int Unplaced) ResolveMatrix(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<MatrixRow> alt,
        List<ReferenceRow> vorhanden,
        Dictionary<string, int> gelesen)
    {
        var uploads = new Dictionary<string, UploadRow>(StringComparer.Ordinal);
        var referenzen = vorhanden.ToDictionary(r => r.Key, r => r, StringComparer.Ordinal);

        var dateien = new Dictionary<string, (string FileId, string? ImdbId, int? Season, string? ItemId, string? SdId)>(StringComparer.OrdinalIgnoreCase);
        var spuren = new List<(string FileId, int SubPos, string TrackId, string Language, bool Hi)>();
        var wagen = new List<(string FileId, string TrackId, string Language, bool Hi)>();
        var packs = new List<(string PackId, string ImdbId, int Season, string Language, bool Hi, string Source)>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT FileId, OsHash, ImdbId, Season, JellyfinItemId, SdId FROM file;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? hash = Text(r, 1);
                if (!string.IsNullOrEmpty(hash) && !dateien.ContainsKey(hash!))
                {
                    dateien[hash!] = (Text(r, 0) ?? string.Empty, Text(r, 2), Int(r, 3), Text(r, 4), Text(r, 5));
                }
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // F-M676: die Datei steht IN der Spur-Id; eine Spalte `FileId` gibt es nicht mehr.
            cmd.CommandText = "SELECT TrackId, SubPos, Language, HearingImpaired FROM track;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string id = Text(r, 0) ?? string.Empty;
                spuren.Add((ScribeIds.FileOf(id), Int(r, 1) ?? 0, id,
                    SourceStore.Normalise(Text(r, 2)), Bool(r, 3) ?? false));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT TrackId, Language, HearingImpaired FROM sidecar;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string id = Text(r, 0) ?? string.Empty;
                wagen.Add((ScribeIds.FileOf(id), id, SourceStore.Normalise(Text(r, 1)), Bool(r, 2) ?? false));
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT PackId, ImdbId, Season, Language, HearingImpaired, Source FROM pack;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                packs.Add((Text(r, 0) ?? string.Empty, Text(r, 1) ?? string.Empty, Int(r, 2) ?? 0,
                    SourceStore.Normalise(Text(r, 3)), Bool(r, 4) ?? false, Text(r, 5) ?? SourceStore.Subdl));
            }
        }

        int ohneSubjekt = 0;
        int verschmolzen = 0;

        foreach (var row in alt)
        {
            var subjekte = new List<string>();
            if (string.Equals(row.Kind, MarkKind.Pack, StringComparison.Ordinal))
            {
                var treffer = packs.FirstOrDefault(p =>
                    string.Equals(p.ImdbId, row.ImdbId, StringComparison.Ordinal)
                    && p.Season == (row.Season ?? 0)
                    && string.Equals(p.Language, row.Language, StringComparison.Ordinal)
                    && p.Hi == (row.HearingImpaired ?? false)
                    && string.Equals(p.Source, row.Source, StringComparison.Ordinal));
                if (!string.IsNullOrEmpty(treffer.PackId))
                {
                    subjekte.Add(treffer.PackId);
                }
            }
            else
            {
                if (!dateien.TryGetValue(row.MediaHash, out var datei))
                {
                    ohneSubjekt++;
                    continue;
                }

                if (string.Equals(row.Kind, MarkKind.File, StringComparison.Ordinal))
                {
                    subjekte.Add(datei.FileId);
                }
                else if (row.SubPos == SourceStore.SidecarPosition)
                {
                    // F-M671: ein Seitenwagen trägt die feste Position −1. Mehrere Seitenwagen derselben
                    // Sprache sind für die Quelle EIN Fall — die Marke gilt für sie alle, und deshalb
                    // bekommt jeder von ihnen den Beleg.
                    subjekte.AddRange(wagen
                        .Where(s => string.Equals(s.FileId, datei.FileId, StringComparison.Ordinal)
                                    && string.Equals(s.Language, row.Language, StringComparison.Ordinal)
                                    && s.Hi == (row.HearingImpaired ?? false))
                        .Select(s => s.TrackId));
                }
                else if (row.SubPos.HasValue)
                {
                    // Eine Spur ist über (Datei, Position) eindeutig — ihre eigene Adresse.
                    var spur = spuren.FirstOrDefault(t =>
                        string.Equals(t.FileId, datei.FileId, StringComparison.Ordinal) && t.SubPos == row.SubPos!.Value);
                    if (!string.IsNullOrEmpty(spur.TrackId))
                    {
                        subjekte.Add(spur.TrackId);
                    }
                }
                else
                {
                    // Eine paarweite Marke aus Schema 19/20 (ohne Position) gilt für JEDE Spur des
                    // Paares — sonst wären die Urteile einer solchen Datei nach dem Aufstieg unsichtbar.
                    subjekte.AddRange(spuren
                        .Where(t => string.Equals(t.FileId, datei.FileId, StringComparison.Ordinal)
                                    && string.Equals(t.Language, row.Language, StringComparison.Ordinal)
                                    && t.Hi == (row.HearingImpaired ?? false))
                        .Select(t => t.TrackId));
                    subjekte.AddRange(wagen
                        .Where(s => string.Equals(s.FileId, datei.FileId, StringComparison.Ordinal)
                                    && string.Equals(s.Language, row.Language, StringComparison.Ordinal)
                                    && s.Hi == (row.HearingImpaired ?? false))
                        .Select(s => s.TrackId));
                }
            }

            if (subjekte.Count == 0)
            {
                ohneSubjekt++;
                continue;
            }

            bool hatUrteil = row.ClosedAt != null || row.RefusedAt != null || row.RefusalReason != null
                             || row.RefusalSignature != null || row.Fingerprint != null || row.MtimeUnix != null;
            bool hatKennung = !string.IsNullOrEmpty(row.SourceId) || !string.IsNullOrEmpty(row.UploadId);

            foreach (string subject in subjekte.Distinct(StringComparer.Ordinal))
            {
                if (hatUrteil)
                {
                    string key = subject + "|" + row.Source;
                    if (!uploads.TryGetValue(key, out var upload))
                    {
                        upload = new UploadRow { SubjectId = subject, Source = row.Source };
                        uploads[key] = upload;
                    }

                    // Ein späterer Wert derselben Zelle überschreibt keinen gesetzten: die Zeilen sind
                    // in der Reihenfolge gelesen, in der sie geschrieben wurden.
                    upload.UploadedAt ??= row.ClosedAt;
                    upload.RefusedAt ??= row.RefusedAt;
                    upload.RefusedReason ??= row.RefusalReason;
                    upload.RefusalSignature ??= row.RefusalSignature;
                    upload.Fingerprint ??= row.Fingerprint;
                    upload.MtimeUnix ??= row.MtimeUnix;
                }

                if (hatKennung)
                {
                    string key = subject + "|" + row.Source;
                    if (!referenzen.TryGetValue(key, out var reference))
                    {
                        reference = new ReferenceRow { SubjectId = subject, Source = row.Source };
                        referenzen[key] = reference;
                    }

                    // Zwei Marken mit VERSCHIEDENEN Kennungen für dasselbe Subjekt und dieselbe Quelle gibt
                    // es in der alten Matrix (eine Abruf-Id und eine Einsende-Id derselben Einsendung sind
                    // zwei Nummernkreise, F-M435) — sie gehen in die zwei Zellen des einen Belegs. Passt
                    // eine dritte Kennung nicht mehr hinein, wird sie GEZÄHLT, nicht still verworfen.
                    if (!string.IsNullOrEmpty(row.SourceId))
                    {
                        if (reference.DownloadSourceId == null)
                        {
                            reference.DownloadSourceId = row.SourceId;
                        }
                        else if (!string.Equals(reference.DownloadSourceId, row.SourceId, StringComparison.Ordinal)
                                 && string.IsNullOrEmpty(reference.UploadedId))
                        {
                            reference.UploadedId = row.SourceId;
                        }
                        else if (!string.Equals(reference.DownloadSourceId, row.SourceId, StringComparison.Ordinal)
                                 && !string.Equals(reference.UploadedId, row.SourceId, StringComparison.Ordinal))
                        {
                            verschmolzen++;
                        }
                    }

                    if (!string.IsNullOrEmpty(row.UploadId))
                    {
                        if (reference.UploadedId == null)
                        {
                            reference.UploadedId = row.UploadId;
                        }
                        else if (!string.Equals(reference.UploadedId, row.UploadId, StringComparison.Ordinal)
                                 && string.IsNullOrEmpty(reference.DownloadSourceId))
                        {
                            reference.DownloadSourceId = row.UploadId;
                        }
                        else if (!string.Equals(reference.UploadedId, row.UploadId, StringComparison.Ordinal)
                                 && !string.Equals(reference.DownloadSourceId, row.UploadId, StringComparison.Ordinal))
                        {
                            verschmolzen++;
                        }
                    }
                }
            }
        }

        gelesen["source_matrix ohne Subjekt"] = ohneSubjekt;
        gelesen["source_matrix verschmolzen"] = verschmolzen;
        return (uploads.Values.ToList(), referenzen.Values.ToList(), ohneSubjekt);
    }

    /// <summary>
    /// Die verworfenen Kandidaten der Download-Seite in schlechte Downloads auflösen. Das Subjekt ist die
    /// Datei des Jellyfin-Items; fehlt sie, wird eine Zeile ohne Pfad angelegt — dieselbe Regel wie bei
    /// einer Spur ohne Datei („keine Marke ohne Subjekt").
    /// </summary>
    private static List<BadAppleRow> ResolveBadApples(
        SqliteConnection conn,
        SqliteTransaction tx,
        List<CandidateRow> alt,
        Dictionary<string, int> gelesen,
        ref int ohneSubjekt)
    {
        var liste = new Dictionary<string, BadAppleRow>(StringComparer.Ordinal);
        if (alt.Count == 0)
        {
            return new List<BadAppleRow>();
        }

        var dateiVonItem = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT FileId, JellyfinItemId FROM file WHERE JellyfinItemId IS NOT NULL AND JellyfinItemId <> '';";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? item = Text(r, 1);
                if (!string.IsNullOrEmpty(item) && !dateiVonItem.ContainsKey(item!))
                {
                    dateiVonItem[item!] = Text(r, 0) ?? string.Empty;
                }
            }
        }

        var neu = new List<(string FileId, string ItemId)>();
        foreach (var row in alt)
        {
            if (string.IsNullOrWhiteSpace(row.ItemId) || string.IsNullOrWhiteSpace(row.SubdlId))
            {
                ohneSubjekt++;
                continue;
            }

            if (!dateiVonItem.TryGetValue(row.ItemId, out string? subject) || string.IsNullOrEmpty(subject))
            {
                // Die Datei fehlt (noch) — die Zeile bekommt eine eigene, damit die Meldung eine Adresse
                // hat. Sie ist eine Datei ohne Pfad und ohne Hash: auffindbar über ihre Item-Id.
                string fileId = ScribeIds.FileIdOf(ScribeIds.NewFileId(
                    id => neu.Any(n => n.FileId == ScribeIds.FileIdOf(id))
                          || dateiVonItem.ContainsValue(ScribeIds.FileIdOf(id))));
                neu.Add((fileId, row.ItemId));
                dateiVonItem[row.ItemId] = fileId;
                subject = fileId;
            }

            var ziel = new BadAppleRow
            {
                SubjectId = subject!,
                Source = SourceStore.Subdl,
                SourceId = row.SubdlId,
                Language = row.Language.ToUpperInvariant(),
                Reason = row.Reason,
                Finding = row.Finding,
                NId = row.NId,
                ReleaseName = row.ReleaseName,
                Kind = string.IsNullOrEmpty(row.Kind) ? "qa" : row.Kind,
                ReportedAt = row.ReportedUtc,
            };

            liste[ziel.Key] = ziel;
        }

        // Die Dateien für Items ohne Zeile zuerst, damit die Marke ein Subjekt hat.
        foreach (var (fileId, itemId) in neu)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO file (FileId, JellyfinItemId, IsSeries, Created, LastSeen) "
                + "VALUES ($id, $item, 0, $now, $now);";
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.Parameters.AddWithValue("$item", itemId);
            cmd.Parameters.AddWithValue("$now", Iso(DateTime.UtcNow));
            cmd.ExecuteNonQuery();
        }

        if (neu.Count > 0)
        {
            gelesen["rejected_candidates ohne Datei"] = neu.Count;
        }

        return liste.Values.ToList();
    }

    /// <summary>
    /// F-M677: die MITGLIEDSCHAFT der Behälter aus dem Feld <c>track.PackId</c> lesen, das die Datei bis
    /// Schema 22 trug. Eine Spur gehört zu dem Behälter, der ihre SPRACHE und Variante pflegt — nicht zu
    /// dem ihrer Datei.
    /// </summary>
    /// <param name="conn">Open connection on the old file.</param>
    /// <param name="tx">The migration transaction.</param>
    /// <returns>Pack id and (new) track id of every member.</returns>
    private static List<(string PackId, string TrackId)> ReadMembership22(SqliteConnection conn, SqliteTransaction tx)
    {
        var liste = new List<(string, string)>();
        if (!TableExists(conn, "track") || !ColumnExists(conn, "track", "PackId"))
        {
            return liste;
        }

        var dateien = new Dictionary<string, (string? ImdbId, int? Season, int? Episode)>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT FileId, ImdbId, Season, Episode FROM file;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // F-M676: die alte Spalte trägt das nackte Segment — der Schlüssel ist die DATEI-Id.
                dateien[ScribeIds.FileIdOf(Text(r, 0))] = (Text(r, 1), Int(r, 2), Int(r, 3));
            }
        }

        var packs = new List<PackEntity>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT PackId, ImdbId, Season, Language, HearingImpaired, Episodes, Source FROM pack;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                packs.Add(new PackEntity
                {
                    PackId = Text(r, 0) ?? string.Empty,
                    ImdbId = Text(r, 1) ?? string.Empty,
                    Season = Int(r, 2) ?? 0,
                    Language = Text(r, 3) ?? string.Empty,
                    HearingImpaired = Bool(r, 4),
                    Episodes = Text(r, 5) ?? string.Empty,
                    Source = Text(r, 6) ?? SourceStore.Subdl,
                });
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT TrackId, FileId, Language, HearingImpaired FROM track;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string alt = Text(r, 0) ?? string.Empty;
                string dateiAlt = Text(r, 1) ?? string.Empty;
                string datei = ScribeIds.FileIdOf(dateiAlt);
                dateien.TryGetValue(datei, out var zeile);
                var dateiZeile = new FileEntity
                {
                    FileId = datei,
                    ImdbId = zeile.ImdbId,
                    Season = zeile.Season,
                    Episode = zeile.Episode,
                };

                string? packId = SourceStore.PackForTrack(
                    dateiZeile, SourceStore.Normalise(Text(r, 2)), Bool(r, 3) ?? false, packs);
                if (!string.IsNullOrEmpty(packId))
                {
                    // Die Pack-Id aus der Zeile ist bei 22 schon die volle Form (`[xxxx]`), bei einem
                    // Handbestand ein nacktes Segment — beides wird zur selben Id.
                    string packSubject = ScribeIds.IsPackId(packId)
                        ? packId!
                        : ScribeIds.PackIdOf(packId);
                    liste.Add((packSubject, ScribeIds.TrackIdOf(datei, alt)));
                }
            }
        }

        return liste;
    }

    /// <summary>
    /// F-M676/F-M677: die vier Entity-Tabellen einer Datei aus Schema 22 in die neue Form bringen.
    /// <para>
    /// WARUM NEU BAUEN UND NICHT BESCHNEIDEN: <c>track</c> und <c>sidecar</c> trugen ihre Datei in einer
    /// eigenen Spalte, und die war Teil des Primärschlüssels — SQLite verweigert das Entfernen einer
    /// Schlüsselspalte („cannot drop PRIMARY KEY column"). Also: alte Tabelle umbenennen, neue in der
    /// Zielform anlegen, jede Zeile mit ihrer LESBAREN Kennung hinüber schreiben, alte fallen lassen,
    /// Indizes neu. Die SEGMENTE bleiben dabei unverändert — eine Kennung, die nur eine Marke bekommt,
    /// ist dieselbe Kennung.
    /// </para>
    /// </summary>
    /// <returns>Die Belege, die aus der Spalte <c>OriginId</c> entstehen (F-M672).</returns>
    private static List<ReferenceRow> RebuildEntityTables(
        SqliteConnection conn, SqliteTransaction tx, Dictionary<string, int> gelesen, ILogger? logger)
    {
        var belege = new List<ReferenceRow>();
        int ohneQuelle = 0;

        // ---------------------------------------------------------------- track
        if (TableExists(conn, "track") && ColumnExists(conn, "track", "FileId"))
        {
            var alt = ReadTracks(conn, "track", tx, out var dateiSpalte);
            Execute(conn, tx, "ALTER TABLE track RENAME TO track_alt;");
            Execute(conn, tx, ScribeDbContext.TrackTableDdl);
            foreach (var t in alt)
            {
                string segment = ScribeIds.TrackPartOf(t.TrackId);
                if (string.IsNullOrEmpty(segment))
                {
                    segment = t.TrackId;
                }

                // F-M676: die alte Spalte trägt das nackte Segment ("yyyyy"), die Id braucht die Marke.
                t.TrackId = ScribeIds.TrackIdOf(ScribeIds.FileIdOf(t.DateiSpalte), segment);
                InsertTrack(conn, tx, t);
                belege.AddRange(OriginBeleg(t.Origin, t.OriginId, t.TrackId, ref ohneQuelle));
            }

            Execute(conn, tx, "DROP TABLE track_alt;");
            gelesen["track neu gebaut"] = alt.Count;
        }

        // ---------------------------------------------------------------- sidecar
        if (TableExists(conn, "sidecar") && ColumnExists(conn, "sidecar", "FileId"))
        {
            var alt = ReadSidecars(conn, tx, out _);
            Execute(conn, tx, "ALTER TABLE sidecar RENAME TO sidecar_alt;");
            Execute(conn, tx, ScribeDbContext.SidecarTableDdl);
            foreach (var s in alt)
            {
                string segment = ScribeIds.TrackPartOf(s.TrackId);
                if (string.IsNullOrEmpty(segment))
                {
                    segment = s.TrackId;
                }

                s.TrackId = ScribeIds.SidecarIdOf(ScribeIds.FileIdOf(s.DateiSpalte), segment);
                InsertSidecar(conn, tx, s);
                belege.AddRange(OriginBeleg(s.Origin, s.OriginId, s.TrackId, ref ohneQuelle));
            }

            Execute(conn, tx, "DROP TABLE sidecar_alt;");
            gelesen["sidecar neu gebaut"] = alt.Count;
        }

        // ---------------------------------------------------------------- file und pack: nur die Id
        foreach (var (tabelle, spalte) in new[] { ("file", "FileId"), ("pack", "PackId") })
        {
            if (!TableExists(conn, tabelle))
            {
                continue;
            }

            var paare = new List<(string Alt, string Neu)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT " + spalte + " FROM " + tabelle + ";";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string wert = Text(r, 0) ?? string.Empty;
                    paare.Add((wert, tabelle == "file" ? ScribeIds.FileIdOf(wert) : ScribeIds.PackIdOf(wert)));
                }
            }

            foreach (var (alt, neu) in paare)
            {
                if (alt != neu)
                {
                    Execute(conn, tx, "UPDATE " + tabelle + " SET " + spalte + " = '" + neu + "' WHERE " + spalte + " = '" + alt + "';");
                }
            }

            gelesen[tabelle + " Id neu"] = paare.Count;
        }

        if (ohneQuelle > 0)
        {
            gelesen["OriginId ohne Quelle"] = ohneQuelle;
        }

        logger?.LogInformation(
            "[Sub Scribe] data file: {Tracks} track row(s) and {Sidecars} sidecar row(s) rebuilt in the readable id form (F-M676).",
            gelesen.TryGetValue("track neu gebaut", out int tr) ? tr : 0,
            gelesen.TryGetValue("sidecar neu gebaut", out int sc) ? sc : 0);

        // Die Indizes der neuen Tabellen (die alten sind mit ihrer Tabelle gefallen).
        foreach (string index in ScribeDbContext.IdModelIndexDdl)
        {
            if (index.Contains("ON track_alt", StringComparison.Ordinal)
                || index.Contains("ON sidecar_alt", StringComparison.Ordinal))
            {
                continue;
            }

            Execute(conn, tx, index);
        }

        return belege;
    }

    /// <summary>F-M672: eine <c>OriginId</c> als Beleg — oder als „ohne Quelle" gezählt.</summary>
    private static IEnumerable<ReferenceRow> OriginBeleg(
        string? origin, string? originId, string subject, ref int ohneQuelle)
    {
        if (string.IsNullOrWhiteSpace(originId))
        {
            return Array.Empty<ReferenceRow>();
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            ohneQuelle++;
            return Array.Empty<ReferenceRow>();
        }

        return new[]
        {
            new ReferenceRow
            {
                SubjectId = subject,
                Source = QuelleName(origin!),
                DownloadSourceId = originId,
            },
        };
    }


    // ---------------------------------------------------------------- neue Zeilen schreiben

    private static void InsertPack(
        SqliteConnection conn, SqliteTransaction tx, PackRow p, (string? Fingerprint, long? MtimeUnix) sig)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO pack (PackId, SeasonKey, Source, ImdbId, TmdbId, Season, Language, HearingImpaired, "
            + "ReleaseName, Signature, MtimeUnix, EpisodeCount, Episodes, AbsorbedIds, Created, Updated) "
            + "VALUES ($pack, $key, $source, $imdb, NULL, $season, $lang, $hi, $release, $sig, $mtime, $count, "
            + "$eps, $absorbed, $created, $updated);";
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
            "INSERT INTO file (FileId, OsHash, FileName, Size, MtimeUnix, Path, JellyfinItemId, ImdbId, TmdbId, "
            + "TvstShowId, MsubMovieId, SdId, IsSeries, Season, Episode, LanguagesAvailable, SubtitlesUploadedAt, "
            + "LastSearchUtc, LastSearchLanguages, LastSearchEmptyUtc, Created, LastSeen) "
            + "VALUES ($file, $hash, $name, NULL, NULL, $path, $item, $imdb, $tmdb, $tvst, $msub, $sd, "
            + "$series, $season, $episode, $langs, $uploaded, $search, $searchLang, $searchEmpty, $created, $seen);";
        cmd.Parameters.AddWithValue("$file", f.FileId);
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
            "INSERT INTO track (TrackId, TrackNumber, MediaHash, SubPos, Language, HearingImpaired, Forced, "
            + "ContentHash, Status, Reason, StatusAt, Origin, DetectionAttemptedAt, DetectionOutcome, Created, LastSeen) "
            + "VALUES ($track, $number, $hash, $pos, $lang, $hi, $forced, $content, $status, $reason, $statusAt, "
            + "$origin, $detectAt, $detectOutcome, $created, $seen);";
        cmd.Parameters.AddWithValue("$track", t.TrackId);
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
            // F-M671: kein PackId — ein Seitenwagen ist `0000-yyyyy-&zzz`.
            "INSERT INTO sidecar (TrackId, TrackNumber, MediaHash, ContentHash, FileName, Path, "
            + "Language, HearingImpaired, Forced, Status, Reason, StatusAt, Origin, ImdbId, TmdbId, IsSeries, Season, "
            + "Episode, Created, LastSeen) "
            + "VALUES ($track, $number, $hash, $content, $name, $path, $lang, $hi, $forced, $status, "
            + "$reason, $statusAt, $origin, $imdb, $tmdb, $series, $season, $episode, $created, $seen);";
        cmd.Parameters.AddWithValue("$track", s.TrackId);
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
        cmd.Parameters.AddWithValue("$imdb", (object?)s.ImdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tmdb", (object?)s.TmdbId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$series", s.IsSeries ? 1 : 0);
        cmd.Parameters.AddWithValue("$season", (object?)s.Season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$episode", (object?)s.Episode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Iso(s.Created));
        cmd.Parameters.AddWithValue("$seen", Iso(s.LastSeen));
        cmd.ExecuteNonQuery();
    }

    private static void InsertPackTrack(SqliteConnection conn, SqliteTransaction tx, PackTrackRow m)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO pack_track (PackId, TrackId, Added) VALUES ($pack, $track, $added);";
        cmd.Parameters.AddWithValue("$pack", m.PackId);
        cmd.Parameters.AddWithValue("$track", m.TrackId);
        cmd.Parameters.AddWithValue("$added", Iso(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    private static void InsertUpload(SqliteConnection conn, SqliteTransaction tx, UploadRow u)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO upload (SubjectId, Source, UploadedAt, RefusedAt, RefusedReason, RefusalSignature, "
            + "Fingerprint, MtimeUnix, Written) VALUES ($subject, $source, $uploaded, $refusedAt, $reason, $signature, "
            + "$fingerprint, $mtime, $written);";
        cmd.Parameters.AddWithValue("$subject", u.SubjectId);
        cmd.Parameters.AddWithValue("$source", u.Source);
        cmd.Parameters.AddWithValue("$uploaded", NullableIso(u.UploadedAt));
        cmd.Parameters.AddWithValue("$refusedAt", NullableIso(u.RefusedAt));
        cmd.Parameters.AddWithValue("$reason", (object?)u.RefusedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$signature", (object?)u.RefusalSignature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fingerprint", (object?)u.Fingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mtime", u.MtimeUnix.HasValue ? u.MtimeUnix.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$written", Iso(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    private static void InsertReference(SqliteConnection conn, SqliteTransaction tx, ReferenceRow r)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO reference (SubjectId, Source, DownloadSourceId, UploadedId, ItemId, SdId, Url, Written) "
            + "VALUES ($subject, $source, $download, $uploaded, $item, $sd, NULL, $written);";
        cmd.Parameters.AddWithValue("$subject", r.SubjectId);
        cmd.Parameters.AddWithValue("$source", r.Source);
        cmd.Parameters.AddWithValue("$download", (object?)r.DownloadSourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uploaded", (object?)r.UploadedId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$item", (object?)r.ItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sd", (object?)r.SdId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$written", Iso(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    private static void InsertBadApple(SqliteConnection conn, SqliteTransaction tx, BadAppleRow b)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO badapple (SubjectId, Source, SourceId, Language, Reason, Finding, NId, ReleaseName, Kind, "
            + "ReportedAt, ReportState, Written) VALUES ($subject, $source, $sourceId, $lang, $reason, $finding, $nid, "
            + "$release, $kind, $reported, $state, $written);";
        cmd.Parameters.AddWithValue("$subject", b.SubjectId);
        cmd.Parameters.AddWithValue("$source", b.Source);
        cmd.Parameters.AddWithValue("$sourceId", b.SourceId);
        cmd.Parameters.AddWithValue("$lang", b.Language);
        cmd.Parameters.AddWithValue("$reason", b.Reason);
        cmd.Parameters.AddWithValue("$finding", b.Finding);
        cmd.Parameters.AddWithValue("$nid", b.NId);
        cmd.Parameters.AddWithValue("$release", b.ReleaseName);
        cmd.Parameters.AddWithValue("$kind", b.Kind);
        cmd.Parameters.AddWithValue("$reported", NullableIso(b.ReportedAt));
        cmd.Parameters.AddWithValue("$state", b.ReportedAt.HasValue
            ? BadAppleEntity.ReportStateReported
            : BadAppleEntity.ReportStateOpen);
        cmd.Parameters.AddWithValue("$written", Iso(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- Werkzeug

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // Der Satz gehört in die Meldung: „SQLite Error 1" allein sagt niemandem, WELCHE Anweisung
            // nicht durchging, und ein Migrationslauf, den man nicht diagnostizieren kann, wird geraten.
            throw new InvalidOperationException("statement failed: " + sql, ex);
        }
    }

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

    // Die Zeitstempel gehen in der Form in die Datei, in der der Store sie fuehrt (dieselbe, die auch
    // Microsoft.Data.Sqlite fuer ein DateTime schreibt). KEIN ToUniversalTime: ein aus dem Altbestand
    // gelesener Wert ist bereits UTC, und ein zweites Verschieben um den Zonenversatz waere ein Fehler,
    // der wie eine richtige Zeit aussieht (gemessen: eine Stunde daneben auf einem CET-Host).
    private static object NullableIso(DateTime? moment)
        => moment.HasValue
            ? moment.Value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
            : DBNull.Value;

    private static string Iso(DateTime? moment)
        => moment.HasValue
            ? moment.Value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
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
