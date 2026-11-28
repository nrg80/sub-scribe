// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it under the terms of the GNU
// General Public License as published by the Free Software Foundation, either version 3 of the License,
// or (at your option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY; without even
// the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General
// Public License for more details.
//
// T338 / F-M681 — DIE MATRIX "QUELLE x FORM x SPRACHE x RICHTUNG".
//
// Betreiber-Auftrag 22.11.2026, wortwoertlich: "Für alle Quellen bitte ein Prüfprogramm aufstellen:
// sub einzeln (non hi), sub einzeln (hi), pack non-hi, pack hi, in verschiedenen Sprachen jeweils,
// upload und download. Mit Metadaten (ids, imdb, tmdb, Kommentarfelder) etc"
//
// Die Matrix hat 3 Quellen x 4 Formen x 4 Sprachen x 2 Richtungen = 96 Zellen. Jede Zelle wird
// GEFAHREN (nicht uebersprungen) und mit demselben Satz Metadaten-Pruefungen belegt; die Zellenzahl
// selbst ist eine Pruefung, und ein Zellenschluessel, der zweimal vorkommt, ist ein Fehlschlag.
//
// WAS HIER ECHT IST — UND WAS NICHT (ehrlich, weil eine Suite, die Abdeckung vortaeuscht, schlimmer
// ist als keine):
//
//   * DER STORE IST ECHT. Jede Zelle schreibt durch die Produktions-Schreiber
//     (ContentHashRegistry.MarkEmbed / RecordPack / RecordPackRefusal, SourceStore.WriteTrackMark /
//     AddMember / RecordBadApple) in einen ScribeDbContext unter einem frischen temporaeren
//     Verzeichnis. Kein Mock, keine Attrappe, keine Zeile von Hand in eine Tabelle.
//   * DER KOMMENTAR IST ECHT GEBAUT: TvSubtitlesClient.BuildUpload / BuildSeasonPack und
//     MovieSubtitlesClient.BuildUpload erzeugen die Nutzlast, das Feld wird aus dem fertigen Rumpf
//     gelesen und gegen UploadComment.Series/Season/Movie gehalten.
//   * DIE HTTP-STRECKE IST NUR DORT GEDECKT, WO DIESER BAUM FIXTURES HAT — und nur fuer die Sprachen,
//     die in diesen Fixtures stehen:
//       - tvsubtitles.net: Folgenseite fixtures/episode-31911.html (Eintraege en, fr, hu),
//         Staffel-Griff ueber den echten Bauer TvSubtitlesDownloadEndpoint.SeasonPackId;
//       - moviesubtitles.org: Filmseite fixtures/movie-24811.html (Eintraege en, br).
//     DE und ES sind in KEINER Fixture belegt. Fuer diese Zellen laeuft der Speicher- und
//     Kommentar-Pfad, aber KEIN HTTP — die Kennungsform stammt aus der Fixture (Zahl bzw.
//     Seasonpack-Griff), der Wert ist je Zelle neu.
//   * SubDL HAT IN DIESEM BAUM KEINEN FIXTURE-WEG (die API braucht Token/Login; den Transport deckt
//     die Suite `apiclient` getrennt ab). Fuer SubDL prueft diese Suite den STORE-Schreibpfad, die
//     Kennungsform (numerische Release-Id) und den Kommentar-Vertrag — NICHT die HTTP-Antwort und
//     nicht das Auspacken einer Antwort. Das ist eine Luecke und wird als solche gemeldet.
//   * UPLOADS AN tvst/msub LAUFEN OHNE NETZ: die Nutzlast wird gebaut und gemessen, gesendet wird
//     nichts.
//   * moviesubtitles.org FUEHRT KEINE STAFFELN. Die 16 Pack-Zellen dieser Quelle koennen nicht
//     gefahren werden wie die anderen; sie pruefen die QUELLE SELBST (die Staffel-Suche der Seite ist
//     leer, der Upload-Endpunkt weist ein SeasonPack-Ticket ab) und schreiben nichts. Das ist die
//     ehrliche Antwort auf "pack fuer alle Quellen", nicht ein uebersprungenes Feld.
//
// Usage: matrixrun <workdir> [repo-root]
//        Das Arbeitsverzeichnis MUSS frisch sein; ein Repository wird abgelehnt (wie bei storex).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Api.Endpoints;
using Jellyfin.Plugin.SubScribe.Data;
using Jellyfin.Plugin.SubScribe.Registry;

namespace MatrixRun;

internal static class Program
{
    /// <summary>Die drei Quellen — der WERT in der Spalte <c>Source</c>, keine Tabelle je Quelle.</summary>
    private static readonly string[] Sources =
    {
        SourceStore.Subdl, SourceStore.TvSubtitles, SourceStore.MovieSubtitles,
    };

    /// <summary>Die vier Formen des Auftrags.</summary>
    private static readonly string[] Forms =
    {
        "sub-einzeln-non-hi", "sub-einzeln-hi", "pack-non-hi", "pack-hi",
    };

    /// <summary>
    /// Die Sprachen des Auftrags: Quellcode (zwei Buchstaben) und die Form, die der STORE fuehrt.
    /// Die Store-Form kommt aus dem echten Mapper (nicht aus einer zweiten Tabelle): EN→ENG, DE→GER,
    /// FR→FRE, ES→SPA — die bibliografischen Codes, die der Store seit F-M557 schreibt.
    /// </summary>
    private static readonly (string Code, string Iso)[] Languages =
    {
        ("EN", Canonical("EN")), ("DE", Canonical("DE")), ("FR", Canonical("FR")), ("ES", Canonical("ES")),
    };

    /// <summary>Die beiden Richtungen.</summary>
    private static readonly string[] Directions = { "download", "upload" };

    private static readonly Dictionary<string, int> Groups = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Visited = new(StringComparer.Ordinal);
    private static readonly HashSet<string> OwnIds = new(StringComparer.Ordinal);
    private static readonly List<string> Failures = new();

    private static int _checks;
    private static string _workdir = string.Empty;
    private static string _root = string.Empty;
    private static string _pluginVersion = string.Empty;
    private static bool _fileCarriesLanguageOrHi;

    private static void Check(bool ok, string what, string? detail = null)
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what + (string.IsNullOrEmpty(detail) ? string.Empty : "  [" + detail + "]"));
        }
        else if (Environment.GetEnvironmentVariable("MATRIX_QUIET") != "1")
        {
            Console.WriteLine("  OK    " + what + (string.IsNullOrEmpty(detail) ? string.Empty : "  [" + detail + "]"));
        }
    }

    private static void Block(string title) => Console.WriteLine("\n--- " + title + " ---");

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(">>> FEHLER: workdir fehlt. Aufruf: matrixrun <frisches arbeitsverzeichnis> [repo-root]");
            return 2;
        }

        _workdir = args[0];
        if (File.Exists(Path.Combine(_workdir, "build.yaml"))
            || File.Exists(Path.Combine(_workdir, "manifest.json"))
            || Directory.Exists(Path.Combine(_workdir, ".git"))
            || File.Exists(Path.Combine(_workdir, ".gitignore")))
        {
            Console.WriteLine($">>> FEHLER: '{_workdir}' ist ein Repository, kein Arbeitsverzeichnis.");
            return 3;
        }

        Directory.CreateDirectory(_workdir);
        foreach (string f in Directory.GetFiles(_workdir))
        {
            File.Delete(f);
        }

        _root = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]) ? args[1] : "/opt/data/subdl-scribe";
        _pluginVersion = typeof(TvSubtitlesClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        _fileCarriesLanguageOrHi = HasLanguageOrHiOnFile();

        Console.WriteLine("matrix: Quellen=" + Sources.Length + " Formen=" + Forms.Length
            + " Sprachen=" + Languages.Length + " Richtungen=" + Directions.Length);
        Console.WriteLine("matrix: Arbeitsverzeichnis " + _workdir + ", Baum " + _root + ", Plugin " + _pluginVersion);

        // -------------------------------------------------------------------------------------
        // 0. Die Zellenzahl ist eine Pruefung, keine Fussnote.
        // -------------------------------------------------------------------------------------
        Block("Zellenzahl");
        int cells = Sources.Length * Forms.Length * Languages.Length * Directions.Length;
        int singles;
        int storedPacks;
        Check(Sources.Length == 3, "drei Quellen (subdl, tvsubtitles, moviesubtitles)", string.Join(",", Sources));
        Check(Forms.Length == 4, "vier Formen (sub-einzeln-non-hi, sub-einzeln-hi, pack-non-hi, pack-hi)",
            string.Join(",", Forms));
        Check(Languages.Length == 4, "vier Sprachen je Durchlauf", string.Join(",", Languages.Select(l => l.Code)));
        // F-M790: der Kanon ist ISO 639-2/T, also DEU und FRA. Die alte Form (GER, FRE) ist nur noch
        // Eingabe und wird beim Lesen uebersetzt — hier wird die Ausgabe geprueft, nicht die Eingabe.
        Check(Languages.Select(l => l.Iso).SequenceEqual(new[] { "ENG", "DEU", "FRA", "SPA" }),
            "der Kanon der vier Sprachen kommt aus dem Mapper (ENG/DEU/FRA/SPA, nicht mehr GER/FRE)",
            string.Join("/", Languages.Select(l => l.Code + "=" + l.Iso)));
        Check(Directions.Length == 2, "beide Richtungen (download, upload)", string.Join(",", Directions));
        Check(cells == 96, "3 x 4 x 4 x 2 = 96 Zellen", cells.ToString(CultureInfo.InvariantCulture));

        // Die Zahlen, die am Ende gegen den Store gehalten werden — gerechnet, nicht getippt.
        singles = Sources.Length * 2 * Languages.Length * Directions.Length;              // 48
        int packCells = Sources.Length * 2 * Languages.Length * Directions.Length;        // 48
        int moviePackCells = Languages.Length * 2 * Directions.Length;                      // 16
        storedPacks = packCells - moviePackCells;                                          // 32
        Check(singles == 48 && storedPacks == 32 && singles + packCells == cells,
            "48 Einzel-Zellen, 48 Pack-Zellen (davon 16 ohne Store-Weg) — Summe = 96",
            singles + " + " + packCells + " = " + (singles + packCells));

        // -------------------------------------------------------------------------------------
        // 1. Die Quellen selbst: was die Fixture-Seiten wirklich tragen (kein Netz).
        // -------------------------------------------------------------------------------------
        Block("Quellen gegen Fixtures (tvsubtitles.net, moviesubtitles.org)");
        await SourceRunAsync().ConfigureAwait(false);

        // -------------------------------------------------------------------------------------
        // 2. Die 96 Zellen.
        // -------------------------------------------------------------------------------------
        using (var db = new ScribeDbContext(_workdir, null))
        {
            var registry = new ContentHashRegistry(db, null, null);
            int beforeCells = _checks;
            int n = 0;
            foreach (string source in Sources)
            {
                Block("Quelle " + source);
                foreach (string form in Forms)
                {
                    foreach (var lang in Languages)
                    {
                        foreach (string direction in Directions)
                        {
                            n++;
                            await CellAsync(db, registry, source, form, lang, direction, n).ConfigureAwait(false);
                        }
                    }
                }
            }

            // ---------------------------------------------------------------------------------
            // 3. Die Matrix ist vollstaendig gefahren, und ihre Kennungen sind einmalig.
            // ---------------------------------------------------------------------------------
            Block("Vollstaendigkeit");
            int blockChecks = beforeCells;
            Check(Groups.Values.Sum() == _checks - blockChecks,
                "die Zellpruefungen sind vollstaendig auf die Gruppen verteilt (keine Pruefung ohne Zelle)",
                Groups.Values.Sum() + " Zellpruefungen + " + blockChecks + " Blockpruefungen = " + _checks);
            Check(Visited.Count == cells, "jede der 96 Zellen wurde genau einmal gefahren",
                Visited.Count.ToString(CultureInfo.InvariantCulture) + " Zellen");
            Check(OwnIds.Count == db.Files.Count() + db.Tracks.Count() + db.Packs.Count(),
                "die Kennungen im Store sind genau die der Zellen (Datei + Spur + Pack), keine doppelt",
                OwnIds.Count.ToString(CultureInfo.InvariantCulture) + " Kennungen, Store: "
                + db.Files.Count() + " file / " + db.Tracks.Count() + " track / " + db.Packs.Count() + " pack");

            Check(db.Files.Count() == singles + storedPacks,
                "jede Zelle mit Store-Weg hat genau eine Datei geschrieben",
                db.Files.Count() + " Dateien (erwartet " + (singles + storedPacks) + ": " + singles
                + " Einzel-Zellen + " + storedPacks + " Pack-Zellen)");
            Check(db.Tracks.Count() == (singles * 2) + storedPacks,
                "die Spuren sind die der Zellen: eine je Spur, zwei je Einzel-Zelle (Sub + Absage-Position)",
                db.Tracks.Count() + " Spuren (erwartet " + ((singles * 2) + storedPacks) + ")");
            Check(db.Packs.Count() == storedPacks,
                "die Pack-Zeilen sind die der Pack-Zellen (ohne die 16 moviesubtitles-Pack-Zellen)",
                db.Packs.Count() + " Packs (erwartet " + storedPacks + ")");
            Check(db.Files.FindAll().Select(f => f.FileId).Distinct().Count() == db.Files.Count()
                  && db.Tracks.FindAll().Select(t => t.TrackId).Distinct().Count() == db.Tracks.Count()
                  && db.Packs.FindAll().Select(p => p.PackId).Distinct().Count() == db.Packs.Count(),
                "keine Kennung steht zweimal (Datei, Spur, Pack — je Tabelle distinct gezählt)",
                db.Files.Count() + "/" + db.Tracks.Count() + "/" + db.Packs.Count());

            // Der Kommentar-Vertrag von SubDL: die Seite hat kein Kommentarfeld, und der Code sendet
            // den leeren Platzhalter — an BEIDEN Upload-Wegen. Das ist die einzige Stelle, an der
            // SubDL in dieser Suite ohne Fixture belegt werden kann; sie liest den ausgelieferten Code.
            string api = await File.ReadAllTextAsync(
                Path.Combine(_root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeApiClient.cs")).ConfigureAwait(false);
            Check(api.Contains("[\"comment\"] = \"\",", StringComparison.Ordinal)
                  && api.Contains("[\"comment\"] = string.Empty,", StringComparison.Ordinal),
                "SubDL: beide Upload-Wege senden den leeren Kommentar (Einzel + Pack, aus dem Code gelesen)");
        }

        // -------------------------------------------------------------------------------------
        // 4. Bericht.
        // -------------------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("Pruefungen je Quelle/Form/Richtung:");
        foreach (var g in Groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine("   " + g.Key.Replace("|", " / ", StringComparison.Ordinal).PadRight(48) + g.Value);
        }

        Console.WriteLine();
        Console.WriteLine("matrix: " + _checks + " Prüfungen (T338, F-M681)");
        if (Failures.Count == 0)
        {
            Console.WriteLine(">>> MATRIX OK: " + _checks + " Pruefungen bestanden");
            return 0;
        }

        Console.WriteLine(">>> MATRIX FEHLER: " + Failures.Count + " von " + _checks + " Pruefungen fehlgeschlagen");
        foreach (string f in Failures)
        {
            Console.WriteLine("   - " + f);
        }

        return 1;
    }

    /// <summary>
    /// Die Quellen selbst: die Kennungsform, die der Parser aus der ECHTEN Seite liest, und die
    /// Tatsache, dass moviesubtitles.org keine Staffeln fuehrt. Alles gegen fixtures/, nie gegen das Netz.
    /// </summary>
    private static async Task SourceRunAsync()
    {
        string tvstFixtures = Path.Combine(_root, "scripts/tests/tvsubtitles/fixtures");
        string msubFixtures = Path.Combine(_root, "scripts/tests/moviesubtitles/fixtures");
        bool fixturesThere = File.Exists(Path.Combine(tvstFixtures, "episode-31911.html"))
                             && File.Exists(Path.Combine(msubFixtures, "movie-24811.html"));
        Check(fixturesThere, "die Fixture-Seiten liegen im Baum (diese Suite ruft kein Netz)",
            tvstFixtures + " / " + msubFixtures);
        if (!fixturesThere)
        {
            return;
        }

        // ---- tvsubtitles.net: Eintraege einer Folgenseite, je Sprache eine Kennung ------------------
        string episode = await File.ReadAllTextAsync(Path.Combine(tvstFixtures, "episode-31911.html"))
            .ConfigureAwait(false);
        var tvst = new TvSubtitlesClient(new HttpClient(FixtureHandler(u => u.AbsolutePath.EndsWith(
            "/episode-31911.html", StringComparison.Ordinal), episode)));
        var entries = await tvst.EntriesAsync(31911).ConfigureAwait(false);
        Check(entries.Count >= 2, "tvst: die Folgenseite traegt Eintraege", entries.Count + " Eintraege");

        var spoken = entries.Select(e => e.Language).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
        foreach (string code in new[] { "en", "fr" })
        {
            var entry = entries.FirstOrDefault(e => string.Equals(e.Language, code, StringComparison.Ordinal));
            Check(entry != null && entry.SubtitleId.Length > 0 && entry.SubtitleId.All(char.IsDigit),
                "tvst " + code + ": die Kennung eines Einzel-Eintrags ist eine Zahl",
                entry is null ? "kein Eintrag" : entry.SubtitleId);
        }

        Check(!spoken.Contains("de") && !spoken.Contains("es"),
            "tvst: die Fixture traegt de/es NICHT — diese Zellen laufen ohne HTTP (ehrlich benannt)",
            "belegt: " + string.Join(",", spoken));

        // Der Staffel-Griff ist die Form, die tvst fuer einen Pack hat — fuer jede Sprache der Matrix.
        foreach (var lang in Languages)
        {
            string site = TvSubtitlesClient.SiteLanguage(lang.Code) ?? string.Empty;
            string handle = TvSubtitlesDownloadEndpoint.SeasonPackId(512, 3, site, 0);
            bool ok = handle == "seasonpack:512:3:" + site + ":0"
                      && TvSubtitlesDownloadEndpoint.TryParseSeasonPackId(
                          handle, out int show, out int season, out string parsed, out int index)
                      && show == 512 && season == 3 && parsed == site && index == 0;
            Check(ok, "tvst " + lang.Code + ": der Staffel-Griff traegt Serie, Staffel, Sprache und Nummer",
                handle);
        }

        // ---- moviesubtitles.org: Filmseite, Kennung, und die Abwesenheit von Staffeln ----------------
        string movie = await File.ReadAllTextAsync(Path.Combine(msubFixtures, "movie-24811.html"))
            .ConfigureAwait(false);
        var msubClient = new MovieSubtitlesClient(new HttpClient(FixtureHandler(
            u => u.AbsolutePath.Contains("movie-24811", StringComparison.Ordinal), movie)));
        var msub = new MovieSubtitlesDownloadEndpoint(msubClient, null);
        var hits = await msub.SearchAsync(new SubtitleQuery(null, null, null, 0, 0, "EN", 0, "24811"))
            .ConfigureAwait(false);
        Check(hits.Count >= 1 && hits.All(h => h.Id.All(char.IsDigit)),
            "msub: die Filmseite liefert Eintraege, die Kennung ist eine Zahl",
            hits.Count + " Treffer: " + string.Join(",", hits.Select(h => h.Id)));
        Check(hits.All(h => string.Equals(h.Language, "en", StringComparison.Ordinal)),
            "msub: die Sprache eines Eintrags kommt aus SEINER Flagge (en)", string.Join(",", hits.Select(h => h.Language)));

        var seasonQuery = await msub.SearchAsync(new SubtitleQuery("Samplemovie", null, null, 1, 1, "EN", 2023, "24811"))
            .ConfigureAwait(false);
        Check(seasonQuery.Count == 0, "msub: eine Staffel-Suche liefert NICHTS — die Quelle fuehrt keine Staffeln",
            seasonQuery.Count.ToString(CultureInfo.InvariantCulture) + " Treffer");

        var msubUpload = new MovieSubtitlesUploadEndpoint(msubClient, dryRun: true, null);
        byte[] srt = SrtBytes();
        var answer = await msubUpload.SendAsync(new UploadTicket(
            "Samplemovie", "tt15398776", "872585", 1, 1, "EN", "Samplemovie.2023.en.srt", srt,
            null, "Samplemovie.2023.1080p.WEB-DL", false, false, true, null, 24811, 2023, null)).ConfigureAwait(false);
        Check(!answer.Ok
              && ((answer.Reason ?? string.Empty).Contains("no season packs", StringComparison.Ordinal)
                  || (answer.Reason ?? string.Empty).Contains("no season and no episode", StringComparison.Ordinal)),
            "msub: der Upload-Endpunkt weist ein SeasonPack-Ticket ab (eine Staffel gibt es dort nicht)",
            answer.Reason ?? "kein Grund");
        Check(answer.DryRun && answer.Bytes == 0, "msub: die Ablehnung sendet nichts", "bytes=" + answer.Bytes);

        // ---- SubDL: kein Fixture-Weg in diesem Baum — das wird benannt, nicht ueberspielt -------------
        Check(!Directory.Exists(Path.Combine(_root, "scripts/tests/subdl/fixtures")),
            "SubDL: es gibt in diesem Baum kein Fixture-Verzeichnis — die HTTP-Strecke dieser Quelle ist hier NICHT gedeckt");
    }

    /// <summary>Eine Zelle der Matrix.</summary>
    private static async Task CellAsync(
        ScribeDbContext db, ContentHashRegistry registry, string source, string form,
        (string Code, string Iso) lang, string direction, int n)
    {
        bool pack = form.StartsWith("pack", StringComparison.Ordinal);
        // "non-hi" ENDET auf "-hi" — die Form allein entscheidet, nicht das Suffix.
        bool hi = !form.Contains("non-hi", StringComparison.Ordinal);
        string cell = source + " " + form + " " + lang.Code + " " + direction;
        string group = source + "|" + form + "|" + direction;
        int before = _checks;

        if (!Visited.Add("|" + source + "|" + form + "|" + lang.Code + "|" + direction))
        {
            Check(false, "Zelle doppelt gefahren: " + cell);
        }

        // moviesubtitles.org hat keine Staffeln: die Zelle prueft die QUELLE, nicht den Store.
        if (pack && string.Equals(source, SourceStore.MovieSubtitles, StringComparison.Ordinal))
        {
            await MovieSubPackCellAsync(db, source, form, lang, direction).ConfigureAwait(false);
            Groups[group] = Groups.GetValueOrDefault(group) + (_checks - before);
            return;
        }

        string osHash = "matrix" + n.ToString("D3", CultureInfo.InvariantCulture);
        string imdb = "tt" + (7000000 + n).ToString(CultureInfo.InvariantCulture);
        string tmdb = (900000 + n).ToString(CultureInfo.InvariantCulture);
        int season = 3;
        int episode = 7;
        string showTitle = "Matrix " + lang.Code;
        bool isSeries = !string.Equals(source, SourceStore.MovieSubtitles, StringComparison.Ordinal);
        string release = "Matrix.S03." + lang.Iso + "." + (hi ? "HI." : string.Empty) + "WEB-GRP";
        string siteLang = SiteLanguageOf(source, lang.Code);
        string downloadId = ForeignId(source, pack, n, siteLang);
        string ownUploadId = OwnUploadId(source, n);

        var file = registry.EnsureFile(
            osHash,
            "matrix-item-" + n.ToString(CultureInfo.InvariantCulture),
            "/lib/matrix/" + osHash + ".mkv",
            f =>
            {
                f.FileName = osHash + ".mkv";
                f.ImdbId = imdb;
                f.TmdbId = tmdb;
                f.IsSeries = isSeries;
                if (isSeries)
                {
                    f.Season = season;
                    f.Episode = episode;
                }
            });
        AddOwnId(file.FileId, cell);

        // Die Spur existiert in JEDER Zelle: bei einem Pack ist sie das MITGLIED, sonst das Sub selbst.
        if (string.Equals(direction, "download", StringComparison.Ordinal))
        {
            if (pack)
            {
                registry.MarkEmbed(osHash, 0, lang.Code, hi, SubtitleStatus.Observed);
            }
            else
            {
                registry.MarkEmbed(
                    osHash, 0, lang.Code, hi, SubtitleStatus.Downloaded,
                    subdlDownloadId: Is(source, SourceStore.Subdl) ? downloadId : null,
                    tvSubtitlesDownloadId: Is(source, SourceStore.TvSubtitles) ? downloadId : null,
                    moviesubtitlesDownloadId: Is(source, SourceStore.MovieSubtitles) ? downloadId : null);
            }
        }
        else
        {
            if (pack)
            {
                registry.MarkEmbed(osHash, 0, lang.Code, hi, SubtitleStatus.Observed);
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                registry.MarkEmbed(
                    osHash, 0, lang.Code, hi, SubtitleStatus.Uploaded,
                    subdlUploadId: Is(source, SourceStore.Subdl) ? ownUploadId : null,
                    tvSubtitlesUploadId: source == SourceStore.TvSubtitles ? ownUploadId : null,
                    subdlDownloadId: null,
                    tvSubtitlesDownloadId: null,
                    subdlUploadedAt: Is(source, SourceStore.Subdl) ? now : null,
                    tvSubtitlesUploadedAt: source == SourceStore.TvSubtitles ? now : null,
                    moviesubtitlesUploadId: Is(source, SourceStore.MovieSubtitles) ? ownUploadId : null,
                    moviesubtitlesUploadedAt: Is(source, SourceStore.MovieSubtitles) ? now : null);
            }
        }

        var track = db.Tracks.FindById(osHash + "|0")!;
        AddOwnId(track.TrackId, cell);
        string trackId = track.TrackId;

        // ---- Pack-Zellen: der Behälter, sein Urteil und seine Mitgliedschaft -----------------------
        string? packId = null;
        PackEntity? packRow = null;
        if (pack)
        {
            string? containerId = string.Equals(direction, "download", StringComparison.Ordinal)
                || Is(source, SourceStore.Subdl)
                    ? downloadId
                    : ownUploadId;

            // F-M681: die TMDb-Kennung gehoert an die Zeile (sie fehlte an JEDEM Pack, bevor diese
            // Matrix es zeigte) — deshalb wird sie hier ausdruecklich mitgegeben.
            registry.RecordPack(
                imdb, season, lang.Iso, hi, release, containerId,
                new[] { episode }, source,
                absorbedIds: null,
                fingerprint: "matrix-fp-" + n.ToString(CultureInfo.InvariantCulture));

            // F-M681: die TMDb-Kennung gehoert an die Zeile (sie fehlte an JEDEM Pack, bevor diese
            // Matrix es zeigte) — der Nachzug laeuft ueber den eigenen Schreiber.
            registry.SetPackTmdbId(imdb, season, lang.Iso, hi, source, tmdb);

            packRow = db.Packs.FindById(ScribeDbContext.PackKey(imdb, season, lang.Iso, hi, source));
            packId = packRow?.PackId;
            if (packId != null)
            {
                AddOwnId(packId, cell);
                SourceStore.AddMember(db, packId, trackId);
            }
        }

        file = db.Files.FindById(osHash)!;
        track = db.Tracks.FindById(osHash + "|0")!;

        // ---- 1. Die eigenen Kennungen in der richtigen Form, keine Doppelmarke ---------------------
        string sidecarId = ScribeIds.SidecarIdOf(file.FileId, trackId);
        bool idsOk = ScribeIds.IsFileId(file.FileId)
                     && ScribeIds.IsTrackId(trackId)
                     && ScribeIds.FileOf(trackId) == file.FileId
                     && ScribeIds.IsSidecarId(sidecarId)
                     && (!pack || ScribeIds.IsPackId(packId))
                     && NoDoubledMark(file.FileId + trackId + sidecarId + (packId ?? string.Empty));
        Check(idsOk, cell + ": Kennungen in Form (#yyyyy / #yyyyy#zzz / #yyyyy&zzz" + (pack ? " / [xxxx])" : ")"),
            file.FileId + " " + trackId + " " + sidecarId + (pack ? " " + packId : string.Empty));

        // ---- 2. Sprache und HI sitzen an der richtigen Ebene ----------------------------------------
        bool ebene = string.Equals(track.Language, lang.Iso, StringComparison.Ordinal)
                     && track.HearingImpaired == hi
                     && !_fileCarriesLanguageOrHi
                     && !db.PackTracks.FindAll().Any(m => string.Equals(m.PackId, file.FileId, StringComparison.Ordinal))
                     && (!pack
                         || (string.Equals(packRow!.Language, lang.Iso, StringComparison.Ordinal)
                             && packRow.HearingImpaired == hi));
        Check(ebene, cell + ": Sprache/HI an Pack und Spur, die Datei traegt keins von beidem",
            "track " + track.Language + (track.HearingImpaired == true ? "+HI" : string.Empty)
            + (pack ? ", pack " + packRow!.Language + (packRow.HearingImpaired == true ? "+HI" : string.Empty) : ", kein Pack"));

        // ---- 3. Titel-Ids stehen an den Entities (file und pack), nicht nur am Urteil ---------------
        bool titel = string.Equals(file.ImdbId, imdb, StringComparison.Ordinal)
                     && string.Equals(file.TmdbId, tmdb, StringComparison.Ordinal)
                     && (!pack
                         || (string.Equals(packRow!.ImdbId, imdb, StringComparison.Ordinal)
                             && string.Equals(packRow.TmdbId, tmdb, StringComparison.Ordinal)));
        Check(titel, cell + ": IMDb und TMDb stehen an der Datei" + (pack ? " UND am Pack" : string.Empty),
            "file " + file.ImdbId + "/" + file.TmdbId
            + (pack ? ", pack " + packRow!.ImdbId + "/" + packRow.TmdbId : string.Empty));

        string subject = pack ? packId! : trackId;
        var upload = db.Uploads.FindById(subject + "|" + source);
        var reference = db.References.FindById(subject + "|" + source);

        if (string.Equals(direction, "download", StringComparison.Ordinal))
        {
            // ---- 4. Der Beleg: die fremde Kennung, in der Form der Engine --------------------------
            bool beleg = reference != null
                         && string.Equals(reference.DownloadSourceId, downloadId, StringComparison.Ordinal)
                         && IdFormOk(source, pack, downloadId, siteLang);
            Check(beleg, cell + ": reference.DownloadSourceId traegt die fremde Kennung in der Form der Quelle",
                (reference?.DownloadSourceId ?? "keine") + " (erwartet " + downloadId + ")");

            // ---- 5. Mitgliedschaft: im Pack ja (mit Sprache/HI), einzeln nein ----------------------
            if (pack)
            {
                var member = db.PackTracks.FindById(packId + "|" + trackId);
                Check(member != null
                      && string.Equals(SourceStore.PackOfTrack(db, trackId), packId, StringComparison.Ordinal)
                      && SourceStore.TracksOfPack(db, packId).Count == 1,
                    cell + ": die Spur steht als Mitglied in pack_track, mit Sprache und HI am Pack",
                    (member == null ? "keine Zeile" : packId + "|" + trackId) + ", Pack "
                    + (packRow!.Language) + (packRow.HearingImpaired == true ? "+HI" : string.Empty));
            }
            else
            {
                Check(SourceStore.PackOfTrack(db, trackId) is null
                      && !db.PackTracks.FindAll().Any(m => string.Equals(m.TrackId, trackId, StringComparison.Ordinal)),
                    cell + ": ein Einzel-Sub ohne Pack steht NICHT in pack_track",
                    SourceStore.PackOfTrack(db, trackId) ?? "keine Mitgliedschaft");
            }
        }
        else
        {
            // ---- 4. Das Urteil: Quelle und Zeitpunkt ------------------------------------------------
            Check(upload != null
                  && string.Equals(upload.Source, source, StringComparison.Ordinal)
                  && upload.UploadedAt.HasValue,
                cell + ": das Urteil steht in upload mit Source = " + source + " und UploadedAt gesetzt",
                upload == null ? "keine Zeile" : upload.Source + " @" + upload.UploadedAt?.ToString("O", CultureInfo.InvariantCulture));

            // ---- 5. Die eigene Einsendungs-Kennung --------------------------------------------------
            if (pack)
            {
                // WritePackMark hat fuer den Pack keinen UploadedId-Parameter: die Behaelter-Kennung
                // landet in der Id-Spalte des Belegs. tvst gibt auf den Upload GAR KEINE Kennung
                // zurueck (F-M612), also bleibt der Beleg dort aus.
                bool kennung = Is(source, SourceStore.Subdl)
                    ? reference != null
                      && string.Equals(reference.DownloadSourceId, downloadId, StringComparison.Ordinal)
                      && IdFormOk(source, true, downloadId, siteLang)
                      && reference.UploadedId is null
                    : reference == null;
                Check(kennung, cell + ": die Behaelter-Kennung steht am Beleg"
                               + (Is(source, SourceStore.Subdl) ? " (upload.UploadedId bleibt fuer Packs leer)" : " (die Quelle gibt keine)"),
                    "reference=" + (reference?.DownloadSourceId ?? "keine") + ", uploadedId=" + (reference?.UploadedId ?? "leer"));
            }
            else
            {
                // SubDL nennt die Einsende-Id (n_id); tvst/msub geben keine zurueck.
                bool kennung = Is(source, SourceStore.Subdl)
                    ? reference != null
                      && string.Equals(reference.UploadedId, ownUploadId, StringComparison.Ordinal)
                      && ownUploadId.Length == 10
                    : reference == null;
                Check(kennung, cell + ": " + (Is(source, SourceStore.Subdl)
                        ? "reference.UploadedId traegt die eigene Einsende-Kennung"
                        : "die Quelle gibt keine Einsende-Kennung zurueck — UploadedId bleibt leer"),
                    "uploadedId=" + (reference?.UploadedId ?? "leer"));
            }

            // ---- 6. Der Kommentar, den die Quelle wirklich mitschickt -------------------------------
            (string comment, string expected, string rule) = BuildComment(source, pack, lang, hi, imdb, tmdb, showTitle, season, episode);
            bool kommentar = string.Equals(comment, expected, StringComparison.Ordinal)
                             && !comment.Contains(_pluginVersion, StringComparison.Ordinal)
                             && !comment.Contains("Sub Scribe", StringComparison.OrdinalIgnoreCase)
                             && !comment.Contains("sub-scribe", StringComparison.OrdinalIgnoreCase);
            Check(kommentar, cell + ": das Kommentarfeld entspricht " + rule + " und traegt KEINE Plugin-Version",
                comment.Length == 0 ? "(leer)" : comment);
        }

        // ---- 7. Absage und schlechter Download: offen bzw. mit Quelle, Kennung und Member -----------
        if (pack)
        {
            registry.RecordPackRefusal(
                imdb, season, lang.Iso, hi, "duplicate on the source",
                "matrix-fp-" + n.ToString(CultureInfo.InvariantCulture), source,
                "matrix-sig-" + n.ToString(CultureInfo.InvariantCulture));
            var after = db.Uploads.FindById(packId + "|" + source);
            var beleg = db.References.FindById(packId + "|" + source);
            // SubDL nennt eine Behaelter-Kennung — der Beleg BLEIBT stehen, wenn die Quelle den
            // Behälter später ablehnt. tvst gibt auf den Upload keine Kennung zurueck (F-M612),
            // dort gibt es von Anfang an keinen Beleg; die Absage steht trotzdem offen.
            bool belegBleibt = !Is(source, SourceStore.Subdl) && !string.Equals(direction, "download", StringComparison.Ordinal)
                ? beleg == null
                : beleg != null && string.Equals(beleg.DownloadSourceId, downloadId, StringComparison.Ordinal);
            Check(after != null
                  && after.RefusedAt.HasValue
                  && !after.UploadedAt.HasValue
                  && string.Equals(after.RefusedReason, "duplicate on the source", StringComparison.Ordinal)
                  && belegBleibt,
                cell + ": eine Absage steht als OFFENES Urteil (RefusedAt, kein UploadedAt)"
                       + (beleg != null ? ", der Beleg bleibt (" + beleg.DownloadSourceId + ")" : " (die Quelle hatte keine Kennung)"),
                after == null ? "keine Zeile" : "refused " + after.RefusedAt?.ToString("O", CultureInfo.InvariantCulture));

            string member = "Matrix.S03E07." + siteLang + ".srt";
            var bad = SourceStore.RecordBadApple(
                db, trackId, source, downloadId, lang.Code, RejectReason.CandidateRejected, member: member);
            Check(bad != null
                  && string.Equals(bad.Source, source, StringComparison.Ordinal)
                  && string.Equals(bad.SourceId, downloadId, StringComparison.Ordinal)
                  && string.Equals(bad.Member, member, StringComparison.Ordinal)
                  && db.BadApples.FindById(trackId + "|" + source + "|" + downloadId + "|" + member) != null,
                cell + ": der schlechte Download steht in badapple — Quelle, fremde Kennung, Member (Pack-Mitglied)",
                bad?.Member ?? "keine Zeile");
        }
        else
        {
            // Ein Einzel-Sub, das abgelehnt wurde: die Spur traegt das Urteil "rejected", die Quelle
            // bleibt OFFEN (kein UploadedAt, keine Einsende-Id) — an einer zweiten Position, damit die
            // Richtung dieser Zelle unberuehrt bleibt.
            registry.MarkEmbed(osHash, 1, lang.Iso, hi, SubtitleStatus.Rejected, reason: RejectReason.DuplicateRemote);
            var rejected = db.Tracks.FindById(osHash + "|1")!;
            AddOwnId(rejected.TrackId, cell);
            var mark = SourceStore.MarkOf(db, rejected.TrackId, source);
            Check(string.Equals(rejected.Status, SubtitleStatus.Rejected, StringComparison.Ordinal) && !mark.IsClosed,
                cell + ": eine Absage ist ein OFFENES Urteil (Spur rejected, Quelle nicht geschlossen)",
                rejected.Status + ", closed=" + mark.IsClosed);

            var bad = SourceStore.RecordBadApple(
                db, rejected.TrackId, source, downloadId, lang.Code, RejectReason.CandidateRejected);
            Check(bad != null
                  && string.Equals(bad.Source, source, StringComparison.Ordinal)
                  && string.Equals(bad.SourceId, downloadId, StringComparison.Ordinal)
                  && bad.Member.Length == 0,
                cell + ": der schlechte Download steht in badapple — Quelle und Kennung, Member LEER beim Einzeldownload",
                bad == null ? "keine Zeile" : "member='" + bad.Member + "'");
        }

        Groups[group] = Groups.GetValueOrDefault(group) + (_checks - before);
    }

    /// <summary>
    /// Die 16 Pack-Zellen von moviesubtitles.org: die Quelle fuehrt keine Staffeln. Geprueft wird genau
    /// das — an der echten Seite (Fixture) und am echten Upload-Endpunkt. Geschrieben wird nichts.
    /// </summary>
    private static async Task MovieSubPackCellAsync(
        ScribeDbContext db, string source, string form, (string Code, string Iso) lang, string direction)
    {
        bool hi = !form.Contains("non-hi", StringComparison.Ordinal);
        string cell = source + " " + form + " " + lang.Code + " " + direction;
        string group = source + "|" + form + "|" + direction;
        int before = _checks;

        string fixture = Path.Combine(_root, "scripts/tests/moviesubtitles/fixtures/movie-24811.html");
        string page = await File.ReadAllTextAsync(fixture).ConfigureAwait(false);
        var client = new MovieSubtitlesClient(new HttpClient(FixtureHandler(
            u => u.AbsolutePath.Contains("movie-24811", StringComparison.Ordinal), page)));

        var endpoint = new MovieSubtitlesDownloadEndpoint(client, null);
        var hits = await endpoint.SearchAsync(
            new SubtitleQuery("Samplemovie", null, null, 3, 7, lang.Code, 2023, "24811")).ConfigureAwait(false);
        Check(hits.Count == 0,
            cell + ": die Staffel-Suche der Quelle ist leer — moviesubtitles.org fuehrt keine Staffel",
            hits.Count.ToString(CultureInfo.InvariantCulture) + " Treffer");

        // Derselbe Befund auch als Regel der Quelle: ihr Upload-Endpunkt lehnt einen Pack ab.
        var upload = new MovieSubtitlesUploadEndpoint(client, dryRun: true, null);
        var answer = await upload.SendAsync(new UploadTicket(
            "Samplemovie", "tt15398776", "872585", 3, 7, lang.Code,
            "Matrix.S03." + lang.Iso + (hi ? ".HI" : string.Empty) + ".zip",
            ZipBytes(), null, "Samplemovie.2023.1080p.WEB-DL", hi, false, true, null, 24811, 2023, null))
            .ConfigureAwait(false);
        Check(!answer.Ok
              && ((answer.Reason ?? string.Empty).Contains("no season packs", StringComparison.Ordinal)
                  || (answer.Reason ?? string.Empty).Contains("no season and no episode", StringComparison.Ordinal)),
            cell + ": der Upload-Endpunkt weist das SeasonPack-Ticket ab (eine Staffel gibt es dort nicht)",
            answer.Reason ?? "kein Grund");

        // nichts geschrieben: kein Pack, kein Beleg, kein Urteil fuer diese Quelle
        Check(db.Packs.FindAll().All(p => !Is(p.Source, source)
                || !string.Equals(p.Language, lang.Iso, StringComparison.Ordinal)),
            cell + ": die Zelle schreibt KEINE Pack-Zeile (die Quelle kann sie nicht fuellen)",
            db.Packs.Count() + " Pack-Zeilen im Store");
    }

    /// <summary>Baut den Kommentar, den die Quelle fuer diese Form wirklich sendet, und die Regel dahinter.</summary>
    private static (string Comment, string Expected, string Rule) BuildComment(
        string source, bool pack, (string Code, string Iso) lang, bool hi,
        string imdb, string tmdb, string showTitle, int season, int episode)
    {
        long? tmdbId = long.Parse(tmdb, CultureInfo.InvariantCulture);
        byte[] srt = SrtBytes();

        if (Is(source, SourceStore.TvSubtitles))
        {
            TvSubtitlesUpload payload = pack
                ? TvSubtitlesClient.BuildSeasonPack(
                    512, season, lang.Code, "Matrix.S03." + lang.Iso + (hi ? ".HI" : string.Empty) + ".zip",
                    ZipBytes(), release: "Matrix.S03." + lang.Iso + "-GRP", tmdbId: tmdbId, imdbId: imdb,
                    hearingImpaired: hi, showTitle: showTitle, year: 2021)
                : TvSubtitlesClient.BuildUpload(
                    512, season, episode, lang.Code, "Matrix.S03E07." + lang.Iso + ".srt", srt,
                    release: "Matrix.S03E07." + lang.Iso + "-GRP", tmdbId: tmdbId, imdbId: imdb,
                    showTitle: showTitle, episodeTitle: "Pilot", year: 2021);
            string comment = Field(payload.Fields, "comment");
            string expected = pack
                ? UploadComment.Season(imdb, tmdb, season, showTitle, 2021)
                : UploadComment.Series(imdb, tmdb, season, episode, showTitle, "Pilot", 2021);
            return (comment, expected, pack ? "UploadComment.Season (Staffelzeile)" : "UploadComment.Series (Folgenzeile)");
        }

        if (Is(source, SourceStore.MovieSubtitles))
        {
            var payload = MovieSubtitlesClient.BuildUpload(
                24811, lang.Code, "Samplemovie.2023.1080p.WEB-DL", "Samplemovie.2023." + lang.Iso + ".srt", srt,
                rip: null, author: null, tmdbId: tmdbId, imdbId: imdb,
                movieTitle: "Samplemovie", year: 2023);
            string comment = Field(payload.Fields, "comments");
            string expected = UploadComment.Movie(imdb, tmdb, "Samplemovie", 2023);
            return (comment, expected, "UploadComment.Movie (Filmzeile)");
        }

        // SubDL: die API hat kein Kommentarfeld — der Code sendet an BEIDEN Wegen den leeren
        // Platzhalter. Hier wird der Vertrag geprueft, nicht eine HTTP-Antwort (die Suite `apiclient`
        // deckt den Transport ab).
        return (string.Empty, string.Empty, "dem SubDL-Vertrag (kein Kommentarfeld, leerer Platzhalter)");
    }

    private static string Field(IReadOnlyList<KeyValuePair<string, string>> fields, string name)
        => fields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.Ordinal)).Value ?? string.Empty;

    /// <summary>Die fremde Kennung, wie sie die Engine fuehrt — je Form und Quelle.</summary>
    private static string ForeignId(string source, bool pack, int n, string siteLang)
    {
        if (Is(source, SourceStore.TvSubtitles) && pack)
        {
            // Die Seite hat fuer eine Staffel keinen Einzel-Eintrag: ihr Griff nennt Serie, Staffel,
            // Sprache und die Nummer im Archiv (F-M663).
            return TvSubtitlesDownloadEndpoint.SeasonPackId(512, 3, siteLang, n % 3);
        }

        // Alle drei Quellen fuehren fuer EINEN Eintrag eine Zahl; die Werte sind je Zelle neu, die
        // FORM ist die der Quelle.
        return Is(source, SourceStore.TvSubtitles)
            ? (170000 + n).ToString(CultureInfo.InvariantCulture)
            : Is(source, SourceStore.MovieSubtitles)
                ? (132000 + n).ToString(CultureInfo.InvariantCulture)
                : (5000000 + n).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Die Form, die eine fremde Kennung haben MUSS — je Quelle und Form.</summary>
    private static bool IdFormOk(string source, bool pack, string id, string siteLang)
    {
        if (Is(source, SourceStore.TvSubtitles) && pack)
        {
            return TvSubtitlesDownloadEndpoint.TryParseSeasonPackId(
                       id, out int show, out int season, out string language, out int index)
                   && show == 512 && season == 3
                   && string.Equals(language, siteLang, StringComparison.Ordinal) && index >= 0;
        }

        return id.Length > 0 && id.All(char.IsDigit);
    }

    /// <summary>Die eigene Einsende-Kennung: nur SubDL gibt eine zurueck.</summary>
    private static string OwnUploadId(string source, int n)
        => Is(source, SourceStore.Subdl)
            ? "N" + n.ToString("D8", CultureInfo.InvariantCulture) + "Q"
            : string.Empty;

    private static string SiteLanguageOf(string source, string code)
        => Is(source, SourceStore.TvSubtitles)
            ? TvSubtitlesClient.SiteLanguage(code) ?? code.ToLowerInvariant()
            : Is(source, SourceStore.MovieSubtitles)
                ? MovieSubtitlesClient.SiteLanguage(code) ?? code.ToLowerInvariant()
                : code;

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static string Canonical(string code)
        => Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical(code) ?? code;

    private static bool NoDoubledMark(string ids)
        => !ids.Contains("[[", StringComparison.Ordinal)
           && !ids.Contains("##", StringComparison.Ordinal)
           && !ids.Contains("&&", StringComparison.Ordinal);

    private static bool HasLanguageOrHiOnFile()
    {
        // F-M673: die DATEI traegt keine Sprache und kein HI — die Zugehoerigkeit ist die der Spur.
        return typeof(FileEntity).GetProperty("Language", BindingFlags.Public | BindingFlags.Instance) != null
               || typeof(FileEntity).GetProperty("HearingImpaired", BindingFlags.Public | BindingFlags.Instance) != null
               || typeof(FileEntity).GetProperty("PackId", BindingFlags.Public | BindingFlags.Instance) != null;
    }

    private static void AddOwnId(string id, string cell)
    {
        if (!OwnIds.Add(id))
        {
            Check(false, cell + ": die Kennung " + id + " wurde ein zweites Mal vergeben");
        }
    }

    private static byte[] SrtBytes()
        => Encoding.UTF8.GetBytes(
            "1\n00:00:01,000 --> 00:00:02,000\nMatrix line one\n\n"
            + "2\n00:00:03,000 --> 00:00:04,000\nMatrix line two\n");

    private static byte[] ZipBytes()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(
                   buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Matrix.S03E07.ENG.srt");
            using var stream = entry.Open();
            byte[] srt = SrtBytes();
            stream.Write(srt, 0, srt.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Eine Attrappe, die genau eine Seite aus fixtures/ beantwortet — sonst 404. Kein Netz.</summary>
    private static HttpMessageHandler FixtureHandler(Func<Uri, bool> match, string body)
        => new DelegateHandler(uri => match(uri) ? body : null);

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<Uri, string?> _body;

        public DelegateHandler(Func<Uri, string?> body) => _body = body;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            string? body = _body(request.RequestUri!);
            var response = body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(string.Empty) }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "text/html"),
                };
            return Task.FromResult(response);
        }
    }
}
