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
/// F-M667/F-M668: die einmalige Übernahme des Altbestands in die Marken-Matrix — und das Niederlegen
/// der breiten Spalten in derselben Transaktion.
/// <para>
/// WAS HIER PASSIERT, UND WARUM VOR DEM ÖFFNEN DES STORES: die Marken standen bis Schema 18 als breite
/// Spalten an den Zeilen (<c>SubdlUploadedAt</c>/<c>TvSubtitlesUploadedAt</c>/<c>MoviesubtitlesUploadedAt</c>
/// samt Abruf- und Einsende-Ids an <c>embeds</c> und <c>sidecars</c>, <c>CompleteSubdl</c>/
/// <c>CompleteTvSubtitles</c>/<c>CompleteMsub</c>/<c>CompleteSignature</c> an <c>media</c>, Fingerabdruck
/// und Ablehnung an <c>packs</c>). Diese Klasse liest sie EINMAL, sichert vorher die ganze Datei unter
/// einem Zeitstempel-Namen, schreibt je (Medium, Quelle, Art) eine Zeile in <c>source_matrix</c> und
/// ENTFERNT die Altspalten danach — in EINER Transaktion. Es gibt also keine zwei Wahrheiten: was
/// übernommen ist, steht nur noch in der Matrix; der alte Stand lebt allein in der Sicherungsdatei.
/// </para>
/// <para>
/// WANN SIE LÄUFT: wenn die Datei existiert, ein Store ist (<c>meta</c> vorhanden) und noch irgendeine
/// Altspalte darin steht. Das ist die ehrliche Bedingung — ein Lauf, der mittendrin abbricht, rollt
/// zurück und wird beim nächsten Start nachgeholt; eine übernommene Datei hat keine Altspalten mehr und
/// tut nichts (idempotent). Der Schema-Marker ist NICHT die Bedingung.
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
    /// <param name="IdsTaken">F-M670: how many values of the old <c>SubdlId</c> column went into the matrix.</param>
    /// <param name="MarksTaken">F-M670: how many rows of the old <c>source_marks</c> table went into the matrix.</param>
    /// <param name="MarksNew">F-M670: how many of those needed a NEW matrix row (the rest were already covered).</param>
    /// <param name="Unplaced">F-M670: old rows that had no anchor (no media hash / no owner row) — they live on in the backup only.</param>
    /// <param name="Conflicts">F-M670: values whose cell was already taken by a different id — kept, both reported.</param>
    public sealed record Outcome(
        bool Ran,
        string? BackupPath,
        IReadOnlyDictionary<string, int> Read,
        IReadOnlyDictionary<string, int> Written,
        int Merged,
        int IdsTaken = 0,
        int MarksTaken = 0,
        int Unplaced = 0,
        int Conflicts = 0,
        int MarksNew = 0);

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

            // F-M668: DIE BEDINGUNG IST DER BESTAND, NICHT DER MARKER. Solange irgendwo noch eine breite
            // Marken-Spalte steht, ist etwas zu übernehmen; sind alle weg, ist der Lauf erledigt und ein
            // zweiter Start tut nichts (idempotent). Damit wird auch ein abgebrochener Lauf beim nächsten
            // Start nachgeholt, statt für immer als „erledigt" zu gelten.
            // F-M670: dasselbe gilt für die zwei letzten Reste eines zweiten Bestands — die Alt-Tabelle
            // `source_marks` und die Id-Spalte `SubdlId` an `embeds`/`sidecars`. Steht einer davon noch da,
            // ist etwas zu übernehmen, auch wenn alle breiten Spalten längst gefallen sind (der Weg 20 → 21).
            var altspalten = LegacyColumns(conn);
            bool markenTabelle = TableExists(conn, "source_marks");
            if (altspalten.Count == 0 && !markenTabelle)
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

            // F-M670: die zwei Reste, die NICHT aus den breiten Spalten stammen. Gelesen wird VOR der
            // Transaktion (wie alles andere), geschrieben wird drin — nach dem Füllen, damit eine
            // Zielzeile schon steht und nur noch ihre Zelle besetzt wird.
            var idWerte = altspalten.Any(a => a.Column == SubdlIdColumn)
                ? LeseSubdlIds(conn)
                : new List<LegacyIdValue>();
            var altMarken = markenTabelle ? LeseSourceMarks(conn) : new List<LegacyMark>();

            // SICHERUNG ZUERST — vor der ersten Änderung an der Datei, und mit dem VOLLSTÄNDIGEN
            // Altbestand darin (Betreiber-Vorgabe 21.11.2026: „Kein doppelter Datenbestand … der
            // Altbestand darf nur als Sicherheitskopie weiterexistieren"). Ein WAL-Rest wird vorher
            // eingefaltet, sonst fehlt der Kopie alles seit dem letzten Checkpoint (F-M326).
            Checkpoint(conn);
            string? sicherung = null;
            if (backup)
            {
                sicherung = BackupPathFor(dbPath);
                File.Copy(dbPath, sicherung, overwrite: true);
            }

            // EINE Transaktion um Anlegen, Füllen UND Niederlegen der Altspalten: bricht der Lauf ab,
            // ist die Datei unverändert — Matrix wie Spalten —, und der nächste Start holt ihn nach.
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

            // Eine Datei aus Schritt A (Schema 18) trägt die Tabelle ohne die Felder, die Schritt B/C
            // ergänzen. Sie werden IN der Transaktion nachgezogen, damit der INSERT unten passt.
            EnsureMatrixColumns(conn, tx);

            // F-M669: eine Datei aus Schritt A trägt die Matrix bereits — dort stand EINE Spur-Marke je
            // Paar (SubPos null). Die Übernahme leitet dieselben Urteile jetzt JE POSITION aus den
            // Altspalten ab; die paarweite Kopie muss fallen, sonst stünde jedes Urteil zweimal da
            // (einmal breit, einmal je Position) — der doppelte Datenbestand, den F-M668 verbietet.
            // Nur wenn Spur-Altspalten da sind: sonst gäbe es nichts, woraus die Positionen entstehen.
            // F-M670: die Bedingung nennt jetzt die MARKEN-Spalten ausdrücklich — `SubdlId` allein sagt
            // nichts über eine Position und darf eine paarweite Brücke aus Schema 19 nicht wegräumen.
            if (altspalten.Any(a => a.Table is "embeds" or "sidecars" && MarkColumns.Contains(a.Column)))
            {
                using var weg = conn.CreateCommand();
                weg.Transaction = tx;
                weg.CommandText = "DELETE FROM source_matrix WHERE Kind IN ('track','track-hi') AND SubPos IS NULL;";
                weg.ExecuteNonQuery();
            }

            int marken = zeilen.Count;
            int geschrieben = Schreibe(conn, tx, zeilen.Values);

            // F-M670: die zwei Reste des zweiten Bestands wandern ZUERST in die Matrix — die Id-Spalte
            // und die Alt-Tabelle —, und erst danach fallen sie. Reihenfolge und Transaktion sind die
            // ganze Regel: was nicht angekommen ist, wird nicht gelöscht.
            var (idsUebernommen, idKonflikte) = UebernimmSubdlIds(conn, tx, idWerte);
            var (markenUebernommen, markenNeu) = UebernimmSourceMarks(conn, tx, altMarken);
            int unplatziert = idWerte.Count(w => w.Unplaced) + altMarken.Count(m => m.Unplaced);

            // F-M668: die Altspalten FALLEN — in derselben Transaktion, die die Marken geschrieben hat.
            // Danach existiert jedes Urteil genau einmal: in `source_matrix`.
            var gefallen = new List<string>();
            foreach (var (table, column) in altspalten)
            {
                using var drop = conn.CreateCommand();
                drop.Transaction = tx;
                drop.CommandText = "ALTER TABLE " + table + " DROP COLUMN " + column + ";";
                drop.ExecuteNonQuery();
                gefallen.Add(table + "." + column);
            }

            // F-M670: die Alt-Tabelle fällt genauso — eine Marken-Tabelle neben der Matrix wäre der
            // doppelte Bestand, den die Betreiber-Vorgabe verbietet.
            if (markenTabelle)
            {
                using var wegTabelle = conn.CreateCommand();
                wegTabelle.Transaction = tx;
                wegTabelle.CommandText = "DROP TABLE IF EXISTS source_marks;";
                wegTabelle.ExecuteNonQuery();
            }

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
                "[Sub Scribe] source matrix filled from the wide columns (F-M668): {Rows} mark(s) from {Old} old row(s), "
                + "{Merged} merged, {Dropped} wide column(s) dropped, backup {Backup}.",
                geschrieben,
                alteZeilen,
                zusammengelegt,
                gefallen.Count,
                sicherung == null ? "none" : Path.GetFileName(sicherung));

            if (idsUebernommen > 0 || markenUebernommen > 0 || unplatziert > 0)
            {
                logger?.LogInformation(
                    "[Sub Scribe] second store folded in (F-M670): {Ids} id value(s) of the SubdlId column, "
                    + "{Marks} row(s) of the old source_marks table ({New} of them needed a new mark) — {Unplaced} "
                    + "without an anchor (kept in the backup), {Conflicts} id(s) whose cell was already taken by another.",
                    idsUebernommen,
                    markenUebernommen,
                    markenNeu,
                    unplatziert,
                    idKonflikte);
            }

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

            return new Outcome(true, sicherung, gelesen, nachher, zusammengelegt,
                idsUebernommen, markenUebernommen, unplatziert, idKonflikte, markenNeu);
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
            // F-M669: die Position der Spur steht an der alten Zeile (`embeds.SubPos`). Ein Seitenwagen
            // hat keine — er bekommt die feste `SidecarPosition`, wie jeder Seitenwagen-Schreiber.
            ("SubPos", sidecar ? "NULL" : Select("SubPos")),
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
            spalten.Add((prefix + "UploadId", Select(prefix + "UploadId")));
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
                // F-M669: eine Schema-17-Zeile hat KEINE Position — dann bleibt sie null und die Marke
                // ist die paarweite Brücke, die ein Leser ohne Position noch findet.
                int? subPos = sidecar
                    ? SourceMatrix.SidecarPosition
                    : AsInt(werte["SubPos"]);

                foreach (string quelle in SourceMatrix.Sources)
                {
                    string prefix = PrefixOf(quelle);
                    if (!werte.ContainsKey(prefix + "UploadedAt"))
                    {
                        continue;
                    }

                    var zeit = werte[prefix + "UploadedAt"] as DateTime?;
                    string? abrufId = werte[prefix + "DownloadId"] as string;
                    string? einsendeId = werte[prefix + "UploadId"] as string;

                    // F-M668: eine Zeile ist auch dann eine Marke, wenn sie nur eine Einsende-Id trägt
                    // (Schema 11 kannte keinen Upload-Zeitpunkt). Bisher zählte sie über die Spalte; jetzt
                    // zählt die Id selbst — was hier steht, geht sonst mit der Spalte verloren.
                    if (zeit == null && string.IsNullOrEmpty(abrufId) && string.IsNullOrEmpty(einsendeId))
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
                        SubPos = subPos,
                        HearingImpaired = hi,
                        ClosedAt = zeit,
                        SourceId = abrufId,
                        UploadId = einsendeId,
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
            ("RefusedSignature", S("RefusedSignature")),
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
                RefusalSignature = werte["RefusedSignature"] as string,
            }, SourceMatrix.AnchorOf(null, imdb, season));
        }

        return n;
    }

    // ---------------------------------------------------------------------------------------------
    // F-M670: die zwei Reste des zweiten Bestands — die Id-Spalte `SubdlId` und die Alt-Tabelle
    // `source_marks`. Beide werden gelesen, in die Matrix übernommen und dann niedergelegt.
    // ---------------------------------------------------------------------------------------------

    /// <summary>F-M670: der Spaltenname des zweiten Bestands an <c>embeds</c> und <c>sidecars</c>.</summary>
    private const string SubdlIdColumn = "SubdlId";

    /// <summary>
    /// F-M670: die Marken-Spalten der Spur-Arten. Der paarweite DELETE (F-M669) hängt an IHNEN — die
    /// Id-Spalte allein sagt nichts über eine Position.
    /// </summary>
    private static readonly string[] MarkColumns =
    {
        "SubdlUploadedAt", "TvSubtitlesUploadedAt", "MoviesubtitlesUploadedAt",
    };

    /// <summary>
    /// F-M670: ein Wert der alten Id-Spalte, samt allem, was seine Marke braucht. <see cref="SubPos"/>
    /// ist die Videospur-Position einer eingebetteten Zeile, bei einem Seitenwagen die feste −1.
    /// </summary>
    private sealed record LegacyIdValue(
        string MediaHash,
        int SubPos,
        string Language,
        bool? HearingImpaired,
        string Status,
        string Value,
        string ImdbId,
        string TmdbId,
        int? Season,
        int? Episode)
    {
        /// <summary>Wahr, wenn der Wert leer ist oder der Anker fehlt — dann gibt es keine Zeile dafür.</summary>
        public bool Unplaced => string.IsNullOrWhiteSpace(Value) || string.IsNullOrWhiteSpace(MediaHash);
    }

    /// <summary>
    /// F-M670: eine Zeile der Alt-Tabelle <c>source_marks</c>, samt Sprache und Position ihrer Besitzerzeile.
    /// <c>OwnerKey</c> war <c>embeds.Id</c> (Medien-Hash|Position) bzw. <c>sidecars.Id</c> (Inhalts-Hash).
    /// </summary>
    private sealed record LegacyMark(
        string Source,
        string? DownloadId,
        string? UploadId,
        DateTime? UploadedAt,
        DateTime? CompletedAt,
        string MediaHash,
        int SubPos,
        string Language,
        bool? HearingImpaired,
        string ImdbId,
        string TmdbId,
        int? Season,
        int? Episode)
    {
        /// <summary>Wahr, wenn die Besitzerzeile fehlt oder keinen Medien-Hash trägt — kein Anker, keine Marke.</summary>
        public bool Unplaced => string.IsNullOrWhiteSpace(MediaHash);
    }

    /// <summary>
    /// F-M670: liest die Id-Spalte an <c>embeds</c> und <c>sidecars</c> — je Zeile mit Sprache, Variante,
    /// Position und Status, weil erst der Status sagt, ob die Id eine Einsendung (<c>uploaded</c>) oder
    /// ein Fund ist.
    /// </summary>
    private static List<LegacyIdValue> LeseSubdlIds(SqliteConnection conn)
    {
        var werte = new List<LegacyIdValue>();
        bool hatMedia = TableExists(conn, "media");

        foreach (var (table, sidecar) in new[] { ("embeds", false), ("sidecars", true) })
        {
            if (!TableExists(conn, table) || !ColumnExists(conn, table, SubdlIdColumn))
            {
                continue;
            }

            string S(string column) => ColumnExists(conn, table, column) ? "e." + column : "NULL";
            string join = hatMedia ? " LEFT JOIN media m ON m.Id = e.MediaHash" : string.Empty;
            string sql =
                "SELECT e.MediaHash, " + (sidecar ? "-1" : S("SubPos")) + ", " + S("Language") + ", "
                + S("HearingImpaired") + ", " + S("Status") + ", e." + SubdlIdColumn + ", "
                + (sidecar ? S("ImdbId") : (hatMedia ? "m.ImdbId" : "NULL")) + ", "
                + (sidecar ? S("TmdbId") : (hatMedia ? "m.TmdbId" : "NULL")) + ", "
                + (sidecar ? S("Season") : (hatMedia ? "m.Season" : "NULL")) + ", "
                + (sidecar ? S("Episode") : (hatMedia ? "m.Episode" : "NULL"))
                + " FROM " + table + " e" + join
                + " WHERE e." + SubdlIdColumn + " IS NOT NULL AND e." + SubdlIdColumn + " <> '';";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                werte.Add(new LegacyIdValue(
                    r.IsDBNull(0) ? string.Empty : r.GetString(0),
                    AsInt(ReadValue(r, 1)) ?? SourceMatrix.SidecarPosition,
                    r.IsDBNull(2) ? string.Empty : Convert.ToString(r.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    AsHi(ReadValue(r, 3)),
                    r.IsDBNull(4) ? string.Empty : Convert.ToString(r.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty,
                    r.IsDBNull(5) ? string.Empty : Convert.ToString(r.GetValue(5), CultureInfo.InvariantCulture) ?? string.Empty,
                    AlsText(ReadValue(r, 6)),
                    AlsText(ReadValue(r, 7)),
                    AsInt(ReadValue(r, 8)),
                    AsInt(ReadValue(r, 9))));
            }
        }

        return werte;
    }

    /// <summary>
    /// F-M670: liest die Alt-Tabelle <c>source_marks</c> und holt Sprache, Variante und Position aus der
    /// Zeile, auf die ihr <c>OwnerKey</c> zeigt. Fehlt die Besitzerzeile, bleibt der Anker leer und die
    /// Zeile wird als „nicht platzierbar" gezählt — sie lebt dann nur in der Sicherung.
    /// </summary>
    private static List<LegacyMark> LeseSourceMarks(SqliteConnection conn)
    {
        var marken = new List<LegacyMark>();
        if (!TableExists(conn, "source_marks"))
        {
            return marken;
        }

        bool hatEmbeds = TableExists(conn, "embeds");
        bool hatSidecars = TableExists(conn, "sidecars");
        bool hatMedia = TableExists(conn, "media");

        var spalten = new List<(string Name, string Expr)>
        {
            ("Source", "s.Source"),
            ("DownloadId", ColumnExists(conn, "source_marks", "DownloadId") ? "s.DownloadId" : "NULL"),
            ("UploadId", ColumnExists(conn, "source_marks", "UploadId") ? "s.UploadId" : "NULL"),
            ("UploadedAt", ColumnExists(conn, "source_marks", "UploadedAt") ? "s.UploadedAt" : "NULL"),
            ("CompletedAt", ColumnExists(conn, "source_marks", "CompletedAt") ? "s.CompletedAt" : "NULL"),
            ("EmbedHash", hatEmbeds ? "e.MediaHash" : "NULL"),
            ("EmbedPos", hatEmbeds ? "e.SubPos" : "NULL"),
            ("EmbedLang", hatEmbeds ? "e.Language" : "NULL"),
            ("EmbedHi", hatEmbeds ? "e.HearingImpaired" : "NULL"),
            ("SideHash", hatSidecars ? "c.MediaHash" : "NULL"),
            ("SideLang", hatSidecars ? "c.Language" : "NULL"),
            ("SideHi", hatSidecars ? "c.HearingImpaired" : "NULL"),
            ("ImdbId", hatMedia ? "m.ImdbId" : "NULL"),
            ("TmdbId", hatMedia ? "m.TmdbId" : "NULL"),
            ("Season", hatMedia ? "m.Season" : "NULL"),
            ("Episode", hatMedia ? "m.Episode" : "NULL"),
        };

        string sql = "SELECT " + string.Join(", ", spalten.Select(s => s.Expr)) + " FROM source_marks s"
                     + (hatEmbeds ? " LEFT JOIN embeds e ON e.Id = s.OwnerKey" : string.Empty)
                     + (hatSidecars ? " LEFT JOIN sidecars c ON c.Id = s.OwnerKey" : string.Empty)
                     + (hatMedia ? " LEFT JOIN media m ON m.Id = COALESCE(e.MediaHash, c.MediaHash)" : string.Empty)
                     + ";";

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

            string embedHash = werte["EmbedHash"] as string ?? string.Empty;
            string sideHash = werte["SideHash"] as string ?? string.Empty;
            bool aufSpur = !string.IsNullOrEmpty(embedHash);

            marken.Add(new LegacyMark(
                werte["Source"] as string ?? string.Empty,
                werte["DownloadId"] as string,
                werte["UploadId"] as string,
                werte["UploadedAt"] as DateTime?,
                werte["CompletedAt"] as DateTime?,
                aufSpur ? embedHash : sideHash,
                aufSpur ? AsInt(werte["EmbedPos"]) ?? SourceMatrix.SidecarPosition : SourceMatrix.SidecarPosition,
                (aufSpur ? werte["EmbedLang"] as string : werte["SideLang"] as string) ?? string.Empty,
                AsHi(aufSpur ? werte["EmbedHi"] : werte["SideHi"]),
                AlsText(werte["ImdbId"]),
                AlsText(werte["TmdbId"]),
                AsInt(werte["Season"]),
                AsInt(werte["Episode"])));
        }

        return marken;
    }

    private static (int Taken, int Conflicts) UebernimmSubdlIds(
        SqliteConnection conn, SqliteTransaction tx, List<LegacyIdValue> werte)
    {
        int genommen = 0, konflikte = 0;
        foreach (var wert in werte)
        {
            if (wert.Unplaced)
            {
                continue;
            }

            bool hi = wert.HearingImpaired == true;
            string key = SourceMatrix.KeyOf(
                SourceMatrix.AnchorOf(wert.MediaHash, null), SourceMatrix.Subdl,
                MarkKind.ForTrack(hi), wert.Language, hi, wert.SubPos);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            // F-M670: der STATUS entscheidet, welches Feld die Id führt. `uploaded` heißt: das ist die
            // Einsendung (F-M435/UploadId). Alles andere ist ein FUND (Download, abgelehnter Kandidat)
            // und gehört als Eintrags-Id nach `SourceId` — die Richtung, in der die alte Spalte
            // beschrieben wurde, ist damit erhalten.
            bool eingesendet = string.Equals(wert.Status, "uploaded", StringComparison.Ordinal);
            string primaer = eingesendet ? "UploadId" : "SourceId";
            string sekundaer = eingesendet ? "SourceId" : "UploadId";

            var (existiert, sourceId, uploadId) = LadeZellen(conn, tx, key);
            if (!existiert)
            {
                // Keine Marke vorhanden: die Id selbst ist der Beleg. Der Zeitstempel bleibt leer —
                // erfunden wird nichts; bei `uploaded` schließt die Einsende-Id die Marke (Mark.IsClosed).
                FuegeZeileEin(conn, tx, key, wert, primaer);
                genommen++;
                continue;
            }

            string? belegt = primaer == "UploadId" ? uploadId : sourceId;
            if (string.IsNullOrEmpty(belegt) || IstEigeneMarke(belegt))
            {
                // Leer oder nur eine selbst vergebene Marke (`own:…`): die echte Kennung gewinnt — sie
                // ist die einzige, mit der sich der Eintrag bei der Quelle adressieren lässt.
                SetzeZelle(conn, tx, key, primaer, wert.Value);
                genommen++;
                continue;
            }

            if (string.Equals(belegt, wert.Value, StringComparison.Ordinal))
            {
                continue;
            }

            string? frei = sekundaer == "UploadId" ? uploadId : sourceId;
            if (string.IsNullOrEmpty(frei))
            {
                // Die andere Zelle derselben Marke ist frei: zwei Kennungen derselben Einsendung sind
                // kein Widerspruch (F-M435: Upload- und Eintrags-Id liegen in verschiedenen Bereichen).
                SetzeZelle(conn, tx, key, sekundaer, wert.Value);
                genommen++;
                continue;
            }

            if (string.Equals(frei, wert.Value, StringComparison.Ordinal))
            {
                continue;
            }

            // Beide Zellen tragen schon etwas anderes. Nichts wird überschrieben; der Wert lebt in der
            // Sicherung weiter und der Lauf meldet ihn.
            konflikte++;
        }

        return (genommen, konflikte);
    }

    private static (int Taken, int New) UebernimmSourceMarks(
        SqliteConnection conn, SqliteTransaction tx, List<LegacyMark> marken)
    {
        int genommen = 0, neu = 0;
        foreach (var marke in marken)
        {
            if (marke.Unplaced || string.IsNullOrEmpty(marke.Source))
            {
                continue;
            }

            bool hi = marke.HearingImpaired == true;
            string key = SourceMatrix.KeyOf(
                SourceMatrix.AnchorOf(marke.MediaHash, null), marke.Source,
                MarkKind.ForTrack(hi), marke.Language, hi, marke.SubPos);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            // Der Abschluss-Zeitpunkt der Alt-Tabelle ist die Einsendung; `CompletedAt` ist der zweite
            // Beleg derselben Quelle und springt nur ein, wenn kein Einsende-Zeitpunkt steht.
            DateTime? geschlossen = marke.UploadedAt ?? marke.CompletedAt;

            bool hatteZeile = LadeZellen(conn, tx, key).Existiert;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO source_matrix (Id, MediaHash, ImdbId, TmdbId, Source, Kind, Language, Season, Episode, "
                + "SubPos, HearingImpaired, ClosedAt, SourceId, UploadId, Written) "
                + "VALUES ($Id, $MediaHash, $ImdbId, $TmdbId, $Source, $Kind, $Language, $Season, $Episode, "
                + "$SubPos, $HearingImpaired, $ClosedAt, $SourceId, $UploadId, $Written) "
                + "ON CONFLICT(Id) DO UPDATE SET "
                + "ClosedAt = COALESCE(source_matrix.ClosedAt, excluded.ClosedAt), "
                + "SourceId = COALESCE(NULLIF(source_matrix.SourceId, ''), excluded.SourceId), "
                + "UploadId = COALESCE(NULLIF(source_matrix.UploadId, ''), excluded.UploadId);";
            cmd.Parameters.AddWithValue("$Id", key);
            cmd.Parameters.AddWithValue("$MediaHash", marke.MediaHash);
            cmd.Parameters.AddWithValue("$ImdbId", marke.ImdbId ?? string.Empty);
            cmd.Parameters.AddWithValue("$TmdbId", marke.TmdbId ?? string.Empty);
            cmd.Parameters.AddWithValue("$Source", marke.Source);
            cmd.Parameters.AddWithValue("$Kind", MarkKind.ForTrack(hi));
            cmd.Parameters.AddWithValue("$Language", SourceMatrix.Normalise(marke.Language));
            cmd.Parameters.AddWithValue("$Season", (object?)marke.Season ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$Episode", (object?)marke.Episode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$SubPos", marke.SubPos);
            cmd.Parameters.AddWithValue("$HearingImpaired", hi ? 1 : 0);
            cmd.Parameters.AddWithValue("$ClosedAt", MomentOderNull(geschlossen));
            cmd.Parameters.AddWithValue("$SourceId", LeerOderWert(marke.DownloadId));
            cmd.Parameters.AddWithValue("$UploadId", LeerOderWert(marke.UploadId));
            cmd.Parameters.AddWithValue("$Written", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
            genommen++;
            if (!hatteZeile)
            {
                neu++;
            }
        }

        return (genommen, neu);
    }

    /// <summary>Liest die zwei Id-Zellen einer Matrix-Zeile; <c>Existiert</c> unterscheidet „keine Zeile" von „leere Zelle".</summary>
    private static (bool Existiert, string? SourceId, string? UploadId) LadeZellen(
        SqliteConnection conn, SqliteTransaction tx, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT SourceId, UploadId FROM source_matrix WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            return (false, null, null);
        }

        return (true,
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static void SetzeZelle(SqliteConnection conn, SqliteTransaction tx, string id, string spalte, string wert)
    {
        // `spalte` ist eine der zwei Konstanten aus diesem Aufrufer — kein Text aus fremder Hand.
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE source_matrix SET " + spalte + " = $wert, Written = $written WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$wert", wert);
        cmd.Parameters.AddWithValue("$written", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void FuegeZeileEin(
        SqliteConnection conn, SqliteTransaction tx, string id, LegacyIdValue wert, string spalte)
    {
        bool hi = wert.HearingImpaired == true;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT OR IGNORE INTO source_matrix (Id, MediaHash, ImdbId, TmdbId, Source, Kind, Language, Season, "
            + "Episode, SubPos, HearingImpaired, SourceId, UploadId, Written) "
            + "VALUES ($Id, $MediaHash, $ImdbId, $TmdbId, $Source, $Kind, $Language, $Season, $Episode, $SubPos, "
            + "$HearingImpaired, $SourceId, $UploadId, $Written);";
        cmd.Parameters.AddWithValue("$Id", id);
        cmd.Parameters.AddWithValue("$MediaHash", wert.MediaHash);
        cmd.Parameters.AddWithValue("$ImdbId", wert.ImdbId ?? string.Empty);
        cmd.Parameters.AddWithValue("$TmdbId", wert.TmdbId ?? string.Empty);
        cmd.Parameters.AddWithValue("$Source", SourceMatrix.Subdl);
        cmd.Parameters.AddWithValue("$Kind", MarkKind.ForTrack(hi));
        cmd.Parameters.AddWithValue("$Language", SourceMatrix.Normalise(wert.Language));
        cmd.Parameters.AddWithValue("$Season", (object?)wert.Season ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$Episode", (object?)wert.Episode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$SubPos", wert.SubPos);
        cmd.Parameters.AddWithValue("$HearingImpaired", hi ? 1 : 0);
        cmd.Parameters.AddWithValue("$SourceId", spalte == "SourceId" ? wert.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$UploadId", spalte == "UploadId" ? wert.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$Written", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Eine selbst vergebene Marke (<c>own:…</c>) ist ein Platzhalter, keine Kennung der Quelle.</summary>
    private static bool IstEigeneMarke(string? id)
        => id != null && id.StartsWith("own:", StringComparison.Ordinal);

    private static object LeerOderWert(string? wert)
        => string.IsNullOrEmpty(wert) ? DBNull.Value : wert;

    private static string? AlsText(object? wert)
        => wert switch
        {
            null => null,
            string s => s.Length == 0 ? null : s,
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(wert, CultureInfo.InvariantCulture),
        };

    private static bool? AsHi(object? wert)
        => wert switch
        {
            null => null,
            long l => l != 0,
            int i => i != 0,
            bool b => b,
            string s when bool.TryParse(s, out bool v) => v,
            string s when s == "1" => true,
            string s when s == "0" => false,
            _ => null,
        };

    /// <summary>
    /// Legt eine Zeile in die Sammlung. Zwei alte Zeilen können auf denselben Schlüssel fallen (zwei
    /// Spuren einer Sprache, zwei Zeilen derselben Datei) — dann gewinnt der spätere Zeitstempel und
    /// eine fehlende Kennung wird ergänzt. Nichts wird überschrieben, was schon etwas sagt.
    /// </summary>
    private static void Lege(
        Dictionary<string, SourceMatrixEntity> zeilen, SourceMatrixEntity neu, string anchor)
    {
        // F-M669: die Position ist Teil des Schlüssels der Spur-Arten; `file`/`pack` tragen keine.
        string key = SourceMatrix.KeyOf(
            anchor, neu.Source, neu.Kind, neu.Language, neu.HearingImpaired == true, neu.SubPos);
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

        if (neu.UploadId != null && neu.UploadId.Length == 0)
        {
            neu.UploadId = null;
        }

        if (neu.RefusalSignature != null && neu.RefusalSignature.Length == 0)
        {
            neu.RefusalSignature = null;
        }

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
            // Auskunft — deshalb wandert sie mit (Eintrags-Id und Einsende-Id).
            if (!string.IsNullOrEmpty(neu.SourceId))
            {
                vorhanden.SourceId = neu.SourceId;
            }

            if (!string.IsNullOrEmpty(neu.UploadId))
            {
                vorhanden.UploadId = neu.UploadId;
            }
        }

        vorhanden.SourceId ??= neu.SourceId;
        vorhanden.UploadId ??= neu.UploadId;
        vorhanden.Fingerprint ??= neu.Fingerprint;
        vorhanden.MtimeUnix ??= neu.MtimeUnix;
        vorhanden.RefusalReason ??= neu.RefusalReason;
        vorhanden.RefusedAt ??= neu.RefusedAt;
        vorhanden.RefusalSignature ??= neu.RefusalSignature;
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
            + "Episode, SubPos, HearingImpaired, ClosedAt, Fingerprint, MtimeUnix, SourceId, UploadId, RefusalReason, "
            + "RefusedAt, RefusalSignature, Written) "
            + "VALUES ($Id, $MediaHash, $ImdbId, $TmdbId, $Source, $Kind, $Language, $Season, $Episode, "
            + "$SubPos, $HearingImpaired, $ClosedAt, $Fingerprint, $MtimeUnix, $SourceId, $UploadId, $RefusalReason, "
            + "$RefusedAt, $RefusalSignature, $Written);";

        var p = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "$Id", "$MediaHash", "$ImdbId", "$TmdbId", "$Source", "$Kind", "$Language", "$Season",
                     "$Episode", "$SubPos", "$HearingImpaired", "$ClosedAt", "$Fingerprint", "$MtimeUnix", "$SourceId",
                     "$UploadId", "$RefusalReason", "$RefusedAt", "$RefusalSignature", "$Written",
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
            p["$SubPos"].Value = (object?)z.SubPos ?? DBNull.Value;
            p["$HearingImpaired"].Value = z.HearingImpaired == null ? DBNull.Value : (object)(z.HearingImpaired.Value ? 1 : 0);
            p["$ClosedAt"].Value = MomentOderNull(z.ClosedAt);
            p["$Fingerprint"].Value = (object?)z.Fingerprint ?? DBNull.Value;
            p["$MtimeUnix"].Value = (object?)z.MtimeUnix ?? DBNull.Value;
            p["$SourceId"].Value = (object?)z.SourceId ?? DBNull.Value;
            p["$UploadId"].Value = (object?)z.UploadId ?? DBNull.Value;
            p["$RefusalReason"].Value = (object?)z.RefusalReason ?? DBNull.Value;
            p["$RefusedAt"].Value = MomentOderNull(z.RefusedAt);
            p["$RefusalSignature"].Value = (object?)z.RefusalSignature ?? DBNull.Value;
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

    /// <summary>
    /// F-M668: die Altspalten, die dieser Lauf übernimmt UND danach niederlegt. EINE Liste, damit
    /// „was wird übernommen" und „was fällt" nicht auseinanderlaufen können — eine Spalte, die
    /// übernommen aber nicht entfernt wird, wäre der doppelte Datenbestand, den F-M668 verbietet.
    /// </summary>
    private static readonly (string Table, string[] Columns)[] LegacyColumnsByTable =
    {
        ("embeds", new[]
        {
            "SubdlDownloadId", "SubdlUploadId", "SubdlUploadedAt",
            "TvSubtitlesDownloadId", "TvSubtitlesUploadId", "TvSubtitlesUploadedAt",
            "MoviesubtitlesDownloadId", "MoviesubtitlesUploadId", "MoviesubtitlesUploadedAt",
            // F-M670: die letzte eigene Id-Spalte. Sie führt „die Id, auf die sich dieses Urteil
            // bezieht" — genau das, was die Matrix als `SourceId`/`UploadId` führt.
            SubdlIdColumn,
        }),
        ("sidecars", new[]
        {
            "SubdlDownloadId", "SubdlUploadId", "SubdlUploadedAt",
            "TvSubtitlesDownloadId", "TvSubtitlesUploadId", "TvSubtitlesUploadedAt",
            "MoviesubtitlesDownloadId", "MoviesubtitlesUploadId", "MoviesubtitlesUploadedAt",
            SubdlIdColumn,
        }),
        ("media", new[] { "CompleteSubdl", "CompleteTvSubtitles", "CompleteMsub", "CompleteSignature" }),
        ("packs", new[] { "PackId", "Fingerprint", "RefusalReason", "RefusedAt", "RefusedSignature", "UploadedAt" }),
    };

    /// <summary>Die Altspalten, die in dieser Datei tatsächlich noch stehen (Tabelle + Spalte).</summary>
    /// <param name="conn">Open connection to the data file.</param>
    /// <returns>The columns to take over and to drop.</returns>
    public static List<(string Table, string Column)> LegacyColumns(SqliteConnection conn)
    {
        var gefunden = new List<(string, string)>();
        foreach (var (table, columns) in LegacyColumnsByTable)
        {
            if (!TableExists(conn, table))
            {
                continue;
            }

            foreach (string column in columns)
            {
                if (ColumnExists(conn, table, column))
                {
                    gefunden.Add((table, column));
                }
            }
        }

        return gefunden;
    }

    /// <summary>
    /// F-M668/F-M669: die Felder, die die Matrix seit Schritt A/B ergänzt hat. Eine ältere Datei trägt
    /// die Tabelle ohne sie — der Einfüge-Befehl würde sonst scheitern.
    /// </summary>
    private static void EnsureMatrixColumns(SqliteConnection conn, SqliteTransaction tx)
    {
        foreach (var (column, ddl) in new[]
                 {
                     ("UploadId", "ALTER TABLE source_matrix ADD COLUMN UploadId TEXT NULL;"),
                     ("RefusalSignature", "ALTER TABLE source_matrix ADD COLUMN RefusalSignature TEXT NULL;"),
                     // F-M669: die Spur-Position (null bei file/pack, -1 bei einem Seitenwagen).
                     ("SubPos", "ALTER TABLE source_matrix ADD COLUMN SubPos INTEGER NULL;"),
                 })
        {
            if (ColumnExists(conn, "source_matrix", column))
            {
                continue;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = ddl;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// F-M668: was in einer Datei steht — je Quelle und Art die Zahl der Marken in der Matrix und die
    /// Zahl der Reste, die noch auf eine Übernahme warten. Der Formatwechsel (F-M637) prüft damit,
    /// dass die neue Datei die Marken der Vorgängerin wirklich trägt.
    /// <para>
    /// F-M670: „Reste" heißt seit 21.11.2026 auch die Alt-Tabelle <c>source_marks</c> — eine zweite
    /// Marken-Tabelle in der neuen Datei wäre derselbe doppelte Bestand wie eine breite Spalte.
    /// </para>
    /// </summary>
    /// <param name="path">Path of a data file.</param>
    /// <returns>Marks per "source|kind" and the number of legacy columns and tables still present.</returns>
    public static (Dictionary<string, int> Marks, int LegacyColumns) Inspect(string path)
    {
        var marken = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return (marken, 0);
        }

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();

        int reste = LegacyColumns(conn).Count + (TableExists(conn, "source_marks") ? 1 : 0);

        if (!TableExists(conn, "source_matrix"))
        {
            return (marken, reste);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Source, Kind, COUNT(*) FROM source_matrix GROUP BY Source, Kind;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            marken[r.GetString(0) + "|" + r.GetString(1)] = r.GetInt32(2);
        }

        return (marken, reste);
    }

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
