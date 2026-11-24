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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M734: die SEITE der Serien-Frage — „steht die Serie da, und darf ich sie anlegen?" — ohne unseren
/// Bestand. Das Gegenstück zu <see cref="MsubMovieLookup"/>, aus <see cref="TvstShowResolver"/> gelöst.
/// <para>
/// Die Regel ist die von F-M501/F-M516, unverändert: erst die Serienliste (exakt verglichen), dann —
/// nur im Ernstfall und nur einmal je Lauf — anlegen, danach NACHSEHEN statt der Anlege-Antwort zu
/// glauben. Die Nummer geht zurück an den Ablauf, der sie in den Bestand schreibt.
/// </para>
/// </summary>
public sealed class TvstShowLookup
{
    private readonly TvSubtitlesClient _client;
    private readonly ILogger? _logger;

    /// <summary>F-M516: je Lauf wird höchstens EINMAL angelegt — der Schlüssel ist das Medium.</summary>
    private readonly ConcurrentDictionary<string, byte> _createAttempted = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TvstShowLookup"/> class.
    /// </summary>
    /// <param name="client">Der Client der Serienseite.</param>
    /// <param name="logger">Optionaler Logger.</param>
    public TvstShowLookup(TvSubtitlesClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>
    /// F-M734: sucht die Serie und legt sie an, wenn sie fehlt.
    /// </summary>
    /// <param name="title">Der Name aus dem TMDb-Datensatz.</param>
    /// <param name="firstAirYear">Das Erstausstrahlungsjahr (die Seite verlangt eines).</param>
    /// <param name="seasons">Die Staffelzahl (die Seite lässt 1..22 zu).</param>
    /// <param name="memoKey">Der Merkschlüssel des Laufs (das Medium).</param>
    /// <param name="dryRun">True: nichts anlegen — dann gibt es auch kein Urteil.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Was geschah, samt Kennung, wenn die Seite eine nennt.</returns>
    public async Task<SourceTitleAnswer> FindOrCreateAsync(
        string? title, int? firstAirYear, int? seasons, string? memoKey, bool dryRun, CancellationToken ct)
    {
        string name = (title ?? string.Empty).Trim();
        if (name.Length < 2)
        {
            return new SourceTitleAnswer(SourceTitleState.NotAskable, Reason: "the record names no usable title");
        }

        // (b) Exakt in der Serienliste. Eine leere oder nicht lesbare Liste ist KEIN „es gibt die Serie
        // nicht" — sie wird einmal nachgefragt, und erst wenn sie wieder unklar ist, ist der Lauf blind.
        var shows = await ShowsOrNullAsync(ct).ConfigureAwait(false);
        if (shows is null)
        {
            return new SourceTitleAnswer(SourceTitleState.Unclear, Reason: "the show list was not readable (twice)");
        }

        var exact = TvSubtitlesClient.ResolveShow(shows, name);
        if (exact is > 0)
        {
            return new SourceTitleAnswer(SourceTitleState.Ready, Id(exact));
        }

        // (c) Anlegen — im Trockenlauf nie.
        if (dryRun)
        {
            return new SourceTitleAnswer(
                SourceTitleState.NotAskable,
                Reason: $"dry run: '{name}' is not on the page, nothing created");
        }

        string schluessel = string.IsNullOrEmpty(memoKey) ? "title:" + name : memoKey!;
        if (!_createAttempted.TryAdd(schluessel, 0))
        {
            return new SourceTitleAnswer(
                SourceTitleState.Unclear, Reason: "creating this show was already tried in this run");
        }

        if (firstAirYear is not int jahr || jahr < 1900 || jahr > 2100)
        {
            return new SourceTitleAnswer(
                SourceTitleState.NotAskable, Reason: "the page asks for a first-air year and there is none");
        }

        var staffeln = Math.Clamp(seasons ?? 1, 1, TvSubtitlesClient.MaxSeason);

        // F-M516: gesendet wird der Rumpf des echten Formulars; die Antwort wird KLASSIFIZIERT, nicht
        // geglaubt — die Nummer wird nachgesehen.
        string outcome;
        int? status = null;
        string? seitenText = null;
        try
        {
            var answer = await _client
                .AddShowAsync(TvSubtitlesClient.BuildAddShow(name, jahr, staffeln), dryRun: false, ct)
                .ConfigureAwait(false);
            outcome = answer.Outcome;
            status = answer.Status;
            seitenText = answer.Response;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Ein Abbruch nach dem Absenden ist KEIN Beweis, dass nichts angelegt wurde.
            outcome = TvstAddShowOutcome.Unclear;
            seitenText = ex.Message;
        }

        var gefunden = await FindShowIdAsync(name, ct).ConfigureAwait(false);
        var zustand = TvstShowResolver.SettleStatus(outcome, gefunden);

        if (gefunden is > 0)
        {
            // F-M516: bestätigt angelegt ODER schon dagewesen — die Nummer ist in beiden Fällen die
            // Antwort, nur der Zustand unterscheidet sich.
            return zustand == TvstShowStatus.Created
                ? new SourceTitleAnswer(SourceTitleState.Created, Id(gefunden))
                : new SourceTitleAnswer(
                    SourceTitleState.Ready,
                    Id(gefunden),
                    $"the page did not confirm the creation (\"{outcome}\"), but the show is there");
        }

        if (zustand == TvstShowStatus.CreatedIdUnknown)
        {
            return new SourceTitleAnswer(
                SourceTitleState.CreatedWithoutId,
                Reason: $"created without a readable id (HTTP {status?.ToString(CultureInfo.InvariantCulture) ?? "?"})");
        }

        if (zustand == TvstShowStatus.Refused)
        {
            // Ein inhaltliches Nein der Seite — kein zweiter Versuch.
            return new SourceTitleAnswer(
                SourceTitleState.NotFound,
                Reason: $"the page refused the creation (HTTP {status?.ToString(CultureInfo.InvariantCulture) ?? "?"}): {seitenText}");
        }

        return new SourceTitleAnswer(
            SourceTitleState.Unclear,
            Reason: "the page did not confirm the creation and the show is not in the list");
    }

    /// <summary>
    /// F-M501: die Serienliste der Seite — einmal, bei unklarer Antwort ein zweites Mal.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Liste, oder null wenn sie auch beim zweiten Versuch unklar blieb.</returns>
    private async Task<IReadOnlyDictionary<int, string>?> ShowsOrNullAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var shows = await _client.ShowsAsync(ct).ConfigureAwait(false);
                if (shows.Count > 0)
                {
                    return shows;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    "[tvsubtitles] show list not readable (attempt {Attempt}): {Error}", attempt + 1, ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// F-M501: nachsehen, welche Nummer die Serie jetzt trägt — über die Suche, dann über die Liste.
    /// </summary>
    /// <param name="title">Der Name, den die Anlage trug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Nummer, oder null wenn keine Antwort sie zeigt.</returns>
    private async Task<int?> FindShowIdAsync(string title, CancellationToken ct)
    {
        try
        {
            var hits = await _client.SearchShowsAsync(title, ct).ConfigureAwait(false);
            if (hits.Count > 0)
            {
                var id = TvSubtitlesClient.ResolveShow(hits, title);
                if (id is > 0)
                {
                    return id;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[tvsubtitles] show search not readable: {Error}", ex.Message);
        }

        var shows = await ShowsOrNullAsync(ct).ConfigureAwait(false);
        return shows is null ? null : TvSubtitlesClient.ResolveShow(shows, title);
    }

    /// <summary>F-M734: die Kennung als Zeichenkette.</summary>
    /// <param name="id">Die Nummer der Seite.</param>
    /// <returns>Die Kennung, oder null.</returns>
    private static string? Id(int? id) => id is > 0 ? id.Value.ToString(CultureInfo.InvariantCulture) : null;
}
