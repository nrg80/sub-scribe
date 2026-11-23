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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M684: die Zähler EINER Quelle — Abrufe, Seiten, Fehlschläge (nach Art getrennt), letzter Erfolg,
/// letzter Fehler samt Grund. Sie leben im Speicher des Prozesses und werden nirgends gespeichert.
/// <para>
/// WARUM NICHT AUS DEM LOG ABGELEITET. Ein Zähler, der erst durch das Durchsuchen einer Logdatei
/// entsteht, ist keine Messung, sondern eine zweite Deutung derselben Zeilen — und er fehlt genau dann,
/// wenn das Log abgeschaltet ist. Diese Zähler zählen am Ort des Geschehens: die Anfrage an eine Quelle
/// zählt der Live-Kanal der Clients (<see cref="SourceTraffic.NoteTrace"/>), den Auftrag der Grenze zählt die Hülle
/// (<see cref="SourceTraffic.EngineScope"/>), und ein unlesbares Einstellungsfile zählt
/// <see cref="SourceTraffic.NoteConfigFault"/>.
/// </para>
/// <para>
/// WARUM EIN AUFTRAG TROTZDEM NUR EINMAL ZÄHLT. F-M682 sagt: die Grenze zählt AUFTRÄGE, nicht Anfragen.
/// Solange ein Auftrag läuft (<see cref="SourceTraffic.EngineScope"/>), werden die Anfragezeilen seiner Engine deshalb
/// nicht ein zweites Mal gezählt — sonst stünde in der Summenzeile die Summe aus Aufträgen und ihren
/// Anfragen.
/// </para>
/// </summary>
public sealed class SourceCounter
{
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of the <see cref="SourceCounter"/> class.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    public SourceCounter(string key)
    {
        Key = key;
        StartedUtc = DateTime.UtcNow;
    }

    /// <summary>Gets der Quellenschlüssel.</summary>
    public string Key { get; }

    /// <summary>Gets wann die Zählung dieses Laufs begann (für die Dauer der Summenzeile).</summary>
    public DateTime StartedUtc { get; private set; }

    /// <summary>Gets die Abrufe: Anfragen an die Quelle (bei der Grenze: je Auftrag eine).</summary>
    public int Requests { get; private set; }

    /// <summary>Gets die Aufträge der Grenze (Suche, Hole, Sende, Melde).</summary>
    public int Tasks { get; private set; }

    /// <summary>Gets die Antworten, die eine Seite/Datei geliefert haben.</summary>
    public int Pages { get; private set; }

    /// <summary>Gets die Fehlschläge insgesamt (Summe der Arten).</summary>
    public int Errors { get; private set; }

    /// <summary>Gets die Fehlschläge wegen einer Bremse der Quelle (429).</summary>
    public int RateLimited { get; private set; }

    /// <summary>Gets die Fehlschläge wegen Zugang (401/403, fehlender Schlüssel, unlesbare Quelldatei).</summary>
    public int AuthFailed { get; private set; }

    /// <summary>Gets die Fehlschläge wegen Netz (5xx, Zeitüberschreitung, keine Antwort).</summary>
    public int NetworkFailed { get; private set; }

    /// <summary>
    /// F-M685: die Antworten „beschäftigt" (429, Stundendeckel) — ein EIGENER Zähler, getrennt von den
    /// Fehlschlägen. Eine Bremse ist keine Störung: sie sagt „später nochmal", und wer sie unter die
    /// Fehler zählte, machte aus einer Wartezeit einen Defekt.
    /// </summary>
    public int Busy { get; private set; }

    /// <summary>F-M685: die Wartezeit der zuletzt gesehenen Bremse in Sekunden, oder null.</summary>
    public int? LastBusySeconds { get; private set; }

    /// <summary>Gets die Abweisungen der Quelle mit Grund.</summary>
    public int Rejected { get; private set; }

    /// <summary>Gets die Anfragen ohne Suchgrundlage.</summary>
    public int NoSearchBase { get; private set; }

    /// <summary>Gets die Einträge, die die Quelle nicht (mehr) kennt.</summary>
    public int NotFound { get; private set; }

    /// <summary>Gets die unlesbaren/kaputten Einstellungsfilen dieser Quelle.</summary>
    public int ConfigFailed { get; private set; }

    /// <summary>Gets der Zeitpunkt des letzten Erfolgs, oder null.</summary>
    public DateTime? LastSuccessUtc { get; private set; }

    /// <summary>Gets der Zeitpunkt des letzten Fehlers, oder null.</summary>
    public DateTime? LastFailureUtc { get; private set; }

    /// <summary>Gets der kurze Grund des letzten Fehlers, oder null.</summary>
    public string? LastFailureReason { get; private set; }

    /// <summary>F-M684: setzt die Zählung dieses Laufs zurück und beginnt die Uhr neu.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Requests = 0;
            Tasks = 0;
            Pages = 0;
            Errors = 0;
            RateLimited = 0;
            AuthFailed = 0;
            NetworkFailed = 0;
            Busy = 0;
            LastBusySeconds = null;
            Rejected = 0;
            NoSearchBase = 0;
            NotFound = 0;
            ConfigFailed = 0;
            LastSuccessUtc = null;
            LastFailureUtc = null;
            LastFailureReason = null;
            StartedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>F-M684: zählt eine Anfrage an die Quelle.</summary>
    public void CountRequest()
    {
        lock (_gate)
        {
            Requests++;
        }
    }

    /// <summary>F-M684: zählt einen Auftrag der Grenze — und die Anfrage, die er mindestens kostet.</summary>
    public void CountTask()
    {
        lock (_gate)
        {
            Tasks++;
            Requests++;
        }
    }

    /// <summary>F-M684: zählt eine Antwort, die eine Seite/Datei geliefert hat.</summary>
    public void CountPage()
    {
        lock (_gate)
        {
            Pages++;
            LastSuccessUtc = DateTime.UtcNow;
        }
    }

    /// <summary>F-M684: zählt einen Fehlschlag samt Art und Grund.</summary>
    /// <param name="kind">Die Art des Fehlers.</param>
    /// <param name="reason">Der kurze Grund, oder null.</param>
    public void CountFault(SourceFault kind, string? reason)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case SourceFault.RateLimited:
                    RateLimited++;
                    break;
                case SourceFault.AuthFailed:
                    AuthFailed++;
                    break;
                case SourceFault.Transient:
                    NetworkFailed++;
                    break;
                case SourceFault.Rejected:
                    Rejected++;
                    break;
                case SourceFault.NoSearchBase:
                    NoSearchBase++;
                    break;
                case SourceFault.NotFound:
                    NotFound++;
                    break;
                default:
                    break;
            }

            // NotFound ist kein Fehlschlag des Netzes: der Eintrag ist fort, die Quelle hat geantwortet.
            if (kind != SourceFault.NotFound)
            {
                Errors++;
                LastFailureUtc = DateTime.UtcNow;
                LastFailureReason = Short(reason);
            }
        }
    }

    /// <summary>
    /// F-M685: zählt eine Bremse — als eigener Zähler, NICHT als Fehlschlag, mit der genannten Wartezeit.
    /// </summary>
    /// <param name="retryAfterSeconds">Die Wartezeit, wenn die Quelle eine genannt hat.</param>
    public void CountBusy(int? retryAfterSeconds)
    {
        lock (_gate)
        {
            Busy++;
            LastBusySeconds = retryAfterSeconds;
        }
    }

    /// <summary>
    /// F-M685: zählt den Ausgang eines schreibenden Auftrags. Die Abbildung steht hier EINMAL, damit
    /// Senden und Melden dieselbe Zahl in dieselben Töpfe legen.
    /// </summary>
    /// <param name="ausgang">Der eine Ausgang.</param>
    /// <param name="reason">Der Klartext der Quelle, wenn sie einen nennt.</param>
    /// <param name="retryAfterSeconds">Die Wartezeit bei „beschäftigt", wenn die Quelle eine nennt.</param>
    public void CountOutcome(SourceOutcome ausgang, string? reason, int? retryAfterSeconds)
    {
        switch (ausgang)
        {
            case SourceOutcome.Accepted:
                CountPage();
                break;

            case SourceOutcome.Rejected:
                CountFault(SourceFault.Rejected, reason);
                break;

            case SourceOutcome.Busy:
                CountBusy(retryAfterSeconds);
                break;

            case SourceOutcome.Failed:
                CountFault(SourceFault.Transient, reason);
                break;

            default:
                break;
        }
    }

    /// <summary>F-M684: zählt ein unlesbares Einstellungsfile dieser Quelle.</summary>
    /// <param name="reason">Der Klartext-Grund aus <see cref="SourceSettings.Problem"/>.</param>
    public void CountConfigFault(string? reason)
    {
        lock (_gate)
        {
            ConfigFailed++;
            Errors++;
            LastFailureUtc = DateTime.UtcNow;
            LastFailureReason = Short(reason);
        }
    }

    /// <summary>F-M684: der Stand als unveränderliche Antwort für den Endpunkt.</summary>
    /// <returns>Der Stand.</returns>
    public SourceCountersView Snapshot()
    {
        lock (_gate)
        {
            return new SourceCountersView(
                Key,
                Requests,
                Tasks,
                Pages,
                Errors,
                RateLimited,
                AuthFailed,
                NetworkFailed,
                Rejected,
                NoSearchBase,
                NotFound,
                ConfigFailed,
                Busy,
                LastBusySeconds,
                LastSuccessUtc,
                LastFailureUtc,
                LastFailureReason,
                StartedUtc);
        }
    }

    /// <summary>Ein Grund in einer Zeile: höchstens 160 Zeichen, Zeilenumbrüche werden zu Leerzeichen.</summary>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der gekürzte Grund, oder null.</returns>
    private static string? Short(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        string eine = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return eine.Length <= 160 ? eine : eine[..160] + "…";
    }
}

/// <summary>
/// F-M684: der Stand der Zähler EINER Quelle, so wie ihn der Endpunkt ausliefert — Zahlen, Zeitpunkte und
/// ein kurzer Grund. Kein Geheimwert, kein Dateiinhalt: der Grund ist der Klartext-Hinweis, den
/// <see cref="SourceSettings.Problem"/> trägt, und der nennt Art und Ursache, nie einen Wert.
/// </summary>
/// <param name="Key">Der Quellenschlüssel.</param>
/// <param name="Requests">Abrufe (Anfragen an die Quelle).</param>
/// <param name="Tasks">Aufträge der Grenze.</param>
/// <param name="Pages">Antworten mit Inhalt.</param>
/// <param name="Errors">Fehlschläge insgesamt.</param>
/// <param name="RateLimited">Fehlschläge wegen einer Bremse.</param>
/// <param name="AuthFailed">Fehlschläge wegen Zugang.</param>
/// <param name="NetworkFailed">Fehlschläge wegen Netz.</param>
/// <param name="Rejected">Abweisungen mit Grund.</param>
/// <param name="NoSearchBase">Anfragen ohne Suchgrundlage.</param>
/// <param name="NotFound">Einträge, die die Quelle nicht mehr kennt.</param>
/// <param name="ConfigFailed">Unlesbare Einstellungsfilen.</param>
/// <param name="Busy">F-M685: Antworten „beschäftigt" (eigener Zähler, nicht bei den Fehlern).</param>
/// <param name="LastBusySeconds">F-M685: die Wartezeit der letzten Bremse in Sekunden, oder null.</param>
/// <param name="LastSuccessUtc">Zeitpunkt des letzten Erfolgs.</param>
/// <param name="LastFailureUtc">Zeitpunkt des letzten Fehlers.</param>
/// <param name="LastFailureReason">Kurzer Grund des letzten Fehlers.</param>
/// <param name="StartedUtc">Beginn der Zählung.</param>
public sealed record SourceCountersView(
    string Key,
    int Requests,
    int Tasks,
    int Pages,
    int Errors,
    int RateLimited,
    int AuthFailed,
    int NetworkFailed,
    int Rejected,
    int NoSearchBase,
    int NotFound,
    int ConfigFailed,
    int Busy,
    int? LastBusySeconds,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    string? LastFailureReason,
    DateTime StartedUtc);

/// <summary>
/// F-M684: die Zähler aller Quellen und die Summenzeile eines Laufs. Eine Quelle, die nicht gefragt
/// wurde, steht mit Nullen darin — das ist die Auskunft „nicht gefragt", nicht „kein Eintrag".
/// <para>
/// Die Tabelle ist statisch, weil die Anfragen aus drei Clients und vier Aufträgen kommen und der
/// Endpunkt sie ohne Lauf lesen können muss. Das ist zulässig, weil es je Prozess nur EINEN Lauf gibt
/// (die globale Lauf-Sperre, F-M94h): zwei Läufe können sich die Zahlen nicht gegenseitig zurücksetzen.
/// </para>
/// </summary>
public static class SourceTraffic
{
    private static readonly ConcurrentDictionary<string, SourceCounter> Table = new(StringComparer.Ordinal);

    /// <summary>F-M684: die Zählung läuft, solange ein Auftrag der Grenze läuft (siehe <see cref="SourceTraffic.EngineScope"/>).</summary>
    private static readonly AsyncLocal<int> EngineDepth = new();

    /// <summary>Gets die drei Quellen dieser Fassung — dieselben Schlüssel wie im Bestand.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } = new[] { "subdl", "tvsubtitles", "moviesubtitles" };

    /// <summary>F-M684: der Zähler EINER Quelle; er entsteht beim ersten Zugriff mit Nullen.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <returns>Der Zähler.</returns>
    public static SourceCounter For(string? key)
        => Table.GetOrAdd(string.IsNullOrWhiteSpace(key) ? "?" : key, k => new SourceCounter(k));

    /// <summary>
    /// F-M684: Beginn eines Laufs — die Zähler der genannten Quellen stehen auf null und die Uhr läuft.
    /// Quellen, die nicht genannt sind, behalten ihren Stand.
    /// </summary>
    /// <param name="keys">Die Quellen dieses Laufs.</param>
    public static void BeginRun(IEnumerable<string>? keys)
    {
        foreach (string key in keys ?? KnownKeys)
        {
            For(key).Reset();
        }
    }

    /// <summary>F-M684: beginnt den Bereich EINES Auftrags — solange er läuft, zählen die Anfragezeilen
    /// seiner Engine nicht ein zweites Mal.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <returns>Ein Bereich, der beim Verlassen endet (using).</returns>
    public static IDisposable EngineScope(string key)
    {
        For(key).CountTask();
        EngineDepth.Value++;
        return new DepthScope();
    }

    /// <summary>
    /// F-M684: eine Zeile des Live-Kanals der Clients („API call …", „API response … → 200") — die
    /// Netzarbeit einer Quelle, gezählt im Augenblick der Anfrage. Unbekannte Zeilen und Anfragen an
    /// fremde Adressen werden nicht gezählt; während ein Auftrag läuft, zählt die Grenze selbst.
    /// </summary>
    /// <param name="traceLine">Die Zeile des Kanals.</param>
    public static void NoteTrace(string? traceLine)
    {
        if (string.IsNullOrWhiteSpace(traceLine) || EngineDepth.Value > 0)
        {
            return;
        }

        bool anfrage = traceLine.StartsWith("API call ", StringComparison.Ordinal);
        bool antwort = traceLine.StartsWith("API response ", StringComparison.Ordinal);
        if (!anfrage && !antwort)
        {
            return;
        }

        string? key = KeyForUrl(FirstUrl(traceLine));
        if (key is null)
        {
            return;
        }

        SourceCounter zaehler = For(key);
        if (anfrage)
        {
            zaehler.CountRequest();
            return;
        }

        int status = TrailingStatus(traceLine);
        if (status is >= 200 and < 400)
        {
            zaehler.CountPage();
            return;
        }

        zaehler.CountFault(FaultOfStatus(status), status == 0 ? "keine Antwort" : "HTTP " + status.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>F-M684: eine Antwort der Grenze (Auftrag beendet) — Erfolg oder Fehler, je nach Antwort.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="fault">Der Fehler, oder null bei Erfolg.</param>
    public static void NoteAnswer(string key, SourceFaultInfo? fault)
    {
        SourceCounter zaehler = For(key);
        if (fault is null)
        {
            zaehler.CountPage();
            return;
        }

        zaehler.CountFault(fault.Kind, fault.Reason);
    }

    /// <summary>
    /// F-M685: der Ausgang eines Sende-Auftrags — angenommen, abgelehnt, beschäftigt oder fehlgeschlagen.
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="answer">Die Antwort der Grenze.</param>
    public static void NoteSendAnswer(string key, SourceSendAnswer answer)
        => For(key).CountOutcome(answer.Outcome, answer.Reason, answer.RetryAfterSeconds);

    /// <summary>
    /// F-M685: der Ausgang eines Melde-Auftrags — dieselben vier Werte, dieselben Zähler.
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="answer">Die Antwort der Grenze.</param>
    public static void NoteReportAnswer(string key, SourceReportAnswer answer)
        => For(key).CountOutcome(answer.Outcome, answer.Reason, answer.RetryAfterSeconds);

    /// <summary>F-M684: ein unlesbares Einstellungsfile EINER Quelle.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="reason">Der Klartext-Grund.</param>
    public static void NoteConfigFault(string key, string? reason) => For(key).CountConfigFault(reason);

    /// <summary>F-M684: der Stand aller bekannten Quellen, für den Endpunkt.</summary>
    /// <returns>Je Quelle ein Eintrag, in der Reihenfolge von <see cref="KnownKeys"/>.</returns>
    public static IReadOnlyList<SourceCountersView> Snapshot()
        => KnownKeys.Select(k => For(k).Snapshot()).ToList();

    /// <summary>
    /// F-M684: die Summenzeile JE QUELLE am Ende eines Laufs — <c>SubDL: 38 Aufrufe, 2 Seiten, 0 Fehler,
    /// 41 s</c>. Sie steht auf Normal (Information), also erscheint sie bei jedem Server-Pegel, und sie
    /// wird auch dann geschrieben, wenn der Lauf abbricht: dann mit dem Stand bis zum Abbruch.
    /// </summary>
    /// <param name="logger">Der Logger des Laufs.</param>
    /// <param name="keys">Die Quellen dieses Laufs; null heißt „alle bekannten".</param>
    public static void WriteSummary(ILogger? logger, IEnumerable<string>? keys = null)
    {
        foreach (string key in keys ?? KnownKeys)
        {
            SourceCountersView stand = For(key).Snapshot();
            long sekunden = (long)Math.Max(0, (DateTime.UtcNow - stand.StartedUtc).TotalSeconds);
            string auftraege = stand.Tasks > 0
                ? " (" + stand.Tasks.ToString(CultureInfo.InvariantCulture) + " Aufträge)"
                : string.Empty;

            // F-M685: „beschäftigt" steht als eigener Posten in der Zeile — getrennt von den Fehlern und
            // mit der Wartezeit, die die Quelle zuletzt genannt hat.
            string beschaeftigt = stand.Busy > 0
                ? " (" + (stand.LastBusySeconds?.ToString(CultureInfo.InvariantCulture) ?? "?") + " s zuletzt)"
                : string.Empty;

            LogUtil.Normal(
                logger,
                "{Source}: {Calls} Aufrufe, {Pages} Seiten, {Errors} Fehler, {Busy} beschäftigt{BusySeconds}, {Seconds} s{Auftraege}",
                DisplayName(key),
                stand.Requests,
                stand.Pages,
                stand.Errors,
                stand.Busy,
                beschaeftigt,
                sekunden,
                auftraege);
        }
    }

    /// <summary>F-M684: der Anzeigename einer Quelle für die Summenzeile.</summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <returns>Der Name, wie der Betreiber ihn schreibt.</returns>
    public static string DisplayName(string key) => key switch
    {
        "subdl" => "SubDL",
        "tvsubtitles" => "tvsubtitles.net",
        "moviesubtitles" => "moviesubtitles.org",
        _ => key,
    };

    /// <summary>F-M684: die Quelle, zu der eine Adresse gehört — oder null für eine fremde.</summary>
    /// <param name="url">Die Adresse aus einer Kanalzeile.</param>
    /// <returns>Der Quellenschlüssel, oder null.</returns>
    public static string? KeyForUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string host = uri.Host;
        if (host.EndsWith("subdl.com", StringComparison.OrdinalIgnoreCase))
        {
            return "subdl";
        }

        if (host.EndsWith("tvsubtitles.net", StringComparison.OrdinalIgnoreCase))
        {
            return "tvsubtitles";
        }

        if (host.EndsWith("moviesubtitles.org", StringComparison.OrdinalIgnoreCase))
        {
            return "moviesubtitles";
        }

        return null;
    }

    /// <summary>Die erste Adresse einer Kanalzeile.</summary>
    /// <param name="line">Die Zeile.</param>
    /// <returns>Die Adresse, oder leer.</returns>
    private static string FirstUrl(string line)
    {
        foreach (string teil in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (teil.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return teil;
            }
        }

        return string.Empty;
    }

    /// <summary>Der Status am Ende einer Antwortzeile (<c>→ 200</c>), oder 0.</summary>
    /// <param name="line">Die Zeile.</param>
    /// <returns>Der Status, oder 0 wenn keiner lesbar ist.</returns>
    private static int TrailingStatus(string line)
    {
        int pfeil = line.LastIndexOf('→');
        string rest = pfeil >= 0 ? line[(pfeil + 1)..].Trim() : string.Empty;
        int ende = 0;
        while (ende < rest.Length && char.IsAsciiDigit(rest[ende]))
        {
            ende++;
        }

        return ende > 0 && int.TryParse(rest[..ende], NumberStyles.Integer, CultureInfo.InvariantCulture, out int status)
            ? status
            : 0;
    }

    /// <summary>Die Art eines Fehlers aus dem HTTP-Status.</summary>
    /// <param name="status">Der Status, 0 wenn keine Antwort kam.</param>
    /// <returns>Die Art.</returns>
    private static SourceFault FaultOfStatus(int status) => status switch
    {
        429 => SourceFault.RateLimited,
        401 or 403 => SourceFault.AuthFailed,
        _ => SourceFault.Transient,
    };

    private sealed class DepthScope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            EngineDepth.Value--;
        }
    }
}
