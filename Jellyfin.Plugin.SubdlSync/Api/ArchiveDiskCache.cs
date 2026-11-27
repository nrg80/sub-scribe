using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M370: archives kept on disk between runs, keyed by <see cref="ArchiveKey"/>.
/// <para>
/// F-M665: dieser Cache gehoert keiner Quelle. Jede Engine legt ihre Archive hier ab; welche Quelle
/// eine Datei geliefert hat, steht im Schluesselraum (<see cref="ArchiveKey.ForTvstSeason"/> fuer die
/// Staffel-ZIP), nicht im Verzeichnis. Frist und Groessengrenze gelten deshalb fuer alles, was in der
/// Ablage liegt — sie sind Eigenschaften des Caches, nicht einer Quelle.
/// </para>
/// <para>
/// The run cache (F-M369) only helps inside one run — but a season pack serves one episode per
/// run, and with the free daily limit at 50 downloads a season needs several days. Measured on
/// 01.11.2026: 51 archive fetches for 45 files, 26 of them on three packs; the run stopped at
/// 98 of 1300 queued items because the quota was gone, so the next run asks for the same packs
/// again. Every avoided fetch on disk is one more file per day.
/// </para>
/// <para>
/// Two switches, both in the Download tab: <c>days</c> (0 = off) and <c>megabytes</c>
/// (0 = off). Either at 0 disables the whole thing. The ceiling is enforced oldest-first, and
/// only <c>.zip</c> bodies under <see cref="MaxEntryBytes"/> are stored,
/// so a runaway upstream cannot fill the volume.
/// </para>
/// </summary>
public sealed class ArchiveDiskCache
{
    /// <summary>Ceiling per archive: bodies above this are not written (F-M386: own constant,
    /// it used to be borrowed from the removed run cache).</summary>
    private const int MaxEntryBytes = 16 * 1024 * 1024;

    private readonly object _sync = new();
    private string? _directory;
    private int _ttlDays;
    private long _maxBytes;

    /// <summary>
    /// F-M767 (Bediener 26.11.2026: „Dann bitte ein umfangreiches debug logging fuer alle
    /// cache Operationen einfuehren"): die Ablage sagt JETZT jede Operation. Bis 12.3.8.66 sagte sie
    /// nichts — der SubDL-Client hatte zwei Zeilen auf einem Kanal, den Jellyfin nicht schreibt, und
    /// der tvst-Client gar keine. Eine Zeile je Treffer, Fehltreffer (mit Grund), Ablage (mit Grund),
    /// Aufraeumen und Purge, alle mit Schluessel und Bytes.
    /// <para>
    /// Der Logger kommt vom Lauf (<see cref="Attach"/>), nicht aus dem Konstruktor: die Instanz lebt
    /// im API-Client, der keinen Logger kennt. Ohne Logger schweigt die Ablage wie vorher.
    /// </para>
    /// </summary>
    private Microsoft.Extensions.Logging.ILogger? _logger;

    /// <summary>
    /// Der Meldegrad, den die Zeilen brauchen. Vorgabe ist <see cref="Configuration.LogLevelMode.Debug"/>
    /// — die Stufe, die der Betreiber genau dafür einschaltet; der Lauf reicht seinen eigenen Grad durch.
    /// </summary>
    private Configuration.LogLevelMode _mode;

    /// <summary>Initializes a new instance of the <see cref="ArchiveDiskCache"/> class.</summary>
    /// <param name="logger">Optional logger; may also be attached later via <see cref="Attach"/>.</param>
    /// <param name="mode">Log level the cache reports at; default is the plugin's Debug mode.</param>
    public ArchiveDiskCache(Microsoft.Extensions.Logging.ILogger? logger = null, Configuration.LogLevelMode mode = Configuration.LogLevelMode.Debug)
    {
        _logger = logger;
        _mode = mode;
    }

    /// <summary>
    /// F-M427 (operator order 03.11.2026: "Cache bei db reset bitte mit purgen"): removes every archive
    /// this cache wrote, and nothing else.
    /// <para>
    /// Static and folder-based on purpose: a reset must be able to purge without a live instance, and the
    /// directory is the plugin's own (`&lt;data folder&gt;/archive-cache`), so no path is passed in from a
    /// caller that might get it wrong. Leftovers under <c>.tmp</c> go with it — an interrupted store
    /// leaves one behind.
    /// </para>
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="logger">Optional logger — the purge is the one operation without an instance.</param>
    /// <param name="mode">Log level the purge reports at; default is the plugin's Debug mode.</param>
    /// <returns>Number of archives removed.</returns>
    public static int PurgeDirectory(
        string dataFolder, Microsoft.Extensions.Logging.ILogger? logger = null, Configuration.LogLevelMode mode = Configuration.LogLevelMode.Debug)
    {
        string dir = Path.Combine(dataFolder, "archive-cache");
        if (!System.IO.Directory.Exists(dir))
        {
            Pipeline.LogUtil.Detail(mode, logger, "[Sub Scribe-Cache] purge — no folder \"{Dir}\", nothing to remove (F-M767)", dir);
            return 0;
        }

        int removed = 0;
        foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.zip"))
        {
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException)
            {
                // Leave it: the count in the log says what really went.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (string path in System.IO.Directory.EnumerateFiles(dir, "*.tmp"))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Pipeline.LogUtil.Detail(
            mode, logger, "[Sub Scribe-Cache] purge — {Removed} archive(s) removed from \"{Dir}\" (F-M767)", removed, dir);
        return removed;
    }

    /// <summary>
    /// F-M767: gibt der Ablage den Logger des Laufs. Der Lauf ruft das, sobald er seinen Cache benutzt
    /// — danach ist jede Operation im Log (Detail-Ebene, also bei Protokollierung „Debug").
    /// </summary>
    /// <param name="logger">The run's logger.</param>
    /// <param name="mode">The run's log mode — the cache speaks at Detail (mode „Debug").</param>
    public void Attach(Microsoft.Extensions.Logging.ILogger? logger, Configuration.LogLevelMode mode = Configuration.LogLevelMode.Debug)
    {
        _logger = logger;
        _mode = mode;
    }

    private void Log(string message, params object?[] args)
        => Pipeline.LogUtil.Detail(_mode, _logger, "[Sub Scribe-Cache] " + message, args);

    /// <summary>Gets the number of lookups answered from disk since <see cref="Reset"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Gets the number of lookups that found nothing usable.</summary>
    public int Misses { get; private set; }

    /// <summary>Gets the number of archives written to disk.</summary>
    public int Stores { get; private set; }

    /// <summary>Gets the number of files deleted by expiry or by the size ceiling.</summary>
    public int Removed { get; private set; }

    /// <summary>Gets the last error the cache swallowed, for the diagnostics endpoint.</summary>
    public string? LastError { get; private set; }

    /// <summary>Gets a value indicating whether the cache currently reads and writes.</summary>
    public bool Enabled => _directory != null && _ttlDays > 0 && _maxBytes > 0;

    /// <summary>Gets the directory the cache lives in (null when never configured).</summary>
    public string? DirectoryPath => _directory;

    /// <summary>
    /// Points the cache at a directory and applies the two switches. Safe to call on every run.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder; the cache uses <c>archive-cache</c> inside it.</param>
    /// <param name="days">Retention in days; 0 disables the cache.</param>
    /// <param name="megabytes">Size ceiling in MB; 0 disables the cache.</param>
    public void Configure(string? dataFolder, int days, int megabytes)
    {
        lock (_sync)
        {
            _ttlDays = days < 0 ? 0 : days;
            _maxBytes = megabytes <= 0 ? 0 : (long)megabytes * 1024L * 1024L;

            if (string.IsNullOrWhiteSpace(dataFolder) || _ttlDays == 0 || _maxBytes == 0)
            {
                _directory = null;
                Log("off — {Days} day(s), {Mb} MB: nothing is read or written (F-M370)", _ttlDays, _maxBytes / (1024L * 1024L));
                return;
            }

            string dir = Path.Combine(dataFolder, "archive-cache");
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                _directory = dir;
                Log("configured — folder \"{Dir}\", {Days} day(s), {Mb} MB (F-M767)", dir, _ttlDays, _maxBytes / (1024L * 1024L));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _directory = null;
                Log("NOT configured — \"{Dir}\": {Error}", dir, ex.Message);
            }
        }
    }

    /// <summary>Clears the counters (not the files) at the start of a run.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            Hits = 0;
            Misses = 0;
            Stores = 0;
            Removed = 0;
            Log("counters reset — hits, misses, stores and removals start at zero for this run (F-M767)");
        }
    }

    /// <summary>
    /// Looks an archive up by key.
    /// </summary>
    /// <param name="key">Archive key from <see cref="ArchiveKey.FromUrl"/>.</param>
    /// <param name="bytes">The archive body when found.</param>
    /// <returns>Whether a live, non-expired archive was found.</returns>
    public bool TryGet(string? key, out byte[]? bytes)
    {
        bytes = null;
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            Log("lookup \"{Key}\" — the cache is off, nothing is read (F-M767)", key ?? "(no key)");
            return false;
        }

        lock (_sync)
        {
            try
            {
                string path = PathFor(key);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    Misses++;
                    Log("miss \"{Key}\" — no archive on disk (F-M767)", key);
                    return false;
                }

                if (IsExpired(info))
                {
                    double tage = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalDays;
                    Delete(path, "expired");
                    Misses++;
                    Log("miss \"{Key}\" — expired ({Age:0.0} day(s) > {Ttl} day(s)), removed (F-M767)", key, tage, _ttlDays);
                    return false;
                }

                bytes = File.ReadAllBytes(path);

                // Touch: retention counts from the last USE, so a pack that keeps serving
                // episodes does not expire in the middle of a season.
                try
                {
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                }
                catch (Exception)
                {
                    // A read-only volume must not turn a hit into a miss.
                }

                Hits++;
                Log(
                    "hit \"{Key}\" — {Bytes} B, written {Age:0.0} h ago, {Count} archive(s) / {Total} B held (F-M767)",
                    key, bytes.Length, (DateTime.UtcNow - info.LastWriteTimeUtc).TotalHours, Count(), SizeBytes());
                return bytes.Length > 0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Misses++;
                Log("miss \"{Key}\" — unreadable: {Error} (F-M767)", key, ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Stores an archive. Bodies that are not ZIPs, or that exceed the per-archive ceiling, are
    /// not written — a cache must not become a dump.
    /// </summary>
    /// <param name="key">Archive key.</param>
    /// <param name="bytes">Archive body.</param>
    public void Put(string? key, byte[]? bytes)
    {
        if (!Enabled || string.IsNullOrEmpty(key) || bytes == null || bytes.Length == 0)
        {
            Log("not stored \"{Key}\" — {Why} (F-M767)", key ?? "(no key)",
                !Enabled ? "the cache is off"
                : string.IsNullOrEmpty(key) ? "no key"
                : bytes == null ? "no body"
                : "empty body");
            return;
        }

        if (bytes.Length > MaxEntryBytes)
        {
            Log("not stored \"{Key}\" — {Bytes} B is above the per-archive ceiling of {Max} B (F-M767)", key, bytes.Length, MaxEntryBytes);
            return;
        }

        // Packs only, for the same reason the client refuses plain bodies: a page segment is
        // shared by every file of that page, so a single file stored under it would be served to
        // the wrong caller. A ZIP carries the whole page (see ArchiveKey and F-M370).
        if (bytes.Length <= 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            // Packs only: a single body under a page segment would be served to the wrong caller.
            Log("not stored \"{Key}\" — {Bytes} B is not a ZIP, only containers are kept (F-M767)", key, bytes.Length);
            return;
        }

        lock (_sync)
        {
            try
            {
                string path = PathFor(key);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                Stores++;
                Log(
                    "stored \"{Key}\" — {Bytes} B; {Count} archive(s) / {Total} B on disk (F-M767)",
                    key, bytes.Length, Count(), SizeBytes());
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log("NOT stored \"{Key}\" — write failed: {Error} (F-M767)", key, ex.Message);
            }
        }
    }

    /// <summary>
    /// Drops expired archives and then the oldest ones until the ceiling is met. Called at run
    /// start, so a run never begins on a full volume.
    /// </summary>
    public void Trim()
    {
        if (!Enabled || _directory == null)
        {
            Log("trim skipped — the cache is off (F-M767)");
            return;
        }

        lock (_sync)
        {
            try
            {
                var files = new List<FileInfo>();
                int abgelaufen = 0;
                foreach (string path in System.IO.Directory.EnumerateFiles(_directory, "*.zip"))
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    if (IsExpired(info))
                    {
                        Delete(path, "expired");
                        abgelaufen++;
                        continue;
                    }

                    files.Add(info);
                }

                long total = files.Sum(f => f.Length);
                if (total <= _maxBytes)
                {
                    Log(
                        "trim — {Expired} expired, nothing evicted; {Count} archive(s) / {Total} B held, ceiling {Max} B (F-M767)",
                        abgelaufen, files.Count, total, _maxBytes);
                    return;
                }

                int verdrangt = 0;
                foreach (var info in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= _maxBytes)
                    {
                        break;
                    }

                    total -= info.Length;
                    Delete(info.FullName, "size ceiling");
                    verdrangt++;
                }

                Log(
                    "trim — {Expired} expired, {Evicted} evicted for the size ceiling; {Total} B held, ceiling {Max} B (F-M767)",
                    abgelaufen, verdrangt, total, _maxBytes);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log("trim failed — {Error} (F-M767)", ex.Message);
            }
        }
    }

    /// <summary>Total bytes currently held on disk, for the diagnostics endpoint.</summary>
    /// <returns>Size in bytes; 0 when disabled or unreadable.</returns>
    public long SizeBytes()
    {
        if (_directory == null)
        {
            return 0;
        }

        try
        {
            return System.IO.Directory.EnumerateFiles(_directory, "*.zip")
                .Select(p => new FileInfo(p))
                .Where(f => f.Exists)
                .Sum(f => f.Length);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Number of archives currently on disk.</summary>
    /// <returns>File count.</returns>
    public int Count()
    {
        if (_directory == null)
        {
            return 0;
        }

        try
        {
            return System.IO.Directory.EnumerateFiles(_directory, "*.zip").Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private string PathFor(string key) => Path.Combine(_directory!, key + ".zip");

    private bool IsExpired(FileInfo info) =>
        _ttlDays > 0 && info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-_ttlDays);

    /// <summary>
    /// Removes one entry by key — for an entry that is there but unusable (F-M787: a damaged entry is
    /// removed, not repaired).
    /// </summary>
    /// <param name="key">The entry's key.</param>
    /// <param name="reason">Why it goes, for the log line.</param>
    public void Remove(string? key, string reason)
    {
        if (!Enabled || string.IsNullOrEmpty(key))
        {
            return;
        }

        lock (_sync)
        {
            Delete(PathFor(key), reason);
        }
    }

    private void Delete(string path, string reason)
    {
        try
        {
            File.Delete(path);
            Removed++;
            Log("removed \"{File}\" — {Reason} (F-M767)", Path.GetFileName(path), reason);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log("could not remove \"{File}\" — {Reason}: {Error} (F-M767)", Path.GetFileName(path), reason, ex.Message);
        }
    }
}
