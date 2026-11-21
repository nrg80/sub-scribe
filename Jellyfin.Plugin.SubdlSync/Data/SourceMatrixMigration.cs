// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M667: die einmalige Übernahme des Bestands in die Marken-Matrix.
/// <para>
/// WAS HIER PASSIERT, UND WARUM VOR DEM ÖFFNEN DES STORES: die Marken standen bis Schema 17 als breite
/// Spalten an den Zeilen (<c>SubdlUploadedAt</c>/<c>TvSubtitlesUploadedAt</c>/<c>MoviesubtitlesUploadedAt</c>
/// an <c>embeds</c> und <c>sidecars</c>, <c>CompleteSubdl</c>/<c>CompleteTvSubtitles</c>/<c>CompleteMsub</c>
/// an <c>media</c>, die Ablehnung an <c>packs</c>). Diese Klasse liest sie EINMAL, sichert vorher die
/// ganze Datei unter einem Zeitstempel-Namen und legt je (Medium, Quelle, Art) eine Zeile in
/// <c>source_matrix</c> an. Danach sind die alten Spalten unangetastet und bleiben lesbar — die
/// Rückfallebene, bis Schritt B (12.3.0.0) die Schreiber umstellt (F-M327).
/// </para>
/// <para>
/// WANN SIE LÄUFT: wenn die Datei existiert, ein Store ist (<c>meta</c> vorhanden) und die Tabelle
/// <c>source_matrix</c> FEHLT. Das ist die ehrliche Bedingung: ein Lauf, der mittendrin abbricht, hat
/// entweder die Tabelle noch nicht angelegt (dann läuft er beim nächsten Start erneut) oder sie ist da
/// und gefüllt (dann ist nichts zu tun). Der Schema-Marker ist NICHT die Bedingung — er wird beim
/// Öffnen auf 18 gehoben, und eine Übernahme, die an ihm hinge, wäre nach einem Abbruch für immer
/// verloren.
/// </para>
/// <para>
/// WARUM SQL UND NICHT DAS MODELL: der Lauf arbeitet auf einer Datei, die ein ÄLTERES Schema hat — ihre
/// Tabellen können Spalten NICHT haben, die das heutige Modell verlangt, und dann würde jedes
/// <c>SELECT</c> über das Modell scheitern. Jede Spalte wird deshalb erst gefragt
/// (<c>PRAGMA table_info</c>) und nur gelesen, wenn sie da ist; alles andere ist <c>NULL</c>.
/// </para>
/// </summary>
public static class SourceMatrixMigration
{
    /// <summary>Was der Übernahmelauf getan hat.</summary>
    /// <param name="Ran">True when the matrix was filled by this call.</param>
    /// <param name="BackupPath">Path of the timestamped copy taken before the fill, else null.</param>
    /// <param name="Read">"source|kind" → number of OLD rows read for that kind.</param>
    /// <param name="Written">"source|kind" → number of matrix rows written for that kind.</param>
    /// <param name="Merged">How many old rows collapsed into an existing key (coarser grain).</param>
    public sealed record Outcome(
        bool Ran,
        string? BackupPath,
        IReadOnlyDictionary<string, int> Read,
        IReadOnlyDictionary<string, int> Written,
        int Merged);

    /// <summary>
    /// Führt die Übernahme aus. Läuft VOR dem Öffnen des Stores und ist der einzige Pfad, der das alte
    /// Format SCHREIBEND übernimmt (F-M327).
    /// </summary>
    /// <param name="dbPath">Path of the live data file.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="backup">
    /// True to copy the file aside first. False only for the store a format switch just built: its
    /// PREVIOUS file is never deleted (F-M638) and is therefore the fallback by itself, so a second
    /// copy would only litter the data directory.
    /// </param>
    /// <returns>What was done — with the real counts per kind.</returns>
    public static Outcome Run(string dbPath, ILogger? logger, bool backup = true)
    {
        var leer = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            return new Outcome(false, null, leer, leer, 0);
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

            // Kein Store (keine meta-Tabelle): nichts zu übernehmen. Eine leere Datei, die eben erst
            // entstanden ist, fällt hier heraus, und der Aufbau darunter legt alles frisch an.
            if (!TableExists(conn, "meta"))
            {
                return new Outcome(false, null, leer, leer, 0);
            }

            // Schon übernommen? Dann ist nichts zu tun — der zweite Start darf weder Zeilen noch eine
            // Sicherung erzeugen. Die Bedingung ist „Tabelle fehlt ODER sie ist noch leer": der
            // Formatwechsel (F-M637) baut seine neue Datei über denselben Kontext und legt die Tabelle
            // dabei an; leer heißt dort „noch nicht übernommen", nicht „nichts zu übernehmen".
            bool tabelleFehlt = !TableExists(conn, "source_matrix");
            if (!tabelleFehlt && CountMatrix(conn) > 0)
            {
                return new Outcome(false, null, leer, leer, 0);
            }

            // Die alten Spalten, die es überhaupt gibt — daraus entstehen die Zeilen.
            var zeilen = new Dictionary<string, SourceMatrixEntity>(StringComparer.Ordinal);
            var gelesen = new Dictionary<string, int>(StringComparer.Ordinal);
            int alteZeilen = 0;

            alteZeilen += LeseSpuren(conn, "embeds", false, zeilen, gelesen);
            alteZeilen += LeseSpuren(conn, "sidecars", true, zeilen, gelesen);
            alteZeilen += LeseDateien(conn, zeilen, gelesen);
            alteZeilen += LesePacks(conn, zeilen, gelesen);

            // SICHERUNG ZUERST — vor der ersten Änderung an der Datei. Ein WAL-Rest wird vorher
            // eingefaltet, sonst fehlt der Kopie alles seit dem letzten Checkpoint (F-M326). Eine
            // vorhandene, nur noch leere Tabelle braucht keine Sicherung: die breiten Spalten, aus
            // denen gefüllt wird, sind unangetastet, und an ihnen ändert dieser Lauf nichts.
            Checkpoint(conn);
            string? sicherung = null;
            if (backup && tabelleFehlt)
            {
                sicherung = BackupPathFor(dbPath);
                File.Copy(dbPath, sicherung, overwrite: true);
            }

            // EINE Transaktion um Anlegen UND Füllen: bricht der Lauf ab, verschwindet auch die neue
            // Tabelle wieder — und der nächste Start holt ihn nach. Bliebe die leere Tabelle stehen,
            // gälte die Übernahme als erledigt, obwohl keine einzige Marke darin steht.
            using var tx = conn.BeginTransaction();

            // Tabelle und Indizes anlegen. Dieselbe DDL, die der Store selbst benutzt — eine Tabelle,
            // die hier anders aussieht als dort, wäre ein Speicher, den niemand liest.
            using (var ddl = conn.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = ScribeDbContext.SourceMatrixDdl;
                ddl.ExecuteNonQuery();
            }

            foreach (string index in ScribeDbContext.SourceMatrixIndexDdl)
            {
                using var ix = conn.CreateCommand();
                ix.Transaction = tx;
                ix.CommandText = index;
                ix.ExecuteNonQuery();
            }

            int marken = zeilen.Count;
            int geschrieben = Schreibe(conn, tx, zeilen.Values);

            var nachher = new Dictionary<string, int>(StringComparer.Ordinal);
            using (var zaehl = conn.CreateCommand())
            {
                zaehl.Transaction = tx;
                zaehl.CommandText = "SELECT Source, Kind, COUNT(*) FROM source_matrix GROUP BY Source, Kind;";
                using var r = zaehl.ExecuteReader();
                while (r.Read())
                {
                    nachher[r.GetString(0) + "|" + r.GetString(1)] = r.GetInt32(2);
                }
            }

            tx.Commit();

            int zusammengelegt = alteZeilen - marken;
            logger?.LogInformation(
                "[Sub Scribe] source matrix filled from the wide columns (F-M667): {Rows} mark(s) from {Old} old row(s), "
                + "{Merged} merged, backup {Backup}.",
                geschrieben,
                alteZeilen,
                zusammengelegt,
                sicherung == null ? "none" : Path.GetFileName(sicherung));

            foreach (string quelle in SourceMatrix.Sources)
            {
                foreach (string art in MarkKind.All)
                {
                    string k = quelle + "|" + art;
                    if (!gelesen.ContainsKey(k) && !nachher.ContainsKey(k))
                    {
                        continue;
                    }

                    logger?.LogInformation(
                        "[Sub Scribe] source matrix: {Source} {Kind} — {Read} old row(s) → {Written} mark(s).",
                        quelle,
                        art,
                        gelesen.TryGetValue(k, out int g) ? g : 0,
                        nachher.TryGetValue(k, out int n) ? n : 0);
                }
            }

            return new Outcome(true, sicherung, gelesen, nachher, zusammengelegt);
        }
        catch (Exception ex)
        {
            // Ein gescheiterter Übernahmelauf darf den Start nicht verhindern. Die Tabelle fehlt dann
            // weiterhin, also läuft der nächste Start ihn erneut — nichts gilt als erledigt, was nicht
            // erledigt wurde.
            logger?.LogWarning(
                "[Sub Scribe] could not fill the source matrix from the wide columns (continuing): {Msg}",
                ex.Message);
            return new Outcome(false, null, leer, leer, 0);
        }
    }

    /// <summary>Der Name einer Sicherung: der Store-Name plus Zeitstempel, wie ihn der Reset benutzt.</summary>
    /// <param name="dbPath">Path of the data file.</param>
    /// <returns>A free backup path.</returns>
    public static string BackupPathFor(string dbPath)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = dbPath + ".bak-" + stamp;
        int n = 1;
        while (File.Exists(path))
        {
            n++;
            path = dbPath + ".bak-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        return path;
    }

    // ---------------------------------------------------------------------------------------------
    // Die alten Spalten lesen. Je Tabelle und je Quelle nur, was wirklich da ist.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Liest eine Spur-Tabelle (<c>embeds</c> oder <c>sidecars</c>) in Marken-Zeilen.</summary>
    private static int LeseSpuren(
        SqliteConnection conn,
        string table,
        bool sidecar,
        Dictionary<string, SourceMatrixEntity> zeilen,
        Dictionary<string, int> gelesen)
    {
        if (!TableExists(conn, table))
        {
            return 0;
        }

        bool hatMedia = TableExists(conn, "media");
        string join = hatMedia ? " LEFT JOIN media m ON m.Id = e.MediaHash" : string.Empty;

        string Select(string column, string alias = "e") => ColumnExists(conn, table, column) ? alias + "." + column : "NULL";

        var spalten = new List<(string Name, string Expr)>
        {
            ("Id", Select("Id")),
            ("MediaHash", Select("MediaHash")),
            ("Language", Select("Language")),
            ("HearingImpaired", Select("HearingImpaired")),
            ("ImdbId", sidecar ? Select("ImdbId") : (hatMedia ? "m.ImdbId" : "NULL")),
            ("TmdbId", sidecar ? Select("TmdbId") : (hatMedia ? "m.TmdbId" : "NULL")),
            ("Season", sidecar ? Select("Season") : (hatMedia ? "m.Season" : "NULL")),
            ("Episode", sidecar ? Select("Episode") : (hatMedia ? "m.Episode" : "NULL")),
        };

        foreach (string quelle in SourceMatrix.Sources)
        {
            string prefix = PrefixOf(quelle);
            if (!ColumnExists(conn, table, prefix + "UploadedAt"))
            {
                continue;
            }

            spalten.Add((prefix + "DownloadId", Select(prefix + "DownloadId")));
            spalten.Add((prefix + "UploadedAt", Select(prefix + "UploadedAt")));
        }

        string sql = "SELECT " + string.Join(", ", spalten.Select(s => s.Expr)) + " FROM " + table + " e" + join + ";";

        int n = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var werte = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < spalten.Count; i++)
                {
                    werte[spalten[i].Name] = ReadValue(r, i);
                }

                string language = werte["Language"] as string ?? string.Empty;
                bool? hi = werte["HearingImpaired"] is long h ? h != 0 : werte["HearingImpaired"] is bool b ? b : null;
                string mediaHash = werte["MediaHash"] as string ?? string.Empty;

                foreach (string quelle in SourceMatrix.Sources)
                {
                    string prefix = PrefixOf(quelle);
                    if (!werte.ContainsKey(prefix + "UploadedAt"))
                    {
                        continue;
                    }

                    var zeit = werte[prefix + "UploadedAt"] as DateTime?;
                    if (zeit == null)
                    {
                        continue;
                    }

                    n++;
                    string art = MarkKind.ForTrack(hi == true);
                    gelesen[quelle + "|" + art] = gelesen.TryGetValue(quelle + "|" + art, out int g) ? g + 1 : 1;

                    Lege(zeilen, new SourceMatrixEntity
                    {
                        MediaHash = mediaHash,
                        ImdbId = werte["ImdbId"] as string ?? string.Empty,
                        TmdbId = werte["TmdbId"] as string ?? string.Empty,
                        Source = quelle,
                        Kind = art,
                        Language = SourceMatrix.Normalise(language),
                        Season = AsInt(werte["Season"]),
                        Episode = AsInt(werte["Episode"]),
                        HearingImpaired = hi,
                        ClosedAt = zeit,
                        SourceId = werte[prefix + "DownloadId"] as string,
                    }, SourceMatrix.AnchorOf(mediaHash, null));
                }
            }
        }

        return n;
    }

    /// <summary>Liest die Zugeh-Stempel der Medienzeilen als <c>file</c>-Marken (F-M557).</summary>
    private static int LeseDateien(
        SqliteConnection conn, Dictionary<string, SourceMatrixEntity> zeilen, Dictionary<string, int> gelesen)
    {
        if (!TableExists(conn, "media"))
        {
            return 0;
        }

        var spalten = new List<(string Name, string Expr)>
        {
            ("Id", "Id"),
            ("ImdbId", ColumnExists(conn, "media", "ImdbId") ? "ImdbId" : "NULL"),
            ("TmdbId", ColumnExists(conn, "media", "TmdbId") ? "TmdbId" : "NULL"),
            ("Season", ColumnExists(conn, "media", "Season") ? "Season" : "NULL"),
            ("Episode", ColumnExists(conn, "media", "Episode") ? "Episode" : "NULL"),
        };

        foreach (string quelle in SourceMatrix.Sources)
        {
            string spalte = quelle switch
            {
                SourceMatrix.Subdl => "CompleteSubdl",
                SourceMatrix.TvSubtitles => "CompleteTvSubtitles",
                _ => "CompleteMsub",
            };

            spalten.Add((spalte, ColumnExists(conn, "media", spalte) ? spalte : "NULL"));
        }

        string sql = "SELECT " + string.Join(", ", spalten.Select(s => s.Expr)) + " FROM media;";
        int n = 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var werte = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < spalten.Count; i++)
            {
                werte[spalten[i].Name] = ReadValue(r, i);
            }

            string mediaHash = werte["Id"] as string ?? string.Empty;

            foreach (string quelle in SourceMatrix.Sources)
            {
                string spalte = quelle switch
                {
                    SourceMatrix.Subdl => "CompleteSubdl",
                    SourceMatrix.TvSubtitles => "CompleteTvSubtitles",
                    _ => "CompleteMsub",
                };

                var zeit = werte[spalte] as DateTime?;
                if (zeit == null)
                {
                    continue;
                }

                n++;
                string k = quelle + "|" + MarkKind.File;
                gelesen[k] = gelesen.TryGetValue(k, out int g) ? g + 1 : 1;

                Lege(zeilen, new SourceMatrixEntity
                {
                    MediaHash = mediaHash,
                    ImdbId = werte["ImdbId"] as string ?? string.Empty,
                    TmdbId = werte["TmdbId"] as string ?? string.Empty,
                    Source = quelle,
                    Kind = MarkKind.File,
                    Language = string.Empty,
                    Season = AsInt(werte["Season"]),
                    Episode = AsInt(werte["Episode"]),
                    ClosedAt = zeit,
                }, SourceMatrix.AnchorOf(mediaHash, null));
            }
        }

        return n;
    }

    /// <summary>Liest die Pack-Zeilen als <c>pack</c>-Marken — Beleg, Fingerabdruck und Ablehnung.</summary>
    private static int LesePacks(
        SqliteConnection conn, Dictionary<string, SourceMatrixEntity> zeilen, Dictionary<string, int> gelesen)
    {
        if (!TableExists(conn, "packs"))
        {
            return 0;
        }

        string S(string column) => ColumnExists(conn, "packs", column) ? column : "NULL";
        var spalten = new List<(string Name, string Expr)>
        {
            ("ImdbId", S("ImdbId")),
            ("Season", S("Season")),
            ("Language", S("Language")),
            ("HearingImpaired", S("HearingImpaired")),
            ("Source", S("Source")),
            ("PackId", S("PackId")),
            ("Fingerprint", S("Fingerprint")),
            ("RefusalReason", S("RefusalReason")),
            ("RefusedAt", S("RefusedAt")),
            ("UploadedAt", S("UploadedAt")),
        };

        string sql = "SELECT " + string.Join(", ", spalten.Select(s => s.Expr)) + " FROM packs;";
        int n = 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var werte = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < spalten.Count; i++)
            {
                werte[spalten[i].Name] = ReadValue(r, i);
            }

            string imdb = werte["ImdbId"] as string ?? string.Empty;
            if (string.IsNullOrEmpty(imdb))
            {
                // Ohne Serie kein Anker und damit keine Zeile — dieselbe Regel wie F-M514: ohne
                // Geschäftsschlüssel gibt es keine Zeile. Die packs-Zeile selbst bleibt unberührt.
                continue;
            }

            int season = AsInt(werte["Season"]) ?? 0;
            bool? hi = werte["HearingImpaired"] is long h ? h != 0 : (werte["HearingImpaired"] is bool b ? b : null);
            // F-M642: eine Zeile aus der Zeit vor der Quellen-Spalte gehörte SubDL — damals war SubDL
            // die einzige Quelle mit Behältern.
            string quelle = werte["Source"] as string is { Length: > 0 } s ? s : SourceMatrix.Subdl;

            n++;
            string k = quelle + "|" + MarkKind.Pack;
            gelesen[k] = gelesen.TryGetValue(k, out int g) ? g + 1 : 1;

            Lege(zeilen, new SourceMatrixEntity
            {
                ImdbId = imdb,
                Source = quelle,
                Kind = MarkKind.Pack,
                Language = SourceMatrix.Normalise(werte["Language"] as string),
                Season = season,
                HearingImpaired = hi,
                ClosedAt = werte["UploadedAt"] as DateTime?,
                Fingerprint = werte["Fingerprint"] as string,
                SourceId = werte["PackId"] as string,
                RefusalReason = werte["RefusalReason"] as string,
                RefusedAt = werte["RefusedAt"] as DateTime?,
            }, SourceMatrix.AnchorOf(null, imdb, season));
        }

        return n;
    }

    /// <summary>
    /// Legt eine Zeile in die Sammlung. Zwei alte Zeilen können auf denselben Schlüssel fallen (zwei
    /// Spuren einer Sprache, zwei Zeilen derselben Datei) — dann gewinnt der spätere Zeitstempel und
    /// eine fehlende Kennung wird ergänzt. Nichts wird überschrieben, was schon etwas sagt.
    /// </summary>
    private static void Lege(
        Dictionary<string, SourceMatrixEntity> zeilen, SourceMatrixEntity neu, string anchor)
    {
        string key = SourceMatrix.KeyOf(anchor, neu.Source, neu.Kind, neu.Language, neu.HearingImpaired == true);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        neu.Id = key;
        if (neu.SourceId != null && neu.SourceId.Length == 0)
        {
            neu.SourceId = null;
        }

        if (neu.Fingerprint != null && neu.Fingerprint.Length == 0)
        {
            neu.Fingerprint = null;
        }

        neu.Written = DateTime.UtcNow;

        if (!zeilen.TryGetValue(key, out var vorhanden))
        {
            zeilen[key] = neu;
            return;
        }

        if (neu.ClosedAt != null && (vorhanden.ClosedAt == null || neu.ClosedAt > vorhanden.ClosedAt))
        {
            vorhanden.ClosedAt = neu.ClosedAt;

            // Die Id gehoert zum Urteil: geschlossen wurde die Marke zu DIESEM Zeitpunkt von DIESEM
            // Eintrag. Eine Id vom frueheren Zeitstempel neben dem spaeteren Datum waere eine falsche
            // Auskunft — deshalb wandert sie mit.
            if (!string.IsNullOrEmpty(neu.SourceId))
            {
                vorhanden.SourceId = neu.SourceId;
            }
        }

        vorhanden.SourceId ??= neu.SourceId;
        vorhanden.Fingerprint ??= neu.Fingerprint;
        vorhanden.MtimeUnix ??= neu.MtimeUnix;
        vorhanden.RefusalReason ??= neu.RefusalReason;
        vorhanden.RefusedAt ??= neu.RefusedAt;
        if (string.IsNullOrEmpty(vorhanden.ImdbId))
        {
            vorhanden.ImdbId = neu.ImdbId;
        }

        if (string.IsNullOrEmpty(vorhanden.TmdbId))
        {
            vorhanden.TmdbId = neu.TmdbId;
        }

        if (string.IsNullOrEmpty(vorhanden.MediaHash))
        {
            vorhanden.MediaHash = neu.MediaHash;
        }
    }

    /// <summary>Schreibt die Zeilen in einem Durchgang. <c>INSERT OR IGNORE</c> — eine vorhandene Marke gewinnt.</summary>
    private static int Schreibe(SqliteConnection conn, SqliteTransaction tx, IEnumerable<SourceMatrixEntity> zeilen)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR IGNORE INTO source_matrix (Id, MediaHash, ImdbId, TmdbId, Source, Kind, Language, Season, "
            + "Episode, HearingImpaired, ClosedAt, Fingerprint, MtimeUnix, SourceId, RefusalReason, RefusedAt, Written) "
            + "VALUES ($Id, $MediaHash, $ImdbId, $TmdbId, $Source, $Kind, $Language, $Season, $Episode, "
            + "$HearingImpaired, $ClosedAt, $Fingerprint, $MtimeUnix, $SourceId, $RefusalReason, $RefusedAt, $Written);";

        var p = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "$Id", "$MediaHash", "$ImdbId", "$TmdbId", "$Source", "$Kind", "$Language", "$Season",
                     "$Episode", "$HearingImpaired", "$ClosedAt", "$Fingerprint", "$MtimeUnix", "$SourceId",
                     "$RefusalReason", "$RefusedAt", "$Written",
                 })
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            cmd.Parameters.Add(param);
            p[name] = param;
        }

        int n = 0;
        foreach (var z in zeilen)
        {
            p["$Id"].Value = z.Id;
            p["$MediaHash"].Value = z.MediaHash;
            p["$ImdbId"].Value = z.ImdbId;
            p["$TmdbId"].Value = z.TmdbId;
            p["$Source"].Value = z.Source;
            p["$Kind"].Value = z.Kind;
            p["$Language"].Value = z.Language;
            p["$Season"].Value = (object?)z.Season ?? DBNull.Value;
            p["$Episode"].Value = (object?)z.Episode ?? DBNull.Value;
            p["$HearingImpaired"].Value = z.HearingImpaired == null ? DBNull.Value : (object)(z.HearingImpaired.Value ? 1 : 0);
            p["$ClosedAt"].Value = MomentOderNull(z.ClosedAt);
            p["$Fingerprint"].Value = (object?)z.Fingerprint ?? DBNull.Value;
            p["$MtimeUnix"].Value = (object?)z.MtimeUnix ?? DBNull.Value;
            p["$SourceId"].Value = (object?)z.SourceId ?? DBNull.Value;
            p["$RefusalReason"].Value = (object?)z.RefusalReason ?? DBNull.Value;
            p["$RefusedAt"].Value = MomentOderNull(z.RefusedAt);
            p["$Written"].Value = MomentOderNull(z.Written) ?? DBNull.Value;
            n += cmd.ExecuteNonQuery();
        }

        return n;
    }

    private static object MomentOderNull(object? wert)
        => wert switch
        {
            null => DBNull.Value,
            DateTime dt => dt,
            string s when s.Length == 0 => DBNull.Value,
            _ => wert,
        };

    // ---------------------------------------------------------------------------------------------
    // Kleine Helfer: Spalten fragen, Werte lesen, WAL einfalten.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Der Spalten-Präfix einer Quelle in den breiten Spalten.</summary>
    private static string PrefixOf(string source) => source switch
    {
        SourceMatrix.Subdl => "Subdl",
        SourceMatrix.TvSubtitles => "TvSubtitles",
        _ => "Moviesubtitles",
    };

    private static int? AsInt(object? wert)
        => wert switch
        {
            null => null,
            long l => (int)l,
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) => v,
            _ => null,
        };

    /// <summary>
    /// Ein Wert aus dem Leser — Zeitpunkte als <see cref="DateTime"/>, damit sie so geschrieben werden,
    /// wie der Store sie schreibt. Was nicht als Zeitpunkt lesbar ist, wird durchgereicht, wie es in der
    /// Spalte stand: eine Umdeutung wäre schlimmer als ein unveränderter Wert.
    /// </summary>
    private static object? ReadValue(SqliteDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal))
        {
            return null;
        }

        object wert = reader.GetValue(ordinal);
        if (wert is string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var zeit))
            {
                return zeit;
            }

            return text;
        }

        return wert;
    }

    private static int CountMatrix(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM source_matrix;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

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
