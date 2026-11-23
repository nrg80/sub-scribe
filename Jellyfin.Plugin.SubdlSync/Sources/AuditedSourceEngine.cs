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
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M684: die Hülle, die JE AUFTRAG spricht. Sie liegt zwischen Ablauf und Engine und tut dreierlei,
/// ohne die Antwort zu verändern: sie schreibt eine Debug-Zeile je Auftrag (Quelle, Griff, Sprache,
/// HI, Dauer, Ergebnis), sie schreibt eine sichtbare Zeile je Fehlerfall, und sie zählt den Auftrag in
/// <see cref="SourceTraffic"/>.
/// <para>
/// WARUM EINE HÜLLE UND NICHT VIER ZEILEN IN JEDER ENGINE. Drei Engines, vier Aufträge: in den Engines
/// wären es zwölf Stellen, die dieselbe Zeile bauen — und die dreizehnte, die jemand später hinzufügt,
/// wäre die, an der es fehlt. Hier steht die Zeile EINMAL, jede Engine wird beim Bau der Registry
/// hindurchgereicht, und der Ablauf merkt nichts davon: die Hülle gibt Fähigkeiten und Felder unverändert
/// weiter.
/// </para>
/// <para>
/// KEIN GEHEIMWERT: die Zeilen nennen Quellenschlüssel, Griff, Sprache, HI, Dauer, Größe, Ergebnis —
/// niemals Bytes, niemals einen Schlüssel, niemals die Adresse mit Anfrage. Der Grund einer Abweisung
/// kommt aus der Grenze (<see cref="SourceFaultInfo.Reason"/>), und der ist der Klartext der Quelle, nicht
/// ihr Inhalt.
/// </para>
/// </summary>
public sealed class AuditedSourceEngine : ISourceEngine
{
    private readonly ISourceEngine _inner;
    private readonly ILogger _logger;
    private readonly Configuration.LogLevelMode _mode;

    /// <summary>Initializes a new instance of the <see cref="AuditedSourceEngine"/> class.</summary>
    /// <param name="inner">Die Engine, die die Arbeit tut.</param>
    /// <param name="logger">Der Logger des Laufs.</param>
    /// <param name="mode">
    /// F-M684: der LogMode dieses Laufs. Ohne ihn löste die Hülle den Schalter erst beim Schreiben auf —
    /// und eine Prüfung ohne laufende Plugin-Instanz sähe dann nie eine Auftragszeile.
    /// </param>
    public AuditedSourceEngine(ISourceEngine inner, ILogger logger, Configuration.LogLevelMode mode = Configuration.LogLevelMode.Normal)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _mode = mode;
    }

    /// <inheritdoc />
    public SourceCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc />
    public IReadOnlyList<SourceField> Fields => _inner.Fields;

    /// <summary>Gets die umhüllte Engine — für Prüfungen und für Aufrufer, die sie ausdrücklich brauchen.</summary>
    public ISourceEngine Inner => _inner;

    /// <inheritdoc />
    public async Task<SourceSearchAnswer> SearchAsync(SourceSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = Key;
        string was = Beschreibung(request);

        SourceSearchAnswer antwort;
        long ms;
        using (SourceTraffic.EngineScope(key))
        {
            var uhr = Stopwatch.StartNew();
            try
            {
                antwort = await _inner.SearchAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                uhr.Stop();
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} Suche — die Quelle hat nicht geantwortet: {Reason} (Netz)",
                    key, Short(ex.Message));
                SourceTraffic.NoteAnswer(key, SourceFaultInfo.Transient(ex.Message));
                throw;
            }

            ms = uhr.ElapsedMilliseconds;
        }

        SourceTraffic.NoteAnswer(key, antwort.Fault);
        LogUtil.Debug(
            _mode,
            _logger,
            "[Sub Scribe-Q] {Source} Suche — {Was} → {Count} Kandidat(en) in {Ms} ms",
            key, was, antwort.Candidates.Count, ms);

        if (antwort.Fault is { } fehler)
        {
            Fault(key, "Suche", fehler);
        }

        return antwort;
    }

    /// <inheritdoc />
    public async Task<SourceFetchAnswer> FetchAsync(SourceFetchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = Key;

        SourceFetchAnswer antwort;
        long ms;
        using (SourceTraffic.EngineScope(key))
        {
            var uhr = Stopwatch.StartNew();
            try
            {
                antwort = await _inner.FetchAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                uhr.Stop();
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} Hole — die Quelle hat nicht geantwortet: {Reason} (Netz)",
                    key, Short(ex.Message));
                SourceTraffic.NoteAnswer(key, SourceFaultInfo.Transient(ex.Message));
                throw;
            }

            ms = uhr.ElapsedMilliseconds;
        }

        SourceTraffic.NoteAnswer(key, antwort.Fault);
        LogUtil.Debug(
            _mode,
            _logger,
            "[Sub Scribe-Q] {Source} Hole — Griff {Handle}, Folge {Episode}, Mitglied {Member} → {Bytes} Byte in {Ms} ms (Hash {Hash})",
            key,
            Short(request.Handle),
            request.Episode?.ToString(CultureInfo.InvariantCulture) ?? "-",
            string.IsNullOrWhiteSpace(request.MemberName) ? "-" : Short(request.MemberName),
            antwort.Bytes.Length,
            ms,
            antwort.ContentHash.Length >= 8 ? antwort.ContentHash[..8] : "-");

        if (antwort.Fault is { } fehler)
        {
            Fault(key, "Hole", fehler);
        }

        return antwort;
    }

    /// <inheritdoc />
    public async Task<SourceSendAnswer> SendAsync(SourceSendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = Key;

        // „Quelle kann nicht hochladen" ist ein Fehlerfall, den die Fähigkeit selbst ansagt — er wird
        // gemeldet, bevor die Engine überhaupt gefragt wird (die Antwort bleibt ihre).
        if (!Capabilities.CanUpload)
        {
            _logger.LogWarning(
                "[Sub Scribe-Q] {Source} Sende — diese Quelle kann nicht hochladen (CanUpload=false); der Auftrag wird nicht gesendet.",
                key);
        }

        SourceSendAnswer antwort;
        long ms;
        using (SourceTraffic.EngineScope(key))
        {
            var uhr = Stopwatch.StartNew();
            try
            {
                antwort = await _inner.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                uhr.Stop();
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} Sende — die Quelle hat nicht geantwortet: {Reason} (Netz)",
                    key, Short(ex.Message));
                SourceTraffic.NoteAnswer(key, SourceFaultInfo.Transient(ex.Message));
                throw;
            }

            ms = uhr.ElapsedMilliseconds;
        }

        SourceTraffic.NoteSendAnswer(key, antwort);
        LogUtil.Debug(
            _mode,
            _logger,
            "[Sub Scribe-Q] {Source} Sende — {Umfang}, Sprache {Language}, HI={Hi}, {Bytes} Byte, {Target} → {Outcome} in {Ms} ms",
            key,
            request.AsPack
                ? "Behälter mit " + request.Members.Count.ToString(CultureInfo.InvariantCulture) + " Mitglied(ern)"
                : "einzelne Datei " + Short(request.FileName),
            string.IsNullOrWhiteSpace(request.Language) ? "-" : request.Language,
            request.HearingImpaired ? "ja" : "nein",
            request.Bytes.Length,
            Ziel(request.Target),
            Ausgang(antwort.Outcome, antwort.RetryAfterSeconds),
            ms);

        Outcome(key, "Sende", antwort.Outcome, antwort.Reason, antwort.RetryAfterSeconds);

        return antwort;
    }

    /// <inheritdoc />
    public async Task<SourceReportAnswer> ReportAsync(SourceReportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string key = Key;

        if (!Capabilities.CanReport)
        {
            _logger.LogWarning(
                "[Sub Scribe-Q] {Source} Melde — diese Quelle hat keinen Meldeweg (CanReport=false); der Auftrag wird nicht gesendet.",
                key);
        }

        SourceReportAnswer antwort;
        long ms;
        using (SourceTraffic.EngineScope(key))
        {
            var uhr = Stopwatch.StartNew();
            try
            {
                antwort = await _inner.ReportAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                uhr.Stop();
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} Melde — die Quelle hat nicht geantwortet: {Reason} (Netz)",
                    key, Short(ex.Message));
                SourceTraffic.NoteAnswer(key, SourceFaultInfo.Transient(ex.Message));
                throw;
            }

            ms = uhr.ElapsedMilliseconds;
        }

        SourceTraffic.NoteReportAnswer(key, antwort);
        LogUtil.Debug(
            _mode,
            _logger,
            "[Sub Scribe-Q] {Source} Melde — Eintrag {Remote}, Art {Kind} → {Outcome} in {Ms} ms",
            key,
            Short(request.RemoteId),
            request.Kind.ToString(),
            Ausgang(antwort.Outcome, antwort.RetryAfterSeconds),
            ms);

        Outcome(key, "Melde", antwort.Outcome, antwort.Reason, antwort.RetryAfterSeconds);

        return antwort;
    }

    private string Key => _inner.Capabilities.Key;

    /// <summary>
    /// F-M685: die sichtbare Zeile eines Ausgangs. „Angenommen" braucht keine — die Debug-Zeile sagt es
    /// schon; „abgelehnt", „beschäftigt" (MIT Wartezeit) und „fehlgeschlagen" stehen sichtbar auf WRN,
    /// weil sie den nächsten Schritt bestimmen.
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="auftrag">Der Auftrag (Sende, Melde).</param>
    /// <param name="ausgang">Der eine Ausgang.</param>
    /// <param name="reason">Der Klartext der Quelle, wenn sie einen nennt.</param>
    /// <param name="retryAfterSeconds">Die Wartezeit bei „beschäftigt", wenn die Quelle eine nennt.</param>
    private void Outcome(string key, string auftrag, SourceOutcome ausgang, string? reason, int? retryAfterSeconds)
    {
        string grund = Short(reason);
        switch (ausgang)
        {
            case SourceOutcome.Busy:
                // Mit Zeitpunkt: die Quelle nennt die Wartezeit, und der Lauf soll sagen, ab wann es
                // weitergeht — ein „429" ohne Zeit ist eine Zeile, mit der niemand planen kann.
                DateTime ab = DateTime.UtcNow.AddSeconds(retryAfterSeconds ?? 0);
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — beschäftigt: {Seconds} s warten, nächster Versuch ab {From:HH:mm:ss}Z ({Reason})",
                    key, auftrag,
                    retryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "?",
                    ab,
                    grund);
                break;

            case SourceOutcome.Rejected:
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — abgelehnt: Feld abgewiesen, die Quelle hat mit Grund abgelehnt ({Reason})",
                    key, auftrag, grund);
                break;

            case SourceOutcome.Failed:
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — fehlgeschlagen: kein Urteil der Quelle ({Reason})",
                    key, auftrag, grund);
                break;

            default:
                // Accepted: die Quelle hat angenommen. Das steht in der Debug-Zeile; eine Warnung wäre
                // hier eine Störung, die keine ist.
                break;
        }
    }

    /// <summary>
    /// F-M685: der Ausgang in einem Wort für die Auftragszeile — bei „beschäftigt" mit der Wartezeit,
    /// damit die Zeile auch ohne die WRN-Zeile lesbar bleibt.
    /// </summary>
    /// <param name="ausgang">Der eine Ausgang.</param>
    /// <param name="retryAfterSeconds">Die Wartezeit, wenn die Quelle eine genannt hat.</param>
    /// <returns>Der Text.</returns>
    private static string Ausgang(SourceOutcome ausgang, int? retryAfterSeconds)
        => ausgang switch
        {
            SourceOutcome.Accepted => "angenommen",
            SourceOutcome.Rejected => "abgelehnt",
            SourceOutcome.Busy => "beschäftigt ("
                + (retryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "unbekannte") + " s)",
            _ => "fehlgeschlagen",
        };

    /// <summary>
    /// F-M684: die sichtbare Zeile eines Fehlerfalls. Vier Fälle sind ausdrücklich verlangt — kein
    /// Zugang/unlesbare Quelldatei, abgewiesenes Feld, Bremse (mit Zeitpunkt), keine Suchgrundlage —,
    /// und ein Netzfehler gehört dazu, weil er den Lauf sonst nur als Zähler erreichte.
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="auftrag">Der Auftrag (Suche, Hole, Sende, Melde).</param>
    /// <param name="fault">Der Fehler der Grenze.</param>
    private void Fault(string key, string auftrag, SourceFaultInfo fault)
    {
        string grund = Short(fault.Reason);
        switch (fault.Kind)
        {
            case SourceFault.RateLimited:
                // Mit Zeitpunkt: die Quelle nennt die Wartezeit, und der Lauf soll sagen, ab wann es
                // weitergeht — ein „429" ohne Zeit ist eine Zeile, mit der niemand planen kann.
                DateTime ab = DateTime.UtcNow.AddSeconds(fault.RetryAfterSeconds ?? 0);
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — Rate begrenzt: {Seconds} s warten, nächster Versuch ab {From:HH:mm:ss}Z ({Reason})",
                    key, auftrag,
                    fault.RetryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "?",
                    ab,
                    grund);
                break;

            case SourceFault.AuthFailed:
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — kein Zugang oder Quelldatei unlesbar: {Reason}",
                    key, auftrag, grund);
                break;

            case SourceFault.NoSearchBase:
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — keine Suchgrundlage: die Anfrage trägt nichts, womit diese Quelle suchen kann ({Reason})",
                    key, auftrag, grund);
                break;

            case SourceFault.Rejected:
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — Feld abgewiesen: die Quelle hat mit Grund abgelehnt ({Reason})",
                    key, auftrag, grund);
                break;

            case SourceFault.Transient:
                _logger.LogWarning(
                    "[Sub Scribe-Q] {Source} {Task} — Netz: keine verwertbare Antwort ({Reason})",
                    key, auftrag, grund);
                break;

            default:
                // NotFound: der Eintrag ist fort, die Quelle hat geantwortet. Ein Hinweis, kein Alarm.
                LogUtil.Detail(
                    _mode,
                    _logger,
                    "[Sub Scribe-Q] {Source} {Task} — die Quelle kennt den Eintrag nicht (mehr): {Reason}",
                    key, auftrag, grund);
                break;
        }
    }

    /// <summary>Die Anfrage in einer Zeile: was wir über den Titel wissen, ohne Geheimnis.</summary>
    /// <param name="request">Die Suchanfrage.</param>
    /// <returns>Der Text.</returns>
    private static string Beschreibung(SourceSearchRequest request)
    {
        string ids = "imdb=" + (string.IsNullOrWhiteSpace(request.ImdbId) ? "-" : request.ImdbId)
            + " tmdb=" + (string.IsNullOrWhiteSpace(request.TmdbId) ? "-" : request.TmdbId);
        string titel = string.IsNullOrWhiteSpace(request.Name) ? "-" : Short(request.Name);
        string staffel = request.IsSeries
            ? " s" + (request.Season?.ToString(CultureInfo.InvariantCulture) ?? "?")
                + "e" + (request.Episode?.ToString(CultureInfo.InvariantCulture) ?? "*")
            : " (Film)";
        string sprachen = request.Languages.Count == 0 ? "alle" : string.Join(",", request.Languages);
        string hi = request.HearingImpaired switch { true => "ja", false => "nein", null => "egal" };
        string pack = request.WantsPack ? ", Behälter gewünscht" : string.Empty;
        return ids + " " + titel + staffel + " sprachen=" + sprachen + " hi=" + hi + pack;
    }

    /// <summary>Das Ziel einer Abgabe in einer Zeile.</summary>
    /// <param name="target">Das Ziel.</param>
    /// <returns>Der Text.</returns>
    private static string Ziel(SourceSendTarget target)
    {
        string art = target.IsSeries
            ? "s" + (target.Season?.ToString(CultureInfo.InvariantCulture) ?? "?")
                + "e" + (target.Episode?.ToString(CultureInfo.InvariantCulture) ?? "*")
            : "Film";
        string titel = string.IsNullOrWhiteSpace(target.Title) ? "-" : Short(target.Title);
        return titel + " " + art;
    }

    /// <summary>Ein Text in einer Zeile: höchstens 120 Zeichen, Zeilenumbrüche werden zu Leerzeichen.</summary>
    /// <param name="text">Der Text.</param>
    /// <returns>Der gekürzte Text.</returns>
    private static string Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        string eine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return eine.Length <= 120 ? eine : eine[..120] + "…";
    }
}
