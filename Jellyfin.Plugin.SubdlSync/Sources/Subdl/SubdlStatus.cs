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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources.Subdl;

/// <summary>
/// F-M730: die Selbstauskunft von SubDL — zwei Lichter (API und Anmeldung) und die Zahlen des
/// Kontingents. Vorher baute die Statusseite dafür zwei eigene Clients und las Schlüssel, Konto und
/// Passwort aus der Plugin-Konfiguration; jetzt fragt sie diese Engine.
/// </summary>
public sealed class SubdlStatus : ISourceStatus
{
    private const string ApiLightKey = "subdl";
    private const string LoginLightKey = "subdlLogin";

    private readonly ScribeApiClient _client;
    private readonly SourceSettings _settings;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlStatus"/> class.
    /// </summary>
    /// <param name="context">Was das Plugin reicht (HttpClient, Logger).</param>
    /// <param name="settings">Die Einstellungen aus der eigenen Datei dieser Quelle.</param>
    public SubdlStatus(SourceEngineContext context, SourceSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        _settings = settings ?? SourceSettings.Empty(SubdlEngine.SourceKey, SubdlEngine.Declared);
        _client = context.Subdl ?? new ScribeApiClient(context.Http);
        SubdlEngine.ApplyCredentials(_client, _settings);
    }

    /// <inheritdoc />
    public string Key => SubdlEngine.SourceKey;

    /// <inheritdoc />
    public async Task<SourceQuotaAnswer> QuotaAsync(bool forDownload, CancellationToken ct)
    {
        QuotaRead gelesen;
        try
        {
            gelesen = await _client.ReadQuotaAsync(forDownload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Sub Scribe-Q] {Source} quota: read failed: {Msg}", Key, ex.Message);
            return new SourceQuotaAnswer(SourceQuotaState.Unreadable, Reason: ex.Message);
        }

        var zahlen = _client.Quota;
        return new SourceQuotaAnswer(
            gelesen switch
            {
                QuotaRead.Available => SourceQuotaState.Available,
                QuotaRead.Spent => SourceQuotaState.Spent,
                _ => SourceQuotaState.Unreadable,
            },
            Limit: (forDownload ? zahlen?.DownloadsLimit : zahlen?.SearchLimit) ?? 0,
            Remaining: (forDownload ? zahlen?.DownloadsRemaining : zahlen?.SearchRemaining) ?? -1,
            ResetAtUtc: zahlen?.ResetAtUtc is { } r
                ? new DateTimeOffset(DateTime.SpecifyKind(r.ToUniversalTime(), DateTimeKind.Utc))
                : null);
    }

    /// <inheritdoc />
    public async Task<SourceStatusAnswer> StatusAsync(CancellationToken ct)
    {
        var uhr = Stopwatch.StartNew();
        var lichter = new List<SourceStatusLight>(2);

        bool hatSchluessel = _settings.Secret(SubdlEngine.FieldApiKey) is { Length: > 0 };
        QuotaRead gelesen = QuotaRead.Unreadable;
        ScribeQuotaSnapshot? zahlen = null;

        if (!hatSchluessel)
        {
            lichter.Add(new SourceStatusLight(
                ApiLightKey,
                "SubDL API (subdl.com)",
                SourceHealth.Warn,
                Configured: false,
                Message: "no API key configured"));
        }
        else
        {
            try
            {
                gelesen = await _client.ReadQuotaAsync(forDownload: false, ct).ConfigureAwait(false);
                zahlen = _client.Quota;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Sub Scribe-Q] {Source} status: quota read failed: {Msg}", Key, ex.Message);
                gelesen = QuotaRead.Unreadable;
            }

            lichter.Add(new SourceStatusLight(
                ApiLightKey,
                "SubDL API (subdl.com)",
                gelesen == QuotaRead.Unreadable ? SourceHealth.Bad : SourceHealth.Good,
                Configured: true,
                // F-M742: „reachable" — und KEINE Zahlen. Was das Kontingent verbraucht hat, steht in
                // den Balken darüber; die Ampel sagt nur, ob die Adresse antwortet. Vorher stand hier
                // „Suche 201/2000, Downloads 50/50" und damit dieselbe Aussage ein zweites Mal.
                Message: gelesen == QuotaRead.Unreadable
                    ? "not reachable"
                    : "reachable"));
        }

        bool hatKonto = _settings.Text(SubdlEngine.FieldAccount) is { Length: > 0 }
                        && _settings.Secret(SubdlEngine.FieldPassword) is { Length: > 0 };
        if (!hatKonto)
        {
            lichter.Add(new SourceStatusLight(
                LoginLightKey,
                "SubDL Login (subdl.com)",
                SourceHealth.Warn,
                Configured: false,
                Message: "no account configured — uploading is off"));
        }
        else
        {
            string meldung;
            SourceHealth licht;
            try
            {
                await _client.LoginAsync(ct).ConfigureAwait(false);
                licht = SourceHealth.Good;
                meldung = "OK";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                licht = SourceHealth.Bad;

                // F-M742: die Meldung der Ampel ist die alte, englische und sagt, was zu tun ist —
                // nicht die Ausnahmebotschaft der Engine. Drei Faelle, die der Betreiber
                // unterscheiden muss: falsches Passwort, falscher Schluessel, zu schnell.
                meldung = ex.Message.Contains("credentials wrong", StringComparison.OrdinalIgnoreCase)
                    ? "wrong username/password"
                    : ex.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                        ? "not authorized — check API key"
                        : ex.Message.Contains("429", StringComparison.Ordinal)
                            ? "rate-limited — try again later"
                            : $"Login failed: {ex.Message}";
                _logger.LogDebug("[Sub Scribe-Q] {Source} status: login failed: {Msg}", Key, ex.Message);
            }

            lichter.Add(new SourceStatusLight(LoginLightKey, "SubDL Login (subdl.com)", licht, Configured: true, Message: meldung));
        }

        uhr.Stop();
        return new SourceStatusAnswer(
            Key,
            lichter,
            SearchLimit: zahlen?.SearchLimit ?? 0,
            SearchRemaining: zahlen?.SearchRemaining ?? -1,
            DownloadLimit: zahlen?.DownloadsLimit ?? 0,
            DownloadRemaining: zahlen?.DownloadsRemaining ?? -1,
            ResetAtUtc: zahlen?.ResetAtUtc is { } r
                ? new DateTimeOffset(DateTime.SpecifyKind(r.ToUniversalTime(), DateTimeKind.Utc))
                : null,
            LatencyMs: uhr.ElapsedMilliseconds);
    }
}
