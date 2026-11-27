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
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// (20.09.2026): diagnostics endpoint that exposes store counters,
/// subtitle state and recent run records without requiring shell/SSH access.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class ScribeDiagnosticsController : ControllerBase
{
    private readonly ILogger<ScribeDiagnosticsController> _logger;

    public ScribeDiagnosticsController(ILogger<ScribeDiagnosticsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get a diagnostic dump of the plugin database: counters, subtitle totals,
    /// per-media upload state and recent pipeline runs.
    /// </summary>
    [HttpGet("Diagnostics")]
    [AllowAnonymous]
    public IActionResult GetDiagnostics()
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return StatusCode(503, new { error = "Plugin instance not available" });
            }

            var db = plugin.SharedDbContext;

            // F-M770: dieser Endpunkt LIEST nur. Deshalb nimmt er die Zeilen ohne Verfolgung — sonst
            // haengt er rund 13.000 Zeilen dauerhaft in den geteilten Kontext (F-M769: 31.117 mitgefuehrte
            // Zeilen gemessen). Die Uhr daneben ist fuer die Debug-Zeile am Ende.
            var uhr = System.Diagnostics.Stopwatch.StartNew();
            var gelesen = 0;

            var counters = db.Counters.ReadOnlySnapshot();
            gelesen += counters.Count;
            var notFoundCounters = counters
                .Where(c => c.Key.StartsWith("not-found:", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Value)
                .Take(50)
                .Select(c => new
                {
                    Key = c.Key,
                    Value = c.Value,
                    Updated = c.Updated,
                    Expires = c.Expires
                })
                .ToList();

            var embeds = db.Tracks.ReadOnlySnapshot();
            var sidecars = db.Sidecars.ReadOnlySnapshot();
            var media = db.Files.ReadOnlySnapshot();
            var runs = db.Runs.ReadOnlySnapshot().OrderByDescending(r => r.Started).Take(20).ToList();
            var oshashes = db.Oshashes.CountReadOnly();
            var burnedCandidates = db.BadApples.CountReadOnly();
            gelesen += embeds.Count + sidecars.Count + media.Count + runs.Count;

            var mediaByUploadState = media
                .GroupBy(m => m.SubtitlesUploadedAt != null)
                .ToDictionary(g => g.Key ? "complete" : "pending", g => g.Count());

            var mediaSample = media
                .Where(m => m.SubtitlesUploadedAt == null)
                .OrderByDescending(m => m.LastSeen)
                .Take(20)
                .Select(m => new
                {
                    m.FileId,
                    m.Path,
                    m.ImdbId,
                    m.TmdbId,
                    m.SdId,
                    m.IsSeries,
                    m.Season,
                    m.Episode,
                    m.SubtitlesUploadedAt,
                    m.LanguagesAvailable,
                    m.LastSeen
                })
                .ToList();

            var subtitleTotals = new
            {
                Total = embeds.Count + sidecars.Count,
                Embeds = embeds.Count,
                Sidecars = sidecars.Count,
                EmbedsByStatus = embeds.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
                SidecarsByStatus = sidecars.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
                RejectedReasons = embeds.Where(s => s.Reason != null).Select(s => s.Reason!)
                    .Concat(sidecars.Where(s => s.Reason != null).Select(s => s.Reason!))
                    .GroupBy(r => r)
                    .ToDictionary(g => g.Key, g => g.Count()),
                // F-M670: die Zahl kommt aus der Marken-Matrix — es gibt keine Id-Spalte an `embeds`/
                // `sidecars` mehr, aus der sie gezählt werden könnte. „Mit Id" heißt: die Marke führt eine
                // Eintrags- oder Einsende-Id bei SubDL.
                WithSubdlId = SourceStore.CountWithId(db, SourceStore.Subdl)
            };

            var result = new
            {
                DbPath = Data.DbFiles.PathIn(plugin.DataFolderPath),
                DbExists = System.IO.File.Exists(Data.DbFiles.PathIn(plugin.DataFolderPath)),
                DbSizeBytes = GetDbSize(plugin.DataFolderPath),
                Counters = new
                {
                    Total = counters.Count,
                    NotFound = notFoundCounters
                },
                Subtitles = subtitleTotals,
                Media = new
                {
                    Total = media.Count,
                    ByUploadState = mediaByUploadState,
                    PendingSample = mediaSample
                },
                Runs = runs.Select(r => new
                {
                    r.Id,
                    r.Direction,
                    r.Trigger,
                    Started = r.Started,
                    Ended = r.Ended,
                    r.ItemsTotal,
                    r.ItemsDone
                }),
                // F-M778: der Zustand der TMDb-Ablage — Zeilen und Bytes je Art und die Groesse der Datei.
                // Ohne diese Zahlen waere eine Decke eine Behauptung.
                TmdbCache = DescribeTmdbCache(),
                OshashCacheEntries = oshashes,
                RejectedCandidateEntries = burnedCandidates,
                // F-M769: der eigene Speicher, aus dem Prozess heraus gemessen. Ohne diese vier Zahlen
                // ist "das Plugin zieht RAM" eine Behauptung — der Diagnose-Endpunkt war der einzige Ort,
                // an dem er sich selbst befragen kann, und er sagte bisher nichts dazu.
                Memory = new
                {
                    // Was der GC als erreichbar ansieht (nach der letzten Sammlung, ohne Collect).
                    GcHeapBytes = GC.GetTotalMemory(false),
                    // Was das Betriebssystem dem Prozess gibt — die Zahl, die im Container zählt.
                    WorkingSetBytes = Environment.WorkingSet,
                    PrivateBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64,
                    Gen0 = GC.CollectionCount(0),
                    Gen1 = GC.CollectionCount(1),
                    Gen2 = GC.CollectionCount(2),
                    // Zeilen, die der geteilte Kontext gerade mitführt — der größte eigene Posten (F-M769).
                    TrackedRows = db.Context.ChangeTracker.Entries().Count()
                },
                Timestamp = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            };

            // F-M770: eine Debug-Zeile je Abruf — wie viele Zeilen gelesen wurden, wie lange es dauerte und
            // wie viele Zeilen der Kontext danach mitfuehrt. Ohne sie steht im Log nur, DASS jemand berichtet
            // hat, nicht was der Abruf gekostet hat.
            uhr.Stop();
            Pipeline.LogUtil.Detail(
                plugin.Configuration.LogMode,
                _logger,
                "[Sub Scribe] diagnostics read — {Rows} row(s) in {Ms} ms, {Tracked} row(s) tracked after it (F-M770)",
                gelesen,
                uhr.ElapsedMilliseconds,
                result.Memory.TrackedRows);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Sub Scribe] diagnostics endpoint failed");
            return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
        }
    }

    /// <summary>
    /// F-M778: rows, bytes and file size of the TMDb shelf, so its ceilings can be checked from outside.
    /// </summary>
    /// <returns>The state as the endpoint reports it.</returns>
    private static object DescribeTmdbCache()
    {
        var stats = TmdbCache.Stats();
        return new
        {
            stats.Enabled,
            stats.FactRows,
            stats.FactBytes,
            stats.BodyRows,
            stats.BodyBytes,
            stats.FileBytes,
            stats.FactCeilingBytes,
            stats.BodyCeilingBytes,
            ByKind = stats.FactsByKind.ToDictionary(
                e => e.Key,
                e => new { Rows = e.Value.Rows, Bytes = e.Value.Bytes })
        };
    }

    private static long? GetDbSize(string dataFolderPath)
    {
        try
        {
            var path = DbFiles.PathIn(dataFolderPath);
            return System.IO.File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }
}
