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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Sources;

/// <summary>
/// F-M682: die Grenze zwischen Plugin und Quelle. Vier Aufträge, kein fünfter.
/// <para>
/// Über diese Grenze gehen ausschließlich neutrale Daten: kein <c>HttpClient</c>, kein JSON, keine
/// Adresse einer Quelle, keine fremde Kennungsform. Wer eine Quelle kennt, steht HINTER der Grenze
/// (<c>Sources/&lt;Quelle&gt;/</c>); der Ablauf fragt nach Fähigkeiten und Aufträgen und nennt keinen
/// Quellennamen.
/// </para>
/// <para>
/// Die vier Aufträge sind absichtlich das GANZE Gespräch: <see cref="SearchAsync"/> (Anfrage trägt
/// alles, was wir über den Titel wissen, Antwort ist eine Kandidatenliste mit undurchsichtigem
/// Griff), <see cref="FetchAsync"/> (Griff → Bytes), <see cref="SendAsync"/> (Bytes → Urteil der
/// Quelle) und <see cref="ReportAsync"/> (fremde Kennung + Text → Urteil der Quelle). Was eine Quelle
/// zusätzlich kann, steht in <see cref="Capabilities"/> und wird ERFRAGT, nicht am Namen abgelesen.
/// </para>
/// </summary>
public interface ISourceEngine
{
    /// <summary>
    /// Gets was diese Engine kann — Schlüssel, Anzeigename, Fähigkeiten, Taktgrenzen und die
    /// Abbildung unserer ISO-Sprachen auf die Codes dieser Quelle.
    /// </summary>
    SourceCapabilities Capabilities { get; }

    /// <summary>
    /// F-M683: die Einstellungen, die DIESE Quelle erklärt — ihre eigenen Felder, in der Reihenfolge, in
    /// der sie erscheinen sollen.
    /// <para>
    /// Das ist die Antwort auf „was braucht diese Quelle, und was davon darf man einstellen": der
    /// Schlüssel (Speichername), eine deutsche Beschriftung, die Art, die Vorgabe, ob das Feld Pflicht
    /// ist, ein Hilfetext, Grenzen bei einer Zahl, die Möglichkeiten bei einer Auswahl und die
    /// Bedingung, unter der ein Feld überhaupt sichtbar ist. Die Felder sind eine EIGENSCHAFT und kein
    /// Auftrag — die vier Aufträge bleiben vier.
    /// </para>
    /// <para>
    /// Gespeichert wird jedes Feld in der eigenen Datei der Quelle
    /// (<c>&lt;Datenordner&gt;/sources/&lt;key&gt;.json</c>, F-M683), gelesen über
    /// <see cref="SourceSettings"/>; ein Geheimnis wird nie ausgeliefert.
    /// </para>
    /// </summary>
    IReadOnlyList<SourceField> Fields { get; }

    /// <summary>
    /// F-M682, Auftrag 1: sucht Untertitel zu einem Titel. Die Engine darf innerhalb EINES Auftrags
    /// beliebig viele Abrufe machen (Serie finden, Staffel, Folgenseite, Einträge) — die Grenze zählt
    /// Aufträge, nicht Anfragen.
    /// </summary>
    /// <param name="request">Alles, was wir über den Titel wissen.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Die Kandidaten; leer, wenn die Quelle nichts hat.</returns>
    Task<SourceSearchAnswer> SearchAsync(SourceSearchRequest request, CancellationToken ct);

    /// <summary>
    /// F-M682, Auftrag 2: holt die Bytes hinter einem Griff.
    /// </summary>
    /// <param name="request">Griff, bei Behältern die Folge und der Mitgliedsname.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bytes, Name im Behälter, fremde Kennung und Inhalts-Hash.</returns>
    Task<SourceFetchAnswer> FetchAsync(SourceFetchRequest request, CancellationToken ct);

    /// <summary>
    /// F-M682, Auftrag 3: gibt Untertitel ab — einzeln oder als Behälter.
    /// </summary>
    /// <param name="request">Bytes, Sprache, Variante, Releasename, Ziel, Kommentar, Umfang.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Der eine Ausgang: angenommen, abgelehnt, beschäftigt oder fehlgeschlagen, mit Grund und — falls die Quelle eine gibt — ihrer Kennung.</returns>
    Task<SourceSendAnswer> SendAsync(SourceSendRequest request, CancellationToken ct);

    /// <summary>
    /// F-M682, Auftrag 4: meldet einen Defekt an einer fremden Kennung.
    /// </summary>
    /// <param name="request">Fremde Kennung, Art des Defekts, Text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Derselbe eine Ausgang wie beim Senden: angenommen, abgelehnt, beschäftigt (429) oder fehlgeschlagen.</returns>
    Task<SourceReportAnswer> ReportAsync(SourceReportRequest request, CancellationToken ct);
}

/// <summary>
/// F-M682: was das Plugin der Engine reicht. Die Engine legt sich KEINEN eigenen
/// <see cref="System.Net.Http.HttpClient"/> an und keinen eigenen Cookie-Container: der Client kommt
/// von außen, damit jede Anfrage über denselben Weg geht wie der Rest des Laufs (genau daran scheiterte
/// der tvst-Staffel-Abruf: eigener Client, eigener Cookie-Container, Testsuite lief auf 0 Abrufe).
/// </summary>
/// <param name="Http">Der HttpClient des Laufs — gereicht, nie selbst gebaut.</param>
/// <param name="Logger">Der Logger des Laufs.</param>
/// <param name="Clock">Der Zeitgeber des Laufs (Fristen, RetryAfter).</param>
/// <param name="ArchiveCache">Der gemeinsame Archiv-Cache, oder null.</param>
/// <param name="Subdl">Der bereits aufgebaute SubDL-Client samt Zugang, wenn es ihn gibt.</param>
/// <param name="Tvst">Der bereits aufgebaute tvst-Client, wenn es ihn gibt.</param>
/// <param name="Msub">Der bereits aufgebaute Film-Client, wenn es ihn gibt.</param>
/// <param name="DataFolder">
/// F-M683: der Datenordner des Plugins — dort liegen die Einstellungsdateien der Quellen
/// (<c>sources/&lt;key&gt;.json</c>). Null heißt „kein Ordner bekannt": dann gelten die Vorgaben der
/// Erklärung und es wird nichts geschrieben.
/// </param>
public sealed record SourceEngineContext(
    HttpClient Http,
    ILogger Logger,
    TimeProvider Clock,
    ArchiveDiskCache? ArchiveCache = null,
    ScribeApiClient? Subdl = null,
    TvSubtitlesClient? Tvst = null,
    MovieSubtitlesClient? Msub = null,
    string? DataFolder = null);

/// <summary>
/// F-M682: was eine Quelle kann — erfragt, nicht am Namen abgelesen.
/// <para>
/// Die Werte beschreiben die Engine SO, WIE DAS PLUGIN SIE FÜR DIESEN LAUF VERDRAHTET HAT: die
/// Fähigkeiten der Quelle und der Schalter des Betreibers sind darin schon verrechnet
/// (<see cref="CanSeasonPacks"/> ist genau die Frage „steht diese Quelle im Pack-Slot?"). Damit fragt
/// der Ablauf eine Fähigkeit und vergleicht keinen Quellennamen.
/// </para>
/// </summary>
public sealed record SourceCapabilities
{
    /// <summary>Gets der Schlüssel der Quelle — derselbe Wert, den der Bestand in seiner Quellen-Spalte führt.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets der Anzeigename für Log und Oberfläche.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets kann die Quelle eigene Untertitel annehmen?</summary>
    public bool CanUpload { get; init; }

    /// <summary>Gets hat die Quelle einen Meldeweg für Defekte?</summary>
    public bool CanReport { get; init; }

    /// <summary>Gets trennt die Quelle die gehörlosenfreundliche Fassung von der normalen?</summary>
    public bool CanHearingImpaired { get; init; }

    /// <summary>
    /// Gets kann die Quelle Staffel-Behälter überhaupt annehmen? Das ist die Fähigkeit der QUELLE,
    /// unabhängig vom Schalter des Betreibers.
    /// </summary>
    public bool CanOfferSeasonPacks { get; init; }

    /// <summary>
    /// Gets steht diese Quelle in DIESEM Lauf im Pack-Slot? Fähigkeit UND Schalter — die Antwort, die
    /// der Ablauf braucht, wenn er keinen Quellennamen vergleichen will (F-M654/F-M680).
    /// </summary>
    public bool CanSeasonPacks { get; init; }

    /// <summary>Gets legt die Quelle einen fehlenden Titel selbst an?</summary>
    public bool CanCreateTitle { get; init; }

    /// <summary>F-M625: führt die Quelle ein Kontingent und antwortet mit Erschöpfung?</summary>
    public bool CanQuota { get; init; }

    /// <summary>Gets findet die Quelle über eine IMDb-Kennung?</summary>
    public bool SearchesByImdb { get; init; }

    /// <summary>Gets findet die Quelle über eine TMDb-Kennung?</summary>
    public bool SearchesByTmdb { get; init; }

    /// <summary>Gets findet die Quelle über einen Namen?</summary>
    public bool SearchesByName { get; init; }

    /// <summary>Gets findet die Quelle über einen Inhalts-Hash?</summary>
    public bool SearchesByHash { get; init; }

    /// <summary>Gets die Taktgrenzen, die die Quelle selbst nennt.</summary>
    public SourcePace Pace { get; init; } = new();

    /// <summary>
    /// Gets die Abbildung unserer ISO-639-2-Kennzeichen (<c>DEU</c>) auf den Code DIESER Quelle
    /// (<c>DE</c>, <c>de</c>) — als Abfrage über die eigene Sprachbrücke der Quelle, damit es keine
    /// zweite Liste gibt, die auseinanderlaufen kann.
    /// </summary>
    public Func<string, string?> LanguageCode { get; init; } = _ => null;

    /// <summary>
    /// F-M682: kann diese Engine einen Auftrag dieser Art überhaupt beantworten? Eine fehlende
    /// Fähigkeit wird nie aufgerufen; die Frage steht hier, damit der Ablauf sie stellen kann, ohne
    /// den Quellennamen zu kennen.
    /// </summary>
    /// <param name="key">Ein Suchschlüssel.</param>
    /// <returns>Wahr, wenn die Quelle diesen Schlüssel verwerten kann.</returns>
    public bool SearchesBy(SourceSearchKey key) => key switch
    {
        SourceSearchKey.Imdb => SearchesByImdb,
        SourceSearchKey.Tmdb => SearchesByTmdb,
        SourceSearchKey.Name => SearchesByName,
        SourceSearchKey.Hash => SearchesByHash,
        _ => false,
    };
}

/// <summary>F-M682: die Schlüssel, mit denen eine Quelle suchen kann.</summary>
public enum SourceSearchKey
{
    /// <summary>IMDb-Kennung (<c>tt…</c>).</summary>
    Imdb,

    /// <summary>TMDb-Nummer.</summary>
    Tmdb,

    /// <summary>Name des Titels (Original oder übersetzt).</summary>
    Name,

    /// <summary>Inhalts-Hash der Datei (osHash).</summary>
    Hash,
}

/// <summary>
/// F-M682: die Taktgrenzen einer Quelle, wie die Quelle selbst sie nennt. 0 heißt „die Quelle nennt
/// keine Grenze" — nicht „unbegrenzt, bitte drauflos".
/// </summary>
/// <param name="UploadsPerHour">Abgaben je Stunde, 0 wenn die Quelle keine nennt.</param>
/// <param name="MinCallPauseSeconds">Mindestabstand zwischen zwei Anfragen, 0 wenn keiner bekannt ist.</param>
public sealed record SourcePace(int UploadsPerHour = 0, double MinCallPauseSeconds = 0);

/// <summary>
/// F-M682, Auftrag 1: alles, was wir über den Titel wissen. Die Anfrage ist die EINZIGE Beschreibung
/// des Titels, die die Engine zu sehen bekommt — sie liest weder Einstellungen noch Bestand.
/// </summary>
public sealed record SourceSearchRequest
{
    /// <summary>Gets die IMDb-Kennung, wenn bekannt.</summary>
    public string? ImdbId { get; init; }

    /// <summary>Gets die TMDb-Nummer, wenn bekannt.</summary>
    public string? TmdbId { get; init; }

    /// <summary>Gets der Name des Titels.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets der Originalname, wenn er sich vom Namen unterscheidet.</summary>
    public string OriginalName { get; init; } = string.Empty;

    /// <summary>Gets das Jahr, wenn bekannt.</summary>
    public int? Year { get; init; }

    /// <summary>Gets Sucht die Quelle eine Serie (wahr) oder einen Film?</summary>
    public bool IsSeries { get; init; }

    /// <summary>Gets die Staffel, wenn es eine Serie ist.</summary>
    public int? Season { get; init; }

    /// <summary>Gets die Folge, wenn eine bestimmte gemeint ist.</summary>
    public int? Episode { get; init; }

    /// <summary>Gets der Inhalts-Hash der Datei (osHash), wenn die Quelle damit sucht.</summary>
    public string? OsHash { get; init; }

    /// <summary>
    /// F-M686: die Nummer, die DIESES Medium bei DIESER Quelle schon hat (Bestand), wenn bekannt.
    /// <para>
    /// Sie ist der Grund, warum die Suche nicht raten muss: der Ablauf liest sie aus seinem eigenen
    /// Bestand (F-M507) und reicht sie als Zeichenkette herüber — die Engine vergleicht sie mit ihrer
    /// eigenen Form und sucht nur, wenn sie fehlt. Keine Quelle muss dafür den Store kennen.
    /// </para>
    /// </summary>
    public string? MediaNumber { get; init; }

    /// <summary>
    /// F-M686: die Suchbreite — wie viele Treffer die Quelle je Sprache höchstens beisteuern soll
    /// (0 = die Quelle entscheidet selbst). Sie ist der EINE Einstellwert der Korrekturversuche
    /// (F-M95/F-M331): wie viele Kandidaten gesucht werden und wie viele der Lauf höchstens zieht.
    /// </summary>
    public int EarlyStop { get; init; }

    /// <summary>Gets die gewünschten Sprachen in unserer ISO-639-2-Schreibweise; leer heißt „egal".</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Gets die gewünschte Variante: wahr = nur HI, falsch = nur normal, null = egal.</summary>
    public bool? HearingImpaired { get; init; }

    /// <summary>Gets sucht der Ablauf einen Behälter (Staffelpack)?</summary>
    public bool WantsPack { get; init; }
}

/// <summary>
/// F-M682, Auftrag 1: ein Kandidat der Quelle. Der Griff ist undurchsichtig — nur die Engine, die ihn
/// ausgegeben hat, weiß, was darin steht.
/// <para>
/// F-M686: die Felder NACH den vier Pflichtangaben sind das, was der Ablauf zum Ranken, Melden und
/// Buchen braucht — und allein die QUELLE füllt sie. Sie sind neutral benannt, weil der Ablauf sie
/// liest, ohne zu wissen, wer sie hergibt: <see cref="EntryId"/> ist die Kennung, unter der der
/// Bestand den Eintrag führt, <see cref="Address"/> die Adresse, unter der er bei der Quelle liegt,
/// <see cref="ReportId"/> die Kennung seines Meldewegs, und <see cref="Members"/> der Inhalt eines
/// Behälters. Eine Quelle, die etwas davon nicht hat, lässt es leer — geraten wird nichts.
/// </para>
/// </summary>
/// <param name="Handle">Undurchsichtiger Griff für <see cref="ISourceEngine.FetchAsync"/>.</param>
/// <param name="Language">
/// Sprache des Eintrags in der Schreibweise, die DIESE Quelle führt, groß geschrieben (SubDL: sein
/// Kürzel <c>EN</c>, tvsubtitles.net: ISO 639-2 <c>ENG</c>). F-M686: der Ablauf prüft sie gegen seine
/// eigene Sprachliste — die Schreibweise einer Quelle nachträglich zu vereinheitlichen hieße, jede
/// Sprachangabe im Bestand umzuschreiben.
/// </param>
/// <param name="HearingImpaired">Wahr für die gehörlosenfreundliche Fassung.</param>
/// <param name="ReleaseName">Der Releasename, wie die Quelle ihn schreibt.</param>
/// <param name="IsPack">Wahr, wenn der Kandidat ein Behälter ist (mehrere Folgen in einer Datei).</param>
public sealed record SourceCandidate(
    string Handle, string Language, bool HearingImpaired, string ReleaseName, bool IsPack)
{
    /// <summary>Gets die Staffel, wenn die Quelle sie nennt (0 = keine).</summary>
    public int Season { get; init; }

    /// <summary>Gets die Folge, wenn die Quelle sie nennt (0 = keine).</summary>
    public int Episode { get; init; }

    /// <summary>Gets F-M686: die erste Folge eines Bereichs-Behälters (0 = keine Angabe).</summary>
    public int EpisodeFrom { get; init; }

    /// <summary>Gets F-M686: die letzte Folge eines Bereichs-Behälters (0 = keine Angabe).</summary>
    public int EpisodeEnd { get; init; }

    /// <summary>Gets F-M686: die Bildrate, die der Eintrag nennt (0 = keine Angabe).</summary>
    public double Fps { get; init; }

    /// <summary>Gets F-M686: die Zahl der Abrufe, die die Quelle nennt (0 = keine Angabe; Tie-Breaker).</summary>
    public long Downloads { get; init; }

    /// <summary>Gets F-M686: der Uploader, wenn die Quelle ihn nennt (nur für Logzeilen).</summary>
    public string? Author { get; init; }

    /// <summary>Gets F-M686: der Name der Datei bei der Quelle, wenn er sich vom Releasenamen unterscheidet.</summary>
    public string? OriginalName { get; init; }

    /// <summary>
    /// Gets F-M686: die Kennung, unter der der Bestand diesen Eintrag führt (<c>originId</c>,
    /// <c>subdlDownloadId</c>, <c>tvSubtitlesDownloadId</c>, <c>moviesubtitlesDownloadId</c>). Sie ist
    /// damit die Marke, an der QA und Bericht denselben Eintrag wiedererkennen.
    /// </summary>
    public string? EntryId { get; init; }

    /// <summary>
    /// Gets F-M686: die Adresse, unter der der Eintrag bei der Quelle liegt. Der Ablauf benutzt sie
    /// nur als Schlüssel („diesen Eintrag habe ich schon versucht") und reicht sie als Griff zurück —
    /// er baut sie nicht und legt sie nicht aus.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// Gets F-M686: die Kennung, die der MELDEWEG der Quelle braucht (der Ablauf meldet über
    /// <see cref="ISourceEngine.ReportAsync"/> mit genau dieser Zeichenkette). Leer, wenn die Quelle
    /// keinen Meldeweg mit Kennung hat.
    /// </summary>
    public string? ReportId { get; init; }

    /// <summary>
    /// Gets F-M686: die Mitglieder eines Behälters, in der Reihenfolge der Quelle. Nur diese Liste
    /// erlaubt es dem Ablauf, den BEHÄLTER gegen den Einzelabruf zu rechnen (F-M384) und die eine
    /// Folge daraus zu wählen (F-M215), ohne die fremde Adresse je zu sehen.
    /// </summary>
    public IReadOnlyList<SourceMember> Members { get; init; } = Array.Empty<SourceMember>();
}

/// <summary>
/// F-M686: ein Mitglied eines Behälters, wie die Suche ihn meldet — Name, Staffel, Folge und die
/// HI-Marke DIESER Datei (sie ist oft die andere als die des Behälters, F-M260).
/// </summary>
/// <param name="FileName">Der Name der Datei im Behälter.</param>
/// <param name="Season">Die Staffel, zu der das Mitglied gehört (0 = keine Angabe).</param>
/// <param name="Episode">Die Folge, zu der das Mitglied gehört (0 = keine Angabe).</param>
/// <param name="HearingImpaired">Wahr, wenn DIESE Datei die gehörlosenfreundliche Fassung ist.</param>
public sealed record SourceMember(string FileName, int Season, int Episode, bool HearingImpaired);

/// <summary>F-M682, Auftrag 1: die Antwort der Suche — Kandidaten und, wenn es schiefging, der Grund.</summary>
/// <param name="Candidates">Die Kandidaten in der Reihenfolge der Quelle.</param>
/// <param name="Fault">Der normalisierte Fehler, oder null.</param>
public sealed record SourceSearchAnswer(
    IReadOnlyList<SourceCandidate> Candidates, SourceFaultInfo? Fault = null)
{
    /// <summary>Eine leere, saubere Antwort.</summary>
    public static SourceSearchAnswer Empty { get; } = new(Array.Empty<SourceCandidate>());

    /// <summary>Eine Antwort, die nur den Grund trägt.</summary>
    /// <param name="fault">Der normalisierte Fehler.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceSearchAnswer Because(SourceFaultInfo fault)
        => new(Array.Empty<SourceCandidate>(), fault);
}

/// <summary>F-M682, Auftrag 2: der Griff, den die Suche ausgegeben hat.</summary>
/// <param name="Handle">Der undurchsichtige Griff.</param>
/// <param name="Episode">Bei Behältern die gewünschte Folge, sonst null.</param>
/// <param name="MemberName">Bei Behältern der Mitgliedsname, sonst null.</param>
public sealed record SourceFetchRequest(string Handle, int? Episode = null, string? MemberName = null)
{
    /// <summary>
    /// F-M686: die Staffel, wenn der Behälter über Staffel UND Folge aufgelöst werden muss — dieselbe
    /// Angabe, die der Abruf bisher mitgab.
    /// </summary>
    public int Season { get; init; }

    /// <summary>
    /// F-M686: die Sprache, für die geholt wird — für Quellen, die in EINER Datei mehrere Fassungen
    /// führen und die richtige über ihr Sprachkürzel im Dateinamen wählen (moviesubtitles.org bei
    /// geschnittenen Filmen). Leer heißt „egal, die Quelle nimmt die erste".
    /// </summary>
    public string Language { get; init; } = string.Empty;
}

/// <summary>
/// F-M682, Auftrag 2: die Bytes hinter einem Griff.
/// </summary>
/// <param name="Bytes">Der Inhalt, unverändert.</param>
/// <param name="FileName">Der Name des Untertitels im Behälter (oder der eigene Name).</param>
/// <param name="RemoteId">Die fremde Kennung, die als <c>reference</c> im Bestand landet.</param>
/// <param name="ContentHash">Der Inhalts-Hash über die Bytes (SHA-256, hex).</param>
/// <param name="Fault">Der normalisierte Fehler, oder null.</param>
public sealed record SourceFetchAnswer(
    byte[] Bytes, string FileName, string RemoteId, string ContentHash, SourceFaultInfo? Fault = null)
{
    /// <summary>Eine Antwort, die nur den Grund trägt.</summary>
    /// <param name="fault">Der normalisierte Fehler.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceFetchAnswer Because(SourceFaultInfo fault)
        => new(Array.Empty<byte>(), string.Empty, string.Empty, string.Empty, fault);

    /// <summary>Gets der Status, den die Quelle auf den Abruf genannt hat (0 = kein Abruf).</summary>
    public int Status { get; init; }

    /// <summary>
    /// F-M686: eine echte leere Antwort der Quelle — ein 200, das nichts trug. Sie ist von „kein
    /// Abruf" (Status 0) und von einer Absage zu unterscheiden; der Ablauf wiederholt genau diesen
    /// Fall (F-M455).
    /// </summary>
    public bool GenuineEmpty => Status == 200 && Bytes.Length == 0;

    /// <summary>Hat die Antwort verwertbare Bytes?</summary>
    public bool HasBytes => Bytes.Length > 0 && Fault is null;
}

/// <summary>F-M682, Auftrag 3: das Ziel einer Abgabe. Die Quelle übersetzt es in ihre eigene Adresse.</summary>
/// <param name="IsSeries">Wahr für eine Serie, falsch für einen Film.</param>
/// <param name="Title">Der Titel, wie er geschrieben steht.</param>
/// <param name="Year">Das Jahr, wenn bekannt.</param>
/// <param name="ImdbId">Die IMDb-Kennung, wenn bekannt.</param>
/// <param name="TmdbId">Die TMDb-Nummer, wenn bekannt.</param>
/// <param name="Season">Die Staffel bei einer Serie.</param>
/// <param name="Episode">Die Folge beim Einzelupload, null beim Behälter.</param>
public sealed record SourceSendTarget(
    bool IsSeries,
    string Title = "",
    int? Year = null,
    string? ImdbId = null,
    string? TmdbId = null,
    int? Season = null,
    int? Episode = null);

/// <summary>F-M682, Auftrag 3: ein Mitglied eines Behälters.</summary>
/// <param name="FileName">Der Name, unter dem das Mitglied im Behälter steht bzw. gesendet wird.</param>
/// <param name="Episode">Die Folge, zu der das Mitglied gehört.</param>
/// <param name="Bytes">Der Inhalt.</param>
public sealed record SourcePackMember(string FileName, int Episode, byte[] Bytes);

/// <summary>
/// F-M682, Auftrag 3: eine Abgabe. Der Kommentar ist der TEXT DES PLUGINS — die Engine erfindet keinen
/// und schreibt weder Version noch Client-Namen hinein.
/// </summary>
public sealed record SourceSendRequest
{
    /// <summary>Gets die Bytes: beim Einzelupload der Untertitel, beim Behälter der fertige Behälter.</summary>
    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>Gets der Dateiname im Behälter der Quelle.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Gets die Sprache in unserer ISO-639-2-Schreibweise.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets wahr für die gehörlosenfreundliche Fassung.</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Gets der Releasename (beim Behälter der Pack-Name).</summary>
    public string ReleaseName { get; init; } = string.Empty;

    /// <summary>Gets das Ziel: Serie oder Film, Staffel und Folge.</summary>
    public SourceSendTarget Target { get; init; } = new(false);

    /// <summary>Gets der Kommentar, den das PLUGIN gebaut hat.</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>Gets wahr, wenn ein Behälter abgegeben wird (dann zählt <see cref="Members"/>).</summary>
    public bool AsPack { get; init; }

    /// <summary>Gets die Mitglieder des Behälters — für die Quellen, die je Folge einen Teil erwarten.</summary>
    public IReadOnlyList<SourcePackMember> Members { get; init; } = Array.Empty<SourcePackMember>();
}

/// <summary>
/// F-M685: der EINE Ausgang der beiden schreibenden Aufträge — für Senden UND Melden derselbe.
/// <para>
/// Bis hierher gab es zwei Aussagen für denselben Sachverhalt: beim Senden war „beschäftigt" nur ein
/// Nebenfeld (ein 429 kam als „ohne Urteil" mit <c>Fault = RateLimited</c> zurück), beim Melden war
/// „vertagt" ein eigener Ausgang. Wer die Antwort las, musste beides kennen — und hielt einen 429
/// sonst für „weiß nicht". Jetzt sind es vier Werte, und der Ausgang allein sagt, was zu tun ist.
/// </para>
/// <para>
/// <see cref="Accepted"/> und <see cref="Rejected"/> sind Entscheidungen der Quelle;
/// <see cref="Busy"/> ist „später nochmal" (die Wartezeit steht am Ausgang, nicht im Fehler);
/// <see cref="Failed"/> ist „nicht als Urteil buchen, aber protokollieren" — Netz, Anmeldung oder
/// Unbekanntes. Suche und Hole behalten ihren Fehlerweg (<see cref="SourceFaultInfo"/>).
/// </para>
/// </summary>
public enum SourceOutcome
{
    /// <summary>Die Quelle hat angenommen.</summary>
    Accepted,

    /// <summary>Die Quelle hat endgültig abgelehnt — der Grund steht im Klartext der Quelle.</summary>
    Rejected,

    /// <summary>Vorübergehend nicht möglich — <see cref="SourceSendAnswer.RetryAfterSeconds"/> sagt, wie lange.</summary>
    Busy,

    /// <summary>Netz, Anmeldung oder Unbekanntes — kein Urteil über die Sache.</summary>
    Failed,
}

/// <summary>
/// F-M682/F-M685, Auftrag 3: Antwort auf eine Abgabe.
/// </summary>
/// <param name="Outcome">Der eine Ausgang: angenommen, abgelehnt, beschäftigt oder fehlgeschlagen.</param>
/// <param name="Reason">Der Grund im Klartext der Quelle, wenn sie einen nennt.</param>
/// <param name="RetryAfterSeconds">
/// F-M685: die Wartezeit bei <see cref="SourceOutcome.Busy"/> — aus <c>Retry-After</c> bzw. der
/// Reset-Zeit der Quelle, <c>null</c>, wenn die Quelle keine nennt. Sie steht AM AUSGANG; der
/// Fehlerweg von Suche und Hole ist davon unberührt.
/// </param>
/// <param name="SubmissionId">
/// Die eigene Einsendungs-Kennung, WENN die Quelle eine zurückgibt. SubDL antwortet mit
/// <c>{"success": true}</c>, tvsubtitles.net mit einer Seite, auf der „thank" steht — beide nennen
/// keine. Das Feld ist deshalb optional und es gibt KEINE Spalte dafür im Bestand.
/// </param>
public sealed record SourceSendAnswer(
    SourceOutcome Outcome, string? Reason = null, int? RetryAfterSeconds = null, string? SubmissionId = null)
{
    /// <summary>F-M685: die Quelle hat angenommen.</summary>
    /// <param name="submissionId">Ihre Einsendungs-Kennung, wenn sie eine nennt.</param>
    /// <param name="reason">Ihr Text, wenn sie einen mitgibt.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceSendAnswer Accepted(string? submissionId = null, string? reason = null)
        => new(SourceOutcome.Accepted, reason, null, submissionId);

    /// <summary>F-M685: die Quelle hat endgültig abgelehnt.</summary>
    /// <param name="reason">Ihr Klartext.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceSendAnswer Rejected(string reason) => new(SourceOutcome.Rejected, reason);

    /// <summary>F-M685: vorübergehend nicht möglich.</summary>
    /// <param name="retryAfterSeconds">Die genannte Wartezeit, oder null.</param>
    /// <param name="reason">Ihr Klartext, wenn sie einen mitgibt.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceSendAnswer Busy(int? retryAfterSeconds, string? reason = null)
        => new(SourceOutcome.Busy, reason, retryAfterSeconds);

    /// <summary>F-M685: Netz, Anmeldung oder Unbekanntes.</summary>
    /// <param name="reason">Der Grund, wenn einer bekannt ist.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceSendAnswer Failed(string? reason) => new(SourceOutcome.Failed, reason);
}

/// <summary>F-M682, Auftrag 4: die Art des Defekts, in neutralen Worten.</summary>
public enum SourceReportKind
{
    /// <summary>Der Untertitel fehlt bzw. die Datei ist nicht mehr da.</summary>
    Missing,

    /// <summary>Der Inhalt passt nicht zum Titel.</summary>
    WrongContent,

    /// <summary>Die Sprache ist falsch ausgezeichnet.</summary>
    WrongLanguage,

    /// <summary>Der Untertitel ist nicht lesbar (kaputt, leer, unbrauchbar).</summary>
    Unplayable,

    /// <summary>Ein anderer Defekt.</summary>
    Other,
}

/// <summary>F-M682, Auftrag 4: eine Meldung an die Quelle.</summary>
/// <param name="RemoteId">Die fremde Kennung des Eintrags.</param>
/// <param name="Kind">Die Art des Defekts, in neutralen Worten.</param>
/// <param name="Text">Der Meldungstext, wie das Plugin ihn schreibt.</param>
public sealed record SourceReportRequest(string RemoteId, SourceReportKind Kind, string Text);

/// <summary>
/// F-M682/F-M685, Auftrag 4: Antwort auf eine Meldung.
/// </summary>
/// <param name="Outcome">Derselbe eine Ausgang wie beim Senden.</param>
/// <param name="Reason">Der Grund im Klartext der Quelle, wenn sie einen nennt.</param>
/// <param name="RetryAfterSeconds">F-M685: die Wartezeit bei <see cref="SourceOutcome.Busy"/>, sonst null.</param>
/// <param name="MessageId">
/// Die Kennung, die die Quelle der Meldung gibt, WENN sie eine gibt. Heute gibt keine der drei Quellen
/// eine zurück; das Feld ist optional und es gibt KEINE Spalte dafür.
/// </param>
public sealed record SourceReportAnswer(
    SourceOutcome Outcome, string? Reason = null, int? RetryAfterSeconds = null, string? MessageId = null)
{
    /// <summary>F-M685: die Quelle hat die Meldung angenommen.</summary>
    /// <param name="messageId">Ihre Kennung, wenn sie eine nennt.</param>
    /// <param name="reason">Ihr Text, wenn sie einen mitgibt.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceReportAnswer Accepted(string? messageId = null, string? reason = null)
        => new(SourceOutcome.Accepted, reason, null, messageId);

    /// <summary>F-M685: die Quelle hat die Meldung endgültig abgelehnt.</summary>
    /// <param name="reason">Ihr Klartext.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceReportAnswer Rejected(string reason) => new(SourceOutcome.Rejected, reason);

    /// <summary>F-M685: vorübergehend nicht möglich.</summary>
    /// <param name="retryAfterSeconds">Die genannte Wartezeit, oder null.</param>
    /// <param name="reason">Ihr Klartext, wenn sie einen mitgibt.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceReportAnswer Busy(int? retryAfterSeconds, string? reason = null)
        => new(SourceOutcome.Busy, reason, retryAfterSeconds);

    /// <summary>F-M685: Netz, Anmeldung oder Unbekanntes.</summary>
    /// <param name="reason">Der Grund, wenn einer bekannt ist.</param>
    /// <returns>Die Antwort.</returns>
    public static SourceReportAnswer Failed(string? reason) => new(SourceOutcome.Failed, reason);
}

/// <summary>
/// F-M682: die normalisierten Fehler der Grenze. Jede Engine übersetzt ihre eigenen Zustände hierher,
/// damit der Ablauf nicht die Fehlersprache einer Quelle lernen muss.
/// </summary>
public enum SourceFault
{
    /// <summary>Kein Fehler.</summary>
    None,

    /// <summary>Die Quelle kennt den Eintrag nicht (mehr).</summary>
    NotFound,

    /// <summary>Die Quelle bremst — <see cref="SourceFaultInfo.RetryAfterSeconds"/> sagt, wie lange.</summary>
    RateLimited,

    /// <summary>Der Zugang fehlt oder gilt nicht mehr.</summary>
    AuthFailed,

    /// <summary>Die Quelle hat abgelehnt, mit Grund.</summary>
    Rejected,

    /// <summary>Vorübergehend nicht erreichbar (5xx, Verbindungsfehler, Abbruch).</summary>
    Transient,

    /// <summary>Die Anfrage trägt nichts, womit DIESE Quelle suchen kann.</summary>
    NoSearchBase,
}

/// <summary>
/// F-M682: ein normalisierter Fehler samt Grund und — bei <see cref="SourceFault.RateLimited"/> —
/// der Wartezeit, die die Quelle genannt hat.
/// </summary>
/// <param name="Kind">Der normalisierte Fehler.</param>
/// <param name="Reason">Der Grund, in neutralen Worten.</param>
/// <param name="RetryAfterSeconds">Die genannte Wartezeit, wenn die Quelle eine nennt.</param>
public sealed record SourceFaultInfo(SourceFault Kind, string? Reason = null, int? RetryAfterSeconds = null)
{
    /// <summary>Die Quelle kennt den Eintrag nicht.</summary>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der Fehler.</returns>
    public static SourceFaultInfo NotFound(string? reason = null) => new(SourceFault.NotFound, reason);

    /// <summary>Die Quelle bremst.</summary>
    /// <param name="retryAfterSeconds">Die genannte Wartezeit.</param>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der Fehler.</returns>
    public static SourceFaultInfo RateLimited(int? retryAfterSeconds, string? reason = null)
        => new(SourceFault.RateLimited, reason, retryAfterSeconds);

    /// <summary>Der Zugang fehlt oder gilt nicht mehr.</summary>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der Fehler.</returns>
    public static SourceFaultInfo AuthFailed(string? reason = null) => new(SourceFault.AuthFailed, reason);

    /// <summary>Die Quelle hat abgelehnt.</summary>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der Fehler.</returns>
    public static SourceFaultInfo Rejected(string? reason = null) => new(SourceFault.Rejected, reason);

    /// <summary>Vorübergehend nicht erreichbar.</summary>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der Fehler.</returns>
    public static SourceFaultInfo Transient(string? reason = null) => new(SourceFault.Transient, reason);

    /// <summary>Die Anfrage trägt nichts, womit diese Quelle suchen kann.</summary>
    /// <param name="reason">Der Grund.</param>
    /// <returns>Der Fehler.</returns>
    public static SourceFaultInfo NoSearchBase(string? reason = null) => new(SourceFault.NoSearchBase, reason);
}

/// <summary>
/// F-M682: kleine, quellenunabhängige Rechnungen der Grenze — der Inhalts-Hash und die Schreibweise
/// einer Sprache. Beides steht hier, damit keine Engine ihre eigene Fassung mitbringt.
/// </summary>
public static class SourceNeutral
{
    /// <summary>
    /// Der Inhalts-Hash der Grenze: SHA-256 über die Bytes, hex. Bewusst NICHT der Hash des Stores —
    /// die Engine kennt den Store nicht (Grenzregel c).
    /// </summary>
    /// <param name="bytes">Der Inhalt.</param>
    /// <returns>64 Hex-Zeichen, oder leer bei leerem Inhalt.</returns>
    public static string ContentHash(byte[]? bytes)
        => bytes is { Length: > 0 }
            ? Convert.ToHexStringLower(SHA256.HashData(bytes))
            : string.Empty;

    /// <summary>
    /// Die eine Schreibweise, in der die Grenze eine Sprache führt: ISO 639-2 in Großbuchstaben
    /// (F-M557). Der Code der Quelle bleibt dahinter.
    /// </summary>
    /// <param name="code">Ein Sprachkennzeichen, gleich in welcher Schreibweise.</param>
    /// <returns>Das ISO-Kennzeichen in Großbuchstaben, oder leer.</returns>
    public static string IsoLanguage(string? code)
        => string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : (Language.LanguageMapper.Canonical(code) ?? string.Empty).ToUpperInvariant();

    /// <summary>
    /// F-M682: die Sekunden bis zu einer Frist, die die Quelle genannt hat — nie negativ, nie geraten.
    /// </summary>
    /// <param name="resetUtc">Die Frist, oder null.</param>
    /// <param name="clock">Der Zeitgeber des Laufs.</param>
    /// <returns>Die Wartezeit in Sekunden, oder null.</returns>
    public static int? SecondsUntil(DateTime? resetUtc, TimeProvider clock)
    {
        if (resetUtc is not { } frist)
        {
            return null;
        }

        double sekunden = (frist - clock.GetUtcNow().UtcDateTime).TotalSeconds;
        return sekunden <= 0 ? 0 : (int)Math.Ceiling(sekunden);
    }

    /// <summary>
    /// F-M682: die Sprachliste einer Anfrage in der Schreibweise einer Quelle — über deren eigene
    /// Sprachbrücke, nie über eine zweite Tabelle.
    /// </summary>
    /// <param name="codes">Unsere ISO-Kennzeichen.</param>
    /// <param name="map">Die Sprachbrücke der Quelle.</param>
    /// <returns>Die übersetzten Codes in der Reihenfolge der Anfrage; unbekannte fallen weg.</returns>
    public static IReadOnlyList<string> MapLanguages(IEnumerable<string>? codes, Func<string, string?> map)
    {
        var ziele = new List<string>();
        foreach (string code in codes ?? Array.Empty<string>())
        {
            if (map(code) is { Length: > 0 } ziel && !ziele.Contains(ziel, StringComparer.Ordinal))
            {
                ziele.Add(ziel);
            }
        }

        return ziele;
    }

    /// <summary>
    /// F-M682: die Sprache eines Kandidaten in unserer Schreibweise — die Quelle nennt ihren Code,
    /// die Grenze gibt ihn in ISO 639-2 weiter.
    /// </summary>
    /// <param name="code">Der Code der Quelle.</param>
    /// <param name="map">Die Sprachbrücke der Quelle (Code der Quelle → ISO 639-2), wenn es eine gibt.</param>
    /// <returns>Das ISO-Kennzeichen in Großbuchstaben, oder der Code der Quelle in Großbuchstaben.</returns>
    public static string CandidateLanguage(string? code, Func<string, string?>? map = null)
        => (map?.Invoke(code ?? string.Empty) is { Length: > 0 } iso
            ? iso
            : IsoLanguage(code)).ToUpperInvariant();

    /// <summary>
    /// F-M682: die Zahl, die eine Quelle in einer Antwort auf eine Bremse nennt, in Sekunden.
    /// </summary>
    /// <param name="status">Der HTTP-Status des Versuchs.</param>
    /// <param name="resetUtc">Die Frist der Quelle, wenn sie eine genannt hat.</param>
    /// <param name="clock">Der Zeitgeber des Laufs.</param>
    /// <returns>RateLimited bei 429, sonst der passende Fehler — oder null bei Erfolg.</returns>
    public static SourceFaultInfo? FaultForStatus(int status, DateTime? resetUtc, TimeProvider clock)
    {
        if (status == 429)
        {
            return SourceFaultInfo.RateLimited(SecondsUntil(resetUtc, clock), "HTTP 429");
        }

        if (status is 401 or 403)
        {
            return SourceFaultInfo.AuthFailed("HTTP " + status.ToString(CultureInfo.InvariantCulture));
        }

        if (status is 404 or 410)
        {
            return SourceFaultInfo.NotFound("HTTP " + status.ToString(CultureInfo.InvariantCulture));
        }

        if (status >= 500 || status == 0)
        {
            return SourceFaultInfo.Transient(status == 0 ? "no answer" : "HTTP " + status.ToString(CultureInfo.InvariantCulture));
        }

        return null;
    }
}
