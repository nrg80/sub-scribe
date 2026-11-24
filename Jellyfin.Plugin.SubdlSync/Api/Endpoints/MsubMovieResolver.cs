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
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Registry;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M507 (Etappe 2a): die Nummer des Films bei moviesubtitles.org — ermittelt, gespeichert,
/// wiederverwendet. Das Muster ist das von <see cref="TvstShowResolver"/>, weil die Frage dieselbe ist:
/// welche Nummer hat DIESES Medium bei der Gegenseite.
/// <para>
/// DIE REIHENFOLGE IST FEST UND KURZ:
/// <list type="number">
/// <item><description>
/// Steht <see cref="Data.FileEntity.MsubMovieId"/> am Medium, wird SIE benutzt — kein Netzzugriff zur
/// Suche, kein Vergleich. Die Nummer wurde bewiesen, als sie geschrieben wurde.
/// </description></item>
/// <item><description>
/// Sonst wird der TMDb-Titel mit dem TMDb-Jahr in der Suche der Seite gesucht und eng verglichen
/// (<see cref="MovieSubtitlesClient.FindMovieAsync"/> → <see cref="MovieSubtitlesClient.ResolveMovie"/>:
/// Titel UND Jahr, bei Mehrdeutigkeit nichts). Ein Treffer wird ins Medium geschrieben und benutzt.
/// </description></item>
/// <item><description>
/// Kein Jahr, kein Titel oder kein eindeutiger Treffer: EINE Logzeile, kein Wurf — die Quelle fällt für
/// dieses Medium aus, die anderen laufen weiter. Angelegt wird hier NIE: das Anlegen ist ein Feld des
/// Sende-Auftrags (F-M734, <see cref="MsubMovieLookup"/>), und der Ablauf liest und schreibt den
/// Bestand weiterhin selbst.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// F-M513 — DIE ANLAGE-REGEL BLEIBT, IHR ORT IST EIN ANDERER: eine Suchrunde beantwortet Auflösung UND
/// Doppeleintrag-Schutz (<see cref="MovieSubtitlesClient.CheckForAddAsync"/>), und nur ein leerer,
/// lesbarer Befund erlaubt das Anlegen. Sie steht seit F-M734 im Sende-Auftrag
/// (<see cref="MsubMovieLookup.FindOrCreateAsync"/>); jede neue Nummer geht in dieselbe Spalte wie die
/// Auflösung (<see cref="Data.FileEntity.MsubMovieId"/>).
/// </para>
/// </summary>
public sealed class MsubMovieResolver
{
    private readonly MovieSubtitlesClient _client;
    private readonly ILogger? _logger;

    /// <summary>
    /// F-M507: die Nummern, die DIESER Lauf schon ermittelt hat. Der Aufruf kommt je Sprache, und ein
    /// Film mit vier fehlenden Sprachen darf nicht viermal bei der Seite suchen — im Trockenlauf schon
    /// gar nicht, denn dort wird nichts gespeichert und die zweite Sprache fände den Store leer.
    /// Der Schlüssel ist das Medium (media hash), nicht der Titel: zwei Filme desselben Titels bleiben
    /// zwei Medien.
    /// </summary>
    private readonly Dictionary<string, MsubMovieResolution> _resolvedThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MsubMovieResolver"/> class.
    /// </summary>
    /// <param name="client">Der moviesubtitles.org-Client (Suche und Filmseite).</param>
    /// <param name="logger">Optionaler Logger — er nennt den Grund, wenn nichts geschrieben wurde.</param>
    public MsubMovieResolver(MovieSubtitlesClient client, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>F-M507: beginnt einen Lauf — die Merkliste gilt je Lauf, nicht je Prozess.</summary>
    public void BeginRun() => _resolvedThisRun.Clear();

    /// <summary>
    /// F-M507: die Filmnummer für dieses Medium — gespeichert, sonst gesucht (TMDb-Titel und TMDb-Jahr).
    /// <para>
    /// F-M513: DIESE FASSUNG LEGT NIE AN. Sie ist der Weg des DOWNLOAD-Pfades und der einzige, den ein
    /// Aufrufer nehmen darf, der nichts schreiben will.
    /// </para>
    /// </summary>
    /// <param name="registry">Der Store, aus dem die gespeicherte Nummer kommt und in den die neue geht.</param>
    /// <param name="mediaHash">Medium (OSHash); null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="movie">Der TMDb-Datensatz des Films; sein Titel und sein Jahr führen.</param>
    /// <param name="dryRun">True: nichts schreiben (der Trockenlauf hinterlässt keine Spur, F-M22).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile,
    /// wenn kein Hash bekannt ist.</param>
    /// <returns>Das Ergebnis; <see cref="MsubMovieResolution.Resolved"/> false heißt „Quelle überspringen".</returns>
    public Task<MsubMovieResolution> ResolveForMediaAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbMovieInfo movie,
        bool dryRun,
        CancellationToken ct,
        string? mediaPath = null)
        => ResolveCoreAsync(registry, mediaHash, movie, dryRun, ct, mediaPath);
    /// <summary>
    /// F-M507/F-M513: die Leiter in einer Fassung — gespeichert, gemerkt, gesucht, und (nur mit
    /// <paramref name="allowCreate"/>) angelegt.
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium (OSHash).</param>
    /// <param name="movie">Der TMDb-Datensatz.</param>
    /// <param name="dryRun">True: nichts schreiben, nichts anlegen.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei — die zweite Adresse der Medienzeile.</param>
    /// <returns>Das Ergebnis der Leiter.</returns>
    private async Task<MsubMovieResolution> ResolveCoreAsync(
        ContentHashRegistry registry,
        string? mediaHash,
        TmdbMovieInfo movie,
        bool dryRun,
        CancellationToken ct,
        string? mediaPath = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(movie);

        // (a) Die gespeicherte Nummer führt. Kein Netzzugriff zur Suche: sie wurde geprüft, als sie
        // geschrieben wurde, und eine erneute Suche könnte sie nur durch eine schlechtere Antwort ersetzen.
        // F-M517: ohne Medien-Hash wird dieselbe Zeile über ihren PFAD gelesen — sonst wäre eine über
        // den Pfad gespeicherte Nummer zwar da, aber für jeden folgenden Lauf unsichtbar.
        var cachedKey = !string.IsNullOrEmpty(mediaHash)
            ? mediaHash!
            : !string.IsNullOrWhiteSpace(mediaPath) ? "path:" + mediaPath : "title:" + movie.Name;
        var stored = !string.IsNullOrEmpty(mediaHash)
            ? registry.GetMedia(mediaHash!)?.MsubMovieId
            : registry.MsubMovieIdAtPath(mediaPath);
        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known) && known > 0)
        {
            return new MsubMovieResolution(known, MsubMovieStatus.Stored);
        }

        // (a2) Was DIESER Lauf schon ermittelt hat, wird nicht ein zweites Mal gesucht — der Store ist
        // dann leer, weil der Trockenlauf nichts schreibt (F-M22) oder weil kein Medium bekannt ist. Ein
        // Fehlschlag steht ebenso in der Merkliste: eine Seite, die den Film nicht führt, führt ihn
        // eine Sprache später auch nicht.
        lock (_resolvedThisRun)
        {
            if (_resolvedThisRun.TryGetValue(cachedKey, out var schonGefragt))
            {
                return schonGefragt;
            }
        }

        var title = (movie.Name ?? string.Empty).Trim();
        if (title.Length < 2)
        {
            Log("[moviesubtitles] {Media}: der TMDb-Datensatz nennt keinen brauchbaren Titel — die Quelle wird übersprungen.",
                mediaHash ?? "?");
            return new MsubMovieResolution(null, MsubMovieStatus.NoTitle);
        }

        if (movie.ReleaseYear is not > 0)
        {
            // Ohne Jahr ist der Namensvergleich der Seite nicht eng genug (`filter=dune` antwortet fünf
            // Filme), und ein geratener Treffer wäre eine Nummer, die im Store steht und einen anderen
            // Film meint.
            Log("[moviesubtitles] {Media}: TMDb nennt kein Jahr für '{Title}' — die Quelle wird übersprungen.",
                mediaHash ?? "?", title);
            return new MsubMovieResolution(null, MsubMovieStatus.NoYear);
        }

        var movieId = await _client.FindMovieAsync(title, movie.ReleaseYear, ct).ConfigureAwait(false);
        if (movieId is not > 0)
        {
            Log("[moviesubtitles] {Media}: '{Title}' ({Year}) steht nicht (oder nicht eindeutig) in der Suche der Seite — die Quelle wird übersprungen.",
                mediaHash ?? "?", title, movie.ReleaseYear.Value);
            return Remember(cachedKey, new MsubMovieResolution(null, MsubMovieStatus.NotFound));
        }

        if (dryRun)
        {
            // F-M22: der Trockenlauf hinterlässt keine Spur — er benutzt die Nummer, schreibt sie aber nicht.
            return Remember(cachedKey, new MsubMovieResolution(movieId.Value, MsubMovieStatus.WouldStore));
        }

        var wie = Persist(registry, mediaHash, mediaPath, movieId.Value);
        Log("[moviesubtitles] {Media}: '{Title}' ({Year}) ist Film {Id} — {Note}",
            mediaHash ?? "?", title, movie.ReleaseYear.Value, movieId.Value,
            Note(wie));
        return Remember(cachedKey, new MsubMovieResolution(movieId.Value, MsubMovieStatus.Found));

    }


    /// <summary>
    /// F-M507: merkt sich das Ergebnis dieses Laufs — auch ein Fehlschlag. Sonst fragte jede weitere
    /// fehlende Sprache dieselbe Frage noch einmal, und die Seite ist ein Nebenamt, kein Cache.
    /// </summary>
    /// <param name="key">Medium bzw. Titel.</param>
    /// <param name="resolution">Das Ergebnis.</param>
    /// <returns>Das Ergebnis, unverändert.</returns>
    private MsubMovieResolution Remember(string key, MsubMovieResolution resolution)
    {
        lock (_resolvedThisRun)
        {
            _resolvedThisRun[key] = resolution;
        }

        return resolution;
    }

    /// <summary>
    /// F-M507/F-M517: schreibt die Nummer ins Medium — nur dorthin, und nur wenn die Seite sie genannt
    /// hat.
    /// <para>
    /// F-M517: DER SCHREIBWEG IST DERSELBE, DER FUND EIN ANDERER. Ohne Medien-Hash sucht
    /// <see cref="ContentHashRegistry.SetMsubMovieId"/> die Zeile über ihren PFAD
    /// (<c>FileEntity.Path</c>) und schreibt dieselbe Spalte <c>MsubMovieId</c>. Vorher kehrte diese
    /// Methode bei fehlendem Hash stumm zurück, obwohl die Zeile existierte — gemessen am 16.11.2026
    /// 04:13 auf jf-test: `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist Film 13392 — nicht
    /// gespeichert: kein Medien-Hash bekannt`. Der Upload lief durch, die Nummer war weg.
    /// </para>
    /// </summary>
    /// <param name="registry">Der Store.</param>
    /// <param name="mediaHash">Medium; null heißt „kein Medium bekannt" — dann führt der Pfad.</param>
    /// <param name="mediaPath">F-M517: der Pfad der Mediendatei (zweite Adresse der Zeile).</param>
    /// <param name="movieId">Die gesehene Nummer.</param>
    /// <returns>Wie geschrieben wurde; <see cref="MediaNumberWrite.None"/> heißt „nicht gespeichert".</returns>
    private static MediaNumberWrite Persist(
        ContentHashRegistry registry, string? mediaHash, string? mediaPath, int movieId)
        => registry.SetMsubMovieId(mediaHash, mediaPath, movieId);

    /// <summary>
    /// F-M514/F-M517: was die Logzeile über das Speichern sagen darf — und was nicht.
    /// <para>
    /// Gemessen 16.11.2026 (jf-test 02:51): `[moviesubtitles] ?: 'Hotel Transylvania 2' (2015) ist
    /// Film 13392 — gespeichert.` Das `?` ist der Platzhalter für „kein Medien-Hash bekannt"
    /// (<c>Registry.GetMediaHash</c> kennt die eben geschriebene Datei noch nicht, es gibt keine
    /// OSHash-Zeile oder die Datei ist nicht lesbar) — und in diesem Fall schrieb der alte Weg
    /// nichts. Die Zeile behauptete trotzdem „gespeichert".
    /// </para>
    /// <para>
    /// F-M517: der Vermerk kommt jetzt aus dem tatsächlichen Schreib-Ausgang
    /// (<see cref="MediaNumberNote"/>): über den Medien-Hash, über den Pfad — oder ehrlich „nicht
    /// gespeichert" mit dem Grund. Ein Text, den beide Adressen (Film und Serie) benutzen.
    /// </para>
    /// <param name="write">Wie geschrieben wurde.</param>
    /// <returns>Der Vermerk für die Logzeile.</returns>
    private static string Note(MediaNumberWrite write) => MediaNumberNote.Of(write);

    private void Log(string template, params object?[] args)
    {
        if (_logger is null)
        {
            return;
        }

        _logger.LogInformation(template, args);
    }
}

/// <summary>
/// F-M507: die Ausgänge der Filmaulösung — je einer eine Entscheidung des Aufrufers.
/// </summary>
public static class MsubMovieStatus
{
    /// <summary>Die Nummer stand am Medium.</summary>
    public const string Stored = "stored";

    /// <summary>Der TMDb-Titel stand eindeutig in der Suche; die Nummer wurde gespeichert.</summary>
    public const string Found = "found";

    /// <summary>Der Trockenlauf hätte gespeichert — er hat es nicht getan.</summary>
    public const string WouldStore = "would-store";

    /// <summary>Kein eindeutiger Treffer in der Suche der Seite.</summary>
    public const string NotFound = "not-found";

    /// <summary>
    /// F-M513: der Titel steht auf der Seite, aber nicht mit dem gesuchten Jahr (oder mit unbekanntem
    /// Jahr, oder in mehr als einer Zeile). Es wurde NICHTS angelegt — das wäre das Doppeleintrag-Risiko.
    /// </summary>
    public const string ExistsOtherYear = "exists-other-year";







    /// <summary>F-M513: die Suche antwortete nicht lesbar — „gibt es nicht" ist nicht belegt, also nichts angelegt.</summary>
    public const string Unclear = "unclear";

    /// <summary>Der TMDb-Datensatz trug keinen brauchbaren Titel.</summary>
    public const string NoTitle = "no-title";

    /// <summary>TMDb nennt kein Jahr — ohne Jahr wird nicht geraten.</summary>
    public const string NoYear = "no-year";
}

/// <summary>
/// F-M507: was die Filmaulösung ergeben hat.
/// </summary>
/// <param name="MovieId">Die Nummer des Films, oder null.</param>
/// <param name="Status">Einer der Werte aus <see cref="MsubMovieStatus"/>.</param>
/// <param name="FoundYear">F-M513: das Jahr, unter dem der Titel schon steht (0 = die Seite nennt
/// keines) — nur bei <see cref="MsubMovieStatus.ExistsOtherYear"/> gesetzt.</param>
/// <param name="AddMovieBytes">F-M513: die Groesse der Anlege-Nutzlast, die im Trockenlauf gebaut wurde
/// (0, wenn nichts gebaut wurde) — sie ist die des Ernstfalls.</param>
/// <param name="Answer">F-M513: der Text der Seite, wenn einer vorliegt (Ablehnung beim Anlegen,
/// unlesbare Antwort) — durchgereicht, nicht gedeutet.</param>
public sealed record MsubMovieResolution(
    int? MovieId, string Status, int FoundYear = 0, string? Answer = null)
{
    /// <summary>Gets a value indicating whether eine Nummer vorliegt, mit der der Abruf arbeiten kann.</summary>
    public bool Resolved => MovieId is > 0;
}
