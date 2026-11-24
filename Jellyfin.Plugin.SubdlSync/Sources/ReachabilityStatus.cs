// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M730: die Selbstauskunft einer Quelle OHNE Konto — eine Frage an ihre Startseite. Genau das ist
/// die Frage, die die Statusseite für tvst und mst gestellt hat, nur früher mit eigenem Code in der
/// Seite selbst. Der HttpClient wird gereicht (Grenzregel b), es gibt keine Zugangsdaten.
/// </summary>
public sealed class ReachabilityStatus : ISourceStatus
{
    private readonly SourceEngineContext _context;
    private readonly string _lightKey;
    private readonly string _label;
    private readonly string _url;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReachabilityStatus"/> class.
    /// </summary>
    /// <param name="key">Der Schlüssel der Quelle — derselbe Wert, den der Bestand führt.</param>
    /// <param name="lightKey">Woran die Statusseite die Kachel hängt (Datenwert, kürzer als der Quellenschlüssel).</param>
    /// <param name="label">Die Beschriftung im Klartext.</param>
    /// <param name="baseUrl">Die Adresse, die angefragt wird.</param>
    /// <param name="context">Was das Plugin reicht (HttpClient, Logger).</param>
    public ReachabilityStatus(string key, string lightKey, string label, string baseUrl, SourceEngineContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(lightKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(context);

        Key = key;
        _lightKey = lightKey;
        _label = label;
        _url = baseUrl.TrimEnd('/') + "/";
        _context = context;
    }

    /// <inheritdoc />
    public string Key { get; }

    /// <inheritdoc />
    public Task<SourceQuotaAnswer> QuotaAsync(bool forDownload, CancellationToken ct)
        => Task.FromResult(new SourceQuotaAnswer(
            SourceQuotaState.Unreadable,
            Reason: "this source keeps no quota"));   // F-M733: die Fähigkeit CanQuota sagt das schon vorher

    /// <inheritdoc />
    public async Task<SourceStatusAnswer> StatusAsync(CancellationToken ct)
    {
        var uhr = Stopwatch.StartNew();
        SourceStatusLight licht;
        try
        {
            using var resp = await _context.Http.GetAsync(_url, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            var gesundheit = resp.IsSuccessStatusCode
                ? SourceHealth.Good
                : resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests
                    ? SourceHealth.Warn
                    : SourceHealth.Bad;

            licht = new SourceStatusLight(
                _lightKey,
                _label,
                gesundheit,
                Configured: true,
                // F-M742: „reachable" heisst erreichbar — der Code gehoert nur dann dazu, wenn er
                // etwas zu sagen hat.
                Message: resp.IsSuccessStatusCode ? "reachable" : $"HTTP {code}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _context.Logger.LogDebug("[Sub Scribe-Q] {Source} status: {Msg}", Key, ex.Message);
            licht = new SourceStatusLight(_lightKey, _label, SourceHealth.Bad, Configured: true, Message: ex.Message);
        }

        uhr.Stop();
        return new SourceStatusAnswer(
            Key,
            new List<SourceStatusLight> { licht },
            LatencyMs: uhr.ElapsedMilliseconds);
    }
}
