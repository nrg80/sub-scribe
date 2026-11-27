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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M772 bis F-M778: die eine Ablage fuer alle Antworten von TMDb — in einer eigenen Datei, mit zwei
/// Tabellen, je eigener Decke und Fristen nach Art der Frage.
/// <para>
/// WARUM EINE EIGENE DATEI: eine Decke muss eine Tatsache sein. In SQLite verkleinert das Loeschen von
/// Zeilen die Datei nicht, in der sie liegen. In der eigenen Datei ist die Decke die Datei selbst,
/// "nichts behalten" heisst die Datei loeschen, und die Plugin-Datenbank traegt keinen Cache mit, den sie
/// selbst wieder aufbauen koennte.
/// </para>
/// <para>
/// ZWEI FORMEN: eine <b>Tatsache</b> (fact) ist die Auskunft, die das Plugin liest — eine Kennung, eine
/// Laufzeit, ein Jahr, eine Staffel- und Folgenzahl. Eine <b>Rohantwort</b> (raw answer) ist die ganze
/// Antwort, wie TMDb sie geschickt hat; sie liegt nur als Rueckfall fuer Adressen ohne Regel und fuer den
/// Schwung von Wiederholungen innerhalb eines Laufs. Gemessen am 27.11.2026: eine Suche 2.327 B, ein
/// Serien-Detail mit angebauten Bloecken 55.963 B — und die Tatsache daraus 66 B. Faktor bis 875.
/// </para>
/// <para>
/// WARUM DIE SPRACHE NICHT IM SCHLUESSEL STECKT: fuer die Tatsachen ist sie bedeutungslos. Wer sie
/// mitzaehlt, bezahlt dieselbe Laufzeit zwei- oder dreimal.
/// </para>
/// </summary>
public static class TmdbCache
{
    /// <summary>Name of the cache file inside the plugin's data folder.</summary>
    public const string FileName = "tmdb-cache.sqlite";

    /// <summary>Prefix of every log line, so one search finds them all.</summary>
    public const string LogPrefix = "[Sub Scribe-TMDb-Cache] ";

    /// <summary>Ceiling over all facts.</summary>
    public const long FactCeilingBytes = 16L * 1024 * 1024;

    /// <summary>Ceiling over all raw answers.</summary>
    public const long BodyCeilingBytes = 16L * 1024 * 1024;

    /// <summary>How many facts the memory mirror keeps at most.</summary>
    public const int MemoryMirrorCap = 5000;

    /// <summary>An identifier TMDb stated itself: asked by number, answered by number.</summary>
    public static readonly TimeSpan StatedIdLifetime = TimeSpan.FromDays(365);

    /// <summary>An identifier the plugin guessed from a title and a year.</summary>
    public static readonly TimeSpan GuessedIdLifetime = TimeSpan.FromDays(30);

    /// <summary>Any other fact.</summary>
    public static readonly TimeSpan FactLifetime = TimeSpan.FromDays(180);

    /// <summary>An answer that found nothing — TMDb adds titles over time.</summary>
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(1);

    /// <summary>A raw answer to a search: a guess about a title.</summary>
    public static readonly TimeSpan SearchBodyLifetime = TimeSpan.FromDays(1);

    /// <summary>Every other raw answer.</summary>
    public static readonly TimeSpan OtherBodyLifetime = TimeSpan.FromDays(3);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Value, DateTime Written)> Mirror = new(StringComparer.Ordinal);
    private static SqliteConnection? _db;
    private static string? _path;
    private static ILogger? _log;
    private static Configuration.LogLevelMode _mode = Configuration.LogLevelMode.Debug;
    private static long _factBytes;
    private static long _bodyBytes;
    private static long _writes;
    private static long _servedFromShelf;
    private static long _networkRequests;

    /// <summary>True when the shelf can be used.</summary>
    public static bool Enabled
    {
        get
        {
            lock (Gate)
            {
                return _db != null;
            }
        }
    }

    /// <summary>
    /// F-M777: how often the shelf has answered in this process. The ladder compares it before and after its
    /// attempts: if only the shelf answered, the attempts were not real questions and must not close a title.
    /// </summary>
    public static long ServedFromShelf
    {
        get
        {
            lock (Gate)
            {
                return _servedFromShelf;
            }
        }
    }

    /// <summary>
    /// F-M777: how often TMDb was really asked in this process. Counted by the HTTP chain, not by the shelf.
    /// </summary>
    public static long NetworkRequests
    {
        get
        {
            lock (Gate)
            {
                return _networkRequests;
            }
        }
    }

    /// <summary>
    /// F-M777: one real request to TMDb leaves the host — called by the HTTP chain before it sends.
    /// </summary>
    public static void NoteNetworkRequest()
    {
        lock (Gate)
        {
            _networkRequests++;
        }
    }

    /// <summary>The file the shelf lives in, or null when it is not attached.</summary>
    public static string? FilePath => _path;

    /// <summary>
    /// Attaches the shelf: opens or creates the file, builds both tables and trims once.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="logger">Target logger.</param>
    /// <param name="mode">Effective plugin log mode.</param>
    public static void Attach(string? dataFolder, ILogger? logger, Configuration.LogLevelMode mode = Configuration.LogLevelMode.Debug)
    {
        lock (Gate)
        {
            _log = logger;
            _mode = mode;

            if (string.IsNullOrWhiteSpace(dataFolder))
            {
                Log("off — no data folder, nothing is stored or read (F-M772)");
                return;
            }

            try
            {
                Directory.CreateDirectory(dataFolder);
                _path = System.IO.Path.Combine(dataFolder, FileName);
                _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
                _db.Open();
                Exec("PRAGMA journal_mode=WAL;");
                Exec("PRAGMA synchronous=NORMAL;");
                Exec("CREATE TABLE IF NOT EXISTS tmdb_fact ("
                    + "Kind TEXT NOT NULL, Subject TEXT NOT NULL, Value TEXT NOT NULL DEFAULT '', "
                    + "Bytes INTEGER NOT NULL DEFAULT 0, Written TEXT NOT NULL DEFAULT '', "
                    + "PRIMARY KEY (Kind, Subject));");
                Exec("CREATE TABLE IF NOT EXISTS tmdb_body ("
                    + "Key TEXT NOT NULL PRIMARY KEY, Body TEXT NOT NULL DEFAULT '', "
                    + "Bytes INTEGER NOT NULL DEFAULT 0, Hits INTEGER NOT NULL DEFAULT 0, "
                    + "Written TEXT NOT NULL DEFAULT '');");
                Log("configured — file \"{File}\", facts {FactMb} MB, raw answers {BodyMb} MB (F-M772)",
                    FileName, FactCeilingBytes / (1024L * 1024L), BodyCeilingBytes / (1024L * 1024L));
                TrimLocked("start");
            }
            catch (Exception ex)
            {
                _db = null;
                _path = null;
                _log?.LogWarning("{Prefix}could not open the cache file: {Type}: {Msg} — the plugin asks TMDb directly (F-M772)", LogPrefix, ex.GetType().Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads a fact — memory first, then the file. An expired row is removed and answers <c>false</c>.
    /// </summary>
    /// <param name="kind">What kind of statement it is.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="value">The value, or null for "asked and found nothing".</param>
    /// <returns>True when a fresh fact exists.</returns>
    public static bool TryGetFact(string kind, string subject, out string? value)
    {
        value = null;
        string key = kind + "|" + subject;
        lock (Gate)
        {
            if (_db == null)
            {
                return false;
            }

            if (Mirror.TryGetValue(key, out var gemerkt))
            {
                if (IsFresh(kind, gemerkt.Value, gemerkt.Written))
                {
                    _servedFromShelf++;
                    value = gemerkt.Value.Length == 0 ? null : gemerkt.Value;
                    Log("hit (memory) \"{Kind}|{Subject}\" — {Age:0.0} h old (F-M773)", kind, subject, (DateTime.UtcNow - gemerkt.Written).TotalHours);
                    return true;
                }

                Mirror.Remove(key);
            }

            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT Value, Written FROM tmdb_fact WHERE Kind = $k AND Subject = $s;";
                cmd.Parameters.AddWithValue("$k", kind);
                cmd.Parameters.AddWithValue("$s", subject);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    Log("miss \"{Kind}|{Subject}\" — not in the shelf (F-M773)", kind, subject);
                    return false;
                }

                string gespeichert = reader.GetString(0);
                DateTime geschrieben = Parse(reader.GetString(1));
                reader.Close();

                if (!IsFresh(kind, gespeichert, geschrieben))
                {
                    DeleteFactLocked(kind, subject);
                    Log("miss \"{Kind}|{Subject}\" — expired and removed (F-M773)", kind, subject);
                    return false;
                }

                MirrorStoreLocked(key, gespeichert, geschrieben);
                _servedFromShelf++;
                value = gespeichert.Length == 0 ? null : gespeichert;
                Log("hit \"{Kind}|{Subject}\" — {Age:0.0} h old (F-M773)", kind, subject, (DateTime.UtcNow - geschrieben).TotalHours);
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning("{Prefix}reading {Kind}|{Subject} failed: {Type}: {Msg} (F-M773)", LogPrefix, kind, subject, ex.GetType().Name, ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Writes a fact. An empty or null value means "asked and found nothing" and lives one day.
    /// </summary>
    /// <param name="kind">What kind of statement it is.</param>
    /// <param name="subject">What it is about.</param>
    /// <param name="value">The value, or null for "nothing found".</param>
    /// <param name="written">When it was answered (UTC), for a migration of older rows.</param>
    public static void PutFact(string kind, string subject, string? value, DateTime? written = null)
    {
        string gespeichert = value ?? string.Empty;
        DateTime zeit = written ?? DateTime.UtcNow;
        lock (Gate)
        {
            if (_db == null)
            {
                return;
            }

            try
            {
                int bytes = System.Text.Encoding.UTF8.GetByteCount(gespeichert) + System.Text.Encoding.UTF8.GetByteCount(kind) + System.Text.Encoding.UTF8.GetByteCount(subject);
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO tmdb_fact (Kind, Subject, Value, Bytes, Written) VALUES ($k, $s, $v, $b, $w) "
                        + "ON CONFLICT(Kind, Subject) DO UPDATE SET Value = $v, Bytes = $b, Written = $w;";
                    cmd.Parameters.AddWithValue("$k", kind);
                    cmd.Parameters.AddWithValue("$s", subject);
                    cmd.Parameters.AddWithValue("$v", gespeichert);
                    cmd.Parameters.AddWithValue("$b", bytes);
                    cmd.Parameters.AddWithValue("$w", zeit.ToString("O", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }

                MirrorStoreLocked(kind + "|" + subject, gespeichert, zeit);
                Log("stored \"{Kind}|{Subject}\" — {Bytes} B, lives {Days} day(s) (F-M773)", kind, subject, bytes, LifetimeOfFact(kind, gespeichert).TotalDays);
                AfterWriteLocked(bytes, 0);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("{Prefix}storing {Kind}|{Subject} failed: {Type}: {Msg} (F-M773)", LogPrefix, kind, subject, ex.GetType().Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads a raw answer.
    /// </summary>
    /// <param name="key">The address without its key.</param>
    /// <param name="body">The body as text.</param>
    /// <returns>True when a fresh raw answer exists.</returns>
    public static bool TryGetBody(string key, out string? body)
    {
        body = null;
        lock (Gate)
        {
            if (_db == null)
            {
                return false;
            }

            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT Body, Written FROM tmdb_body WHERE Key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    Log("miss (raw) \"{Key}\" — not in the shelf (F-M773)", Short(key));
                    return false;
                }

                string gespeichert = reader.GetString(0);
                DateTime geschrieben = Parse(reader.GetString(1));
                reader.Close();

                if (DateTime.UtcNow - geschrieben > LifetimeOfBody(key))
                {
                    using var del = _db.CreateCommand();
                    del.CommandText = "DELETE FROM tmdb_body WHERE Key = $k;";
                    del.Parameters.AddWithValue("$k", key);
                    del.ExecuteNonQuery();
                    Log("miss (raw) \"{Key}\" — expired and removed (F-M773)", Short(key));
                    return false;
                }

                using (var touch = _db.CreateCommand())
                {
                    touch.CommandText = "UPDATE tmdb_body SET Hits = Hits + 1 WHERE Key = $k;";
                    touch.Parameters.AddWithValue("$k", key);
                    touch.ExecuteNonQuery();
                }

                _servedFromShelf++;
                body = gespeichert;
                Log("hit (raw) \"{Key}\" — {Bytes} B, {Age:0.0} h old (F-M773)", Short(key), System.Text.Encoding.UTF8.GetByteCount(gespeichert), (DateTime.UtcNow - geschrieben).TotalHours);
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogWarning("{Prefix}reading raw \"{Key}\" failed: {Type}: {Msg} (F-M773)", LogPrefix, Short(key), ex.GetType().Name, ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Writes a raw answer. Only a successful answer is stored; a failure is no answer.
    /// </summary>
    /// <param name="key">The address without its key.</param>
    /// <param name="body">The body as text.</param>
    public static void PutBody(string key, string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            Log("refused (raw) \"{Key}\" — empty body (F-M773)", Short(key));
            return;
        }

        lock (Gate)
        {
            if (_db == null)
            {
                return;
            }

            int bytes = System.Text.Encoding.UTF8.GetByteCount(body);
            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO tmdb_body (Key, Body, Bytes, Hits, Written) VALUES ($k, $b, $n, 0, $w) "
                        + "ON CONFLICT(Key) DO UPDATE SET Body = $b, Bytes = $n, Written = $w;";
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$b", body);
                    cmd.Parameters.AddWithValue("$n", bytes);
                    cmd.Parameters.AddWithValue("$w", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }

                Log("stored (raw) \"{Key}\" — {Bytes} B, lives {Days} day(s) (F-M773)", Short(key), bytes, LifetimeOfBody(key).TotalDays);
                AfterWriteLocked(0, bytes);
            }
            catch (Exception ex)
            {
                _log?.LogWarning("{Prefix}storing raw \"{Key}\" failed: {Type}: {Msg} (F-M773)", LogPrefix, Short(key), ex.GetType().Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// F-M781: one line when the retired answer table has been dropped.
    /// </summary>
    public static void NoteRetiredTable()
        => Log("old table tmdb_answers dropped for good — nothing was taken over (F-M781)");

    /// <summary>
    /// F-M782: folds the shelf into a dense file, like <c>ScribeDbContext.Compact</c> does for the plugin
    /// database. Deleting rows frees pages inside the file but never releases them, so without this step
    /// the file would keep its high-water mark while the ceilings are met.
    /// </summary>
    /// <returns>Bytes before and after, over the file and its journal.</returns>
    public static (long Before, long After) Compact()
    {
        lock (Gate)
        {
            if (_db == null)
            {
                return (0, 0);
            }

            long before = FileBytesLocked();
            try
            {
                Exec("PRAGMA wal_checkpoint(TRUNCATE);");
                Exec("VACUUM;");
                Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex)
            {
                _log?.LogWarning("{Prefix}compaction failed: {Type}: {Msg} (F-M782)", LogPrefix, ex.GetType().Name, ex.Message);
                return (before, FileBytesLocked());
            }

            long after = FileBytesLocked();
            Log("compacted — {Before} B before, {After} B after, {Freed} B freed (F-M782)", before, after, before - after);
            return (before, after);
        }
    }

    /// <summary>
    /// Removes everything that is too old and then the oldest rows until both ceilings are met.
    /// </summary>
    public static void Trim()
    {
        lock (Gate)
        {
            TrimLocked("trim");
        }
    }

    /// <summary>
    /// Deletes the whole shelf — the meaning of "keep nothing".
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Mirror.Clear();
            try
            {
                _db?.Dispose();
                _db = null;
                if (_path != null && File.Exists(_path))
                {
                    File.Delete(_path);
                }

                Log("purged — file removed (F-M772)");
            }
            catch (Exception ex)
            {
                _log?.LogWarning("{Prefix}purging failed: {Type}: {Msg} (F-M772)", LogPrefix, ex.GetType().Name, ex.Message);
            }
        }
    }

    /// <summary>
    /// Rows and bytes per table and kind, and the size of the file — what the diagnostics endpoint reports.
    /// </summary>
    /// <returns>The state of the shelf.</returns>
    public static TmdbCacheStats Stats()
    {
        lock (Gate)
        {
            var stats = new TmdbCacheStats();
            if (_db != null)
            {
                try
                {
                    using (var cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT Kind, COUNT(*), COALESCE(SUM(Bytes), 0) FROM tmdb_fact GROUP BY Kind;";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            string kind = reader.GetString(0);
                            stats.FactsByKind[kind] = (reader.GetInt64(1), reader.GetInt64(2));
                            stats.FactRows += reader.GetInt64(1);
                            stats.FactBytes += reader.GetInt64(2);
                        }
                    }

                    using (var cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(Bytes), 0) FROM tmdb_body;";
                        using var reader = cmd.ExecuteReader();
                        if (reader.Read())
                        {
                            stats.BodyRows = reader.GetInt64(0);
                            stats.BodyBytes = reader.GetInt64(1);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log?.LogWarning("{Prefix}reading the state failed: {Type}: {Msg} (F-M778)", LogPrefix, ex.GetType().Name, ex.Message);
                }
            }

            stats.FileBytes = FileBytesLocked();

            stats.Enabled = _db != null;
            stats.FactCeilingBytes = FactCeilingBytes;
            stats.BodyCeilingBytes = BodyCeilingBytes;
            return stats;
        }
    }

    /// <summary>
    /// F-M775: the lifetime of a fact. An answer that found nothing lives one day; an identifier TMDb
    /// stated itself 365 days; an identifier guessed from a title and a year 30 days; everything else 180.
    /// </summary>
    /// <param name="kind">The kind of statement.</param>
    /// <param name="value">The stored value.</param>
    /// <returns>How long it may be used.</returns>
    public static TimeSpan LifetimeOfFact(string kind, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return MissLifetime;
        }

        if (string.Equals(kind, Kinds.IdentifierStated, StringComparison.Ordinal))
        {
            return StatedIdLifetime;
        }

        return string.Equals(kind, Kinds.IdentifierGuessed, StringComparison.Ordinal) ? GuessedIdLifetime : FactLifetime;
    }

    /// <summary>
    /// F-M776: the lifetime of a raw answer — one day for a search, three days for everything else.
    /// </summary>
    /// <param name="key">The address without its key.</param>
    /// <returns>How long it may be used.</returns>
    public static TimeSpan LifetimeOfBody(string key)
        => key != null && key.Contains("search/", StringComparison.OrdinalIgnoreCase) ? SearchBodyLifetime : OtherBodyLifetime;

    /// <summary>
    /// F-M774: which kind of fact a question of the resolver becomes. A question asked by number is
    /// <b>stated</b> by TMDb; a question carrying a title is a <b>guess</b>, and its key carries the title
    /// and the year as its evidence — a different title is a different key and therefore a miss.
    /// </summary>
    /// <param name="questionKey">The resolver's question key.</param>
    /// <returns>The kind of fact.</returns>
    public static string KindOfQuestion(string questionKey)
    {
        if (string.IsNullOrEmpty(questionKey))
        {
            return Kinds.IdentifierGuessed;
        }

        bool geraten = questionKey.StartsWith("multi:", StringComparison.Ordinal)
            || questionKey.StartsWith("q:", StringComparison.Ordinal)
            || questionKey.StartsWith("showByEp:", StringComparison.Ordinal);
        return geraten ? Kinds.IdentifierGuessed : Kinds.IdentifierStated;
    }

    /// <summary>The kinds of fact the shelf knows.</summary>
    public static class Kinds
    {
        /// <summary>An identifier TMDb stated for a number it was asked about.</summary>
        public const string IdentifierStated = "id-stated";

        /// <summary>An identifier guessed from a title and a year.</summary>
        public const string IdentifierGuessed = "id-guessed";

        /// <summary>Any other statement about a title: runtime, year, counts, air dates.</summary>
        public const string Details = "details";
    }

    /// <summary>
    /// F-M782: the size of the shelf as it sits on disk — main file plus journal. Reporting the main file
    /// alone would understate it exactly like the data file's size was understated before F-M214.
    /// </summary>
    /// <returns>Bytes on disk.</returns>
    private static long FileBytesLocked()
    {
        try
        {
            long bytes = 0;
            foreach (string pfad in new[] { _path, _path == null ? null : _path + "-wal", _path == null ? null : _path + "-shm" })
            {
                if (pfad != null && File.Exists(pfad))
                {
                    bytes += new FileInfo(pfad).Length;
                }
            }

            return bytes;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool IsFresh(string kind, string value, DateTime written)
        => DateTime.UtcNow - written <= LifetimeOfFact(kind, value);

    private static void AfterWriteLocked(int factBytes, int bodyBytes)
    {
        _factBytes += factBytes;
        _bodyBytes += bodyBytes;
        _writes++;
        if ((_factBytes > FactCeilingBytes || _bodyBytes > BodyCeilingBytes) && _writes % 10 == 0)
        {
            TrimLocked("ceiling");
        }

        if (Mirror.Count > MemoryMirrorCap)
        {
            DropMirrorHalfLocked();
        }
    }

    private static void MirrorStoreLocked(string key, string value, DateTime written)
    {
        Mirror[key] = (value, written);
        if (Mirror.Count > MemoryMirrorCap)
        {
            DropMirrorHalfLocked();
        }
    }

    private static void DropMirrorHalfLocked()
    {
        foreach (var alt in Mirror.OrderBy(e => e.Value.Written).Take(Mirror.Count / 2).Select(e => e.Key).ToList())
        {
            Mirror.Remove(alt);
        }

        Log("memory mirror dropped to {Count} entr(ies) (F-M773)", Mirror.Count);
    }

    private static void TrimLocked(string reason)
    {
        if (_db == null)
        {
            return;
        }

        try
        {
            int abgelaufen = 0;
            long fakten = 0;
            long koerper = 0;
            var faktenschuld = new List<(string Kind, string Subject)>();
            var koerperschuld = new List<string>();

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT Kind, Subject, Value, Written FROM tmdb_fact;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string kind = reader.GetString(0);
                    string value = reader.GetString(2);
                    DateTime written = Parse(reader.GetString(3));
                    if (!IsFresh(kind, value, written))
                    {
                        faktenschuld.Add((kind, reader.GetString(1)));
                        abgelaufen++;
                    }
                    else
                    {
                        fakten += System.Text.Encoding.UTF8.GetByteCount(value);
                    }
                }
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT Key, Written FROM tmdb_body;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string key = reader.GetString(0);
                    if (DateTime.UtcNow - Parse(reader.GetString(1)) > LifetimeOfBody(key))
                    {
                        koerperschuld.Add(key);
                        abgelaufen++;
                    }
                }
            }

            foreach (var (kind, subject) in faktenschuld)
            {
                DeleteFactLocked(kind, subject);
            }

            foreach (string key in koerperschuld)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "DELETE FROM tmdb_body WHERE Key = $k;";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.ExecuteNonQuery();
            }

            // Decken: die aeltesten zuerst, bis beide wieder passen.
            int verdraengt = 0;
            long faktenBytes = SumLocked("tmdb_fact");
            long koerperBytes = SumLocked("tmdb_body");
            while (faktenBytes > FactCeilingBytes)
            {
                if (!DeleteOldestLocked("tmdb_fact", "Kind", "Subject"))
                {
                    break;
                }

                verdraengt++;
                faktenBytes = SumLocked("tmdb_fact");
            }

            while (koerperBytes > BodyCeilingBytes)
            {
                if (!DeleteOldestLocked("tmdb_body", "Key", null))
                {
                    break;
                }

                verdraengt++;
                koerperBytes = SumLocked("tmdb_body");
            }

            _factBytes = faktenBytes;
            _bodyBytes = koerperBytes;
            Log("{Reason} — {Expired} expired, {Evicted} evicted; facts {FactMb:0.0} MB / {FactCeil} MB, raw answers {BodyMb:0.0} MB / {BodyCeil} MB (F-M772)",
                reason,
                abgelaufen,
                verdraengt,
                faktenBytes / (1024.0 * 1024.0),
                FactCeilingBytes / (1024L * 1024L),
                koerperBytes / (1024.0 * 1024.0),
                BodyCeilingBytes / (1024L * 1024L));
        }
        catch (Exception ex)
        {
            _log?.LogWarning("{Prefix}trim failed: {Type}: {Msg} (F-M772)", LogPrefix, ex.GetType().Name, ex.Message);
        }
    }

    private static long SumLocked(string table)
    {
        if (_db == null)
        {
            return 0;
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(SUM(Bytes), 0) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static bool DeleteOldestLocked(string table, string keyA, string? keyB)
    {
        if (_db == null)
        {
            return false;
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = keyB == null
            ? $"DELETE FROM {table} WHERE Key = (SELECT Key FROM {table} ORDER BY Written ASC LIMIT 1);"
            : $"DELETE FROM {table} WHERE (Kind, Subject) = (SELECT Kind, Subject FROM {table} ORDER BY Written ASC LIMIT 1);";
        return cmd.ExecuteNonQuery() > 0;
    }

    private static void DeleteFactLocked(string kind, string subject)
    {
        if (_db == null)
        {
            return;
        }

        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM tmdb_fact WHERE Kind = $k AND Subject = $s;";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$s", subject);
            cmd.ExecuteNonQuery();
            Mirror.Remove(kind + "|" + subject);
        }
        catch (Exception ex)
        {
            _log?.LogWarning("{Prefix}removing {Kind}|{Subject} failed: {Type}: {Msg} (F-M773)", LogPrefix, kind, subject, ex.GetType().Name, ex.Message);
        }
    }

    private static void Exec(string sql)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static DateTime Parse(string text)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var zeit) ? zeit.ToUniversalTime() : DateTime.UtcNow;

    private static string Short(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        int frage = key.IndexOf('?', StringComparison.Ordinal);
        return frage > 0 ? key[..frage] : key;
    }

    private static void Log(string message, params object?[] args)
    {
        if (_mode >= Configuration.LogLevelMode.Debug)
        {
            _log?.LogInformation(LogPrefix + message, args);
        }
    }
}

/// <summary>
/// F-M778: the state of the TMDb shelf — what the diagnostics endpoint reports so a ceiling can be
/// checked from outside instead of being taken on trust.
/// </summary>
public sealed class TmdbCacheStats
{
    /// <summary>True when the shelf is attached.</summary>
    public bool Enabled { get; set; }

    /// <summary>Rows per kind of fact.</summary>
    public Dictionary<string, (long Rows, long Bytes)> FactsByKind { get; } = new(StringComparer.Ordinal);

    /// <summary>Rows over all facts.</summary>
    public long FactRows { get; set; }

    /// <summary>Bytes over all facts.</summary>
    public long FactBytes { get; set; }

    /// <summary>Rows of raw answers.</summary>
    public long BodyRows { get; set; }

    /// <summary>Bytes of raw answers.</summary>
    public long BodyBytes { get; set; }

    /// <summary>Size of the cache file, which is the ceiling that can be checked.</summary>
    public long FileBytes { get; set; }

    /// <summary>Ceiling over the facts.</summary>
    public long FactCeilingBytes { get; set; }

    /// <summary>Ceiling over the raw answers.</summary>
    public long BodyCeilingBytes { get; set; }
}
