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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Language;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources.Subdl;

/// <summary>
/// F-M729: die Konto-Pflege von SubDL hinter dem zweiten, schmalen Vertrag — dünner Adapter auf
/// <see cref="ScribeApiClient"/>, wie die vier Aufträge auch.
/// <para>
/// Der Zugang kommt aus der EIGENEN Datei dieser Quelle (<c>sources/subdl.json</c>: <c>apiKey</c>,
/// <c>account</c>, <c>password</c>, F-M683/F-M694). Bis heute baute der Nachlauf seinen Client aus der
/// Plugin-Konfiguration und hielt die Werte damit an zwei Orten — hier steht der eine Ort.
/// </para>
/// <para>
/// Die Grenzregeln gelten: der HttpClient wird GEREICHT (kein eigener, kein eigener Handler), es gibt
/// keinen Zugriff auf Store, Pipeline oder unsere Kennungen, und nach außen gehen nur neutrale Daten.
/// Angemeldet wird beim ersten Gebrauch, nicht im Konstruktor — die Registry wird auch für Läufe
/// gebaut, die den Nachlauf nie erreichen.
/// </para>
/// </summary>
public sealed class SubdlMaintenance : ISourceMaintenance
{
    private readonly ScribeApiClient _client;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Task<bool>? _login;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlMaintenance"/> class.
    /// </summary>
    /// <param name="context">Was das Plugin reicht — hier nur HttpClient, Logger und Zeitgeber.</param>
    /// <param name="settings">
    /// Die Einstellungen aus der eigenen Datei dieser Quelle. Fehlt sie, gilt der Zugang des gereichten
    /// Clients (so verhält sich die Quelle wie vor diesem Schritt).
    /// </param>
    public SubdlMaintenance(SourceEngineContext context, SourceSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;

        // F-M682 (Grenzregel b): der Client ist der des Plugins. Der gereichte SubDL-Client wird
        // bevorzugt; fehlt er, entsteht einer über DENSELBEN HttpClient — nie über einen neuen.
        _client = context.Subdl ?? new ScribeApiClient(context.Http);
        Settings = settings ?? SourceSettings.Empty(SubdlEngine.SourceKey, SubdlEngine.Declared);

        // F-M729: die Datei antwortet zuerst. Steht dort ein Schlüssel, gilt er; sonst bleibt stehen,
        // was der Client schon trägt.
        if (Settings.IsConfigured && Settings.Secret(SubdlEngine.FieldApiKey) is { Length: > 0 } key)
        {
            _client.ApiKey = key;
        }

        if (Settings.Text(SubdlEngine.FieldAccount) is { Length: > 0 } account)
        {
            _client.Username = account;
        }

        if (Settings.Secret(SubdlEngine.FieldPassword) is { Length: > 0 } password)
        {
            _client.Password = password;
        }
    }

    /// <summary>Gets der Quellenschlüssel — derselbe Wert, den der Bestand führt.</summary>
    public string Key => SubdlEngine.SourceKey;

    /// <summary>Gets die Einstellungen dieser Quelle, wie sie gelesen wurden.</summary>
    public SourceSettings Settings { get; }

    /// <inheritdoc />
    public async Task<SourceSubmissionAnswer> ListAsync(SourceSubmissionQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!Settings.IsConfigured)
        {
            return SourceSubmissionAnswer.Failed(
                SourceFault.AuthFailed,
                Settings.Problem ?? "the source settings file is not readable");
        }

        if (!await EnsureLoginAsync(ct).ConfigureAwait(false))
        {
            return SourceSubmissionAnswer.Failed(SourceFault.AuthFailed, "login failed");
        }

        int pages = 0;
        try
        {
            List<OwnSubtitleEntry>? entries;
            if (query.Published)
            {
                // F-M437: bis zum Wasserstand ablaufen, dann aufhören — die Liste ist die jüngste zuerst.
                var watermark = query.NewerThan?.ToUniversalTime() ?? DateTimeOffset.MinValue;
                entries = await _client.ListMyPublishedSubtitlesAsync(
                        ct,
                        maxPages: query.MaxPages,
                        shouldStop: e => watermark > DateTimeOffset.MinValue
                                         && e.CreatedAt != default
                                         && e.CreatedAt.ToUniversalTime() < watermark.UtcDateTime,
                        pageRead: n => pages = n)
                    .ConfigureAwait(false);
            }
            else
            {
                entries = await _client.ListMySubtitlesAsync(ct, maxPages: query.MaxPages)
                    .ConfigureAwait(false);
                pages = entries == null ? 0 : 1;
            }

            if (entries == null)
            {
                return SourceSubmissionAnswer.Failed(SourceFault.Transient, "the source did not answer");
            }

            var submissions = entries
                .Select(e => new SourceSubmission(
                    Handle: e.UploadId.ToString(CultureInfo.InvariantCulture),
                    EntryHandle: e.EntryId > 0 ? e.EntryId.ToString(CultureInfo.InvariantCulture) : null,
                    Language: Iso(e.Language),
                    Name: e.Name,
                    CreatedAtUtc: e.CreatedAt == default
                        ? DateTimeOffset.MinValue
                        : new DateTimeOffset(DateTime.SpecifyKind(e.CreatedAt.ToUniversalTime(), DateTimeKind.Utc)),
                    State: StateOf(e, query.Published),
                    Reason: string.IsNullOrEmpty(e.RejectReason) ? null : e.RejectReason,
                    ContentHash: e.ContentHash,
                    ImdbId: e.ImdbId,
                    TmdbId: e.TmdbId?.ToString(CultureInfo.InvariantCulture),
                    ForeignId: string.IsNullOrEmpty(e.NId) ? null : e.NId,
                    Season: e.Season,
                    Episode: e.Episode))
                .ToList();

            return new SourceSubmissionAnswer(submissions, SourceFault.None, null, pages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Sub Scribe-Q] {Source} list failed: {Msg}", Key, ex.Message);
            return SourceSubmissionAnswer.Failed(
                _client.Edge429NoHeaders || _client.RateLimitResetUtc.HasValue
                    ? SourceFault.RateLimited
                    : SourceFault.Transient,
                ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<SourceWithdrawAnswer> WithdrawAsync(string handle, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(handle)
            || !int.TryParse(handle, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            || id <= 0)
        {
            return SourceWithdrawAnswer.Failed($"not a handle of this source: {handle}");
        }

        if (!Settings.IsConfigured)
        {
            return SourceWithdrawAnswer.Failed(
                Settings.Problem ?? "the source settings file is not readable");
        }

        if (!await EnsureLoginAsync(ct).ConfigureAwait(false))
        {
            return SourceWithdrawAnswer.Failed("login failed");
        }

        try
        {
            bool gone = await _client.DeleteMySubtitleAsync(id, ct).ConfigureAwait(false);
            return gone
                ? SourceWithdrawAnswer.Accepted()
                : SourceWithdrawAnswer.Gone("the source did not confirm the withdrawal");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Sub Scribe-Q] {Source} withdraw {Handle} failed: {Msg}", Key, handle, ex.Message);
            return SourceWithdrawAnswer.Failed(
                ex.Message,
                busy: _client.Edge429NoHeaders || _client.RateLimitResetUtc.HasValue);
        }
    }

    /// <summary>
    /// Meldet den gereichten Client beim ersten Gebrauch an — einmal je Instanz.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True, wenn eine Anmeldung steht.</returns>
    private Task<bool> EnsureLoginAsync(CancellationToken ct)
    {
        // Ein Versuch je Instanz: der Nachlauf läuft einmal, und ein zweiter Anlauf im selben Objekt
        // braucht keinen neuen Login. Ein gemerktes Fehlschlagen ist ebenfalls ein Ergebnis — die
        // nächste Instanz fragt neu.
        lock (_gate)
        {
            return _login ??= LoginNowAsync(ct);
        }
    }

    /// <summary>Meldet den Client an — einmal, mit dem Zugang aus der eigenen Datei.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True, wenn eine Anmeldung steht.</returns>
    private async Task<bool> LoginNowAsync(CancellationToken ct)
    {
        try
        {
            await _client.LoginAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Sub Scribe-Q] {Source} login failed: {Msg}", Key, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// F-M682: die Sprache geht in UNSERER Schreibweise über die Grenze. Die Liste der Seite trägt
    /// einen Namen oder einen Code — beides wird hier auf ISO-639-2 gebracht, damit der Aufrufer nicht
    /// zwei Schreibweisen kennen muss.
    /// </summary>
    /// <param name="language">Der Wert der Quelle.</param>
    /// <returns>ISO-639-2, oder der Wert unverändert, wenn er unbekannt ist.</returns>
    private static string Iso(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        string value = language.Trim();
        return LanguageMapper.ToIso6392(value)
               ?? LanguageMapper.Canonical(value)
               ?? value;
    }

    /// <summary>
    /// Der Zustand einer Zeile. Die offene Liste führt den Zustand im Feld, die veröffentlichte Liste
    /// ist per Definition veröffentlicht — dort zählt die Kennung des Eintrags, nicht ein Wort.
    /// </summary>
    /// <param name="entry">Die Zeile der Quelle.</param>
    /// <param name="published">True, wenn die Zeile aus der veröffentlichten Liste kommt.</param>
    /// <returns>Der Zustand.</returns>
    private static SubmissionState StateOf(OwnSubtitleEntry entry, bool published)
    {
        if (published || entry.EntryId > 0)
        {
            return SubmissionState.Accepted;
        }

        return entry.Status.Contains("reject", StringComparison.OrdinalIgnoreCase)
            ? SubmissionState.Rejected
            : SubmissionState.Pending;
    }
}
