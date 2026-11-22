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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M667: die vier Arten, für die es ein Urteil je Quelle gibt.
/// <para>
/// Die Art ist seit Schema 23 (F-M672) KEIN Feld mehr, sondern eine Eigenschaft des SUBJEKTS: welche
/// Tabelle die Kennung führt, sagt die Ebene. Die Konstanten bleiben, weil Berichte („wie viele Urteile
/// je Art und Quelle") und Prüfungen sie benennen — berechnet werden sie aus dem Subjekt.
/// </para>
/// <para>
/// F-M673: <see cref="TrackHi"/> bleibt eine eigene ART und nicht bloß ein Flag. Ein Urteil über die
/// normale Spur sagt nichts über die HI-Spur, und die Anzeige soll beides auseinanderhalten können —
/// die Spur-Zeile trägt die Variante, die Art sagt, was daraus folgt.
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
/// F-M667/F-M672: der Zugriff auf die Quellen-Tabellen <c>upload</c>, <c>reference</c> und
/// <c>badapple</c> — die Subjekt-Auflösung, die Lese-Fassade und die EINZIGEN Schreibwege.
/// <para>
/// EINE WAHRHEIT, KEIN RÜCKFALL: seit Schema 23 gibt es keine Marken-Matrix und keine breite
/// Quellen-Spalte mehr. Ein Urteil steht in <c>upload</c>, eine fremde Kennung in <c>reference</c>,
/// ein schlechter Download in <c>badapple</c> — und nirgends sonst. Fehlt die Zeile, ist die Quelle
/// offen; das ist die Antwort, nicht ein Rückfall.
/// </para>
/// <para>
/// DAS SUBJEKT IST DIE EIGENE KENNUNG: eine Zeile nennt <c>PackId</c>, <c>FileId</c> oder
/// <c>TrackId</c> — nie einen Hash und nie eine Position. Die Aufrufer der Pipeline arbeiten
/// weiterhin mit (Medien-Hash, Position) beziehungsweise (Serie, Staffel, Sprache, Variante), weil das
/// ihre Adressen sind; die Auflösung in die Kennung steht EINMAL hier (<see cref="SubjectAt"/>,
/// <see cref="SubjectsOf"/>, <see cref="PackSubject"/>), damit ein Leser und ein Schreiber nicht
/// verschiedene Zeilen meinen.
/// </para>
/// <para>
/// EIN SEITENWAGEN TEILT SICH DIE MARKE MIT SEINESGLEICHEN: bis Schema 22 stand die feste Position −1
/// im Schlüssel, also hatten zwei Seitenwagen derselben Sprache EINE Marke. Das bleibt so — beide
/// Seitenwagen der Sprache sind inhaltlich derselbe Fall für die Quelle —, und deshalb schreibt ein
/// Urteil über „den Seitenwagen dieser Sprache" an JEDEN Seitenwagen dieses Paares und liest über alle.
/// </para>
/// </summary>
public static class SourceStore
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
    /// Der feste Positionswert eines SEITENWAGENS (−1). Ein Seitenwagen ist eine Datei neben dem Video,
    /// keine Spur IM Video — er hat keine ffmpeg-Position. Er ist keine Kennung und steht in keiner
    /// Tabelle; er ist die Adresse, unter der die Aufrufer einen Seitenwagen ansprechen.
    /// </summary>
    public const int SidecarPosition = -1;

    /// <summary>
    /// Die Position, unter der ein Kandidat seine Adresse führt: die Videospur-Position einer
    /// eingebetteten Spur, <see cref="SidecarPosition"/> für einen Seitenwagen.
    /// </summary>
    /// <param name="isLoose">True for a sidecar file.</param>
    /// <param name="subPos">Embedded stream position (ignored for a sidecar).</param>
    /// <returns>The position that belongs in the address.</returns>
    public static int PositionOf(bool isLoose, int subPos) => isLoose ? SidecarPosition : subPos;

    /// <summary>
    /// Legt die drei Tabellen an, falls sie fehlen. Der harmlose Weg für eine Datei, die ohne Migration
    /// geöffnet wurde (ausdrückliche Datei) oder deren Aufstieg abgebrochen ist.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="logger">Optional logger.</param>
    public static void EnsureTables(ScribeSqliteContext db, ILogger? logger = null)
    {
        try
        {
            db.Database.ExecuteSqlRaw(ScribeDbContext.UploadTableDdl);
            db.Database.ExecuteSqlRaw(ScribeDbContext.ReferenceTableDdl);
            db.Database.ExecuteSqlRaw(ScribeDbContext.BadAppleTableDdl);
            foreach (string index in ScribeDbContext.SourceTableIndexDdl)
            {
                db.Database.ExecuteSqlRaw(index);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("[Sub Scribe] could not create the source tables (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M450/F-M672: die Melde-Felder an <c>badapple</c> nachziehen. Eine Datei aus Schema 20/21 trägt sie
    /// schon (dort hieß die Tabelle <c>rejected_candidates</c>); der Schritt ist additiv und beliebig oft
    /// wiederholbar, damit eine von Hand gebaute Datei nicht an einer fehlenden Spalte scheitert.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="logger">Optional logger.</param>
    public static void EnsureBadAppleColumns(ScribeSqliteContext db, ILogger? logger = null)
    {
        var vorhanden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            if (cmd!.Connection!.State != System.Data.ConnectionState.Open)
            {
                cmd.Connection.Open();
            }

            cmd.CommandText = "PRAGMA table_info(badapple);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                vorhanden.Add(reader.GetString(1));
            }
        }

        foreach (var (name, ddl) in new[]
                 {
                     ("Finding", "ALTER TABLE badapple ADD COLUMN Finding TEXT NOT NULL DEFAULT '';"),
                     ("NId", "ALTER TABLE badapple ADD COLUMN NId TEXT NOT NULL DEFAULT '';"),
                     ("ReleaseName", "ALTER TABLE badapple ADD COLUMN ReleaseName TEXT NOT NULL DEFAULT '';"),
                     ("ReportState", "ALTER TABLE badapple ADD COLUMN ReportState TEXT NOT NULL DEFAULT 'open';"),
                 })
        {
            if (vorhanden.Count > 0 && !vorhanden.Contains(name))
            {
                db.Database.ExecuteSqlRaw(ddl);
                logger?.LogInformation("[Sub Scribe] data file: column {Column} added to badapple.", name);
            }
        }
    }

    // ------------------------------------------------------------------ Subjekt-Auflösung

    /// <summary>Die Datei zu einem Medien-Hash — der Hash ist die Adresse, unter der sie geführt wird.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash, or null.</param>
    /// <returns>The row, or null.</returns>
    public static FileEntity? FileByHash(ScribeDbContext db, string? mediaHash)
        => db == null || string.IsNullOrWhiteSpace(mediaHash) ? null : db.Files.FindById(mediaHash);

    /// <summary>Die Kennung der Datei, zu der dieser Hash gehört.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash, or null.</param>
    /// <returns>The FileId, or null when the file is unknown.</returns>
    public static string? FileIdOf(ScribeDbContext db, string? mediaHash)
        => FileByHash(db, mediaHash)?.FileId;

    /// <summary>
    /// Das Subjekt an EINER Adresse: die Spur mit dieser Container-Position, oder — bei
    /// <see cref="SidecarPosition"/> — jeder Seitenwagen der Datei. Ein Seitenwagen-Paar hat mehrere
    /// Subjekte (siehe Klassenbeschreibung); der erste wird als der benannte zurückgegeben.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="subPos">Stream position, or <see cref="SidecarPosition"/>.</param>
    /// <returns>The subject id, or null.</returns>
    public static string? SubjectAt(ScribeDbContext db, string? mediaHash, int subPos)
    {
        var subjects = SubjectsAt(db, mediaHash, subPos);
        return subjects.Count == 0 ? null : subjects[0];
    }

    /// <summary>Alle Subjekte an einer Adresse (mehr als eines nur bei einem Seitenwagen-Paar).</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="subPos">Stream position, or <see cref="SidecarPosition"/>.</param>
    /// <returns>The subject ids, never null.</returns>
    public static List<string> SubjectsAt(ScribeDbContext db, string? mediaHash, int subPos)
    {
        var datei = FileByHash(db, mediaHash);
        if (datei == null)
        {
            return new List<string>();
        }

        if (subPos == SidecarPosition)
        {
            return db.Sidecars.FindAll()
                .Where(s => string.Equals(s.FileId, datei.FileId, StringComparison.Ordinal))
                .Select(s => s.TrackId)
                .ToList();
        }

        return db.Tracks.FindAll()
            .Where(t => string.Equals(t.FileId, datei.FileId, StringComparison.Ordinal) && t.SubPos == subPos)
            .Select(t => t.TrackId)
            .ToList();
    }

    /// <summary>F-M676: der erste Seitenwagen einer Datei — die feste Position ist seine Adresse.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the host file.</param>
    /// <returns>The sidecar id, or null.</returns>
    public static string? TrackAtSidecar(ScribeDbContext db, string? mediaHash)
    {
        var subjects = SubjectsAt(db, mediaHash, SidecarPosition);
        return subjects.Count == 0 ? null : subjects[0];
    }

    /// <summary>
    /// Die Subjekte eines PAARES (Datei, Sprache, Variante) — die Adresse der Aufrufer, die keine
    /// Position in der Hand haben. Eingebettete Spuren und Seitenwagen sind zwei Areale; welche gemeint
    /// sind, sagt <paramref name="loose"/>.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="language">Language of the tracks.</param>
    /// <param name="hearingImpaired">HI variant.</param>
    /// <param name="loose">True for sidecars.</param>
    /// <returns>The subject ids, never null.</returns>
    public static List<string> SubjectsOf(
        ScribeDbContext db, string? mediaHash, string? language, bool hearingImpaired, bool loose)
    {
        var datei = FileByHash(db, mediaHash);
        if (datei == null)
        {
            return new List<string>();
        }

        string gesucht = Normalise(language);
        if (loose)
        {
            return db.Sidecars.FindAll()
                .Where(s => string.Equals(s.FileId, datei.FileId, StringComparison.Ordinal)
                            && string.Equals(Normalise(s.Language), gesucht, StringComparison.Ordinal)
                            && (s.HearingImpaired ?? false) == hearingImpaired)
                .Select(s => s.TrackId)
                .ToList();
        }

        return db.Tracks.FindAll()
            .Where(t => string.Equals(t.FileId, datei.FileId, StringComparison.Ordinal)
                        && string.Equals(Normalise(t.Language), gesucht, StringComparison.Ordinal)
                        && (t.HearingImpaired ?? false) == hearingImpaired)
            .Select(t => t.TrackId)
            .ToList();
    }

    /// <summary>
    /// Das Pack, zu dem eine SPUR gehört — über (Serie, Staffel, Sprache, Variante), nicht über die
    /// Datei. Ein Pack pflegt EINE Sprache und EINE Variante; die Spur entscheidet, welcher Behälter
    /// ihrer Staffel gemeint ist.
    /// <para>
    /// Bei mehreren Treffern entscheidet eine feste Reihenfolge (Quelle, Kennung), damit zwei Läufe
    /// dasselbe Ergebnis liefern — dieselbe Regel, die die Übernahme schon hatte.
    /// </para>
    /// </summary>
    /// <param name="file">The track's file, or null.</param>
    /// <param name="language">The track's language.</param>
    /// <param name="hearingImpaired">The track's HI flag.</param>
    /// <param name="packs">The candidate packs (defaults to the store's rows).</param>
    /// <returns>The pack's id, or null when no container covers this pair.</returns>
    public static string? PackForTrack(
        FileEntity? file, string? language, bool hearingImpaired, IEnumerable<PackEntity>? packs = null)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.ImdbId) || file.Season is not > 0)
        {
            return null;
        }

        string gesucht = Normalise(language);
        return (packs ?? Array.Empty<PackEntity>())
            .Where(p => string.Equals(p.ImdbId, file.ImdbId, StringComparison.Ordinal)
                        && p.Season == file.Season
                        && string.Equals(Normalise(p.Language), gesucht, StringComparison.Ordinal)
                        && (p.HearingImpaired ?? false) == hearingImpaired)
            .Where(p => file.Episode == null || Carries(p.Episodes, file.Episode.Value))
            .OrderBy(p => string.IsNullOrWhiteSpace(p.Source) ? PackEntity.SubdlSource : p.Source, StringComparer.Ordinal)
            .ThenBy(p => p.PackId, StringComparer.Ordinal)
            .Select(p => p.PackId)
            .FirstOrDefault();
    }

    /// <summary>Wahr, wenn die Folgenliste des Behälters diese Folge nennt (leere Liste: die ganze Staffel).</summary>
    /// <param name="episodes">Comma separated episode numbers.</param>
    /// <param name="episode">The episode.</param>
    /// <returns>True when the pack carries it.</returns>
    public static bool Carries(string? episodes, int episode)
    {
        if (string.IsNullOrWhiteSpace(episodes))
        {
            // Ein Pack ohne Folgenliste ist die ältere Form: er gilt für die ganze Staffel.
            return true;
        }

        foreach (string teil in episodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(teil, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)
                && n == episode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// F-M677: die SPUREN eines Behälters — sein Array, zeilenweise gelesen. Nur eingebettete Spuren:
    /// aus Seitenwagen werden keine Packs gebaut, eine Mitgliedschaft für einen gäbe es nicht.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="packId">Die Kennung des Behälters (<c>[xxxx]</c>).</param>
    /// <returns>The member track ids.</returns>
    public static List<string> TracksOfPack(ScribeDbContext db, string? packId)
        => db == null || string.IsNullOrWhiteSpace(packId)
            ? new List<string>()
            : db.PackTracks.FindAll()
                .Where(m => string.Equals(m.PackId, packId, StringComparison.Ordinal))
                .Select(m => m.TrackId)
                .ToList();

    /// <summary>
    /// F-M677: die Dateien eines Behälters — über die <c>FileId</c> seiner Spuren, ohne eigene
    /// Zuordnungstabelle. Genau das, was der Betreiber „Mitglieder eines Packs sind über distinct FileId
    /// zu finden" nennt.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="packId">Die Kennung des Behälters.</param>
    /// <returns>The distinct file ids of its members.</returns>
    public static List<string> FilesOfPack(ScribeDbContext db, string? packId)
    {
        var dateien = new HashSet<string>(StringComparer.Ordinal);
        var spuren = db?.Tracks.FindAll().ToList() ?? new List<TrackEntity>();
        foreach (string trackId in TracksOfPack(db, packId))
        {
            var spur = spuren.FirstOrDefault(t => string.Equals(t.TrackId, trackId, StringComparison.Ordinal));
            if (spur != null && !string.IsNullOrEmpty(spur.FileId))
            {
                dateien.Add(spur.FileId);
            }
        }

        return dateien.ToList();
    }

    /// <summary>F-M677: der Behälter, dessen Mitglied diese Spur ist — oder null.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="trackId">Die Kennung der Spur.</param>
    /// <returns>The pack id, or null.</returns>
    public static string? PackOfTrack(ScribeDbContext db, string? trackId)
        => db == null || string.IsNullOrWhiteSpace(trackId)
            ? null
            : db.PackTracks.FindAll()
                .FirstOrDefault(m => string.Equals(m.TrackId, trackId, StringComparison.Ordinal))?.PackId;

    /// <summary>
    /// F-M677: eine Spur als Mitglied eines Behälters eintragen. Eine Zeile, die es schon gibt, bleibt —
    /// eine Mitgliedschaft ist keine Historie.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="packId">Die Kennung des Behälters.</param>
    /// <param name="trackId">Die Kennung der Spur.</param>
    public static void AddMember(ScribeDbContext db, string? packId, string? trackId)
    {
        if (db == null || string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        db.PackTracks.Upsert(new PackTrackEntity { PackId = packId!, TrackId = trackId! });
    }

    /// <summary>Die Kennung des PACKS zu (Serie, Staffel, Sprache, Variante, Quelle) — oder null.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season.</param>
    /// <param name="language">Language of the pack.</param>
    /// <param name="hearingImpaired">HI variant of the pack.</param>
    /// <param name="source">Source that carries the pack.</param>
    /// <returns>The PackId, or null.</returns>
    public static string? PackSubject(
        ScribeDbContext db, string? imdbId, int season, string? language, bool hearingImpaired, string source)
    {
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season < 1 || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        string key = ScribeDbContext.PackKey(imdbId!, season, language!, hearingImpaired, source);
        return db.Packs.FindById(key)?.PackId;
    }

    /// <summary>Die Kennung der DATEI zu einer Jellyfin-Item-Id — die Adresse des Download-Pfades.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="itemId">Jellyfin item id.</param>
    /// <returns>The FileId, or null.</returns>
    public static string? FileSubjectOfItem(ScribeDbContext db, string? itemId)
        => db == null || string.IsNullOrWhiteSpace(itemId)
            ? null
            : db.Files.FindOne(f => string.Equals(f.JellyfinItemId, itemId, StringComparison.Ordinal))?.FileId;

    /// <summary>Normiert eine Sprachangabe so, wie der Store sie vergleicht (ISO 639-2, Großbuchstaben).</summary>
    /// <param name="language">Any language code.</param>
    /// <returns>The normalised code, empty for null/blank.</returns>
    public static string Normalise(string? language)
        => Language.LanguageMapper.Canonical(language) ?? (language ?? string.Empty).Trim().ToUpperInvariant();

    // ------------------------------------------------------------------ Lesen

    /// <summary>Die Zeile einer Marke, oder null. <paramref name="subjectId"/> ist die eigene Kennung.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Subject id (FileId, TrackId or PackId).</param>
    /// <param name="source">The source.</param>
    /// <returns>The merged mark.</returns>
    public static Mark MarkOf(ScribeDbContext db, string? subjectId, string source)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId))
        {
            return Mark.Empty(source, MarkKind.Track);
        }

        var upload = db.Uploads.FindById(subjectId + "|" + source);
        var reference = db.References.FindById(subjectId + "|" + source);
        return Merge(upload, reference, source);
    }

    /// <summary>
    /// Die Marke einer SPUR an EINER POSITION: geschlossen heißt „für diese Quelle hochgeladen",
    /// <c>SourceId</c> ist der Eintrag, aus dem sie kam, <c>UploadId</c> die Einsendung.
    /// <para>Für einen Seitenwagen ist <paramref name="subPos"/> <see cref="SidecarPosition"/>.</para>
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="source">The source.</param>
    /// <param name="language">ISO 639-2 language (only used for a sidecar pair).</param>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <param name="subPos">Stream position (see <see cref="PositionOf"/>).</param>
    /// <returns>The mark; a mark without a timestamp is „open" but may carry an id.</returns>
    public static Mark TrackMarkAt(
        ScribeDbContext db, string? mediaHash, string source, string? language, bool hearingImpaired, int subPos)
    {
        string kind = MarkKind.ForTrack(hearingImpaired);
        if (subPos == SidecarPosition)
        {
            return PairMark(db, SubjectsOf(db, mediaHash, language, hearingImpaired, loose: true), source, kind);
        }

        var subjects = SubjectsAt(db, mediaHash, subPos);
        return subjects.Count == 0
            ? Mark.Empty(source, kind)
            : MarkOf(db, subjects[0], source) with { Kind = kind };
    }

    /// <summary>
    /// Die Marke einer SPUR, POSITIONSÜBERGREIFEND: geschlossen, wenn irgendeine Position dieses Paares
    /// geschlossen ist; bei mehreren gewinnt die jüngste.
    /// <para>
    /// Diese Fassung ist die PAAR-FRAGE — sie gehört den Aufrufern, die keine Position in der Hand haben
    /// (Seitenwagen-Listen, Inhaltsfragen). Wer eine Position hat, fragt <see cref="TrackMarkAt"/>.
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
        var subjects = SubjectsOf(db, mediaHash, language, hearingImpaired, loose: false)
            .Concat(SubjectsOf(db, mediaHash, language, hearingImpaired, loose: true))
            .ToList();
        return PairMark(db, subjects, source, kind);
    }

    private static Mark PairMark(ScribeDbContext db, List<string> subjects, string source, string kind)
    {
        if (subjects.Count == 0)
        {
            return Mark.Empty(source, kind);
        }

        var marks = subjects.Select(s => MarkOf(db, s, source)).ToList();
        var beste = marks.Where(m => m.ClosedAt != null).OrderByDescending(m => m.ClosedAt).FirstOrDefault()
                     ?? marks.FirstOrDefault(m => m.Exists)
                     ?? Mark.Empty(source, kind);
        return beste with { Kind = kind };
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
        string? fileId = FileIdOf(db, mediaHash);
        return fileId == null
            ? Mark.Empty(source, MarkKind.File)
            : MarkOf(db, fileId, source) with { Kind = MarkKind.File };
    }

    /// <summary>
    /// Die Marke eines PACKS: geschlossen heißt „die Quelle hat den Behälter angenommen"; eine Ablehnung
    /// steht daneben und ist kein Urteil (F-M644).
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
        string? packId = PackSubject(db, imdbId, season, language, hearingImpaired, source);
        return packId == null
            ? Mark.Empty(source, MarkKind.Pack)
            : MarkOf(db, packId, source) with { Kind = MarkKind.Pack };
    }

    /// <summary>
    /// Alle Marken der PACKS einer Quelle, jeweils mit der Zeile, die das Subjekt beschreibt — die
    /// Lesefassung für Karten über ganze Staffeln. Eine Marke ohne ihre Pack-Zeile ist keine Aussage
    /// über eine Staffel und wird übersprungen (sie fällt im Database Refresh).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="source">The source, or null for all.</param>
    /// <returns>One entry per pack and source.</returns>
    public static List<(PackEntity Pack, Mark Mark)> PackMarks(ScribeDbContext db, string? source)
    {
        var liste = new List<(PackEntity, Mark)>();
        if (db == null)
        {
            return liste;
        }

        var packs = db.Packs.FindAll().ToList();
        foreach (var upload in db.Uploads.FindAll())
        {
            if (source != null && !string.Equals(upload.Source, source, StringComparison.Ordinal))
            {
                continue;
            }

            var pack = packs.FirstOrDefault(p => string.Equals(p.PackId, upload.SubjectId, StringComparison.Ordinal));
            if (pack == null)
            {
                continue;
            }

            liste.Add((pack, MarkOf(db, upload.SubjectId, upload.Source) with { Kind = MarkKind.Pack }));
        }

        return liste;
    }

    /// <summary>Zählt die Urteile je Art und Quelle — die Prüfgrundlage von Übernahme und Diagnose.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>"source|kind" → number of rows.</returns>
    public static Dictionary<string, int> CountByKind(ScribeDbContext db)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.Ordinal);
        if (db == null)
        {
            return zaehler;
        }

        var files = db.Files.FindAll().Select(f => f.FileId).ToHashSet(StringComparer.Ordinal);
        var tracks = db.Tracks.FindAll().ToDictionary(t => t.TrackId, t => t.HearingImpaired ?? false, StringComparer.Ordinal);
        var sidecars = db.Sidecars.FindAll().Select(s => s.TrackId).ToHashSet(StringComparer.Ordinal);
        var packs = db.Packs.FindAll().Select(p => p.PackId).ToHashSet(StringComparer.Ordinal);

        // F-M672: eine MARKE ist ein (Subjekt, Quelle), zu dem es irgendeine Zeile gibt — ein Urteil
        // (`upload`) ODER einen Beleg (`reference`) ODER einen schlechten Download. Wer nur `upload`
        // zählt, übersieht genau die Marken, die allein eine Kennung tragen (die Alt-Tabelle
        // `source_marks` schrieb solche), und meldet nach der Übernahme weniger Marken als vorher.
        var marken = db.Uploads.FindAll().Select(r => (r.SubjectId, r.Source)).ToList();
        marken.AddRange(db.References.FindAll().Select(r => (r.SubjectId, r.Source)));
        marken.AddRange(db.BadApples.FindAll().Select(r => (r.SubjectId, r.Source)));

        foreach (var (subject, source) in marken.Distinct())
        {
            var row = (SubjectId: subject, Source: source);
            string kind = packs.Contains(row.SubjectId)
                ? MarkKind.Pack
                : files.Contains(row.SubjectId)
                    ? MarkKind.File
                    : sidecars.Contains(row.SubjectId)
                        ? MarkKind.ForTrack(false)
                        : tracks.TryGetValue(row.SubjectId, out bool hi)
                            ? MarkKind.ForTrack(hi)
                            : MarkKind.Track;

            string key = row.Source + "|" + kind;
            zaehler[key] = zaehler.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        return zaehler;
    }

    /// <summary>
    /// Alle Kennungen, die ein URTEIL dieser Quelle trägt (Eintrag und Einsendung) — die Frage „welche
    /// Einzel-Uploads hängen an diesen Zeilen noch?".
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="source">The source, or null for all.</param>
    /// <returns>The ids, non-empty only.</returns>
    public static HashSet<string> TrackIds(ScribeDbContext db, string? source)
    {
        var spurSubjekte = new HashSet<string>(db.Tracks.FindAll().Select(t => t.TrackId), StringComparer.Ordinal);
        spurSubjekte.UnionWith(db.Sidecars.FindAll().Select(s => s.TrackId));

        return db.References.FindAll()
            .Where(r => (source == null || string.Equals(r.Source, source, StringComparison.Ordinal))
                        && spurSubjekte.Contains(r.SubjectId))
            .SelectMany(r => new[] { r.DownloadSourceId, r.UploadedId })
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Wie viele Belege dieser Quelle überhaupt eine Kennung führen — die Zahl der Diagnose.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="source">The source, or null for all.</param>
    /// <returns>Number of reference rows carrying an id.</returns>
    public static int CountWithId(ScribeDbContext db, string? source)
        => db.References.FindAll()
            .Where(r => source == null || string.Equals(r.Source, source, StringComparison.Ordinal))
            .Count(r => !string.IsNullOrEmpty(r.DownloadSourceId) || !string.IsNullOrEmpty(r.UploadedId));

    // ------------------------------------------------------------------ Schreiben

    /// <summary>
    /// Schreibt die Marke EINER SPUR für EINE Quelle. Was leer hereinkommt, löscht nichts: eine Id ohne
    /// Kenntnis überschreibt keine frühere, ein Zeitpunkt ohne Wert bleibt der alte.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="media">Die Medienzeile, zu der die Spur gehört (nur noch für die Adresse), oder null.</param>
    /// <param name="mediaHash">Der Medien-Hash der Spur.</param>
    /// <param name="source">Die Quelle der Marke.</param>
    /// <param name="language">Sprache der Spur.</param>
    /// <param name="hearingImpaired">HI-Variante der Spur.</param>
    /// <param name="subPos">Die Position der Spur (bei einem Seitenwagen <see cref="SidecarPosition"/>).</param>
    /// <param name="closedAt">Zeitpunkt der Einsendung, oder null.</param>
    /// <param name="downloadId">Id des Abrufs bei dieser Quelle, oder null.</param>
    /// <param name="uploadId">Id der Einsendung bei dieser Quelle, oder null.</param>
    public static void WriteTrackMark(
        ScribeDbContext db,
        FileEntity? media,
        string? mediaHash,
        string source,
        string? language,
        bool hearingImpaired,
        int subPos,
        DateTime? closedAt,
        string? downloadId,
        string? uploadId)
    {
        if (db == null || string.IsNullOrWhiteSpace(mediaHash)
            || (closedAt == null && string.IsNullOrEmpty(downloadId) && string.IsNullOrEmpty(uploadId)))
        {
            return;
        }

        var subjects = subPos == SidecarPosition
            ? SubjectsOf(db, mediaHash, language, hearingImpaired, loose: true)
            : SubjectsAt(db, mediaHash, subPos);

        foreach (string subject in subjects)
        {
            if (closedAt.HasValue)
            {
                var upload = db.Uploads.FindById(subject + "|" + source) ?? NewUpload(subject, source);
                upload.UploadedAt = closedAt;
                upload.Written = DateTime.UtcNow;
                db.Uploads.Upsert(upload);
            }

            if (!string.IsNullOrEmpty(downloadId) || !string.IsNullOrEmpty(uploadId))
            {
                var reference = db.References.FindById(subject + "|" + source) ?? NewReference(subject, source);
                if (!string.IsNullOrEmpty(downloadId))
                {
                    reference.DownloadSourceId = downloadId;
                }

                if (!string.IsNullOrEmpty(uploadId))
                {
                    reference.UploadedId = uploadId;
                }

                reference.Written = DateTime.UtcNow;
                db.References.Upsert(reference);
            }
        }
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
        ScribeDbContext db, FileEntity? media, string? mediaHash, string source, DateTime? closedAt, string? signature)
    {
        string? fileId = media?.FileId ?? FileIdOf(db, mediaHash);
        if (db == null || fileId == null || closedAt == null)
        {
            return;
        }

        var upload = db.Uploads.FindById(fileId + "|" + source) ?? NewUpload(fileId, source);
        upload.UploadedAt = closedAt;
        // Die Signatur ist der Fingerabdruck, gegen den dieses Urteil gilt: weicht sie beim nächsten
        // Nachsehen ab, ist die Datei für diese Quelle wieder offen (F-M562).
        upload.Fingerprint = signature;

        // Ein Erfolg räumt eine frühere Absage derselben Zeile weg — Beleg und Absage schließen sich aus.
        upload.RefusedAt = null;
        upload.RefusedReason = null;
        upload.RefusalSignature = null;
        upload.Written = DateTime.UtcNow;
        db.Uploads.Upsert(upload);
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
        string? packId = PackSubject(db, imdbId, season, language, hearingImpaired, source);
        if (db == null || packId == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(sourceId))
        {
            var reference = db.References.FindById(packId + "|" + source) ?? NewReference(packId, source);
            reference.DownloadSourceId = sourceId;
            reference.Written = DateTime.UtcNow;
            db.References.Upsert(reference);
        }

        var upload = db.Uploads.FindById(packId + "|" + source) ?? NewUpload(packId, source);

        // Ein leerer Fingerabdruck überschreibt keinen bekannten: die Zeile behält den letzten Beleg.
        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            upload.Fingerprint = fingerprint;
        }

        if (refusalReason != null)
        {
            // Absage: kein Urteil, aber Grund, Datum und die Signatur, auf der sie beruht (F-M650).
            upload.UploadedAt = null;
            upload.RefusedReason = refusalReason;
            upload.RefusedAt = refusedAt ?? DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(refusalSignature))
            {
                upload.RefusalSignature = refusalSignature;
            }
        }
        else if (closedAt != null)
        {
            // Annahme: das Urteil — und die Absage derselben Zeile fällt (F-M644).
            upload.UploadedAt = closedAt;
            upload.RefusedReason = null;
            upload.RefusedAt = null;
            upload.RefusalSignature = null;
        }

        upload.Written = DateTime.UtcNow;
        db.Uploads.Upsert(upload);
    }

    /// <summary>
    /// F-M435/F-M670: trägt eine später bekannt gewordene Id der EINSENDUNG an die Marke dieser Spur
    /// nach. Der Upload setzt die Marke ohne Id (die Antwort nennt keine, F-M588); am Laufende holt EIN
    /// Listenabruf die Ids und schreibt sie hierher. Es gibt keinen zweiten Ort, an dem sie stehen
    /// könnte.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the row.</param>
    /// <param name="subPos">Stream position (a sidecar passes <see cref="SidecarPosition"/>).</param>
    /// <param name="language">Language of the row (for a sidecar pair).</param>
    /// <param name="hearingImpaired">HI flag of the row.</param>
    /// <param name="source">The source.</param>
    /// <param name="uploadId">The submission id at that source.</param>
    /// <returns>Number of rows that received the id (0 = nothing to patch).</returns>
    public static int SetDeferredUploadId(
        ScribeDbContext db,
        string? mediaHash,
        int subPos,
        string? language,
        bool hearingImpaired,
        string source,
        string? uploadId)
    {
        if (db == null || string.IsNullOrWhiteSpace(uploadId) || string.IsNullOrWhiteSpace(mediaHash))
        {
            return 0;
        }

        var subjects = subPos == SidecarPosition
            ? SubjectsOf(db, mediaHash, language, hearingImpaired, loose: true)
            : SubjectsAt(db, mediaHash, subPos);

        int geschrieben = 0;
        foreach (string subject in subjects)
        {
            var reference = db.References.FindById(subject + "|" + source) ?? NewReference(subject, source);
            reference.UploadedId = uploadId;
            reference.Written = DateTime.UtcNow;
            db.References.Upsert(reference);
            geschrieben++;
        }

        return geschrieben;
    }

    /// <summary>Nimmt die Zugeh-Stempel einer Datei für ALLE Quellen zurück (Datei-Reset, F-M620).</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Der Medien-Hash der Datei.</param>
    /// <returns>Wie viele Stempel gefallen sind.</returns>
    public static int ClearFileMarks(ScribeDbContext db, string? mediaHash)
    {
        string? fileId = FileIdOf(db, mediaHash);
        if (db == null || fileId == null)
        {
            return 0;
        }

        int gefallen = 0;
        foreach (string source in Sources)
        {
            var upload = db.Uploads.FindById(fileId + "|" + source);
            if (upload != null)
            {
                db.Uploads.Delete(upload.Key);
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
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season < 1)
        {
            return 0;
        }

        var packs = db.Packs.FindAll()
            .Where(p => string.Equals(p.ImdbId, imdbId, StringComparison.Ordinal) && p.Season == season)
            .Select(p => p.PackId)
            .ToHashSet(StringComparer.Ordinal);

        int geloescht = 0;
        foreach (var upload in db.Uploads.FindAll()
                     .Where(x => packs.Contains(x.SubjectId) && !string.IsNullOrWhiteSpace(x.RefusedReason))
                     .ToList())
        {
            upload.RefusedReason = null;
            upload.RefusedAt = null;
            upload.RefusalSignature = null;
            upload.Written = DateTime.UtcNow;
            db.Uploads.Upsert(upload);
            geloescht++;
        }

        return geloescht;
    }

    /// <summary>
    /// Schreibt eine fremde Kennung um: die alte Einsende-Id wird zur Eintrags-Id (F-M435). Betroffen
    /// sind alle Belege, die die alte Kennung tragen — beide Zellen, weil die zwei Nummernkreise
    /// derselben Einsendung beide wandern.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="oldId">Die gespeicherte Kennung.</param>
    /// <param name="newId">Die Kennung des veröffentlichten Eintrags.</param>
    /// <returns>Wie viele Belege umgeschrieben wurden.</returns>
    public static int RemapId(ScribeDbContext db, string oldId, string newId)
    {
        if (db == null || string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(newId)
            || string.Equals(oldId, newId, StringComparison.Ordinal))
        {
            return 0;
        }

        int geaendert = 0;
        foreach (var row in db.References.FindAll()
                     .Where(x => string.Equals(x.UploadedId, oldId, StringComparison.Ordinal)
                                 || string.Equals(x.DownloadSourceId, oldId, StringComparison.Ordinal))
                     .ToList())
        {
            if (string.Equals(row.UploadedId, oldId, StringComparison.Ordinal))
            {
                row.UploadedId = newId;
            }

            if (string.Equals(row.DownloadSourceId, oldId, StringComparison.Ordinal))
            {
                row.DownloadSourceId = newId;
            }

            row.Written = DateTime.UtcNow;
            db.References.Upsert(row);
            geaendert++;
        }

        return geaendert;
    }

    // ------------------------------------------------------------------ schlechte Downloads

    /// <summary>
    /// Merkt einen schlechten Download: die Nummer des fremden Eintrags, der Grund und alles, was eine
    /// Meldung an die Quelle braucht. Eine Zeile je (Subjekt, Quelle, fremde Kennung) — ein zweites
    /// Melden desselben Eintrags ist eine Aktualisierung, keine zweite Zeile.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Die Datei, für die der Eintrag geholt wurde.</param>
    /// <param name="source">Die Quelle des Eintrags.</param>
    /// <param name="sourceId">Die Nummer des fremden Eintrags.</param>
    /// <param name="language">Die Sprache, für die er geholt wurde.</param>
    /// <param name="reason">Warum er verworfen wurde.</param>
    /// <param name="finding">Der Befund (kann leer bleiben).</param>
    /// <param name="nId">Die Melde-Id bei der Quelle (kann leer bleiben).</param>
    /// <param name="releaseName">Der Releasename, wie die Quelle ihn führt (kann leer bleiben).</param>
    /// <param name="kind">qa oder empty.</param>
    /// <param name="reportedAt">Wann gemeldet wurde, oder null.</param>
    /// <returns>Die geschriebene Zeile, oder null wenn kein Subjekt bekannt ist.</returns>
    public static BadAppleEntity? RecordBadApple(
        ScribeDbContext db,
        string? subjectId,
        string source,
        string sourceId,
        string? language,
        string reason,
        string? finding = null,
        string? nId = null,
        string? releaseName = null,
        string kind = "qa",
        DateTime? reportedAt = null)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(sourceId))
        {
            return null;
        }

        string key = subjectId + "|" + source + "|" + sourceId;
        var row = db.BadApples.FindById(key) ?? new BadAppleEntity
        {
            SubjectId = subjectId!,
            Source = source,
            SourceId = sourceId,
        };

        row.Language = (language ?? string.Empty).ToUpperInvariant();
        row.Reason = reason ?? string.Empty;
        if (!string.IsNullOrEmpty(finding))
        {
            row.Finding = finding!;
        }

        if (!string.IsNullOrEmpty(nId))
        {
            row.NId = nId!;
        }

        if (!string.IsNullOrEmpty(releaseName))
        {
            row.ReleaseName = releaseName!;
        }

        row.Kind = string.IsNullOrEmpty(kind) ? "qa" : kind;

        // F-M450: ein einmal gemeldeter Eintrag bleibt gemeldet — ein weiterer Fehlschlag darf
        // „schon gemeldet" nicht löschen, sonst ginge dieselbe Meldung immer wieder hinaus.
        if (reportedAt.HasValue)
        {
            row.ReportedAt = reportedAt;
        }

        row.ReportState = row.ReportedAt.HasValue
            ? BadAppleEntity.ReportStateReported
            : BadAppleEntity.ReportStateOpen;
        row.Written = DateTime.UtcNow;
        db.BadApples.Upsert(row);
        return row;
    }

    /// <summary>Die gemerkten schlechten Downloads einer Datei und Sprache.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Die Datei.</param>
    /// <param name="language">Die Sprache (Vergleich ohne Rücksicht auf Schreibung, wie bisher).</param>
    /// <returns>The rows.</returns>
    public static List<BadAppleEntity> BadApplesOf(ScribeDbContext db, string? subjectId, string? language)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId))
        {
            return new List<BadAppleEntity>();
        }

        string gesucht = (language ?? string.Empty).ToUpperInvariant();
        return db.BadApples.FindAll()
            .Where(x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal)
                        && string.Equals(x.Language, gesucht, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Merkt, dass die Quelle die Meldung angenommen hat — ein zweites Melden gibt es nicht.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="row">The row that was accepted by the source.</param>
    public static void MarkBadAppleReported(ScribeDbContext db, BadAppleEntity row)
    {
        if (db == null || row == null)
        {
            return;
        }

        row.ReportedAt = DateTime.UtcNow;
        row.ReportState = BadAppleEntity.ReportStateReported;
        row.Written = DateTime.UtcNow;
        db.BadApples.Upsert(row);
    }

    /// <summary>Nimmt die schlechten Downloads einer Datei und Sprache zurück (nach einem echten Treffer).</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Die Datei.</param>
    /// <param name="language">Die Sprache, exakt wie gespeichert (Großbuchstaben).</param>
    /// <returns>Number of removed rows.</returns>
    public static int ClearBadApples(ScribeDbContext db, string? subjectId, string? language)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId))
        {
            return 0;
        }

        string gesucht = (language ?? string.Empty).ToUpperInvariant();
        return db.BadApples.DeleteMany(
            x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal) && x.Language == gesucht);
    }

    /// <summary>
    /// Räumt Marken und schlechte Downloads ab, deren Subjekt es nicht mehr gibt — die Gegenprobe zu
    /// „keine Marke ohne Subjekt". Ein Pack, dessen Zeile der Refresh entfernt hat, nimmt seine Urteile
    /// mit; eine Spur, die aus dem Container verschwunden ist, ebenso.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>Number of removed rows.</returns>
    public static int PruneOrphanSubjects(ScribeDbContext db)
    {
        if (db == null)
        {
            return 0;
        }

        var bekannt = new HashSet<string>(db.Files.FindAll().Select(f => f.FileId), StringComparer.Ordinal);
        bekannt.UnionWith(db.Tracks.FindAll().Select(t => t.TrackId));
        bekannt.UnionWith(db.Sidecars.FindAll().Select(s => s.TrackId));
        bekannt.UnionWith(db.Packs.FindAll().Select(p => p.PackId));

        int entfernt = 0;
        // F-M677: eine Mitgliedschaft ohne Behälter oder ohne Spur ist keine — die Fremdschlüssel der
        // Tabelle sagen dasselbe, das Aufräumen hält es auch dann wahr, wenn eine Zeile daneben entstand.
        entfernt += db.PackTracks.DeleteMany(x => !bekannt.Contains(x.PackId) || !bekannt.Contains(x.TrackId));
        entfernt += db.Uploads.DeleteMany(x => !bekannt.Contains(x.SubjectId));
        entfernt += db.References.DeleteMany(x => !bekannt.Contains(x.SubjectId));
        entfernt += db.BadApples.DeleteMany(x => !bekannt.Contains(x.SubjectId));
        return entfernt;
    }

    // ------------------------------------------------------------------ Werkzeug

    /// <summary>
    /// F-M672: die fremde Id, die bis Schema 22 an der Zeile stand (<c>OriginId</c>), aus dem Beleg
    /// lesen. Sie ist nur noch über (Subjekt, Quelle) erreichbar — der Quellenname kommt aus dem Feld
    /// <c>Origin</c>, das die Herkunft nennt.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Die Kennung der Spur oder des Seitenwagens.</param>
    /// <param name="origin">Der Quellenname aus der Zeile (<c>subdl</c>, <c>tvsubtitles</c>, …).</param>
    /// <returns>The foreign id, or null.</returns>
    public static string? OriginIdOf(ScribeDbContext db, string? subjectId, string? origin)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(origin))
        {
            return null;
        }

        return db.References.FindById(subjectId + "|" + SourceNameOf(origin))?.DownloadSourceId;
    }

    /// <summary>
    /// F-M672: die fremde Id eines Fundes an den Beleg dieser Spur schreiben — der Weg, den vorher die
    /// Spalte <c>OriginId</c> ging. Was leer hereinkommt, löscht nichts.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Die Kennung der Spur oder des Seitenwagens.</param>
    /// <param name="origin">Der Quellenname aus der Zeile.</param>
    /// <param name="originId">Die fremde Id.</param>
    public static void SetDownloadSourceId(ScribeDbContext db, string? subjectId, string? origin, string? originId)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(originId)
            || string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        string source = SourceNameOf(origin);
        var row = db.References.FindById(subjectId + "|" + source) ?? NewReference(subjectId!, source);
        row.DownloadSourceId = originId;
        row.Written = DateTime.UtcNow;
        db.References.Upsert(row);
    }

    /// <summary>
    /// F-M672: alles vergessen, was über ein Subjekt gesagt wurde — Urteil, Beleg und schlechter
    /// Download. Der Weg für ein Subjekt, das es nicht mehr gibt; „keine Marke ohne Subjekt".
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="subjectId">Die Kennung, die verschwindet.</param>
    /// <returns>Number of removed rows.</returns>
    public static int ForgetSubject(ScribeDbContext db, string? subjectId)
    {
        if (db == null || string.IsNullOrWhiteSpace(subjectId))
        {
            return 0;
        }

        int entfernt = 0;
        entfernt += db.PackTracks.DeleteMany(
            x => string.Equals(x.TrackId, subjectId, StringComparison.Ordinal)
                 || string.Equals(x.PackId, subjectId, StringComparison.Ordinal));
        entfernt += db.Uploads.DeleteMany(x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal));
        entfernt += db.References.DeleteMany(x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal));
        entfernt += db.BadApples.DeleteMany(x => string.Equals(x.SubjectId, subjectId, StringComparison.Ordinal));
        return entfernt;
    }

    /// <summary>Der Quellenname aus dem Feld <c>Origin</c> einer Zeile, als Wert der Quellen-Tabellen.</summary>
    /// <param name="origin">The stored origin name.</param>
    /// <returns>One of <see cref="Sources"/>.</returns>
    public static string SourceNameOf(string origin)
    {
        if (origin.Contains("tvsub", StringComparison.OrdinalIgnoreCase))
        {
            return TvSubtitles;
        }

        if (origin.Contains("movie", StringComparison.OrdinalIgnoreCase)
            || origin.Contains("msub", StringComparison.OrdinalIgnoreCase))
        {
            return MovieSubtitles;
        }

        return Subdl;
    }

    private static UploadEntity NewUpload(string subjectId, string source)
        => new() { SubjectId = subjectId, Source = source, Written = DateTime.UtcNow };

    private static ReferenceEntity NewReference(string subjectId, string source)
        => new() { SubjectId = subjectId, Source = source, Written = DateTime.UtcNow };

    private static Mark Merge(UploadEntity? upload, ReferenceEntity? reference, string source)
        => new Mark(
            source,
            MarkKind.Track,
            upload?.UploadedAt,
            reference?.DownloadSourceId,
            reference?.UploadedId,
            upload?.Fingerprint,
            upload?.MtimeUnix,
            upload?.RefusedReason,
            upload?.RefusedAt,
            upload?.RefusalSignature)
        {
            Exists = upload != null || reference != null,
        };
}

/// <summary>
/// F-M667/F-M672: das Ergebnis EINER Marken-Frage.
/// <para>
/// Es gibt keine zweite Antwortquelle: was hier leer ist, steht in keiner der drei Tabellen — und damit
/// ist die Quelle offen. Die Felder sind dieselben wie vor Schema 23, weil die Aufrufer dieselben Fragen
/// stellen: das Urteil (geschlossen, abgelehnt) und die Kennungen des Belegs.
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
    /// <summary>Wahr, wenn es zu diesem Subjekt und dieser Quelle überhaupt eine Zeile gibt.</summary>
    public bool Exists { get; init; }

    /// <summary>
    /// Wahr, wenn die Quelle das Paar geschlossen hat: ein Zeitpunkt ODER eine Einsende-Id. Eine bloße
    /// Abruf-Id (<see cref="SourceId"/>) schließt nichts — sie belegt einen Fund, keine Einsendung.
    /// </summary>
    public bool IsClosed => ClosedAt != null || !string.IsNullOrEmpty(UploadId);

    /// <summary>Eine leere Marke — die Antwort „diese Quelle ist offen".</summary>
    /// <param name="source">The source.</param>
    /// <param name="kind">One of <see cref="MarkKind"/>.</param>
    /// <returns>An empty mark.</returns>
    public static Mark Empty(string source, string kind)
        => new(source, kind, null, null, null, null, null, null, null, null);
}
