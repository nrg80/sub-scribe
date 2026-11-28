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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Pipeline;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// (20.09.2026): live traffic-light status for SubDL/TMDB credentials,
/// API health and media-directory write access. Used by the plugin status tab.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class ScribeStatusController : ControllerBase
{
    private readonly ILogger<ScribeStatusController> _logger;
    private readonly ILibraryManager _libraryManager;
    // F-M343: this one client serves two probes — the SubDL search probe (a SubDL call like any
    // other, see below) and the TMDb configuration probe at the bottom of this file — so both
    // identify the plugin. TMDb seeing it is intended; see ScribeApiClient.DefaultUserAgent.
    // F-M783: die Probe fragt wirklich — sie geht an der Ablage vorbei (F-M779) und zahlt dafuer den Takt,
    // wie jede andere externe Anfrage auch.
    private static readonly HttpClient _httpClient = BuildProbeClient();

    /// <summary>
    /// F-M783: der Client der Statusprobe — eigener Takt, eigener Handler, kein Regal. Die Probe fragt
    /// "ist TMDb erreichbar", also muss sie wirklich fragen (F-M779) und dafuer den Takt zahlen (F-M547).
    /// </summary>
    /// <returns>The one client the status probes use.</returns>
    private static HttpClient BuildProbeClient()
    {
        var client = new HttpClient(new PacingHandler(new SocketsHttpHandler()))
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ScribeApiClient.DefaultUserAgent);
        return client;
    }

    public ScribeStatusController(ILogger<ScribeStatusController> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Get a traffic-light status overview.
    /// </summary>
    /// <returns>Status object with subdl, tmdb and directory entries.</returns>
    [HttpGet("Status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var plugin = Plugin.Instance;

        // F-M730 (Betreiber-Entscheidung 24.11.2026: „Auch das könnte man in die maintenance/status
        // engine einbringen."): Die Seite baut keine Quellen-Clients mehr und liest keine Zugangsdaten
        // aus der Plugin-Konfiguration. Sie fragt jede Quelle nach ihrer Selbstauskunft und malt nur
        // noch Kacheln — welche Lichter eine Quelle setzt, entscheidet die Quelle.
        var lichter = new Dictionary<string, ApiStatusDto>(StringComparer.OrdinalIgnoreCase);
        using (var statusHttp = ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(15)))
        {
            var registry = Jellyfin.Plugin.SubScribe.Sources.SourceRegistry.BuildStatus(
                new Jellyfin.Plugin.SubScribe.Sources.SourceEngineContext(
                    statusHttp,
                    _logger,
                    TimeProvider.System,
                    DataFolder: plugin?.DataFolderPath),
                cfg);

            foreach (var quelle in registry.Status)
            {
                Jellyfin.Plugin.SubScribe.Sources.SourceStatusAnswer antwort;
                try
                {
                    antwort = await quelle.StatusAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    antwort = new Jellyfin.Plugin.SubScribe.Sources.SourceStatusAnswer(
                        quelle.Key,
                        new[]
                        {
                            new Jellyfin.Plugin.SubScribe.Sources.SourceStatusLight(
                                quelle.Key,
                                quelle.Key,
                                Jellyfin.Plugin.SubScribe.Sources.SourceHealth.Bad,
                                Configured: true,
                                Message: ex.Message),
                        });
                }

                foreach (var licht in antwort.Lights)
                {
                    lichter[licht.Key] = new ApiStatusDto
                    {
                        Label = licht.Label,
                        Configured = licht.Configured,
                        Light = licht.Health switch
                        {
                            Jellyfin.Plugin.SubScribe.Sources.SourceHealth.Good => "green",
                            Jellyfin.Plugin.SubScribe.Sources.SourceHealth.Warn => "yellow",
                            _ => "red",
                        },
                        RateLimited = licht.Health == Jellyfin.Plugin.SubScribe.Sources.SourceHealth.Warn,
                        Message = licht.Message,
                    };
                }
            }
        }

        ApiStatusDto Kachel(string key, string ersatz) =>
            lichter.TryGetValue(key, out var dto)
                ? dto
                : new ApiStatusDto
                {
                    Label = ersatz,
                    Light = "red",
                    Configured = false,
                    Message = "die Quelle hat sich nicht gemeldet",
                };

        var result = new StatusDto
        {
            Subdl = Kachel("subdl", "SubDL API"),
            SubdlLogin = Kachel("subdlLogin", "SubDL Login"),
            Tmdb = await CheckTmdbAsync(cfg, ct).ConfigureAwait(false),
            Tvst = Kachel("tvst", "tvst (tvsubtitles.net)"),
            Mst = Kachel("mst", "mst (moviesubtitles.org)"),
            Directories = CheckDirectories(cfg),
            Timestamp = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };
        return Ok(result);
    }

    /// <summary>
    /// Reads the cumulative GUI counters from the database.
    /// <para>
    /// Kept out of <c>getPluginConfiguration</c> on purpose: the counters live in the data file now
    /// (25.09.2026), so the GUI must ask the database, not the configuration.
    /// </para>
    /// </summary>
    /// <returns>Uploaded/downloaded totals and the period start.</returns>
    [HttpGet("Stats")]
    [AllowAnonymous]
    public IActionResult GetStats()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        var row = plugin.GetStatusStats();

        // F-M218: the quality counters ride along with the volume counters.
        return Ok(new
        {
            Uploaded = row.Uploaded,
            Downloaded = row.Downloaded,
            RejectedDownload = row.RejectedDownload,
            RejectedUpload = row.RejectedUpload,
            FittedToAudio = row.FittedToAudio,
            BrokenReported = row.BrokenReported, // F-M455
            SeasonPacksUploaded = row.SeasonPacksUploaded, // F-M759
            SeasonPacksDownloaded = row.SeasonPacksDownloaded, // F-M759
            LanguageCodesAllocated = row.LanguageCodesAllocated,
            LooseSubtitlesRenamed = row.LooseSubtitlesRenamed,
            SinceUtc = row.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Updated = row.Updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// The last run of every worker, read from the DATA FILE.
    /// <para>
    /// The postprocessing status used to be static in-memory state, so it read "never run" after every
    /// Jellyfin restart even when the task had run minutes earlier (measured 30.09.2026). This endpoint
    /// answers from the stored rows instead, which survive a restart, and reports every known worker —
    /// including ones that have never run, so the section is complete from the first page load.
    /// </para>
    /// </summary>
    /// <returns>One entry per worker with its last run.</returns>
    /// <summary>F-M401: true when this row describes a run that is in flight right now.</summary>
    private static bool IsLive(Data.WorkerRunEntity row)
        => string.Equals(row.Outcome, Registry.WorkerRunRegistry.Outcome.Running, StringComparison.Ordinal);

    /// <summary>
    /// F-M401: the dry flag of the run that is in flight for this worker, from live configuration.
    /// Falls back to the stored flag for every worker that has no dry mode of its own.
    /// </summary>
    private static bool LiveDryFlag(Data.WorkerRunEntity row)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg == null)
        {
            return row.DryRun;
        }

        if (string.Equals(row.Id, Registry.WorkerRunRegistry.UploadWorkerKey, StringComparison.Ordinal))
        {
            return cfg.DryRun;
        }

        if (string.Equals(row.Id, Registry.WorkerRunRegistry.DownloadWorkerKey, StringComparison.Ordinal))
        {
            return cfg.DownloadDryRun;
        }

        return row.DryRun;
    }

    [HttpGet("WorkerRuns")]
    [AllowAnonymous]
    public IActionResult GetWorkerRuns()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        var rows = plugin.WorkerRuns.List();

        // F-M292/F-M294 (user decision 03.10.2026): a worker whose deferred fire is still pending is
        // painted YELLOW, whichever worker it is — not just the two directions. A fire exists only
        // because a run could not do its work (quota, run lock, a restart mid-run), so the stored row
        // describes the past while the fire describes what is still owed.
        //
        // Measured on prod 03.10.2026: the download hit its daily limit at 06:08 leaving 384 of 412
        // queued items due and armed its fire for 04:37 the next day, then ran normally at 10:30 and
        // 13:50 — its last row read ok, the lamp went green, and the backlog was still waiting.
        //
        // Read from the coordinator rather than the stored row: the fire is live state, the row is a
        // record. Keys are the worker keys, so every worker can hold one.
        var pendingFires = new System.Collections.Generic.Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            var coord = Jellyfin.Plugin.SubScribe.ScheduledTasks.ScribeSchedulerCoordinator.Instance;
            if (coord != null)
            {
                pendingFires = coord.GetPendingFires();
            }
        }
        catch
        {
            // Plugin not fully started — the map stays empty and every row keeps its own colour.
            // This endpoint must never fail over a lamp.
        }

        // F-M352 (operator order 19.10.2026): the off state is NOT painted onto the lamps, and this
        // endpoint does not carry it either — it rides on the deferral status line, which the quota
        // answer feeds (ScribeQuotaService.LocalRateLimitState → `local.off`). The outcome, the detail and
        // a pending fire here stay exactly as they are, so a run never disappears behind a switch.
        // F-M445 (operator order 02.11.2026, "eine logzeile unter die worker schreiben wann ein
        // postprocessing ansteht. Zeile entfällt wenn gestartet"): the armed moment is reported on its
        // OWN field. It deliberately does NOT ride on the worker row: every finished upload run arms it,
        // so the row would sit on yellow "defer" permanently and the light would stop meaning anything.
        // F-M623 (Operator-Anweisung 19.11.2026): der aufgeschobene Zyklus hat keine Worker-Zeile, an
        // der seine Meldung haengen koennte — deshalb eigene Felder, wie beim Postprocessing (F-M445).
        DateTime? cycleDue = null;
        string? cycleWhy = null;
        int cycleAttempt = 0;
        int cycleAttemptLimit = 0;
        try
        {
            var coordCycle = Jellyfin.Plugin.SubScribe.ScheduledTasks.ScribeSchedulerCoordinator.Instance;
            if (coordCycle != null)
            {
                var (dueCycle, whyCycle, attemptsCycle) = coordCycle.GetCycleDeferral();
                cycleDue = dueCycle;
                cycleWhy = whyCycle;
                cycleAttempt = attemptsCycle;
                cycleAttemptLimit = Jellyfin.Plugin.SubScribe.ScheduledTasks.ScribeSchedulerCoordinator.RescheduleAttemptLimit;
            }
        }
        catch
        {
            // Plugin not fully started — no line then.
        }

        DateTime? postprocessDue = null;
        try
        {
            postprocessDue = Jellyfin.Plugin.SubScribe.ScheduledTasks.ScribeSchedulerCoordinator.Instance?
                .GetUploadPostprocessDue();
        }
        catch
        {
            // Plugin not fully started — no line then, the workers keep their own colours.
        }

        // F-M445: the postprocessing keeps its own line, so its worker row must NOT be repainted as
        // "defer" — every upload run arms the order, and a permanently yellow light says nothing.
        var workerFires = new System.Collections.Generic.Dictionary<string, DateTime>(pendingFires, StringComparer.Ordinal);
        workerFires.Remove("SubDLPostprocessTask");

        return Ok(new
        {
            PostprocessDue = postprocessDue?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            // F-M623: der Zyklus selbst — Faelligkeit, Grund, Versuch. Leer, wenn nichts aufgeschoben ist.
            CycleDue = cycleDue?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            CycleWhy = cycleWhy,
            CycleAttempt = cycleAttempt,
            CycleAttemptLimit = cycleAttemptLimit,
            Workers = rows.Select(r => new
            {
                r.Id,
                r.Name,
                Started = r.Started?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                Ended = r.Ended?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                r.Outcome,
                r.Detail,
                // F-M401 (operator order 02.11.2026): while a worker runs, the dry flag is the LIVE one.
                // The stored row is written when the run ENDS ("run" can also be written by the wait cap
                // of a cycle that is still working), so reading the row alone left "run" without the
                // marker exactly when the operator was watching. The live configuration is the truth
                // during a run; the stored flag stays the truth for a finished one.
                DryRun = IsLive(r) ? LiveDryFlag(r) : r.DryRun,
                // F-M294: per worker, plus the moment the fire is due so the page can name it.
                Deferred = workerFires.ContainsKey(r.Id),
                DeferUntil = workerFires.TryGetValue(r.Id, out var due)
                    ? due.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    : null
            }),
            Timestamp = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// Zeroes the cumulative GUI counters and restarts the period ("Reset statistics").
    /// </summary>
    /// <param name="confirm">Must be exactly "true".</param>
    /// <returns>The zeroed counters.</returns>
    [HttpPost("StatsReset")]
    public IActionResult ResetStats([FromQuery] string confirm = "")
    {
        if (!string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { ok = false, error = "confirmation-missing" });
        }

        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { ok = false, error = "plugin not initialized" });
        }

        var row = plugin.ResetStatusStats();
        LogUtil.Normal(_logger, "[Sub Scribe] statistics reset: counters zeroed, period restarted.");

        // F-M218/F-M219: the response is fed straight back into the renderer by the GUI
        // (window.subdlRenderStats(res)), so it must carry the SAME fields the Stats GET
        // returns. Returning only the volume counters left the quality line reading
        // "undefined typed by file name, undefined TMDb year-filter misses…" right after a
        // reset — the F-M216 class of bug, one endpoint over.
        return Ok(new
        {
            ok = true,
            Uploaded = row.Uploaded,
            Downloaded = row.Downloaded,
            SeasonPacksUploaded = row.SeasonPacksUploaded, // F-M759
            SeasonPacksDownloaded = row.SeasonPacksDownloaded, // F-M759
            RejectedDownload = row.RejectedDownload,
            RejectedUpload = row.RejectedUpload,
            FittedToAudio = row.FittedToAudio,
            BrokenReported = row.BrokenReported, // F-M455
            LanguageCodesAllocated = row.LanguageCodesAllocated,
            LooseSubtitlesRenamed = row.LooseSubtitlesRenamed,
            SinceUtc = row.SinceUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            Updated = row.Updated.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private async Task<ApiStatusDto> CheckTmdbAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var dto = new ApiStatusDto { Label = "TMDb API", Configured = !string.IsNullOrWhiteSpace(cfg.TmdbApiKey) };
        if (!dto.Configured)
        {
            // REQUIRED, not optional (user decision 25.09.2026): id resolution is
            // TMDB-authoritative and series are skipped entirely without a key, so a
            // missing key is a defect the user must see as red — not a yellow
            // "optional, fine to leave empty" hint.
            dto.Light = "red";
            dto.Message = "Not configured — REQUIRED: series are skipped without it (films still upload)";
            return dto;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            // F-M779: diese Anfrage geht BEWUSST an der Ablage vorbei. Sie ist keine Frage nach einer
            // Auskunft, sondern die Frage "ist TMDb gerade erreichbar" — eine aus der Ablage beantwortete
            // Probe waere tagelang gruen, auch wenn der Schluessel laengst nicht mehr gilt. Der eigene
            // HttpClient bleibt hier also richtig; er ist die einzige Stelle, die das Regal ueberspringt.
            var url = $"https://api.themoviedb.org/3/configuration?api_key={WebUtility.UrlEncode(cfg.TmdbApiKey)}";
            using var resp = await _httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                dto.Light = "red";
                dto.Message = "API key invalid (HTTP 401)";
            }
            else if ((int)resp.StatusCode == 429)
            {
                dto.Light = "yellow";
                dto.Message = "Rate limited (HTTP 429)";
                dto.RateLimited = true;
            }
            else if (resp.IsSuccessStatusCode)
            {
                dto.Light = "green";
                dto.Message = "reachable";
            }
            else
            {
                dto.Light = "red";
                dto.Message = $"API returned HTTP {(int)resp.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            dto.Light = "red";
            dto.Message = $"Probe error: {ex.Message}";
        }

        return dto;
    }

    private List<DirectoryStatusDto> CheckDirectories(PluginConfiguration cfg)
    {
        var result = new List<DirectoryStatusDto>();
        if (_libraryManager == null)
        {
            return result;
        }

        var scope = LibraryScope.Create(_libraryManager, cfg.SelectedLibraries, _logger);
        if (scope.IsEmpty)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = _libraryManager.GetVirtualFolders();
        foreach (var folder in folders)
        {
            // F-M213 (26.09.2026, user decision): the report is about the
            // SELECTION, never about the walk scope. A nested selection's items
            // do live in the outer library (F-M189) and the walk must keep
            // enumerating it — but listing that outer library here showed a
            // directory the user never picked (/media/films next to the
            // selected /media/films/Testing), which reads as "all of this is
            // scanned". A status row therefore names what was selected.
            if (!scope.IsSelectedName(folder.Name))
            {
                continue;
            }

            foreach (var loc in folder.Locations)
            {
                if (!seen.Add(loc))
                {
                    continue;
                }

                var dto = new DirectoryStatusDto
                {
                    LibraryName = folder.Name,
                    Path = loc,
                    Exists = Directory.Exists(loc),
                    Writable = false
                };

                if (!dto.Exists)
                {
                    dto.Light = "red";
                    dto.Message = "Directory does not exist";
                }
                else
                {
                    try
                    {
                        var probe = Path.Combine(loc, ".subdl-write-probe");
                        System.IO.File.WriteAllBytes(probe, new byte[] { 0x70 });
                        System.IO.File.Delete(probe);
                        dto.Writable = true;
                        dto.Light = "green";
                        dto.Message = "Writable";
                    }
                    catch (Exception ex)
                    {
                        dto.Light = "red";
                        dto.Message = $"Not writable: {ex.Message}";
                    }
                }

                result.Add(dto);
            }
        }

        return result;
    }

    /// <summary>DTO returned by /Plugins/SubdlSync/Status.</summary>
    public class StatusDto
    {
        public ApiStatusDto Subdl { get; set; } = new();
        public ApiStatusDto SubdlLogin { get; set; } = new();
        public ApiStatusDto Tmdb { get; set; } = new();

        /// <summary>
        /// Operator order 16.11.2026: the tvst family has two addresses and each is probed on its
        /// own — one can answer while the other does not. The SubDL quota bars above are SubDL's
        /// allowance, not theirs; the labels say so.
        /// </summary>
        public ApiStatusDto Tvst { get; set; } = new();

        /// <summary>See <see cref="Tvst"/>: the film address of the same family.</summary>
        public ApiStatusDto Mst { get; set; } = new();
        public List<DirectoryStatusDto> Directories { get; set; } = new();
        public string Timestamp { get; set; } = string.Empty;
    }

    /// <summary>Traffic-light status for a single API.</summary>
    public class ApiStatusDto
    {
        public string Label { get; set; } = string.Empty;
        public string Light { get; set; } = "red";
        public bool Configured { get; set; }
        public bool RateLimited { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Traffic-light status for a single library directory.</summary>
    public class DirectoryStatusDto
    {
        public string LibraryName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public bool Writable { get; set; }
        public string Light { get; set; } = "red";
        public string Message { get; set; } = string.Empty;
    }
}
