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

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// Result of a single subtitle upload attempt.
/// </summary>
public class UploadResult
{
    /// <summary>Gets a value indicating whether the upload succeeded.</summary>
    public bool Ok { get; private init; }

    /// <summary>Gets a value indicating whether the item was skipped (with reason).</summary>
    public bool SkippedItem { get; private init; }

    /// <summary>Gets the skip or error reason.</summary>
    public string? Reason { get; private init; }

    /// <summary>
    /// Optional raw response payload from the SubDL uploadSubtitle call.
    /// </summary>
    public System.Text.Json.JsonDocument? Response { get; init; }

    /// <summary>
    /// F-M488: the id the SOURCE gave our upload, when it gave one. SubDL fills it from its response,
    /// the second source from the read-back of its episode page. Null means "no id known" — it is never
    /// guessed, because the store would then vouch for a row that does not exist.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// F-M603 (Operator-Entscheidung 18.11.2026, Weg „A"): die Quelle hat den Versuch ANGENOMMEN, aber
    /// keine verwertbare Kennung zurückgegeben. Gemessen an moviesubtitles.org: <c>add-movie.php</c>
    /// antwortet <c>200</c>, die angelegte Filmnummer ist danach nicht auslesbar — und ohne Marke blieb
    /// das Paar für immer offen. Die Datei kehrte in JEDEN Lauf zurück, wurde erneut entpackt und
    /// endete mit 0 Uploads (18.11.2026, „The End Of Oak Street": 209 „still open"-Zeilen, 176 SKIPs,
    /// 0 Uploads, ~20 s je Lauf).
    /// <para>
    /// Das Ergebnis ist bewusst KEIN <see cref="Ok"/>: die Statistik soll nicht „hochgeladen" behaupten,
    /// was die Seite nicht bestätigt hat. Es ist ein SKIP mit Beleg — die Marke wird geschrieben, das
    /// Paar ist damit entschieden, und der Lauf wiederholt den Versuch nicht.
    /// </para>
    /// </summary>
    public bool SourceAccepted { get; private init; }

    /// <summary>
    /// F-M609 (Operator-Anweisung 18.11.2026: „Wieso nehmen wir nicht alles was nicht akzetanz ist, als
    /// Ablehnung und schließen das paar?" — und „Verbindung zu / keine Antwort ist was anderes als kein
    /// positives verdict"): das URTEIL des Versuchs, in drei trennbaren Faellen.
    /// <para>
    /// <see cref="UploadVerdict.Refused"/> schliesst das Paar: es gibt eine Entscheidung, sei es die der
    /// Seite oder eine unserer eigenen Vorpruefungen (Staffel ausserhalb des Formulars, kein TMDb-Eintrag,
    /// Kennung nicht bespielbar). <see cref="UploadVerdict.NoVerdict"/> schliesst NICHTS: hier hat niemand
    /// entschieden — Timeout, Verbindungsfehler, 429, 5xx, Stundendeckel, Abbruch. Der naechste Lauf fragt
    /// neu, sonst waere die IP-Sperre vom 17.11.2026 (21:25-22:41) zum Dauerurteil fuer jeden Untertitel
    /// geworden, der in diesen 76 Minuten angefragt wurde.
    /// </para>
    /// </summary>
    public UploadVerdict Verdict { get; private init; } = UploadVerdict.Refused;

    /// <summary>
    /// F-M685: der Klartext der Quelle, wenn sie einen nennt — etwa
    /// „Duplicate upload: identical file already published". <see cref="Reason"/> bleibt daneben unser
    /// kurzer Grund; dieser hier ist der Satz der Seite, damit ein Urteil im Wortlaut der Quelle steht
    /// und nicht in unserer Übersetzung.
    /// </summary>
    public string? SourceText { get; init; }

    /// <summary>
    /// F-M685: war der Versuch nur VORÜBERGEHEND unmöglich (429, Stundendeckel)? Dann ist die Quelle
    /// „beschäftigt" — ein echter Ausgang, kein „weiß nicht". Ein Timeout, ein Verbindungsfehler oder
    /// ein Abbruch ist das NICHT; der bleibt ohne Antwort und damit ohne Urteil.
    /// </summary>
    public bool TemporarilyBusy { get; private init; }

    /// <summary>
    /// F-M685: hat niemand entschieden (Timeout, Verbindungsfehler, 5xx, Deckel, Abbruch)? Die Frage
    /// steht hier, damit kein Aufrufer die Werteliste des Urteils nachbauen muss.
    /// </summary>
    public bool Unanswered => Verdict == UploadVerdict.NoVerdict;

    /// <summary>
    /// F-M550: the source that actually answered. The log used to name no source at all, and the old
    /// <c>[SubDL]</c> prefix in front of an upload line read like "uploaded to SubDL" even when the
    /// upload went to tvsubtitles. Set by the chain where the source is known, not by its caller.
    /// </summary>
    public Configuration.SubtitleProvider? Source { get; set; }

    /// <summary>
    /// Creates a success result carrying the source's own id for the upload.
    /// </summary>
    /// <param name="sourceId">The id the source reported, or null.</param>
    /// <returns>The result.</returns>
    public static UploadResult Success(string? sourceId)
        => new() { Ok = true, SourceId = sourceId, Verdict = UploadVerdict.Accepted };

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static UploadResult Success() => new() { Ok = true, Verdict = UploadVerdict.Accepted };

    /// <summary>
    /// Creates a success result with the raw SubDL response attached.
    /// </summary>
    public static UploadResult Success(System.Text.Json.JsonDocument? response)
        => new() { Ok = true, Response = response, Verdict = UploadVerdict.Accepted };

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static UploadResult Failed(string reason) => new() { Ok = false, Reason = reason };

    /// <summary>
    /// F-M609/F-M685: keine Antwort — Timeout, Verbindungsfehler, 5xx, Deckel, Abbruch durch uns.
    /// KEIN Urteil der Quelle, deshalb bleibt das Paar offen und der naechste Lauf fragt neu.
    /// <para>
    /// F-M685: <paramref name="busy"/> trennt „vorübergehend nicht möglich" (429, Stundendeckel) von
    /// „niemand hat geantwortet" (Timeout, Netz). Nur das Erste ist ein Ausgang der Quelle.
    /// </para>
    /// </summary>
    /// <param name="reason">What went wrong.</param>
    /// <param name="busy">True nur fuer 429 und den Stundendeckel.</param>
    /// <returns>The result.</returns>
    public static UploadResult NoVerdict(string reason, bool busy = false)
        => new() { Ok = false, Reason = reason, Verdict = UploadVerdict.NoVerdict, TemporarilyBusy = busy };

    /// <summary>
    /// Creates a skip result.
    /// </summary>
    /// <param name="reason">Why the attempt was skipped.</param>
    /// <param name="sourceText">F-M685: der Klartext der Quelle, wenn sie einen genannt hat.</param>
    /// <returns>The result.</returns>
    public static UploadResult Skipped(string reason, string? sourceText = null)
        => new() { Ok = false, SkippedItem = true, Reason = reason, SourceText = sourceText };

    /// <summary>
    /// F-M603: the source took the attempt but returned no usable id — evidence enough to close the pair,
    /// but not a success to report.
    /// </summary>
    /// <param name="reason">Why the source named no id.</param>
    /// <returns>The result.</returns>
    public static UploadResult AcceptedWithoutId(string reason)
        => new() { Ok = false, SkippedItem = true, SourceAccepted = true, Reason = reason, Verdict = UploadVerdict.AcceptedWithoutId };
}

/// <summary>
/// F-M609: what a single upload attempt ended in. See <see cref="UploadResult.Verdict"/>.
/// </summary>
public enum UploadVerdict
{
    /// <summary>The source took the upload (2xx, or <c>status:true</c> at SubDL).</summary>
    Accepted,

    /// <summary>The source took the upload but named no id — evidence enough to close the pair (F-M603).</summary>
    AcceptedWithoutId,

    /// <summary>There is a decision: the site said no, or one of our own pre-checks did. Closes the pair.</summary>
    Refused,

    /// <summary>Nobody decided — timeout, connection, 429, 5xx, hourly cap, cancellation. Closes nothing.</summary>
    NoVerdict,
}