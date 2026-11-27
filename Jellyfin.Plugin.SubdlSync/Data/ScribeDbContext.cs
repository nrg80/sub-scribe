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
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Jellyfin.Plugin.SubScribe.Registry;

namespace Jellyfin.Plugin.SubScribe.Data;

/// <summary>
/// F-M236: what a compaction attempt left behind. Three states, because two of them need different
/// log lines and only one of them means the file actually got smaller.
/// </summary>
/// F-M214: compacted / recovered / broken, so a failed rebuild is visible instead of silent.
public enum CompactOutcome
{
    /// <summary>The rebuild finished; the file was released and shrunk.</summary>
    Compacted,

    /// <summary>
    /// The rebuild failed, but the file was rebuilt from its own rows into a fresh one (F-M237) —
    /// usable AND smaller. Distinct from <see cref="Compacted"/>: the rows were rewritten, not the
    /// pages merely released.
    /// </summary>
    Repaired,

    /// <summary>The rebuild failed, but the database was reopened — usable, NOT shrunk.</summary>
    Recovered,

    /// <summary>The rebuild failed and the database could not be reopened — restart required.</summary>
    Broken
}

/// <summary>
/// Central data context for Sub Scribe. One SQLite file, five clearly separated areas:
/// <list type="number">
/// <item><description>0 — file compatibility (schema/plugin/Jellyfin version)</description></item>
/// <item><description>1 — OSHash cache (path → file hash, disposable)</description></item>
/// <item><description>2 — video files, each with its embedded subtitle tracks beneath it</description></item>
/// <item><description>3 — sidecar .srt files, keyed by content</description></item>
/// <item><description>4 — burned download candidates</description></item>
/// <item><description>5 — cumulative GUI counters (one row)</description></item>
/// </list>
/// <para>
/// Every area is keyed by a business key carried in the document's own Id field, never by an
/// auto-assigned id. That is the correction of F-M194: with an auto id, an upsert of a "new" row
/// can never find the row it means to update, which is what silently produced duplicates. It cannot
/// happen now because the key is computed, not assigned.
/// </para>
/// <para>
/// Why SQLite through Entity Framework Core rather than the document store this replaced: Jellyfin
/// already ships <c>Microsoft.EntityFrameworkCore.Sqlite</c> and its native <c>libe_sqlite3</c>, and
/// its <c>PluginLoadContext</c> resolves an assembly the plugin does not carry from the host. With
/// <c>ExcludeAssets=runtime</c> on that package NOTHING rides along in the ZIP, where the previous
/// engine had to ship its whole 510 KB engine. The provider's own service set (connection pooling,
/// change tracking, migrations surface, <c>VACUUM</c>) is part of what the host already pays for.
/// </para>
/// <para>
/// The store is a file of this plugin's own, never Jellyfin's <c>jellyfin.db</c>. Jellyfin offers
/// plugins no database interface at all, and its own schema is rebuilt by its migrations; a plugin
/// writing there would lose data on a server update.
/// </para>
/// </summary>
public sealed class ScribeDbContext : IDisposable
{
    /// <summary>
    /// Structure version of the data file. Increase whenever the stored shape changes in a way an
    /// older build could not survive.
    /// </summary>
    // F-M195: the schema marker the compatibility row carries.
    // F-M282/F-M283/F-M284 (02.10.2026): raised to 2. The stored shape changed — a subtitle row now
    // carries `Forced` beside `HearingImpaired`, and the media record no longer carries the download
    // mark (`SubtitlesDownloadedAt` / `SubtitlesDownloadedLanguages`). Per F-M195b there is no
    // migration: the marker makes the change visible, the older records are ignored rather than
    // rewritten, and a reset (F-M90) is the intended path.
    // F-M286 (02.10.2026): raised to 3. The status row's two quality counters were renamed
    // (`QaRejectedDownload`/`QaRejectedUpload` -> `RejectedDownload`/`RejectedUpload`) because they
    // now count every fetched-and-discarded candidate, not the QA gates alone. An older build reading
    // this file finds neither field and reports 0, which is why the marker moves with the rename.
    // F-M308 (07.10.2026): raised to 4. The status row gained `FittedToAudio` — the count of
    // downloaded subtitles the run fitted to their audio track. An older build reading this file
    // finds no such field and reports 0, which is why the marker moves with the addition.
    // F-M325 (08.10.2026): raised to 5. The FILE CHANGED FORMAT — a SQLite database replacing the
    // document store, named subdl-scribe.sqlite (F-M326). An older build opening this file finds no
    // document header and reports a broken store; the marker is what makes that loud instead of
    // silent. The old file is imported once on first start and kept as a .bak (F-M327).
    // F-M501 (15.11.2026): raised to 13. The MEDIA record gained `TvstShowId` — die Nummer der Serie
    // bei tvsubtitles.net, auf derselben Ebene wie TmdbId und ImdbId. Die Spalte wird beim Start
    // nachgezogen (das Kennungs-Modell), ein Bestand bleibt also gueltig und lesbar; der Marker wandert
    // mit, weil ein aelterer Build das Feld nicht kennt und es beim Schreiben stillschweigend
    // verlieren wuerde.
    // F-M507 (15.11.2026): raised to 14. Die dritte Quelle bekommt ihre eigenen Spalten — je Quelle und
    // Richtung eine (MoviesubtitlesDownloadId, MoviesubtitlesUploadId) plus den Upload-Zeitpunkt
    // (MoviesubtitlesUploadedAt) an `sidecars` UND `embeds`, und das MEDIA-Record die Film-Nummer der
    // Gegenseite (`MsubMovieId`, dieselbe Ebene wie TvstShowId). Nachgezogen beim Start
    // (das Kennungs-Modell), Bestandszeilen bleiben unangetastet und lesen die neuen
    // Felder als null.
    // F-M637 (Entwurf): der Marker bleibt auf 17. Der Store wechselt zwar die Datei (sub-scribe.sqlite) und
    // bekommt die Tabelle der Quellenbelege (source_marks), aber das ist additiv — der alte Bestand bleibt
    // vollständig lesbar, und ein Marker-Sprung würde die Bestandsprüfungen der Testharnisse auf einen
    // Stand ziehen, der noch nicht gebaut ist.
    // F-M668 (21.11.2026): raised to 19. Die Marken-Matrix (`source_matrix`) ist die EINZIGE Wahrheit für
    // Quellen-Urteile; die breiten Quellen-Spalten an `embeds`, `sidecars`, `media` und `packs` sind aus
    // der Datei ENTFERNT (kein doppelter Datenbestand) — die Übernahme beim Start füllt die Matrix aus
    // ihnen und legt sie danach in derselben Transaktion nieder (siehe SourceMatrixMigration). Der Marker
    // steigt, weil die Datei danach eine Tabelle trägt und Spalten NICHT mehr hat, die ein älterer Build
    // erwartet. Die Übernahme selbst hängt NICHT am Marker: sie läuft, solange Altspalten in der Datei
    // stehen, damit ein abgebrochener Lauf beim nächsten Start nachgeholt wird.
    // F-M669 (21.11.2026): raised to 20. Die Spur-Position gehört in den Schlüssel der Spur-Arten — je
    // (Medium, Quelle, Sprache, HI, Position) eine Zeile statt einer je Paar. Die Spalte `SubPos` ist
    // additiv (null bei `file`/`pack`, -1 bei einem Seitenwagen); eine Datei aus Schema 19 behält ihre
    // paarweiten Zeilen als Brücke (SubPos null), bis die nächste Marke die Position nachträgt.
    // F-M670 (21.11.2026): raised to 21. Die zwei letzten Reste eines zweiten Bestands fallen — die
    // Alt-Tabelle `source_marks` (ein Beleg je Paar aus Spur und Quelle, ohne Leser und Schreiber) und
    // die Id-Spalte `SubdlId` an `embeds`/`sidecars` („SubDL upload or download id this verdict refers
    // to"). Beide werden in DERSELBEN Transaktion in die Matrix übernommen und danach entfernt: jede
    // Quellen-Id steht danach GENAU EINMAL in `source_matrix` (`SourceId` = Eintrag/Fund, `UploadId` =
    // Einsendung). Der Marker steigt, weil die Datei danach eine Tabelle und zwei Spalten NICHT mehr hat,
    // die ein älterer Build liest bzw. schreibt.
    /// <summary>
    /// F-M671/F-M672 (22.11.2026): raised to 23 mit Schritt B. Der Aufstieg hat zwei Hälften, und beide
    /// sind EIN Stand: (a) die drei Quellen-Tabellen `upload`/`reference`/`badapple` ersetzen die
    /// Marken-Matrix `source_matrix` und die Tabelle `rejected_candidates`, und die letzte fremde
    /// Id-Spalte `OriginId` fällt aus `track`/`sidecar` (sie steht danach in `reference`); (b) die
    /// Betreiber-Korrekturen am Kennungs-Modell — das Pack hängt an der SPUR statt an der Datei
    /// (F-M677: `pack_track`), die Id selbst ist der lesbare Text mit ihrer Marke (F-M676), und der
    /// Seitenwagen trägt im dritten Feld ein `&amp;` (F-M674). Der Marker steigt, weil die
    /// Datei danach Tabellen und Spalten NICHT mehr hat, die ein älterer Build liest bzw. schreibt.
    /// Beide Hälften laufen im selben Lauf (`IdModel`), hängen aber am BESTAND, nicht am Marker — ein
    /// Zwischenstand in einer Datei wird beim nächsten Start nachgeholt.
    /// </summary>
    public const int CurrentSchemaVersion = 24;

    /// <summary>
    /// F-M671/F-M672: die DDL der vier Entity-Tabellen. Sie steht hier, wo die anderen Tabellen
    /// entstehen, und wird von <see cref="IdModel.EnsureTables"/> ausgeführt — der Migrationslauf braucht
    /// sie schon VOR dem Öffnen des Stores, also bevor es einen Kontext gibt, der sie anlegen könnte.
    /// <para>
    /// Der Name der Tabellen IST die Ebene: <c>pack</c>, <c>file</c>, <c>track</c>, <c>sidecar</c>. Die
    /// Schlüssel heißen wie ihre Kennung (<c>PackId</c>, <c>FileId</c>, <c>TrackId</c>) — es gibt keine
    /// Spalte mehr, die „Id" heißt und etwas anderes meint.
    /// </para>
    /// </summary>
    public const string FileTableDdl =
        "CREATE TABLE IF NOT EXISTS file ("
        // F-M676: die Id IST der Text — `#yyyyy`.
        + "FileId TEXT NOT NULL PRIMARY KEY, "
        // F-M673/F-M677: KEIN PackId — die Mitgliedschaft hält der Behälter (`pack_track`).

        + "OsHash TEXT NULL, "
        + "FileName TEXT NULL, "
        + "Size INTEGER NULL, "
        + "MtimeUnix INTEGER NULL, "
        + "Path TEXT NULL, "
        + "JellyfinItemId TEXT NULL, "
        + "ImdbId TEXT NULL, "
        + "TmdbId TEXT NULL, "
        + "TvstShowId TEXT NULL, "
        + "MsubMovieId TEXT NULL, "
        + "SdId TEXT NULL, "
        + "IsSeries INTEGER NOT NULL DEFAULT 0, "
        + "Season INTEGER NULL, "
        + "Episode INTEGER NULL, "
        + "LanguagesAvailable TEXT NULL, "
        + "SubtitlesUploadedAt TEXT NULL, "
        + "LastSearchUtc TEXT NULL, "
        + "LastSearchLanguages TEXT NULL, "
        + "LastSearchEmptyUtc TEXT NULL, "
        + "Created TEXT NOT NULL DEFAULT '', "
        + "LastSeen TEXT NOT NULL DEFAULT '');";

    /// <summary>
    /// F-M671/F-M673: die DDL der Spurtabelle — Kennung, Datei, Pack der SPUR, Spurnummer, Inhalt,
    /// Herkunft. Die fremde Id (<c>OriginId</c>) steht seit F-M672 in <c>reference</c>.
    /// </summary>
    public const string TrackTableDdl =
        "CREATE TABLE IF NOT EXISTS track ("
        // F-M676: `#yyyyy#zzz` — die Datei steht IN der Id. F-M677: kein PackId, kein Display.
        + "TrackId TEXT NOT NULL PRIMARY KEY, "
        + "TrackNumber INTEGER NOT NULL DEFAULT 0, "
        + "MediaHash TEXT NOT NULL DEFAULT '', "
        + "SubPos INTEGER NOT NULL DEFAULT 0, "
        + "Language TEXT NOT NULL DEFAULT '', "
        + "HearingImpaired INTEGER NULL, "
        + "Forced INTEGER NULL, "
        + "ContentHash TEXT NULL, "
        + "Status TEXT NOT NULL DEFAULT '', "
        + "Reason TEXT NULL, "
        + "StatusAt TEXT NOT NULL DEFAULT '', "
        + "Origin TEXT NULL, "
        + "DetectionAttemptedAt TEXT NULL, "
        + "DetectionOutcome TEXT NULL, "
        + "Created TEXT NOT NULL DEFAULT '', "
        + "LastSeen TEXT NOT NULL DEFAULT '');";

    /// <summary>F-M671/F-M676: die DDL der Seitenwagen-Tabelle — dieselben Ebenen, aber ohne Pack.</summary>
    public const string SidecarTableDdl =
        "CREATE TABLE IF NOT EXISTS sidecar ("
        // F-M676: `#yyyyy&zzz` — die Datei steht IN der Id. F-M671: kein Pack, also kein PackId.
        + "TrackId TEXT NOT NULL PRIMARY KEY, "
        + "TrackNumber INTEGER NOT NULL DEFAULT 0, "
        + "MediaHash TEXT NOT NULL DEFAULT '', "
        + "ContentHash TEXT NULL, "
        + "FileName TEXT NULL, "
        + "Path TEXT NULL, "
        + "Language TEXT NOT NULL DEFAULT '', "
        + "HearingImpaired INTEGER NULL, "
        + "Forced INTEGER NULL, "
        + "Status TEXT NOT NULL DEFAULT '', "
        + "Reason TEXT NULL, "
        + "StatusAt TEXT NOT NULL DEFAULT '', "
        + "Origin TEXT NULL, "
        + "ImdbId TEXT NULL, "
        + "TmdbId TEXT NULL, "
        + "IsSeries INTEGER NOT NULL DEFAULT 0, "
        + "Season INTEGER NULL, "
        + "Episode INTEGER NULL, "
        + "Created TEXT NOT NULL DEFAULT '', "
        + "LastSeen TEXT NOT NULL DEFAULT '');";

    /// <summary>F-M671/F-M676: die DDL der Pack-Tabelle — Kennung, Staffelschlüssel, Mitglieder, Signatur.</summary>
    public const string PackTableDdl =
        "CREATE TABLE IF NOT EXISTS pack ("
        // F-M676: `[xxxx]`.
        + "PackId TEXT NOT NULL PRIMARY KEY, "
        + "SeasonKey TEXT NOT NULL DEFAULT '', "
        + "Source TEXT NOT NULL DEFAULT 'subdl', "
        + "ImdbId TEXT NOT NULL DEFAULT '', "
        + "TmdbId TEXT NULL, "
        + "Season INTEGER NOT NULL DEFAULT 0, "
        + "Language TEXT NOT NULL DEFAULT '', "
        + "HearingImpaired INTEGER NULL, "
        + "ReleaseName TEXT NULL, "
        + "Signature TEXT NULL, "
        + "MtimeUnix INTEGER NULL, "
        + "EpisodeCount INTEGER NOT NULL DEFAULT 0, "
        + "Episodes TEXT NOT NULL DEFAULT '', "
        + "AbsorbedIds TEXT NULL, "
        + "Created TEXT NOT NULL DEFAULT '', "
        + "Updated TEXT NOT NULL DEFAULT '');";

    /// <summary>
    /// F-M677: die DDL der Zuordnungstabelle <c>pack_track</c> — die Mitglieder eines Behälters. Beide
    /// Spalten sind echte Kennungen und echte Fremdschlüssel: eine Mitgliedschaft ohne Behälter oder ohne
    /// Spur ist keine, und SQLite setzt das durch (Fremdschlüssel sind eingeschaltet, siehe
    /// <c>ConfigurePragmas</c>).
    /// </summary>
    public const string PackTrackTableDdl =
        "CREATE TABLE IF NOT EXISTS pack_track ("
        + "PackId TEXT NOT NULL, "
        + "TrackId TEXT NOT NULL, "
        + "Added TEXT NOT NULL DEFAULT '', "
        + "PRIMARY KEY (PackId, TrackId), "
        + "FOREIGN KEY (PackId) REFERENCES pack (PackId) ON DELETE CASCADE, "
        + "FOREIGN KEY (TrackId) REFERENCES track (TrackId) ON DELETE CASCADE);";

    /// <summary>F-M671: die Indizes der vier Entity-Tabellen (Adressen, nicht Identitäten).</summary>
    public static readonly string[] IdModelIndexDdl =
    {
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_file_OsHash ON file (OsHash);",
        "CREATE INDEX IF NOT EXISTS IX_file_ImdbId ON file (ImdbId);",
        "CREATE INDEX IF NOT EXISTS IX_file_Path ON file (Path);",
        "CREATE INDEX IF NOT EXISTS IX_file_JellyfinItemId ON file (JellyfinItemId);",
        // F-M677: der Fremdschlüssel von `pack_track` zeigt auf `track (TrackId)`. SQLite verlangt dafür
        // einen UNIQUE-Index auf der Zielspalte — bei einer Datei aus Schema 22 trägt `track` noch den
        // zusammengesetzten Schlüssel (TrackId, FileId), und ohne diesen Index wäre die Mitgliedschaft
        // nicht anlegbar ("foreign key mismatch").
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_track_TrackId ON track (TrackId);",
        "CREATE INDEX IF NOT EXISTS IX_track_MediaHash ON track (MediaHash);",
        "CREATE INDEX IF NOT EXISTS IX_track_ContentHash ON track (ContentHash);",
        "CREATE INDEX IF NOT EXISTS IX_sidecar_ContentHash ON sidecar (ContentHash);",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_pack_SeasonKey ON pack (SeasonKey);",
        "CREATE INDEX IF NOT EXISTS IX_pack_ImdbId ON pack (ImdbId);",
    };

    /// <summary>
    /// F-M677: die Indizes der Mitgliedschaft. Sie stehen NICHT in <see cref="IdModelIndexDdl"/>, weil die
    /// Tabelle erst entsteht, wenn die vier Entity-Tabellen ihre endgültige Form haben — vorher zeigt ihr
    /// Fremdschlüssel auf einen Zwischennamen.
    /// </summary>
    public static readonly string[] PackTrackIndexDdl =
    {
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_pack_track_PackTrack ON pack_track (PackId, TrackId);",
        "CREATE INDEX IF NOT EXISTS IX_pack_track_TrackId ON pack_track (TrackId);",
    };

    /// <summary>
    /// F-M672: die DDL der drei Quellen-Tabellen. Sie stehen hier, wo die anderen Tabellen entstehen,
    /// und werden von <see cref="SourceStore.EnsureTables"/> angelegt — der Migrationslauf braucht sie
    /// schon VOR dem Öffnen des Stores.
    /// <para>
    /// DIE QUELLE IST EIN WERT, KEINE TABELLE: §5 des Konzepts. Eine vierte Quelle erzeugt Zeilen in
    /// diesen drei Tabellen, keine Spalte an jeder Zeile des Stores.
    /// </para>
    /// </summary>
    public const string UploadTableDdl =
        "CREATE TABLE IF NOT EXISTS upload ("
        + "SubjectId TEXT NOT NULL, "
        + "Source TEXT NOT NULL, "
        + "UploadedAt TEXT NULL, "
        + "RefusedAt TEXT NULL, "
        + "RefusedReason TEXT NULL, "
        + "RefusalSignature TEXT NULL, "
        + "Fingerprint TEXT NULL, "
        + "MtimeUnix INTEGER NULL, "
        + "Written TEXT NOT NULL DEFAULT '', "
        + "PRIMARY KEY (SubjectId, Source));";

    /// <summary>F-M672: die DDL der Belege — der einzige Ort für eine fremde Kennung.</summary>
    public const string ReferenceTableDdl =
        "CREATE TABLE IF NOT EXISTS reference ("
        + "SubjectId TEXT NOT NULL, "
        + "Source TEXT NOT NULL, "
        + "DownloadSourceId TEXT NULL, "
        + "UploadedId TEXT NULL, "
        + "ItemId TEXT NULL, "
        + "SdId TEXT NULL, "
        + "Url TEXT NULL, "
        + "Written TEXT NOT NULL DEFAULT '', "
        + "PRIMARY KEY (SubjectId, Source));";

    /// <summary>F-M672: die DDL der schlechten Downloads — die fremde Kennung, die eine Meldung braucht.</summary>
    /// <remarks>
    /// F-M679: <c>Member</c> steht im Schlüssel, nicht nur in der Zeile — ein schlechter Untertitel ist ein
    /// einzelnes Sub, auch wenn er aus einem Staffelpack kam. Der Schlüssel lautet damit
    /// (SubjectId, Source, SourceId, Member), und <c>Member</c> ist bei einem Einzeldownload leer.
    /// </remarks>
    public const string BadAppleTableDdl =
        "CREATE TABLE IF NOT EXISTS badapple ("
        + "SubjectId TEXT NOT NULL, "
        + "Source TEXT NOT NULL, "
        + "SourceId TEXT NOT NULL DEFAULT '', "
        + "Member TEXT NOT NULL DEFAULT '', "
        + "Language TEXT NOT NULL DEFAULT '', "
        + "Reason TEXT NOT NULL DEFAULT '', "
        + "Finding TEXT NOT NULL DEFAULT '', "
        + "NId TEXT NOT NULL DEFAULT '', "
        + "ReleaseName TEXT NOT NULL DEFAULT '', "
        + "Kind TEXT NOT NULL DEFAULT 'qa', "
        + "ReportedAt TEXT NULL, "
        + "ReportState TEXT NOT NULL DEFAULT 'open', "
        + "Written TEXT NOT NULL DEFAULT '', "
        + "PRIMARY KEY (SubjectId, Source, SourceId, Member));";

    /// <summary>F-M672: die Indizes der drei Quellen-Tabellen.</summary>
    public static readonly string[] SourceTableIndexDdl =
    {
        "CREATE INDEX IF NOT EXISTS IX_upload_Source ON upload (Source);",
        "CREATE INDEX IF NOT EXISTS IX_reference_Source ON reference (Source);",
        "CREATE INDEX IF NOT EXISTS IX_reference_DownloadSourceId ON reference (DownloadSourceId);",
        "CREATE INDEX IF NOT EXISTS IX_reference_UploadedId ON reference (UploadedId);",
        "CREATE INDEX IF NOT EXISTS IX_badapple_Source ON badapple (Source);",
        "CREATE INDEX IF NOT EXISTS IX_badapple_SubjectLanguage ON badapple (SubjectId, Language);",
    };

    /// <summary>
    /// F-M667: die DDL der Marken-Matrix. Sie steht hier, wo die anderen Tabellen entstehen, und wird
    /// von <see cref="SourceMatrixMigration"/> ausgeführt — der Migrationslauf braucht sie schon
    /// VOR dem Öffnen des Stores, also bevor es einen Kontext gibt, der sie anlegen könnte.
    /// <para>
    /// F-M672: DIESE TABELLE IST EIN ZWISCHENSCHRITT GEWORDEN. Sie existiert nur noch für den einen
    /// Moment, in dem eine Datei aus Schema 17 ihre Marken noch in breiten Spalten führt: der
    /// Übernahmelauf schreibt sie dorthin und <see cref="IdModel"/> löst sie im selben Start in
    /// <c>upload</c>/<c>reference</c> auf und legt sie nieder. Ein Store, den dieser Build schreibt,
    /// kennt sie nicht mehr.
    /// </para>
    /// </summary>
    public const string SourceMatrixDdl =
        "CREATE TABLE IF NOT EXISTS source_matrix ("
        + "Id TEXT NOT NULL PRIMARY KEY, "
        + "MediaHash TEXT NOT NULL DEFAULT '', "
        + "ImdbId TEXT NOT NULL DEFAULT '', "
        + "TmdbId TEXT NOT NULL DEFAULT '', "
        + "Source TEXT NOT NULL DEFAULT '', "
        + "Kind TEXT NOT NULL DEFAULT '', "
        + "Language TEXT NOT NULL DEFAULT '', "
        + "Season INTEGER NULL, "
        + "Episode INTEGER NULL, "
        // F-M669: die Spur-Position im Video — Teil des Schlüssels der Spur-Arten, null bei file/pack.
        + "SubPos INTEGER NULL, "
        + "HearingImpaired INTEGER NULL, "
        + "ClosedAt TEXT NULL, "
        + "Fingerprint TEXT NULL, "
        + "MtimeUnix INTEGER NULL, "
        + "SourceId TEXT NULL, "
        // F-M668: die Einsende-Id liegt NEBEN der Eintrags-Id (beide Nummernkreise sind verschieden).
        + "UploadId TEXT NULL, "
        + "RefusalReason TEXT NULL, "
        + "RefusedAt TEXT NULL, "
        // F-M650/F-M668: die Staffel-Signatur, auf der eine Absage beruht.
        + "RefusalSignature TEXT NULL, "
        + "Written TEXT NOT NULL DEFAULT '');";

    /// <summary>F-M667: die Indizes der Marken-Matrix — dieselbe Begründung wie <see cref="SourceMatrixDdl"/>.</summary>
    public static readonly string[] SourceMatrixIndexDdl =
    {
        "CREATE INDEX IF NOT EXISTS IX_source_matrix_MediaHash ON source_matrix (MediaHash);",
        "CREATE INDEX IF NOT EXISTS IX_source_matrix_ImdbId ON source_matrix (ImdbId);",
        "CREATE INDEX IF NOT EXISTS IX_source_matrix_TmdbId ON source_matrix (TmdbId);",
        "CREATE INDEX IF NOT EXISTS IX_source_matrix_SourceKind ON source_matrix (Source, Kind);",
    };

    private readonly string _dbPath;
    private readonly ILogger? _logger;
    private readonly ScribeSqliteContext _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScribeDbContext"/> class.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <param name="logger">Optional logger.</param>
    public ScribeDbContext(string dataDir, ILogger? logger = null)
        : this(dataDir, null, logger)
    {
    }

    /// <summary>
    /// F-M637: derselbe Aufbau, aber auf einer ausdrücklich genannten Datei und OHNE den Formatwechsel.
    /// Diesen Weg nimmt nur der Wechsel selbst — er öffnet die Datei, die er gerade prüft.
    /// </summary>
    /// <param name="dataDir">Plugin data directory.</param>
    /// <param name="explicitPath">Explicit store file, or null for the normal path.</param>
    /// <param name="logger">Optional logger.</param>
    internal ScribeDbContext(string dataDir, string? explicitPath, ILogger? logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDir);
        _dbPath = DbFiles.PathIn(dataDir);

        if (explicitPath != null)
        {
            _dbPath = explicitPath;
        }
        else
        {
            // F-M327/F-M637: der Formatwechsel läuft VOR dem Öffnen des Stores. Er ist der einzige Pfad,
            // der die Vorgängerdatei liest. Fehlt sie, startet der Store leer (F-M639); stimmt die
            // Prüfsumme nicht oder bricht das Schreiben ab, bleibt die Vorgängerdatei die live Datei und
            // es wurde nichts angefasst (F-M637). Die Vorgängerdatei wird nie gelöscht (F-M638).
            var wechsel = StoreFileSwitch.Run(dataDir, logger);
            _dbPath = wechsel.LivePath;
        }

        // F-M667/F-M668: die Übernahme in die Marken-Matrix läuft VOR dem Öffnen des Stores — sie sichert
        // zuerst die alte Datei (Zeitstempel im Namen), füllt dann die Matrix aus den breiten Spalten und
        // legt diese in DERSELBEN Transaktion nieder. Danach gibt es genau eine Wahrheit. Das Ergebnis
        // wird festgehalten, damit der Formatwechsel (F-M637) es prüfen kann, ohne die Arbeit zu
        // wiederholen.
        //
        // Sie läuft auf BEIDEN Wegen: die Datei, die der Formatwechsel gerade gebaut hat, braucht
        // dieselbe Füllung. Nur die Sicherung entfällt dort — die Datei, aus der sie entstand, bleibt
        // nach F-M638 liegen und ist die Sicherung selbst.
        MatrixOutcome = SourceMatrixMigration.Run(_dbPath, logger, backup: explicitPath == null);

        // F-M671: die Übernahme in das Kennungs-Modell (Schema 22) läuft unmittelbar danach — und wie die
        // Marken-Übernahme VOR dem Öffnen des Stores, weil sie die Alt-Tabellen liest, die das neue Modell
        // nicht mehr kennt. EINE Sicherung für den ganzen Aufstieg: hat die Marken-Übernahme eben eine
        // angelegt, ist sie die Sicherung dieses Starts (der Bestand darin ist der Stand VOR beiden
        // Schritten); sonst legt dieser Lauf seine eigene an. Ein zweiter Start findet nichts mehr zu tun
        // und kopiert nichts.
        IdOutcome = IdModel.Run(_dbPath, logger, backup: explicitPath == null && MatrixOutcome?.BackupPath == null);
        _db = new ScribeSqliteContext(_dbPath);

        // F-M514: the context itself warns now (a rejected row, a rebuilt tracker), so it needs the
        // same logger the registries log to — one line, one place.
        _db.Logger = logger;
        _db.Database.EnsureCreated();
        IdModel.EnsureTables(_db, logger);
        EnsureAiredSeasonsTable();
        EnsurePackSeasonsTable();
        EnsureSourceRefusalsTable();
        EnsureTitleFailuresTable();
        // F-M672: die drei Quellen-Tabellen (upload, reference, badapple) stehen ab Schema 23 dort, wo
        // die Marken-Matrix stand. Sie werden VOR dem Modell angelegt, weil der Kontext sie liest.
        SourceStore.EnsureTables(_db, logger);
        EnsureReportColumns();

        EnsureStatusColumns();
        NormaliseLanguages();
        _db.ConfigurePragmas();

        Meta = new ScribeSet<MetaEntity>(_db, e => e.Id);
        Oshashes = new ScribeSet<OshashEntity>(_db, e => e.Id);
        Files = new ScribeSet<FileEntity>(_db, e => e.OsHash ?? string.Empty, onNew: AssignNewFile);
        Tracks = new ScribeSet<TrackEntity>(_db, e => e.MediaHash + "|" + e.SubPos, onNew: AssignNewTrack);
        Sidecars = new ScribeSet<SidecarEntity>(_db, e => e.ContentHash ?? string.Empty, onNew: AssignNewSidecar);
        Counters = new ScribeSet<CounterEntity>(_db, e => e.Key);
        Runs = new ScribeSet<PipelineRunEntity>(_db, e => e.Id.ToString(CultureInfo.InvariantCulture));
        StatusStats = new ScribeSet<StatusStatsEntity>(_db, e => e.Id);
        WorkerRuns = new ScribeSet<WorkerRunEntity>(_db, e => e.Id);
        Packs = new ScribeSet<PackEntity>(_db, e => e.SeasonKey, onNew: AssignNewPack);
        PackTracks = new ScribeSet<PackTrackEntity>(_db, e => e.Key);
        Uploads = new ScribeSet<UploadEntity>(_db, e => e.Key);
        References = new ScribeSet<ReferenceEntity>(_db, e => e.Key);
        BadApples = new ScribeSet<BadAppleEntity>(_db, e => e.Key);

        CheckSchemaVersion();
    }

    /// <summary>
    /// F-M671: vergibt einer neuen DATEI ihre Kennung. Der Hash ist die Adresse, unter der die Zeile
    /// gefunden wird; die Kennung ist die Identität und wird nie wieder angefasst.
    /// </summary>
    /// <param name="file">The new row, its <c>OsHash</c> already set by the caller.</param>
    private void AssignNewFile(FileEntity file)
    {
        if (string.IsNullOrEmpty(file.FileId))
        {
            // F-M676: die Id IST der Text — `#yyyyy`, geprüft gegen den Bestand.
            file.FileId = ScribeIds.FileIdOf(ScribeIds.NewFileId(id => Files.FindAll().Any(f => f.FileId == ScribeIds.FileIdOf(id))));
        }

        if (file.Created == default)
        {
            file.Created = DateTime.UtcNow;
        }

        if (string.IsNullOrEmpty(file.FileName) && !string.IsNullOrEmpty(file.Path))
        {
            file.FileName = Path.GetFileName(file.Path);
        }
    }

    /// <summary>
    /// F-M671: vergibt einer neuen eingebetteten SPUR Kennung, Datei, Pack und Spurnummer. Der Nummernkreis
    /// ist der der DATEI und umfasst eingebettete Spuren UND Seitenwagen — deshalb wird hier über beide
    /// Areale gezählt. Eine Nummer, die einmal vergeben war, wird nie wiederverwendet.
    /// </summary>
    /// <param name="track">The new row, its <c>MediaHash</c> and <c>SubPos</c> already set.</param>
    private void AssignNewTrack(TrackEntity track)
    {
        // F-M676: die Datei steht IN der Spur-Id. Die Adresse, unter der der Aufrufer die Spur führt, ist
        // (Medien-Hash, Position) — daraus wird die Datei und aus ihr die Id.
        string? fileId = string.IsNullOrEmpty(track.MediaHash)
            ? ScribeIds.FileOf(track.TrackId)
            : Files.FindAll().FirstOrDefault(f => string.Equals(f.OsHash, track.MediaHash, StringComparison.Ordinal))?.FileId;
        fileId ??= ScribeIds.FileOf(track.TrackId);

        if (string.IsNullOrEmpty(fileId))
        {
            // Ohne Datei gibt es keine Spur-Id — dieselbe Regel wie „keine Marke ohne Subjekt".
            return;
        }

        var datei = Files.FindById(fileId);
        string? segment = ScribeIds.TrackPartOf(track.TrackId);

        if (track.TrackNumber <= 0 || TrackNumbers.IsTaken(TracksOf(fileId), SidecarsOf(fileId), track.TrackNumber))
        {
            track.TrackNumber = TrackNumbers.NextFree(TracksOf(fileId), SidecarsOf(fileId));
        }

        if (string.IsNullOrEmpty(segment)
            || TracksOf(fileId).Any(t => string.Equals(ScribeIds.TrackPartOf(t.TrackId), segment, StringComparison.Ordinal))
            || SidecarsOf(fileId).Any(s => string.Equals(ScribeIds.TrackPartOf(s.TrackId), segment, StringComparison.Ordinal)))
        {
            segment = ScribeIds.NewTrackId(id =>
                TracksOf(fileId).Any(t => string.Equals(ScribeIds.TrackPartOf(t.TrackId), id, StringComparison.Ordinal))
                || SidecarsOf(fileId).Any(s => string.Equals(ScribeIds.TrackPartOf(s.TrackId), id, StringComparison.Ordinal)));
        }

        track.TrackId = ScribeIds.TrackIdOf(fileId, segment);

        if (track.Created == default)
        {
            track.Created = DateTime.UtcNow;
        }

        track.LastSeen = DateTime.UtcNow;

        // F-M677: die MITGLIEDSCHAFT hält der Behälter. Passt ein Pack zu (Serie, Staffel, Sprache,
        // Variante) dieser Spur, bekommt es eine Zeile in `pack_track`.
        string? packId = SourceStore.PackForTrack(datei, track.Language, track.HearingImpaired == true);
        if (!string.IsNullOrEmpty(packId))
        {
            PackTracks.Upsert(new PackTrackEntity { PackId = packId!, TrackId = track.TrackId });
        }
    }

    /// <summary>
    /// F-M671: vergibt einem neuen SEITENWAGEN Kennung, Datei und Spurnummer aus demselben Kreis wie die
    /// eingebetteten Spuren seiner Datei. Das Pack bleibt leer — die Anzeige eines Seitenwagens ist
    /// <c>0000-yyyyy-zzz</c>.
    /// </summary>
    /// <param name="sidecar">The new row, its <c>MediaHash</c> and <c>ContentHash</c> already set.</param>
    private void AssignNewSidecar(SidecarEntity sidecar)
    {
        string? fileId = string.IsNullOrEmpty(sidecar.MediaHash)
            ? ScribeIds.FileOf(sidecar.TrackId)
            : Files.FindAll().FirstOrDefault(f => string.Equals(f.OsHash, sidecar.MediaHash, StringComparison.Ordinal))?.FileId;
        fileId ??= ScribeIds.FileOf(sidecar.TrackId);

        if (string.IsNullOrEmpty(fileId))
        {
            return;
        }

        string? segment = ScribeIds.TrackPartOf(sidecar.TrackId);

        if (sidecar.TrackNumber <= 0 || TrackNumbers.IsTaken(TracksOf(fileId), SidecarsOf(fileId), sidecar.TrackNumber))
        {
            sidecar.TrackNumber = TrackNumbers.NextFree(TracksOf(fileId), SidecarsOf(fileId));
        }

        if (string.IsNullOrEmpty(segment)
            || TracksOf(fileId).Any(t => string.Equals(ScribeIds.TrackPartOf(t.TrackId), segment, StringComparison.Ordinal))
            || SidecarsOf(fileId).Any(s => string.Equals(ScribeIds.TrackPartOf(s.TrackId), segment, StringComparison.Ordinal)))
        {
            segment = ScribeIds.NewTrackId(id =>
                TracksOf(fileId).Any(t => string.Equals(ScribeIds.TrackPartOf(t.TrackId), id, StringComparison.Ordinal))
                || SidecarsOf(fileId).Any(s => string.Equals(ScribeIds.TrackPartOf(s.TrackId), id, StringComparison.Ordinal)));
        }

        sidecar.TrackId = ScribeIds.SidecarIdOf(fileId, segment);

        if (sidecar.Created == default)
        {
            sidecar.Created = DateTime.UtcNow;
        }

        sidecar.LastSeen = DateTime.UtcNow;
    }

    /// <summary>F-M671: vergibt einem neuen PACK seine Kennung; der Staffelschlüssel bleibt die Adresse.</summary>
    /// <param name="pack">The new row, its <c>SeasonKey</c> already set.</param>
    private void AssignNewPack(PackEntity pack)
    {
        if (string.IsNullOrEmpty(pack.PackId))
        {
            // F-M676: `[xxxx]`, geprüft gegen den Bestand.
            pack.PackId = ScribeIds.PackIdOf(ScribeIds.NewPackId(
                id => Packs.FindAll().Any(p => p.PackId == ScribeIds.PackIdOf(id))));
        }

        if (pack.Created == default)
        {
            pack.Created = DateTime.UtcNow;
        }
    }

    private List<TrackEntity> TracksOf(string? fileId)
        => string.IsNullOrEmpty(fileId)
            ? new List<TrackEntity>()
            : Tracks.FindAll().Where(t => string.Equals(t.FileId, fileId, StringComparison.Ordinal)).ToList();

    private List<SidecarEntity> SidecarsOf(string? fileId)
        => string.IsNullOrEmpty(fileId)
            ? new List<SidecarEntity>()
            : Sidecars.FindAll().Where(s => string.Equals(s.FileId, fileId, StringComparison.Ordinal)).ToList();

    /// <summary>Area 0: the single compatibility record describing this data file.</summary>
    public ScribeSet<MetaEntity> Meta { get; }

    /// <summary>Area 1: OSHash cache, keyed by media path.</summary>
    public ScribeSet<OshashEntity> Oshashes { get; }

    /// <summary>Area 2: one record per video file, addressed by its OSHash and identified by its FileId.</summary>
    public ScribeSet<FileEntity> Files { get; }

    /// <summary>Area 2, beneath the file: embedded subtitle tracks, addressed by "&lt;osHash&gt;|&lt;position&gt;".</summary>
    public ScribeSet<TrackEntity> Tracks { get; }

    /// <summary>Area 3: sidecar subtitle files, keyed by content hash.</summary>
    public ScribeSet<SidecarEntity> Sidecars { get; }

    /// <summary>Retry and fail-budget counters, keyed by the namespaced counter key.</summary>
    public ScribeSet<CounterEntity> Counters { get; }

    /// <summary>Pipeline run bookkeeping.</summary>
    public ScribeSet<PipelineRunEntity> Runs { get; }

    /// <summary>
    /// Cumulative subtitle counters for the GUI (single row, key "status"). Stored here rather than
    /// in the configuration XML so a database reset resets them too — see <see cref="StatusStatsEntity"/>.
    /// </summary>
    public ScribeSet<StatusStatsEntity> StatusStats { get; }

    /// <summary>
    /// Area 6: the LAST run of each worker, keyed by the worker's task key (one row per worker, never
    /// a history). Read by the configuration page's "Workers" section — see <see cref="WorkerRunEntity"/>.
    /// </summary>
    public ScribeSet<WorkerRunEntity> WorkerRuns { get; }

    /// <summary>
    /// Area 7: the season packs this plugin published, keyed by "&lt;imdb&gt;|S&lt;season&gt;|&lt;lang&gt;|&lt;hi|plain&gt;".
    /// One row per pack, never a history — it answers "was this season already packed in this
    /// language, and with which members" — see <see cref="PackEntity"/>.
    /// </summary>
    public ScribeSet<PackEntity> Packs { get; }

    /// <summary>
    /// F-M677: die Mitglieder der Behälter — eine Zeile je (Pack, Spur). Der Weg „welche Spuren gehören zu
    /// diesem Pack" und seine Umkehrung; aus Seitenwagen wird keine Zeile gebaut.
    /// </summary>
    public ScribeSet<PackTrackEntity> PackTracks { get; }

    /// <summary>
    /// F-M672: die drei Quellen-Tabellen. <c>Uploads</c> führt die URTEILE (eine Zeile je Subjekt und
    /// Quelle), <c>References</c> die BELEGE (die fremden Kennungen) und <c>BadApples</c> die schlechten
    /// Downloads samt der Nummer, die eine Meldung an die Quelle braucht. Gelesen und geschrieben wird
    /// über <see cref="SourceStore"/> — hier stehen nur die Bereiche.
    /// </summary>
    public ScribeSet<UploadEntity> Uploads { get; }

    /// <summary>F-M672: die Belege je Subjekt und Quelle — der einzige Ort für eine fremde Kennung.</summary>
    public ScribeSet<ReferenceEntity> References { get; }

    /// <summary>F-M672: die schlechten Downloads (vormals <c>rejected_candidates</c>).</summary>
    public ScribeSet<BadAppleEntity> BadApples { get; }

    /// <summary>
    /// F-M668: was die Übernahme in die Marken-Matrix bei DIESEM Öffnen getan hat — Zeilen gelesen,
    /// Marken geschrieben, Sicherung angelegt. Null, wenn nichts zu übernehmen war. Der Formatwechsel
    /// (F-M637) prüft damit, dass die neue Datei die Marken der Vorgängerin wirklich trägt.
    /// </summary>
    public SourceMatrixMigration.Outcome? MatrixOutcome { get; private set; }

    /// <summary>
    /// F-M671: was die Übernahme in das Kennungs-Modell (Schema 22) bei DIESEM Öffnen getan hat — Zeilen je
    /// Ebene gelesen und geschrieben, Kennungen vergeben, Spurnummern verteilt, Sicherung angelegt. Null,
    /// wenn nichts zu übernehmen war (die Datei trägt das Modell schon).
    /// </summary>
    public IdModel.Outcome? IdOutcome { get; private set; }

    /// <summary>
    /// True when the data file was written by a NEWER plugin build than this one. Callers must not
    /// write in that case: the newer build may store fields this one would silently drop.
    /// </summary>
    public bool IsWrittenByNewerVersion { get; private set; }

    /// <summary>
    /// F-M542: the EF context itself, for a store that owns its own table (the TMDb answers). The
    /// wrapper's other members stay the normal way in; this is the one door for a new table that has no
    /// wrapper API yet.
    /// </summary>
    public ScribeSqliteContext Context => _db;

    /// <summary>
    /// Reads area 0 and decides whether this build may write to the file.
    /// </summary>
    private void CheckSchemaVersion()
    {
        try
        {
            var meta = Meta.FindById("db");
            if (meta == null)
            {
                meta = new MetaEntity { Id = "db", SchemaVersion = CurrentSchemaVersion };
                Meta.Upsert(meta);
                LogUtil.Normal(_logger,
                    "[Sub Scribe] data file initialised, schema version {Version}.", CurrentSchemaVersion);
                return;
            }

            if (meta.SchemaVersion > CurrentSchemaVersion)
            {
                IsWrittenByNewerVersion = true;
                _logger?.LogError(
                    "[Sub Scribe] data file schema version {Found} is NEWER than this plugin understands ({Current}). "
                    + "Writing is disabled for this session so the newer data is not damaged. Update the plugin.",
                    meta.SchemaVersion, CurrentSchemaVersion);
                return;
            }

            if (meta.SchemaVersion < CurrentSchemaVersion)
            {
                _logger?.LogWarning(
                    "[Sub Scribe] data file schema version {Found} is older than {Current}; upgrading the marker. "
                    + "Records from other schema versions are ignored, not rewritten.",
                    meta.SchemaVersion, CurrentSchemaVersion);
                meta.SchemaVersion = CurrentSchemaVersion;
                Meta.Upsert(meta);
            }
        }
        catch (Exception ex)
        {
            // A broken compatibility check must never stop the plugin from starting.
            _logger?.LogWarning("[Sub Scribe] schema version check failed (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M601: creates the <c>aired_seasons</c> table on a data file that predates it. Same shape and
    /// same reason as the other ensured tables: <c>EnsureCreated</c> builds a schema only
    /// when the FILE is new, and a missing table would throw on the first stored answer instead of at
    /// startup. Name and mapping are identical on purpose (<c>aired_seasons</c> both here and in
    /// <c>OnModelCreating</c>) — a table that is created under one name and read under another is
    /// dead weight that looks like a working cache.
    /// </summary>
    /// <summary>
    /// F-M604: the stored pack verdicts. Same reasoning as <see cref="EnsureAiredSeasonsTable"/> — the
    /// table is created here and read under the very same name in <c>OnModelCreating</c>.
    /// </summary>
    /// <summary>
    /// F-M605: the remembered source refusals. Same reasoning as <see cref="EnsurePackSeasonsTable"/>.
    /// </summary>
    /// <summary>
    /// F-M450: adds the report columns to <c>rejected_candidates</c> on a data file that predates them.
    /// <para>
    /// <c>EnsureCreated</c> builds a schema only when the FILE is new and never alters an existing table,
    /// so an upgraded installation would otherwise keep five columns while every write names ten — and
    /// the failure would land in the middle of a download run. A failure is logged and swallowed:
    /// reporting must never stop the plugin from starting.
    /// </para>
    /// <para>
    /// F-M671/F-M672: die Herkunfts-Spalten an <c>embeds</c>/<c>sidecars</c> werden hier NICHT mehr
    /// nachgezogen — diese Tabellen gibt es nicht mehr. Die Herkunft einer Spur steht als
    /// Quellen-NAME an <c>track</c>/<c>sidecar</c>, die Id in <c>reference</c>; das Kennungs-Modell legt
    /// beides von Anfang an an.
    /// </para>
    /// </summary>
    private void EnsureReportColumns()
    {
        // F-M672: die Melde-Felder (NId, ReleaseName, Finding, Kind, ReportedUtc) standen an
        // `rejected_candidates`. Diese Tabelle gibt es nicht mehr — sie sind Spalten von `badapple` und
        // entstehen mit ihrer DDL. Der Nachzug für eine Datei aus Schema 20/21 bleibt trotzdem nötig,
        // weil sie die Tabelle noch MIT diesen Spalten führt und sie gleich darauf fällt.
        try
        {
            SourceStore.EnsureBadAppleColumns(_db, _logger);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not add the report columns (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M608: how often creating and finding a title failed, and whether it is closed for good.
    /// </summary>
    private void EnsureTitleFailuresTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS title_failures ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Attempts INTEGER NOT NULL DEFAULT 0, "
                + "Closed INTEGER NOT NULL DEFAULT 0, "
                + "Reason TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the title_failures table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M667: legt die Marken-Matrix auf einer Datei an, die sie noch nicht kennt. Dieselbe Begruendung
    /// wie bei <see cref="IdModel.EnsureTables"/> — <c>EnsureCreated</c> baut ein Schema nur bei einer NEUEN
    /// Datei. Der Migrationslauf legt die Tabelle bereits VOR dem Oeffnen an; dieser Schritt ist damit
    /// der zweite, harmlose Weg fuer einen Store, der ohne Migration geoeffnet wurde (ausdrueckliche
    /// Datei), und fuer eine Datei, deren Uebernahme abgebrochen ist.
    /// </summary>
    private void EnsureSourceRefusalsTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS source_refusals ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Signature TEXT NOT NULL DEFAULT '', "
                + "Reason TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the source_refusals table (continuing): {Msg}", ex.Message);
        }
    }

    private void EnsurePackSeasonsTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS pack_seasons ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Signature TEXT NOT NULL DEFAULT '', "
                + "Verdict TEXT NOT NULL DEFAULT '', "
                + "Reason TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the pack_seasons table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M790: steht dieser gespeicherte Wert schon im Kanon? Leer gilt als in Ordnung (die Zeile sagt
    /// nichts ueber eine Sprache).
    /// </summary>
    public static bool SpracheAusserKanon(string? wert)
    {
        if (string.IsNullOrWhiteSpace(wert))
        {
            return false;
        }

        // F-M791: ein zweistelliger Wert ist NIE der Kanon — auch dann nicht, wenn die Tabelle ihn
        // nicht uebersetzen kann (etwa eu, gl). Er wird nicht erfunden, sondern gemeldet.
        if (wert.Trim().Length == 2)
        {
            return true;
        }

        return !string.Equals(Language.LanguageMapper.Canonical(wert), wert, StringComparison.Ordinal);
    }

    /// <summary>
    /// F-M790: dasselbe fuer eine gespeicherte Liste.
    /// </summary>
    public static bool ListeAusserKanon(string? liste)
    {
        if (string.IsNullOrWhiteSpace(liste))
        {
            return false;
        }

        if (!string.Equals(Language.LanguageMapper.CanonicalList(liste), liste, StringComparison.Ordinal))
        {
            return true;
        }

        // F-M791: auch eine Liste kann ein Glied tragen, das die Tabelle nicht kennt.
        return liste.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(glied => glied.Length == 2);
    }

    private void EnsureAiredSeasonsTable()
    {
        try
        {
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS aired_seasons ("
                + "Id TEXT NOT NULL PRIMARY KEY, "
                + "Value TEXT NOT NULL DEFAULT '', "
                + "Written TEXT NOT NULL DEFAULT '');");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not create the aired_seasons table (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M557 (Operator-Anweisung 17.11.2026: „Ich würde iso nehmen und immer auf die jeweiligen Quellen
    /// umsetzen. Per Software-Routine"): Schema 16 schreibt die Sprache EINMAL in ISO 639-2
    /// (<c>ENG</c>, <c>DEU</c>) in den Store; jede Quelle bekommt ihre Schreibweise erst an der Kante.
    /// <para>
    /// Diese Wanderung zieht den Bestand nach: jede Zeile in <c>embeds</c> und <c>sidecars</c> bekommt ihr
    /// ISO-Kennzeichen, und die Zugeh-Stempel werden geleert — eine Signatur aus der alten Schreibweise
    /// darf keine Datei als fertig behaupten.
    /// </para>
    /// </summary>
    private void NormaliseLanguages()
    {
        try
        {
            int geaendert = 0;
            foreach (var row in _db.TrackRows.ToList())
            {
                string? iso = Language.LanguageMapper.Canonical(row.Language);
                if (iso != null && !string.Equals(iso, row.Language, StringComparison.Ordinal))
                {
                    row.Language = iso;
                    _db.TrackRows.Update(row);
                    geaendert++;
                }
            }

            foreach (var row in _db.SidecarRows.ToList())
            {
                string? iso = Language.LanguageMapper.Canonical(row.Language);
                if (iso != null && !string.Equals(iso, row.Language, StringComparison.Ordinal))
                {
                    row.Language = iso;
                    _db.SidecarRows.Update(row);
                    geaendert++;
                }
            }

            // F-M790: seit 12.3.8.78 zielt der Kanon auf ISO 639-2/T (deu, ces, nld) statt auf die
            // bibliografische Form (ger, cze, dut). Tracks und Sidecars zieht der Block oben mit; die
            // drei uebrigen Spalten brauchen denselben Zug, sonst stuende in einem Bestand zweierlei.
            foreach (var row in _db.PackRows.ToList())
            {
                string? iso = Language.LanguageMapper.Canonical(row.Language);
                if (iso != null && !string.Equals(iso, row.Language, StringComparison.Ordinal))
                {
                    row.Language = iso;
                    _db.PackRows.Update(row);
                    geaendert++;
                }
            }

            foreach (var row in _db.BadAppleRows.ToList())
            {
                string? iso = Language.LanguageMapper.Canonical(row.Language);
                if (iso != null && !string.Equals(iso, row.Language, StringComparison.Ordinal))
                {
                    row.Language = iso;
                    _db.BadAppleRows.Update(row);
                    geaendert++;
                }
            }

            // F-M790: zwei Textspalten tragen ebenfalls Sprachen — die Abdeckung der Datei
            // (`LanguagesAvailable`, aus den Spuren gebildet) und der Suchstempel (`LastSearchLanguages`,
            // die Wunschliste des letzten Laufs). Beide werden gliedweise in den Kanon gezogen, sonst
            // stuende in einer Datei-Zeile "EN,DE" neben Spuren, die DEU sagen.
            foreach (var row in _db.FileRows.ToList())
            {
                bool geaendertHier = false;
                if (row.LanguagesAvailable != null)
                {
                    string kanonListe = Language.LanguageMapper.CanonicalList(row.LanguagesAvailable);
                    if (!string.Equals(kanonListe, row.LanguagesAvailable, StringComparison.Ordinal))
                    {
                        row.LanguagesAvailable = kanonListe;
                        geaendertHier = true;
                    }
                }

                if (row.LastSearchLanguages != null)
                {
                    string kanonStempel = Language.LanguageMapper.CanonicalList(row.LastSearchLanguages);
                    if (!string.Equals(kanonStempel, row.LastSearchLanguages, StringComparison.Ordinal))
                    {
                        row.LastSearchLanguages = kanonStempel;
                        geaendertHier = true;
                    }
                }

                if (geaendertHier)
                {
                    _db.FileRows.Update(row);
                    geaendert++;
                }
            }

            // F-M790: die Urteile der Pack-Phase tragen die Sprache im SCHLUESSEL ("imdb|staffel|GER
            // non-HI"). Sie werden MITGENOMMEN, nicht vergessen: der Schluessel ist Text, der Tag traegt
            // Sprache und Variante und sonst nichts, also laesst er sich sauber umschreiben. Die Staffel
            // bleibt damit angesehen. Steht unter dem neuen Schluessel schon eine Zeile, gilt die juengere
            // Aussage — sie ist die ueber den heutigen Mitgliederstand.
            int urteile = 0;
            var urteilsZeilen = _db.PackSeasonRows.ToList();
            var urteileNachId = urteilsZeilen.ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var row in urteilsZeilen)
            {
                int zweiter = row.Id.IndexOf('|', row.Id.IndexOf('|', StringComparison.Ordinal) + 1);
                if (zweiter <= 0 || zweiter + 1 >= row.Id.Length)
                {
                    continue;
                }

                string kopf = row.Id[..(zweiter + 1)];
                string tag = row.Id[(zweiter + 1)..];
                int leer = tag.IndexOf(' ', StringComparison.Ordinal);
                string sprache = leer < 0 ? tag : tag[..leer];
                string variante = leer < 0 ? string.Empty : tag[leer..];
                if (sprache.Length != 3)
                {
                    continue;
                }

                string? kanon = Language.LanguageMapper.Canonical(sprache);
                if (kanon == null || string.Equals(kanon, sprache, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string neuerSchluessel = kopf + kanon.ToUpperInvariant() + variante;
                _db.PackSeasonRows.Remove(row);
                if (urteileNachId.TryGetValue(neuerSchluessel, out var ziel))
                {
                    if (row.Written > ziel.Written)
                    {
                        ziel.Signature = row.Signature;
                        ziel.Verdict = row.Verdict;
                        ziel.Reason = row.Reason;
                        ziel.Written = row.Written;
                        _db.PackSeasonRows.Update(ziel);
                    }
                }
                else
                {
                    var umgezogen = new Registry.PackSeasonEntity
                    {
                        Id = neuerSchluessel,
                        Signature = row.Signature,
                        Verdict = row.Verdict,
                        Reason = row.Reason,
                        Written = row.Written
                    };
                    _db.PackSeasonRows.Add(umgezogen);
                    urteileNachId[neuerSchluessel] = umgezogen;
                }

                urteile++;
            }

            // F-M790: die Gegenprobe. Nach allen Zuegen wird gezaehlt, was NICHT im Kanon steht — in allen
            // sechs Stellen, an denen der Store eine Sprache haelt. Die Zahl gehoert ins Protokoll, weil
            // ein halb gewanderter Bestand schlimmer ist als ein alter: die eine Haelfte findet die andere
            // nicht mehr.
            int ausserKanon = 0;
            foreach (var row in _db.TrackRows.ToList())
            {
                ausserKanon += SpracheAusserKanon(row.Language) ? 1 : 0;
            }

            foreach (var row in _db.SidecarRows.ToList())
            {
                ausserKanon += SpracheAusserKanon(row.Language) ? 1 : 0;
            }

            foreach (var row in _db.PackRows.ToList())
            {
                ausserKanon += SpracheAusserKanon(row.Language) ? 1 : 0;
            }

            foreach (var row in _db.BadAppleRows.ToList())
            {
                ausserKanon += SpracheAusserKanon(row.Language) ? 1 : 0;
            }

            foreach (var row in _db.FileRows.ToList())
            {
                ausserKanon += ListeAusserKanon(row.LanguagesAvailable) ? 1 : 0;
                ausserKanon += ListeAusserKanon(row.LastSearchLanguages) ? 1 : 0;
            }

            foreach (var row in _db.PackSeasonRows.ToList())
            {
                int zweiter = row.Id.IndexOf('|', row.Id.IndexOf('|', StringComparison.Ordinal) + 1);
                string tag = zweiter > 0 && zweiter + 1 < row.Id.Length ? row.Id[(zweiter + 1)..] : string.Empty;
                int leer = tag.IndexOf(' ', StringComparison.Ordinal);
                ausserKanon += SpracheAusserKanon(leer < 0 ? tag : tag[..leer]) ? 1 : 0;
            }

            if (ausserKanon > 0)
            {
                // F-M791: die Zahl allein hilft niemandem — der Befund nennt Ort, Wert und Haeufigkeit,
                // damit ein Rest, der nicht wandern kann, benannt ist statt vermutet.
                var befund = new SortedDictionary<string, int>(StringComparer.Ordinal);
                void Merke(string ort, string? wert)
                {
                    if (string.IsNullOrWhiteSpace(wert))
                    {
                        return;
                    }

                    string schluessel = ort + "=" + wert;
                    befund[schluessel] = befund.TryGetValue(schluessel, out int n) ? n + 1 : 1;
                }

                foreach (var row in _db.TrackRows.ToList())
                {
                    if (SpracheAusserKanon(row.Language))
                    {
                        Merke("track", row.Language);
                    }
                }

                foreach (var row in _db.SidecarRows.ToList())
                {
                    if (SpracheAusserKanon(row.Language))
                    {
                        Merke("sidecar", row.Language);
                    }
                }

                foreach (var row in _db.PackRows.ToList())
                {
                    if (SpracheAusserKanon(row.Language))
                    {
                        Merke("pack", row.Language);
                    }
                }

                foreach (var row in _db.BadAppleRows.ToList())
                {
                    if (SpracheAusserKanon(row.Language))
                    {
                        Merke("badapple", row.Language);
                    }
                }

                foreach (var row in _db.FileRows.ToList())
                {
                    if (ListeAusserKanon(row.LanguagesAvailable))
                    {
                        Merke("file.languages", row.LanguagesAvailable);
                    }

                    if (ListeAusserKanon(row.LastSearchLanguages))
                    {
                        Merke("file.stamp", row.LastSearchLanguages);
                    }
                }

                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] data file: {Count} stored language value(s) still outside the canon, in {Places} value(s) — the migration is incomplete (F-M790).",
                    ausserKanon,
                    befund.Count);
                foreach (var eintrag in befund.OrderByDescending(x => x.Value).Take(8))
                {
                    LogUtil.Normal(
                        _logger,
                        "[Sub Scribe] data file:   {Place} (F-M791).",
                        eintrag.Key + " ×" + eintrag.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                LogUtil.Debug(_logger, "[Sub Scribe-DB] language values: all inside the canon (F-M790)");
            }

            if (urteile > 0)
            {
                LogUtil.Normal(
                    _logger,
                    "[Sub Scribe] data file: {Count} stored season verdict(s) moved to the one language form (F-M790).",
                    urteile);
            }

            // F-M668: der Block, der die Zugeh-Stempel der Medienzeile leerte (Schema-16-Wanderung), ist
            // mit den Spalten selbst entfallen — die Stempel stehen jetzt als `file`-Zeilen in der
            // Marken-Matrix. Die Sprachumstellung selbst bleibt unangetastet (bekanntes Verhalten).

            int n = _db.SaveChanges();
            if (geaendert > 0)
            {
                LogUtil.Normal(_logger, "[Sub Scribe] data file: {Count} language tag(s) rewritten to ISO 639-2/T (F-M790).", geaendert);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not rewrite the language tags (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// F-M455: adds the status row's own late column — the count of broken subtitles reported to
    /// SubDL. A table that already carries it is left alone, so the pass runs on every start and
    /// costs one PRAGMA.
    /// </summary>
    private void EnsureStatusColumns()
    {
        try
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _db.Database.GetDbConnection().CreateCommand())
            {
                if (cmd.Connection!.State != System.Data.ConnectionState.Open)
                {
                    cmd.Connection.Open();
                }

                cmd.CommandText = "PRAGMA table_info(status_stats);";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(1));
                }
            }

            if (!existing.Contains("BrokenReported"))
            {
                _db.Database.ExecuteSqlRaw("ALTER TABLE status_stats ADD COLUMN BrokenReported INTEGER NOT NULL DEFAULT 0;");
                LogUtil.Normal(_logger, "[Sub Scribe] data file: column BrokenReported added to status_stats.");
            }

            // F-M759: the two season-pack counters reach an existing data file the same way — additively,
            // one PRAGMA per start, no schema bump. A fresh file has them from the model.
            if (!existing.Contains("SeasonPacksUploaded"))
            {
                _db.Database.ExecuteSqlRaw("ALTER TABLE status_stats ADD COLUMN SeasonPacksUploaded INTEGER NOT NULL DEFAULT 0;");
                LogUtil.Normal(_logger, "[Sub Scribe] data file: column SeasonPacksUploaded added to status_stats.");
            }

            if (!existing.Contains("SeasonPacksDownloaded"))
            {
                _db.Database.ExecuteSqlRaw("ALTER TABLE status_stats ADD COLUMN SeasonPacksDownloaded INTEGER NOT NULL DEFAULT 0;");
                LogUtil.Normal(_logger, "[Sub Scribe] data file: column SeasonPacksDownloaded added to status_stats.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] could not add the status column (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Records the running plugin and Jellyfin versions in area 0.
    /// <para>
    /// Called once at startup. Deliberately NOT called per run: writing the plugin's own version on
    /// every cycle would turn the audit record into a write amplifier.
    /// </para>
    /// </summary>
    /// <param name="pluginVersion">Version of this plugin build.</param>
    /// <param name="jellyfinVersion">Version of the hosting Jellyfin server.</param>
    public void RecordVersions(string? pluginVersion, string? jellyfinVersion)
    {
        try
        {
            var meta = Meta.FindById("db") ?? new MetaEntity { Id = "db", SchemaVersion = CurrentSchemaVersion };
            meta.SchemaVersion = CurrentSchemaVersion;
            meta.PluginVersion = pluginVersion;
            meta.JellyfinVersion = jellyfinVersion;
            meta.Updated = DateTime.UtcNow;
            Meta.Upsert(meta);
            LogUtil.Normal(_logger,
                "[Sub Scribe] data file last written by plugin {Plugin} on Jellyfin {Jellyfin}",
                pluginVersion ?? "unknown", jellyfinVersion ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[Sub Scribe] recording versions failed (continuing): {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// Reads the journal mode the data file is actually in, through the live context.
    /// </summary>
    /// <returns>The mode string, e.g. <c>wal</c> or <c>delete</c>.</returns>
    public string ReadJournalMode()
    {
        FlushPending();
        return _db.ReadJournalMode();
    }

    /// <summary>
    /// Drops every idle pooled connection for this database, so a file-level REPLACEMENT of the
    /// database is actually seen by the next open.
    /// <para>
    /// Called by the reset and restore paths immediately before they copy a file over the database.
    /// Pooling is off in this context, but SQLite keeps a process-wide pool registry and the plugin
    /// shares one path across several contexts over its lifetime — so the call is cheap insurance
    /// for the one operation that cannot tolerate a stale handle.
    /// </para>
    /// </summary>
    /// <param name="path">Path of the database file.</param>
    public static void ClearPoolFor(string path)
    {
        using var cn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        SqliteConnection.ClearPool(cn);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Folds the write-ahead log into the main file, so the file on disk is a complete,
    /// self-contained snapshot.
    /// <para>
    /// Required before any file-level copy of the database. In WAL mode every committed write sits
    /// in <c>&lt;name&gt;-wal</c> until a checkpoint; a copy taken without this one carries the main
    /// file alone and silently loses everything written since the last checkpoint — and a RESTORE
    /// from such a copy leaves a stale <c>-wal</c> beside the restored file, which SQLite then tries
    /// to apply as if it belonged to it. The previous engine was a single file and needed none of
    /// this, which is exactly why the copy sites are worth checking when the format changes.
    /// </para>
    /// </summary>
    public void Checkpoint()
    {
        FlushPending();
        _db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>
    /// Delete all expired counters and very old runs.
    /// </summary>
    /// <param name="now">Reference time, defaults to now.</param>
    public void Prune(DateTime? now = null)
    {
        var cutoff = now ?? DateTime.UtcNow;
        var expiredCounters = Counters.DeleteMany(x => x.Expires != null && x.Expires < cutoff);
        var oldRuns = Runs.DeleteMany(x => x.Ended != null && x.Ended < cutoff.AddDays(-30));
        LogUtil.Detail(_logger, "[Sub Scribe] store pruned: {ExpiredCounters} expired counters, {OldRuns} old runs", expiredCounters, oldRuns);
    }

    /// <summary>
    /// Physically compacts the data file. SQLite's <c>VACUUM</c> does in one statement what the
    /// previous engine needed a rebuild for: it rewrites the database into a dense file, which
    /// releases the pages that deletes freed.
    /// <para>
    /// Why this exists: deleting rows in SQLite does not shrink the file. The freed pages go on the
    /// freelist and the file stays as large as before the prune, so a state prune would leave the
    /// store physically untouched.
    /// </para>
    /// <para>
    /// <c>VACUUM</c> cannot run inside a transaction, and this runs on a database other writers hold
    /// open — so the outcome is reported rather than assumed. The returned flag is true only when the
    /// database answers afterwards, so a failed compaction costs the size reduction and nothing else.
    /// </para>
    /// </summary>
    /// <returns>Compaction result: sizes before/after, collections rebuilt, and what became of the
    /// rebuild.</returns>
    /// F-M214: a database refresh compacts in the same run; measured over the database file plus its
    /// write-ahead log, because reporting the main file alone understates the starting size.
    public (long Before, long After, long Rebuilt, CompactOutcome Outcome) Compact()
    {
        // The measured footprint is what the FILE holds, so a batch still in the change tracker
        // would be missing from it and the "before" number would understate the store.
        FlushPending();
        var (mainBefore, walBefore) = MeasureFootprint();
        long beforeTotal = mainBefore + walBefore;

        try
        {
            // Fold the WAL into the main file first. Without this, the bytes a prune just freed are
            // still counted against the data directory in the side file, and VACUUM would report a
            // "before" that no reader ever sees.
            _db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");

            _db.Database.ExecuteSqlRaw("VACUUM;");

            // F-M342 (operator order 10.10.2026): refresh the query statistics in the SAME pass.
            // VACUUM shrinks the file; ANALYZE is what fills sqlite_stat1 — the row count and the
            // rows-per-key average of every index — and sqlite_stat1 is what the query planner reads
            // to pick an index. Without it the planner only guesses, and the guess is measurably
            // wrong: on "MediaHash = ? AND Status = ?" against this schema it chose IX_embeds_Status
            // before the statistics existed and IX_embeds_MediaHash after, and MediaHash is the
            // selective one (13 rows per key against 29).
            //
            // Its OWN try, deliberately: a statistics failure must NOT be reported as a failed
            // compaction. The file WAS shrunk at this point, and the outcomes below ("recovered",
            // "the data file was NOT shrunk") would be a lie about a file that is smaller than it was.
            // The statistics are the cheaper half of the pair and lose nothing but their own freshness.
            bool statisticsRefreshed = false;
            try
            {
                _db.Database.ExecuteSqlRaw("ANALYZE;");
                statisticsRefreshed = true;
            }
            catch (Exception statEx)
            {
                _logger?.LogWarning(
                    "[Sub Scribe-DB] ANALYZE skipped ({Msg}) — the file was compacted, the query statistics stay stale.",
                    statEx.Message);
            }

            // Fold the rewrite in before measuring. VACUUM and ANALYZE BOTH write into the journal
            // in WAL mode, so without this second checkpoint the "after" number describes a journal
            // holding the whole rebuilt database rather than the settled file — measured in the
            // store harness: main 159 744 B plus wal 164 856 B gave the 324 600 B the line reported,
            // against a 155 648 B "before", which reads as a file that doubled while it shrank.
            // F-M214 wants the footprint of the settled store, and the file is meant to be
            // self-contained afterwards (a plain copy is a backup) — one fold serves both.
            _db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");

            // A VACUUM rewrites every table; SQLite reports nothing, so the count that the previous
            // engine got from its rebuild is taken from the collections that actually hold rows.
            long rebuilt = _db.GetAreaRowCounts().Count(x => x.Rows > 0);

            var (mainAfter, walAfter) = MeasureFootprint();
            long afterTotal = mainAfter + walAfter;

            LogUtil.Detail(_logger,
                "[Sub Scribe-DB] compacted: {BeforeTotal} -> {AfterTotal} bytes total "
                + "(main {MainBefore} -> {MainAfter}, wal {WalBefore} -> {WalAfter}); "
                + "{Rebuilt} area(s) rewritten, {Released} bytes released; statistics {Stats}.",
                beforeTotal, afterTotal, mainBefore, mainAfter, walBefore, walAfter,
                rebuilt, beforeTotal - afterTotal,
                statisticsRefreshed ? "refreshed" : "stale (ANALYZE did not run)");
            return (beforeTotal, afterTotal, rebuilt, CompactOutcome.Compacted);
        }
        catch (Exception ex)
        {
            // Compaction is housekeeping. A failure must never abort the task that called it — but it
            // must not leave a dead connection behind either. The outcome stays distinguishable:
            // "recovered" means the store works but the FILE WAS NOT SHRUNK, which is not the same
            // message as "compacted".
            _logger?.LogWarning("[Sub Scribe-DB] compaction failed: {Msg}", ex.Message);
            var recovered = VerifyAndRepair(ex);
            return (beforeTotal, beforeTotal, 0,
                recovered ? CompactOutcome.Recovered : CompactOutcome.Broken);
        }
    }

    /// <summary>
    /// Proves the database still answers after a failed compaction.
    /// <para>
    /// The probe is a real query — reading one row — because a flag would only restate what we
    /// already know about the exception. A SQLite connection that survives a failed VACUUM needs no
    /// repair; the check exists so the caller can tell "usable but not shrunk" from "broken".
    /// </para>
    /// </summary>
    /// <param name="cause">The exception the compaction threw, for the log line.</param>
    /// <returns>True when the database answers again.</returns>
    private bool VerifyAndRepair(Exception cause)
    {
        try
        {
            _ = Meta.FindById("db");
            _logger?.LogWarning("[Sub Scribe-DB] data file still answers after the failed compaction ({Cause}).", cause.Message);
            return true;
        }
        catch (Exception probeEx)
        {
            _logger?.LogError(probeEx, "[Sub Scribe-DB] the data file stopped answering after the failed compaction — restart Jellyfin to recover the database.");
            return false;
        }
    }

    /// <summary>
    /// Returns the byte sizes of the database file and its write-ahead log. SQLite in WAL mode keeps
    /// every delete and update in the side <c>-wal</c> file until a checkpoint folds it in.
    /// </summary>
    /// <returns>Main file size and WAL size in bytes; 0 for a file that does not exist.</returns>
    private (long Main, long Wal) MeasureFootprint()
    {
        FlushPending();
        long main = File.Exists(_dbPath) ? new FileInfo(_dbPath).Length : 0;
        var walPath = DbFiles.SidePath(_dbPath, "-wal");
        long wal = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        return (main, wal);
    }

    /// <summary>
    /// Opens a batch on the underlying context — see <see cref="ScribeSqliteContext.BeginBatch"/>.
    /// <para>
    /// The wrapper exposes it because this is the type the callers hold; the batch state itself
    /// lives beside <c>SaveChanges</c>, which only the context can call.
    /// </para>
    /// </summary>
    public void BeginBatch() => _db.BeginBatch();

    /// <summary>Closes a batch and commits what it accumulated.</summary>
    public void EndBatch() => _db.EndBatch();

    /// <summary>Commits accumulated writes now.</summary>
    public void FlushPending() => _db.FlushPending();

    /// <summary>
    /// Replace aliases for a media record by hash. Creates the record if absent.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="patch">Optional mutation.</param>
    /// <returns>The stored record.</returns>
    public FileEntity EnsureFile(string osHash, Action<FileEntity>? patch = null)
    {
        return EnsureFile(osHash, null, null, patch);
    }

    /// <summary>
    /// Replace aliases for a media record by hash, including Jellyfin item id and path.
    /// Creates the record if absent.
    /// </summary>
    /// <param name="mediaHash">Media hash.</param>
    /// <param name="jellyfinItemId">Optional Jellyfin item id.</param>
    /// <param name="path">Optional file path.</param>
    /// <param name="patch">Optional mutation.</param>
    /// <returns>The stored record.</returns>
    public FileEntity EnsureFile(string? osHash, string? jellyfinItemId, string? path, Action<FileEntity>? patch = null)
    {
        // F-M514 (Vorfall 16.11.2026, jf-test 02:51): OHNE SCHLUESSEL GIBT ES KEINE ZEILE.
        // Ein leerer Medien-Hash heisst "diese Datei hat noch keine Identitaet" — nicht "eine Zeile
        // ohne Id". Genau das wurde hier aber gebaut: `new FileEntity { Id = null }`, und Entity
        // Framework verweigert das Anhaengen ("Unable to track an entity of type 'FileEntity'
        // because its primary key property 'Id' is null"). Seit EF Core 10 bleibt die abgewiesene
        // Instanz danach als Detached im Change Tracker stehen, und JEDES spaetere SaveChanges
        // dieses Kontexts wirft "Unexpected entry.EntityState: Detached" — der Store war damit fuer
        // den Rest des Prozesses schreibunfaehig, und der Upload-Zyklus endete ohne Lauf. Gemessen
        // im Log: 02:51:48.410 die WRITE-FAILED-Zeile, 02:51:48.428 die erste Detached-Warnung.
        // Der Aufruf kommt aus `MarkSidecar` (Download-Pfad), wenn `GetMediaHash` fuer die eben
        // geschriebene Datei noch keinen OSHash kennt. Also: nichts anlegen, nichts anhaengen, und
        // die Zusage "kein Medium bekannt" durchreichen — der Schreiber bekommt eine leere, nicht
        // gespeicherte Instanz (seine Kopien der Kennungen bleiben dann leer, wie ohne Zeile).
        if (string.IsNullOrEmpty(osHash))
        {
            LogUtil.Detail(_logger, "[Sub Scribe-DB] media row NOT written: no media hash known (F-M514)");
            return new FileEntity();
        }

        var entity = Files.FindById(osHash);
        if (entity == null)
        {
            entity = new FileEntity { OsHash = osHash, Created = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
        }
        else
        {
            entity.LastSeen = DateTime.UtcNow;
        }

        if (!string.IsNullOrEmpty(jellyfinItemId))
        {
            entity.JellyfinItemId = jellyfinItemId;
        }

        if (!string.IsNullOrEmpty(path))
        {
            entity.Path = path;
        }

        patch?.Invoke(entity);
        Files.Upsert(entity);
        // F-M684: Massenzeile — eine je Datei und Lauf, deshalb auf echtem Debug-Pegel.
        LogUtil.Debug(_logger, "[Sub Scribe-DB] file upsert {Hash} -> {FileId}", osHash, entity.FileId);
        return entity;
    }

    /// <summary>
    /// Builds the primary key of an embedded track. Kept here beside the collection so writers and
    /// readers cannot drift apart on the key shape.
    /// </summary>
    /// <param name="mediaHash">Parent media hash.</param>
    /// <param name="subPos">ffmpeg stream position.</param>
    /// <returns>The composite key.</returns>
    public static string EmbedKey(string mediaHash, int subPos) => mediaHash + "|" + subPos;

    /// <summary>
    /// F-M390/F-M642: builds the primary key of a season pack — one pack per season, language, variant
    /// and source.
    /// <para>
    /// The variant is part of the key because a plain pack and an SDH pack of the same season are two
    /// different containers, and every source carries the HI flag as a single field. Without it the
    /// second pack would overwrite the first one's row and the plugin would believe it had published
    /// something it never sent.
    /// </para>
    /// <para>
    /// F-M642: die QUELLE ist seit dem 20.11.2026 der fuenfte Teil. Sie fehlte, solange nur SubDL
    /// Container baute; sobald eine zweite Quelle Packs kennt, waere ein gemeinsamer Schluessel eine
    /// geteilte Zeile — und ein Pack der einen Quelle wuerde den der anderen ueberschreiben. Die
    /// Vorgabe <c>subdl</c> haelt jeden Aufrufer, der die Quelle nicht nennt, auf dem alten Verhalten.
    /// </para>
    /// </summary>
    /// <param name="imdbId">Series IMDb id.</param>
    /// <param name="season">Season number.</param>
    /// <param name="language">Language code of the pack at its source.</param>
    /// <param name="hearingImpaired">HI/SDH variant flag.</param>
    /// <param name="source">Source that carries the pack — must be named explicitly (F-M656); a default would
    /// build the SubDL key for every forgotten call and silently hit the wrong row.</param>
    /// <returns>The composite key.</returns>
    public static string PackKey(
        string imdbId, int season, string language, bool hearingImpaired,
        string source)
        => imdbId + "|S" + season.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + "|" + language + "|" + (hearingImpaired ? "hi" : "plain")
           + "|" + (string.IsNullOrWhiteSpace(source) ? PackEntity.SubdlSource : source);

    /// <inheritdoc />
    public void Dispose()
    {
        // A batch left open by an exception still owns writes the unbatched store would have
        // committed, so the close drains them before the file handle goes away.
        try
        {
            FlushPending();
        }
        catch (Exception ex)
        {
            LogUtil.Detail(_logger, "[Sub Scribe-DB] pending writes could not be flushed on dispose: {Msg}", ex.Message);
        }

        _db.Dispose();
    }
}

/// <summary>
/// The Entity Framework Core model over the plugin's own SQLite file.
/// <para>
/// One <see cref="DbSet{TEntity}"/> per area, with the business key as the primary key — never an
/// auto-assigned id (F-M194). The indexes are declared here so they exist in the schema rather than
/// being created at runtime on every open.
/// </para>
/// </summary>
public sealed class ScribeSqliteContext : DbContext
{
    private readonly string _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScribeSqliteContext"/> class.
    /// </summary>
    /// <param name="path">Path of the SQLite file, this plugin's own.</param>
    public ScribeSqliteContext(string path)
    {
        _path = path;
    }

    /// <summary>Path of the database file this context owns.</summary>
    public string Path => _path;

    /// <summary>
    /// The one gate every read and write goes through.
    /// <para>
    /// NOT optional, and not a performance choice. A <see cref="DbContext"/> is single-threaded by
    /// contract: a second operation started while another is in flight throws
    /// <c>InvalidOperationException</c> ("A second operation was started on this context instance
    /// before a previous operation completed"). The plugin shares ONE context across all registries,
    /// the scheduled tasks and the API endpoints, and the configuration page polls
    /// <c>/Plugins/SubdlSync/Stats</c> every 10 seconds while a worker writes — so without this gate
    /// the failure is not hypothetical, it is a matter of timing.
    /// </para>
    /// <para>
    /// The previous engine held thread-safe collection handles and needed no such gate. This is the
    /// one behavioural difference the format change introduced that the callers cannot see, which is
    /// why it lives here rather than at every call site.
    /// </para>
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>
    /// F-M514: the plugin's logger, handed down by <see cref="ScribeDbContext"/> so a store-side guard
    /// can warn without a second wiring path. Null in a host-free probe, so every use is optional.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// F-M514: a rejected attach must not poison this context for the rest of the process.
    /// <para>
    /// Entity Framework Core 10 leaves the instance behind as a <c>Detached</c> entry when it refuses
    /// to track it (measured 16.11.2026 on 10.0.11: "Unable to track an entity … primary key property
    /// 'Id' is null"). From then on EVERY <c>SaveChanges</c> of the same context throws
    /// <c>ArgumentOutOfRangeException: Unexpected entry.EntityState: Detached</c> from the command
    /// batch preparer, and nothing can be written again — that is how one unkeyed media row ended a
    /// whole upload cycle ("cycle finished without an upload run", jf-test 02:51–03:07).
    /// </para>
    /// <para>
    /// The repair drops the ghosts and re-attaches everything else from the same instances, so the
    /// identity map and every pending change survive. It is safe to retry because the exception is
    /// thrown while the commands are PREPARED, before any of them is executed: a failed preparation
    /// writes nothing at all. A second failure is NOT swallowed — the retry is the last word, and a
    /// store that still refuses stays visible.
    /// </para>
    /// </summary>
    /// <inheritdoc />
    public override int SaveChanges()
    {
        try
        {
            return base.SaveChanges();
        }
        catch (ArgumentOutOfRangeException ex) when (IsDetachedGhost(ex) && RestoreTracker())
        {
            Logger?.LogWarning(
                "[Sub Scribe-DB] a rejected row left a Detached entry in the change tracker; the tracker was rebuilt and the write retried ({Msg})",
                ex.Message);
            return base.SaveChanges();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return RetryAfterVanishedRows(ex);
        }
    }

    /// <summary>
    /// F-M716/F-M717 (Befund 23.11.2026, 19:46 und 20:11): eine Zeile, die beim Schreiben schon weg ist,
        /// ist für ein DELETE kein Fehler — das Ziel ist erreicht; für ein UPDATE gibt es nichts mehr zu
        /// schreiben. Ursache ist die Löschweitergabe im Schema (<c>pack_track</c> hängt mit
        /// <c>ON DELETE CASCADE</c> an <c>pack</c> und <c>track</c>): die Kaskade räumt Zeilen weg, die der
        /// Kontext noch als „unverändert" führt, und jedes solche DELETE trifft 0 Zeilen.
        /// <para>
        /// WARUM IN EINER SCHLEIFE: die Ausnahme nennt nur die Einträge EINES Befehls aus dem Stapel. Ein
        /// einzelner Wiederholungsversuch traf beim Messen (20:11) die nächste Kaskadenzeile und riss den
        /// Refresh erneut ab. Jede Runde hakt die gemeldeten Einträge ab, bis keine „schon weg"-Zeile mehr im
        /// Stapel steht.
        /// </para>
        /// </summary>
        /// <param name="zuerst">Die Ausnahme des ersten Versuchs.</param>
        /// <returns>Die Anzahl der geschriebenen Zeilen.</returns>
        private int RetryAfterVanishedRows(DbUpdateConcurrencyException zuerst)
        {
        DbUpdateConcurrencyException offen = zuerst;
        for (int runde = 1; runde <= 200; runde++)
        {
            var verschwunden = offen.Entries.ToList();
            foreach (var eintrag in verschwunden)
            {
                eintrag.State = EntityState.Detached;
            }

            Logger?.LogWarning(
                "[Sub Scribe-DB] {Count} row(s) were already gone when the write ran — dropped as done, retry {Round} (F-M716/F-M717).",
                verschwunden.Count,
                runde);

            try
            {
                return base.SaveChanges();
            }
            catch (DbUpdateConcurrencyException weiter)
            {
                offen = weiter;
            }
        }

        throw offen;
    }

    /// <summary>F-M514: the one exception shape the tracker repair answers to.</summary>
    /// <param name="ex">The thrown exception.</param>
    /// <returns>True when this is the Detached-ghost failure of the command batch preparer.</returns>
    private static bool IsDetachedGhost(Exception ex)
        => ex.Message.Contains("Unexpected entry.EntityState", StringComparison.Ordinal);

    /// <summary>
    /// F-M514: rebuilds the change tracker without the entries Entity Framework left as
    /// <c>Detached</c>, keeping every other row tracked with the state it had.
    /// <para>
    /// Setting <c>Entry.State = Detached</c> on the ghost does NOT remove it — measured against
    /// 10.0.11: the entry stays in the tracker and the next save throws the same exception. Only
    /// <see cref="ChangeTracker.Clear"/> drops it, which is why every tracked row is captured first
    /// and re-attached afterwards (as <c>Added</c>/<c>Modified</c>/<c>Deleted</c>/<c>Unchanged</c>).
    /// The row cache of the areas points at these same instances, so re-attaching them is also what
    /// keeps "read a row, mutate it, save it" working — an untracked cached row would make a later
    /// save silently write nothing.
    /// </para>
    /// </summary>
    /// <returns>True when the tracker was rebuilt, false when even that failed.</returns>
    private bool RestoreTracker()
    {
        try
        {
            // Detect first: a row mutated by its caller still reads "Unchanged" until change
            // detection runs, and re-attaching it as Unchanged would discard that change.
            ChangeTracker.DetectChanges();

            var snapshot = ChangeTracker.Entries()
                .Where(e => e.State != EntityState.Detached)
                .Select(e => (e.Entity, e.State))
                .ToList();

            ChangeTracker.Clear();

            foreach (var (entity, state) in snapshot)
            {
                try
                {
                    Entry(entity).State = state;
                }
                catch (Exception)
                {
                    // One row that cannot be attached again must not abort the repair; it is lost,
                    // and the warning above names the cause.
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger?.LogWarning("[Sub Scribe-DB] the change tracker could not be rebuilt ({Msg})", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// How many writes may accumulate before a batch commits on its own.
    /// <para>
    /// The floor exists so the change tracker never walks an unbounded set: Entity Framework runs
    /// change detection over every tracked row on each <c>SaveChanges</c>, and the row cache holds
    /// whole areas. On the largest install measured (11 153 embedded rows, 1 201 media rows) 500
    /// keeps that walk cheap while collapsing a 14 000-write scan into roughly 28 commits.
    /// </para>
    /// </summary>
    public const int BatchFloor = 500;

    private int _batchDepth;
    private int _pendingWrites;

    /// <summary>
    /// Opens a batch: writes made until <see cref="EndBatch"/> accumulate and commit together
    /// instead of one transaction per row.
    /// <para>
    /// WHY this exists: a single row used to cost one commit, and a commit is a platter sync.
    /// Measured live (08.10.2026): a library scan issued 9 644 writes at a flat 45 ms each — 496 s
    /// of wall time for a pass that changed nothing. The upload and download pipelines write through
    /// the same path, so the cost was never the seeder's alone.
    /// </para>
    /// <para>
    /// READ-AFTER-WRITE IS PRESERVED WITHOUT THE COMMIT: readers go through the per-area row cache,
    /// which the areas maintain in place, so a value written a moment ago is visible to this context
    /// before it is durable. What changes is when the FILE receives it — an independent context (the
    /// config page holds one) sees it at the next flush, up to <see cref="BatchFloor"/> rows later.
    /// </para>
    /// <para>
    /// Callers pair this with <see cref="EndBatch"/> in a <c>finally</c>: an exception that skips the
    /// close would drop the accumulated writes, where the unbatched store had already persisted each
    /// one.
    /// </para>
    /// </summary>
    public void BeginBatch() => _batchDepth++;

    /// <summary>
    /// Closes a batch and commits what it accumulated. Nested batches commit at the outermost close,
    /// so a caller opening one inside another cannot flush a half-finished unit of work.
    /// </summary>
    public void EndBatch()
    {
        if (_batchDepth > 0)
        {
            _batchDepth--;
        }

        if (_batchDepth == 0)
        {
            FlushPending();
        }
    }

    /// <summary>
    /// Persists one write, or accumulates it while a batch is open. The areas call this instead of
    /// <c>SaveChanges</c> directly, so the batching decision lives in one place.
    /// </summary>
    public void RequestSave()
    {
        _pendingWrites++;
        if (_batchDepth == 0 || _pendingWrites >= BatchFloor)
        {
            FlushPending();
        }
    }

    /// <summary>
    /// Commits accumulated writes now. Called at the end of a batch and at every point whose answer
    /// comes from the FILE rather than the row cache: area row counts, footprint, checkpoint,
    /// compaction and disposal.
    /// </summary>
    public void FlushPending()
    {
        if (_pendingWrites == 0)
        {
            return;
        }

        _pendingWrites = 0;
        SaveChanges();
    }

    /// <summary>Area 0: the single compatibility row.</summary>
    public DbSet<MetaEntity> MetaRows => Set<MetaEntity>();

    /// <summary>Area 1: OSHash cache.</summary>
    public DbSet<OshashEntity> OshashRows => Set<OshashEntity>();

    /// <summary>Area 2: video files (`file`).</summary>
    public DbSet<FileEntity> FileRows => Set<FileEntity>();

    /// <summary>Area 2: embedded subtitle tracks (`track`).</summary>
    public DbSet<TrackEntity> TrackRows => Set<TrackEntity>();

    /// <summary>Area 3: sidecar subtitle files (`sidecar`).</summary>
    public DbSet<SidecarEntity> SidecarRows => Set<SidecarEntity>();

    /// <summary>Area 4: burned download candidates.</summary>
    /// <summary>Retry and fail-budget counters.</summary>
    public DbSet<CounterEntity> CounterRows => Set<CounterEntity>();

    /// <summary>Pipeline run bookkeeping.</summary>
    public DbSet<PipelineRunEntity> RunRows => Set<PipelineRunEntity>();

    /// <summary>F-M542: TMDb answers, so a restart does not re-ask the same questions.</summary>

    /// <summary>Area 5: cumulative GUI counters.</summary>
    public DbSet<StatusStatsEntity> StatusRows => Set<StatusStatsEntity>();

    /// <summary>Area 6: the last run of each worker.</summary>
    public DbSet<WorkerRunEntity> WorkerRunRows => Set<WorkerRunEntity>();

    /// <summary>The published season packs — one row per season, language and variant (F-M390).</summary>
    public DbSet<PackEntity> PackRows => Set<PackEntity>();

    /// <summary>F-M601: the aired episode numbers per season, so a restart does not re-ask IMDb/TMDb.</summary>
    public DbSet<AiredSeasonEntity> AiredSeasonRows => Set<AiredSeasonEntity>();

    /// <summary>F-M604: the stored pack verdicts with the signature of the members they were made for.</summary>
    public DbSet<PackSeasonEntity> PackSeasonRows => Set<PackSeasonEntity>();

    /// <summary>F-M605: the remembered refusals of the sources, per track and content hash.</summary>
    public DbSet<SourceRefusalEntity> SourceRefusalRows => Set<SourceRefusalEntity>();

    /// <summary>F-M608: the titles whose creation keeps failing, and the ones closed for good.</summary>
    public DbSet<TitleFailureEntity> TitleFailureRows => Set<TitleFailureEntity>();

    /// <summary>
    /// Sets the pragmas this store runs with. Called once per context, right after the schema exists.
    /// <para>
    /// <c>journal_mode=WAL</c> is set EXPLICITLY, because it is not the default: SQLite and Entity
    /// Framework both start in the rollback-journal mode (<c>delete</c>) — verified by reading
    /// <c>PRAGMA journal_mode</c> back on a fresh EF-created file, which answered <c>delete</c>. WAL
    /// is what this store wants: the plugin has one writer (a scheduled task, serialised by the
    /// global run lock) and several readers (the configuration page polls every 10 seconds, the
    /// diagnostics endpoint walks four areas), and in WAL a reader never blocks the writer or sees a
    /// half-written state.
    /// </para>
    /// <para>
    /// The mode is stored IN the file, so this is a one-time change per database. It is also the
    /// reason the reset and restore paths must checkpoint before they copy the file: in WAL the
    /// committed writes sit in the side journal until a checkpoint, and a copy that ignores it is a
    /// backup missing everything since the last one.
    /// </para>
    /// <para>
    /// <c>busy_timeout</c> covers the one case the in-process gate cannot: two CONTEXTS, or a
    /// second process, over the same file. SQLite then answers "database is locked" immediately;
    /// the timeout makes it wait instead of failing a scheduled run.
    /// </para>
    /// </summary>
    public void ConfigurePragmas()
    {
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
    }

    /// <summary>
    /// Reads the journal mode the FILE is actually in.
    /// </summary>
    /// <returns>The mode string, e.g. <c>wal</c> or <c>delete</c>.</returns>
    public string ReadJournalMode()
    {
        using var cn = (SqliteConnection)Database.GetDbConnection();
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    /// <summary>
    /// Row counts per area, for the diagnostics endpoint and the compaction line.
    /// </summary>
    /// <returns>One entry per area with its row count.</returns>
    public List<(string Area, long Rows)> GetAreaRowCounts()
        => new()
        {
            ("meta", MetaRows.LongCount()),
            ("oshashes", OshashRows.LongCount()),
            ("file", FileRows.LongCount()),
            ("track", TrackRows.LongCount()),
            ("sidecar", SidecarRows.LongCount()),
            // F-M672: die drei Quellen-Tabellen stehen an der Stelle der Matrix und der verworfenen
            // Kandidaten — die Diagnose zählt sie einzeln, weil ihre Zeilen verschiedene Dinge sind.
            ("upload", UploadRows.LongCount()),
            ("reference", ReferenceRows.LongCount()),
            ("badapple", BadAppleRows.LongCount()),
            ("counters", CounterRows.LongCount()),
            ("runs", RunRows.LongCount()),
            ("status_stats", StatusRows.LongCount()),
            ("worker_runs", WorkerRunRows.LongCount()),
        };

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // WAL so a reader never blocks a writer; the plugin has one writer and several readers
        // (config page, diagnostics, scheduled tasks) on the same file.
        // Pooling OFF, deliberately. The plugin's reset and restore routes are file-level
        // operations: they dispose the context, copy a file over the database and open a fresh
        // context. With pooling ON the physical connection is not closed on Dispose — it returns
        // to the pool holding the file handle and its WAL index — so the copy does not take
        // effect and the reopened context reads the OLD file. Measured, in a probe that mirrors
        // both paths: pooling ON → 2 of 3 rows restored and a mutated value still reads 999;
        // pooling OFF → 3 of 3 and the backed-up value. The cost is one connection open per
        // context, and the plugin creates one context per process.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        optionsBuilder.UseSqlite(cs, sqlite =>
        {
            // The plugin schema is created by EnsureCreated and owned by the plugin alone, so no
            // migration assembly is involved.
            sqlite.CommandTimeout(30);
        });
        optionsBuilder.EnableSensitiveDataLogging(false);
    }

    /// <summary>F-M672: das Modell der drei Quellen-Tabellen.</summary>
    public DbSet<UploadEntity> UploadRows => Set<UploadEntity>();

    /// <summary>F-M672: das Modell der Belege.</summary>
    public DbSet<ReferenceEntity> ReferenceRows => Set<ReferenceEntity>();

    /// <summary>F-M672: das Modell der schlechten Downloads.</summary>
    public DbSet<BadAppleEntity> BadAppleRows => Set<BadAppleEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetaEntity>(e =>
        {
            e.ToTable("meta");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<OshashEntity>(e =>
        {
            e.ToTable("oshashes");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<FileEntity>(e =>
        {
            e.ToTable("file");
            e.HasKey(x => x.FileId);
            // F-M671: der Hash ist die ADRESSE, unter der die Datei wiedergefunden wird — eindeutig, aber
            // nicht die Identität. Ein zweiter Datensatz mit demselben Hash wäre dieselbe Datei.
            e.HasIndex(x => x.OsHash).IsUnique();
            e.HasIndex(x => x.ImdbId);
            e.HasIndex(x => x.TmdbId);
            e.HasIndex(x => x.Path);
            e.HasIndex(x => x.JellyfinItemId);
        });

        modelBuilder.Entity<TrackEntity>(e =>
        {
            e.ToTable("track");
            // F-M676: der Schlüssel ist die ID SELBST (`#yyyyy#zzz`); die Datei steht darin. Das abgeleitete
            // `FileId` gehört nicht in die Tabelle — eine zweite Spalte wäre dieselbe Aussage doppelt.
            e.HasKey(x => x.TrackId);
            e.Ignore(x => x.FileId);
            e.HasIndex(x => x.MediaHash);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<SidecarEntity>(e =>
        {
            e.ToTable("sidecar");
            e.HasKey(x => x.TrackId);
            e.Ignore(x => x.FileId);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => x.MediaHash);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Language);
        });

        // F-M677: die Mitgliedschaft eines Behälters — das Array, zeilenweise.
        modelBuilder.Entity<PackTrackEntity>(e =>
        {
            e.ToTable("pack_track");
            e.HasKey(x => new { x.PackId, x.TrackId });
            e.HasIndex(x => x.PackId);
            e.HasIndex(x => x.TrackId);
            e.HasOne<PackEntity>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<TrackEntity>().WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CounterEntity>(e =>
        {
            e.ToTable("counters");
            // Key, not the numeric Id: the previous engine's auto id is what made an upsert unable
            // to find the row it meant to update (F-M194). The business key is the identity.
            e.HasKey(x => x.Key);
            e.HasIndex(x => x.Expires);
        });

        modelBuilder.Entity<PipelineRunEntity>(e =>
        {
            e.ToTable("runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Started);
        });

        modelBuilder.Entity<StatusStatsEntity>(e =>
        {
            e.ToTable("status_stats");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<WorkerRunEntity>(e =>
        {
            e.ToTable("worker_runs");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<PackEntity>(e =>
        {
            e.ToTable("pack");
            // F-M671: der Pack trägt seine eigene Kennung; der Staffelschlüssel bleibt die Adresse, unter
            // der die Aufrufer ihn fragen — eindeutig, damit „ein Behälter je Staffel/Sprache/Variante/
            // Quelle" weiter gilt.
            e.HasKey(x => x.PackId);
            e.HasIndex(x => x.SeasonKey).IsUnique();
            e.HasIndex(x => x.ImdbId);
            e.HasIndex(x => x.Season);
        });

        // F-M601: explicit name, so the table created by EnsureAiredSeasonsTable is the one EF reads.
        modelBuilder.Entity<AiredSeasonEntity>(e =>
        {
            e.ToTable("aired_seasons");
            e.HasKey(x => x.Id);
        });

        // F-M608: and for the title ladder.
        modelBuilder.Entity<TitleFailureEntity>(e =>
        {
            e.ToTable("title_failures");
            e.HasKey(x => x.Id);
        });

        // F-M605: and for the remembered source refusals.
        modelBuilder.Entity<SourceRefusalEntity>(e =>
        {
            e.ToTable("source_refusals");
            e.HasKey(x => x.Id);
        });

        // F-M672: die drei Quellen-Tabellen. Die Tabellennamen sind dieselben, die
        // SourceStore.EnsureTables (und der Migrationslauf davor) anlegen — sonst schriebe der eine Teil
        // in eine Tabelle, die der andere nie liest.
        modelBuilder.Entity<UploadEntity>(e =>
        {
            e.ToTable("upload");
            e.HasKey(x => new { x.SubjectId, x.Source });
            e.HasIndex(x => x.Source);
        });

        modelBuilder.Entity<ReferenceEntity>(e =>
        {
            e.ToTable("reference");
            e.HasKey(x => new { x.SubjectId, x.Source });
            e.HasIndex(x => x.Source);
            e.HasIndex(x => x.DownloadSourceId);
            e.HasIndex(x => x.UploadedId);
        });

        modelBuilder.Entity<BadAppleEntity>(e =>
        {
            e.ToTable("badapple");
            // F-M679: der Member gehoert zum Schluessel — zwei kaputte Subs eines Packs sind zwei Zeilen.
            e.HasKey(x => new { x.SubjectId, x.Source, x.SourceId, x.Member });
            e.HasIndex(x => x.Source);
            e.HasIndex(x => new { x.SubjectId, x.Language });
        });

        // F-M604: same contract for the pack verdicts.
        modelBuilder.Entity<PackSeasonEntity>(e =>
        {
            e.ToTable("pack_seasons");
            e.HasKey(x => x.Id);
        });
    }
}

/// <summary>
/// One area of the store, reached by the same verbs the previous engine offered —
/// <c>FindById</c>, <c>FindAll</c>, <c>Find</c>, <c>Upsert</c>, <c>DeleteMany</c>, <c>Exists</c>,
/// <c>Count</c>.
/// <para>
/// Why the predicates are evaluated in memory rather than handed to Entity Framework as SQL: the
/// registries depend on that behaviour and say so. <c>QaFailTracker</c> compares languages with
/// <c>String.Equals(..., StringComparison.OrdinalIgnoreCase)</c>, which no provider can translate,
/// and the stored values are the plugin's own strings — a provider that translates the same
/// comparison into SQL <c>LIKE</c> changes which rows match. A silent difference in matching is
/// exactly the class of bug the registry code was written to avoid, so the comparison stays where it
/// was: in LINQ-to-objects.
/// </para>
/// <para>
/// That is affordable because these areas are small — measured on a live install: 117 rows across
/// all ten areas, the largest being 86 embedded tracks. Rows are read once per area, kept for the
/// lifetime of the context, and invalidated on write, so a read costs a memory walk and a write one
/// round trip.
/// </para>
/// </summary>
/// <typeparam name="TEntity">The area's row type.</typeparam>
public sealed class ScribeSet<TEntity>
    where TEntity : class
{
    private readonly ScribeSqliteContext _db;
    private readonly Func<TEntity, string> _keyText;
    private bool _dirty = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScribeSet{TEntity}"/> class.
    /// </summary>
    /// <param name="db">The owning context.</param>
    /// <param name="keyText">Reads the business key as text, for in-memory lookup.</param>
    public ScribeSet(ScribeSqliteContext db, Func<TEntity, string> keyText, Action<TEntity>? onNew = null)
    {
        _db = db;
        _keyText = keyText;
        _onNew = onNew;
    }

    /// <summary>
    /// F-M671: was eine NEUE Zeile bekommt, bevor sie angehängt wird — die Kennung und, bei einer Spur,
    /// Datei, Pack und Spurnummer. Der Haken läuft nur beim Anlegen; eine vorhandene Zeile behält ihre
    /// Kennung, denn eine Kennung ist unveränderlich.
    /// </summary>
    private readonly Action<TEntity>? _onNew;

    /// <summary>Every row of the area.</summary>
    /// <returns>The rows.</returns>
    public IEnumerable<TEntity> FindAll()
    {
        lock (_db.Gate)
        {
            return Rows().ToList();
        }
    }

    /// <summary>
    /// F-M770: die Zeilen der Area fuer einen Leser, der nichts schreiben will — ohne Gedaechtnis.
    /// <para>
    /// WARUM ES DIESE METHODE GIBT: <see cref="FindAll"/> ist der Schreibweg. Es holt die ganze Tabelle in
    /// die Identity Map, damit eine danach veraenderte Zeile genau die Instanz ist, die gespeichert wird,
    /// und es haelt sie fuer die Lebensdauer des geteilten Kontexts. Fuer einen Endpunkt, der nur berichtet,
    /// ist beides falsch: er braucht die Zeilen fuer einen Augenblick, und niemand wird sie je aendern.
    /// Gemessen am 26.11.2026 hing der Diagnose-Endpunkt damit rund 13.000 Zeilen dauerhaft in den Kontext
    /// (F-M769 zeigte 31.117 mitgefuehrte Zeilen), also 10 bis 30 MB, die erst ein Neustart freigab.
    /// </para>
    /// <para>
    /// Diese Methode liest dieselben Zeilen ohne Verfolgung und ruehrt den Zeilenspeicher der Area nicht an:
    /// ein Aufruf hinterlaesst nichts. Sie ist nur fuer LESER da — wer eine Zeile aendern will, nimmt
    /// weiterhin <see cref="FindAll"/> oder <see cref="FindById"/>, denn nur dort ist die Instanz die, die
    /// gespeichert wird.
    /// </para>
    /// </summary>
    /// <returns>Detached copies of every row.</returns>
    public List<TEntity> ReadOnlySnapshot()
    {
        lock (_db.Gate)
        {
            return _db.Set<TEntity>().AsNoTracking().ToList();
        }
    }

    /// <summary>
    /// F-M770: die Zahl der Zeilen, ohne die Zeilen selbst in den Kontext zu holen.
    /// </summary>
    /// <returns>The number of rows.</returns>
    public int CountReadOnly()
    {
        lock (_db.Gate)
        {
            return _db.Set<TEntity>().Count();
        }
    }

    /// <summary>Rows matching a predicate, evaluated in memory.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The matching rows.</returns>
    public IEnumerable<TEntity> Find(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().Where(compiled).ToList();
        }
    }

    /// <summary>One row by its business key, or null.</summary>
    /// <param name="id">Business key.</param>
    /// <returns>The row, or null.</returns>
    public TEntity? FindById(string id)
    {
        lock (_db.Gate)
        {
            return Rows().FirstOrDefault(r => string.Equals(_keyText(r), id, StringComparison.Ordinal));
        }
    }

    /// <summary>The first row matching a predicate, or null.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The row, or null.</returns>
    public TEntity? FindOne(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().FirstOrDefault(compiled);
        }
    }

    /// <summary>Whether any row matches.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>True when at least one row matches.</returns>
    public bool Exists(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().Any(compiled);
        }
    }

    /// <summary>Number of rows in the area.</summary>
    /// <returns>The row count.</returns>
    public int Count()
    {
        lock (_db.Gate)
        {
            return Rows().Count;
        }
    }

    /// <summary>Number of rows matching a predicate.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The matching row count.</returns>
    public int Count(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            return Rows().Count(compiled);
        }
    }

    /// <summary>Insert or update one row, matched by its business key.</summary>
    /// <param name="entity">The row.</param>
    public void Upsert(TEntity entity)
    {
        lock (_db.Gate)
        {
            UpsertCore(entity);
        }
    }

    private void UpsertCore(TEntity entity)
    {
        var key = _keyText(entity);

        // F-M514: OHNE SCHLUESSEL GIBT ES KEINE ZEILE — und ein Schreibversuch darf den Store nicht
        // vergiften. Eine Zeile ohne Geschäftsschlüssel kann Entity Framework nicht verfolgen; seit
        // EF Core 10 bleibt sie danach als Detached im Change Tracker stehen, und JEDES spätere
        // SaveChanges dieses Kontexts wirft „Unexpected entry.EntityState: Detached". Deshalb wird
        // sie hier gar nicht erst angehängt: warnen, nicht schreiben. Die Regel gilt für JEDE Area
        // und damit für jeden Worker, weil sie im gemeinsamen Set steht, nicht am Aufrufer.
        if (string.IsNullOrEmpty(key))
        {
            _db.Logger?.LogWarning(
                "[Sub Scribe-DB] {Area}: a row without a business key was not written (F-M514)",
                typeof(TEntity).Name);
            return;
        }

        var existing = Rows().FirstOrDefault(r => string.Equals(_keyText(r), key, StringComparison.Ordinal));
        if (existing == null)
        {
            // F-M671: die Kennung wird VOR dem Anhängen vergeben — eine Zeile ohne Kennung ist keine
            // Zeile, und ein Nachziehen nach dem Anhängen wäre eine zweite Wahrheit über dieselbe Zeile.
            _onNew?.Invoke(entity);

            // Attach FIRST, cache second: a rejected attach that lands in the cache would leave a
            // row behind that no table holds and no key can address.
            _db.Add(entity);
            _rowCache.Add(entity);
        }
        else if (!ReferenceEquals(existing, entity))
        {
            // F-M671: die KENNUNG der vorhandenen Zeile überlebt den Schreibvorgang. Ein Aufrufer, der
            // eine frische Instanz ohne Kennung baut (der Normalfall), darf sie nicht leeren — sonst
            // wäre „unveränderlich" nur eine Behauptung.
            string? gehalten = null;
            foreach (var pk in _db.Model.FindEntityType(typeof(TEntity))!.FindPrimaryKey()!.Properties)
            {
                var wert = pk.PropertyInfo?.GetValue(entity) as string;
                if (string.IsNullOrEmpty(wert))
                {
                    var alt = pk.PropertyInfo?.GetValue(existing) as string;
                    if (!string.IsNullOrEmpty(alt))
                    {
                        pk.PropertyInfo!.SetValue(entity, alt);
                    }
                }
            }
            _ = gehalten;

            _db.Entry(existing).CurrentValues.SetValues(entity);

            // Keep the cache pointing at the instance that now holds the new values, so a read
            // straight after the write sees them without a reload from disk.
            var at = _rowCache.IndexOf(existing);
            if (at >= 0 && !ReferenceEquals(entity, existing))
            {
                _rowCache[at] = existing;
            }
        }

        _db.RequestSave();

        // The cache is maintained IN PLACE rather than dropped. Marking it dirty here would make a
        // write cost a full table read, which turns a run's writes into quadratic work: a refresh
        // that writes 3000 embedded rows would re-read the whole area 3000 times. Measured on the
        // 117-row live store the difference is invisible; on a large library it is the difference
        // between one pass and thousands.
    }

    /// <summary>Update one row, matched by its business key. Alias of <see cref="Upsert"/>.</summary>
    /// <param name="entity">The row.</param>
    public void Update(TEntity entity) => Upsert(entity);

    /// <summary>
    /// Update many rows, each matched by its business key.
    /// <para>
    /// The previous engine accepted a list here and callers rely on it — the reset path collects the
    /// rows it cleared and persists exactly those, and the refresh path does the same for sidecar
    /// rows whose forced flag it just stated. Without this overload the batch would have to be
    /// written row by row at each call site, which is how one of them silently stops persisting.
    /// </para>
    /// </summary>
    /// <param name="entities">The rows.</param>
    public void Update(IEnumerable<TEntity> entities)
    {
        lock (_db.Gate)
        {
            // ONE SaveChanges for the batch. UpsertCore saves per row, which turns a reset that
            // clears 3000 markers into 3000 round trips; the batch is what the callers actually mean,
            // and the reset path persists a list precisely so it is a single write.
            var any = false;
            foreach (var e in entities)
            {
                var key = _keyText(e);
                if (string.IsNullOrEmpty(key))
                {
                    // F-M514: see UpsertCore — a keyless row is not a row, and attaching it would
                    // leave a Detached entry that breaks every later write of this context.
                    _db.Logger?.LogWarning(
                        "[Sub Scribe-DB] {Area}: a row without a business key was not written (F-M514)",
                        typeof(TEntity).Name);
                    continue;
                }

                any = true;
                var existing = Rows().FirstOrDefault(r => string.Equals(_keyText(r), key, StringComparison.Ordinal));
                if (existing == null)
                {
                    _db.Add(e);
                    _rowCache.Add(e);
                }
                else if (!ReferenceEquals(existing, e))
                {
                    _db.Entry(existing).CurrentValues.SetValues(e);
                }

                // When the caller passed the SAME instance the cache handed out, it mutated that
                // tracked instance in place and there is nothing to copy — the change is visible to
                // Entity Framework's change tracker, and SaveChanges is what persists it. Skipping
                // the save in that case is exactly the F-M192 defect: the reset reported success
                // while the rows kept their markers, because the marked list was never written.
            }

            if (any)
            {
                _db.RequestSave();
            }
        }
    }

    /// <summary>Insert many rows in one transaction, for the import path.</summary>
    /// <param name="entities">The rows.</param>
    public void InsertBulk(IEnumerable<TEntity> entities)
    {
        lock (_db.Gate)
        {
            foreach (var e in entities)
            {
                // F-M514: a keyless row is not a row — see UpsertCore.
                if (string.IsNullOrEmpty(_keyText(e)))
                {
                    _db.Logger?.LogWarning(
                        "[Sub Scribe-DB] {Area}: a row without a business key was not written (F-M514)",
                        typeof(TEntity).Name);
                    continue;
                }

                _db.Add(e);
                _rowCache.Add(e);
            }

            _db.RequestSave();
        }
    }

    /// <summary>
    /// F-M671: entfernt genau die übergebene Zeile. Nötig, weil die Spur- und Seitenwagen-Areale über ihre
    /// ADRESSE (Hash, Position bzw. Inhalt) angesprochen werden, gelöscht aber eine Zeile werden muss, die
    /// der Aufrufer in der Hand hat — ihre Kennung ist nicht die Adresse.
    /// </summary>
    /// <param name="entity">The row to remove.</param>
    /// <returns>True when the row was in the area.</returns>
    public bool Remove(TEntity entity)
    {
        lock (_db.Gate)
        {
            var existing = Rows().FirstOrDefault(r => ReferenceEquals(r, entity))
                           ?? Rows().FirstOrDefault(r => string.Equals(_keyText(r), _keyText(entity), StringComparison.Ordinal));
            if (existing == null)
            {
                return false;
            }

            _db.Remove(existing);
            _db.RequestSave();
            _rowCache.Remove(existing);
            return true;
        }
    }

    /// <summary>Delete the row with this business key.</summary>
    /// <param name="id">Business key.</param>
    /// <returns>True when a row was deleted.</returns>
    public bool Delete(string id)
    {
        lock (_db.Gate)
        {
            var existing = Rows().FirstOrDefault(r => string.Equals(_keyText(r), id, StringComparison.Ordinal));
            if (existing == null)
            {
                return false;
            }

            _db.Remove(existing);
            _db.RequestSave();
            _rowCache.Remove(existing);
            return true;
        }
    }

    /// <summary>Delete every row matching a predicate.</summary>
    /// <param name="predicate">The condition.</param>
    /// <returns>The number of rows deleted.</returns>
    public int DeleteMany(Expression<Func<TEntity, bool>> predicate)
    {
        var compiled = predicate.Compile();
        lock (_db.Gate)
        {
            // F-M716 (Befund 23.11.2026, 19:46): wer dieselbe Zeile im selben Kontext zweimal zum Löschen
            // anmeldet, dessen zweites DELETE trifft 0 Zeilen — und EF wirft dafür eine
            // DbUpdateConcurrencyException, die den ganzen Aufraeumlauf abbrach (PruneOrphanedPacks →
            // ForgetSubject, vier Loeschaufrufe hintereinander). Schon als gelöscht markierte Zeilen
            // werden deshalb hier nicht noch einmal angemeldet: „ist schon weg" ist das Ziel, kein Fehler.
            var doomed = Rows()
                .Where(compiled)
                .Where(e => _db.Entry(e).State != EntityState.Deleted)
                .ToList();
            if (doomed.Count == 0)
            {
                return 0;
            }

            _db.RemoveRange(doomed);
            _db.RequestSave();
            foreach (var d in doomed)
            {
                _rowCache.Remove(d);
            }

            return doomed.Count;
        }
    }

    /// <summary>Delete every row of the area.</summary>
    public void DeleteAll()
    {
        lock (_db.Gate)
        {
            _db.RemoveRange(Rows().ToList());
            _db.RequestSave();
            _rowCache.Clear();
        }
    }

    private List<TEntity> Rows()
    {
        if (_dirty)
        {
            // TRACKED, deliberately — the identity map IS the write path here. An entity handed out
            // by FindById and then mutated by its caller is the instance SaveChanges will write, and
            // a second read returns that same instance rather than a detached copy that would be
            // re-attached and throw. These areas are small by construction (117 rows measured), so
            // holding them for the context's lifetime is the cheap side of the trade.
            _rowCache = _db.Set<TEntity>().ToList();
            _dirty = false;
        }

        return _rowCache;
    }

    private List<TEntity> _rowCache = new();
}
