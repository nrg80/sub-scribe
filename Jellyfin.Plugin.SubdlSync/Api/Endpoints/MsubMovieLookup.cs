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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M734: die SEITE dieser Frage — „gibt es den Film, und darf ich ihn anlegen?" — ohne unseren Bestand.
/// <para>
/// Bis hierher steckte genau diese Logik in <see cref="MsubMovieResolver"/>, zusammen mit dem Lesen und
/// Schreiben der gespeicherten Nummer. Damit hing sie an zwei Welten. Hier steht nur die Seite: fragen,
/// doppelte vermeiden, anlegen, nachsehen — und zurück kommt ein neutraler Zustand samt Kennung
/// (<see cref="SourceTitleAnswer"/>). Die Nummer in den Bestand zu schreiben bleibt Sache des Ablaufs
/// (Grenzregel a: die Engine kennt unseren Store nicht).
/// </para>
/// <para>
/// Die Regeln sind die von F-M513/F-M515, unverändert: eine Suchrunde beantwortet Auflösung UND
/// Doppeleintrag-Schutz; „die Seite führt den Titel mit einem anderen Jahr" ist ein inhaltliches Nein
/// (nichts anlegen); „nicht lesbar" ist kein Nein (nichts anlegen, nichts entscheiden); und ein 2xx ohne
/// belegten Marker ist keine Anlage.
/// </para>
/// </summary>
public sealed class MsubMovieLookup
{
    private readonly MovieSubtitlesClient _client;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MsubMovieLookup"/> class.
    /// </summary>
    /// <param name="client">Der Client der Filmseite.</param>
    /// <param name="logger">Optionaler Logger.</param>
    public MsubMovieLookup(MovieSubtitlesClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>
    /// F-M734: sucht den Film und legt ihn an, wenn er fehlt.
    /// </summary>
    /// <param name="title">Der Titel aus dem TMDb-Datensatz.</param>
    /// <param name="year">Das Jahr aus dem TMDb-Datensatz.</param>
    /// <param name="dryRun">True: nichts anlegen — dann gibt es auch kein Urteil.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Was geschah, samt Kennung, wenn die Seite eine nennt.</returns>
    public async Task<SourceTitleAnswer> FindOrCreateAsync(
        string? title, int? year, bool dryRun, CancellationToken ct)
    {
        string name = (title ?? string.Empty).Trim();
        if (name.Length < 2)
        {
            return new SourceTitleAnswer(SourceTitleState.NotAskable, Reason: "the record names no usable title");
        }

        // Ohne Jahr ist der Namensvergleich der Seite nicht eng genug (`filter=dune` antwortet fünf
        // Filme), und ein geratener Treffer wäre eine Nummer, die einen anderen Film meint (F-M508).
        if (year is not > 0)
        {
            return new SourceTitleAnswer(SourceTitleState.NotAskable, Reason: "the record names no year");
        }

        MovieSubtitlesAddCheck check;
        try
        {
            // EINE Runde: Auflösung und Doppeleintrag-Schutz (F-M513).
            check = await _client.CheckForAddAsync(name, year.Value, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("[moviesubtitles] title check for '{Title}' failed: {Msg}", name, ex.Message);
            return new SourceTitleAnswer(SourceTitleState.Unclear, Reason: ex.Message);
        }

        if (check.Status == MovieSubtitlesAddStatus.Take)
        {
            return new SourceTitleAnswer(SourceTitleState.Ready, Id(check.MovieId));
        }

        if (check.Status == MovieSubtitlesAddStatus.Refuse)
        {
            string jahr = check.FoundYear > 0
                ? check.FoundYear.ToString(CultureInfo.InvariantCulture)
                : "none";
            return new SourceTitleAnswer(
                SourceTitleState.NotFound,
                Reason: $"the page carries the title with another year (found {jahr}, {check.Hits} hit(s))");
        }

        if (check.Status == MovieSubtitlesAddStatus.Unclear)
        {
            // Keine lesbare Antwort: „gibt es nicht" ist damit NICHT belegt — also nicht anlegen.
            return new SourceTitleAnswer(SourceTitleState.Unclear, Reason: "the search answered nothing readable");
        }

        // ---- kein Treffer, und die Suche war lesbar: anlegen --------------------------------------
        MovieSubtitlesAddMovie nutzlast;
        try
        {
            nutzlast = MovieSubtitlesClient.BuildAddMovie(name, year.Value);
        }
        catch (ArgumentException ex)
        {
            return new SourceTitleAnswer(SourceTitleState.NotAskable, Reason: ex.Message);
        }

        if (dryRun)
        {
            // F-M22: der Trockenlauf hinterlässt keine Spur — und er entscheidet auch nichts.
            return new SourceTitleAnswer(
                SourceTitleState.NotAskable,
                Reason: $"dry run: the form body for '{name}' ({year.Value}) was built, nothing sent");
        }

        MovieSubtitlesAddMovieResult angelegt;
        try
        {
            angelegt = await _client.AddMovieAsync(nutzlast, dryRun: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("[moviesubtitles] creating '{Title}' failed: {Msg}", name, ex.Message);
            return new SourceTitleAnswer(SourceTitleState.Unclear, Reason: ex.Message);
        }

        // F-M515: „existiert schon" und die Nachsuche nennt den Film → diese Nummer nehmen.
        if (angelegt.Outcome == MovieSubtitlesAddOutcome.Exists)
        {
            return new SourceTitleAnswer(
                SourceTitleState.Ready,
                Id(angelegt.MovieId),
                "the page said it exists already — the id comes from the follow-up search");
        }

        // F-M515 (live gemessen): „existiert schon", die Suche nennt aber keine Id.
        if (angelegt.Outcome == MovieSubtitlesAddOutcome.ExistsUnknownId)
        {
            return new SourceTitleAnswer(SourceTitleState.ExistsWithoutId, Reason: angelegt.Response ?? angelegt.Reason);
        }

        if (angelegt.Outcome == MovieSubtitlesAddOutcome.Created)
        {
            return new SourceTitleAnswer(SourceTitleState.Created, Id(angelegt.MovieId));
        }

        // ANGELEGT, ID NICHT LESBAR: der POST wurde angenommen, die Nachsuche nennt den Film nicht.
        if (angelegt.Outcome == MovieSubtitlesAddOutcome.CreatedIdUnknown)
        {
            return new SourceTitleAnswer(SourceTitleState.CreatedWithoutId, Reason: angelegt.Response ?? angelegt.Reason);
        }

        // 2xx ohne Marker ist KEINE Anlage (F-M515); kein 2xx ist eine Ablehnung. Beides ist ein Nein
        // dieser Adresse für dieses Medium.
        return new SourceTitleAnswer(
            SourceTitleState.NotFound,
            Reason: angelegt.Response ?? angelegt.Reason ?? angelegt.Outcome);
    }

    /// <summary>F-M734: die Kennung als Zeichenkette — die Grenze führt keine fremde Kennungsform.</summary>
    /// <param name="id">Die Nummer der Seite.</param>
    /// <returns>Die Kennung, oder null.</returns>
    private static string? Id(int? id) => id is > 0 ? id.Value.ToString(CultureInfo.InvariantCulture) : null;
}
