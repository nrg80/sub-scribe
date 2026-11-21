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
/// F-M667: die vier Arten, für die es ein Urteil je Quelle gibt.
/// <para>
/// Die HI-Variante ist eine eigene ART und kein Feld: ein Urteil über die normale Spur sagt nichts
/// über die HI-Spur, und in der Matrix ist die Art der billigste Ort, das auseinanderzuhalten — dieselbe
/// Entscheidung, die die beiden Spaltenpaare vorher je Richtung getroffen haben.
/// </para>
/// </summary>
public static class MarkKind
{
    /// <summary>Spur, normale Fassung.</summary>
    public const string Track = "track";

    /// <summary>Spur, hearing-impaired / SDH.</summary>
    public const string TrackHi = "track-hi";

    /// <summary>Die Datei ist für diese Quelle zugegangen (Zugeh-Stempel je Quelle, F-M557).</summary>
    public const string File = "file";

    /// <summary>Der Staffelpack dieser Quelle (F-M390/F-M642).</summary>
    public const string Pack = "pack";

    /// <summary>Alle vier Arten in der Reihenfolge, in der sie berichtet werden.</summary>
    public static readonly string[] All = { Track, TrackHi, File, Pack };

    /// <summary>Die Art einer Spur aus ihrer HI-Angabe.</summary>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <returns><see cref="TrackHi"/> or <see cref="Track"/>.</returns>
    public static string ForTrack(bool hearingImpaired) => hearingImpaired ? TrackHi : Track;
}

/// <summary>
/// F-M667/F-M668: Zugriff auf die Marken-Matrix (<c>source_matrix</c>) — der Schlüssel, die Lese-Fassade
/// und die EINZIGEN Schreibwege für Quellen-Urteile.
/// <para>
/// EINE WAHRHEIT, KEIN RÜCKFALL: seit F-M668 (12.3.0.0) ist die Matrix der einzige Ort, an dem ein
/// Quellen-Urteil steht. Die breiten Spalten, aus denen sie bis Schritt A gefüllt wurde
/// (<c>SubdlUploadedAt</c> … an <c>embeds</c>/<c>sidecars</c>, <c>CompleteSubdl</c> … an <c>media</c>,
/// Ablehnung und Fingerabdruck an <c>packs</c>), sind aus der Live-Datei ENTFERNT; es gibt keinen
/// zweiten Lesepfad und keinen zweiten Schreibpfad. Diese Klasse liest und schreibt ausschließlich die
/// Matrix.
/// </para>
/// <para>
/// WAS EINE ZEILE BEDEUTET: sie steht für ein Urteil der Quelle über (Medium, Sprache, Variante,
/// Position) — <c>track</c>/<c>track-hi</c> für die Spur, <c>file</c> für „diese Datei ist für die
/// Quelle zu", <c>pack</c> für den Staffelbehälter. Fehlt die Zeile, ist die Quelle offen. Seit
/// F-M669 gilt die Spur-Marke JE POSITION: zwei Spuren derselben Sprache sind zwei Marken, denn ein
/// Urteil über die eine sagt nichts über die andere. <c>file</c> und <c>pack</c> tragen keine Position
/// (ihr Urteil gilt der Datei bzw. dem Behälter), ein Seitenwagen die feste <see cref="SidecarPosition"/>.
/// </para>
/// </summary>
public static class SourceMatrix
{
    /// <summary>Quellenname, wie ihn auch <see cref="PackEntity.SubdlSource"/> trägt.</summary>
    public const string Subdl = "subdl";

    /// <summary>Quellenname, wie ihn auch <see cref="PackEntity.TvSubtitlesSource"/> trägt.</summary>
    public const string TvSubtitles = "tvsubtitles";

    /// <summary>Die dritte Quelle. Derselbe Wert wie <c>Api.Endpoints.Provenance.MovieSubtitles</c> — die Store-Schicht nennt ihn selbst, damit sie nicht von der API-Schicht abhängt.</summary>
    public const string MovieSubtitles = "moviesubtitles";

    /// <summary>Alle drei Quellen in der Reihenfolge, in der sie berichtet werden.</summary>
    public static readonly string[] Sources = { Subdl, TvSubtitles, MovieSubtitles };

    /// <summary>
    /// Der Anker einer Marke: der Medien-Hash, sonst die Serie mit ihrer Staffel.
    /// <para>
    /// Der Hash ist die fachliche Kennung des Mediums (F-M194); eine Pack-Zeile hat keine Datei,
    /// sondern eine Serie und eine Staffel — dieselbe Serie kann in jeder Staffel einen Pack haben,
    /// also gehört die Staffel in den Anker und nicht nur in die Zeile.
    /// </para>
    /// </summary>
    /// <param name="mediaHash">Media hash, or null/empty.</param>
    /// <param name="imdbId">Series/movie IMDb id, or null/empty.</param>
    /// <param name="season">Season for a pack anchor, else null.</param>
    /// <returns>The anchor, or an empty string when neither is known.</returns>
    public static string AnchorOf(string? mediaHash, string? imdbId, int? season = null)
    {
        if (!string.IsNullOrWhiteSpace(mediaHash))
        {
            return "h:" + mediaHash;
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            return season == null
                ? "i:" + imdbId
                : "i:" + imdbId + "#S" + season.Value.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        }

        return string.Empty;
    }

    /// <summary>
    /// Der feste Positionswert eines SEITENWAGENS (−1). Ein Seitenwagen ist eine Datei neben dem Video,
    /// keine Spur IM Video — er hat keine ffmpeg-Position. Damit seine Marke trotzdem einen vollständigen
    /// Schlüssel hat und nicht mit der Spur 0 oder einer unbekannten Position kollidiert, trägt sie −1:
    /// ein Wert, den keine echte Spur haben kann (`ffmpeg` zählt ab 0). Zwei Seitenwagen derselben
    /// Sprache an derselben Datei teilen sich diese Marke — sie sind inhaltlich derselbe Fall, und der
    /// Inhalt selbst hängt ohnehin am Content-Hash der Zeile.
    /// </summary>
    public const int SidecarPosition = -1;

    /// <summary>
    /// Die Position, unter der ein Kandidat seine Marke führt: die Videospur-Position einer eingebetteten
    /// Spur, <see cref="SidecarPosition"/> für einen Seitenwagen.
    /// </summary>
    /// <param name="isLoose">True for a sidecar file.</param>
    /// <param name="subPos">Embedded stream position (ignored for a sidecar).</param>
    /// <returns>The position that belongs in the mark's key.</returns>
    public static int PositionOf(bool isLoose, int subPos) => isLoose ? SidecarPosition : subPos;

    /// <summary>
    /// Der Schlüssel einer Marke. EINE Stelle, die ihn baut — ein Schlüssel, den ein Leser anders
    /// baut als ein Schreiber, ist eine Zeile, die niemand findet (F-M194).
    /// </summary>
    /// <param name="anchor">Anchor from <see cref="AnchorOf"/>.</param>
    /// <param name="source">The source.</param>
    /// <param name="kind">One of <see cref="MarkKind"/>.</param>
    /// <param name="language">ISO 639-2 language, empty for <see cref="MarkKind.File"/>.</param>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <param name="subPos">
    /// F-M669: the stream position for <c>track</c>/<c>track-hi</c> (see <see cref="PositionOf"/>);
    /// null for <c>file</c> and <c>pack</c>, whose verdict is not per position.
    /// </param>
    /// <returns>The composite key, or an empty string when the anchor is unknown.</returns>
    public static string KeyOf(
        string anchor, string source, string kind, string? language, bool hearingImpaired, int? subPos = null)
    {
        if (string.IsNullOrEmpty(anchor) || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(kind))
        {
            return string.Empty;
        }

        // Die Sprache wird HIER normiert, nicht beim Aufrufer: ein Aufrufer mit "EN" und ein Aufrufer
        // mit "ENG" meinen dieselbe Marke, und ein Schlüssel, den der eine baut und der andere nicht
        // findet, ist eine Zeile, die niemand liest (F-M194).
        string kopf = string.Join(
            "|",
            anchor,
            source,
            kind,
            Normalise(language),
            hearingImpaired ? "hi" : "plain");

        // F-M669: die Position hängt NUR an den Spur-Arten. Eine `file`- oder `pack`-Marke zerfällt
        // nicht in Positionen — sie gilt für die Datei bzw. den Behälter.
        return subPos.HasValue
            ? kopf + "|" + subPos.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : kopf;
    }

    /// <summary>
    /// Legt die Tabelle an, falls sie fehlt, und zieht die zwei Felder nach, die Schritt B additiv
    /// ergänzt hat (<c>UploadId</c>, <c>RefusalSignature</c>). Additiv — ein Bestand bleibt lesbar.
    /// <para>
    /// Die Anweisungen stehen als Konstante in <see cref="ScribeDbContext"/> und werden HIER
    /// ausgeführt, weil der Migrationslauf sie schon VOR dem Öffnen des Stores braucht: eine Tabelle,
    /// die an einer Stelle angelegt und an einer anderen gelesen wird, ist toter Ballast, der wie ein
    /// funktionierender Speicher aussieht.
    /// </para>
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="logger">Optional logger.</param>
    public static void EnsureTable(ScribeSqliteContext db, ILogger? logger = null)
    {
        try
        {
            db.Database.ExecuteSqlRaw(ScribeDbContext.SourceMatrixDdl);
            foreach (string index in ScribeDbContext.SourceMatrixIndexDdl)
            {
                db.Database.ExecuteSqlRaw(index);
            }

            // F-M668: eine Datei, die die Matrix schon aus Schritt A (Schema 18) trägt, kennt die zwei
            // neuen Felder nicht. SQLite hat kein „ADD COLUMN IF NOT EXISTS", also erst fragen.
            var vorhanden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }

                cmd.CommandText = "PRAGMA table_info(source_matrix);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    vorhanden.Add(reader.GetString(1));
                }
            }

            foreach (var (column, ddl) in new[]
                     {
                         ("UploadId", "ALTER TABLE source_matrix ADD COLUMN UploadId TEXT NULL;"),
                         ("RefusalSignature", "ALTER TABLE source_matrix ADD COLUMN RefusalSignature TEXT NULL;"),
                         // F-M669: die Spur-Position — additiv, null bei file/pack.
                         ("SubPos", "ALTER TABLE source_matrix ADD COLUMN SubPos INTEGER NULL;"),
                     })
            {
                if (!vorhanden.Contains(column))
                {
                    db.Database.ExecuteSqlRaw(ddl);
                    logger?.LogInformation("[Sub Scribe] data file: column {Column} added to source_matrix.", column);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[Sub Scribe] could not create the source_matrix table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>Die Zeile einer Marke, oder null (dann ist die Quelle für dieses Paar offen).</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="id">Key from <see cref="KeyOf"/>.</param>
    /// <returns>The row, or null.</returns>
    public static SourceMatrixEntity? Find(ScribeDbContext db, string id)
        => db == null || string.IsNullOrEmpty(id) ? null : db.Matrix.FindById(id);

    /// <summary>Zählt die Zeilen der Matrix je Art und Quelle — die Prüfgrundlage der Migration.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>"source|kind" → number of rows.</returns>
    public static Dictionary<string, int> CountByKind(ScribeDbContext db)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gruppe in db.Matrix.FindAll()
                     .GroupBy(r => new { r.Source, r.Kind })
                     .Select(g => new { g.Key.Source, g.Key.Kind, N = g.Count() })
                     .ToList())
        {
            zaehler[gruppe.Source + "|" + gruppe.Kind] = gruppe.N;
        }

        return zaehler;
    }

    /// <summary>Alle Marken einer Quelle und Art — die Lesefassung für Karten über ganze Staffeln.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="source">The source, or null for all.</param>
    /// <param name="kind">One of <see cref="MarkKind"/>, or null for all.</param>
    /// <returns>The matching rows.</returns>
    public static List<SourceMatrixEntity> Rows(ScribeDbContext db, string? source, string? kind)
        => db.Matrix.FindAll()
            .Where(r => (source == null || string.Equals(r.Source, source, StringComparison.Ordinal))
                        && (kind == null || string.Equals(r.Kind, kind, StringComparison.Ordinal)))
            .ToList();

    /// <summary>
    /// Die Marke einer SPUR an EINER POSITION: geschlossen heißt „für diese Quelle hochgeladen" (der
    /// Upload-Zeitstempel der Quelle), <c>SourceId</c> ist der Eintrag, aus dem sie kam, <c>UploadId</c>
    /// die Einsendung (F-M669).
    /// <para>
    /// Für einen Seitenwagen ist <paramref name="subPos"/> <see cref="SidecarPosition"/>; die Frage ist
    /// dann die des Seitenwagens, nicht die der Spur 0.
    /// </para>
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="source">The source.</param>
    /// <param name="language">ISO 639-2 language.</param>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <param name="subPos">Stream position (see <see cref="PositionOf"/>).</param>
    /// <returns>The mark; a mark without a timestamp is „open" but may carry an id.</returns>
    public static Mark TrackMarkAt(
        ScribeDbContext db, string? mediaHash, string source, string? language, bool hearingImpaired, int subPos)
    {
        string kind = MarkKind.ForTrack(hearingImpaired);
        string anchor = AnchorOf(mediaHash, null);
        var row = Find(db, KeyOf(anchor, source, kind, language, hearingImpaired, subPos))
            // Brücke für eine Datei aus Schema 19: dort stand EINE Marke je (Medium, Quelle, Sprache,
            // Variante) ohne Position. Sie gilt weiter, bis die nächste Marke die Position nachträgt —
            // sonst wären alle Urteile einer 19er-Datei nach dem Wechsel unsichtbar.
            ?? Find(db, KeyOf(anchor, source, kind, language, hearingImpaired));
        return FromRow(row, source, kind);
    }

    /// <summary>
    /// Die Marke einer SPUR, POSITIONSÜBERGREIFEND: geschlossen, wenn irgendeine Position dieses Paares
    /// geschlossen ist; bei mehreren gewinnt die jüngste (F-M669).
    /// <para>
    /// Diese Fassung ist die PAAR-FRAGE — sie gehört den Aufrufern, die keine Position in der Hand haben
    /// (Seitenwagen-Listen, Inhaltsfragen). Wer eine Position hat, fragt <see cref="TrackMarkAt"/>; sonst
    /// schlösse die Marke einer hochgeladenen Spur die zweite, nie gesendete derselben Sprache mit.
    /// </para>
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="source">The source.</param>
    /// <param name="language">ISO 639-2 language.</param>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <returns>The mark; a mark without a timestamp is „open" but may carry an id.</returns>
    public static Mark TrackMark(
        ScribeDbContext db, string? mediaHash, string source, string? language, bool hearingImpaired)
    {
        string kind = MarkKind.ForTrack(hearingImpaired);
        string anchor = AnchorOf(mediaHash, null);

        // Eine paarweite Zeile aus Schema 19 (ohne Position) hat Vorrang: sie ist die ältere, breitere
        // Aussage und gilt für jede Position.
        var legacy = Find(db, KeyOf(anchor, source, kind, language, hearingImpaired));
        if (legacy != null)
        {
            return FromRow(legacy, source, kind);
        }

        string prefix = KeyOf(anchor, source, kind, language, hearingImpaired);
        if (db == null || string.IsNullOrEmpty(prefix))
        {
            return FromRow(null, source, kind);
        }

        prefix += "|";
        var zeilen = db.Matrix.FindAll()
            .Where(r => r.Id.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (zeilen.Count == 0)
        {
            return FromRow(null, source, kind);
        }

        var beste = zeilen
            .Where(r => r.ClosedAt != null)
            .OrderByDescending(r => r.ClosedAt)
            .FirstOrDefault()
            ?? zeilen[0];
        return FromRow(beste, source, kind);
    }

    /// <summary>
    /// Die Marke einer DATEI: geschlossen heißt „jede vorhandene Dialogspur dieser Datei ist für diese
    /// Quelle entschieden" (der Zugeh-Stempel der Quelle, F-M557). <c>Fingerprint</c> trägt die
    /// Spursignatur, zu der der Stempel gehört (F-M562).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="source">The source.</param>
    /// <returns>The mark.</returns>
    public static Mark FileMark(ScribeDbContext db, string? mediaHash, string source)
    {
        string key = KeyOf(AnchorOf(mediaHash, null), source, MarkKind.File, null, false);
        return FromRow(Find(db, key), source, MarkKind.File);
    }

    /// <summary>
    /// Die Marke eines PACKS: geschlossen heißt „die Quelle hat den Behälter angenommen" (Upload-Beleg);
    /// eine Ablehnung steht daneben und ist kein Urteil (F-M644).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="imdbId">Series IMDb id the pack belongs to.</param>
    /// <param name="season">Season.</param>
    /// <param name="language">ISO 639-2 language of the pack.</param>
    /// <param name="hearingImpaired">True for the SDH pack.</param>
    /// <param name="source">The source.</param>
    /// <returns>The mark.</returns>
    public static Mark PackMark(
        ScribeDbContext db, string? imdbId, int season, string? language, bool hearingImpaired, string source)
    {
        string key = KeyOf(AnchorOf(null, imdbId, season), source, MarkKind.Pack, language, hearingImpaired);
        return FromRow(Find(db, key), source, MarkKind.Pack);
    }

    /// <summary>
    /// Schreibt die Marke EINER SPUR für EINE Quelle. Was leer hereinkommt, löscht nichts: eine Id ohne
    /// Kenntnis überschreibt keine frühere, ein Zeitpunkt ohne Wert bleibt der alte (dieselbe Regel wie
    /// die breiten Spalten vorher).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="media">Die Medienzeile, zu der die Spur gehört (liefert IMDb/TMDb/Staffel/Folge), oder null.</param>
    /// <param name="mediaHash">Der Medien-Hash der Spur.</param>
    /// <param name="source">Die Quelle der Marke.</param>
    /// <param name="language">Sprache der Spur.</param>
    /// <param name="hearingImpaired">HI-Variante der Spur.</param>
    /// <param name="subPos">
    /// F-M669: die Position der Spur (bei einem Seitenwagen <see cref="SidecarPosition"/>). Sie steht im
    /// Schlüssel — zwei Spuren derselben Sprache sind zwei Marken, nicht eine.
    /// </param>
    /// <param name="closedAt">Zeitpunkt der Einsendung, oder null.</param>
    /// <param name="downloadId">Id des Abrufs bei dieser Quelle, oder null.</param>
    /// <param name="uploadId">Id der Einsendung bei dieser Quelle, oder null.</param>
    public static void WriteTrackMark(
        ScribeDbContext db,
        MediaEntity? media,
        string? mediaHash,
        string source,
        string? language,
        bool hearingImpaired,
        int subPos,
        DateTime? closedAt,
        string? downloadId,
        string? uploadId)
    {
        if (subPos < SidecarPosition || string.IsNullOrWhiteSpace(mediaHash)
            || (closedAt == null && string.IsNullOrEmpty(downloadId) && string.IsNullOrEmpty(uploadId)))
        {
            return;
        }

        string kind = MarkKind.ForTrack(hearingImpaired);
        string key = KeyOf(AnchorOf(mediaHash, null), source, kind, language, hearingImpaired, subPos);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var row = db.Matrix.FindById(key) ?? new SourceMatrixEntity
        {
            Id = key,
            MediaHash = mediaHash!,
            Source = source,
            Kind = kind,
            Language = Normalise(language),
        };

        row.SubPos = subPos;

        row.MediaHash = mediaHash!;
        row.ImdbId = media?.ImdbId ?? row.ImdbId;
        row.TmdbId = media?.TmdbId ?? row.TmdbId;
        row.Season = media?.Season ?? row.Season;
        row.Episode = media?.Episode ?? row.Episode;
        row.HearingImpaired = hearingImpaired;

        if (closedAt.HasValue)
        {
            row.ClosedAt = closedAt;
        }

        if (!string.IsNullOrEmpty(downloadId))
        {
            row.SourceId = downloadId;
        }

        if (!string.IsNullOrEmpty(uploadId))
        {
            row.UploadId = uploadId;
        }

        row.Written = DateTime.UtcNow;
        db.Matrix.Upsert(row);
    }

    /// <summary>
    /// Schreibt die Marke einer DATEI für EINE Quelle: „alle Spuren entschieden" plus die Spursignatur,
    /// zu der das Urteil gehört (F-M557/F-M562).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="media">Die Medienzeile, oder null.</param>
    /// <param name="mediaHash">Der Medien-Hash der Datei.</param>
    /// <param name="source">Die Quelle, die fertig ist.</param>
    /// <param name="closedAt">Der Zeitpunkt des Urteils, oder null.</param>
    /// <param name="signature">Die Spursignatur des Durchlaufs, oder null.</param>
    public static void WriteFileMark(
        ScribeDbContext db, MediaEntity? media, string? mediaHash, string source, DateTime? closedAt, string? signature)
    {
        if (string.IsNullOrWhiteSpace(mediaHash) || closedAt == null)
        {
            return;
        }

        string key = KeyOf(AnchorOf(mediaHash, null), source, MarkKind.File, null, false);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var row = db.Matrix.FindById(key) ?? new SourceMatrixEntity
        {
            Id = key,
            MediaHash = mediaHash!,
            Source = source,
            Kind = MarkKind.File,
            Language = string.Empty,
        };

        row.MediaHash = mediaHash!;
        row.ImdbId = media?.ImdbId ?? row.ImdbId;
        row.TmdbId = media?.TmdbId ?? row.TmdbId;
        row.Season = media?.Season ?? row.Season;
        row.Episode = media?.Episode ?? row.Episode;
        row.ClosedAt = closedAt;
        // Die Signatur ist der Fingerabdruck, gegen den dieses Urteil gilt: weicht sie beim nächsten
        // Nachsehen ab, ist die Datei für diese Quelle wieder offen (F-M562).
        row.Fingerprint = signature;

        // Ein Erfolg räumt eine frühere Absage derselben Zeile weg — Beleg und Absage schließen sich aus.
        row.RefusalReason = null;
        row.RefusedAt = null;
        row.RefusalSignature = null;
        row.Written = DateTime.UtcNow;
        db.Matrix.Upsert(row);
    }

    /// <summary>
    /// Schreibt das Urteil eines PACKS: angenommen (mit Behälter-Kennung und Mitglieder-Fingerabdruck)
    /// oder abgelehnt (mit Grund, Datum und der Staffel-Signatur, auf der die Absage beruht).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="imdbId">Serie.</param>
    /// <param name="season">Staffel.</param>
    /// <param name="language">Sprache des Behälters.</param>
    /// <param name="hearingImpaired">HI-Variante des Behälters.</param>
    /// <param name="source">Die Quelle, deren Urteil geschrieben wird.</param>
    /// <param name="closedAt">Zeitpunkt der Annahme, oder null bei einer Absage.</param>
    /// <param name="sourceId">Behälter-Kennung bei der Quelle, oder null.</param>
    /// <param name="fingerprint">Mitglieder-Fingerabdruck, oder null.</param>
    /// <param name="refusalReason">Grund der Absage, oder null.</param>
    /// <param name="refusedAt">Zeitpunkt der Absage, oder null.</param>
    /// <param name="refusalSignature">Staffel-Signatur der Absage, oder null.</param>
    public static void WritePackMark(
        ScribeDbContext db,
        string? imdbId,
        int season,
        string? language,
        bool hearingImpaired,
        string source,
        DateTime? closedAt,
        string? sourceId,
        string? fingerprint,
        string? refusalReason,
        DateTime? refusedAt,
        string? refusalSignature)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || season < 1 || string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        string key = KeyOf(AnchorOf(null, imdbId, season), source, MarkKind.Pack, language, hearingImpaired);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var row = db.Matrix.FindById(key) ?? new SourceMatrixEntity
        {
            Id = key,
            MediaHash = string.Empty,
            ImdbId = imdbId!,
            Source = source,
            Kind = MarkKind.Pack,
            Language = Normalise(language),
            Season = season,
            HearingImpaired = hearingImpaired,
        };

        row.ImdbId = imdbId!;
        row.Season = season;
        row.HearingImpaired = hearingImpaired;

        if (!string.IsNullOrEmpty(sourceId))
        {
            row.SourceId = sourceId;
        }

        // Ein leerer Fingerabdruck überschreibt keinen bekannten: die Zeile behält den letzten Beleg.
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            row.Fingerprint = fingerprint;
        }

        if (refusalReason != null)
        {
            // Absage: kein Urteil, aber Grund, Datum und die Signatur, auf der sie beruht (F-M650).
            row.ClosedAt = null;
            row.RefusalReason = refusalReason;
            row.RefusedAt = refusedAt ?? DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(refusalSignature))
            {
                row.RefusalSignature = refusalSignature;
            }
        }
        else if (closedAt != null)
        {
            // Annahme: das Urteil — und die Absage derselben Zeile fällt (F-M644).
            row.ClosedAt = closedAt;
            row.RefusalReason = null;
            row.RefusedAt = null;
            row.RefusalSignature = null;
        }

        row.Written = DateTime.UtcNow;
        db.Matrix.Upsert(row);
    }

    /// <summary>Nimmt die Zugeh-Stempel einer Datei für ALLE Quellen zurück (Datei-Reset, F-M620).</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Der Medien-Hash der Datei.</param>
    /// <returns>Wie viele Stempel gefallen sind.</returns>
    public static int ClearFileMarks(ScribeDbContext db, string? mediaHash)
    {
        if (string.IsNullOrWhiteSpace(mediaHash))
        {
            return 0;
        }

        int gefallen = 0;
        foreach (string source in Sources)
        {
            string key = KeyOf(AnchorOf(mediaHash, null), source, MarkKind.File, null, false);
            var row = string.IsNullOrEmpty(key) ? null : db.Matrix.FindById(key);
            if (row != null)
            {
                db.Matrix.Delete(row.Id);
                gefallen++;
            }
        }

        return gefallen;
    }

    /// <summary>
    /// Nimmt die Absage eines PACKS zurück (Datei-Reset einer Staffel, F-M620/F-M644) — das Urteil
    /// „angenommen" bleibt stehen, denn ein Beleg ist kein Urteil, das ein Reset wegnehmen darf.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="imdbId">Serie.</param>
    /// <param name="season">Staffel.</param>
    /// <returns>Wie viele Zeilen ihre Absage verloren haben.</returns>
    public static int ClearPackRefusals(ScribeDbContext db, string? imdbId, int season)
    {
        if (string.IsNullOrWhiteSpace(imdbId) || season < 1)
        {
            return 0;
        }

        int geloescht = 0;
        foreach (var row in Rows(db, null, MarkKind.Pack)
                     .Where(x => string.Equals(x.ImdbId, imdbId, StringComparison.Ordinal)
                                 && x.Season == season
                                 && !string.IsNullOrWhiteSpace(x.RefusalReason))
                     .ToList())
        {
            row.RefusalReason = null;
            row.RefusedAt = null;
            row.RefusalSignature = null;
            row.Written = DateTime.UtcNow;
            db.Matrix.Upsert(row);
            geloescht++;
        }

        return geloescht;
    }

    /// <summary>
    /// Schreibt eine Kennung um: die alte Einsende-Id wird zur Eintrags-Id (F-M435), beim Pack die
    /// Behälter-Kennung. Betroffen sind alle Marken, die die alte Kennung tragen.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="oldId">Die gespeicherte Kennung.</param>
    /// <param name="newId">Die Kennung des veröffentlichten Eintrags.</param>
    /// <returns>Wie viele Matrix-Zeilen umgeschrieben wurden.</returns>
    public static int RemapId(ScribeDbContext db, string oldId, string newId)
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId)
            || string.Equals(oldId, newId, StringComparison.Ordinal))
        {
            return 0;
        }

        int geaendert = 0;
        foreach (var row in db.Matrix.FindAll()
                     .Where(x => string.Equals(x.UploadId, oldId, StringComparison.Ordinal)
                                 || string.Equals(x.SourceId, oldId, StringComparison.Ordinal))
                     .ToList())
        {
            if (string.Equals(row.UploadId, oldId, StringComparison.Ordinal))
            {
                row.UploadId = newId;
            }

            if (string.Equals(row.SourceId, oldId, StringComparison.Ordinal))
            {
                row.SourceId = newId;
            }

            row.Written = DateTime.UtcNow;
            db.Matrix.Upsert(row);
            geaendert++;
        }

        return geaendert;
    }

    /// <summary>Normiert eine Sprachangabe so, wie die Matrix sie führt (ISO 639-2, Großbuchstaben).</summary>
    /// <param name="language">Any language code.</param>
    /// <returns>The normalised code, empty for null/blank.</returns>
    public static string Normalise(string? language)
        => Language.LanguageMapper.Canonical(language) ?? (language ?? string.Empty).Trim().ToUpperInvariant();

    private static Mark FromRow(SourceMatrixEntity? row, string source, string kind)
        => row == null
            ? new Mark(source, kind, null, null, null, null, null, null, null, null)
            : new Mark(
                row.Source,
                row.Kind,
                row.ClosedAt,
                row.SourceId,
                row.UploadId,
                row.Fingerprint,
                row.MtimeUnix,
                row.RefusalReason,
                row.RefusedAt,
                row.RefusalSignature);
}

/// <summary>
/// F-M667/F-M668: das Ergebnis EINER Marken-Frage.
/// <para>
/// Es gibt keine zweite Antwortquelle mehr: was hier leer ist, ist in der Matrix leer — und damit für
/// die Quelle offen.
/// </para>
/// </summary>
/// <param name="Source">The source the mark belongs to.</param>
/// <param name="Kind">One of <see cref="MarkKind"/>.</param>
/// <param name="ClosedAt">The verdict "closed", or null for open.</param>
/// <param name="SourceId">The id at that source (the entry), when known.</param>
/// <param name="UploadId">The submission id at that source, when known.</param>
/// <param name="Fingerprint">Content fingerprint (file/pack only).</param>
/// <param name="MtimeUnix">File mtime the fingerprint belongs to (file/pack only).</param>
/// <param name="RefusalReason">Why the source refused, when it did.</param>
/// <param name="RefusedAt">When it refused.</param>
/// <param name="RefusalSignature">The season signature a pack refusal rests on (F-M650).</param>
public sealed record Mark(
    string Source,
    string Kind,
    DateTime? ClosedAt,
    string? SourceId,
    string? UploadId,
    string? Fingerprint,
    long? MtimeUnix,
    string? RefusalReason,
    DateTime? RefusedAt,
    string? RefusalSignature)
{
    /// <summary>
    /// Wahr, wenn die Quelle das Paar geschlossen hat: ein Zeitpunkt ODER eine Einsende-Id. Eine bloße
    /// Abruf-Id (<see cref="SourceId"/>) schließt nichts — sie belegt einen Fund, keine Einsendung.
    /// </summary>
    public bool IsClosed => ClosedAt != null || !string.IsNullOrEmpty(UploadId);
}
