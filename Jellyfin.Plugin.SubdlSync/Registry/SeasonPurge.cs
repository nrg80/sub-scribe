using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// F-M740 (Betreiber-Auftrag 24.11.2026: „Dann alle plugin db Einträge von brothers S01 bitte löschen"):
/// **eine Staffel vollständig aus dem Store nehmen.**
/// <para>
/// Warum es dafür einen eigenen Weg braucht: der Store führt zu einer Staffel nichts Einfaches, sondern
/// ein Geflecht. Die Datei (<see cref="FileEntity"/>) ist der Anker; ihre Spuren und ihr Beiwerk tragen
/// die Dateikennung **in ihrer eigenen Kennung** (<c>#yyyyy#zzz</c>, F-M676) — die Spalte <c>FileId</c>
/// existiert nicht, sie wäre dieselbe Aussage doppelt. Die Marken-Matrix
/// (<see cref="UploadEntity"/>, <see cref="ReferenceEntity"/>) hängt an Datei- oder Spurkennung, die
/// Behälter-Mitgliedschaft (<see cref="PackTrackEntity"/>) am Behälter, Behälter und Staffelurteil am
/// Schlüssel <c>imdb|staffel|…</c>, die ausgestrahlte Folgenzahl ebenso, die Sprachabsagen
/// (<see cref="SourceRefusalEntity"/>) am Medienhash und die Titel-Erschöpfung
/// (<see cref="TitleFailureEntity"/>) am <c>quelle|titel</c>-Schlüssel. Wer nur eine Tabelle leert,
/// hinterlässt den Rest — und der nächste Lauf verhält sich weiter, als wäre die Staffel erledigt.
/// </para>
/// <para>
/// Die Reihenfolge ist die der Abhängigkeiten: Marken und Beiwerk, dann Spuren, dann die Dateien.
/// <c>dryRun</c> zählt nur und löscht nichts — die Zahlen werden vorher genannt, damit das Löschen keine
/// Überraschung ist. Eine Sicherung der Store-Datei legt der Aufrufer an.
/// </para>
/// </summary>
public static class SeasonPurge
{
    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context.
    /// </summary>
    /// <param name="db">The plugin's data context.</param>
    public static void Attach(ScribeSqliteContext? db)
    {
        lock (Gate)
        {
            _db = db;
        }
    }

    /// <summary>
    /// Removes every stored row that belongs to one season of one series, or only counts them.
    /// </summary>
    /// <param name="imdbId">Series IMDb id, e.g. <c>tt6773088</c>.</param>
    /// <param name="season">Season number.</param>
    /// <param name="dryRun">True counts only and deletes nothing.</param>
    /// <returns>Per-table counts and whether the delete actually ran (false when no store is attached).</returns>
    public static (Dictionary<string, int> Counts, bool Done) Purge(string imdbId, int season, bool dryRun)
    {
        var zahlen = new Dictionary<string, int>(StringComparer.Ordinal);
        ScribeSqliteContext? db = _db;
        if (db == null || string.IsNullOrWhiteSpace(imdbId) || season <= 0)
        {
            return (zahlen, false);
        }

        string id = imdbId.Trim().ToLowerInvariant();

        lock (Gate)
        {
            // ---- der Anker: die Dateien der Staffel ------------------------------------------
            var dateien = db.FileRows.Where(f => f.ImdbId == id && f.Season == season).ToList();
            var dateiIds = dateien.Select(f => f.FileId).ToList();

            // ---- Spuren und Beiwerk tragen die Dateikennung in ihrer eigenen Kennung ----------
            var spuren = new List<TrackEntity>();
            var beiwerk = new List<SidecarEntity>();
            foreach (string dateiId in dateiIds)
            {
                string praefix = dateiId;
                spuren.AddRange(db.TrackRows.Where(t => t.TrackId.StartsWith(praefix)).ToList());
                beiwerk.AddRange(db.SidecarRows.Where(s => s.TrackId.StartsWith(praefix)).ToList());
            }

            var spurIds = spuren.Select(t => t.TrackId).ToList();

            // ---- die Marken-Matrix haengt an Datei- ODER Spurkennung --------------------------
            var hochgeladen = db.UploadRows
                .Where(u => dateiIds.Contains(u.SubjectId) || spurIds.Contains(u.SubjectId))
                .ToList();
            var verweise = db.ReferenceRows
                .Where(r => dateiIds.Contains(r.SubjectId) || spurIds.Contains(r.SubjectId))
                .ToList();

            // ---- Staffel-Ebene: Behaelter, Mitgliedschaft, Urteil ----------------------------
            var packs = db.PackRows.Where(p => p.ImdbId == id && p.Season == season).ToList();
            var packIds = packs.Select(p => p.PackId).ToList();
            var mitglieder = db.Set<PackTrackEntity>().Where(m => packIds.Contains(m.PackId)).ToList();
            string urteilPraeferenz = id + "|" + season.ToString(CultureInfo.InvariantCulture) + "|";
            var urteile = db.PackSeasonRows.Where(r => r.Id.StartsWith(urteilPraeferenz)).ToList();

            // ---- die ausgestrahlte Folgenzahl -------------------------------------------------
            string ausgestrahltId = AiredSeasonStore.KeyOf(id, season);
            var ausgestrahlt = db.AiredSeasonRows.Where(r => r.Id == ausgestrahltId).ToList();

            // ---- Absagen haengen am Medienhash, nicht an der Staffel --------------------------
            var hashes = dateien.Where(f => !string.IsNullOrWhiteSpace(f.OsHash))
                .Select(f => f.OsHash!.Trim().ToLowerInvariant())
                .Concat(spuren.Where(t => !string.IsNullOrWhiteSpace(t.MediaHash))
                    .Select(t => t.MediaHash.Trim().ToLowerInvariant()))
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var absagen = new List<SourceRefusalEntity>();
            foreach (string hash in hashes)
            {
                string praefix = hash + "|";
                absagen.AddRange(db.SourceRefusalRows.Where(r => r.Id.StartsWith(praefix)).ToList());
            }

            absagen = absagen.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();

            // ---- Titel-Erschoepfung: quelle|titel ---------------------------------------------
            string titelSchluss = "|" + id;
            var titelFehler = db.TitleFailureRows.Where(r => r.Id.EndsWith(titelSchluss)).ToList();

            zahlen["files"] = dateien.Count;
            zahlen["tracks"] = spuren.Count;
            zahlen["sidecars"] = beiwerk.Count;
            zahlen["uploads"] = hochgeladen.Count;
            zahlen["references"] = verweise.Count;
            zahlen["packs"] = packs.Count;
            zahlen["pack members"] = mitglieder.Count;
            zahlen["season verdicts"] = urteile.Count;
            zahlen["aired counts"] = ausgestrahlt.Count;
            zahlen["source refusals"] = absagen.Count;
            zahlen["title failures"] = titelFehler.Count;

            if (dryRun)
            {
                return (zahlen, false);
            }

            try
            {
                db.UploadRows.RemoveRange(hochgeladen);
                db.ReferenceRows.RemoveRange(verweise);
                db.SidecarRows.RemoveRange(beiwerk);
                db.TrackRows.RemoveRange(spuren);
                db.Set<PackTrackEntity>().RemoveRange(mitglieder);
                db.PackRows.RemoveRange(packs);
                db.PackSeasonRows.RemoveRange(urteile);
                db.AiredSeasonRows.RemoveRange(ausgestrahlt);
                db.SourceRefusalRows.RemoveRange(absagen);
                db.TitleFailureRows.RemoveRange(titelFehler);
                db.FileRows.RemoveRange(dateien);
                db.SaveChanges();
                return (zahlen, true);
            }
            catch (DbUpdateException)
            {
                return (zahlen, false);
            }
        }
    }
}
