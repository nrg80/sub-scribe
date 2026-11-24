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
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// Der Zustand einer Einsendung, wie ihn das Konto führt.
/// <para>
/// Bewusst gröber als jede Quelle: die Seiten nennen ihre Zustände selbst, für den Nachlauf zählt nur,
/// ob noch etwas offen ist, ob es angenommen wurde oder ob es abgelehnt wurde (dann steht der Klartext
/// der Quelle daneben).
/// </para>
/// </summary>
public enum SubmissionState
{
    /// <summary>Liegt beim Konto und wartet auf Prüfung.</summary>
    Pending,

    /// <summary>Ist veröffentlicht.</summary>
    Accepted,

    /// <summary>Wurde abgelehnt — <see cref="SourceSubmission.Reason"/> sagt, warum.</summary>
    Rejected,
}

/// <summary>
/// F-M729: eine Einsendung, neutral beschrieben — was der Nachlauf braucht und nichts darüber hinaus.
/// <para>
/// Es gibt ZWEI Kennungen, weil die Seiten zwei führen: <see cref="Handle"/> ist der Griff, mit dem
/// zurückgezogen wird, <see cref="EntryHandle"/> die Kennung des veröffentlichten Eintrags, sobald es
/// einen gibt. Sie liegen in verschiedenen Zahlenkreisen (F-M435), deshalb sind sie zwei Felder und
/// nicht eines.
/// </para>
/// <para>
/// Sprachen gehen in unserer ISO-639-2-Schreibweise über die Grenze (dieselbe Regel wie bei den vier
/// Aufträgen, F-M682) — die Engine übersetzt die Schreibweise ihrer Seite selbst.
/// </para>
/// </summary>
/// <param name="Handle">Der Griff, mit dem diese Einsendung zurückgezogen wird.</param>
/// <param name="EntryHandle">Die Kennung des veröffentlichten Eintrags, oder null solange er nicht veröffentlicht ist.</param>
/// <param name="Language">Sprache in ISO-639-2 (wie der Bestand sie führt).</param>
/// <param name="Name">Der Releasename, wie die Quelle ihn führt.</param>
/// <param name="CreatedAtUtc">Wann die Einsendung angelegt wurde.</param>
/// <param name="State">Offen, angenommen oder abgelehnt.</param>
/// <param name="Reason">Der Klartext einer Ablehnung, sonst null.</param>
/// <param name="ContentHash">Der Inhalts-Hash, wenn die Quelle einen nennt.</param>
/// <param name="ImdbId">IMDb-Kennung des Titels, wenn die Quelle sie nennt.</param>
/// <param name="TmdbId">TMDb-Nummer des Titels, wenn die Quelle sie nennt.</param>
/// <param name="ForeignId">Die Kennung, die die Quelle dem EINTRAG selbst gibt (für <c>reference</c>), sonst null.</param>
/// <param name="Season">Staffel (Serien) oder 0.</param>
/// <param name="Episode">Folge (Serien) oder 0.</param>
public sealed record SourceSubmission(
    string Handle,
    string? EntryHandle,
    string Language,
    string Name,
    DateTimeOffset CreatedAtUtc,
    SubmissionState State,
    string? Reason = null,
    string? ContentHash = null,
    string? ImdbId = null,
    string? TmdbId = null,
    string? ForeignId = null,
    int Season = 0,
    int Episode = 0);

/// <summary>
/// F-M729: die Frage nach den eigenen Einsendungen.
/// </summary>
/// <param name="Published">True fragt die veröffentlichten Einträge, false die noch offenen Einsendungen.</param>
/// <param name="MaxPages">Obergrenze der Seiten, die abgelaufen werden (Sicherheit, nicht Takt).</param>
/// <param name="NewerThan">
/// Der Wasserstand: die Liste ist die jüngste zuerst, und die Engine hört auf, sobald sie Einträge
/// sieht, die älter sind (F-M437). Null heißt „von vorn".
/// </param>
/// <param name="StartPage">Erste Seite — 1 für den Nachlauf, eine höhere Zahl nur für die Diagnose.</param>
public sealed record SourceSubmissionQuery(
    bool Published = false,
    int MaxPages = 10,
    DateTimeOffset? NewerThan = null,
    int StartPage = 1);

/// <summary>
/// F-M729: die Antwort auf <see cref="ISourceMaintenance.ListAsync"/>.
/// </summary>
/// <param name="Submissions">Die Einträge, in der Reihenfolge der Quelle.</param>
/// <param name="Fault">Der normalisierte Fehler, oder <see cref="SourceFault.None"/>.</param>
/// <param name="Why">Der Klartext der Quelle, wenn sie einen mitgibt.</param>
/// <param name="PagesWalked">Wie viele Seiten wirklich abgelaufen wurden.</param>
public sealed record SourceSubmissionAnswer(
    IReadOnlyList<SourceSubmission> Submissions,
    SourceFault Fault = SourceFault.None,
    string? Why = null,
    int PagesWalked = 0)
{
    /// <summary>Gets a value indicating whether die Liste gelesen werden konnte.</summary>
    public bool Ok => Fault == SourceFault.None;

    /// <summary>Eine Antwort ohne Einträge, mit Grund.</summary>
    /// <param name="fault">Der normalisierte Fehler.</param>
    /// <param name="why">Der Klartext, wenn es einen gibt.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceSubmissionAnswer Failed(SourceFault fault, string? why = null)
        => new(Array.Empty<SourceSubmission>(), fault, why);
}

/// <summary>
/// F-M729: die Antwort auf <see cref="ISourceMaintenance.WithdrawAsync"/> — derselbe Ausgang wie beim
/// Senden (F-M685), damit der Aufrufer nicht zwei Vokabulare lernt.
/// </summary>
/// <param name="Outcome">Angenommen, abgelehnt, beschäftigt oder fehlgeschlagen.</param>
/// <param name="Reason">Der Klartext der Quelle, wenn sie einen mitgibt.</param>
public sealed record SourceWithdrawAnswer(SourceOutcome Outcome, string? Reason = null)
{
    /// <summary>Gets a value indicating whether der Eintrag weg ist.</summary>
    public bool Ok => Outcome == SourceOutcome.Accepted;

    /// <summary>Der Eintrag ist zurückgezogen.</summary>
    /// <returns>Die Antwort.</returns>
    public static SourceWithdrawAnswer Accepted() => new(SourceOutcome.Accepted);

    /// <summary>Der Eintrag war nicht (mehr) da — kein Fehler des Laufs.</summary>
    /// <param name="reason">Der Klartext, wenn es einen gibt.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceWithdrawAnswer Gone(string? reason = null) => new(SourceOutcome.Rejected, reason);

    /// <summary>Netz, Anmeldung oder Unbekanntes — kein Urteil über die Sache.</summary>
    /// <param name="reason">Der Klartext, wenn es einen gibt.</param>
    /// <param name="busy">True, wenn die Quelle gebremst hat (429).</param>
    /// <returns>Die Antwort.</returns>
    public static SourceWithdrawAnswer Failed(string? reason = null, bool busy = false)
        => new(busy ? SourceOutcome.Busy : SourceOutcome.Failed, reason);
}

/// <summary>
/// F-M729 (Betreiber-Entscheidung 24.11.2026: „Zweiter schmaler Vertrag als maintenance engine. Mit
/// Zugangsdaten aus dem source konfi file"): die Konto-Pflege einer Quelle — ein ZWEITER, optionaler
/// Vertrag neben den vier Aufträgen, nicht ein fünfter Auftrag.
/// <para>
/// Anlass: der Upload-Nachlauf braucht „eigene Einsendungen auflisten" und „Eintrag zurückziehen".
/// Beides ist keine Arbeit an einem Untertitel, sondern Buchhaltung des Uploader-Kontos, und nur eine
/// Quelle kann es überhaupt (SubDL führt eine Einsendungsliste; tvsubtitles nicht, moviesubtitles hat
/// keinen Upload). F-M682 hat den Vertrag ausdrücklich auf vier Aufträge festgelegt — dieser hier
/// erweitert ihn nicht: <see cref="ISourceEngine"/> bleibt bei vier Methoden, und wer keine Konto-Pflege
/// hat, implementiert dieses Interface einfach nicht.
/// </para>
/// <para>
/// Die Grenzregeln gelten unverändert: die Engine legt sich keinen HttpClient an (er wird gereicht),
/// sie kennt weder Store noch unsere Kennungsformen, und über die Grenze gehen nur neutrale Daten.
/// Die Zugangsdaten liest sie aus ihrer EIGENEN Einstellungsdatei (<c>sources/&lt;key&gt;.json</c>,
/// F-M683/F-M694), nicht aus der Plugin-Konfiguration.
/// </para>
/// </summary>
public interface ISourceMaintenance
{
    /// <summary>Gets der Schlüssel dieser Quelle — derselbe Wert, den der Bestand führt.</summary>
    string Key { get; }

    /// <summary>
    /// Die eigenen Einsendungen oder die veröffentlichten Einträge dieses Kontos.
    /// </summary>
    /// <param name="query">Was gefragt ist: welche Liste, wie tief, ab welchem Wasserstand.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Einträge und wie viele Seiten dafür abgelaufen wurden.</returns>
    Task<SourceSubmissionAnswer> ListAsync(SourceSubmissionQuery query, CancellationToken ct);

    /// <summary>
    /// Zieht EINE Einsendung zurück.
    /// </summary>
    /// <param name="handle">Der Griff aus <see cref="SourceSubmission.Handle"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Der Ausgang.</returns>
    Task<SourceWithdrawAnswer> WithdrawAsync(string handle, CancellationToken ct);
}

/// <summary>
/// F-M730: wie eine Quelle dasteht — grün, gelb oder rot. Dieselbe Ampel, die die Statusseite zeigt,
/// nur ohne dass die Seite die Quelle kennt.
/// </summary>
public enum SourceHealth
{
    /// <summary>Die Quelle antwortet und tut, was sie soll.</summary>
    Good,

    /// <summary>Die Quelle antwortet, aber eingeschränkt (Drosselung, kein Zugang hinterlegt).</summary>
    Warn,

    /// <summary>Die Quelle antwortet nicht oder weist den Zugang ab.</summary>
    Bad,
}

/// <summary>
/// F-M730: EIN Licht auf der Statusseite, so wie die Quelle es selbst beschreibt. Der Schlüssel ist ein
/// Datenwert (die Seite hängt ihre Kachel daran), die Beschriftung ist Klartext, und der Klartext der
/// Meldung kommt aus der Quelle — er nennt keine unserer Kennungen und keine Zugangsdaten.
/// </summary>
/// <param name="Key">Woran die Seite die Kachel hängt: <c>subdl</c>, <c>subdlLogin</c>, <c>tvst</c>, <c>mst</c>.</param>
/// <param name="Label">Die Beschriftung im Klartext.</param>
/// <param name="Health">Die Ampel.</param>
/// <param name="Configured">True, wenn der nötige Zugang hinterlegt ist (sonst ist die Ampel gelb, nicht rot).</param>
/// <param name="Message">Was die Quelle zu sagen hat, in ihren Worten.</param>
public sealed record SourceStatusLight(
    string Key,
    string Label,
    SourceHealth Health,
    bool Configured,
    string Message);

/// <summary>
/// F-M730: die Antwort einer Quelle auf „wie stehst du da". Sie trägt ihre Lichter und, wenn sie ein
/// Konto führt, die Zahlen ihres Kontingents — mehr weiß die Seite nicht und mehr braucht sie nicht.
/// </summary>
/// <param name="Source">Der Schlüssel der Quelle.</param>
/// <param name="Lights">Die Lichter, die diese Quelle setzt (eine Quelle darf mehrere haben).</param>
/// <param name="SearchLimit">Tagesgrenze der Suche, 0 wenn unbekannt.</param>
/// <param name="SearchRemaining">Was heute noch geht, -1 wenn unbekannt.</param>
/// <param name="DownloadLimit">Tagesgrenze der Downloads, 0 wenn unbekannt.</param>
/// <param name="DownloadRemaining">Was heute noch geht, -1 wenn unbekannt.</param>
/// <param name="ResetAtUtc">Wann beide Zähler zurückgesetzt werden, null wenn unbekannt.</param>
/// <param name="LatencyMs">Wie lange die Quelle für die Antwort brauchte.</param>
public sealed record SourceStatusAnswer(
    string Source,
    IReadOnlyList<SourceStatusLight> Lights,
    int SearchLimit = 0,
    int SearchRemaining = -1,
    int DownloadLimit = 0,
    int DownloadRemaining = -1,
    DateTimeOffset? ResetAtUtc = null,
    long LatencyMs = 0);

/// <summary>
/// F-M730: der DRITTE, schmale Vertrag — die Selbstauskunft einer Quelle. Betreiber-Entscheidung
/// 24.11.2026: „Auch das könnte man in die maintenance/status engine einbringen." Bis dahin baute die
/// Statusseite ihre Clients selbst und las die Zugangsdaten aus der Plugin-Konfiguration; jetzt fragt
/// sie jede Quelle, wie sie dasteht, und malt nur noch Kacheln.
/// <para>
/// Diesen Vertrag kann JEDE Quelle erfüllen — auch eine ohne Konto, denn „bin ich erreichbar" weiß sie
/// selbst am besten. Deshalb steht er hier neben der Konto-Pflege (<see cref="ISourceMaintenance"/>) und
/// nicht darin: Konto und Erreichbarkeit sind zwei verschiedene Fragen, und eine Quelle ohne Konto kann
/// die zweite beantworten, ohne die erste zu erfinden.
/// </para>
/// </summary>
public interface ISourceStatus
{
    /// <summary>Gets der Schlüssel dieser Quelle — derselbe Wert, den der Bestand führt.</summary>
    string Key { get; }

    /// <summary>
    /// Wie die Quelle dasteht: ihre Lichter und, wenn sie ein Konto führt, ihr Kontingent.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Selbstauskunft. Sie wirft nicht — ein Fehler ist ein rotes Licht.</returns>
    Task<SourceStatusAnswer> StatusAsync(CancellationToken ct);
}
