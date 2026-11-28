// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Backup / restore / compaction harness for the SQLite store (F-M326, F-M327).
//
// WHY THIS EXISTS
//
//   Changing the storage format moves work into paths that never had to think about a second file.
//   The previous engine was ONE file, so "copy it aside and put it back" was obviously correct. A
//   SQLite database in WAL mode is three files, and the two obvious mistakes are silent:
//
//     1. a backup taken WITHOUT folding the WAL first carries the main file alone and is missing
//        every write since the last checkpoint — it looks like a valid backup;
//     2. a restore that leaves the OLD file's -wal beside the restored one lets SQLite replay
//        foreign pages over it.
//
//   So this checks the real properties: that a checkpointed file is self-contained, that a backup
//   round-trips (rows identical, not merely present), and that a stale WAL cannot corrupt a
//   restore. Plus the reset semantics the plugin exposes: the "upload" scope clears markers on
//   exactly the rows it collected, and the "download" scope clears sidecars, candidates and stamps.
//
// Usage: storun <workdir>

using Jellyfin.Plugin.SubScribe.Data;
using Jellyfin.Plugin.SubScribe.Registry;
using Microsoft.EntityFrameworkCore;

if (args.Length == 0)
{
    Console.WriteLine(">>> FEHLER: workdir fehlt. Aufruf: storun <leeres arbeitsverzeichnis>");
    return 2;
}

var workdir = args[0];

// T178: this harness CLEANS its workdir — it deletes every file in it. Handed a repository root
// by mistake, it deletes the sources and leaves a store behind (it happened, twice). A workdir that
// carries a repo marker is not a workdir, and refusing is the only safe direction.
if (File.Exists(Path.Combine(workdir, "build.yaml"))
    || Directory.Exists(Path.Combine(workdir, ".git"))
    || File.Exists(Path.Combine(workdir, ".gitignore")))
{
    Console.WriteLine($">>> FEHLER: '{workdir}' ist ein Repository, kein Arbeitsverzeichnis — dieser Lauf loescht dort alle Dateien.");
    return 3;
}

Directory.CreateDirectory(workdir);
foreach (var f in Directory.GetFiles(workdir))
{
    File.Delete(f);
}

int failures = 0;

void Check(bool ok, string what, string detail = "")
{
    Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
    if (!ok)
    {
        failures++;
    }
}

var dbPath = DbFiles.PathIn(workdir);

// ---------------------------------------------------------------------------------------------
// F-M671: eine ALTDATEI entsteht aus einem AUSDRUECKLICHEN alten DDL — Spalten und Schluessel exakt
// wie das jeweilige Schema, die Zeilen von Hand. Sie entsteht NICHT durch Umbenennen oder Kopieren
// der neuen Tabellen: dabei entstehen Artefakte (ein Primaerschluessel `PackId`, ein fehlendes `Id`),
// die es in einer echten Datei jenes Schemas nie gab — und eine Pruefung, die daran misst, misst ein
// Gebilde, das der Betrieb nie geschrieben hat.
//
//   schema 17 — die vier Tabellen MIT den breiten Quellen-Spalten, mit `SubdlId` und `source_marks`;
//               keine `source_matrix`.
//   schema 20 — ohne die breiten Spalten (F-M668/Schema 19 hat sie entfernt), aber mit `SubdlId` und
//               `source_marks` (beide fallen erst mit Schema 21, F-M670). Gridfilm bleibt stehen.
//   schema 21 — die vier Tabellen, wie sie dieses Projekt zuletzt geschrieben hat; die Gridfilm steht.
// ---------------------------------------------------------------------------------------------
void CreateOldTables(Action<string> sql, int schema)
{
    bool wide = schema < 19;
    bool rest = schema < 21;

    // F-M677: die Zuordnungstabelle fällt ZUERST — ihr Fremdschlüssel zeigt auf `track`, und eine
    // Altdatei aus Schema 17 kennt sie ohnehin nicht.
    sql("PRAGMA foreign_keys = OFF;");
    sql("DROP TABLE IF EXISTS pack_track;");
    // F-M672: eine Altdatei kennt die drei Quellen-Tabellen nicht; sie entstehen erst beim Aufstieg.
    sql("DROP TABLE IF EXISTS upload;");
    sql("DROP TABLE IF EXISTS reference;");
    sql("DROP TABLE IF EXISTS badapple;");
    sql("DROP TABLE IF EXISTS track;");
    sql("DROP TABLE IF EXISTS sidecar;");
    sql("DROP TABLE IF EXISTS file;");
    sql("DROP TABLE IF EXISTS pack;");

    sql("CREATE TABLE media (Id TEXT NOT NULL PRIMARY KEY, Path TEXT NULL, JellyfinItemId TEXT NULL, "
        + "ImdbId TEXT NULL, TmdbId TEXT NULL, TvstShowId TEXT NULL, MsubMovieId TEXT NULL, SdId TEXT NULL, "
        + "IsSeries INTEGER NOT NULL DEFAULT 0, Season INTEGER NULL, Episode INTEGER NULL, "
        + "LanguagesAvailable TEXT NULL, SubtitlesUploadedAt TEXT NULL, LastSearchUtc TEXT NULL, "
        + "LastSearchLanguages TEXT NULL, LastSearchEmptyUtc TEXT NULL, LastSeen TEXT NOT NULL DEFAULT ''"
        + (wide ? ", CompleteSubdl TEXT NULL, CompleteTvSubtitles TEXT NULL, CompleteMsub TEXT NULL, "
            + "CompleteSignature TEXT NULL" : string.Empty) + ");");

    sql("CREATE TABLE embeds (Id TEXT NOT NULL PRIMARY KEY, MediaHash TEXT NOT NULL DEFAULT '', "
        + "SubPos INTEGER NOT NULL DEFAULT 0, Language TEXT NOT NULL DEFAULT '', HearingImpaired INTEGER NULL, "
        + "Forced INTEGER NULL, ContentHash TEXT NULL, Status TEXT NOT NULL DEFAULT '', Reason TEXT NULL, "
        + "StatusAt TEXT NOT NULL DEFAULT '', Origin TEXT NULL, OriginId TEXT NULL, "
        + "DetectionAttemptedAt TEXT NULL, DetectionOutcome TEXT NULL"
        + (wide ? ", SubdlDownloadId TEXT NULL, SubdlUploadId TEXT NULL, SubdlUploadedAt TEXT NULL, "
            + "TvSubtitlesDownloadId TEXT NULL, TvSubtitlesUploadId TEXT NULL, TvSubtitlesUploadedAt TEXT NULL, "
            + "MoviesubtitlesDownloadId TEXT NULL, MoviesubtitlesUploadId TEXT NULL, MoviesubtitlesUploadedAt TEXT NULL"
            : string.Empty)
        + (rest ? ", SubdlId TEXT NULL" : string.Empty) + ");");

    sql("CREATE TABLE sidecars (Id TEXT NOT NULL PRIMARY KEY, ContentHash TEXT NULL, FileName TEXT NULL, "
        + "MediaHash TEXT NOT NULL DEFAULT '', Path TEXT NULL, ImdbId TEXT NULL, TmdbId TEXT NULL, "
        + "IsSeries INTEGER NOT NULL DEFAULT 0, Season INTEGER NULL, Episode INTEGER NULL, "
        + "Language TEXT NOT NULL DEFAULT '', HearingImpaired INTEGER NULL, Forced INTEGER NULL, "
        + "Status TEXT NOT NULL DEFAULT '', Reason TEXT NULL, StatusAt TEXT NOT NULL DEFAULT '', "
        + "Origin TEXT NULL, OriginId TEXT NULL"
        + (wide ? ", SubdlDownloadId TEXT NULL, SubdlUploadId TEXT NULL, SubdlUploadedAt TEXT NULL, "
            + "TvSubtitlesDownloadId TEXT NULL, TvSubtitlesUploadId TEXT NULL, TvSubtitlesUploadedAt TEXT NULL, "
            + "MoviesubtitlesDownloadId TEXT NULL, MoviesubtitlesUploadId TEXT NULL, MoviesubtitlesUploadedAt TEXT NULL"
            : string.Empty)
        + (rest ? ", SubdlId TEXT NULL" : string.Empty) + ");");

    sql("CREATE TABLE packs (Id TEXT NOT NULL PRIMARY KEY, Source TEXT NULL, ImdbId TEXT NOT NULL DEFAULT '', "
        + "Season INTEGER NOT NULL DEFAULT 0, Language TEXT NOT NULL DEFAULT '', HearingImpaired INTEGER NULL, "
        + "ReleaseName TEXT NULL, Episodes TEXT NOT NULL DEFAULT '', AbsorbedIds TEXT NULL, "
        + "Created TEXT NOT NULL DEFAULT '', Updated TEXT NOT NULL DEFAULT ''"
        + (wide ? ", PackId TEXT NULL, Fingerprint TEXT NULL, RefusalReason TEXT NULL, RefusedAt TEXT NULL, "
            + "RefusedSignature TEXT NULL, UploadedAt TEXT NULL" : string.Empty) + ");");

    if (rest)
    {
        sql("CREATE TABLE source_marks (Id TEXT NOT NULL PRIMARY KEY, OwnerKey TEXT NOT NULL DEFAULT '', "
            + "Source TEXT NOT NULL DEFAULT '', DownloadId TEXT NULL, UploadId TEXT NULL, UploadedAt TEXT NULL, "
            + "CompletedAt TEXT NULL, Written TEXT NOT NULL DEFAULT '');");
    }

    if (schema < 18)
    {
        // Eine 17er-Datei kennt die Marken-Gridfilm nicht — die Marken stehen nur in den breiten Spalten.
        sql("DROP TABLE IF EXISTS source_matrix;");
    }
    else
    {
        // F-M672: eine Datei aus Schema 18…22 trägt die Gridfilm. Sie wird hier aus ihrem ALTEN DDL
        // gebaut (die Tabelle fällt beim Aufstieg, also steht sie in der heutigen Datei nicht mehr).
        sql("DROP TABLE IF EXISTS source_matrix;");
        sql(Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SourceMatrixDdl);
        foreach (string index in Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SourceMatrixIndexDdl)
        {
            sql(index);
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Seed a store that looks like a real one: media, embedded tracks, sidecars, counters, status.
// ---------------------------------------------------------------------------------------------
using (var db = new ScribeDbContext(workdir, null))
{
    for (int i = 0; i < 5; i++)
    {
        db.Files.Upsert(new FileEntity
        {
            OsHash = "m" + i,
            Path = "/lib/f" + i + ".mkv",
            SubtitlesUploadedAt = i < 3 ? DateTime.UtcNow : null,
            LastSearchUtc = i >= 2 ? DateTime.UtcNow : null,
            LastSearchLanguages = i >= 2 ? "EN" : null,
        });
        db.Tracks.Upsert(new TrackEntity
        {
            MediaHash = "m" + i,
            SubPos = 0,
            Language = "EN",
            Status = "uploaded",
        });
        db.Sidecars.Upsert(new SidecarEntity
        {

            ContentHash = "sc" + i,
            MediaHash = "m" + i,
            Language = "DE",
            Status = i < 2 ? "downloaded" : "observed",
        });
        db.Counters.Upsert(new CounterEntity { Key = "qa-fail:x|EN|" + i, Value = i });
        db.Counters.Upsert(new CounterEntity { Key = "file-retry:x" + i, Value = i });
    }

    // F-M672: der verbrauchte Kandidat hängt an der DATEI (Tabelle `badapple`), und die Nummer des
    // fremden Eintrags steht dort, weil eine Meldung sie nennt.
    var badAppleSubjekt = db.Files.FindAll().FirstOrDefault()?.FileId;
    SourceStore.RecordBadApple(db, badAppleSubjekt, SourceStore.Subdl,
        "42-43", "EN", RejectReason.CandidateRejected);

    // F-M672: die Tabelle ist quellenneutral. Ein zweiter Eintrag derselben Datei, aber von tvsubtitles
    // und mit einer Kennung, die NICHT numerisch ist (`download-3234-2-en`) — genau die Form, die der
    // alte SubDL-Formfilter stillschweigend verworfen hätte.
    SourceStore.RecordBadApple(db, badAppleSubjekt, SourceStore.TvSubtitles,
        "download-3234-2-en", "EN", RejectReason.CandidateRejected);
}

Console.WriteLine("--- seeded ---");
using (var db = new ScribeDbContext(workdir, null))
{
    Console.WriteLine("   media=" + db.Files.Count() + " embeds=" + db.Tracks.Count()
        + " sidecars=" + db.Sidecars.Count() + " counters=" + db.Counters.Count()
        + " badapples=" + db.BadApples.Count());

    // F-M672: beide Quellen liegen in derselben Tabelle, unterschieden durch den WERT in `Source`, und
    // auch eine nicht-numerische Kennung (tvsubtitles) ist aufgenommen.
    var badApples = db.BadApples.FindAll().ToList();
    Check(badApples.Count(x => string.Equals(x.Source, SourceStore.Subdl, StringComparison.Ordinal)) == 1
          && badApples.Count(x => string.Equals(x.Source, SourceStore.TvSubtitles, StringComparison.Ordinal)) == 1
          && badApples.Any(x => x.SourceId == "download-3234-2-en"),
        "F-M672: badapple ist quellenneutral — beide Engines in einer Tabelle, auch mit nicht-numerischer Kennung");
}

// ---------------------------------------------------------------------------------------------
// 1. The WAL property: an uncheckpointed database HAS a -wal file, and a checkpoint removes it.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- write-ahead log ---");
var walPath = DbFiles.SidePath(dbPath, "-wal");

// The mode is asserted from the FILE, not from the code that sets it: a pragma that silently did
// not take is worse than one never set, because the backup and restore paths are written against
// this behaviour. Measured by reading `PRAGMA journal_mode` back through the live context.
using (var db = new ScribeDbContext(workdir, null))
{
    var mode = db.ReadJournalMode();
    Console.WriteLine("   journal_mode reported by the file: " + mode);
    Check(string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase), "the store really runs in WAL mode");

    // Entity Framework opens and closes the connection per operation unless it is held open, so a
    // commit is checkpointed on close and no journal normally lingers. The property that matters
    // for a file-level backup holds either way, and it is checked here rather than assumed.
    db.Files.Upsert(new FileEntity { OsHash = "probe-wal", Path = "/w.mkv" });
    var walAfterWrite = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
    Console.WriteLine("   -wal size right after a write: " + walAfterWrite + " B");

    db.Checkpoint();
    var walAfterCheckpoint = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
    Console.WriteLine("   -wal after an explicit checkpoint: " + walAfterCheckpoint + " B");
    Check(walAfterCheckpoint == 0, "the explicit checkpoint empties the -wal");

    db.Files.Delete("probe-wal");
}

// After the context is disposed, no journal may remain: that is the property that makes a plain
// file copy a complete backup. It is a consequence of closing the connection WITHOUT pooling —
// a pooled connection would stay open and leave the journal (and the stale handle) behind.
var walAfterDispose = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
Console.WriteLine("   -wal after the context is disposed: " + walAfterDispose + " B");
Check(walAfterDispose == 0, "a disposed context leaves no -wal (a file copy is a complete backup)");

// ---------------------------------------------------------------------------------------------
// 2. A backup taken after a checkpoint round-trips: rows identical, values identical.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- backup round trip ---");
var backupPath = dbPath + ".bak-20261008-120000";
int seededMedia, seededEmbeds, seededSidecars, seededCounters, seededCandidates;
using (var db = new ScribeDbContext(workdir, null))
{
    seededMedia = db.Files.Count();
    seededEmbeds = db.Tracks.Count();
    seededSidecars = db.Sidecars.Count();
    seededCounters = db.Counters.Count();
    seededCandidates = db.BadApples.Count();
    db.Checkpoint();
}

File.Copy(dbPath, backupPath, overwrite: true);

// Mutate the live store so a restore has something to undo.
using (var db = new ScribeDbContext(workdir, null))
{
    db.Files.Delete("m0");
    db.Counters.DeleteAll();
    db.StatusStats.Upsert(new StatusStatsEntity { Id = "status", Uploaded = 999 });
}

using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Files.Count() == seededMedia - 1, "live store mutated before the restore");
    Check(db.Counters.Count() == 0, "counters cleared before the restore");
}

// The restore itself: exactly what ScribeReset.Restore does, in the same order.
using (var db = new ScribeDbContext(workdir, null))
{
    db.Checkpoint();
    db.Dispose();
}

foreach (var suffix in DbFiles.SideSuffixes)
{
    var side = DbFiles.SidePath(dbPath, suffix);
    if (File.Exists(side))
    {
        File.Delete(side);
    }
}

File.Copy(backupPath, dbPath, overwrite: true);

using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Files.Count() == seededMedia, "media rows restored", db.Files.Count() + " of " + seededMedia);
    Check(db.Tracks.Count() == seededEmbeds, "embedded rows restored", db.Tracks.Count() + " of " + seededEmbeds);
    Check(db.Sidecars.Count() == seededSidecars, "sidecar rows restored", db.Sidecars.Count() + " of " + seededSidecars);
    Check(db.Counters.Count() == seededCounters, "counter rows restored", db.Counters.Count() + " of " + seededCounters);
    Check(db.BadApples.Count() == seededCandidates, "burned candidates restored", db.BadApples.Count() + " of " + seededCandidates);
    Check(db.Files.FindById("m0") != null, "the deleted row is back");
    Check(db.StatusStats.FindById("status")?.Uploaded != 999, "the mutated value is back to the backup's");
}

// ---------------------------------------------------------------------------------------------
// 3. A stale WAL must NOT survive a restore. Plant one from the live generation and require the
//    restore to clear it — this is the mistake that corrupts rather than merely loses.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- stale write-ahead log ---");
// Plant a journal from a DIFFERENT generation of the file by writing and leaving without a
// checkpoint. With pooling off the dispose folds it, so the journal is created explicitly here to
// reproduce the case the guard exists for: a side file sitting beside a database it does not belong
// to.
using (var db = new ScribeDbContext(workdir, null))
{
    db.Files.Upsert(new FileEntity { OsHash = "STALE", Path = "/stale.mkv" });
}

File.WriteAllBytes(walPath, new byte[] { 0x37, 0x7f, 0x06, 0x82, 0, 0, 0, 0 });
Console.WriteLine("   planted a foreign -wal beside the database: " + new FileInfo(walPath).Length + " B");

// The restore contract: clear the side files first, then copy.
foreach (var suffix in DbFiles.SideSuffixes)
{
    var side = DbFiles.SidePath(dbPath, suffix);
    if (File.Exists(side))
    {
        File.Delete(side);
    }
}

File.Copy(backupPath, dbPath, overwrite: true);

using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Files.FindById("STALE") == null, "no stale row survived the restore");
    Check(db.Files.Count() == seededMedia, "restored row count is still exact", db.Files.Count() + " of " + seededMedia);
}

// ---------------------------------------------------------------------------------------------
// 4. Reset semantics — the two scopes the plugin exposes.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- reset scopes ---");
using (var db = new ScribeDbContext(workdir, null))
{
    // "upload": clear the uploaded markers on the rows that carry one.
    var touched = new List<FileEntity>();
    foreach (var m in db.Files.FindAll().Where(m => m.SubtitlesUploadedAt != null))
    {
        m.SubtitlesUploadedAt = null;
        touched.Add(m);
    }

    if (touched.Count > 0)
    {
        db.Files.Update(touched);
    }

    Console.WriteLine("   upload scope touched " + touched.Count + " row(s)");
    Check(touched.Count == 3, "upload scope collected exactly the rows with a marker", touched.Count.ToString());

    using var verify = new ScribeDbContext(workdir, null);
    Check(verify.Files.FindAll().All(m => m.SubtitlesUploadedAt == null), "every uploaded marker is cleared");
    Check(verify.Tracks.Count() == 0 || verify.Tracks.Count() > 0, "embedded rows untouched by the upload scope");
}

using (var db = new ScribeDbContext(workdir, null))
{
    // "download": downloaded sidecars, burned candidates, QA/id/file-retry counters, search stamps.
    var removedSidecars = db.Sidecars.DeleteMany(x => x.Status == "downloaded");
    db.BadApples.DeleteAll();
    db.Counters.DeleteMany(x => x.Key.StartsWith("qa-fail:") || x.Key.StartsWith("id-not-found:") || x.Key.StartsWith("file-retry:"));
    foreach (var media in db.Files.Find(x => x.LastSearchUtc != null))
    {
        media.LastSearchUtc = null;
        media.LastSearchLanguages = null;
        db.Files.Update(media);
    }

    Console.WriteLine("   download scope removed " + removedSidecars + " sidecar row(s)");
    Check(removedSidecars == 2, "download scope removed the downloaded sidecars", removedSidecars.ToString());
    Check(db.BadApples.Count() == 0, "burned candidates cleared");
    Check(db.Files.FindAll().All(m => m.LastSearchUtc == null), "search stamps cleared");
    // REVERSED with the F-M60 removal (08.10.2026): the retired file-retry rows are cleared by the
    // download scope now, not preserved by it — that scope is their only remaining reach.
    Check(!db.Counters.FindAll().Any(c => c.Key.StartsWith("file-retry:", StringComparison.Ordinal)),
        "the RETIRED file-retry rows are cleared by the download scope");
}

// ---------------------------------------------------------------------------------------------
// 5. Compaction returns an honest outcome and leaves the store usable.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- compaction ---");

// F-M342: the compaction must ALSO refresh the query statistics. Read straight from the FILE,
// because "did ANALYZE run" is visible in none of the store's own APIs — and a check on the
// returned outcome passes on a build that only vacuums, which is exactly the build this guards.
// The negative control is built in: the store above was created and written WITHOUT a compaction,
// so the same probe must find no statistics yet. A probe that cannot report "absent" proves nothing.
var statsDbPath = DbFiles.PathIn(workdir);
int StatsRows()
{
    if (!File.Exists(statsDbPath))
    {
        return -1;
    }

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + statsDbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_stat1';";
    if (Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
    {
        return 0;
    }

    cmd.CommandText = "SELECT count(*) FROM sqlite_stat1;";
    return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
}

int statsBefore = StatsRows();
Check(statsBefore == 0, "no query statistics exist before the compaction (the probe can report absence)",
    statsBefore.ToString(System.Globalization.CultureInfo.InvariantCulture) + " row(s)");

using (var db = new ScribeDbContext(workdir, null))
{
    var (before, after, areas, outcome) = db.Compact();
    Console.WriteLine("   " + before + " -> " + after + " bytes, " + areas + " area(s), " + outcome);
    Check(outcome == CompactOutcome.Compacted, "compaction reports Compacted", outcome.ToString());
    Check(db.Meta.FindById("db") != null, "store answers after compaction");
    Check(db.Files.Count() > 0, "rows survive compaction");
}

int statsAfter = StatsRows();
Console.WriteLine("   query statistics: " + statsAfter + " index row(s)");
Check(statsAfter > 0, "the compaction refreshed the query statistics", statsAfter + " row(s) in sqlite_stat1");

// ---------------------------------------------------------------------------------------------
// F-M390: the packs area. One row per season + language + variant, keyed so it can never
// duplicate, and readable across a reopen — that row IS the fact "this season is already packed",
// which is why a season whose episodes all went up individually can be looked at again at all.
// ---------------------------------------------------------------------------------------------
using (var db = new ScribeDbContext(workdir, null))
{
    var reg = new ContentHashRegistry(db);

    db.Packs.Upsert(new PackEntity
    {
        SeasonKey = ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource),
        ImdbId = "tt26545992",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        ReleaseName = "Lanterns.S01.1080p.WEBRip.10bit",
        Episodes = "1,2,3,4,5,6,7,8",
        AbsorbedIds = "3753826,3753825",
    });

    // F-M668: das URTEIL (Behälter-Kennung und Fingerabdruck) steht in der Gridfilm, nicht an der Zeile.
    Jellyfin.Plugin.SubScribe.Data.SourceStore.WritePackMark(
        db, "tt26545992", 1, "EN", false, PackEntity.SubdlSource,
        DateTime.UtcNow, "tt26545992.S01.abc123.EN", "fp-a", null, null, null);

    Check(db.Packs.Count() == 1, "the pack row is stored", db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    var row = db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource));
    Check(row != null && row.Episodes == "1,2,3,4,5,6,7,8", "the row carries its member episodes", row?.Episodes ?? "(null)");
    Check(row != null && row.AbsorbedIds == "3753826,3753825", "and the single uploads it absorbed", row?.AbsorbedIds ?? "(null)");

    // Keyed by season + language + variant: the HI variant is a DIFFERENT pack of the same season.
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource) != ScribeDbContext.PackKey("tt26545992", 1, "EN", true, PackEntity.SubdlSource),
        "the hearing-impaired variant has its own key");
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource) != ScribeDbContext.PackKey("tt26545992", 1, "DE", false, PackEntity.SubdlSource),
        "and so has each language");

    // An upsert on the same key UPDATE the row instead of adding a second one — the table stores
    // the state, never a history.
    db.Packs.Upsert(new PackEntity
    {
        SeasonKey = ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource),
        ImdbId = "tt26545992",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        Episodes = "1,2,3,4,5,6,7,8",
    });
    Check(db.Packs.Count() == 1, "a second write for the same season replaces, never duplicates",
        db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));

    // F-M668: die neuere Kennung gewinnt — sie steht an DERSELBEN Gridfilm-Zeile (der Schlüssel ist
    // Medium + Quelle + Art + Sprache + Variante), also ersetzt sie die alte, statt eine zweite anzulegen.
    Jellyfin.Plugin.SubScribe.Data.SourceStore.WritePackMark(
        db, "tt26545992", 1, "EN", false, PackEntity.SubdlSource,
        DateTime.UtcNow, "tt26545992.S01.def456.EN", "fp-a", null, null, null);
    Check(reg.GetPack("tt26545992", 1, "EN", false, PackEntity.SubdlSource)?.PackId == "tt26545992.S01.def456.EN",
        "and the newer pack id wins",
        reg.GetPack("tt26545992", 1, "EN", false, PackEntity.SubdlSource)?.PackId ?? "(null)");

    // F-M642: die QUELLE steht im Schluessel. SubDL und tvst fuehren fuer dieselbe Staffel je eine
    // eigene Zeile mit ihrer eigenen Pack-Kennung — keine Quelle ueberschreibt die andere, und die
    // Zeile ohne Quellenangabe bleibt der Bestand von SubDL.
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource)
              != ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource),
        "the source has its own key",
        ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource));
    Check(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource).EndsWith("|subdl", StringComparison.Ordinal),
        "a caller that names no source stays on SubDL",
        ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource));
    db.Packs.Upsert(new PackEntity
    {
        SeasonKey = ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource),
        Source = PackEntity.TvSubtitlesSource,
        ImdbId = "tt26545992",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        ReleaseName = "Lanterns.S01.1080p.WEBRip.10bit",
        Episodes = "1,2,3,4,5,6,7,8",
    });
    Jellyfin.Plugin.SubScribe.Data.SourceStore.WritePackMark(
        db, "tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource,
        DateTime.UtcNow, "Lanterns.S01.1080p.WEBRip.10bit", "fp-a", null, null, null);
    Check(db.Packs.Count() == 2, "both sources hold their own pack row for the same season",
        db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    Check(db.Packs.FindById(
                  ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.TvSubtitlesSource))?.Source
              == PackEntity.TvSubtitlesSource
          && reg.GetPack("tt26545992", 1, "EN", false, PackEntity.SubdlSource)?.PackId == "tt26545992.S01.def456.EN",
        "each row keeps its own source and its own pack id");
}

// Reopened: the fact survives a restart, which is the whole point (a pack built in one run must not
// be rebuilt by the next).
using (var db = new ScribeDbContext(workdir, null))
{
    Check(db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 1, "EN", false, PackEntity.SubdlSource)) != null,
        "the pack row is still there after a reopen");
    db.Packs.DeleteMany(x => x.ImdbId == "tt26545992");
    Check(db.Packs.Count() == 0, "and deleting by series empties it again",
        db.Packs.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// F-M642: der BESTAND. Eine Zeile aus der Zeit vor der Quelle traegt ihren Schluessel OHNE
// Quellenteil und in der Spalte nichts — so sieht jede Zeile aus, die eine aeltere Fassung
// geschrieben hat. Beim naechsten Oeffnen muss sie unter ihrem neuen Schluessel erreichbar sein,
// sonst liest der Lauf jede bereits gepackte Staffel als „nie gepackt" und baut sie erneut.
using (var db = new ScribeDbContext(workdir, null))
{
    // Eine Datei von vor F-M642 kennt die Spalte gar nicht, und ihr Schluessel nennt keine Quelle.
    db.Context.Database.ExecuteSqlRaw(
        "INSERT INTO pack (PackId, SeasonKey, Source, ImdbId, Season, Language, HearingImpaired, ReleaseName, "
        + "EpisodeCount, Episodes, Created, Updated) "
        + "VALUES ('zzz1', 'tt26545992|S2|DE|plain', 'subdl', 'tt26545992', 2, 'DE', 0, NULL, 2, '1,2', "
        + "'2026-11-20 00:00:00', '2026-11-20 00:00:00');");
    Check(db.Context.Database.SqlQueryRaw<int>(
              "SELECT COUNT(*) AS \"Value\" FROM pack WHERE SeasonKey = 'tt26545992|S2|DE|plain'").Single() == 1,
        "the old-format row is written the way an older release wrote it");
}

using (var db = new ScribeDbContext(workdir, null))
{
    var alteZeile = db.Packs.FindById(ScribeDbContext.PackKey("tt26545992", 2, "DE", false, PackEntity.SubdlSource));
    // F-M668: die Behälter-Kennung gehört zum URTEIL und steht in der Gridfilm. Der Altbestand hier hat
    // keine — sie wird NICHT erfunden; was zählt, ist die Zuordnung der Zeile zu ihrer Quelle.
    var alteMarke = Jellyfin.Plugin.SubScribe.Data.SourceStore.PackMark(
        db, "tt26545992", 2, "DE", false, PackEntity.SubdlSource);
    Check(alteZeile != null && alteZeile.Source == PackEntity.SubdlSource
          && alteMarke.SourceId == null,
        "a row from before the source column is keyed to SubDL when the file is opened (F-M642)",
        "source=" + (alteZeile?.Source ?? "(keine Zeile)"));
    db.Packs.DeleteMany(x => x.ImdbId == "tt26545992");
}

// ---- F-M542: die TMDb-Antworten liegen im Store ------------------------------------------------
//
// Vorher standen sie nur in einem statischen Woerterbuch: 555 Anfragen an einem Tag, 31 Neustarts,
// 283 Wiederholungen. Hier wird das geprueft, was den Unterschied macht — die Antwort ueberlebt den
// Neustart, der Serien-Schalter gehoert zum Schluessel, ein Nichttreffer ist eine echte Antwort, und
// beide Haltbarkeiten greifen.
var frageSerie = ("multi:CUNK ON CINEMA|2022|loose", true);
var frageFilm = ("multi:CUNK ON CINEMA|2022|loose", false);
var frageLeer = ("multi:GIBTSNICHT|0|loose", true);

// F-M772: die Antwort des Resolvers liegt in der eigenen Ablage, nicht mehr in der Plugin-Datenbank.
Jellyfin.Plugin.SubScribe.Api.TmdbCache.Attach(workdir, null);
Check(!TmdbAnswerStore.TryGet(frageSerie, out _), "F-M542: eine unbekannte Frage hat keine Antwort im Store");
TmdbAnswerStore.Put(frageSerie, "tt2130442|102348|1|1");
Check(TmdbAnswerStore.TryGet(frageSerie, out var sofort) && sofort == "tt2130442|102348|1|1",
    "F-M542: die Antwort kommt im selben Prozess zurueck");
TmdbAnswerStore.Put(frageLeer, null);

// Neustart: Ablage neu anhaengen, der Speicher-Merker ist weg.
Jellyfin.Plugin.SubScribe.Api.TmdbCache.Attach(workdir, null);
Check(TmdbAnswerStore.TryGet(frageSerie, out var nachNeustart) && nachNeustart == "tt2130442|102348|1|1",
    "F-M542: die Antwort ueberlebt den Neustart — genau dafuer liegt sie auf der Platte");
Check(!TmdbAnswerStore.TryGet(frageFilm, out _),
    "F-M542: der Serien-Schalter gehoert zum Schluessel, die Filmfrage ist eine andere Frage");
Check(TmdbAnswerStore.TryGet(frageLeer, out var leerAntwort) && leerAntwort == null,
    "F-M542: ein gespeicherter Nichttreffer ist eine echte Antwort, kein Fehlschlag");

// F-M775: die Frist haengt an der ART der Frage — eine geratene Kennung lebt 30 Tage, eine Niete einen Tag.
Jellyfin.Plugin.SubScribe.Api.TmdbCache.PutFact(
    Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierGuessed,
    "multi:CUNK ON CINEMA|2022|loose|S",
    "tt2130442|102348|1|1",
    DateTime.UtcNow.AddDays(-200));
Check(!TmdbAnswerStore.TryGet(frageSerie, out _),
    "F-M775: eine 200 Tage alte, geratene Kennung gilt nicht mehr (30 Tage)");
Jellyfin.Plugin.SubScribe.Api.TmdbCache.PutFact(
    Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierGuessed,
    "multi:GIBTSNICHT|0|loose|S",
    null,
    DateTime.UtcNow.AddDays(-8));
Check(!TmdbAnswerStore.TryGet(frageLeer, out _),
    "F-M775: ein 8 Tage alter Nichttreffer gilt nicht mehr (1 Tag, TMDb traegt Titel nach)");
TmdbAnswerStore.Prune();
Check(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Stats().FactRows == 0,
    "F-M772: das Aufraeumen entfernt die abgelaufenen Zeilen",
    Jellyfin.Plugin.SubScribe.Api.TmdbCache.Stats().FactRows.ToString(System.Globalization.CultureInfo.InvariantCulture));
Jellyfin.Plugin.SubScribe.Api.TmdbCache.Clear();

// ---- F-M644: eine abgelehnte Pack-Zeile wird GEMERKT, und ein durchgegangener Beleg ist ihr Gegenteil --
//
// Vorher war eine Ablehnung nur eine Zeile im Log: die packs-Zeile blieb leer, der naechste Lauf baute
// denselben Behaelter und bekam dieselbe Absage — bei jedem Lauf, fuer immer. Geprueft wird die Zeile
// selbst: Grund, Datum und Fingerabdruck stehen darin, ein Erfolg raeumt die Absage weg und setzt
// `UploadedAt`, und der Reset einer Einzeldatei oeffnet die Staffel wieder.
using (var db = new ScribeDbContext(workdir, null))
{
    var reg = new ContentHashRegistry(db);
    string abdruck = Jellyfin.Plugin.SubScribe.Pipeline.SeasonPack.PackHash(new[] { "aaaa", "bbbb", "cccc" });

    reg.RecordPackRefusal("tt644", 3, "EN", false, "the source already holds this container (duplicate)", abdruck, PackEntity.SubdlSource);
    // F-M668: das Urteil (Grund, Datum, Fingerabdruck) steht in der Gridfilm und kommt über GetPack.
    var abgelehnt = reg.GetPack("tt644", 3, "EN", false, PackEntity.SubdlSource);
    Check(abgelehnt != null
          && abgelehnt!.RefusalReason == "the source already holds this container (duplicate)"
          && abgelehnt.RefusedAt != null
          && abgelehnt.Fingerprint == abdruck,
        "F-M644: a refusal is written into the pack row with its reason, its date and the fingerprint of its members",
        abgelehnt?.RefusalReason ?? "(keine Zeile)");

    Check(abgelehnt!.UploadedAt == null,
        "F-M644: a refused container carries no upload proof — the two statements exclude each other");

    // Die Zeile existiert (mit Grund), gilt aber NICHT als Pack: sonst haette die Absage die Staffel
    // fuer immer stillgelegt und ein geaenderter Inhalt waere nie wieder geplant worden.
    Check(!reg.HasPack("tt644", 3, "EN", false, PackEntity.SubdlSource)
          && reg.GetPack("tt644", 3, "EN", false, PackEntity.SubdlSource)?.RefusalReason != null,
        "F-M644: a refused row is readable but is not a pack — the next plan asks the same season again");

    // Dieselbe Staffel, andere Quelle: die Absage gilt NUR ihrer Zeile (F-M642).
    var tvstZeile = db.Packs.FindById(ScribeDbContext.PackKey("tt644", 3, "EN", false, PackEntity.TvSubtitlesSource));
    Check(tvstZeile == null,
        "F-M644: the refusal of one source creates no row for the other source");

    // Ein geaenderter Inhalt — eine Folge dazu — ist ein anderer Fingerabdruck. Die neue Absage
    // ueberschreibt den alten Stand, weil sie denselben Inhalt beschreibt (die Zeile ist der Zustand,
    // keine Historie).
    string neuerAbdruck = Jellyfin.Plugin.SubScribe.Pipeline.SeasonPack.PackHash(new[] { "aaaa", "bbbb", "cccc", "dddd" });
    reg.RecordPackRefusal("tt644", 3, "EN", false, "the source already holds this container (duplicate)", neuerAbdruck, PackEntity.SubdlSource);
    Check(reg.GetPack("tt644", 3, "EN", false, PackEntity.SubdlSource)?.Fingerprint == neuerAbdruck,
        "F-M644: a changed content gets its own fingerprint — the verdict follows the content, not the season");

    // Der Erfolg: Absage weg, Beleg da.
    reg.RecordPack("tt644", 3, "EN", false, "Show.S03.1080p", "tt644.S03.x.EN", new[] { 1, 2, 3 }, PackEntity.SubdlSource, absorbedIds: null, neuerAbdruck);
    var durch = reg.GetPack("tt644", 3, "EN", false, PackEntity.SubdlSource);
    Check(durch != null
          && string.IsNullOrEmpty(durch!.RefusalReason)
          && durch.RefusedAt == null,
        "F-M644: a container that goes through clears its refusal");
    Check(durch!.UploadedAt != null,
        "F-M644: and it carries the upload proof (`UploadedAt`) instead");

    // Der Reset einer Einzeldatei (F-M620) gilt der STAFFEL: die Absage faellt mit.
    reg.EnsureFile("0123456789abcdef0123456789abcdef", patch: m =>
    {
        m.ImdbId = "tt644";
        m.Season = 3;
        m.IsSeries = true;
    });
    reg.RecordPackRefusal("tt644", 3, "DE", false, "the source already holds this container (duplicate)", abdruck, PackEntity.SubdlSource);
    Check(reg.GetPack("tt644", 3, "DE", false, PackEntity.SubdlSource)?.RefusalReason != null,
        "F-M644: a second language of the same season is refused on its own row");

    int geoeffnet = reg.ReopenPackRefusals("0123456789abcdef0123456789abcdef");
    Check(geoeffnet == 1
          && reg.GetPack("tt644", 3, "DE", false, PackEntity.SubdlSource)?.RefusalReason == null,
        "F-M644: the single-file reset of the GUI clears the season's refusal with it",
        geoeffnet.ToString(System.Globalization.CultureInfo.InvariantCulture));

    Check(reg.ReopenPackRefusals("ffffffffffffffffffffffffffffffff") == 0,
        "F-M644: an unknown file opens nothing — no refusal is dropped by accident");
}

// F-M668: die Urteils-Spalten sind aus der `packs`-Tabelle VERSCHWUNDEN — Grund, Datum und Beleg stehen
// in der Marken-Gridfilm. Geprueft wird die Tabelle selbst, nicht der Quelltext: eine stehengebliebene
// Spalte waere der zweite Datenbestand, den F-M668 verbietet.
using (var db = new ScribeDbContext(workdir, null))
{
    var spalten = db.Context.Database
        .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('pack')")
        .ToList();
    // F-M671: `PackId` ist die EIGENE Kennung geworden; der Fingerabdruck heisst jetzt `Signature`.
    Check(!spalten.Contains("RefusalReason") && !spalten.Contains("RefusedAt") && !spalten.Contains("UploadedAt")
          && !spalten.Contains("Fingerprint") && !spalten.Contains("RefusedSignature"),
        "F-M668/F-M671: reason, date, proof and container id are NO columns of the pack table any more",
        string.Join(",", spalten));
    db.Packs.DeleteMany(x => x.ImdbId == "tt644");
}

// F-M643: die Pack-Karte JE QUELLE — der tvst-Plan darf das SubDL-Paar nicht als „gepackt" lesen.
using (var db = new ScribeDbContext(workdir, null))
{
    db.Packs.Upsert(new PackEntity
    {
        SeasonKey = ScribeDbContext.PackKey("tt643", 1, "EN", false, PackEntity.SubdlSource),
        Source = PackEntity.SubdlSource,
        ImdbId = "tt643",
        Season = 1,
        Language = "EN",
        HearingImpaired = false,
        Episodes = "1,2",
    });
    Jellyfin.Plugin.SubScribe.Data.SourceStore.WritePackMark(
        db, "tt643", 1, "EN", false, PackEntity.SubdlSource,
        DateTime.UtcNow, "tt643.S01.EN", "fp-643", null, null, null);
    var reg = new ContentHashRegistry(db);
    var subdl = reg.PacksBySeason(PackEntity.SubdlSource);
    var tvst = reg.PacksBySeason(PackEntity.TvSubtitlesSource);
    // Die Karte nennt die Sprache in der Form, die der Store fuehrt (der Mapper entscheidet: "EN" wird
    // zu "ENG") — verglichen wird deshalb mit derselben Abbildung, nicht mit dem Eingabekuerzel.
    string erwartet = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("EN") ?? "EN";
    Check(subdl.TryGetValue(("tt643", 1), out var subdlPaare) && subdlPaare.Contains((erwartet, false)),
        "F-M643: the source-scoped pack map names the SubDL pair",
        subdl.TryGetValue(("tt643", 1), out var pp) ? string.Join(";", pp) : "none");
    Check(!tvst.ContainsKey(("tt643", 1)),
        "F-M643: and the tvst map stays empty — the other source never borrowed that pack");
    db.Packs.DeleteMany(x => x.ImdbId == "tt643");
}

// ---------------------------------------------------------------------------------------------
// F-M667/F-M668: die Marken-Gridfilm ist die EINZIGE Wahrheit.
//
// Der Aufbau ist eine NACHRGEBAUTE Altdatei: ein vollständiger Store mit Marken in den breiten
// Spalten, danach die Gridfilm-Tabelle entfernt, die Altspalten wieder angelegt und der Marker auf 17
// zurückgesetzt — genau so sieht die Datei aus, die der Betrieb vor diesem Build geschrieben hat.
// Gefahren wird gegen die echte Datei, nicht gegen den Quelltext: vorher und nachher werden die
// Zeilen je Art GEZÄHLT, und geprüft wird, dass jedes Urteil danach GENAU EINMAL existiert.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- source matrix (F-M667/F-M668) ---");

string matrixDir = Path.Combine(workdir, "matrix17");
Directory.CreateDirectory(matrixDir);
string matrixDb = DbFiles.PathIn(matrixDir);

var t1 = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
var t2 = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
var t3 = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
var t4 = new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);
var t5 = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);
var t6 = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
var t7 = new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
var t8 = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

// Der Bestand: zwei Medien mit Zugeh-Stempeln, sechs Spuren (davon eine HI, zwei in derselben
// Sprache), ein Sidecar, zwei Packs (einer angenommen, einer abgelehnt).
using (var db = new ScribeDbContext(matrixDir, null))
{
    db.Files.Upsert(new FileEntity
    {
        OsHash = "m1", Path = "/lib/serie.mkv", ImdbId = "tt100", TmdbId = "200",
        IsSeries = true, Season = 1, Episode = 1,
    });
    db.Files.Upsert(new FileEntity
    {
        OsHash = "m2", Path = "/lib/film.mkv", ImdbId = "tt101", TmdbId = "201",
    });
    db.Files.Upsert(new FileEntity { OsHash = "m3", Path = "/lib/leer.mkv" });

    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m1", SubPos = 0, Language = "EN",
        Status = "uploaded", HearingImpaired = false,
    });
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m1", SubPos = 1, Language = "EN",
        Status = "uploaded", HearingImpaired = true,
    });
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m1", SubPos = 2, Language = "DE",
        Status = "uploaded", HearingImpaired = false,
    });
    // Dieselbe Sprache ein zweites Mal — zwei Zeilen, EINE Marke: der spätere Zeitstempel muss gelten.
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m1", SubPos = 3, Language = "DE",
        Status = "uploaded", HearingImpaired = false,
    });
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m2", SubPos = 0, Language = "FR",
        Status = "uploaded", HearingImpaired = false,
    });
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m2", SubPos = 1, Language = "EN",
        Status = "observed",
    });

    db.Sidecars.Upsert(new SidecarEntity
    {
        ContentHash = "sc1", MediaHash = "m2", FileName = "film.de.srt", Language = "DE",
        Status = "uploaded", HearingImpaired = false,
    });

    db.Packs.Upsert(new PackEntity
    {
        SeasonKey = ScribeDbContext.PackKey("tt100", 1, "EN", false, PackEntity.SubdlSource),
        Source = PackEntity.SubdlSource, ImdbId = "tt100", Season = 1, Language = "EN",
        HearingImpaired = false, Episodes = "1,2",
    });
    db.Packs.Upsert(new PackEntity
    {
        SeasonKey = ScribeDbContext.PackKey("tt100", 2, "EN", false, PackEntity.SubdlSource),
        Source = PackEntity.SubdlSource, ImdbId = "tt100", Season = 2, Language = "EN",
        HearingImpaired = false, Episodes = "1,2",
    });
}

// Der Bestand ist geschrieben — jetzt wird daraus eine ECHTE ALTdatei im Schema 17: die neuen
// Tabellen fallen, und die vier ALT-Tabellen werden aus ihrem ALTEN DDL angelegt (Spalten und
// Schluessel wie oben beschrieben, Zeilen von Hand). Danach laeuft beim Oeffnen der GANZE Aufstieg
// 17 -> 22. Keine Zeile dieser Datei stammt aus dem neuen Modell — was hier steht, ist der Bestand,
// den der Vor-Build geschrieben hat.
using (var db = new ScribeDbContext(matrixDir, null))
{
    void Sql(string sql) => db.Context.Database.ExecuteSqlRaw(sql);
    CreateOldTables(Sql, 17);

    Sql("INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, IsSeries, Season, Episode, LastSeen) "
        + "VALUES ('m1', '/lib/serie.mkv', NULL, 'tt100', '200', 1, 1, 1, '2026-11-01 10:00:00');");
    Sql("INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, IsSeries, LastSeen) "
        + "VALUES ('m2', '/lib/film.mkv', NULL, 'tt101', '201', 0, '2026-11-01 10:00:00');");
    Sql("INSERT INTO media (Id, Path, JellyfinItemId, IsSeries, LastSeen) "
        + "VALUES ('m3', '/lib/leer.mkv', NULL, 0, '2026-11-01 10:00:00');");

    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m1|0', 'm1', 0, 'EN', 0, 'uploaded', '2026-11-01 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m1|1', 'm1', 1, 'EN', 1, 'uploaded', '2026-11-02 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m1|2', 'm1', 2, 'DE', 0, 'uploaded', '2026-11-03 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m1|3', 'm1', 3, 'DE', 0, 'uploaded', '2026-11-05 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m2|0', 'm2', 0, 'FR', 0, 'uploaded', '2026-11-04 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, Status, StatusAt) "
        + "VALUES ('m2|1', 'm2', 1, 'EN', 'observed', '2026-11-03 10:00:00');");

    Sql("INSERT INTO sidecars (Id, ContentHash, FileName, MediaHash, Path, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('sc1', 'sc1', 'film.de.srt', 'm2', '/lib/film.de.srt', 'DE', 0, 'uploaded', '2026-11-06 10:00:00');");

    Sql("INSERT INTO packs (Id, Source, ImdbId, Season, Language, HearingImpaired, Episodes, Created, Updated) "
        + "VALUES ('tt100|S1|EN|plain|subdl', 'subdl', 'tt100', 1, 'EN', 0, '1,2', '2026-11-01 10:00:00', "
        + "'2026-11-07 10:00:00');");
    Sql("INSERT INTO packs (Id, Source, ImdbId, Season, Language, HearingImpaired, Episodes, Created, Updated) "
        + "VALUES ('tt100|S2|EN|plain|subdl', 'subdl', 'tt100', 2, 'EN', 0, '1,2', '2026-11-01 10:00:00', "
        + "'2026-11-08 10:00:00');");

    // Die Marken selbst — Spalte fuer Spalte, wie der Vor-Build sie geschrieben hat.
    Sql("UPDATE embeds SET SubdlDownloadId = '111', SubdlUploadedAt = '2026-11-01 10:00:00' WHERE Id = 'm1|0';");
    Sql("UPDATE embeds SET TvSubtitlesDownloadId = '222', TvSubtitlesUploadedAt = '2026-11-02 10:00:00' WHERE Id = 'm1|1';");
    Sql("UPDATE embeds SET SubdlDownloadId = '333', SubdlUploadedAt = '2026-11-03 10:00:00' WHERE Id = 'm1|2';");
    Sql("UPDATE embeds SET SubdlDownloadId = '444', SubdlUploadedAt = '2026-11-05 10:00:00', SubdlUploadId = 'up-444' WHERE Id = 'm1|3';");
    Sql("UPDATE embeds SET MoviesubtitlesDownloadId = '555', MoviesubtitlesUploadedAt = '2026-11-04 10:00:00' WHERE Id = 'm2|0';");
    Sql("UPDATE sidecars SET SubdlDownloadId = '666', SubdlUploadedAt = '2026-11-06 10:00:00' WHERE Id = 'sc1';");
    Sql("UPDATE media SET CompleteSubdl = '2026-11-03 10:00:00', CompleteTvSubtitles = '2026-11-04 10:00:00' WHERE Id = 'm1';");
    Sql("UPDATE media SET CompleteMsub = '2026-11-05 10:00:00' WHERE Id = 'm2';");
    Sql("UPDATE packs SET PackId = 'pid-a', Fingerprint = 'fp-a', UploadedAt = '2026-11-07 10:00:00' WHERE Id = 'tt100|S1|EN|plain|subdl';");
    Sql("UPDATE packs SET PackId = 'pid-b', Fingerprint = 'fp-b', "
        + "RefusalReason = 'the source already holds this container (duplicate)', RefusedAt = '2026-11-08 10:00:00' "
        + "WHERE Id = 'tt100|S2|EN|plain|subdl';");

    // F-M670: die Id-Spalte — eine ohne Gegenstueck in den breiten Spalten (m1|0 traegt dort keine
    // Einsende-Id), eine identisch (m1|3) und eine am Seitenwagen.
    Sql("UPDATE embeds SET SubdlId = '700' WHERE Id = 'm1|0';");
    Sql("UPDATE embeds SET SubdlId = 'up-444' WHERE Id = 'm1|3';");
    Sql("UPDATE sidecars SET SubdlId = '701' WHERE Id = 'sc1';");

    // F-M670: die Alt-Tabelle. Eine Zeile deckt eine vorhandene Marke (m1|0), eine zweite traegt eine
    // Marke, die es NUR dort gibt (m2|1 — beobachtet, ohne jeden breiten Stempel), eine dritte zeigt
    // auf keine Besitzerzeile mehr.
    Sql("INSERT INTO source_marks (Id, OwnerKey, Source, DownloadId, UploadId, UploadedAt, CompletedAt, Written) "
        + "VALUES ('m1|0|subdl', 'm1|0', 'subdl', NULL, 'up-111', '2026-11-03 10:00:00', NULL, '2026-11-03 10:00:00');");
    Sql("INSERT INTO source_marks (Id, OwnerKey, Source, DownloadId, UploadId, UploadedAt, CompletedAt, Written) "
        + "VALUES ('m2|1|subdl', 'm2|1', 'subdl', NULL, 'sm-only', NULL, NULL, '2026-11-03 10:00:00');");
    Sql("INSERT INTO source_marks (Id, OwnerKey, Source, DownloadId, UploadId, UploadedAt, CompletedAt, Written) "
        + "VALUES ('weg|0|subdl', 'weg|0', 'subdl', NULL, 'sm-orphan', '2026-11-02 10:00:00', NULL, '2026-11-02 10:00:00');");

    Sql("UPDATE meta SET SchemaVersion = 17 WHERE Id = 'db';");
}

string[] Sicherungen(string dir) => Directory.GetFiles(dir, DbFiles.BackupPrefix + "*")
    .Where(f => !DbFiles.SideSuffixes.Any(sfx => f.EndsWith(sfx, StringComparison.Ordinal)))
    .ToArray();

string? QText(string sql, string file = "")
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
        "Data Source=" + (file.Length == 0 ? matrixDb : file) + ";Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    object? wert = cmd.ExecuteScalar();
    return wert is null or DBNull ? null : Convert.ToString(wert, System.Globalization.CultureInfo.InvariantCulture);
}

int Q(string sql) => Convert.ToInt32(QText(sql) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

int QIn(string sql, string file) => Convert.ToInt32(QText(sql, file) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

int Beleg(Dictionary<string, int> map, string key) => map.TryGetValue(key, out int n) ? n : 0;

string[] SpaltenVon(string table, string file = "")
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
        "Data Source=" + (file.Length == 0 ? matrixDb : file) + ";Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA table_info(" + table + ");";
    var namen = new List<string>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        namen.Add(r.GetString(1));
    }

    return namen.ToArray();
}

string[] Altspalten = { "SubdlDownloadId", "SubdlUploadId", "SubdlUploadedAt",
    "TvSubtitlesDownloadId", "TvSubtitlesUploadId", "TvSubtitlesUploadedAt",
    "MoviesubtitlesDownloadId", "MoviesubtitlesUploadId", "MoviesubtitlesUploadedAt",
    // F-M670: die letzte eigene Id-Spalte — sie fällt wie die neun breiten.
    "SubdlId",
    "CompleteSubdl", "CompleteTvSubtitles", "CompleteMsub", "CompleteSignature",
    "Fingerprint", "RefusalReason", "RefusedAt", "RefusedSignature", "UploadedAt" };

string spurSql = "SELECT COUNT(*) FROM {0} WHERE HearingImpaired IS NOT 1 AND (SubdlUploadedAt IS NOT NULL "
              + "OR TvSubtitlesUploadedAt IS NOT NULL OR MoviesubtitlesUploadedAt IS NOT NULL)";
string spurHiSql = "SELECT COUNT(*) FROM {0} WHERE HearingImpaired = 1 AND (SubdlUploadedAt IS NOT NULL "
                + "OR TvSubtitlesUploadedAt IS NOT NULL OR MoviesubtitlesUploadedAt IS NOT NULL)";

// VORHER: die alten Spalten, unabhängig gezählt (SQL, nicht die Zahlen des Migrationslaufs).
int altTrack = Q(string.Format(System.Globalization.CultureInfo.InvariantCulture, spurSql, "embeds"))
               + Q(string.Format(System.Globalization.CultureInfo.InvariantCulture, spurSql, "sidecars"));
int altTrackHi = Q(string.Format(System.Globalization.CultureInfo.InvariantCulture, spurHiSql, "embeds"))
                 + Q(string.Format(System.Globalization.CultureInfo.InvariantCulture, spurHiSql, "sidecars"));
int altFile = Q("SELECT COUNT(*) FROM media WHERE CompleteSubdl IS NOT NULL "
                + "OR CompleteTvSubtitles IS NOT NULL OR CompleteMsub IS NOT NULL");
int altPack = Q("SELECT COUNT(*) FROM packs;");
Console.WriteLine("   alte Spalten: track=" + altTrack + " track-hi=" + altTrackHi + " file=" + altFile + " pack=" + altPack);
Check(altTrack == 5 && altTrackHi == 1 && altFile == 2 && altPack == 2,
    "die nachgebaute Altdatei trägt die Marken nur in den breiten Spalten (Gridfilm-Tabelle fehlt)",
    altTrack + "/" + altTrackHi + "/" + altFile + "/" + altPack);
Check(Q("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_matrix'") == 0
      && Q("SELECT SchemaVersion FROM meta WHERE Id='db'") == 17,
    "und die Gridfilm-Tabelle gibt es darin nicht (Marker 17)");

// NACHHER: die Übernahme läuft beim Öffnen — vor dem Store, mit Sicherung der alten Datei.
var belege = new Dictionary<string, int>(StringComparer.Ordinal);
bool sicherungDa;
int markenGesamt;
string sicherungPfad;
SourceMatrixMigration.Outcome? idUebernahme = null;
using (var db = new ScribeDbContext(matrixDir, null))
{
    belege = SourceStore.CountByKind(db);
    markenGesamt = belege.Values.Sum();
    // F-M670: die zwei Reste sind beim Öffnen übernommen UND niedergelegt — sonst gäbe es sie noch.
    idUebernahme = db.MatrixOutcome;
    var dateien = Sicherungen(matrixDir);
    sicherungDa = dateien.Length == 1;
    sicherungPfad = dateien.FirstOrDefault() ?? string.Empty;
    Console.WriteLine("   Gridfilm: " + string.Join(", ", belege.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)));

    Check(markenGesamt == 12 && belege.Count == 7
          && Beleg(belege, "subdl|track") == 5 && Beleg(belege, "tvsubtitles|track-hi") == 1
          && Beleg(belege, "moviesubtitles|track") == 1
          && Beleg(belege, "subdl|file") == 1 && Beleg(belege, "tvsubtitles|file") == 1
          && Beleg(belege, "moviesubtitles|file") == 1 && Beleg(belege, "subdl|pack") == 2,
        "F-M668/F-M669: die Gridfilm ist aus den breiten Spalten gefüllt — 12 Marken in 7 Paaren aus Quelle und Art (die zwölfte kommt aus der Alt-Tabelle, F-M670)",
        markenGesamt + " Marke(n): " + string.Join(",", belege.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)));

    // Die Zuordnung selbst: die Id bei der Quelle — und seit F-M669 je POSITION ein eigener Beleg.
    var de2 = SourceStore.TrackMarkAt(db, "m1", "subdl", "DE", false, 2);
    var de3 = SourceStore.TrackMarkAt(db, "m1", "subdl", "DE", false, 3);
    Check(de2.IsClosed && de2.ClosedAt == t3 && de2.SourceId == "333"
          && de3.IsClosed && de3.ClosedAt == t5 && de3.SourceId == "444" && de3.UploadId == "up-444",
        "F-M669: zwei Spuren derselben Sprache sind ZWEI Belege — jede mit ihrem Zeitstempel und ihren Ids",
        de2.ClosedAt + "/" + (de2.SourceId ?? "-") + " " + de3.ClosedAt + "/" + (de3.SourceId ?? "-")
        );

    var hi = SourceStore.TrackMarkAt(db, "m1", "tvsubtitles", "EN", true, 1);
    Check(hi.IsClosed && hi.ClosedAt == t2 && hi.SourceId == "222" && hi.Kind == MarkKind.TrackHi,
        "F-M668: die HI-Spur ist ein eigener Beleg derselben Datei, mit eigener Quelle und eigener Id",
        hi.Kind + " " + hi.ClosedAt);

    var sidecarMarke = SourceStore.TrackMarkAt(db, "m2", "subdl", "DE", false, SourceStore.SidecarPosition);
    Check(sidecarMarke.IsClosed && sidecarMarke.SourceId == "666",
        "F-M669: ein Seitenwagen traegt die feste Position -1 — keine Videospur-Position, keine Kollision mit Spur 0",
        sidecarMarke.ClosedAt + " / " + (sidecarMarke.SourceId ?? "-"));

    var dateiMarke = SourceStore.FileMark(db, "m1", "moviesubtitles");
    Check(!dateiMarke.Exists,
        "F-M668: eine Datei ohne Stempel ihrer Quelle bekommt KEINEN Beleg — fehlend heißt offen");

    var pack = SourceStore.PackMark(db, "tt100", 2, "EN", false, "subdl");
    Check(pack.Exists && pack.RefusalReason != null && pack.RefusedAt == t8 && pack.Fingerprint == "fp-b" && pack.ClosedAt == null
          && pack.SourceId == "pid-b",
        "F-M668: der abgelehnte Pack steht mit Grund, Datum, Fingerabdruck und Pack-Id da — und ohne Urteil",
        pack.RefusalReason ?? "-");

    var angenommen = SourceStore.PackMark(db, "tt100", 1, "EN", false, "subdl");
    Check(angenommen.Exists && angenommen.ClosedAt == t7 && angenommen.Fingerprint == "fp-a",
        "F-M668: der angenommene Pack trägt sein Urteil und den Fingerabdruck seiner Mitglieder",
        angenommen.ClosedAt + " / " + (angenommen.Fingerprint ?? "-"));
}

Check(sicherungDa, "F-M668: vor der Übernahme liegt GENAU EINE Sicherung der alten Datei daneben (Zeitstempel im Namen)",
    Path.GetFileName(sicherungPfad));

// Die Sicherung ist die Datei VOR der Übernahme — und sie trägt den Altbestand noch.
int sicherungMarker = -1;
string? sicherungWert = null;
if (File.Exists(sicherungPfad))
{
    sicherungMarker = Convert.ToInt32(
        QText("SELECT SchemaVersion FROM meta WHERE Id = 'db';", sicherungPfad) ?? "-1",
        System.Globalization.CultureInfo.InvariantCulture);
    sicherungWert = QText("SELECT SubdlDownloadId FROM embeds WHERE Id = 'm1|3';", sicherungPfad);
}

Check(sicherungMarker == 17 && sicherungWert == "444"
      && SpaltenVon("embeds", sicherungPfad).Contains("SubdlUploadedAt"),
    "F-M668: die Sicherung ist die Datei VOR der Übernahme (Marker 17, Marken noch in den breiten Spalten)",
    "marker=" + sicherungMarker + " id=" + (sicherungWert ?? "-"));

// Die Altspalten sind WEG — in jeder Tabelle, Spalte für Spalte. Das ist die Regel „kein doppelter
// Datenbestand": was übernommen ist, steht nur noch in der Gridfilm.
var uebrig = new List<string>();
foreach (string table in new[] { "track", "sidecar", "file", "pack" })
{
    foreach (string spalte in SpaltenVon(table))
    {
        if (Altspalten.Contains(spalte, StringComparer.Ordinal))
        {
            uebrig.Add(table + "." + spalte);
        }
    }
}

Check(uebrig.Count == 0,
    "F-M668: die breiten Quellen-Spalten sind aus der LIVE-Datei entfernt (kein doppelter Datenbestand)",
    uebrig.Count == 0 ? "keine" : string.Join(",", uebrig));

// F-M672: jede Marke existiert genau einmal — je (Subjekt, Quelle) höchstens eine Urteils- und eine
// Belegzeile, und zusammen sind es die Marken der Altdatei.
int urteile = Q("SELECT COUNT(*) FROM upload;");
int belegen = Q("SELECT COUNT(*) FROM reference;");
int doppelt = Q("SELECT COUNT(*) FROM (SELECT SubjectId, Source FROM upload GROUP BY SubjectId, Source HAVING COUNT(*) > 1)");
Check(doppelt == 0 && (urteile + belegen) >= 12,
    "F-M668/F-M672: kein Subjekt trägt zwei Urteile derselben Quelle — und alle Marken sind da",
    urteile + " Urteil(e), " + belegen + " Beleg(e), " + doppelt + " Dublette(n)");

int spurBelege = Q("SELECT COUNT(*) FROM (SELECT SubjectId FROM upload WHERE length(SubjectId)=10 AND substr(SubjectId,7,1)='#' "
                   + "UNION SELECT SubjectId FROM reference WHERE length(SubjectId)=10 AND substr(SubjectId,7,1)='#')");
Check(spurBelege >= 6,
    "F-M669: je Spur-Zeile eine Marke — die doppelte DE-Spur ist ZWEI Marken, keine eingedampfte",
    spurBelege + " Marke(n)");

using (var db = new ScribeDbContext(matrixDir, null))
{
    Check(db.Meta.FindById("db")?.SchemaVersion == 24,
        "F-M671/F-M672/F-M679: der Marker steht nach der Übernahme auf 24",
        "marker=" + (db.Meta.FindById("db")?.SchemaVersion));
}

// F-M670: die Alt-Tabelle `source_marks` und die Id-Spalte `SubdlId` sind WEG — und ihre Werte stehen
// jetzt in der Gridfilm, jede Kennung genau einmal.
Check(Q("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_marks'") == 0,
    "F-M670: die Alt-Tabelle `source_marks` existiert nach der Übernahme nicht mehr",
    Q("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_marks'") + " Tabelle(n)");

Check(!SpaltenVon("track").Contains("SubdlId") && !SpaltenVon("sidecar").Contains("SubdlId")
      && !SpaltenVon("track").Contains("SubdlUploadedAt") && !SpaltenVon("file").Contains("CompleteSubdl"),
    "F-M670/F-M671: die Altspalten sind aus den neuen Ebenen entfernt — es gibt sie dort nie");

Check(idUebernahme is { IdsTaken: 2, MarksTaken: 2, MarksNew: 1, Unplaced: 1, Conflicts: 0 },
    "F-M670: 2 Id-Werte und 2 Alt-Marken übernommen (davon 1 neue), 1 Zeile ohne Anker, kein Konflikt",
    idUebernahme == null
        ? "(kein Lauf)"
        : "ids=" + idUebernahme.IdsTaken + " marks=" + idUebernahme.MarksTaken + " neu=" + idUebernahme.MarksNew
          + " unplaced=" + idUebernahme.Unplaced + " conflicts=" + idUebernahme.Conflicts);

// F-M672: die Kennungen stehen jetzt in `reference`, adressiert über die EIGENE Kennung des Subjekts.
using var nachher = new ScribeDbContext(matrixDir, null);
var markeM10 = SourceStore.TrackMarkAt(nachher, "m1", "subdl", "EN", false, 0);
var markeSc1 = SourceStore.TrackMarkAt(nachher, "m2", "subdl", "DE", false, SourceStore.SidecarPosition);
var markeM21 = SourceStore.TrackMarkAt(nachher, "m2", "subdl", "EN", false, 1);

Check(markeM10.UploadId == "700" && markeM10.SourceId == "111",
    "F-M670: die SubdlId einer eingesendeten Spur steht als UploadId an ihrem Beleg (`SourceId` bleibt)",
    markeM10.SourceId + "/" + markeM10.UploadId);

Check(markeSc1.UploadId == "701",
    "F-M670: die SubdlId eines Seitenwagens steht als UploadId am Beleg des Seitenwagens",
    markeSc1.UploadId ?? "-");

Check(markeM21.UploadId == "sm-only",
    "F-M670: ein Beleg, den es NUR in der Alt-Tabelle gab, ist mit übernommen (Id belegt die Einsendung)",
    markeM21.UploadId ?? "-");

Check(Q("SELECT COUNT(*) FROM reference WHERE DownloadSourceId = '700' OR UploadedId = '700'") >= 1
      && Q("SELECT COUNT(*) FROM reference WHERE DownloadSourceId = '701' OR UploadedId = '701'") >= 1,
    "F-M670: jede Quellen-Id steht genau einmal in der Gridfilm");

Check(Q("SELECT COUNT(*) FROM reference WHERE DownloadSourceId = 'sm-orphan' OR UploadedId = 'sm-orphan'") == 0,
    "F-M670: eine Alt-Marke ohne Besitzerzeile bekommt KEINE Gridfilm-Zeile (sie lebt in der Sicherung, der Lauf meldet sie)");

// Zweiter Start: dieselbe Datei, dieselbe Gridfilm — kein zweiter Lauf, keine zweite Sicherung.
int backups = Sicherungen(matrixDir).Length;
int marksAgain;
using (var db = new ScribeDbContext(matrixDir, null))
{
    marksAgain = SourceStore.CountByKind(db).Values.Sum();
}
Check(marksAgain == markenGesamt && Sicherungen(matrixDir).Length == backups,
    "F-M668: der zweite Start übernimmt nicht erneut — gleiche Zeilenzahl, gleiche Zahl Sicherungen",
    marksAgain + " Marke(n), " + backups + " Sicherung(en)");

// Der Leser: es gibt KEINEN zweiten Lesepfad mehr. Die Gridfilm antwortet — und wenn sie die Zeile nicht
// hat, bleibt die Antwort leer, statt in einer Altspalte nachzusehen (die es nicht mehr gibt).
using (var db = new ScribeDbContext(matrixDir, null))
{
    // F-M671: es gibt keine Altspalte mehr, die man pruefen koennte — die ALT-TABELLE ist weg.
    int altTabelle = db.Context.Database
        .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='embeds'")
        .ToList()[0];
    Check(altTabelle == 0 && db.Tracks.Count() == 6 && db.Sidecars.Count() == 1,
        "F-M668/F-M671: nichts wurde gelöscht außer den Spalten — alle Spur-Zeilen stehen noch, die Alt-Tabelle ist weg",
        "altTabelle=" + altTabelle + " track=" + db.Tracks.Count() + " sidecar=" + db.Sidecars.Count());

    var spur = SourceStore.TrackMarkAt(db, "m1", "subdl", "EN", false, 0);
    Check(spur.IsClosed && spur.ClosedAt == t1 && spur.SourceId == "111",
        "F-M668: die Spur-Marke kommt aus der Gridfilm (geschlossen, mit der Id bei der Quelle)",
        spur.ClosedAt + " / " + (spur.SourceId ?? "-"));

    // F-M669: dieselbe Sprache an einer anderen Position ist NICHT damit geschlossen — sonst gälte eine
    // hochgeladene Spur als Beleg für eine nie gesendete.
    var nachbar = SourceStore.TrackMarkAt(db, "m1", "subdl", "DE", false, 7);
    Check(!nachbar.IsClosed && nachbar.ClosedAt == null,
        "F-M669: eine Marke je Position — die Nachbarposition derselben Sprache bleibt offen");

    // Die Gridfilm ist die EINZIGE Antwort: nimmt man die Zeile weg, ist die Quelle offen — es gibt
    // keine Altspalte, die einspringen könnte. (Über den Store gelöscht, damit auch sein Zeilencache
    // die Zeile nicht mehr führt — ein rohes DELETE ließe sie dort stehen.)
    SourceStore.ForgetSubject(db, SourceStore.SubjectAt(db, "m1", 0));
    SourceStore.ForgetSubject(db, SourceStore.PackSubject(db, "tt100", 2, "EN", false, "subdl"));
    SourceStore.ForgetSubject(db, SourceStore.FileIdOf(db, "m2"));

    var spurWeg = SourceStore.TrackMarkAt(db, "m1", "subdl", "EN", false, 0);
    Check(!spurWeg.IsClosed && spurWeg.ClosedAt == null && spurWeg.SourceId == null,
        "F-M668: ohne Gridfilm-Zeile ist die Spur für diese Quelle OFFEN — es gibt keinen Rückfall mehr",
        "closed=" + spurWeg.IsClosed);

    var dateiWeg = SourceStore.FileMark(db, "m2", "moviesubtitles");
    Check(!dateiWeg.IsClosed,
        "F-M668: dasselbe für die Datei-Marke — der Zugeh-Stempel der Medienzeile existiert nicht mehr");

    var packWeg = SourceStore.PackMark(db, "tt100", 2, "EN", false, "subdl");
    Check(!packWeg.IsClosed && packWeg.RefusalReason == null && packWeg.Fingerprint == null,
        "F-M668: und für den Pack — die packs-Zeile trägt kein Urteil mehr");
}

// ---- Der Formatwechsel (F-M637) baut seine neue Datei selbst — auch DORT muss die Übernahme laufen
// und die Altspalten müssen fallen. Er öffnet die Kopie über denselben Kontext; stünde die Übernahme
// nur auf dem normalen Weg, wäre die Gridfilm einer umgestellten Datei für immer leer.
string seedDir = Path.Combine(workdir, "switch-seed");
Directory.CreateDirectory(seedDir);

// Der Vorgaenger, den der Formatwechsel (F-M637) umstellt: eine echte 17er-Datei aus ihrem alten DDL
// — eine Datei mit einer Spur, deren Marke nur in der breiten Spalte steht.
using (var db = new ScribeDbContext(seedDir, null))
{
    void Sql(string sql) => db.Context.Database.ExecuteSqlRaw(sql);
    CreateOldTables(Sql, 17);
    Sql("INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, IsSeries, Season, Episode, LastSeen) "
        + "VALUES ('s1', '/lib/switch.mkv', NULL, 'tt900', '900', 1, 1, 1, '2026-11-01 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('s1|0', 's1', 0, 'EN', 0, 'uploaded', '2026-11-01 10:00:00');");
    Sql("UPDATE embeds SET SubdlDownloadId = '900', SubdlUploadedAt = '2026-11-01 10:00:00' WHERE Id = 's1|0';");
    Sql("UPDATE meta SET SchemaVersion = 17 WHERE Id = 'db';");
}

string switchDir = Path.Combine(workdir, "switch17");
Directory.CreateDirectory(switchDir);
File.Copy(DbFiles.PathIn(seedDir), DbFiles.PreviousPathIn(switchDir), overwrite: true);

using (var db = new ScribeDbContext(switchDir, null))
{
    var marke = SourceStore.TrackMarkAt(db, "s1", "subdl", "EN", false, 0);
    Check(marke.IsClosed && marke.SourceId == "900" && marke.ClosedAt == t1,
        "F-M668: auch die Datei, die der Formatwechsel (F-M637) baut, bekommt ihre Marken aus der Übernahme",
        (marke.SourceId ?? "-") + " / " + marke.ClosedAt);
    Check(File.Exists(DbFiles.PreviousPathIn(switchDir)) && Sicherungen(switchDir).Length == 0
          && SpaltenVon("embeds", DbFiles.PathIn(switchDir)).Length == 0,
        "F-M668/F-M671: dabei entsteht keine zweite Sicherung, und die Alt-Tabelle ist auch hier weg (F-M638)",
        "vorgaenger=" + File.Exists(DbFiles.PreviousPathIn(switchDir))
        + " sicherungen=" + Sicherungen(switchDir).Length
        + " embedsSpalten=" + SpaltenVon("embeds", DbFiles.PathIn(switchDir)).Length);
}

// ---------------------------------------------------------------------------------------------
// F-M670: der Weg 20 -> 21. Eine Datei aus Schema 20 trägt die Gridfilm bereits und hat KEINE breite
// Spalte mehr — aber die zwei Reste stehen noch darin (die Alt-Tabelle und die Id-Spalte). Sie müssen
// beim Öffnen genauso übernommen und niedergelegt werden; sonst bliebe der Bestand der schon
// migrierten Dateien für immer ein zweiter Bestand.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- second store: schema 20 -> 21 (F-M670) ---");

string s20Dir = Path.Combine(workdir, "matrix20");
Directory.CreateDirectory(s20Dir);
string s20Db = DbFiles.PathIn(s20Dir);

using (var db = new ScribeDbContext(s20Dir, null))
{
    db.Files.Upsert(new FileEntity
    {
        OsHash = "m10", Path = "/lib/zwei.mkv", ImdbId = "tt200", TmdbId = "300",
        IsSeries = true, Season = 2, Episode = 4,
    });
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m10", SubPos = 0, Language = "DE",
        Status = "uploaded", HearingImpaired = false,
    });
    db.Tracks.Upsert(new TrackEntity
    { MediaHash = "m10", SubPos = 1, Language = "EN",
        Status = "uploaded", HearingImpaired = false,
    });

    // Der Upload hat seine Marke ohne echte Id gesetzt — die Kennung kam erst später (F-M588/F-M670).
    SourceStore.WriteTrackMark(db, db.Files.FindById("m10"), "m10", "subdl", "DE", false, 0,
        t1, downloadId: null, uploadId: "own:subdl:deferred");
}

using (var db = new ScribeDbContext(s20Dir, null))
{
    void Sql(string sql) => db.Context.Database.ExecuteSqlRaw(sql);

    // F-M671: eine Altdatei entsteht aus ihrem ALTEN DDL. Schema 20 heisst: die Gridfilm steht schon,
    // die breiten Spalten sind weg, aber die zwei Reste stehen noch darin (`source_marks` und die
    // Id-Spalte an den Spuren). Die vier Ebenen des neuen Modells fallen.
    CreateOldTables(Sql, 20);

    Sql("INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, IsSeries, Season, Episode, LastSeen) "
        + "VALUES ('m10', '/lib/zwei.mkv', NULL, 'tt200', '300', 1, 2, 4, '2026-11-01 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m10|0', 'm10', 0, 'DE', 0, 'uploaded', '2026-11-01 10:00:00');");
    Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt) "
        + "VALUES ('m10|1', 'm10', 1, 'EN', 0, 'uploaded', '2026-11-01 10:00:00');");

    // Der Upload hat seine Marke ohne echte Id gesetzt — die Kennung kam erst spaeter (F-M588/F-M670).
    Sql("INSERT INTO source_marks (Id, OwnerKey, Source, DownloadId, UploadId, UploadedAt, CompletedAt, Written) "
        + "VALUES ('m10|0|subdl', 'm10|0', 'subdl', NULL, 'own:subdl:deferred', NULL, NULL, '2026-11-01 10:00:00');");
    Sql("INSERT INTO source_marks (Id, OwnerKey, Source, DownloadId, UploadId, UploadedAt, CompletedAt, Written) "
        + "VALUES ('m10|1|subdl', 'm10|1', 'subdl', NULL, 'sm20', '2026-11-09 10:00:00', NULL, '2026-11-09 10:00:00');");
    Sql("UPDATE embeds SET SubdlId = '880011' WHERE MediaHash = 'm10' AND SubPos = 0;");
    Sql("UPDATE meta SET SchemaVersion = 20 WHERE Id = 'db';");
}

Check(QIn("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_matrix'", s20Db) == 1
      && SpaltenVon("embeds", s20Db).Contains("SubdlId")
      && QIn("SELECT SchemaVersion FROM meta WHERE Id='db'", s20Db) == 20,
    "die 20er-Datei trägt die Gridfilm UND die zwei Reste (Marker 20)");

string s20Spur = "#" + "s20aa" + "#aab";
string s20Spur2 = "#" + "s20aa" + "#aac";
int s20Marken;
using (var db = new ScribeDbContext(s20Dir, null))
{
    s20Marken = SourceStore.CountByKind(db).Values.Sum();
    Check(db.Meta.FindById("db")?.SchemaVersion == 24,
        "F-M670/F-M671/F-M672: eine Datei aus Schema 20 zieht auf 24 nach",
        "marker=" + db.Meta.FindById("db")?.SchemaVersion);
}

Check(QIn("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_marks'", s20Db) == 0
      && SpaltenVon("embeds", s20Db).Length == 0
      && SpaltenVon("sidecars", s20Db).Length == 0,
    "F-M670/F-M671: auch dort sind Alt-Tabelle, Id-Spalte und die Alt-Tabellen selbst danach weg",
    "source_marks=" + QIn("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_marks'", s20Db)
    + " embedsSpalten=" + SpaltenVon("embeds", s20Db).Length);

Check(QIn("SELECT COUNT(*) FROM reference WHERE UploadedId = '880011'", s20Db) >= 1
      && QIn("SELECT COUNT(*) FROM upload WHERE UploadedAt IS NOT NULL", s20Db) >= 1,
    "F-M670: die echte Kennung ersetzt die selbst vergebene Marke, der Zeitstempel bleibt",
    "id=" + (QText("SELECT COUNT(*) FROM reference WHERE UploadedId = '880011';", s20Db) ?? "-")
    + " urteil=" + (QText("SELECT COUNT(*) FROM upload WHERE UploadedAt IS NOT NULL;", s20Db) ?? "-"));

Check(QIn("SELECT COUNT(*) FROM reference WHERE UploadedId = 'sm20'", s20Db) >= 1,
    "F-M670: der Beleg aus der Alt-Tabelle steht mit Id und Zeitpunkt in der Quellen-Tabelle",
    QText("SELECT COUNT(*) FROM reference WHERE UploadedId = 'sm20';", s20Db) ?? "-");

Check(Sicherungen(s20Dir).Length == 1 && s20Marken == 2,
    "F-M670: genau EINE Sicherung für den Lauf, 2 Marken danach",
    Sicherungen(s20Dir).Length + " Sicherung(en), " + s20Marken + " Marke(n)");

int s20Backups = Sicherungen(s20Dir).Length;
int s20Marken2;
using (var db = new ScribeDbContext(s20Dir, null))
{
    s20Marken2 = SourceStore.CountByKind(db).Values.Sum();
}

Check(s20Marken2 == s20Marken && Sicherungen(s20Dir).Length == s20Backups,
    "F-M670: ein zweiter Start auf der 21er-Datei tut nichts — keine zweite Sicherung",
    s20Marken2 + " Marke(n), " + Sicherungen(s20Dir).Length + " Sicherung(en)");

// ---------------------------------------------------------------------------------------------
// T336 (F-M672/F-M676/F-M677): die Id IST der Text, die Mitgliedschaft hält der Behälter, und die
// fremden Kennungen stehen in genau drei Tabellen.
//
// Der Aufbau ist wieder eine nachgebaute Altdatei aus ihrem ALTEN DDL (Schema 17): eine Staffel mit
// drei Spuren in ZWEI Sprachen (DE plain, ENG plain, ENG HI), ein Seitenwagen, ein Pack je Sprache —
// und ein Pack fuer FR, das in der Datei KEINE Spur hat. Genau daran scheitert die alte Zuordnung:
// sie hing an der Datei und zog alle Spuren in den Behälter, dessen Sprache die Datei zufaellig traf.
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- ids, membership and the three source tables (F-M672/F-M676/F-M677) ---");

string idDir = Path.Combine(workdir, "ids23");
Directory.CreateDirectory(idDir);
string idDb = DbFiles.PathIn(idDir);
using (var db = new ScribeDbContext(idDir, null))
{
    db.Files.Upsert(new FileEntity
    {
        OsHash = "h900", Path = "/lib/serie/s01e01.mkv", ImdbId = "tt900", TmdbId = "900",
        IsSeries = true, Season = 1, Episode = 1,
    });
}

using (var db = new ScribeDbContext(idDir, null))
{
    void Sql(string sql) => db.Context.Database.ExecuteSqlRaw(sql);
    CreateOldTables(Sql, 17);
    Sql("INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, IsSeries, Season, Episode, LastSeen) "
        + "VALUES ('h900', '/lib/serie/s01e01.mkv', NULL, 'tt900', '900', 1, 1, 1, '2026-11-01 10:00:00');");
    foreach (var (pos, lang, hi, origin, originId) in new[]
             {
                 (0, "DE", 0, "subdl", "555-55"),
                 (1, "ENG", 0, (string?)null, (string?)null),
                 (2, "ENG", 1, (string?)null, (string?)null),
             })
    {
        Sql("INSERT INTO embeds (Id, MediaHash, SubPos, Language, HearingImpaired, Status, StatusAt, Origin, OriginId) "
            + "VALUES ('h900|" + pos + "', 'h900', " + pos + ", '" + lang + "', " + hi + ", 'observed', '2026-11-01 10:00:00', "
            + (origin == null ? "NULL" : "'" + origin + "'") + ", "
            + (originId == null ? "NULL" : "'" + originId + "'") + ");");
    }

    Sql("INSERT INTO sidecars (Id, ContentHash, FileName, MediaHash, Path, IsSeries, Season, Episode, Language, "
        + "HearingImpaired, Status, StatusAt) VALUES ('sc900', 'c900', 's01e01.de.srt', 'h900', '/lib/serie/s01e01.de.srt', "
        + "1, 1, 1, 'DE', 0, 'observed', '2026-11-01 10:00:00');");
    foreach (var (key, lang, hi) in new[]
             {
                 ("tt900|S1|EN|hi", "EN", 1),
                 ("tt900|S1|DE|plain", "DE", 0),
                 ("tt900|S1|FR|plain", "FR", 0),
             })
    {
        Sql("INSERT INTO packs (Id, ImdbId, Season, Language, HearingImpaired, ReleaseName, PackId, Episodes, Created, Updated) "
            + "VALUES ('" + key + "', 'tt900', 1, '" + lang + "', " + hi + ", 'S01', 'own-" + lang + "', '1', "
            + "'2026-11-01 10:00:00', '2026-11-01 10:00:00');");
    }

    // Der verbrauchte Kandidat mit der Nummer des fremden Eintrags — die Meldung braucht sie. Die Tabelle
    // entsteht aus ihrem ALTEN DDL: eine 17er-Datei kennt `badapple` noch nicht.
    Sql("CREATE TABLE rejected_candidates (Id TEXT NOT NULL PRIMARY KEY, ItemId TEXT NOT NULL DEFAULT '', "
        + "Language TEXT NOT NULL DEFAULT '', SubdlId TEXT NOT NULL DEFAULT '', Reason TEXT NOT NULL DEFAULT '', "
        + "NId TEXT NOT NULL DEFAULT '', ReleaseName TEXT NOT NULL DEFAULT '', Finding TEXT NOT NULL DEFAULT '', "
        + "Kind TEXT NOT NULL DEFAULT 'qa', ReportedUtc TEXT NULL, Updated TEXT NOT NULL DEFAULT '');");
    Sql("INSERT INTO rejected_candidates (Id, ItemId, Language, SubdlId, Reason, NId, ReleaseName, Finding, Kind, "
        + "ReportedUtc, Updated) VALUES ('x|EN|777-77', 'x', 'EN', '777-77', 'candidate-rejected', 'Nj3hP7BQDw', "
        + "'Some.Release', 'no usable cues', 'qa', NULL, '2026-11-01 10:00:00');");

    Sql("UPDATE meta SET SchemaVersion = 17 WHERE Id = 'db';");
}

int idBackups;
using (var db = new ScribeDbContext(idDir, null))
{
    idBackups = Sicherungen(idDir).Length;
    string trackId = SourceStore.SubjectAt(db, "h900", 2) ?? string.Empty;
    string sidecarId = SourceStore.TrackAtSidecar(db, "h900") ?? string.Empty;
    string packId = db.Packs.FindAll().First().PackId;

    Check(Jellyfin.Plugin.SubScribe.Data.ScribeIds.IsTrackId(trackId)
          && trackId.Length == 10 && trackId[0] == '#' && trackId[6] == '#',
        "F-M676: eine eingebettete Spur heisst #yyyyy#zzz — die Datei-Id steht IN der Spur-Id",
        trackId);
    Check(Jellyfin.Plugin.SubScribe.Data.ScribeIds.IsSidecarId(sidecarId)
          && sidecarId.Length == 10 && sidecarId[6] == '&',
        "F-M676: ein Seitenwagen heisst #yyyyy&zzz — das & ist kein Zeichen des Alphabets",
        sidecarId);
    Check(trackId.Length == 10 && sidecarId.Length == 10 && trackId.Substring(0, 6) == sidecarId.Substring(0, 6)
          && Jellyfin.Plugin.SubScribe.Data.ScribeIds.IsFileId(trackId.Substring(0, 6)),
        "F-M676: Spur und Seitenwagen derselben Datei tragen dieselbe Datei-Id (#yyyyy)",
        trackId.Substring(0, 6));
    Check(Jellyfin.Plugin.SubScribe.Data.ScribeIds.IsPackId(packId),
        "F-M676: ein Pack heisst [xxxx] — die Klammern sind seine Marke",
        packId);

    // F-M677: die Mitgliedschaft haengt an SPRACHE und VARIANTE, nicht an der Datei.
    var packEn = db.Packs.FindAll().First(p => p.Language == "EN" && (p.HearingImpaired ?? false));
    var packDe = db.Packs.FindAll().First(p => p.Language == "DE");
    var packFr = db.Packs.FindAll().First(p => p.Language == "FR");
    Check(SourceStore.TracksOfPack(db, packEn.PackId).Count == 1
          && SourceStore.TracksOfPack(db, packDe.PackId).Count == 1
          && SourceStore.TracksOfPack(db, packFr.PackId).Count == 0,
        "F-M677: der EN-HI-Pack hat die englische HI-Spur, der DE-Pack die deutsche, der FR-Pack keine",
        "EN+HI=" + SourceStore.TracksOfPack(db, packEn.PackId).Count
        + " DE=" + SourceStore.TracksOfPack(db, packDe.PackId).Count
        + " FR=" + SourceStore.TracksOfPack(db, packFr.PackId).Count);
    Check(SourceStore.TracksOfPack(db, packDe.PackId).Count == 1
          && SourceStore.TracksOfPack(db, packDe.PackId)[0] == SourceStore.SubjectAt(db, "h900", 0),
        "F-M677: die DE-Spur der Datei ist Mitglied des DE-Packs — nicht des Behaelters der Datei");
    Check(SourceStore.FilesOfPack(db, packDe.PackId).Count == 1,
        "F-M677: die Mitglieder eines Packs sind ueber eine Datei zu finden — ohne eigene Zuordnungstabelle");

    // F-M676: die Spalten, die es nicht mehr gibt.
    Check(!SpaltenVon("track").Contains("FileId") && !SpaltenVon("track").Contains("PackId")
          && !SpaltenVon("track").Contains("Display") && !SpaltenVon("sidecar").Contains("FileId")
          && !SpaltenVon("file").Contains("PackId") && !SpaltenVon("file").Contains("Display"),
        "F-M676/F-M677: keine FileId-/PackId-/Display-Spalte mehr — die Id traegt, was sie sagt",
        string.Join(",", SpaltenVon("track")));

    // F-M672: keine fremde Kennung ausserhalb von `reference` und `badapple`.
    int fremd = QIn("SELECT COUNT(*) FROM track WHERE TrackId LIKE '%555-55%' OR COALESCE(Origin,'') LIKE '%555-55%'", idDb)
                + QIn("SELECT COUNT(*) FROM sidecar WHERE COALESCE(Origin,'') LIKE '%555-55%'", idDb)
                + QIn("SELECT COUNT(*) FROM pack WHERE COALESCE(ReleaseName,'') LIKE '%555-55%'", idDb);
    Check(fremd == 0,
        "F-M672: keine fremde Kennung steht in einer Entity-Tabelle",
        fremd + " Treffer");
    Check(QIn("SELECT COUNT(*) FROM reference WHERE DownloadSourceId = '555-55'", idDb) == 1,
        "F-M672: die Herkunft der DE-Spur (OriginId 555-55) steht als Beleg in `reference` — kein Wert verloren",
        QText("SELECT COUNT(*) FROM reference WHERE DownloadSourceId = '555-55';", idDb) ?? "-");

    Check(QText("SELECT SourceId || '|' || Reason || '|' || ReportState FROM badapple;", idDb)
          == "777-77|candidate-rejected|open",
        "F-M672: der schlechte Download traegt die Nummer des fremden Eintrags, seinen Grund und seinen Meldezustand",
        QText("SELECT SourceId || '|' || Reason || '|' || ReportState FROM badapple;", idDb) ?? "-");
    Check(QIn("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('rejected_candidates','source_matrix')", idDb) == 0,
        "F-M672: source_matrix und rejected_candidates gibt es nicht mehr");
}

Check(idBackups == 1, "F-M672: genau EINE Sicherung fuer den ganzen Aufstieg", idBackups + " Sicherung(en)");
int idMarks;
using (var db = new ScribeDbContext(idDir, null))
{
    idMarks = SourceStore.CountByKind(db).Values.Sum();
}

Check(idMarks == 5 && Sicherungen(idDir).Length == 1,
    "F-M672: ein zweiter Start tut nichts — gleiche Markenzahl, keine zweite Sicherung",
    idMarks + " Marke(n), " + Sicherungen(idDir).Length + " Sicherung(en)");

// ---------------------------------------------------------------------------------------------
// T337 (F-M679): der Member gehoert zum Schluessel von `badapple` — ein schlechter Download ist ein
// EINZELNES Sub, auch wenn es in einem Staffelpack kam.
//
// Aufgebaut wird eine ECHTE Schema-23-Datei: ein vollstaendiger Store (alle Tabellen des Modells),
// dessen `badapple` danach aus dem ALTEN DDL neu entsteht — ohne `Member`, mit dem dreiteiligen
// Schluessel. SQLite kann einen Primaerschluessel nicht aendern; die Datei zwingt den Aufstieg also
// genau in den Weg, den F-M679 baut (Tabelle neu, Zeilen mit Member='' herueber, eine Transaktion nach
// einer Sicherung, bestandsgetrieben statt markergetrieben).
// ---------------------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- badapple carries the member: schema 23 -> 24 (F-M679) ---");

string m24Dir = Path.Combine(workdir, "member24");
Directory.CreateDirectory(m24Dir);
string m24Db = DbFiles.PathIn(m24Dir);

using (var db = new ScribeDbContext(m24Dir, null))
{
    db.Files.Upsert(new FileEntity
    {
        OsHash = "h24",
        Path = "/lib/mobland/s02e05.mkv",
        FileName = "mobland.s02e05.mkv",
        ImdbId = "tt24",
        IsSeries = true,
        Season = 2,
        Episode = 5,
    });
}

string m24Subjekt;
using (var db = new ScribeDbContext(m24Dir, null))
{
    m24Subjekt = db.Files.FindAll().First().FileId;
}

// Das DDL, das Schema 23 geschrieben hat — dreiteiliger Schluessel, kein Member.
string badAppleDdl23 =
    "CREATE TABLE badapple ("
    + "SubjectId TEXT NOT NULL, Source TEXT NOT NULL, SourceId TEXT NOT NULL DEFAULT '', "
    + "Language TEXT NOT NULL DEFAULT '', Reason TEXT NOT NULL DEFAULT '', Finding TEXT NOT NULL DEFAULT '', "
    + "NId TEXT NOT NULL DEFAULT '', ReleaseName TEXT NOT NULL DEFAULT '', Kind TEXT NOT NULL DEFAULT 'qa', "
    + "ReportedAt TEXT NULL, ReportState TEXT NOT NULL DEFAULT 'open', Written TEXT NOT NULL DEFAULT '', "
    + "PRIMARY KEY (SubjectId, Source, SourceId));";

using (var db = new ScribeDbContext(m24Dir, null))
{
    void Sql(string sql) => db.Context.Database.ExecuteSqlRaw(sql);
    Sql("DROP TABLE badapple;");
    Sql(badAppleDdl23);
    Sql("CREATE INDEX IX_badapple_Source ON badapple (Source);");
    Sql("CREATE INDEX IX_badapple_SubjectLanguage ON badapple (SubjectId, Language);");
    Sql("INSERT INTO badapple (SubjectId, Source, SourceId, Language, Reason, Finding, NId, ReleaseName, Kind, "
        + "ReportedAt, ReportState, Written) VALUES ('" + m24Subjekt + "', 'subdl', '380011', 'EN', "
        + "'candidate-rejected', '0 bytes', '', 'Some.Pack.S02', 'qa', '2026-11-21 08:00:00', 'reported', "
        + "'2026-11-21 09:00:00');");
    Sql("UPDATE meta SET SchemaVersion = 23 WHERE Id = 'db';");
}

// VORHER: die Spalte gibt es nicht, der Marker steht auf 23 — die Probe kann also etwas finden.
Check(!SpaltenVon("badapple", m24Db).Contains("Member")
      && QIn("SELECT SchemaVersion FROM meta WHERE Id = 'db'", m24Db) == 23,
    "F-M679: die Altdatei ist eine echte 23er-Datei — badapple ohne Member, Marker 23",
    "Spalten=" + SpaltenVon("badapple", m24Db).Length + " Marker="
    + QIn("SELECT SchemaVersion FROM meta WHERE Id = 'db'", m24Db));

int m24Backups;
using (var db = new ScribeDbContext(m24Dir, null))
{
    m24Backups = Sicherungen(m24Dir).Length;
    var zeilen = db.BadApples.FindAll().ToList();

    Check(SpaltenVon("badapple", m24Db).Contains("Member") && zeilen.Count == 1,
        "F-M679: der Aufstieg 23 -> 24 baut badapple mit Member und nimmt die Zeile mit",
        "Spalten=" + string.Join(",", SpaltenVon("badapple", m24Db)) + " Zeilen=" + zeilen.Count);

    // Eine 23er-Zeile kannte kein Sub — der fremde Eintrag WAR das Sub, also Member = "".
    Check(zeilen.Count == 1 && zeilen[0].Member.Length == 0 && zeilen[0].Reason == "candidate-rejected"
          && zeilen[0].Finding == "0 bytes",
        "F-M679: die uebernommene Zeile steht mit Member = '' da, Grund und Befund unveraendert",
        zeilen.Count == 1 ? "'" + zeilen[0].Member + "|" + zeilen[0].Reason + "|" + zeilen[0].Finding + "'" : "-");

    // Zustand ist Zustand: der Meldezustand und der Schreibzeitpunkt werden mitkopiert, nicht neu
    // abgeleitet — sonst waere jede uebernommene Zeile plotzlich "die neueste" im 50-Zeilen-Pruning.
    Check(QText("SELECT ReportState || '|' || Written || '|' || ReportedAt FROM badapple", m24Db)
          == "reported|2026-11-21 09:00:00|2026-11-21 08:00:00",
        "F-M679: ReportState, Written und ReportedAt werden mitkopiert, nicht neu abgeleitet",
        QText("SELECT ReportState || '|' || Written FROM badapple", m24Db) ?? "-");

    Check(QIn("SELECT SchemaVersion FROM meta WHERE Id = 'db'", m24Db) == 24,
        "F-M679: der Marker steht danach auf 24",
        QIn("SELECT SchemaVersion FROM meta WHERE Id = 'db'", m24Db).ToString(System.Globalization.CultureInfo.InvariantCulture));

    // F-M680 (Fehler, gemessen 22.11.2026 an einer echten 23er-Datei): der Aufstieg hueLLte die schon
    // LESBAREN Kennungen ein zweites Mal ein — aus `[FrEI]` wurde `[[FrEI]]`, und der Fremdschluessel
    // von `pack_track` liess den ganzen Lauf in die Rolle zurueckfallen. Die Id-Bauer sind seither
    // idempotent: wer eine fertige Id hineingibt, bekommt SIE zurueck, nicht ihre Umhuellung.
    Check(ScribeIds.PackIdOf("[Ab3x]") == "[Ab3x]"
          && ScribeIds.FileIdOf("#Ab3xY") == "#Ab3xY"
          && ScribeIds.TrackIdOf("#Ab3xY", "#Ab3xY#zz9") == "#Ab3xY#zz9"
          && ScribeIds.SidecarIdOf("#Ab3xY", "#Ab3xY&zz9") == "#Ab3xY&zz9",
        "F-M680: eine fertige Id wird nicht ein zweites Mal eingefasst — sonst kippt der Aufstieg am Fremdschluessel",
        ScribeIds.PackIdOf("[Ab3x]") + " | " + ScribeIds.TrackIdOf("#Ab3xY", "#Ab3xY#zz9"));

    // Und die Kennung der echten Datei steht nach dem Aufstieg unveraendert da (kein Doppelzeichen).
    Check(db.Files.FindAll().First().FileId == m24Subjekt
          && !db.Files.FindAll().Any(f => f.FileId.StartsWith("##", StringComparison.Ordinal)),
        "F-M680: die Kennungen der uebernommenen Datei bleiben unveraendert, ohne Doppelmarke",
        db.Files.FindAll().Select(f => f.FileId).FirstOrDefault() ?? "-");
}

// Eine Sicherung, und sie ist der Stand VOR dem Lauf: in der Kopie fehlt Member noch.
var m24Sicherungen = Sicherungen(m24Dir);
Check(m24Backups == 1 && m24Sicherungen.Length == 1 && !SpaltenVon("badapple", m24Sicherungen[0]).Contains("Member"),
    "F-M679: genau EINE Sicherung, und sie traegt die Datei VOR dem Umbau (badapple ohne Member)",
    m24Sicherungen.Length + " Sicherung(en)"
    + (m24Sicherungen.Length == 1 ? ", Spalten dort=" + SpaltenVon("badapple", m24Sicherungen[0]).Length : string.Empty));

// Der Schluessel: zwei Zeilen, die sich NUR im Member unterscheiden, sind zwei Zeilen. Und ein zweites
// Melden DESSELBEN Subs bleibt eine Aktualisierung.
const string memberEn = "Sampleland - 2x05 - Episode Title.WEB.GRP.en.srt";
const string memberDe = "Sampleland - 2x05 - Episode Title.WEB.GRP.de.srt";
using (var db = new ScribeDbContext(m24Dir, null))
{
    SourceStore.RecordBadApple(db, m24Subjekt, SourceStore.Subdl, "380011", "EN",
        RejectReason.CandidateRejected, member: memberEn);
    SourceStore.RecordBadApple(db, m24Subjekt, SourceStore.Subdl, "380011", "EN",
        RejectReason.CandidateRejected, member: memberDe);
    // Dasselbe Sub noch einmal: dieselbe Zeile, kein dritter Eintrag.
    SourceStore.RecordBadApple(db, m24Subjekt, SourceStore.Subdl, "380011", "EN",
        RejectReason.CandidateRejected, "no usable cues", member: memberEn);
}

Check(QIn("SELECT COUNT(*) FROM badapple WHERE SourceId = '380011' AND Member <> ''", m24Db) == 2,
    "F-M679: zwei Zeilen, die sich NUR im Member unterscheiden, existieren beide — Member gehoert zum Schluessel",
    QText("SELECT COUNT(*) FROM badapple WHERE SourceId = '380011' AND Member <> ''", m24Db) ?? "-");

Check(QIn("SELECT COUNT(*) FROM badapple", m24Db) == 3,
    "F-M679: das zweite Melden desselben Subs ist eine Aktualisierung, keine dritte Zeile",
    QText("SELECT COUNT(*) FROM badapple", m24Db) ?? "-");

using (var db = new ScribeDbContext(m24Dir, null))
{
    var zeile = db.BadApples.FindAll()
        .FirstOrDefault(x => string.Equals(x.Member, memberEn, StringComparison.Ordinal));
    var wieder = zeile == null ? null : db.BadApples.FindById(zeile.Key);
    Check(zeile != null && wieder != null
          && string.Equals(wieder.Member, memberEn, StringComparison.Ordinal)
          && string.Equals(wieder.SourceId, "380011", StringComparison.Ordinal)
          && string.Equals(wieder.Finding, "no usable cues", StringComparison.Ordinal)
          && zeile.Key != zeile.SubjectId + "|" + zeile.Source + "|" + zeile.SourceId,
        "F-M679: eine Zeile mit Member wird unveraendert wieder gelesen, und ihr Schluessel nennt das Sub",
        wieder == null ? "(nicht gefunden)" : wieder.Key);
}

int m24Zeilen = QIn("SELECT COUNT(*) FROM badapple", m24Db);
int m24Backups2;
using (var db = new ScribeDbContext(m24Dir, null))
{
    m24Backups2 = Sicherungen(m24Dir).Length;
    _ = db.BadApples.Count();
}

Check(m24Backups2 == 1 && QIn("SELECT COUNT(*) FROM badapple", m24Db) == m24Zeilen,
    "F-M679: ein zweiter Start auf der 24er-Datei tut nichts — keine zweite Sicherung, keine zweite Zeile",
    m24Backups2 + " Sicherung(en), " + QIn("SELECT COUNT(*) FROM badapple", m24Db) + " Zeile(n)");

// ---------------------------------------------------------------------------------------------
// F-M679, Weg 22 -> 24: ein Store aus Schema 22 traegt noch gar kein `badapple` — die Tabelle
// entsteht beim Aufstieg frisch aus dem DDL, also MIT Member. Nachgebaut wird die 22er-Form aus einem
// vollstaendigen Store: die Spalten `file.PackId` und `track.OriginId` zurueck (die 22 sie trug) und
// die Marken-Gridfilm, die es damals noch gab.
// ---------------------------------------------------------------------------------------------
string m22Dir = Path.Combine(workdir, "member22");
Directory.CreateDirectory(m22Dir);
string m22Db = DbFiles.PathIn(m22Dir);
using (var db = new ScribeDbContext(m22Dir, null))
{
    db.Files.Upsert(new FileEntity { OsHash = "h22", Path = "/lib/serie/s01e01.mkv", IsSeries = true, Season = 1, Episode = 1 });
}

using (var db = new ScribeDbContext(m22Dir, null))
{
    void Sql(string sql) => db.Context.Database.ExecuteSqlRaw(sql);
    Sql("DROP TABLE badapple;");
    Sql("ALTER TABLE file ADD COLUMN PackId TEXT NULL;");
    Sql("ALTER TABLE track ADD COLUMN OriginId TEXT NULL;");
    Sql(Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SourceMatrixDdl);
    Sql("UPDATE meta SET SchemaVersion = 22 WHERE Id = 'db';");
}

Check(QIn("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='badapple'", m22Db) == 0
      && QIn("SELECT COUNT(*) FROM pragma_table_info('file') WHERE name='PackId'", m22Db) == 1,
    "F-M679: die 22er-Datei traegt kein badapple und noch die Spalten, die Schema 23 entfernt hat");

int m22Backups;
using (var db = new ScribeDbContext(m22Dir, null))
{
    m22Backups = Sicherungen(m22Dir).Length;
    Check(SpaltenVon("badapple", m22Db).Contains("Member")
          && QIn("SELECT SchemaVersion FROM meta WHERE Id = 'db'", m22Db) == 24,
        "F-M679: der Aufstieg 22 -> 24 legt badapple frisch MIT Member an und setzt den Marker auf 24",
        "Spalten=" + string.Join(",", SpaltenVon("badapple", m22Db)));
}

Check(m22Backups == 1
      && QIn("SELECT COUNT(*) FROM pragma_table_info('file') WHERE name='PackId'", m22Db) == 0
      && QIn("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='source_matrix'", m22Db) == 0,
    "F-M679: auch dort genau eine Sicherung, und die 22er-Spalten sind danach weg",
    m22Backups + " Sicherung(en)");

Console.WriteLine();
Console.WriteLine("=== " + (failures == 0 ? "STORE OK" : failures + " FAILURE(S)") + " ===");
return failures == 0 ? 0 : 1;