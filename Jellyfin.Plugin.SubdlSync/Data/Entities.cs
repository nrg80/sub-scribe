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

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// The four states a subtitle can be in. Everything else is derived.
/// <para>
/// There is deliberately no "settled"/"done" state: "is this position finished?" is answered by
/// the presence of one of the three stored states below. The previous model carried a separate
/// settled row next to the outcome row, which duplicated every verdict and forced every reader
/// to know both layers.
/// </para>
/// </summary>
public static class SubtitleStatus
{
    /// <summary>The subtitle was uploaded to SubDL.</summary>
    public const string Uploaded = "uploaded";

    /// <summary>The subtitle was not accepted. <see cref="SubtitleState.Reason"/> says by whom and why.</summary>
    public const string Rejected = "rejected";

    /// <summary>The subtitle was downloaded from SubDL.</summary>
    public const string Downloaded = "downloaded";

    /// <summary>
    /// F-M257: the track was SEEN, no verdict exists. Written by whoever reads the streams or the
    /// directory without doing anything about it (seeder scan, download pipeline) — it records the
    /// file fact (language, HI) and deliberately claims nothing else.
    /// <para>
    /// Needed because the embedded area had exactly ONE writer (the upload pipeline), so with
    /// <c>UploadEnabled=false</c> — the shipped default — no embedded row existed at all. A reader
    /// that depends on that area (the HI question, F-M254) then answered "HI absent" forever for a
    /// container that already carried the track, and nothing could ever correct it.
    /// </para>
    /// <para>
    /// NOT terminal by design: <see cref="SubtitleStatus.Uploaded"/> and
    /// <see cref="SubtitleStatus.Rejected"/> are verdicts that close a position, an observation is
    /// not. Every terminality check must keep filtering for the verdict values, otherwise this state
    /// would mark work as done that was never attempted.
    /// </para>
    /// </summary>
    public const string Observed = "observed";

    /// <summary>Nothing recorded yet. Never stored — absence of a row.</summary>
    public const string Pending = "pending";
}

/// <summary>
/// Why a subtitle was rejected. Stored in <see cref="SubtitleState.Reason"/> so the verdict itself
/// stays one state (<see cref="SubtitleStatus.Rejected"/>).
/// <para>
/// The distinction that matters operationally is WHO rejected: a remote rejection is final and
/// unactionable, a local quality rejection means the file itself could be improved, and a
/// self-echo rejection only says "we already uploaded this pair in this run".
/// </para>
/// </summary>
public static class RejectReason
{
    // --- rejected by SubDL (remote, final) ---
    /// <summary>SubDL already holds this exact subtitle content.</summary>
    public const string DuplicateRemote = "duplicate-remote";

    /// <summary>SubDL reports identical content.</summary>
    public const string DuplicateContent = "duplicate-content";

    // --- rejected by our own quality gates ---
    /// <summary>Below the 2 KB floor.</summary>
    public const string TooSmall = "too-small";

    /// <summary>Fewer than 30 cues.</summary>
    public const string TooFewCues = "too-few-cues";

    /// <summary>Detected content language does not match the tag (suffix carries the detail).</summary>
    public const string LanguageMismatch = "lang-mismatch";

    /// <summary>Timings unparseable, non-monotonic or implausible.</summary>
    public const string BadStructure = "bad-structure";

    /// <summary>Cue span does not match the item runtime.</summary>
    public const string RuntimeMismatch = "runtime-mismatch";

    // --- rejected because the stream carries no usable language tag ---
    /// <summary>Untagged stream, resolve-und disabled.</summary>
    public const string UndOff = "und-off";

    /// <summary>Untagged stream, too little text to detect a language.</summary>
    public const string UndTooSmall = "und-too-small";

    /// <summary>
    /// F-M577 (Operator-Entscheidung 17.11.2026: Variante B): die Extraktion dieser Spur ist
    /// gescheitert — kein Urteil über den Untertitel, sondern über den Zugriff auf ihn. Die Ablehnung
    /// steht im vorhandenen Store (kein eigenes Feld, kein Schema-Schritt) und gilt nach
    /// sieben Tagen als veraltet: dann bekommt die Spur ihren nächsten Versuch.
    /// </summary>
    public const string ExtractionFailed = "extraction-failed";

    /// <summary>
    /// F-M586 (Operator-Anweisung 17.11.2026: „Die spur sollte dann für tvsubtitles oder mst geschlossen
    /// werden, das paar. Unsupported language oder so"): keine der eingeschalteten Quellen führt diese
    /// Sprache. tvsubtitles.net bietet 24 Sprachen (en, es, fr, de, br, ru, ua, it, gr, ar, hu, pl, tr,
    /// nl, pt, sv, da, fi, ko, cn, jp, bg, cz, ro) — Norwegisch, Slowakisch oder Estnisch sind nicht
    /// dabei, weder im Sprachfilter noch als Flagge (gemessen 17.11.2026: <c>sk.gif</c>/<c>no.gif</c>
    /// antworten 404, <c>de.gif</c> 200). Ohne dieses Urteil nannte das Log das Paar in jedem Lauf
    /// weiter „still open for tvsubtitles" (1.316 DUE-Zeilen, davon 104 für Norwegisch).
    /// </summary>
    public const string UnsupportedLanguage = "unsupported-language";

    /// <summary>Untagged stream, language could not be detected.</summary>
    public const string UndDetectionFailed = "und-detection-failed";

    /// <summary>Language tag could not be mapped to a SubDL language.</summary>
    public const string UnmappedLanguage = "unmapped-language";

    // --- rejected by our own self-echo guard ---
    /// <summary>This (file, language, HI) pair was already uploaded in this run.</summary>
    public const string SelfEcho = "duplicate-self-echo";


    // --- download side: a candidate was fetched, screened and discarded ---
    /// <summary>A download candidate was fetched and failed a gate. The gate is the reason suffix.</summary>
    public const string CandidateRejected = "candidate-rejected";
}

/// <summary>
/// Compatibility record for the database itself (area 0). Exactly one row, updated on startup.
/// <para>
/// Read at startup to answer "can I safely touch this file?". A data file written by a NEWER
/// plugin version than the one running must not be modified — the newer version may rely on
/// fields this build does not know, and writing would silently drop them.
/// </para>
/// </summary>
/// F-M195b: structural changes are NOT migrated. An unrecognised schema version is ignored
/// rather than rewritten; the database reset is the intended path.
public class MetaEntity
{
    /// <summary>Fixed primary key.</summary>
    public string Id { get; set; } = "db";

    /// <summary>
    /// Structure version of this data file. Bumped by hand whenever the shape of the stored
    /// documents changes in a way an older build could not handle.
    /// </summary>
    // F-M195: raised by hand whenever the stored shape changes; a higher value disables writing.
    public int SchemaVersion { get; set; }

    /// <summary>Plugin version that wrote this file last.</summary>
    public string? PluginVersion { get; set; }

    /// <summary>Jellyfin server version, for diagnosing API-related breakage.</summary>
    public string? JellyfinVersion { get; set; }

    /// <summary>When this record was last written.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Area 2: one row per video file, keyed by the stable OSHash/media hash.
/// <para>
/// The embedded subtitle streams of this file live in <see cref="TrackEntity"/> with this
/// hash as their parent — they have no existence of their own, which is why they are stored
/// beneath the file rather than beside it.
/// </para>
/// </summary>
/// F-M196: one record per video file, keyed by a random, immutable FileId (F-M671). For a series episode
/// the stored ids are the SHOW ids, never episode or season ids.
/// F-M38: state is split by the kind of thing described - tracks live under their video file,
/// sidecars carry their own record, and the file record holds path, ids and the derived aggregates.
/// F-M671: bis Schema 21 war der OSHash der Schlüssel. Ein Hash beschreibt den INHALT; ein neu getaggter
/// Container bekam damit eine neue Identität samt neuer Spurkennungen, obwohl Datei und Spuren dieselben
/// waren. Seit Schema 22 ist die Kennung zufällig und unveränderlich, der Hash eine Eigenschaft daneben.
public class FileEntity
{
    /// <summary>
    /// F-M676: der Primärschlüssel — <c>#yyyyy</c>, fünf Zeichen aus dem Alphabet mit vorangestelltem
    /// <c>#</c>. Zufällig, einmalig, unveränderlich.
    /// </summary>
    public string FileId { get; set; } = string.Empty;

    // F-M673 (Betreiber-Korrektur 22.11.2026): die DATEI TRÄGT KEIN PACK. Eine Datei enthält Spuren in
    // vielen Sprachen, und der Pack einer Quelle pflegt EINE Sprache (und HI) — die Zugehörigkeit ist
    // deshalb die der Spur. F-M676: sie trägt auch keinen Anzeige-String mehr; ihre Id IST der Text.

    /// <summary>
    /// F-M671: der OSHash (Größe plus erste und letzte 64 KB). Er ist die Eigenschaft, an der die Datei
    /// WIEDERERKANNT wird — nicht ihre Identität: bleibt der Hash gleich, ist es dieselbe Datei; ändert
    /// er sich, behält die Zeile ihre Kennung und bekommt einen neuen Hash.
    /// </summary>
    public string? OsHash { get; set; }

    /// <summary>Date name (not the full path), for the osHash check against name and path.</summary>
    public string? FileName { get; set; }

    /// <summary>File size in bytes at the last sighting.</summary>
    public long? Size { get; set; }

    /// <summary>File mtime in unix seconds at the last sighting.</summary>
    public long? MtimeUnix { get; set; }

    /// <summary>When the row was first written (UTC).</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Latest known absolute path to the media file.</summary>
    public string? Path { get; set; }

    /// <summary>Jellyfin item ID, when available.</summary>
    public string? JellyfinItemId { get; set; }

    /// <summary>IMDb ID (ttNNNNNNNN). For series this is the SHOW id, never an episode id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>TMDb ID. For series this is the SHOW id, never an episode or season id.</summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// F-M501 (Operator-Anweisung 15.11.2026: „Tvst Seriennummer in den Store, gleiche Ebene wie tvshow
    /// id von tmdb und tmdb“): die Nummer, unter der die Serie bei tvsubtitles.net geführt wird —
    /// dieselbe Ebene wie <see cref="TmdbId"/> und <see cref="ImdbId"/>, weil sie dieselbe Frage
    /// beantwortet: welche Serie IST diese Datei.
    /// <para>
    /// Sie wird EINMAL ermittelt (exakter TMDb-Titel in der Serienliste der Seite oder, wenn die Serie
    /// dort fehlt, nach dem Anlegen) und danach gelesen statt neu gesucht. Null heißt „noch nicht
    /// ermittelt“ — nie „keine Nummer vorhanden“: die Auflösung schreibt nur, was sie wirklich gesehen
    /// hat, und ein Fehlschlag lässt das Feld leer.
    /// </para>
    /// </summary>
    public string? TvstShowId { get; set; }

    /// <summary>
    /// F-M507 (Schema 14, Nutzerauftrag 15.11.2026 „Was was ist, Auflösung per tmdb"): die Nummer, unter
    /// der der FILM bei moviesubtitles.org geführt wird — dieselbe Ebene wie <see cref="TmdbId"/>,
    /// <see cref="ImdbId"/> und <see cref="TvstShowId"/>, weil sie dieselbe Frage beantwortet: welcher
    /// Film IST diese Datei.
    /// <para>
    /// Sie wird EINMAL ermittelt (TMDb-Titel plus TMDb-Jahr in der Suche der Seite, eng verglichen) und
    /// danach gelesen statt neu gesucht. Null heißt „noch nicht ermittelt" — nie „keine Nummer
    /// vorhanden": geschrieben wird nur, was die Seite wirklich genannt hat.
    /// </para>
    /// </summary>
    public string? MsubMovieId { get; set; }

    /// <summary>SubDL release ID (sd_id) when known.</summary>
    public string? SdId { get; set; }

    /// <summary>True for series episodes, false for movies.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Season number (series only).</summary>
    public int? Season { get; set; }

    /// <summary>Episode number (series only).</summary>
    public int? Episode { get; set; }

    /// <summary>Languages present in this file, comma separated and sorted. Derived from the embedded tracks.</summary>
    public string? LanguagesAvailable { get; set; }

    /// <summary>
    /// When every embedded subtitle of this file reached a terminal state (uploaded or rejected).
    /// <para>
    /// Explicitly NOT "the video file was uploaded" — the video file never leaves this machine.
    /// Only subtitle data travels to SubDL.
    /// </para>
    /// </summary>
    // F-M88c: the upload-side completion mark; a file whose embedded side is complete is skipped.
    public DateTime? SubtitlesUploadedAt { get; set; }

    // F-M668 (21.11.2026): der Zugeh-Stempel je Quelle (F-M557) und die Spursignatur (F-M562) standen
    // hier als `CompleteSubdl`/`CompleteTvSubtitles`/`CompleteMsub`/`CompleteSignature`. Sie sind in die
    // Marken-Matrix gewandert (`Kind = file`, Signatur im Feld `Fingerprint`) und werden hier NICHT mehr
    // geführt: ein Urteil hat genau einen Ort (F-M668).

    // F-M283 (user decision 02.10.2026): the download-side completion mark is GONE.
    //
    // `SubtitlesDownloadedAt` (a timestamp) and `SubtitlesDownloadedLanguages` (a comma-separated
    // language list, later carrying `:hi` tokens) used to sit here. They were a second truth beside
    // the rows that describe the subtitles, and the two drifted: the reader that decided whether to
    // WRITE the mark and the reader that trusted it asked for different evidence, so a variant
    // embedded in the container kept the mark from ever being written and the item was re-downloaded
    // on every pass (41 downloads in one run against a 50/day limit, 35 marks withheld, the same
    // content hash for three consecutive days).
    //
    // Completeness is derived instead, on every ask, from the files and the embedded rows
    // (`Registry.SubtitleCoverage`). Nothing needs invalidating when a subtitle is deleted because
    // nothing was ever stored as "done" — and the hearing-impaired variant is judged as what it is:
    // its own subtitle datum, with its own language and its own flag.

    /// <summary>Last time a SubDL search ran for this file.</summary>
    public DateTime? LastSearchUtc { get; set; }

    /// <summary>Target-language list the last search was made for.</summary>
    public string? LastSearchLanguages { get; set; }

    /// <summary>
    /// F-M661: last time a search for this file came back EMPTY — no candidate from any source.
    /// An empty answer is re-asked on a longer clock than the configured refetch interval, so a
    /// library full of titles no source carries does not spend its whole search budget on the same
    /// empty questions at every run. Cleared by any search that does return something.
    /// </summary>
    public DateTime? LastSearchEmptyUtc { get; set; }

    /// <summary>Last time this media record was seen/updated.</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Shared state fields of a subtitle record, in both areas 2 and 3.
/// </summary>
public abstract class SubtitleState
{
    /// <summary>Two or three letter language code.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Hearing-impaired / SDH variant.
    /// <para>
    /// F-M285 (user decision 02.10.2026): the stored value is THREE-valued, not two. A row must say
    /// <c>true</c> or <c>false</c> EXPLICITLY; <c>null</c> means "this row does not say". The
    /// distinction is the whole point: a document store has no schema, so a row written before this
    /// field existed reads back as a default, and a default that looks like a decision is how a
    /// missing fact becomes a false claim. An explicit <c>false</c> is a statement (this subtitle is
    /// NOT the variant); a <c>null</c> is a gap that a backfill may fill and no reader may read.
    /// </para>
    /// </summary>
    public bool? HearingImpaired { get; set; }

    /// <summary>
    /// F-M284 (user decision 02.10.2026): the track is FORCED — it carries the lines of
    /// foreign-language scenes, not the film's dialogue.
    /// <para>
    /// An Eigenschaft OF THIS DATUM, exactly like <see cref="HearingImpaired"/>, and stored the same
    /// three-valued way (F-M285): <c>true</c> / <c>false</c> are statements, <c>null</c> is a row
    /// that does not say. It never counts as coverage, so a language whose only track is forced stays
    /// open and is searched for (F-M246).
    /// </para>
    /// <para>
    /// Unlike the hearing-impaired flag it is not bilateral: Jellyfin reports it on its streams, but
    /// SubDL has no forced counterpart — no search filter, no response field, and 0 of 22 candidates
    /// named it when measured live on 02.10.2026. So a forced datum is OBSERVED, never sought,
    /// fetched or uploaded. Recording it is what stops it from being mistaken for the film's
    /// dialogue.
    /// </para>
    /// </summary>
    public bool? Forced { get; set; }

    /// <summary>MD5 of the normalized SRT content, in SubDL's own format. Null when the content was never read.</summary>
    public string? ContentHash { get; set; }

    /// <summary>One of <see cref="SubtitleStatus"/>. Absence of a row means pending.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Rejection reason (one of <see cref="RejectReason"/>, suffixes allowed), else null.</summary>
    public string? Reason { get; set; }


    /// <summary>When <see cref="Status"/> was last set.</summary>
    public DateTime StatusAt { get; set; } = DateTime.UtcNow;

    // F-M670 (21.11.2026): `SubdlId` stand hier als „SubDL upload or download id this verdict refers
    // to". Genau das führt die Marken-Matrix als `SourceId` (Eintrag/Fund) und `UploadId` (Einsendung);
    // die Spalte war der zweite Bestand derselben Aussage und ist entfernt (F-M668/F-M670).

    /// <summary>
    /// F-M486 (operator order 15.11.2026): where this subtitle CAME FROM — <c>subdl</c> or
    /// <c>tvsubtitles</c>. Null means "this row does not say" (an older row, or a file nobody fetched),
    /// which is why it is never guessed. This value is what keeps the two sources apart: a subtitle
    /// fetched from one source is never uploaded to the other (F-M487).
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>
    /// F-M486: the source's OWN id for this subtitle — the tvsubtitles.net subtitle id (for example
    /// <c>170678</c>) or the SubDL entry id. Together with <see cref="Origin"/> it names the original
    /// exactly, so a later check can quote it instead of describing it.
    /// <para>
    /// F-M672 (Schritt B, 22.11.2026): DIE SPALTE IST FORT. Eine fremde Id steht seit Schema 23 in
    /// <c>reference</c> (<c>DownloadSourceId</c>), adressiert über die eigene Kennung des Subjekts — sie
    /// ist damit ein Beleg JE QUELLE statt eines Feldes an der Zeile, und <c>Origin</c> bleibt, was es
    /// immer war: der Quellenname, also Herkunft, kein Urteil.
    /// </para>
    /// </summary>
    // F-M672: `OriginId` stand hier bis Schema 22.

    // F-M668 (21.11.2026): die neun Marken-Spalten je Quelle und Richtung (`SubdlDownloadId`,
    // `SubdlUploadId`, `SubdlUploadedAt`, dasselbe für tvsubtitles und moviesubtitles) standen hier und
    // an den Sidecars. Sie sind in die Marken-Matrix gewandert (Anker = Medien-Hash, Art = `track` bzw.
    // `track-hi`, `SourceId` = Abruf-Id, `UploadId` = Einsende-Id, `ClosedAt` = Einsende-Zeitpunkt) und
    // werden hier NICHT mehr geführt: ein Urteil hat genau einen Ort (F-M668).
    // F-M670 (21.11.2026): dasselbe gilt für `SubdlId`. Die Spalte stand hier und an den Sidecars und
    // wiederholte, was die Matrix als `SourceId`/`UploadId` führt — sie ist samt Feld entfernt. Was
    // bleibt, ist allein die Herkunft (`Origin`/`OriginId`); sie sagt, WOHER der Text kam, nicht was
    // die Quelle über ihn geurteilt hat.
}

/// <summary>
/// Area 2, beneath the file: one row per embedded subtitle stream.
/// <para>
/// The key is (media hash, stream position) because that IS the identity of an embedded track —
/// it has no name and no independent existence. Two streams of one file can share a language
/// (a normal and an SDH variant, or two releases), so the language alone must never identify the
/// row; conflating them is what made one rejected stream block all its siblings.
/// </para>
/// </summary>
/// F-M197: one record per embedded subtitle stream, keyed by a random TrackId that is unique within its
/// file (F-M671). Two streams of one file can share a language (a normal and an SDH variant); the
/// position stays a column because it is how a caller addresses the stream today.
public class TrackEntity : SubtitleState
{
    /// <summary>
    /// F-M676: der Primärschlüssel — <c>#yyyyy#zzz</c>: die Datei-Id, ein <c>#</c> und drei Zeichen. Die
    /// drei Zeichen sind innerhalb der Datei eindeutig; die Datei-Id steht in derselben Spalte, also ist
    /// die Id global eindeutig und lesbar.
    /// </summary>
    public string TrackId { get; set; } = string.Empty;

    /// <summary>
    /// F-M676: die Datei, zu der die Spur gehört — ABGELEITET aus <see cref="TrackId"/>, nicht
    /// gespeichert. Eine eigene Spalte wäre dieselbe Aussage ein zweites Mal, und zwei Aussagen können
    /// verschieden sein.
    /// </summary>
    public string FileId
    {
        get => ScribeIds.FileOf(TrackId);
    }

    // F-M677 (Betreiber-Korrektur 22.11.2026): DIE MITGLIEDSCHAFT HÄLT DER PACK, nicht die Spur. Ein Feld
    // `PackId` hier war der Fehler, der alle 315 Spuren in den EN-HI-Pack gezogen hat: es kam von der
    // DATEI, und eine Datei trägt Spuren in vielen Sprachen. Die Zugehörigkeit steht jetzt in der
    // Zuordnungstabelle `pack_track` (PackId, TrackId) — das Array des Packs, zeilenweise und abfragbar.

    /// <summary>
    /// F-M671: die SPURNUMMER je Datei — aus EINEM Nummernkreis für eingebettete Spuren und Seitenwagen,
    /// beim ersten Sehen vergeben, nie neu vergeben und nie wiederverwendet. Sie ist NICHT der
    /// ffmpeg-Index: ffmpeg zählt nur, was gerade im Container liegt, und Seitenwagen stehen dazwischen.
    /// Der Index wird zur Laufzeit abgeleitet (<see cref="TrackNumbers.FfmpegIndex"/>).
    /// </summary>
    public int TrackNumber { get; set; }

    /// <summary>
    /// Der OSHash der Datei, zu der die Spur gehört — die Adresse, unter der die Aufrufer sie heute
    /// führen (F-M256). Er ist eine Eigenschaft der Datei und wird beim Neu-Hashen mitgezogen.
    /// </summary>
    public string MediaHash { get; set; } = string.Empty;

    /// <summary>ffmpeg stream position (0:s:N) as last observed — a property, never the identity.</summary>
    public int SubPos { get; set; }

    /// <summary>F-M671: when the row was first written (UTC).</summary>
    public DateTime Created { get; set; }

    /// <summary>F-M671: when the track was last seen in its file (UTC).</summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// When language DETECTION last ran for this position, or null when it never did.
    /// <para>
    /// Detection is the expensive half of the gate (extraction plus classification), and for a
    /// track the detector cannot classify the answer never changes. Recording the attempt is what
    /// lets the next scan skip it instead of re-opening the media file: the seeder's cheap
    /// pre-check only asks whether an untagged track EXISTS, and a track that stays untagged
    /// passes that question forever. Per TRACK, not per file — a file can have one unresolved
    /// track beside forty settled ones.
    /// </para>
    /// <para>
    /// A tag write gives the file a new hash and therefore fresh rows, which is the one event
    /// that should retry. Set by the gate, read by the gate and by the refresh worker.
    /// </para>
    /// </summary>
    public DateTime? DetectionAttemptedAt { get; set; }

    /// <summary>How the last detection attempt ended — one of <see cref="DetectionOutcome"/>, else null.</summary>
    public string? DetectionOutcome { get; set; }

}

/// <summary>
/// How a per-track language detection attempt ended. Stored on the embedded track row alongside
/// <see cref="TrackEntity.DetectionAttemptedAt"/>.
/// </summary>
public static class DetectionOutcome
{
    /// <summary>A language was detected and written into the container.</summary>
    public const string Resolved = "resolved";

    /// <summary>The track held no text at all, so there was nothing to classify.</summary>
    public const string NoText = "no-text";

    /// <summary>The track held less text than the 2 KB floor — a verdict would be garbage.</summary>
    public const string TooSmall = "too-small";

    /// <summary>The classifier returned no confident verdict for the text it was given.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Area 3: one row per loose .srt sidecar file.
/// <para>
/// Keyed by the normalized CONTENT hash rather than by name or language. That is what makes a
/// sidecar an independent thing: the same subtitle text stays "known" when the file is renamed
/// or moved, and two sidecars that share a language but differ in content stay two rows instead
/// of collapsing into one. The filename is kept for reference only and deliberately plays no part
/// in the identity.
/// </para>
/// </summary>
/// F-M199: one record per loose .srt file, addressed by the normalized content hash (F-M186), so a
/// sidecar stays known after a rename or a move, and two sidecars differing in text stay two rows.
/// F-M671: die IDENTITÄT ist die Spurkennung, nicht der Inhalt: ein Seitenwagen ist „diese Untertitelspur
/// dieser Datei", seine Anzeige ist <c>0000-yyyyy-zzz</c> — er trägt bewusst KEIN Pack (aus Seitenwagen
/// werden keine Packs gebaut), teilt aber den Nummernkreis seiner Datei mit den eingebetteten Spuren.
public class SidecarEntity : SubtitleState
{
    /// <summary>
    /// F-M676: der Primärschlüssel — <c>#yyyyy&amp;zzz</c>: die Datei-Id, ein <c>&amp;</c> und drei Zeichen
    /// aus dem gemeinsamen Nummernkreis der Datei. Das <c>&amp;</c> ist kein Zeichen des Alphabets, also
    /// kann ein Seitenwagen nie mit einer eingebetteten Spur derselben Datei kollidieren.
    /// </summary>
    public string TrackId { get; set; } = string.Empty;

    /// <summary>F-M676: die Hostdatei — ABGELEITET aus <see cref="TrackId"/>, nicht gespeichert.</summary>
    public string FileId
    {
        get => ScribeIds.FileOf(TrackId);
    }

    // F-M671: ein Seitenwagen gehört zu KEINEM Pack — aus Seitenwagen werden keine Packs gebaut, und
    // eine `pack_track`-Zeile für ihn würde eine Mitgliedschaft behaupten, die es nicht gibt.

    /// <summary>F-M671: die Spurnummer aus dem gemeinsamen Nummernkreis der Hostdatei.</summary>
    public int TrackNumber { get; set; }

    /// <summary>When the row was first written (UTC).</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Last time this sidecar was seen on disk (UTC).</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>File name (not the full path) of the sidecar, for diagnostics.</summary>
    public string? FileName { get; set; }

    /// <summary>Media hash of the file this sidecar sits next to (address, not identity).</summary>
    public string MediaHash { get; set; } = string.Empty;

    /// <summary>Full path of the sidecar as last seen, for diagnostics.</summary>
    public string? Path { get; set; }

    /// <summary>IMDb id of the media, for lookups that do not know the media hash.</summary>
    public string? ImdbId { get; set; }

    /// <summary>TMDb id of the media (show id for series).</summary>
    public string? TmdbId { get; set; }

    /// <summary>True when the parent file is a series episode.</summary>
    public bool IsSeries { get; set; }

    /// <summary>Season number of the parent file (series only).</summary>
    public int? Season { get; set; }

    /// <summary>Episode number of the parent file (series only).</summary>
    public int? Episode { get; set; }
}

/// <summary>
/// F-M200/F-M450/F-M672: die verworfenen Download-Kandidaten standen bis Schema 22 in einer eigenen
/// Tabelle (<c>rejected_candidates</c>, Schlüssel <c>item|language|subdl-id</c>). Sie sind mit F-M672 in
/// <see cref="BadAppleEntity"/> aufgegangen — dieselbe Aussage („dieser fremde Eintrag war schlecht"),
/// aber adressiert über das SUBJEKT des Stores statt über eine Jellyfin-Item-Id, und mit der fremden
/// Kennung an der Stelle, an der sie ausdrücklich erlaubt ist.
/// </summary>
/// <summary>F-M450: what a report needs about the candidate that failed, as stored.</summary>
/// <param name="NId">Subtitle report id.</param>
/// <param name="Finding">Bare finding.</param>
/// <param name="ReleaseName">Release name as SubDL lists it.</param>
public sealed record ReportFacts(string NId, string Finding, string ReleaseName);

/// <summary>
/// Area 1: OSHash cache entry mapping a media path to its computed hash.
/// <para>
/// Pure cache — losing it costs a recomputation and nothing else, which is why it is keyed by
/// path while every other area is keyed by a hash.
/// </para>
/// </summary>
public class OshashEntity
{
    /// <summary>Absolute media path as primary key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Computed OSHash value.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>File size at time of hashing.</summary>
    public long Size { get; set; }

    /// <summary>Last hash computation timestamp.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;

    /// <summary>File mtime (unix seconds) the hash was computed against.</summary>
    public long MtimeUnix { get; set; }
}

/// <summary>
/// Generic key/value counters with optional expiration (retry and QA-fail budgets).
/// </summary>
public class CounterEntity
{
    /// <summary>Database-assigned auto-id. No longer the identity — see the Key above.</summary>
    public int Id { get; set; }

    /// <summary>Counter key, namespaced, e.g. "not-found:tt1234567".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Current integer value.</summary>
    public int Value { get; set; }

    /// <summary>Optional expiration (UTC). Null = never.</summary>
    public DateTime? Expires { get; set; }

    /// <summary>Last update timestamp.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// High-level pipeline run bookkeeping.
/// </summary>
public class PipelineRunEntity
{
    /// <summary>Legacy auto-assigned id from the previous document store.</summary>
    public int Id { get; set; }

    /// <summary>Direction: download, upload or both.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Trigger source: scheduled, manual, event.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Run start time.</summary>
    public DateTime Started { get; set; } = DateTime.UtcNow;

    /// <summary>Run end time; null while running.</summary>
    public DateTime? Ended { get; set; }

    /// <summary>Number of items queued.</summary>
    public int ItemsTotal { get; set; }

    /// <summary>Number of items terminal.</summary>
    public int ItemsDone { get; set; }
}

/// <summary>
/// Cumulative subtitle counters shown in the configuration GUI.
/// <para>
/// These used to live in the plugin configuration XML (F-M23), which meant they were stored next to
/// user settings while the data they describe lives in the database — so a database reset left them
/// untouched and the display drifted away from the data it claimed to report (measured 25.09.2026:
/// after a reset the database held 311 uploaded rows while the display still said 743). They now live
/// in the database, which makes them part of the same reset and incapable of drifting.
/// </para>
/// <para>
/// Single row, fixed key <c>"status"</c> — this is a running total, not a per-item record.
/// </para>
/// </summary>
public class StatusStatsEntity
{
    /// <summary>Fixed primary key.</summary>
    public string Id { get; set; } = "status";

    /// <summary>Total subtitles uploaded since <see cref="SinceUtc"/>.</summary>
    public long Uploaded { get; set; }

    /// <summary>Total subtitles downloaded since <see cref="SinceUtc"/>.</summary>
    public long Downloaded { get; set; }


    /// <summary>
    /// F-M286: candidates FETCHED and then thrown away, per direction — every reject path, not the QA
    /// gates alone. Renamed from <c>QaRejectedDownload</c> when the meaning widened: a fetched
    /// candidate discarded because its content was already known, or because no usable bytes came
    /// back, spends a request just like a gate rejection does. Reconciles with the day's quota:
    /// requests = saved + rejected. Nothing that was never fetched is counted.
    /// </summary>
    public long RejectedDownload { get; set; }

    /// <summary>
    /// F-M286: upload candidates discarded before or instead of an upload — QA gates, und-off,
    /// unmapped language, self-echo, duplicate-remote, forced (F-M284). Same rule as the download:
    /// one field, every reject path.
    /// </summary>
    public long RejectedUpload { get; set; }

    /// <summary>
    /// F-M308: downloaded subtitles that were FITTED to their audio track — the correction applied,
    /// not merely offered. The volume counter counts subtitles that arrived; this one counts the ones
    /// the run had to move, which is why it is reported next to the corrections and not next to the
    /// volume. Download only: the upload direction has no audio fit.
    /// </summary>
    public long FittedToAudio { get; set; }

    /// <summary>
    /// F-M455 (operator order 10.11.2026: "Neues Statistikfeld Download: Broken subtitle reported to
    /// SubDL nach Auto-Sync"): subtitles this run reported to SubDL as broken — a file that failed
    /// its content gates or answered empty TWICE, and whose report the portal confirmed. It sits
    /// directly behind the Auto-Sync row because it belongs to the same story: the fit measures the
    /// file, and when the measurement says the upload is broken, this is where the answer goes. Only
    /// an accepted report counts; a deferred or refused one is not a report.
    /// </summary>
    public long BrokenReported { get; set; }

    /// <summary>
    /// F-M311: media FILES whose language codes were written by the seeder's allocation pass — not a
    /// download and not an upload. One count is one file that got at least one real tag where it had
    /// none (or had <c>und</c>), counted only when the write actually happened: a dry run, a disabled
    /// switch or a detector that could not decide all leave it at 0. Collapsed to one per file on
    /// purpose — a file with three tags is one file edited, and that is the question the line
    /// answers. It belongs on the status row because the row otherwise reports only what entered the
    /// library, never that the library itself was rewritten.
    /// </summary>
    public long LanguageCodesAllocated { get; set; }

    /// <summary>
    /// F-M313: LOOSE subtitle files renamed so their NAME carries the detected language (F-M278). A
    /// separate counter from the one above because it is a different act on a different kind of file:
    /// a container gets a language tag INSIDE, a loose .srt gets a new name. Counted only on a real
    /// move — a refusal (target taken, unlistable directory, filesystem error) and a dry run leave
    /// both the file and the counter untouched.
    /// </summary>
    public long LooseSubtitlesRenamed { get; set; }

    /// <summary>Start of the counting period; set by "Reset statistics", null before first use.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>Last time a counter changed.</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The LAST run of one worker (scheduled task), one row per worker.
/// <para>
/// Deliberately not a run HISTORY: the key is the worker's own task key, so a new run overwrites the
/// previous row and the area can never grow. A history would need a retention policy and a prune
/// path; the question this answers — "when did each worker last run, and how did it end?" — needs
/// exactly one row per worker.
/// </para>
/// <para>
/// Stored in the DATA FILE rather than in memory: the previous status lived in static fields, so a
/// Jellyfin restart reset it to "never run" and the GUI claimed a task had never executed minutes
/// after it had (measured 30.09.2026). A database row survives a restart and is wiped by a database
/// reset, which is the honest scope for it.
/// </para>
/// </summary>
public class WorkerRunEntity
{
    /// <summary>The worker's task key — the row's own primary key, so it can never duplicate.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name of the worker as Jellyfin shows it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Run start (UTC); null for a worker that has not run yet.</summary>
    public DateTime? Started { get; set; }

    /// <summary>Run end (UTC); null while the worker is still running.</summary>
    public DateTime? Ended { get; set; }

    /// <summary>
    /// How the run ended: <c>ok</c>, <c>failed</c>, <c>cancelled</c>, <c>skipped</c> (disabled or
    /// deferred by the global run lock) or <c>never</c> for a worker that has not run yet.
    /// <para>
    /// One word, and the GUI shows it as-is. It used to render as a sentence plus a parenthesised
    /// detail, which the user rejected outright: "ein worker, eine ampel, ein datum, ein end status"
    /// (30.09.2026).
    /// </para>
    /// </summary>
    public string Outcome { get; set; } = "never";

    /// <summary>
    /// Detailed summary of the run, kept for diagnosis and the status endpoint. NOT shown in the
    /// Workers section: that line carries only light, date and outcome. Retained because it is the
    /// only durable trace of what a run actually did once the log has rotated.
    /// </summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// True when this run was a dry run, so the GUI can say so without the outcome line carrying a
    /// sentence. A dry run's numbers are hypothetical, and a reader who cannot see the flag would
    /// take the run for a real one — but the note belongs beside the outcome, not in the detail text.
    /// </summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// F-M390 (operator order 02.11.2026): one row per season pack this plugin published, keyed by the
/// season it covers — "&lt;imdb&gt;|S&lt;season&gt;|&lt;lang&gt;|&lt;hi|plain&gt;".
/// <para>
/// WHY IT EXISTS: a pack could only ever be built while every episode of the season was still in the
/// run's upload queue. Once the episodes had gone up individually the season could never become a
/// pack again, because nothing persisted the fact that it had been attempted — a later run would
/// either repeat the same pack forever or never try again. The row is that fact: it says which
/// season, in which language and variant, was packed, under which release, with which members, and
/// which single uploads it absorbed.
/// </para>
/// <para>
/// The absorbed ids are kept deliberately. Folding singles into a pack means deleting them from
/// SubDL, and a deletion with no record is a deletion nobody can audit or undo; with the ids on the
/// row the plugin can name what it removed and put it back when a pack upload fails after them.
/// </para>
/// </summary>
public class PackEntity
{
    /// <summary>F-M642: die Quelle, die den Pack traegt — SubDL baut seine Container selbst.</summary>
    public const string SubdlSource = "subdl";

    /// <summary>F-M642: die zweite Quelle, die seit F-M641 Staffel-Packs kennt.</summary>
    public const string TvSubtitlesSource = "tvsubtitles";

    /// <summary>
    /// F-M676: der Primärschlüssel — <c>[xxxx]</c>, vier Zeichen in eckigen Klammern. Zufällig, einmalig,
    /// unveränderlich. Die Klammern sind die Marke der Ebene und stehen nicht im Alphabet.
    /// </summary>
    public string PackId { get; set; } = string.Empty;

    /// <summary>
    /// F-M390/F-M642: der STAFFEL-SCHLÜSSEL — „&lt;imdb&gt;|S&lt;season&gt;|&lt;lang&gt;|&lt;hi|plain&gt;|&lt;source&gt;".
    /// Er ist ab F-M671 NICHT mehr die Identität, sondern die Adresse, unter der die Aufrufer den Behälter
    /// fragen („gibt es zu dieser Staffel, Sprache, Variante und Quelle eine Zeile?"). Er bleibt eindeutig
    /// indiziert, damit „ein Behälter je Staffel/Sprache/Variante/Quelle" weiter gilt.
    /// <para>
    /// F-M642: die Quelle steht im Schlüssel. Vorher war der Pack je (Staffel, Sprache, Variante) genau
    /// einer, weil nur SubDL Container baute; zwei Quellen teilten sich also eine Zeile, und der zweite
    /// Pack hätte den ersten überschrieben.
    /// </para>
    /// </summary>
    public string SeasonKey { get; set; } = string.Empty;

    /// <summary>
    /// F-M642: die Quelle, zu der die Zeile gehoert (<see cref="SubdlSource"/> oder
    /// <see cref="TvSubtitlesSource"/>). Zeilen aus der Zeit vor der Spalte gelten als
    /// <see cref="SubdlSource"/> — damals war SubDL die einzige Quelle mit Packs.
    /// </summary>
    public string Source { get; set; } = SubdlSource;

    /// <summary>Series IMDb id the pack was built for.</summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>Season number.</summary>
    public int Season { get; set; }

    /// <summary>SubDL language code of the pack.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Hearing-impaired variant of the pack: <c>true</c>/<c>false</c> are statements, <c>null</c> is a
    /// row that does not say (same three-valued convention as <see cref="SubtitleState.HearingImpaired"/>).
    /// </summary>
    public bool? HearingImpaired { get; set; }

    /// <summary>Release name the pack carries — one release for the whole season, never a mixture.</summary>
    public string? ReleaseName { get; set; }

    /// <summary>F-M671: TMDb id of the series, the same level as the IMDb id.</summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// F-M671: Signatur der Mitglieder (F-M466/F-M650) — „ist das noch dieselbe Staffel?". Sie steht am
    /// Behälter, weil sie eine Eigenschaft seiner Mitglieder ist; das URTEIL der Quelle steht in
    /// <c>upload</c> (F-M672).
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>F-M671: mtime of the newest member file in unix seconds, to which <see cref="Signature"/> belongs.</summary>
    public long? MtimeUnix { get; set; }

    /// <summary>F-M671: how many episodes the pack carries (the count behind <see cref="Episodes"/>).</summary>
    public int EpisodeCount { get; set; }

    /// <summary>Episode numbers the pack carries, comma separated and ascending ("1,2,3").</summary>
    public string Episodes { get; set; } = string.Empty;

    /// <summary>
    /// SubDL ids of the single uploads that were folded into this pack and deleted afterwards, comma
    /// separated. Empty when the pack was built from fresh uploads only.
    /// </summary>
    public string? AbsorbedIds { get; set; }

    /// <summary>
    /// F-M668 (21.11.2026): die Behälter-Kennung der Quelle (`PackId`), der Mitglieder-Fingerabdruck
    /// (`Fingerprint`), die Ablehnung (`RefusalReason`/`RefusedAt`/`RefusedSignature`) und das Urteil
    /// `UploadedAt` standen hier. Sie wanderten in die Marken-Matrix (`Kind = pack`, `SourceId` =
    /// Behälter-Kennung, `ClosedAt` = Urteil, `Fingerprint`, `RefusalReason`/`RefusedAt`/
    /// `RefusalSignature`) und ab Schema 23 (F-M672) in die Tabelle `upload` beziehungsweise
    /// `reference`. Diese Zeile trägt nur noch die MITGLIEDER — Release, Folgen, aufgesogene
    /// Einzel-Ids —, die kein Urteil einer Quelle sind.
    /// </summary>

    /// <summary>When the row was first written (UTC).</summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>When the row last changed (UTC).</summary>
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// F-M668: EIN Pack, wie ihn die Leser sehen — die Mitglieder aus der <c>packs</c>-Zeile und das Urteil
/// aus der Marken-Matrix.
/// <para>
/// WARUM EIN EIGENER TYP: das Urteil steht seit F-M668 nur noch in <c>source_matrix</c>, die Mitglieder
/// (Release, Folgen, aufgesogene Kennungen) weiter in <c>packs</c>. Wer „ist dieser Behälter oben?" oder
/// „warum wurde er abgelehnt?" fragt, braucht beide — und zwar als EINE Antwort, nicht als zwei Abfragen
/// an zwei Stellen. Der Typ macht außerdem sichtbar, dass seine Urteils-Felder NICHT schreibbar sind:
/// geschrieben wird über <c>ContentHashRegistry.RecordPack</c>/<c>RecordPackRefusal</c>.
/// </para>
/// </summary>
public sealed class PackView
{
    /// <summary>Der Schlüssel der <c>packs</c>-Zeile (Serie, Staffel, Sprache, Variante, Quelle).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Die Quelle des Behälters (<see cref="PackEntity.SubdlSource"/> oder <see cref="PackEntity.TvSubtitlesSource"/>).</summary>
    public string Source { get; set; } = PackEntity.SubdlSource;

    /// <summary>Serie des Behälters.</summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>Staffel.</summary>
    public int Season { get; set; }

    /// <summary>Sprache des Behälters.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>HI-Variante des Behälters (dreiwertig, F-M285).</summary>
    public bool? HearingImpaired { get; set; }

    /// <summary>Release, den der Behälter trägt.</summary>
    public string? ReleaseName { get; set; }

    /// <summary>Folgen des Behälters, kommasepariert und aufsteigend.</summary>
    public string Episodes { get; set; } = string.Empty;

    /// <summary>Die Einzel-Kennungen, die in diesem Behälter aufgegangen sind.</summary>
    public string? AbsorbedIds { get; set; }

    /// <summary>F-M390/F-M668: die Kennung des Behälters BEI DER QUELLE (Matrix-<c>SourceId</c>); null, solange keine bekannt ist.</summary>
    public string? PackId { get; set; }

    /// <summary>F-M466/F-M668: Fingerabdruck der Mitglieder (Matrix-<c>Fingerprint</c>) — „ist das derselbe Behälter?".</summary>
    public string? Fingerprint { get; set; }

    /// <summary>F-M644/F-M668: der Grund, aus dem die Quelle den Behälter abgelehnt hat; null, solange nichts abgelehnt wurde.</summary>
    public string? RefusalReason { get; set; }

    /// <summary>F-M644/F-M668: wann abgelehnt wurde (UTC).</summary>
    public DateTime? RefusedAt { get; set; }

    /// <summary>F-M650/F-M668: die Staffel-Signatur, auf der die Ablehnung beruht.</summary>
    public string? RefusedSignature { get; set; }

    /// <summary>F-M644/F-M668: wann der Behälter wirklich durchging (Matrix-<c>ClosedAt</c>); null bei Ablehnung.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>Wahr, wenn es für diesen Behälter überhaupt eine Matrix-Zeile gibt (Urteil oder Absage).</summary>
    public bool HasVerdict { get; set; }
}

/// <summary>
/// F-M672 (Betreiber-Konzept §5, 22.11.2026): DAS URTEIL EINER QUELLE ÜBER EIN SUBJEKT — Tabelle
/// <c>upload</c>.
/// <para>
/// WAS EINE ZEILE BEDEUTET: „diese Quelle hat über dieses Subjekt so entschieden". Das Subjekt ist
/// eines der vier Dinge, die eine Kennung tragen (<c>pack</c>, <c>file</c>, <c>track</c>, <c>sidecar</c>),
/// und steht als <see cref="SubjectId"/>; die Quelle ist ein WERT im Feld, keine Tabelle je Quelle —
/// eine vierte Quelle erzeugt Zeilen, keine Spalten.
/// </para>
/// <para>
/// WARUM „GESCHLOSSEN" KEIN FELD IST: <c>UploadedAt</c> gesetzt heißt geschlossen, und nichts anderes.
/// Ein eigenes Ja/Nein-Feld daneben wäre eine zweite Aussage über dieselbe Sache, und zwei Aussagen
/// können verschieden sein. Eine Absage ist kein Urteil, sondern ihr Gegenteil — sie steht deshalb mit
/// Grund, Zeitpunkt und der Signatur, auf der sie beruht, in derselben Zeile, und <c>UploadedAt</c>
/// bleibt leer.
/// </para>
/// </summary>
public class UploadEntity
{
    /// <summary>
    /// Die Kennung des SUBJEKTS — <c>PackId</c> (4 Zeichen), <c>FileId</c> (5) oder <c>TrackId</c> (3).
    /// Welche Ebene gemeint ist, sagt die Tabelle, in der die Kennung steht; die Ebene in die Kennung
    /// hineinzuschreiben wäre eine zweite Wahrheit.
    /// </summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Die Quelle — <c>subdl</c>, <c>tvsubtitles</c> oder <c>moviesubtitles</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Wann die Quelle das Subjekt angenommen hat (UTC). Gesetzt = geschlossen.</summary>
    public DateTime? UploadedAt { get; set; }

    /// <summary>Wann die Quelle abgelehnt hat (UTC); null, wenn nie abgelehnt wurde.</summary>
    public DateTime? RefusedAt { get; set; }

    /// <summary>Der Grund, den die Quelle genannt hat.</summary>
    public string? RefusedReason { get; set; }

    /// <summary>
    /// F-M650: die Signatur, auf der eine ABSAGE beruht (beim Pack die Staffel-Signatur). Sie ist der
    /// Grund, warum ein abgelehntes Paar nicht bei jedem Lauf neu gesammelt werden muss: ist sie beim
    /// nächsten Lauf dieselbe, steht das Urteil schon fest.
    /// </summary>
    public string? RefusalSignature { get; set; }

    /// <summary>
    /// Inhaltssignatur, zu der dieses Urteil gehört — bei der Datei die Spursignatur (F-M562), beim Pack
    /// der Mitglieder-Hash (F-M466). Sie steht am URTEIL, nicht am Subjekt: eine Datei kann für eine
    /// Quelle geschlossen und für eine andere offen sein, und jede Schließung gilt für einen anderen
    /// Inhaltsstand.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>Änderungszeit der Datei in Unix-Sekunden, zu der <see cref="Fingerprint"/> gehört.</summary>
    public long? MtimeUnix { get; set; }

    /// <summary>Wann die Zeile zuletzt geschrieben wurde (UTC) — Diagnose, kein Urteil.</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;

    /// <summary>Der zusammengesetzte Schlüssel der Zeile (Subjekt und Quelle) — nur für den Speicher.</summary>
    public string Key => SubjectId + "|" + Source;
}

/// <summary>
/// F-M672: DER BELEG EINER QUELLE ÜBER EIN SUBJEKT — Tabelle <c>reference</c>. Der einzige Ort, an dem
/// eine FREMDE Kennung stehen darf (Ausnahme: <see cref="BadAppleEntity"/>, siehe dort).
/// <para>
/// WARUM GETRENNT VOM URTEIL: „die Quelle hat angenommen" und „der Inhalt kam aus dem Eintrag 3758706"
/// sind zwei verschiedene Aussagen mit zwei verschiedenen Lebensläufen. Ein Abruf trägt eine Id, lange
/// bevor irgendetwas hochgeladen ist, und eine Einsendung kann eine Id nachliefern, wenn das Urteil
/// längst steht. In einer Zeile müsste jede dieser Nachlieferungen die andere überschreiben.
/// </para>
/// <para>
/// DIE FÜNF KENNUNGEN SIND VERSCHIEDENE DINGE: <see cref="DownloadSourceId"/> (fremder Eintrag, aus dem
/// der Inhalt kam), <see cref="UploadedId"/> (unsere Einsendung), <see cref="ItemId"/> (Jellyfin),
/// <see cref="SdId"/> (Staffel-/Release-Kennung der Quelle) und <see cref="Url"/> (bei tvsubtitles die
/// Seite, wenn die Id allein nicht reicht).
/// </para>
/// </summary>
public class ReferenceEntity
{
    /// <summary>Die Kennung des Subjekts (siehe <see cref="UploadEntity.SubjectId"/>).</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Die Quelle, bei der diese Kennungen gelten.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Der fremde Eintrag, aus dem der Inhalt kam.</summary>
    public string? DownloadSourceId { get; set; }

    /// <summary>Unsere Einsendung bei dieser Quelle.</summary>
    public string? UploadedId { get; set; }

    /// <summary>Die Kennung des Jellyfin-Items, zu dem der Inhalt gehört.</summary>
    public string? ItemId { get; set; }

    /// <summary>Die Staffel-/Release-Kennung der Quelle (<c>sd_id</c> bei SubDL).</summary>
    public string? SdId { get; set; }

    /// <summary>Die Seite des Eintrags, wo eine nackte Kennung nicht reicht (tvsubtitles.net).</summary>
    public string? Url { get; set; }

    /// <summary>Wann die Zeile zuletzt geschrieben wurde (UTC) — Diagnose.</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;

    /// <summary>Der zusammengesetzte Schlüssel der Zeile — nur für den Speicher.</summary>
    public string Key => SubjectId + "|" + Source;
}

/// <summary>
/// F-M672: EIN SCHLECHTER DOWNLOAD — Tabelle <c>badapple</c>. Die einzige Stelle, an der eine fremde
/// Kennung NEBEN dem Beleg stehen darf.
/// <para>
/// WARUM DIE FREMDE ID HIER AUSDRÜCKLICH ERLAUBT IST: die Meldung an die Quelle nennt genau diese
/// Nummer. „Ein Eintrag von uns ist mangelhaft" ist nicht meldbar; „Eintrag 380011 hat 0 Bytes" ist es.
/// Deshalb trägt diese Zeile <see cref="SourceId"/> — und nur diese Tabelle, nur für diesen Zweck.
/// </para>
/// <para>
/// F-M200/F-M450: der Speicher ist zugleich das Gedächtnis der Download-Seite — ein Kandidat, der
/// einmal geholt und verworfen wurde, wird nicht zweimal geholt. <see cref="Kind"/> hält die alte
/// Unterscheidung fest: <c>qa</c> schließt den Kandidaten aus, <c>empty</c> merkt nur, dass das Paket
/// nichts lieferte (ein Aussetzer darf keinen Kandidaten für immer verbrennen).
/// </para>
/// </summary>
public class BadAppleEntity
{
    /// <summary>Der Zustand „noch nicht gemeldet".</summary>
    public const string ReportStateOpen = "open";

    /// <summary>Der Zustand „die Quelle hat die Meldung angenommen".</summary>
    public const string ReportStateReported = "reported";

    /// <summary>Die Kennung des Subjekts — bei einem verworfenen Kandidaten die Datei, für die er geholt wurde.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>Die Quelle, deren Eintrag mangelhaft ist.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Die Nummer des fremden Eintrags — hier ausdrücklich erlaubt, weil die Meldung sie nennt.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Die Sprache, für die der Eintrag geholt wurde (der Kandidat ist je Sprache verbraucht).</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Warum er verworfen wurde (eine der Konstanten aus <see cref="RejectReason"/>, Zusätze erlaubt).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Der Befund, wie er an die Quelle ginge („downloads with 0 bytes", „no usable cues").</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>F-M450: die Melde-Id des Untertitels bei der Quelle, wenn die Suche eine nannte.</summary>
    public string NId { get; set; } = string.Empty;

    /// <summary>F-M450: der Releasename, wie die Quelle ihn führt — die Meldung nennt das Paket mit beidem.</summary>
    public string ReleaseName { get; set; } = string.Empty;

    /// <summary>F-M450: <c>qa</c> oder <c>empty</c> — die zwei Arten von Verbrauch (siehe Klassenbeschreibung).</summary>
    public string Kind { get; set; } = "qa";

    /// <summary>F-M450: wann die Meldung von der Quelle angenommen wurde; null = (noch) nicht gemeldet.</summary>
    public DateTime? ReportedAt { get; set; }

    /// <summary>Der Meldezustand als Wort — <c>open</c> oder <c>reported</c>.</summary>
    public string ReportState { get; set; } = ReportStateOpen;

    /// <summary>Wann die Zeile zuletzt geschrieben wurde (UTC) — Diagnose.</summary>
    public DateTime Written { get; set; } = DateTime.UtcNow;

    /// <summary>Der zusammengesetzte Schlüssel der Zeile (Subjekt, Quelle, fremde Kennung) — nur für den Speicher.</summary>
    public string Key => SubjectId + "|" + Source + "|" + SourceId;
}

/// <summary>
/// F-M677 (Betreiber-Korrektur 22.11.2026): DIE MITGLIEDSCHAFT EINES PACKS — Tabelle <c>pack_track</c>.
/// <para>
/// Der Betreiber sagt: „Der Pack ist eine eigene Id <c>[xxxx]</c> und besitzt ein Array seiner Spuren."
/// Ein Array in einer Zeile wäre ein Textfeld, das niemand abfragen kann; als Zuordnungstabelle ist es
/// dasselbe Array, nur zeilenweise: eine Zeile je (Behälter, Spur), beide als echte Kennungen.
/// </para>
/// <para>
/// WARUM NICHT EIN FELD AN DER SPUR: ein Feld <c>PackId</c> an <c>track</c> behauptet, eine Spur gehöre
/// genau einem Behälter — und beim Aufstieg kam dieser Wert von der DATEI, die Spuren in vielen Sprachen
/// trägt. Gemessen am echten Bestand zog das alle 315 Spuren in den EN-HI-Pack, während die Packs für
/// FR, PT und ES leer blieben. Die Zugehörigkeit ist eine Aussage des BEHÄLTERS über seine Mitglieder;
/// deshalb steht sie hier und nicht dort.
/// </para>
/// </summary>
public class PackTrackEntity
{
    /// <summary>F-M676: die Kennung des Behälters — <c>[xxxx]</c>.</summary>
    public string PackId { get; set; } = string.Empty;

    /// <summary>
    /// F-M676: die Kennung der eingebetteten Spur — <c>#yyyyy#zzz</c>. Ein Seitenwagen ist nie Mitglied
    /// (aus Seitenwagen werden keine Packs gebaut).
    /// </summary>
    public string TrackId { get; set; } = string.Empty;

    /// <summary>Wann die Mitgliedschaft eingetragen wurde (UTC).</summary>
    public DateTime Added { get; set; } = DateTime.UtcNow;

    /// <summary>Der zusammengesetzte Schlüssel der Zeile — nur für den Speicher.</summary>
    public string Key => PackId + "|" + TrackId;
}
