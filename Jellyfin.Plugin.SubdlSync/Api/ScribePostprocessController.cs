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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// Manual trigger for upload postprocessing and debug endpoint for diagnostics.
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
public class ScribePostprocessController : ControllerBase
{
    private readonly ILogger<ScribePostprocessController> _logger;

    public ScribePostprocessController(ILogger<ScribePostprocessController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run upload postprocessing immediately (cleans up rejected entries from /user/mySubtitles).
    /// </summary>
    [HttpPost("PostprocessUploads")]
    [AllowAnonymous]
    public async Task<IActionResult> PostprocessUploads()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        // F-M291 (operator order 08.10.2026): a MANUAL trigger always runs, with upload switched on
        // or off. The upload binding belongs to the automatic run only, and it is enforced where that
        // run is armed (the scheduler's anchor).
        LogUtil.Normal(_logger, "[Sub Scribe] Manual upload postprocessing triggered via API.");
        await UploadPipeline.RunUploadPostprocessingAsync().ConfigureAwait(false);
        return Ok(new { status = "postprocessing completed" });
    }

    /// <summary>
    /// Count /user/mySubtitles entries on the first N pages for verification.
    /// </summary>
    [HttpPost("CountMySubtitles")]
    [AllowAnonymous]
    public async Task<IActionResult> CountMySubtitles([FromQuery] int pages = 5)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        var cfg = plugin.Configuration;

        // F-M729: auch die Diagnose geht ueber den zweiten Vertrag — kein eigener Client, kein Zugang aus
        // der Plugin-Konfiguration. Gefragt wird seitenweise (StartPage/MaxPages=1), weil genau das die
        // Frage dieses Endpunkts ist: liefert die Seite ueberhaupt eine neue Seite?
        using var http = ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(60));
        var pflege = Jellyfin.Plugin.SubScribe.Sources.SourceRegistry.BuildMaintenance(
            new Jellyfin.Plugin.SubScribe.Sources.SourceEngineContext(
                http,
                plugin.LoggerFactory?.CreateLogger("SubDL-CountMySubtitles")
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TimeProvider.System,
                DataFolder: plugin.DataFolderPath),
            cfg);

        var perPageCounts = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var statusBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        bool more = true;
        bool paginationUnsupported = false;
        string? quelle = null;

        var maintenance = System.Linq.Enumerable.FirstOrDefault(pflege.Maintenance);
        if (maintenance == null)
        {
            return StatusCode(503, new { error = "no source offers its submissions" });
        }

        quelle = maintenance.Key;
        for (int page = 1; more && page <= Math.Max(1, pages); page++)
        {
            var answer = await maintenance
                .ListAsync(
                    new Jellyfin.Plugin.SubScribe.Sources.SourceSubmissionQuery(
                        Published: false,
                        MaxPages: 1,
                        StartPage: page),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!answer.Ok)
            {
                perPageCounts.Add(-1);
                if (perPageCounts.Count == 1)
                {
                    return StatusCode(502, new
                    {
                        error = "could not read the submissions",
                        source = quelle,
                        fault = answer.Fault.ToString(),
                        message = answer.Why,
                    });
                }

                break;
            }

            int newOnPage = 0;
            foreach (var entry in answer.Submissions)
            {
                if (!seen.Add(entry.Handle))
                {
                    continue;
                }

                string zustand = entry.State switch
                {
                    Jellyfin.Plugin.SubScribe.Sources.SubmissionState.Accepted => "accepted",
                    Jellyfin.Plugin.SubScribe.Sources.SubmissionState.Rejected => "rejected",
                    _ => "pending",
                };
                statusBreakdown[zustand] = statusBreakdown.TryGetValue(zustand, out int n) ? n + 1 : 1;
                newOnPage++;
            }

            perPageCounts.Add(answer.Submissions.Count);
            total += newOnPage;
            more = answer.Submissions.Count == 100 && newOnPage > 0;
            if (answer.Submissions.Count == 100 && newOnPage == 0)
            {
                paginationUnsupported = true;
            }
        }

        return Ok(new
        {
            source = quelle,
            pagesRequested = pages,
            perPageCounts,
            total,
            distinctUploadIds = seen.Count,
            hasMore = more,
            paginationUnsupported,
            statusBreakdown
        });
    }

    /// <summary>
    /// Get the current postprocessing run status and result of the last run.
    /// </summary>
    [HttpGet("PostprocessStatus")]
    [AllowAnonymous]
    public IActionResult GetPostprocessStatus()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        return Ok(new
        {
            running = UploadPipeline.IsPostprocessingRunning,
            startedUtc = UploadPipeline.PostprocessingStartedUtc,
            pendingTotal = UploadPipeline.PostprocessingPendingTotal,
            pendingRemaining = UploadPipeline.PostprocessingPendingRemaining,
            resolved = UploadPipeline.PostprocessingResolved,
            accepted = UploadPipeline.PostprocessingAccepted,
            rejected = UploadPipeline.PostprocessingRejected,
            deleted = UploadPipeline.PostprocessingDeleted,
            lastResult = UploadPipeline.PostprocessingLastResult
        });
    }
}
