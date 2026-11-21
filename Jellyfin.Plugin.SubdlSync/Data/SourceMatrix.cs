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
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M667: die vier Arten, für die es ein Urteil je Quelle gibt.
/// <para>
/// Die HI-Variante ist eine eigene ART und kein Feld: ein Urteil über die normale Spur sagt nichts
/// über die HI-Spur, und in der Matrix ist die Art der billigste Ort, das auseinanderzuhalten —
/// dieselbe Entscheidung, die die beiden Spaltenpaare heute schon je Richtung treffen.
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
/// F-M667: Zugriff auf die Marken-Matrix (<c>source_matrix</c>) — der Schlüssel, die eine
/// Lese-Fassade und der Rückfall auf die breiten Spalten.
/// <para>
/// ZWEI LESEWEGE, EIN ERGEBNIS: die Matrix ist die Ziel-Struktur; solange Schritt B die Schreiber
/// nicht umgestellt hat, kann eine Marke dort fehlen, die in den alten Spalten steht. Jede Frage
/// hier liest deshalb die Matrix und fällt — NUR wenn dort keine Zeile steht — auf die alten
/// Spalten zurück (<c>FromMatrix</c> sagt, wer geantwortet hat). Das ist die Rückfallebene, von der
/// F-M327 spricht; sie verschwindet mit Schritt B.
/// </para>
/// <para>
/// KEIN ZWEITER SCHREIBPFAD: in diesem Schritt schreibt nur der Migrationslauf
/// (<see cref="SourceMatrixMigration"/>) in die Tabelle. Diese Klasse liest und legt die Tabelle an.
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
    /// Der Schlüssel einer Marke. EINE Stelle, die ihn baut — ein Schlüssel, den ein Leser anders
    /// baut als ein Schreiber, ist eine Zeile, die niemand findet (F-M194).
    /// </summary>
    /// <param name="anchor">Anchor from <see cref="AnchorOf"/>.</param>
    /// <param name="source">The source.</param>
    /// <param name="kind">One of <see cref="MarkKind"/>.</param>
    /// <param name="language">ISO 639-2 language, empty for <see cref="MarkKind.File"/>.</param>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <returns>The composite key, or an empty string when the anchor is unknown.</returns>
    public static string KeyOf(string anchor, string source, string kind, string? language, bool hearingImpaired)
    {
        if (string.IsNullOrEmpty(anchor) || string.IsNullOrEmpty(source) || string.IsNullOrEmpty(kind))
        {
            return string.Empty;
        }

        // Die Sprache wird HIER normiert, nicht beim Aufrufer: ein Aufrufer mit "EN" und ein Aufrufer
        // mit "ENG" meinen dieselbe Marke, und ein Schlüssel, den der eine baut und der andere nicht
        // findet, ist eine Zeile, die niemand liest (F-M194).
        return string.Join(
            "|",
            anchor,
            source,
            kind,
            Normalise(language),
            hearingImpaired ? "hi" : "plain");
    }

    /// <summary>
    /// Legt die Tabelle an, falls sie fehlt. Additiv — ein Bestand bleibt lesbar (F-M327).
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
    public static SourceMatrixEntity? Find(ScribeSqliteContext db, string id)
    {
        if (db == null || string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            lock (db.Gate)
            {
                return db.SourceMatrixRows.AsNoTracking().FirstOrDefault(r => r.Id == id);
            }
        }
        catch (Exception)
        {
            // Ein Store, der nicht antwortet, heißt „keine Zeile" — und dann antwortet der Rückfall.
            return null;
        }
    }

    /// <summary>Zählt die Zeilen der Matrix je Art und Quelle — die Prüfgrundlage der Migration.</summary>
    /// <param name="db">The plugin's data context.</param>
    /// <returns>"source|kind" → number of rows.</returns>
    public static Dictionary<string, int> CountByKind(ScribeSqliteContext db)
    {
        var zaehler = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gruppe in db.SourceMatrixRows.AsNoTracking()
                     .GroupBy(r => new { r.Source, r.Kind })
                     .Select(g => new { g.Key.Source, g.Key.Kind, N = g.Count() })
                     .ToList())
        {
            zaehler[gruppe.Source + "|" + gruppe.Kind] = gruppe.N;
        }

        return zaehler;
    }

    /// <summary>
    /// Die Marke einer SPUR: geschlossen heißt „für diese Quelle hochgeladen" (der Upload-Zeitstempel
    /// der Quelle), <c>SourceId</c> ist der Eintrag, aus dem sie kam.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="source">The source.</param>
    /// <param name="language">ISO 639-2 language.</param>
    /// <param name="hearingImpaired">True for the SDH variant.</param>
    /// <returns>The mark; <c>FromMatrix=false</c> means the old columns answered.</returns>
    public static Mark TrackMark(
        ScribeSqliteContext db, string? mediaHash, string source, string? language, bool hearingImpaired)
    {
        string kind = MarkKind.ForTrack(hearingImpaired);
        string key = KeyOf(AnchorOf(mediaHash, null), source, kind, Normalise(language), hearingImpaired);
        var zeile = Find(db, key);
        if (zeile != null)
        {
            return Mark.FromRow(zeile);
        }

        // Rückfall: die breiten Spalten der Spur-Zeilen (F-M327). Zwei Spuren einer Sprache sind zwei
        // Zeilen — die Frage ist die nach dem Paar, also gewinnt der späteste Zeitstempel.
        DateTime? geschlossen = null;
        string? quelle = null;
        string iso = Normalise(language);
        foreach (var spur in TrackRows(db, mediaHash))
        {
            if (!SpracheGleich(spur.Language, iso) || (spur.HearingImpaired != null && spur.HearingImpaired != hearingImpaired))
            {
                continue;
            }

            var (zeit, id) = spur.For(source);
            if (zeit != null && (geschlossen == null || zeit > geschlossen))
            {
                geschlossen = zeit;
            }

            if (quelle == null && !string.IsNullOrEmpty(id))
            {
                quelle = id;
            }
        }

        return new Mark(source, kind, geschlossen, quelle, null, null, null, null, false);
    }

    /// <summary>
    /// Die Marke einer DATEI: geschlossen heißt „jede vorhandene Dialogspur dieser Datei ist für diese
    /// Quelle entschieden" (der Zugeh-Stempel der Quelle, F-M557).
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    /// <param name="mediaHash">Media hash of the file.</param>
    /// <param name="source">The source.</param>
    /// <returns>The mark.</returns>
    public static Mark FileMark(ScribeSqliteContext db, string? mediaHash, string source)
    {
        string key = KeyOf(AnchorOf(mediaHash, null), source, MarkKind.File, null, false);
        var zeile = Find(db, key);
        if (zeile != null)
        {
            return Mark.FromRow(zeile);
        }

        var media = string.IsNullOrEmpty(mediaHash)
            ? null
            : db.MediaRows.AsNoTracking().FirstOrDefault(m => m.Id == mediaHash);
        DateTime? geschlossen = source switch
        {
            Subdl => media?.CompleteSubdl,
            TvSubtitles => media?.CompleteTvSubtitles,
            MovieSubtitles => media?.CompleteMsub,
            _ => null,
        };

        return new Mark(source, MarkKind.File, geschlossen, null, null, null, null, null, false);
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
        ScribeSqliteContext db, string? imdbId, int season, string? language, bool hearingImpaired, string source)
    {
        string key = KeyOf(AnchorOf(null, imdbId, season), source, MarkKind.Pack, Normalise(language), hearingImpaired);
        var zeile = Find(db, key);
        if (zeile != null)
        {
            return Mark.FromRow(zeile);
        }

        string iso = Normalise(language);
        var pack = db.PackRows.AsNoTracking().ToList().FirstOrDefault(p =>
            string.Equals(p.ImdbId, imdbId ?? string.Empty, StringComparison.Ordinal)
            && p.Season == season
            && string.Equals(Quelle(p.Source), source, StringComparison.Ordinal)
            && SpracheGleich(p.Language, iso)
            && (p.HearingImpaired ?? false) == hearingImpaired);

        return pack == null
            ? new Mark(source, MarkKind.Pack, null, null, null, null, null, null, false)
            : new Mark(
                source,
                MarkKind.Pack,
                pack.UploadedAt,
                pack.PackId,
                pack.Fingerprint,
                null,
                pack.RefusalReason,
                pack.RefusedAt,
                false);
    }

    /// <summary>Normiert eine Sprachangabe so, wie die Matrix sie führt (ISO 639-2, Großbuchstaben).</summary>
    /// <param name="language">Any language code.</param>
    /// <returns>The normalised code, empty for null/blank.</returns>
    public static string Normalise(string? language)
        => Language.LanguageMapper.Canonical(language) ?? (language ?? string.Empty).Trim().ToUpperInvariant();

    private static string Quelle(string? source)
        => string.IsNullOrWhiteSpace(source) ? Subdl : source!;

    private static bool SpracheGleich(string? a, string b)
        => string.Equals(Normalise(a), b, StringComparison.Ordinal);

    /// <summary>Die Spur-Zeilen eines Mediums aus beiden Tabellen, als Rückfall-Fassung.</summary>
    private static IEnumerable<SpurMarken> TrackRows(ScribeSqliteContext db, string? mediaHash)
    {
        if (string.IsNullOrEmpty(mediaHash))
        {
            yield break;
        }

        foreach (var e in db.EmbedRows.AsNoTracking().Where(x => x.MediaHash == mediaHash).ToList())
        {
            yield return new SpurMarken(
                e.Language,
                e.HearingImpaired,
                e.SubdlDownloadId,
                e.SubdlUploadedAt,
                e.TvSubtitlesDownloadId,
                e.TvSubtitlesUploadedAt,
                e.MoviesubtitlesDownloadId,
                e.MoviesubtitlesUploadedAt);
        }

        foreach (var s in db.SidecarRows.AsNoTracking().Where(x => x.MediaHash == mediaHash).ToList())
        {
            yield return new SpurMarken(
                s.Language,
                s.HearingImpaired,
                s.SubdlDownloadId,
                s.SubdlUploadedAt,
                s.TvSubtitlesDownloadId,
                s.TvSubtitlesUploadedAt,
                s.MoviesubtitlesDownloadId,
                s.MoviesubtitlesUploadedAt);
        }
    }
}

/// <summary>
/// F-M667: das Ergebnis EINER Marken-Frage — und die Auskunft, wer sie beantwortet hat.
/// </summary>
/// <param name="Source">The source the mark belongs to.</param>
/// <param name="Kind">One of <see cref="MarkKind"/>.</param>
/// <param name="ClosedAt">The verdict "closed", or null for open.</param>
/// <param name="SourceId">The id at that source, when known.</param>
/// <param name="Fingerprint">Content fingerprint (file/pack only).</param>
/// <param name="MtimeUnix">File mtime the fingerprint belongs to (file/pack only).</param>
/// <param name="RefusalReason">Why the source refused, when it did.</param>
/// <param name="RefusedAt">When it refused.</param>
/// <param name="FromMatrix">True when the matrix answered, false when the old columns did.</param>
public sealed record Mark(
    string Source,
    string Kind,
    DateTime? ClosedAt,
    string? SourceId,
    string? Fingerprint,
    long? MtimeUnix,
    string? RefusalReason,
    DateTime? RefusedAt,
    bool FromMatrix)
{
    /// <summary>Die Marke aus einer Matrix-Zeile.</summary>
    /// <param name="row">The stored row.</param>
    /// <returns>The mark, answered by the matrix.</returns>
    public static Mark FromRow(SourceMatrixEntity row)
        => new(
            row.Source,
            row.Kind,
            row.ClosedAt,
            row.SourceId,
            row.Fingerprint,
            row.MtimeUnix,
            row.RefusalReason,
            row.RefusedAt,
            true);
}

/// <summary>
/// F-M667: die quellenbezogenen Felder EINER Spur, wie sie in den breiten Spalten stehen — damit der
/// Rückfall für eingebettete Spuren und Sidecars dieselbe Regel benutzt.
/// </summary>
/// <param name="Language">Language of the track.</param>
/// <param name="HearingImpaired">HI flag, three-valued.</param>
/// <param name="SubdlDownloadId">SubDL entry id.</param>
/// <param name="SubdlUploadedAt">SubDL upload time.</param>
/// <param name="TvSubtitlesDownloadId">tvsubtitles entry id.</param>
/// <param name="TvSubtitlesUploadedAt">tvsubtitles upload time.</param>
/// <param name="MoviesubtitlesDownloadId">moviesubtitles entry id.</param>
/// <param name="MoviesubtitlesUploadedAt">moviesubtitles upload time.</param>
internal sealed record SpurMarken(
    string Language,
    bool? HearingImpaired,
    string? SubdlDownloadId,
    DateTime? SubdlUploadedAt,
    string? TvSubtitlesDownloadId,
    DateTime? TvSubtitlesUploadedAt,
    string? MoviesubtitlesDownloadId,
    DateTime? MoviesubtitlesUploadedAt)
{
    /// <summary>Zeitpunkt und Id dieser Spur für EINE Quelle.</summary>
    /// <param name="source">The source.</param>
    /// <returns>The upload time and the source's own id, or nulls.</returns>
    public (DateTime? Zeit, string? Id) For(string source) => source switch
    {
        SourceMatrix.Subdl => (SubdlUploadedAt, SubdlDownloadId),
        SourceMatrix.TvSubtitles => (TvSubtitlesUploadedAt, TvSubtitlesDownloadId),
        SourceMatrix.MovieSubtitles => (MoviesubtitlesUploadedAt, MoviesubtitlesDownloadId),
        _ => (null, null),
    };
}
