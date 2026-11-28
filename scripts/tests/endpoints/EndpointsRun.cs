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

// T204 / F-M483-F-M484 (operator order 15.11.2026): two download endpoints and two upload endpoints,
// the rest of the plugin unchanged.
//
// What is asserted here, and why each one would bite if it drifted:
//   1. FOUR endpoints exist, two per direction, one per source — the whole point of the order.
//   2. The CATALOG hands out only what may run: switch on AND wired. A catalog that ignores the
//      wiring would let a caller believe tvsubtitles.net runs.
//   3. An implemented-but-unwired endpoint NAMES its reason, in the same words the page shows —
//      one sentence, not three (F-M366 line).
//   4. The SubDL download endpoint maps the client's candidate onto the neutral hit; the mapping is
//      pure, so it is checked without a network call.
//   5. The tvsubtitles download endpoint really walks the captured pages: show list → season →
//      episode page → entries filtered to the wanted language → fetch. The fake handler serves the
//      fixtures, so a changed page shape shows up as a red check.
//   6. The tvsubtitles upload endpoint builds the form body and sends NOTHING in a dry run, refuses
//      an unknown language with a reason — and takes a season pack as ONE zip into the site's season
//      slot (episode=0), but only while the GUI's pack switch is on (F-M641/F-M642).
//   7. The SubDL upload endpoint refuses what it cannot do (season pack, no id, non-UTF-8 bytes)
//      instead of half-sending it.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Api.Endpoints;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Registry;

internal static class Program
{
    private static int _failures;
    private static int _checks;
    private static string _fixtures = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        var root = args.Length > 0 ? args[0] : ScribeRepoPaths.Root;
        _fixtures = Path.Combine(root, "scripts/tests/tvsubtitles/fixtures");

        // ---- 1. four endpoints, two per direction ----------------------------------------------
        var tvClient = new TvSubtitlesClient(new HttpClient(new FixtureHandler(_fixtures)));
        var subdlClient = new ScribeApiClient(new HttpClient(new FixtureHandler(_fixtures)));
        var catalog = new SubtitleEndpointCatalog(
            new IDownloadEndpoint[]
            {
                new ScribeDownloadEndpoint(subdlClient),
                new TvSubtitlesDownloadEndpoint(tvClient),
            },
            new IUploadEndpoint[]
            {
                new ScribeUploadEndpoint(subdlClient),
                new TvSubtitlesUploadEndpoint(tvClient, dryRun: true),
            });

        Check("there are two download endpoints, one per source",
            catalog.AllDownloads.Count == 2
            && catalog.AllDownloads.Select(e => e.Provider).Distinct().Count() == 2);
        Check("there are two upload endpoints, one per source",
            catalog.AllUploads.Count == 2
            && catalog.AllUploads.Select(e => e.Provider).Distinct().Count() == 2);
        Check("both sources are wired in both directions (F-M485)",
            catalog.AllDownloads.All(e => e.Wired) && catalog.AllUploads.All(e => e.Wired));

        // ---- 2. the catalog obeys switches AND wiring ------------------------------------------
        // F-M503: der tvst-Download ist im null-Fall AN. „alles aus" heisst deshalb seit dem
        // Operator-Entscheid vom 15.11.2026: BEIDE Download-Schalter aus — der Alt-Schalter allein
        // traegt die Quelle nicht mehr.
        // F-M507: „alles aus" verlangt jetzt DREI Download-Schalter, weil die dritte Quelle im
        // ungesetzten Fall an ist.
        var aus = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.MovieSubtitlesDownloadEnabled = false;
        });
        Check("everything off yields no endpoint",
            catalog.EnabledDownloads(aus).Count == 0 && catalog.EnabledUploads(aus).Count == 0);

        var nurDownload = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.MovieSubtitlesDownloadEnabled = false;
        });
        Check("the download switch alone yields exactly the SubDL download endpoint",
            catalog.EnabledDownloads(nurDownload).Count == 1
            && catalog.EnabledDownloads(nurDownload)[0].Provider == SubtitleProvider.Subdl
            && catalog.EnabledUploads(nurDownload).Count == 0);

        var beide = Conf(c => { c.DownloadEnabled = true; c.UploadEnabled = true; });
        // F-M503 (Nutzerentscheid 15.11.2026): der ungesetzte tvst-Download-Schalter zieht die zweite
        // Quelle jetzt herein — das ist der Default, keine Regression. Der Upload bleibt unberuehrt.
        Check("an unset tvsubtitles DOWNLOAD switch now pulls the second source in — the upload switch does not (F-M503)",
            catalog.EnabledDownloads(beide).Count == 2
            && catalog.EnabledDownloads(beide).Any(e => e.Provider == SubtitleProvider.TvSubtitles)
            && catalog.EnabledUploads(beide).All(e => e.Provider == SubtitleProvider.Subdl),
            "downloads=" + catalog.EnabledDownloads(beide).Count + " uploads=" + catalog.EnabledUploads(beide).Count);

        var beideTvstAus = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = true;
            c.TvSubtitlesDownloadEnabled = false;
        });
        Check("an explicitly switched-off tvsubtitles download stays out of the run (F-M503)",
            catalog.EnabledDownloads(beideTvstAus).All(e => e.Provider == SubtitleProvider.Subdl)
            && catalog.EnabledUploads(beideTvstAus).All(e => e.Provider == SubtitleProvider.Subdl));

        var tvAn = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.UploadEnabled = true;
            c.TvSubtitlesDownloadEnabled = true;
            c.TvSubtitlesUploadEnabled = true;
        });
        Check("the tvsubtitles switches now pull the second endpoint in, each on its own",
            catalog.EnabledDownloads(tvAn).Count == 2 && catalog.EnabledUploads(tvAn).Count == 2,
            "downloads=" + catalog.EnabledDownloads(tvAn).Count + " uploads=" + catalog.EnabledUploads(tvAn).Count);

        var tvNurDownload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = true;
            c.TvSubtitlesUploadEnabled = false;
        });
        Check("tvsubtitles download alone runs while both SubDL directions stay off",
            catalog.EnabledDownloads(tvNurDownload).Count == 1
            && catalog.EnabledDownloads(tvNurDownload)[0].Provider == SubtitleProvider.TvSubtitles
            && catalog.EnabledUploads(tvNurDownload).Count == 0);

        var tvNurUpload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = false;
            c.TvSubtitlesDownloadEnabled = false;
            c.TvSubtitlesUploadEnabled = true;
        });
        Check("tvsubtitles upload alone runs while everything else is off",
            catalog.EnabledUploads(tvNurUpload).Count == 1
            && catalog.EnabledUploads(tvNurUpload)[0].Provider == SubtitleProvider.TvSubtitles
            && catalog.EnabledDownloads(tvNurUpload).Count == 0);

        var nurSubdlUpload = Conf(c =>
        {
            c.DownloadEnabled = false;
            c.UploadEnabled = true;
            c.TvSubtitlesUploadEnabled = false;
        });
        Check("SubDL upload alone stays alone", catalog.EnabledUploads(nurSubdlUpload).Count == 1
            && catalog.EnabledUploads(nurSubdlUpload)[0].Provider == SubtitleProvider.Subdl);

        // ---- 3. the refusal names its reason, in the page's words ------------------------------
        var refusedDown = catalog.Refused(tvAn, SubtitleDirection.Download);
        var refusedUp = catalog.Refused(tvAn, SubtitleDirection.Upload);
        Check("with both switches on nothing is refused", refusedDown.Count == 0 && refusedUp.Count == 0);
        var tvAus = Conf(c => { c.DownloadEnabled = true; c.TvSubtitlesDownloadEnabled = false; });
        var refusedTvAus = catalog.Refused(tvAus, SubtitleDirection.Download);
        Check("a source whose switch is off is listed as refused, not silently missing",
            refusedTvAus.Count == 1 && refusedTvAus[0].Provider == SubtitleProvider.TvSubtitles
            && refusedTvAus[0].Reason == "switched off");
        Check("the sentence for a source is the one the config page prints",
            PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles) is { Length: > 20 }
            && PluginConfiguration.MissingProviderReason(SubtitleProvider.TvSubtitles)!
                .Contains("Staffelpacks", StringComparison.Ordinal));
        Check("a wired endpoint that is switched off says so",
            catalog.Refused(aus, SubtitleDirection.Download)
                .Any(r => r.Provider == SubtitleProvider.Subdl && r.Reason == "switched off"));

        // ---- 4. the SubDL mapping is pure and complete ----------------------------------------
        var candidate = new SubtitleCandidate
        {
            ReleaseName = "The.Sample.S02E24.720p.HDTV.x264-LOL",
            Language = "DE",
            Url = "https://dl.subdl.com/subtitle/abc123.zip",
            Season = 2,
            Episode = 24,
            FullSeason = false,
        };
        var map = ScribeDownloadEndpoint.Map(candidate);
        Check("a SubDL candidate becomes a neutral hit without losing its handle",
            map.Provider == SubtitleProvider.Subdl && map.Id == candidate.Url
            && map.Language == "DE" && map.Release == candidate.ReleaseName && !map.FullSeason);
        Check("SubDL has no rip cell, so the hit does not invent one", map.Rip.Length == 0);
        Check("the language bridge knows the site's vocabulary and refuses the rest",
            TvSubtitlesDownloadEndpoint.SiteLanguage("DE") == "de"
            && TvSubtitlesDownloadEndpoint.SiteLanguage("pt") == "pt"
            && TvSubtitlesDownloadEndpoint.SiteLanguage("xx") is null);

        // ---- 5. the tvsubtitles download endpoint walks the captured pages ---------------------
        var tvDownload = new TvSubtitlesDownloadEndpoint(tvClient);
        var query = new SubtitleQuery("The Sample", null, null, 2, 24, "en");
        var hits = await tvDownload.SearchAsync(query).ConfigureAwait(false);
        Check("the search finds the episode page and keeps the wanted language",
            hits.Count == 1 && hits[0].Language == "en" && hits[0].Id == "170678",
            "found: " + string.Join(",", hits.Select(h => h.Language + "/" + h.Id)));
        var andereSprache = await tvDownload.SearchAsync(query with { Language = "fr" }).ConfigureAwait(false);
        Check("a different language yields that language's entry instead",
            andereSprache.Count == 1 && andereSprache[0].Id == "190460");
        var falscheFolge = await tvDownload.SearchAsync(query with { Episode = 23 }).ConfigureAwait(false);
        Check("an episode the source does not carry yields nothing, not the nearest one",
            falscheFolge.Count == 0);
        var unbekannteSerie = await tvDownload.SearchAsync(query with { ShowName = "Nope-Nope" }).ConfigureAwait(false);
        Check("an unknown show yields nothing", unbekannteSerie.Count == 0);
        var ohneNamen = await tvDownload.SearchAsync(query with { ShowName = null }).ConfigureAwait(false);
        Check("a source without an id index needs the name — no name, no hits", ohneNamen.Count == 0);

        var payload = await tvDownload.FetchAsync(hits[0]).ConfigureAwait(false);
        Check("the fetch delivers name and bytes, undecoded",
            payload is not null && payload.Data.Length > 0 && payload.FileName.EndsWith(".srt", StringComparison.Ordinal),
            payload is null ? "no payload" : payload.FileName + " " + payload.Data.Length + " B");

        // ---- 5b. F-M663: die Staffel-ZIP (Stufe 1) fuellt BEIDE Seiten, getrennt ----------------
        // Die Trennung ist eine reine Funktion (DownloadPipeline.SplitByHearingImpaired) und wird hier
        // ohne Lauf geprueft: regulaer auf die regulaere Seite, HI auf die HI-Seite, nie beide.
        var gemischt = new[]
        {
            new SubtitleHit(SubtitleProvider.TvSubtitles, "seasonpack:3234:2:en:0", "en", string.Empty, "Sampleland - 2x05 - Episode Title.WEB.GRP", false, false),
            new SubtitleHit(SubtitleProvider.TvSubtitles, "seasonpack:3234:2:en:1", "en", string.Empty, "Sampleland - 2x05 - Episode Title.WEB.GRP.HI", false, true),
            new SubtitleHit(SubtitleProvider.TvSubtitles, "seasonpack:3234:2:en:2", "en", string.Empty, "Sampleland - 2x06 - Titel.WEB.GRP.HI", false, true),
        };
        var getrennt = Jellyfin.Plugin.SubScribe.Pipeline.DownloadPipeline.SplitByHearingImpaired(
            gemischt, SubtitleProvider.TvSubtitles, "en", 2, 5);
        Check("F-M663: ein regulaerer Treffer geht auf die regulaere Seite, ein HI-Treffer auf die HI-Seite — nie beide",
            getrennt.Regular.Count == 1 && getrennt.HearingImpaired.Count == 2
            && !getrennt.Regular[0].HearingImpaired
            && getrennt.Regular[0].Url == "seasonpack:3234:2:en:0"
            && getrennt.HearingImpaired.All(c => c.HearingImpaired)
            && getrennt.Regular.Concat(getrennt.HearingImpaired).All(c => c.Provider == SubtitleProvider.TvSubtitles)
            && getrennt.Regular.Concat(getrennt.HearingImpaired).All(c => c.Language == "EN" && c.Season == 2 && c.Episode == 5),
            "regular=" + string.Join(",", getrennt.Regular.Select(c => c.Url))
            + " hi=" + string.Join(",", getrennt.HearingImpaired.Select(c => c.Url)));
        Check("F-M663: der Griff eines ZIP-Treffers ist kein Subtitle-id und wird auch nicht dafuer gehalten",
            TvSubtitlesDownloadEndpoint.TryParseSeasonPackId("seasonpack:3234:2:en:7", out int pShow, out int pSeason, out string pLang, out int pIndex)
            && pShow == 3234 && pSeason == 2 && pLang == "en" && pIndex == 7
            && !TvSubtitlesDownloadEndpoint.TryParseSeasonPackId("170678", out _, out _, out _, out _)
            && !TvSubtitlesDownloadEndpoint.TryParseSeasonPackId("seasonpack:3234:2:en", out _, out _, out _, out _)
            && !TvSubtitlesDownloadEndpoint.TryParseSeasonPackId(null, out _, out _, out _, out _));

        // ---- 5c. F-M664: die Naht im Lauf (Quelltext-Grenzen, wie die uebrigen F-M5xx-Pruefungen) --
        var pipeline664 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check("F-M664: die HI-Treffer werden JE ENGINE gefuehrt, nicht in einem gemeinsamen Topf",
            pipeline664.Contains("var hiProQuelle = new Dictionary<Configuration.SubtitleProvider, List<SubtitleCandidate>>();", StringComparison.Ordinal)
            && pipeline664.Contains("hiProQuelle[MainSearchSource] = ergebnis.HiCandidates;", StringComparison.Ordinal)
            && pipeline664.Contains("hiProQuelle[quelle] = hiKorb;", StringComparison.Ordinal)
            && !pipeline664.Contains("fremdHiProSprache", StringComparison.Ordinal)
            && !pipeline664.Contains("hiCandidates.AddRange(liste);", StringComparison.Ordinal)
            && !pipeline664.Contains("hiCandidates = ergebnis.HiCandidates;", StringComparison.Ordinal),
            "ein Korb je Quelle, kein Zusammenkippen in den SubDL-Pool");
        Check("F-M664: die HI-Seite wird Engine fuer Engine in Frage-Reihenfolge gelesen",
            pipeline664.Contains("var hiCandidates = HiPoolForLanguage(quellenReihenfolge, hiProQuelle, lang);", StringComparison.Ordinal)
            && pipeline664.Contains("public static List<SubtitleCandidate> HiPoolForLanguage(", StringComparison.Ordinal)
            && pipeline664.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal));
        Check("F-M698: die Reihenfolge der ENGINES kommt aus der gespeicherten Liste",
            pipeline664.Contains("Api.Endpoints.SourceOrder.Sequence(_config, isSeries)", StringComparison.Ordinal)
            && pipeline664.Contains("var quellenReihenfolge = DownloadQueryOrder(isSeries);", StringComparison.Ordinal)
            && pipeline664.IndexOf("var quellenReihenfolge = DownloadQueryOrder(isSeries);", StringComparison.Ordinal)
               < pipeline664.IndexOf("var hiCandidates = HiPoolForLanguage(quellenReihenfolge, hiProQuelle, lang);", StringComparison.Ordinal),
            "die Fragen-Reihenfolge kommt aus der GUI, die HI-Seite folgt ihr");
        Check("F-M663: die HI-Fassung wird bei der QUELLE des Kandidaten geholt, nicht ueber SubDLs Archiv",
            pipeline664.Contains("hi.Provider == MainSearchSource", StringComparison.Ordinal)
            && pipeline664.Split(": await FetchForeignHitAsync(hi, ct)").Length - 1 == 2
            && pipeline664.Contains("FetchForeignHitAsync(SubtitleCandidate hit, CancellationToken ct)", StringComparison.Ordinal)
            && !pipeline664.Contains("DownloadOutcome hiDl = await FetchArchiveAsync", StringComparison.Ordinal),
            "SubDL-Zweig behaelt sein Archiv, der fremde Zweig geht ueber den Endpunkt");
        Check("F-M663: die Antwort einer Quelle wird in zwei Seiten geteilt, der HI-Teil geht in ihren Korb",
            pipeline664.Contains("SplitByHearingImpaired(", StringComparison.Ordinal)
            && pipeline664.Contains("hiZiel.AddRange(hi);", StringComparison.Ordinal));

        // Die reine Funktion ohne Lauf: zwei Engines, beide mit eigenen HI-Treffern. Geprueft wird, dass
        // die Ausgabe der Frage-Reihenfolge folgt, die Sprache filtert — und dass KEIN Korb in den
        // anderen wandert (jeder Kandidat traegt die Quelle seines eigenen Korbs).
        var tvstHi = new List<SubtitleCandidate>
        {
            new() { Provider = SubtitleProvider.TvSubtitles, Language = "DE", Url = "seasonpack:3234:2:de:3", ReleaseName = "Sampleland - 2x05 - de.HI", HearingImpaired = true },
        };
        var subdlHi = new List<SubtitleCandidate>
        {
            new() { Provider = SubtitleProvider.Subdl, Language = "DE", Url = "170678", ReleaseName = "Sampleland.S02E05.GERMAN.HI", HearingImpaired = true },
            new() { Provider = SubtitleProvider.Subdl, Language = "EN", Url = "170679", ReleaseName = "Sampleland.S02E05.ENGLISH.HI", HearingImpaired = true },
        };
        var koerbe = new Dictionary<SubtitleProvider, List<SubtitleCandidate>>
        {
            [SubtitleProvider.TvSubtitles] = tvstHi,
            [SubtitleProvider.Subdl] = subdlHi,
        };
        var tvstZuerst = Jellyfin.Plugin.SubScribe.Pipeline.DownloadPipeline.HiPoolForLanguage(
            new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl }, koerbe, "DE");
        var subdlZuerst = Jellyfin.Plugin.SubScribe.Pipeline.DownloadPipeline.HiPoolForLanguage(
            new[] { SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles }, koerbe, "DE");
        Check("F-M664: die HI-Seite folgt der GUI-Reihenfolge der Engines",
            tvstZuerst.Select(c => c.Provider).SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && subdlZuerst.Select(c => c.Provider).SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles }),
            "tvst-first=" + string.Join(">", tvstZuerst.Select(c => c.Provider))
            + " subdl-first=" + string.Join(">", subdlZuerst.Select(c => c.Provider)));
        Check("F-M664: jede Engine bringt nur ihre eigenen HI-Treffer mit",
            tvstZuerst.Count == 2 && subdlZuerst.Count == 2
            && tvstZuerst.Where(c => c.Provider == SubtitleProvider.TvSubtitles).All(c => c.Url!.StartsWith("seasonpack:", StringComparison.Ordinal))
            && tvstZuerst.Where(c => c.Provider == SubtitleProvider.Subdl).All(c => !c.Url!.StartsWith("seasonpack:", StringComparison.Ordinal))
            && tvstZuerst.All(c => c.Language == "DE")
            && subdlZuerst.All(c => c.Language == "DE"),
            "tvst=" + string.Join(",", tvstHi.Select(c => c.Url)) + " subdl=" + string.Join(",", subdlHi.Select(c => c.Url)));
        Check("F-M664: eine Engine, die nicht gefragt wurde, steht auch nicht auf der HI-Seite",
            Jellyfin.Plugin.SubScribe.Pipeline.DownloadPipeline.HiPoolForLanguage(
                new[] { SubtitleProvider.Subdl }, koerbe, "DE").All(c => c.Provider == SubtitleProvider.Subdl)
            && Jellyfin.Plugin.SubScribe.Pipeline.DownloadPipeline.HiPoolForLanguage(
                Array.Empty<SubtitleProvider>(), koerbe, "DE").Count == 0
            && Jellyfin.Plugin.SubScribe.Pipeline.DownloadPipeline.HiPoolForLanguage(
                new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl }, koerbe, "FR").Count == 0);

        var tvstSource664 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/TvSubtitlesEndpoints.cs"));
        Check("F-M664: in tvst steht die Staffel-ZIP vor dem Einzeleintrag der Folgenseite",
            tvstSource664.Contains("SeasonPackAsync(", StringComparison.Ordinal)
            && tvstSource664.IndexOf("SeasonPackAsync(", StringComparison.Ordinal)
               < tvstSource664.IndexOf("EpisodePagesAsync(", StringComparison.Ordinal)
            && tvstSource664.Contains("if (!regulaerGedeckt || !hiGedeckt)", StringComparison.Ordinal),
            "Staffel-ZIP vor der Folgenliste");
        Check("F-M664: die HI-Treffer von tvst gehen erst NACH beiden regulaeren Stufen weiter",
            tvstSource664.Contains("var hiTreffer = new List<SubtitleHit>();", StringComparison.Ordinal)
            && tvstSource664.Contains("hiTreffer.Add(zipTreffer);", StringComparison.Ordinal)
            && tvstSource664.Contains("hiTreffer.Add(treffer);", StringComparison.Ordinal)
            && tvstSource664.IndexOf("hits.AddRange(hiTreffer);", StringComparison.Ordinal)
               > tvstSource664.IndexOf("hiTreffer.Add(treffer);", StringComparison.Ordinal)
            && tvstSource664.IndexOf("hits.AddRange(hiTreffer);", StringComparison.Ordinal)
               > tvstSource664.LastIndexOf("EpisodePagesAsync(", StringComparison.Ordinal),
            "Stufe 3 wird am Ende angehaengt, nicht zwischen den Stufen gesammelt");

        // ---- 6. the tvsubtitles upload endpoint: dry run, and refusals -------------------------
        var tvUpload = new TvSubtitlesUploadEndpoint(tvClient, dryRun: true);
        var ticket = new UploadTicket(
            "The Sample", "tt1442464", "1422", 2, 24, "de", "The.Sample.S02E24.de.srt",
            Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhallo\n"), "HDTV", "GRP", false, false, false);
        var dry = await tvUpload.SendAsync(ticket).ConfigureAwait(false);
        Check("a dry run reports a built body, sends nothing, and says it was a dry run",
            dry.Ok && dry.DryRun && dry.Bytes > ticket.Data.Length && dry.Provider == SubtitleProvider.TvSubtitles,
            "bytes=" + dry.Bytes);
        // ---- 6b. der tvst-Staffel-Pack (F-M641/F-M642) -----------------------------------------
        // Die Seite hat einen Staffel-Slot (episode=0) und nimmt dort EIN Zip. Der Endpunkt liest den
        // vorhandenen Pack-Schalter der GUI nicht selbst — er bekommt ihn uebergeben wie den
        // Trockenlauf — und lehnt ab, wenn er aus ist oder die Datei kein Zip ist.
        byte[] packZip;
        using (var puffer = new MemoryStream())
        {
            using (var archiv = new ZipArchive(puffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var folge in new[] { 1, 2 })
                {
                    var eintrag = archiv.CreateEntry("The Sample S02E0" + folge + ".de.srt");
                    using var strom = await eintrag.OpenAsync().ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhallo\n");
                    await strom.WriteAsync(bytes).ConfigureAwait(false);
                }
            }

            packZip = puffer.ToArray();
        }

        var packTicket = ticket with
        {
            SeasonPack = true,
            FileName = "The.Sample.S02.GERMAN.1080p.WEB-GRP.zip",
            Data = packZip,
            Release = "The.Sample.S02.GERMAN.1080p.WEB-GRP",
        };
        // F-M765: der Endpunkt kennt keinen Pack-Schalter mehr — wer packen darf, entscheidet die Quelle
        // (PackSlot). Frueher lehnte er hier mit einem Grund ab, den es seit F-M719 nicht mehr gibt.
        Check("the upload endpoint carries no pack switch of its own any more (F-M765)",
            typeof(TvSubtitlesUploadEndpoint).GetConstructors()
                .All(c => c.GetParameters().All(p => !string.Equals(p.Name, "seasonPacks", StringComparison.Ordinal))),
            "ctor parameters: " + string.Join(", ", typeof(TvSubtitlesUploadEndpoint).GetConstructors()
                .SelectMany(c => c.GetParameters()).Select(p => p.Name)));

        var mitSchalter = new TvSubtitlesUploadEndpoint(tvClient, dryRun: true);
        var packTrocken = await mitSchalter.SendAsync(packTicket).ConfigureAwait(false);
        Check("with the switch on a pack is built (zip body) and sends nothing in a dry run (F-M641)",
            packTrocken.Ok && packTrocken.DryRun && packTrocken.Bytes > packZip.Length,
            "ok=" + packTrocken.Ok + " bytes=" + packTrocken.Bytes + " reason=" + (packTrocken.Reason ?? "-"));
        var keinZip = await mitSchalter
            .SendAsync(packTicket with { FileName = "folge.srt", Data = ticket.Data }).ConfigureAwait(false);
        Check("a pack that is not a .zip is refused before any request (F-M641)",
            !keinZip.Ok && keinZip.Reason!.Contains(".zip", StringComparison.Ordinal), keinZip.Reason);
        var packOhneNamen = await mitSchalter
            .SendAsync(packTicket with { Release = null }).ConfigureAwait(false);
        Check("a pack without its name in release is refused — the page would have no name for it (F-M641)",
            !packOhneNamen.Ok && packOhneNamen.Reason!.Contains("pack name", StringComparison.Ordinal),
            packOhneNamen.Reason);

        var sprache = await tvUpload.SendAsync(ticket with { Language = "xx" }).ConfigureAwait(false);
        Check("an unknown language is refused before any request", !sprache.Ok && sprache.Reason!.Contains("xx", StringComparison.Ordinal));
        var ohneShow = await tvUpload.SendAsync(ticket with { ShowName = null }).ConfigureAwait(false);
        Check("without a show name nothing can be resolved", !ohneShow.Ok && ohneShow.Reason!.Contains("show name", StringComparison.Ordinal));
        var unbekannt = await tvUpload.SendAsync(ticket with { ShowName = "Nope-Nope" }).ConfigureAwait(false);
        Check("a show the site does not carry is refused with its name", !unbekannt.Ok
            && unbekannt.Reason!.Contains("Nope-Nope", StringComparison.Ordinal));

        // ---- 7. the SubDL upload endpoint refuses what it cannot do ---------------------------
        var subdlUpload = new ScribeUploadEndpoint(subdlClient);
        var keinPack = await subdlUpload.SendAsync(ticket with { SeasonPack = true }).ConfigureAwait(false);
        Check("SubDL refuses a season pack here — packs are the pipeline's job (F-M372)",
            !keinPack.Ok && keinPack.Reason!.Contains("season packs", StringComparison.Ordinal));
        var ohneImdb = await subdlUpload.SendAsync(ticket with { ImdbId = null }).ConfigureAwait(false);
        Check("SubDL refuses without an IMDb id", !ohneImdb.Ok && ohneImdb.Reason == "no-imdb");
        var cp1250 = await subdlUpload.SendAsync(
            ticket with { Data = new byte[] { 0x41, 0xF5, 0x42 } }).ConfigureAwait(false);
        Check("SubDL refuses bytes that are not UTF-8 instead of transcoding them",
            !cp1250.Ok && cp1250.Reason!.Contains("not UTF-8", StringComparison.Ordinal));
        var angemeldet = await subdlUpload.SendAsync(ticket).ConfigureAwait(false);
        Check("without a session the refusal is readable, not an exception",
            !angemeldet.Ok && angemeldet.Reason!.Contains("Not logged in", StringComparison.Ordinal));

        // ---- 8. die Einbindung in den Lauf: Quelltext-Bindung (F-M485) -----------------------
        var root2 = args.Length > 0 ? args[0] : ScribeRepoPaths.Root;
        var downloadSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        var uploadSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var orderSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/SourceOrder.cs"));
        Check("the download pipeline asks EVERY enabled source before a language is given up (F-M485/F-M504)",
            // F-M504: die Quellen werden nicht mehr in der Sprachschleife gefragt, sondern VOR dem
            // Ausstieg bei leerer Hauptantwort — und die Schleife laeuft ueber alle Quellen in der
            // eingestellten Reihenfolge, nicht ueber zwei. Die Absicht der Pruefung bleibt: kein
            // Ausstieg, bevor die Quelle gefragt wurde.
            downloadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            // F-M507: der Aufruf traegt zusaetzlich TMDb-Id, Medienpfad und Medien-Hash — die dritte
            // Quelle loest ihren Film ueber TMDb auf und legt die Nummer am Medium ab.
            // F-M663: dazu kommt der Korb fuer die HI-Treffer (`hiKorb`) — die Signatur endet deshalb
            // auf `mediaHash, hiKorb, ct`, und die Absicht dieser Pruefung bleibt dieselbe: die Quelle
            // wird gefragt, BEVOR der Ausstieg bei leerer Antwort greift.
            && downloadSource.Contains(
                "ForeignCandidatesAsync(quelle, title, isSeries, season, episode, lang, tmdbId, mediaPath, mediaHash, hiKorb, ct)",
                StringComparison.Ordinal)
            && downloadSource.Contains("if (candidates.Count == 0 && !fremdProSprache.Values.Any(", StringComparison.Ordinal)
            && downloadSource.IndexOf("ForeignCandidatesAsync(quelle, title", StringComparison.Ordinal)
               < downloadSource.IndexOf("no candidates for [{Langs}]", StringComparison.Ordinal),
            "the sources are asked before the empty exit");
        // F-M686: der Holweg einer Zusatzquelle ist ihre ENGINE geworden — die Aussage bleibt dieselbe
        // (ein Kandidat wird bei SEINER Quelle geholt, nicht über SubDLs Routen), und sie wird genauso
        // am Quelltext gemessen: der Zweig fragt die Engine der Quelle, die der Kandidat trägt.
        Check("a candidate is fetched at its own source, not over SubDL's routes (F-M686)",
            downloadSource.Contains("if (cand.Provider != MainSearchSource)", StringComparison.Ordinal)
            && downloadSource.Contains(
                "Sources.ISourceEngine? fremdEngine = _sources.ByKey(Api.Endpoints.Provenance.Name(cand.Provider));",
                StringComparison.Ordinal)
            && downloadSource.Contains(
                "OutcomeOf(fremd, Api.Endpoints.Provenance.Name(cand.Provider))", StringComparison.Ordinal)
            && !downloadSource.Contains("endpoint.FetchAsync(", StringComparison.Ordinal));
        Check("the upload pipeline sends to the family address through the same gate, and its switch is asked (F-M485/F-M495/F-M509)",
            uploadSource.Contains("SourceAllowed(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal)
            && uploadSource.Contains("Configuration.SubtitleProvider.TvSubtitles => SendToTvSubtitlesAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("Configuration.SubtitleProvider.MovieSubtitles => SendToMovieSubtitlesAsync(", StringComparison.Ordinal)
            // F-M509: die Bauform „alles ausser SubDL" ist weg — sie haette einen Film zur Serien-Adresse
            // geschickt und keiner dritten Quelle je einen eigenen Weg gelassen.
            && !uploadSource.Contains("SendToSecondSourceAsync(", StringComparison.Ordinal));
        Check("the SubDL order carries the same fields as the call before it — quality, framerate and HI included",
            // F-M687: der Aufruf steht nicht mehr in der Versandtabelle, sondern im AUFTRAG — die Angaben
            // sind dieselben, und die Engine reicht sie unverändert an denselben Client.
            uploadSource.Contains("Configuration.SubtitleProvider.Subdl => SendAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("Quality = quality,", StringComparison.Ordinal)
            && uploadSource.Contains("Framerate = framerate ?? string.Empty,", StringComparison.Ordinal)
            && uploadSource.Contains("Encoding.UTF8.GetBytes(srtContent)", StringComparison.Ordinal)
            && downloadSource.Contains("SubDL", StringComparison.Ordinal));
        Check("an additional source never breaks the run — its errors are warnings",
            downloadSource.Contains("[{Source}-D] search failed for", StringComparison.Ordinal));

        // ---- 9. Herkunft und Kreuzkontamination (F-M486/F-M487) -------------------------------
        Check("the store value names the source, and parses back",
            Provenance.Name(SubtitleProvider.Subdl) == "subdl"
            && Provenance.Name(SubtitleProvider.TvSubtitles) == "tvsubtitles"
            && Provenance.Parse("tvsubtitles") == SubtitleProvider.TvSubtitles
            && Provenance.Parse("SubDL") == SubtitleProvider.Subdl
            && Provenance.Parse(null) is null
            && Provenance.Parse("  ") is null);
        // F-M500 (Operator-Anweisung 15.11.2026, verschaerft): eine BEKANNTE Herkunft geht NIRGENDS hoch —
        // nicht zurueck zur eigenen Quelle (Reup) und nicht zur anderen (Kreuzkontamination). Vorher galt
        // "bekannt = nur zur eigenen Quelle"; das ist aufgehoben.
        Check("a tvsubtitles find goes nowhere — not to SubDL and not back to tvsubtitles",
            !Provenance.MayUpload("tvsubtitles")
            && !Provenance.MayUpload("tvsubtitles")
            && !Provenance.MayUpload("subdl")
            && !Provenance.MayUpload("subdl"));
        Check("the origin is named in the reason the run logs",
            Provenance.OriginBlockedReason("subdl") is { } subdlSatz
            && subdlSatz.Contains("origin:subdl", StringComparison.Ordinal)
            && subdlSatz.Contains("never uploaded", StringComparison.Ordinal)
            && Provenance.OriginBlockedReason("tvsubtitles") is { } tvSatz
            && tvSatz.Contains("origin:tvsubtitles", StringComparison.Ordinal)
            && Provenance.OriginBlockedReason(null) is null,
            (Provenance.OriginBlockedReason("subdl") ?? "-") + " | " + (Provenance.OriginBlockedReason("tvsubtitles") ?? "-"));
        Check("unknown provenance stays allowed everywhere — a hand copy or an embedded track",
            Provenance.MayUpload(null)
            && Provenance.MayUpload(null)
            && Provenance.MayUpload(string.Empty));
        Check("the refusal sentence names both sides",
            Provenance.CrossSourceReason("tvsubtitles", SubtitleProvider.Subdl)
                .Contains("tvsubtitles", StringComparison.Ordinal)
            && Provenance.CrossSourceReason("tvsubtitles", SubtitleProvider.Subdl)
                .Contains("subdl", StringComparison.Ordinal));
        Check("the download records the origin and the source's own id in the store",
            downloadSource.Contains("Api.Endpoints.Provenance.Name(cand.Provider)", StringComparison.Ordinal)
            && downloadSource.Contains("cand.Provider == Configuration.SubtitleProvider.Subdl ? cand.SubdlId : cand.Url", StringComparison.Ordinal));
        Check("the upload reads the origin from the store before it picks an endpoint",
            uploadSource.Contains("Registry.GetOrigin(contentHashEarly)", StringComparison.Ordinal)
            && uploadSource.Contains("UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Contains("herkunft,", StringComparison.Ordinal));
        Check("the store schema carries the origin and a place for the foreign id",
            // F-M672: die fremde Id steht seit Schema 23 in `reference` (DownloadSourceId), nicht mehr
            // als Spalte an der Zeile — die Herkunft (der Quellen-NAME) bleibt, wo sie war.
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"))
                .Contains("public string? Origin { get; set; }", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"))
                .Contains("public string? DownloadSourceId { get; set; }", StringComparison.Ordinal)
            // F-M671: die Spalte steht nicht mehr in einem ALTER-Nachzug, sondern in der DDL der
            // Entity-Tabelle — ein Bestand wird beim Öffnen vollständig übernommen, nicht Spalte für
            // Spalte nachgezogen.
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"))
                .Contains("+ \"Origin TEXT NULL, \"", StringComparison.Ordinal)
            // F-M501: der Marker steht auf 13 (media.TvstShowId kam dazu); die Pruefung fragt den
            // AKTUELLEN Stand, weil ein stehengebliebener Marker die Migration nicht mehr anzeigt.
            // F-M507: er steht inzwischen auf 14 (die Spalten der dritten Quelle).
            // F-M669: 20 — die Spur-Position gehoert in den Schluessel der Gridfilm.
            // F-M670: 21 — die Alt-Tabelle `source_marks` und die Id-Spalte `SubdlId` sind entfallen.
            // F-M671: 22 — die eigenen Kennungen der vier Ebenen.
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"))
                .Contains("CurrentSchemaVersion = 24", StringComparison.Ordinal));

        // ---- 10. eigene Kennzeichen je Quelle und Richtung (F-M488) ---------------------------
        var entities = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"));
        var dbc = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"));
        Check("four own fields exist, one per source and direction",
            // F-M668: die vier Spaltenpaare sind EINE Gridfilm-Zeile je Quelle und Richtung geworden —
            // `SourceId` (Abruf) und `UploadId` (Einsendung) nebeneinander, die Quelle als Wert.
            entities.Contains("public string? DownloadSourceId { get; set; }", StringComparison.Ordinal)
            && entities.Contains("public string? UploadedId { get; set; }", StringComparison.Ordinal)
            && !entities.Contains("public string? SubdlDownloadId { get; set; }", StringComparison.Ordinal)
            && !entities.Contains("public string? TvSubtitlesUploadId { get; set; }", StringComparison.Ordinal));
        Check("schema 11 adds them to sidecars AND embeds",
            // F-M668: Statt neun Spalten je Tabelle trägt die Gridfilm die Quelle im WERT — Schema 19
            // zieht die zwei Felder der Gridfilm nach und legt die Altspalten nieder.
            // F-M671: der Marker steht auf 22.
            dbc.Contains("CurrentSchemaVersion = 24", StringComparison.Ordinal)
            && dbc.Contains("+ \"UploadedId TEXT NULL, \"", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SourceStore.cs"))
                .Contains("public static void EnsureBadAppleColumns(", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SourceStore.cs"))
                .Contains("ALTER TABLE badapple ADD COLUMN ReportState", StringComparison.Ordinal));
        Check("the download writes the field of its own source",
            downloadSource.Contains("subdlDownloadId: cand.Provider == Configuration.SubtitleProvider.Subdl ? cand.SubdlId : null", StringComparison.Ordinal)
            && downloadSource.Contains("tvSubtitlesDownloadId: cand.Provider == Configuration.SubtitleProvider.TvSubtitles ? cand.Url : null", StringComparison.Ordinal));
        Check("the upload writes SubDL's id into SubDL's field",
            // F-M670: die zweite Schreibstelle (`subdlId: uploadId?.ToString(…)` in die Spalte
            // `embeds.SubdlId`) ist entfallen — die Einsende-Id steht ausschliesslich an der Marke.
            uploadSource.Contains("subdlUploadId: subdlMarke.Id", StringComparison.Ordinal)
            && !uploadSource.Contains("subdlId: uploadId?.ToString(System.Globalization.CultureInfo.InvariantCulture)", StringComparison.Ordinal));
        Check("each family address carries its upload id in its OWN field (F-M508)",
            uploadSource.Contains("tvSubtitlesUploadId: tvstMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("moviesubtitlesUploadId: msubMarke.Id", StringComparison.Ordinal)
            // die Marke kommt aus EINER Stelle, die die eigene Id der Quelle nimmt oder eine vergibt
            && uploadSource.Contains("knownId ?? ergebnis.SourceId", StringComparison.Ordinal));
        var ownId = Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "abc123", "The.Sample.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc));
        Check("a missing id is replaced by our own, unmistakably prefixed",
            ownId.StartsWith("own:tvsubtitles:20261115T143012Z:", StringComparison.Ordinal)
            && Provenance.IsOwnUploadId(ownId)
            && !Provenance.IsOwnUploadId("170678"));
        Check("the minted id is reproducible for the same input and differs for another subtitle",
            ownId == Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "abc123", "The.Sample.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc))
            && ownId != Provenance.OwnUploadId(SubtitleProvider.TvSubtitles, "def456", "The.Sample.S02E24.720p", new DateTime(2026, 11, 15, 14, 30, 12, DateTimeKind.Utc)));
        Check("only the source that took the upload gets its mark",
            uploadSource.Contains("subdlUploadId: subdlMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("tvSubtitlesUploadId: tvstMarke.Id", StringComparison.Ordinal)
            && uploadSource.Contains("moviesubtitlesUploadId: msubMarke.Id", StringComparison.Ordinal)
            // MarkOf gibt (null, null) fuer eine Quelle, die nicht genommen hat — keine Marke ohne Upload.
            // F-M603: ein ANGENOMMENER Versuch ohne Kennung ist die Ausnahme (SKIP mit Beleg).
            && uploadSource.Contains("if (!ergebnisse.TryGetValue(quelle, out var ergebnis) || (!ergebnis.Ok && !ergebnis.SourceAccepted))", StringComparison.Ordinal)
            && !uploadSource.Contains("subdlUploadId: vonZweiter", StringComparison.Ordinal));
        Check("the id is read back from the episode page, never invented",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs"))
                .Contains("public async Task<string?> FindUploadedIdAsync(", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/UploadResult.cs"))
                .Contains("public string? SourceId { get; init; }", StringComparison.Ordinal));

        // ---- 11. Reup und Kreuzkontamination (F-M489) -----------------------------------------
        // F-M656: keine Vorgabewerte fuer die dritte Quelle. Mit `movieSubtitlesSwitch = false` liess ein
        // vergessener Aufruf die Filmquelle still aus — im fruehen Tor war genau das der Fall.
        var gateSrc = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/UploadGate.cs"));
        Check("F-M656: every source of the gate is required, the film source has no default",
            !gateSrc.Contains("bool movieSubtitlesSwitch = false", StringComparison.Ordinal)
            && !gateSrc.Contains("bool movieSubtitlesAlready = false", StringComparison.Ordinal)
            && gateSrc.Contains("bool movieSubtitlesSwitch, bool movieSubtitlesAlready)", StringComparison.Ordinal));

        Check("F-M656: the early gate asks the film source too — it used to answer 'nothing to do' for a film",
            uploadSource.Contains("var gateFrueh = Api.Endpoints.UploadGate.Decide(", StringComparison.Ordinal)
            // F-M721: die Filmquelle wird nicht mehr von Hand angehaengt — die Ziele kommen aus der
            // Registry, und welche Quelle eine Medienart fuehrt, sagt die Engine selbst (F-M696).
            && uploadSource.Contains("Api.Endpoints.UploadGate.TargetsFrom(", StringComparison.Ordinal)
            && !uploadSource.Contains("SourceAllowed(Configuration.SubtitleProvider.MovieSubtitles) && !isSeries", StringComparison.Ordinal));

        Check("F-M656: the pack key and the buffered pack upload name their source too",
            !ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"))
                .Contains("string source = PackEntity.SubdlSource", StringComparison.Ordinal)
            && !ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"))
                .Contains("string source = PackEntity.SubdlSource", StringComparison.Ordinal));

        var g = UploadGate.Decide(subdlSwitch: true, tvSubtitlesSwitch: true, origin: null, subdlAlready: false, tvSubtitlesAlready: false,
            movieSubtitlesSwitch: false, movieSubtitlesAlready: false);
        Check("nothing uploaded yet and both switches on: both sources may run", g.Subdl && g.TvSubtitles && g.SkipReason is null);
        Check("no reup on tvsubtitles: that source already has it",
            UploadGate.Decide(true, true, null, false, true, false, false) is { Subdl: true, TvSubtitles: false, SkipReason: null });
        Check("no reup on SubDL: that source already has it",
            UploadGate.Decide(true, true, null, true, false, false, false) is { Subdl: false, TvSubtitles: true, SkipReason: null });
        Check("both already have it: nothing runs, and the reason names both",
            UploadGate.Decide(true, true, null, true, true, false, false) is { Subdl: false, TvSubtitles: false, SkipReason: "already-uploaded:subdl+tvsubtitles" });
        Check("a SubDL upload does not block tvsubtitles and a tvst upload does not block SubDL",
            UploadGate.Decide(true, true, origin: null, true, false, false, false) is { Subdl: false, TvSubtitles: true, SkipReason: null }
            && UploadGate.Decide(true, true, origin: null, false, true, false, false) is { Subdl: true, TvSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, origin: null, true, true, false, false).SkipReason == "already-uploaded:subdl+tvsubtitles");
        Check("a known origin is refused at BOTH sources, with the origin named as the reason (F-M500)",
            UploadGate.Decide(true, true, "tvsubtitles", false, false, false, false) is { Subdl: false, TvSubtitles: false } tvTor
            && tvTor.SkipReason == "origin:tvsubtitles — downloaded there, never uploaded"
            && UploadGate.Decide(true, true, "subdl", false, false, false, false) is { Subdl: false, TvSubtitles: false } sdTor
            && sdTor.SkipReason == "origin:subdl — downloaded there, never uploaded");
        Check("switches off mean nothing runs",
            UploadGate.Decide(false, false, null, false, false, false, false).SkipReason == "no-upload-source");
        // F-M508: die Film-Adresse der Familie steht im SELBEN Tor — eigener Schalter, eigene Marke,
        // eigene Antwort. Sie ist die dritte Adresse im Tor, aber keine dritte Quelle in der Reihenfolge.
        Check("the film address is decided by the same gate, with its own switch and its own mark (F-M508)",
            UploadGate.Decide(false, false, null, false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: false)
                is { MovieSubtitles: true, Subdl: false, TvSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, null, false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: true)
                is { MovieSubtitles: false, SkipReason: null }
            && UploadGate.Decide(true, true, null, true, true, movieSubtitlesSwitch: true, movieSubtitlesAlready: true).SkipReason
                == "already-uploaded:subdl+tvsubtitles+moviesubtitles"
            && UploadGate.Decide(true, true, null, false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: false).AllowedCount == 3
            && !UploadGate.Decide(true, true, "moviesubtitles", false, false, movieSubtitlesSwitch: true, movieSubtitlesAlready: false).MovieSubtitles,
            "F-M508: eigenes Feld, eigene Frage");
        Check("the pipeline asks per source, through the one gate — twice, never inline",
            uploadSource.Contains("UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Split("UploadGate.Decide(").Length - 1 >= 2
            // F-M669/F-M721: das Tor fragt die Marke DIESER POSITION, und zwar ueber den einen Helfer
            // mit dem Quellenschluessel — kein Quellenname mehr im Aufruf.
            && uploadSource.Contains("alreadyThere: key => AlreadyAt(", StringComparison.Ordinal)
            && !uploadSource.Contains("Registry.IsUploaded(mediaHash, cand.Lang", StringComparison.Ordinal));

        // ---- 12. "hochgeladen" je Quelle (F-M490) ---------------------------------------------
        var registrySource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        Check("each source has its own uploaded timestamp, sidecars and embeds alike",
            // F-M668: „hochgeladen je Quelle" ist das Feld `ClosedAt` der Gridfilm-Zeile — je Quelle eine
            // Zeile, statt einer Spalte je Quelle an jeder Untertitel-Zeile.
            entities.Contains("public DateTime? UploadedAt { get; set; }", StringComparison.Ordinal)
            && !entities.Contains("public DateTime? SubdlUploadedAt { get; set; }", StringComparison.Ordinal)
            && dbc.Contains("CurrentSchemaVersion = 24", StringComparison.Ordinal)
            && dbc.Contains("\"UploadedAt TEXT NULL, \"", StringComparison.Ordinal));
        Check("the timestamp is set for the source that took the upload, never for another",
            uploadSource.Contains("subdlUploadedAt: subdlMarke.At", StringComparison.Ordinal)
            && uploadSource.Contains("tvSubtitlesUploadedAt: tvstMarke.At", StringComparison.Ordinal)
            && uploadSource.Contains("moviesubtitlesUploadedAt: msubMarke.At", StringComparison.Ordinal)
            // EIN Zeitpunkt fuer Id und Stempel — sonst traegt die Marke eine andere Sekunde als der Upload
            && uploadSource.Contains("var jetzt = DateTime.UtcNow;", StringComparison.Ordinal));
        Check("\"uploaded to this source\" is read from its own timestamp first",
            // F-M668: eine Frage an die Gridfilm, je Quelle eine Zeile — `IsClosed` = Zeitpunkt oder
            // Einsende-Id. Kein Spaltenpaar mehr, das die Antwort der anderen Quelle vortäuschen könnte.
            registrySource.Contains("SourceStore.TrackMark(_db, mediaHash, Quelle(provider), language, hearingImpaired).IsClosed", StringComparison.Ordinal)
            && !registrySource.Contains("x.TvSubtitlesUploadedAt != null", StringComparison.Ordinal));
        Check("a timestamp is never overwritten with nothing",
            // dieselbe Regel, jetzt an der einen Schreibstelle der Gridfilm: was leer hereinkommt, löscht nichts.
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SourceStore.cs"))
                .Contains("if (closedAt.HasValue)", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/SourceStore.cs"))
                .Contains("if (!string.IsNullOrEmpty(uploadId))", StringComparison.Ordinal));

        // ---- 13. Reihenfolge und Erfolgsbegriff (F-M491, F-M494) -----------------------------
        Check("the download order is a setting, and tvsubtitles is first by default (operator 15.11.2026)",
            downloadSource.Contains("DownloadOrder", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"))
                .Contains("DownloadSourceOrder.TvSubtitlesFirst;", StringComparison.Ordinal));
        Check("F-M698: the GUI offers the order on the general tab as a list and saves it",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"))
                .Contains("id=\"SourceOrderValue\"", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"))
                .Contains("config.DownloadOrder =", StringComparison.Ordinal));
        Check("every source is asked and the candidates are MERGED, not replaced (F-M494/F-M504)",
            !downloadSource.Contains("langCands = fremd;", StringComparison.Ordinal)
            && downloadSource.Contains("candidates.AddRange(fremd);", StringComparison.Ordinal)
            && downloadSource.Contains("{Counts}, order {Order}", StringComparison.Ordinal)
            && downloadSource.Contains("langCands.Count(c => c.Provider == quelle)", StringComparison.Ordinal)
            && !downloadSource.Contains("tvZuerst", StringComparison.Ordinal));
        Check("the ranking leads with the source in the query order, pack and score stay behind it (F-M494/F-M504)",
            downloadSource.Contains(".OrderBy(x => QuellenRang(quellenRang, x.Cand.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains(".ThenByDescending(x => x.Pack)", StringComparison.Ordinal));
        Check("a source change restarts the correction hunt instead of ending it (F-M494)",
            downloadSource.Contains("var offeneAndereQuelle = ranked.Skip(attempts).Where(x => x.Cand.Provider != cand.Provider)", StringComparison.Ordinal)
            && downloadSource.Contains("correctionHuntAbandoned = false;", StringComparison.Ordinal)
            && downloadSource.Contains("refusalsThisRun = 0;", StringComparison.Ordinal));
        Check("the fetch path asks the candidate's own source switch, through ONE helper, before the fetch (F-M494)",
            downloadSource.Contains("private bool SourceAllowed(Configuration.SubtitleProvider provider) => _config.IsDownloadEnabled(provider);", StringComparison.Ordinal)
            && downloadSource.Contains("if (!SourceAllowed(cand.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains("if (!SourceAllowed(hi.Provider))", StringComparison.Ordinal)
            && downloadSource.Contains(".Where(SourceAllowed)", StringComparison.Ordinal)
            && downloadSource.Contains("candidate from {Source} skipped: that source is switched off", StringComparison.Ordinal)
            && !downloadSource.Contains("if (_config.IsDownloadEnabled(Configuration.SubtitleProvider.TvSubtitles))", StringComparison.Ordinal));
        // F-M760 (Konzept 26.11.2026): die Zahl der Dateien im Behaelter kommt aus dem Behaelter SELBST
        // und wird von JEDER Quelle an derselben neutralen Stelle gefuehrt — nicht aus einem Dateinamen,
        // nicht aus einer Quellen-Marke. Die Statistik liest nur diese eine Zahl (F-M759).
        var subdlSource760 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Sources/Subdl/SubdlEngine.cs"));
        var tvstSource760 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Sources/Tvst/TvstEngine.cs"));
        var tvstEndpoints760 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/TvSubtitlesEndpoints.cs"));
        Check("the container's file count is read from the container, for every source",
            downloadSource.Contains("cand.ContainerFileCount >= 2", StringComparison.Ordinal)
            && subdlSource760.Contains("ContainerFileCount = c.UnpackFiles.Count", StringComparison.Ordinal)
            && tvstSource760.Contains("ContainerFileCount = h.ContainerFileCount", StringComparison.Ordinal)
            && tvstEndpoints760.Contains("pack.Count", StringComparison.Ordinal)
            && !downloadSource.Contains("OriginalName.EndsWith(\".zip\"", StringComparison.Ordinal));

        // F-M759 (26.11.2026): die Unterscheidung "heruntergeladen, aber nicht erfolgreich" lebt weiter,
        // sie entscheidet jetzt VOR der Zaehlung, ob ein Save ueberhaupt zaehlt (Pack oder Untertitel).
        Check("a kept original is a download but not a success while auto-sync is on",
            downloadSource.Contains("bool erfolg = !_config.QaDownloadAutoSync || alignedForNaming;", StringComparison.Ordinal)
            && downloadSource.Contains("summary.DownloadedWithoutSignificance++;", StringComparison.Ordinal));

        // ---- 14. Quellenabhaengige Vorpruefungen und Herkunft an jeder Ablage (F-M492/F-M493) -----
        var cfgSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        var dispatcherSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/ScribeEventDispatcher.cs"));
        Check("the only demanded credential is the TMDb key — SubDL's pair is a source question (F-M492/F-M497)",
            cfgSource.Contains("if (string.IsNullOrWhiteSpace(TmdbApiKey))", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL API key\")", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL email\")", StringComparison.Ordinal)
            && !cfgSource.Contains("missing.Add(\"SubDL password\")", StringComparison.Ordinal)
            && cfgSource.Contains("public bool IsConfiguredForUpload(SubtitleProvider provider)", StringComparison.Ordinal)
            && cfgSource.Contains("public bool IsConfiguredForDownload(SubtitleProvider provider)", StringComparison.Ordinal));
        Check("a cycle that does not ask SubDL is not held by the SubDL counters",
            dispatcherSource.Contains("if (!config.IsDownloadEnabled(Configuration.SubtitleProvider.Subdl))", StringComparison.Ordinal));
        Check("a spent SubDL quota does not hold a cycle that still has tvsubtitles",
            dispatcherSource.Contains("secondSourceCanRun", StringComparison.Ordinal)
            && dispatcherSource.Contains("this cycle runs on tvsubtitles.net only (F-M492)", StringComparison.Ordinal));
        var markCalls = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"MarkDownloaded\(").Count;
        var originArgs = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"origin: Api\.Endpoints\.Provenance\.Name\(").Count;
        Check($"every MarkDownloaded call carries its origin ({originArgs}/{markCalls})",
            markCalls >= 4 && originArgs == markCalls);
        Check("the run-end line names the downloads that did not pass the significance",
            downloadSource.Contains("{NotSignificant} downloaded without significance", StringComparison.Ordinal));
        var taskSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/ScribeDownloadTask.cs"));
        var offStateSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/WorkerOffState.cs"));
        Check("the download task asks whether ANY source downloads, not whether SubDL does",
            taskSource.Contains("if (!config.AnyDownloadEnabled)", StringComparison.Ordinal)
            && !taskSource.Contains("IsDownloadEnabled(SubtitleProvider.Subdl)", StringComparison.Ordinal));
        var seederSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/ScribeSeeder.cs"));
        Check("the queue fills for the direction, not for SubDL — a spent SubDL allowance does not empty it",
            seederSource.Contains("bool downloadQueueable = config.AnyDownloadEnabled", StringComparison.Ordinal)
            && seederSource.Contains("(downloadAllowed || config.IsDownloadEnabled(SubtitleProvider.TvSubtitles))", StringComparison.Ordinal)
            && seederSource.Contains("config.AnyUploadEnabled", StringComparison.Ordinal)
            && seederSource.Contains("UploadTodoSources(item, mediaPath, config)", StringComparison.Ordinal));
        Check("the off-state of a direction is asked for the direction, not for SubDL",
            offStateSource.Contains("return !config.AnyDownloadEnabled", StringComparison.Ordinal)
            && offStateSource.Contains("return !config.AnyUploadEnabled", StringComparison.Ordinal));

        // ---- 15. Upload: beide Endpunkte hintereinander (F-M495) --------------------------------
        Check("the upload walks the sources of the configured order — one loop, one result per source (F-M495/F-M509)",
            uploadSource.Contains("var quellenReihenfolge = UploadSourceOrder(isSeries);", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.SourceOrder.Sequence(_config, art)", StringComparison.Ordinal)
            && orderSource.Contains("PluginConfiguration.DownloadSourceOrder.SubdlFirst", StringComparison.Ordinal)
            && uploadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            && uploadSource.Contains("var ergebnisse = new Dictionary<Configuration.SubtitleProvider, UploadResult>();", StringComparison.Ordinal)
            && uploadSource.Contains("zustaendig.Add(quelle);", StringComparison.Ordinal)
            && uploadSource.Contains("await Task.WhenAll(laeufe).ConfigureAwait(false);", StringComparison.Ordinal)
            && uploadSource.Contains("bool subdlGenommen = Genommen(Configuration.SubtitleProvider.Subdl);", StringComparison.Ordinal)
            && uploadSource.Contains("bool zweiteGenommen = Genommen(Configuration.SubtitleProvider.TvSubtitles);", StringComparison.Ordinal)
            && uploadSource.Contains("bool dritteGenommen = Genommen(Configuration.SubtitleProvider.MovieSubtitles);", StringComparison.Ordinal)
            && !uploadSource.Contains("var vonZweiter = false;", StringComparison.Ordinal));
        Check("each endpoint's switch is asked right before its start — through ONE helper, skip instead of break (F-M495)",
            // F-M713: derselbe eine Helfer, jetzt mit dem Ausfall-Zusatz.
            uploadSource.Contains("_config.IsUploadEnabled(provider) && !_quellenAus.Contains(provider)", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("source {Source} skipped: that source is switched off, nothing sent", StringComparison.Ordinal)
            && !uploadSource.Contains("_config.IsUploadEnabled(Configuration.SubtitleProvider.Subdl)", StringComparison.Ordinal)
            && !uploadSource.Contains("_config.IsUploadEnabled(Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal));
        Check("the provenance gate still decides PER SOURCE — a refused source is skipped, the chain runs on (F-M495/F-M508)",
            uploadSource.Contains("if (!Api.Endpoints.UploadGate.May(gate, quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.Provenance.OriginBlockedReason(herkunft)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.Provenance.MayUpload(herkunft)", StringComparison.Ordinal)
            && uploadSource.Contains("Api.Endpoints.UploadGate.Decide(", StringComparison.Ordinal));

        // ---- 16. Trockenlauf: eine Zeile je Quelle, keine Zugangsdaten (F-M496) -----------------
        // Der Trockenlauf stand als EIN Ausstieg vor der Kette: eine Zeile fuer zwei Endpunkte, und
        // MissingCredentials verlangte fuer ihn Benutzername und Passwort, obwohl er keinen einzigen
        // Aufruf macht. Beides ist gemessen (jf-test, 15.11.2026) und beides wird hier festgehalten.
        var trockenOhneLogin = Conf(c =>
        {
            c.DryRun = true;
            c.UploadEnabled = true;
            c.SubdlUploadEnabled = true;
            c.TvSubtitlesUploadEnabled = true;
            c.Username = string.Empty;
            c.Password = string.Empty;
        });
        Check("a dry run is not refused for missing SubDL credentials (F-M496)",
            trockenOhneLogin.MissingCredentials().Count == 0
            && uploadSource.Contains("if (!_config.DryRun && !_config.IsConfiguredForUpload(quelle))", StringComparison.Ordinal),
            "missing=" + string.Join(",", trockenOhneLogin.MissingCredentials()));

        var dryLineTvst = uploadSource.IndexOf("DryRunLine(quelle)", StringComparison.Ordinal);
        var dryBlockStart = uploadSource.IndexOf("if (_config.DryRun)", StringComparison.Ordinal);
        var secondSourceCall = uploadSource.IndexOf("await SendToSourceAsync(", StringComparison.Ordinal);
        Check("the tvst branch has its own dry-run guard BEFORE its call — one line per source (F-M496)",
            dryLineTvst > 0 && dryBlockStart > 0 && secondSourceCall > 0
            && dryLineTvst > dryBlockStart
            && dryLineTvst < secondSourceCall
            && uploadSource.Contains("\"[\" + Api.Endpoints.Provenance.Name(quelle) + \"] DRY-RUN would upload {Name} [{Lang}]\"", StringComparison.Ordinal)
            && uploadSource.Contains("foreach (var quelle in UploadSourceOrder(isSeries))", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(quelle))", StringComparison.Ordinal)
            // F-M687: der Endpunkt ist aus der Kette verschwunden — die Quelle wird ueber ihren AUFTRAG
            // angesprochen, und ihr eigener Schalter wird vorher gefragt (F-M495).
            && uploadSource.Contains("Configuration.SubtitleProvider.TvSubtitles => SendToTvSubtitlesAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("if (!SourceAllowed(Configuration.SubtitleProvider.TvSubtitles))", StringComparison.Ordinal),
            "tvst line @" + dryLineTvst + ", dry block @" + dryBlockStart + ", call @" + secondSourceCall);

        // ---- 17. TMDb ist die einzige Pflicht, SubDL faellt ohne Konfiguration aus der Kette (F-M497) --
        var ohneTmdb = Conf(c => { c.TmdbApiKey = string.Empty; });
        var ohneSubdlKonfig = Conf(c =>
        {
            c.UploadEnabled = true;
            c.DownloadEnabled = true;
            c.Username = string.Empty;
            c.Password = string.Empty;
            c.ApiKey = string.Empty;
        });
        Check("missing TMDb blocks the run, missing SubDL configuration does not (F-M497)",
            ohneTmdb.MissingCredentials().Contains("TMDb API key (v3)")
            && ohneSubdlKonfig.MissingCredentials().Count == 0,
            "ohne Tmdb=" + string.Join(",", ohneTmdb.MissingCredentials())
            + " ohne SubDL=" + string.Join(",", ohneSubdlKonfig.MissingCredentials()));

        Check("SubDL without its credentials steps aside instead of blocking — both directions (F-M497)",
            !ohneSubdlKonfig.IsConfiguredForUpload(SubtitleProvider.Subdl)
            && ohneSubdlKonfig.IsConfiguredForUpload(SubtitleProvider.TvSubtitles)
            && !ohneSubdlKonfig.IsConfiguredForDownload(SubtitleProvider.Subdl)
            && ohneSubdlKonfig.IsConfiguredForDownload(SubtitleProvider.TvSubtitles)
            && uploadSource.Contains(
                "\"[Sub Scribe] {Name} [{Lang}] — source {Source} skipped — not configured (required for SubDL only)\"",
                StringComparison.Ordinal)
            && downloadSource.Contains(
                "if (!_config.IsConfiguredForDownload(Configuration.SubtitleProvider.Subdl))", StringComparison.Ordinal)
            && downloadSource.Contains(
                "\"[Sub Scribe-D] {File} — SubDL skipped — not configured (required for SubDL only)\"", StringComparison.Ordinal));

        // --- Abschnitt 16: Rip-Zuordnung fuer den tvst-Upload (F-M498) ---
        // Gemessen 15.11.2026 im scharfen Lauf: die Seite lehnt jede Folge mit "rip must be one of
        // WEB, HDTV, HEVC, DVDRip, DVDSCR, BDRip, BluRay" ab, weil der Release-Name "WEB-DL" sagt.
        var tvClientSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/TvSubtitlesClient.cs"));
        Check("der Release-Name wird auf einen der sieben Rip-Werte gezogen (F-M498)",
            tvClientSource.Contains("public static string RipFromQuality(string? quality)", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"WEB\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"HEVC\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"BDRip\";", StringComparison.Ordinal)
            && tvClientSource.Contains("return \"BluRay\";", StringComparison.Ordinal));

        Check("der Endpunkt bekommt den Rip durch die Zuordnung, nicht den rohen N amen (F-M498)",
            uploadSource.Contains("TvSubtitlesClient.RipFromQuality(quality)", StringComparison.Ordinal));

        Check("das Hearing-impaired-Kaestchen wird gesendet, wenn der Untertitel es ist (F-M498)",
            tvClientSource.Contains("new KeyValuePair<string, string>(\"hi\", \"1\")", StringComparison.Ordinal)
            && tvClientSource.Contains("if (hearingImpaired)", StringComparison.Ordinal));

        Check("die Fehlermeldung nennt nicht mehr den falschen Parameter (F-M498)",
            !tvClientSource.Contains("nameof(showId));", StringComparison.Ordinal));

        // --- Abschnitt 17: die Upload-Queue fragt je Quelle, nicht quellenblind (F-M499) ---
        // Gemessen 15.11.2026, 16:04: nach 27 SubDL-Uploads reihte der Seeder 0 Folgen ein
        // ("scan finished (upload) — 0 upload queued"), weil "schon hochgeladen" aus Status=Uploaded
        // gelesen wurde — also "irgendwo hochgeladen". Der zweite Endpunkt bekam nie seinen Versuch.
        Check("die Upload-Queue fragt je Quelle statt quellenblind (F-M499)",
            seederSource.Contains("UploadTodoFor(item, mediaPath, config, quelle,", StringComparison.Ordinal)
            && seederSource.Contains("registry.ArePositionsTerminalFor(mediaHash", StringComparison.Ordinal)
            && seederSource.Contains("registry.CountKnownPairsFor(mediaHash, quelle)", StringComparison.Ordinal)
            && uploadSource.Contains("registry.IsSidecarUploadedTo(looseHash, quelle)", StringComparison.Ordinal)
            && !seederSource.Contains("registry.CountKnownPairs(mediaHash)", StringComparison.Ordinal)
            && !seederSource.Contains("registry.ArePositionsTerminal(mediaHash", StringComparison.Ordinal)
            && !seederSource.Contains("registry.CountKnownPairs(mediaHash)", StringComparison.Ordinal)
            && !uploadSource.Contains("registry.IsSidecarUploaded(looseHash)", StringComparison.Ordinal));

        Check("der Helfer nimmt nur eingeschaltete UND konfigurierte Quellen, und die Marke liest EINE Regel (F-M499)",
            seederSource.Contains("foreach (var quelle in UploadSources(config, isSeries))", StringComparison.Ordinal)
            && seederSource.Contains(
                "if (config.IsUploadEnabled(quelle) && config.IsConfiguredForUpload(quelle))",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public bool ArePositionsTerminalFor(string? mediaHash, IEnumerable<int> positions, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public int CountKnownPairsFor(string? mediaHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "public bool IsSidecarUploadedTo(string? contentHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            // F-M669: die Spur fragt IHRE Position — eine Marke je Position, nicht je Paar.
            && registrySource.Contains(
                "return IsUploadedToPosition(mediaHash, track.SubPos, track.Language, track.HearingImpaired ?? false, provider);",
                StringComparison.Ordinal));

        // --- Abschnitt 18: auch der KOPF des Laufs fragt je Quelle (F-M499) ---------------------
        // Gemessen 15.11.2026, 16:17 im scharfen Lauf: der Seeder hatte nach Abschnitt 17 27 Folgen
        // quellenweise eingereiht, und die Pipeline warf sie alle mit "[SubDL] SKIP {File} — upload
        // file-complete" wieder weg — die Dateimarke stand von den SubDL-Uploads. Ergebnis "0 uploaded".
        Check("die Pipeline ueberspringt nur, wenn ALLE eingeschalteten und konfigurierten Quellen fertig sind (F-M499)",
            uploadSource.Contains("Registry.IsCompleteFor(earlyHash, quelle, kopfSignatur)", StringComparison.Ordinal)
            && uploadSource.Contains("if (quellen.Count > 0 && offeneQuellen.Count == 0)", StringComparison.Ordinal)
            && uploadSource.Contains(
                "private List<Configuration.SubtitleProvider> UploadSources(bool? isSeries) =>", StringComparison.Ordinal)
            && uploadSource.Contains(
                "_config.DryRun || _config.IsConfiguredForUpload(quelle)", StringComparison.Ordinal)
            // Die blinde Frage darf im Kopf des Laufs nicht mehr stehen — sie war der Defekt.
            && !uploadSource.Contains("IsSubtitlesUploaded(", StringComparison.Ordinal));

        Check("F-M557: die Abrechnung bildet das Sprachkennzeichen ab, bevor sie den Store fragt",
            uploadSource.Contains("LanguageMapper.Canonical(s.Language)", StringComparison.Ordinal)
            && seederSource.Contains("LanguageMapper.Canonical(s.Language)", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.OpenPositionsFor(earlyHash, quelle, kopfPosen.Select(p => p.Position))", StringComparison.Ordinal));

        // --- F-M557..F-M563: Zugeh-Stempel je Quelle, abgeleitet und nicht global -----------------
        var ticketSource = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Api", "Endpoints",
            "SubtitleEndpoints.cs")).ConfigureAwait(false);
        var tvstSource = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Api", "Endpoints",
            "TvSubtitlesEndpoints.cs")).ConfigureAwait(false);
        string reg557 = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Registry", "ContentHashRegistry.cs")).ConfigureAwait(false);
        string db557 = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Data", "ScribeDbContext.cs")).ConfigureAwait(false);
        Check("F-M557: der Store fuehrt ISO, die Quelle bekommt ihren Code erst an der Kante",
            reg557.Contains("public static string? Canonical(string? code)", StringComparison.Ordinal)
            || reg557.Contains("Language.LanguageMapper.Canonical(language) ?? language", StringComparison.Ordinal));
        Check("F-M557: Schema 16 schreibt den Bestand auf ISO um und leert die alten Stempel",
            // F-M668: die Sprachumstellung bleibt unangetastet; das Leeren der Stempel ist mit den
            // Spalten selbst entfallen (die Stempel sind `file`-Zeilen der Gridfilm).
            db557.Contains("private void NormaliseLanguages()", StringComparison.Ordinal)
            && !db557.Contains("media.CompleteSignature = null;", StringComparison.Ordinal)
            && db557.Contains("NormaliseLanguages();", StringComparison.Ordinal));

        Check("F-M557: die Abrechnung fragt JE SPUR ueber IsEmbedTerminalFor, nicht je Paar",
            reg557.Contains("public List<int> OpenPositionsFor(string? mediaHash, SubtitleProvider provider, IEnumerable<int> positions)", StringComparison.Ordinal)
            && reg557.Contains("if (!IsEmbedTerminalFor(mediaHash, pos, provider))", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.OpenPositionsFor(earlyHash, quelle, kopfPosen.Select(p => p.Position))", StringComparison.Ordinal)
            && !uploadSource.Contains("SettlementFor", StringComparison.Ordinal));
        Check("F-M562: die Spursignatur traegt Position, Sprache und HI je Spur",
            reg557.Contains("public static string PositionSignatureOf(", StringComparison.Ordinal)
            && seederSource.Contains("PositionSignatureOf(seedPosen)", StringComparison.Ordinal)
            && uploadSource.Contains("PositionSignatureOf(kopfPosen)", StringComparison.Ordinal));

        Check("F-M557: der Zugeh-Stempel steht JE QUELLE auf der Medienzeile (Schema 16)",
            // F-M668: der Stempel ist eine `file`-Zeile je (Medium, Quelle); die vier Medien-Spalten
            // gibt es nicht mehr, und die Signatur steht als `Fingerprint` daneben.
            db557.Contains("CurrentSchemaVersion = 24", StringComparison.Ordinal)
            && !db557.Contains("ADD COLUMN CompleteSubdl TEXT NULL", StringComparison.Ordinal)
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/SourceStore.cs"))
                .Contains("public static void WriteFileMark(", StringComparison.Ordinal)
            // und die Altspalten stehen in der Liste, die der Übernahmelauf niederlegt
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/SourceMatrixMigration.cs"))
                .Contains("\"media\", new[] { \"CompleteSubdl\", \"CompleteTvSubtitles\", \"CompleteMsub\", \"CompleteSignature\" }", StringComparison.Ordinal));
        Check("F-M557/F-M562: eine Frage — Stempel da UND Signatur unveraendert",
            reg557.Contains("public bool IsCompleteFor(string? mediaHash, SubtitleProvider provider, string? signature)", StringComparison.Ordinal)
            && reg557.Contains("public void MarkCompleteFor(string? mediaHash, SubtitleProvider provider, string? signature)", StringComparison.Ordinal)
            && reg557.Contains("public static string PositionSignatureOf(", StringComparison.Ordinal)
            // F-M668: die Signatur gehört zur `file`-Marke und wird dort verglichen — je Quelle.
            && reg557.Contains("return string.Equals(marke.Fingerprint ?? string.Empty, signature, StringComparison.Ordinal);", StringComparison.Ordinal));
        Check("F-M558/F-M559: die Abrechnung zaehlt entschieden, nicht hoffnungsvoll",
            reg557.Contains("public List<int> OpenPositionsFor(", StringComparison.Ordinal)
            && reg557.Contains("if (!IsEmbedTerminalFor(mediaHash, pos, provider))", StringComparison.Ordinal)
            && uploadSource.Contains("var offen = Registry.OpenPositionsFor(earlyHash, quelle, kopfPosen.Select(p => p.Position))", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.MarkCompleteFor(earlyHash, quelle, kopfSignatur)", StringComparison.Ordinal));
        Check("F-M563: Kopf und Einreiher stellen dieselbe Frage",
            uploadSource.Contains("!Registry.IsCompleteFor(earlyHash, quelle, kopfSignatur)", StringComparison.Ordinal)
            && seederSource.Contains("registry.IsCompleteFor(", StringComparison.Ordinal)
            && seederSource.Contains("ContentHashRegistry.PositionSignatureOf(seedPosen)", StringComparison.Ordinal));

        // --- F-M572: der Folgentitel steht im Kommentar ------------------------------------------
        Check("F-M572/F-M574: der Folgentitel geht ueber den Kommentar-Bauer mit, ein Platzhalter nicht",
            ticketSource.Contains("string? EpisodeTitle = null);", StringComparison.Ordinal)
            && tvstSource.Contains("episodeTitle: ticket.EpisodeTitle", StringComparison.Ordinal)
            && tvstSource.Contains("showTitle: ticket.ShowName", StringComparison.Ordinal)
            && tvstSource.Contains("year: ticket.MovieYear", StringComparison.Ordinal)
            && uploadSource.Contains("EpisodeTitle = EpisodeTitleOf(mediaPath)", StringComparison.Ordinal)
            && uploadSource.Contains("EpisodenPlatzhalter", StringComparison.Ordinal));

        // --- F-M575: eine Sammel-Karte statt eines Tabellendurchlaufs je Staffel -------------------
        Check("F-M575: beide Staffelfragen kommen aus einer Karte, die einmal je Lauf gelesen wird",
            uploadSource.Contains("_allePacks ??= Registry.PacksBySeason();", StringComparison.Ordinal)
            && uploadSource.Contains("_alleSprachen ??= Registry.LanguagePairsBySeason();", StringComparison.Ordinal)
            && !uploadSource.Contains("Registry.LanguagesInSeason(", StringComparison.Ordinal)
            && !uploadSource.Contains("Registry.PacksInSeason(", StringComparison.Ordinal)
            && reg557.Contains("public Dictionary<(string ImdbId, int Season), HashSet<(string Language, bool HearingImpaired)>> LanguagePairsBySeason()", StringComparison.Ordinal)
            && reg557.Contains("public Dictionary<(string ImdbId, int Season), HashSet<(string Language, bool HearingImpaired)>> PacksBySeason()", StringComparison.Ordinal));

        // --- F-M576: der billige Kopf-Ausstieg (F-M88c wiederhergestellt) -------------------------
        // Befund 17.11.2026: der Kopf fragte je Datei GetMediaStreams, die Positions-Abrechnung je
        // Quelle und ein Urteil je Textspur — bei 1.415 Dateien pro Zyklus fuer 155 Uploads. F-M88c
        // verlangt den Ausstieg vor ffprobe, Verzeichnisliste und API. Geprueft wird der Ort: der
        // Ausstieg steht VOR dem Stream-Abruf, und er fragt die Marke je Quelle.
        Check("F-M576: der billige Kopf-Ausstieg steht vor dem Stream-Abruf und fragt die Marke je Quelle",
            uploadSource.Contains("stamped complete for every source, no stream read (F-M576)", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.IsCompleteFor(earlyHash, quelle, null)", StringComparison.Ordinal)
            && uploadSource.IndexOf("stamped complete for every source, no stream read (F-M576)", StringComparison.Ordinal)
                < uploadSource.IndexOf("_mediaSourceManager.GetMediaStreams(item.Id)", StringComparison.Ordinal)
            && reg557.Contains("public DateTime? NewestCompletionStamp(string? mediaHash)", StringComparison.Ordinal));

        // --- F-M577: Extraktions-Sperre je Spur (1d/7d/30d/nie) -----------------------------------
        Check("F-M577: eine gescheiterte Spur wird als Ablehnung geparkt und faellt aus der Extraktionsliste",
            uploadSource.Contains("Registry.EmbedRejectedReason(mediaHash, position) == RejectReason.ExtractionFailed", StringComparison.Ordinal)
            && uploadSource.Contains("every open text stream is parked after an extraction failure, no ffmpeg pass (F-M577)", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.ExtractRejectIsStale(mediaHash, position, ExtractRetryAfter)", StringComparison.Ordinal)
            && reg557.Contains("public bool ExtractRejectIsStale(string? mediaHash, int subPos, TimeSpan ttl)", StringComparison.Ordinal)
            && reg557.Contains("zeile is { Reason: RejectReason.ExtractionFailed }", StringComparison.Ordinal)
            && entities.Contains("public const string ExtractionFailed = \"extraction-failed\";", StringComparison.Ordinal));
        Check("F-M577: eine fehlende Datei wird nicht als Extraktionsfehler geparkt",
            uploadSource.Contains("bool dateiDa = File.Exists(mediaPath);", StringComparison.Ordinal)
            && uploadSource.Contains("reason: RejectReason.ExtractionFailed", StringComparison.Ordinal)
            && uploadSource.IndexOf("bool dateiDa = File.Exists(mediaPath);", StringComparison.Ordinal)
                < uploadSource.IndexOf("reason: RejectReason.ExtractionFailed", StringComparison.Ordinal));

        // --- F-M578 / F-M579: zwei Bestandskonzepte, deren Wegfall am 17.11.2026 ohne Beleg war
        //     und deshalb als Anforderung festgeschrieben wurde --------------------------------
        var apiClientSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/ScribeApiClient.cs"));
        Check("F-M578: kein Kandidaten-Deckel je Sprache — der Schalter ist weg und keine Aufrufstelle setzt die Schwelle",
            !cfgSource.Contains("DownloadMaxCandidatesPerLanguage", StringComparison.Ordinal)
            && apiClientSource.Contains("int maxCandidatesPerLanguage = 0", StringComparison.Ordinal)
            && !downloadSource.Contains("maxCandidatesPerLanguage", StringComparison.Ordinal)
            && !uploadSource.Contains("maxCandidatesPerLanguage", StringComparison.Ordinal));
        Check("F-M579: kein IMDb-Pflicht-Schalter — die Leiter holt die fehlende IMDb-Id aus TMDb",
            !cfgSource.Contains("DownloadRequireImdb", StringComparison.Ordinal)
            && downloadSource.Contains("ResolveImdbAsync", StringComparison.Ordinal)
            && !downloadSource.Contains("DownloadRequireImdb", StringComparison.Ordinal));

        // --- F-M593: Takt 100-650, Vorgabe 450, Hinweis auf SubDLs 500/h ---------------------------
        var guiSource593 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        var configSource593 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/PluginConfiguration.cs"));
        Check("F-M593: das Feld heisst 50-500 und laesst nicht mehr zu",
            guiSource593.Contains("Transfer rate per hour (50&ndash;500)", StringComparison.Ordinal)
            && guiSource593.Contains("id=\"UploadsPerHour\" name=\"UploadsPerHour\" type=\"number\" is=\"emby-input\" min=\"50\" max=\"500\"", StringComparison.Ordinal));
        Check("F-M593: die Beschreibung nennt nur noch Takt und Vorgabe — kein SubDL-Hinweis mehr",
            guiSource593.Contains("Paces the gap between two real transfers (uploads or downloads). Default: 450.", StringComparison.Ordinal)
            && !guiSource593.Contains("SubDL temporarily blocks", StringComparison.Ordinal));
        Check("F-M593: beim Speichern wird auf 50-500 begrenzt, der Rueckfall ist 450",
            guiSource593.Contains("Math.min(500, Math.max(50, uph))", StringComparison.Ordinal)
            && guiSource593.Contains("document.querySelector('#UploadsPerHour').value) || 450", StringComparison.Ordinal));
        Check("F-M593: die Vorgabe im Konfigurationsmodell ist 450",
            configSource593.Contains("UploadsPerHour = 450;", StringComparison.Ordinal));

        // --- F-M605: das Nein einer Quelle wird gemerkt -----------------------------------------------
        var refusal605 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/SourceRefusalStore.cs"));
        var up605 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var db605 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"));
        Check("F-M605: ein gemerktes Nein gilt nur fuer genau diesen Inhalt",
            refusal605.Contains("public static bool StillRefused(", StringComparison.Ordinal)
            && refusal605.Contains("if (row == null || !string.Equals(row.Signature, signature, StringComparison.Ordinal))", StringComparison.Ordinal));
        // F-M609 hat die Wortliste durch das Urteil ersetzt: gemerkt wird jedes Refused, eine Stoerung
        // (NoVerdict) nie. Die Pruefung haelt jetzt diese Zusage fest, nicht mehr die alte Liste.
        Check("F-M605/F-M609: gemerkt wird ein inhaltliches Nein, kein Rate-Limit und kein Abbruch",
            refusal605.Contains("public static bool ShouldRemember(string? reason, Api.UploadVerdict verdict)", StringComparison.Ordinal)
            && refusal605.Contains("return verdict == Api.UploadVerdict.Refused;", StringComparison.Ordinal)
            && !refusal605.Contains("\"timeout\"", StringComparison.Ordinal));
        Check("F-M605: vor dem Quellen-Lauf wird gefragt, nach dem Lauf gemerkt, bei Erfolg vergessen",
            up605.Contains("SourceRefusalStore.StillRefused(", StringComparison.Ordinal)
            && up605.Contains("SourceRefusalStore.Put(", StringComparison.Ordinal)
            && up605.Contains("SourceRefusalStore.Forget(", StringComparison.Ordinal));
        Check("F-M605: die Tabelle source_refusals entsteht beim Start und wird unter demselben Namen gelesen",
            db605.Contains("CREATE TABLE IF NOT EXISTS source_refusals (", StringComparison.Ordinal)
            && db605.Contains("e.ToTable(\"source_refusals\");", StringComparison.Ordinal));

        // --- F-M617: jeder Store hilft sich selbst -----------------------------------------------
        var storeDir617 = Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry");
        var ohneSelbsthilfe = new List<string>();
        foreach (string datei in new[] { "TmdbAnswerStore.cs", "AiredSeasonStore.cs", "PackSeasonStore.cs", "SourceRefusalStore.cs", "TitleFailureStore.cs" })
        {
            string quelle = ReadText(Path.Combine(storeDir617, datei));
            if (!quelle.Contains("Plugin.Instance?.SharedDbContext?.Context", StringComparison.Ordinal))
            {
                ohneSelbsthilfe.Add(datei);
            }
        }

        Check("F-M617: JEDER Store holt sich einen fehlenden Kontext selbst",
            ohneSelbsthilfe.Count == 0,
            ohneSelbsthilfe.Count == 0 ? "alle fünf" : string.Join(", ", ohneSelbsthilfe));

        // --- F-M616: alle Stores haengen an beiden Startwegen -----------------------------------------
        var pl616 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Plugin.cs"));
        Check("F-M616: es gibt genau eine Stelle, die alle Stores anhaengt",
            System.Text.RegularExpressions.Regex.Matches(pl616, @"\bAttachStores\(_sharedDbContext\.Context, logger, DataFolderPath, Configuration\.LogMode\);").Count == 2
            && pl616.Contains("private static void AttachStores(", StringComparison.Ordinal));
        Check("F-M616: Verweigerungen und Titelsperre sind dabei, nicht nur die drei alten",
            pl616.Contains("SourceRefusalStore.Attach(db);", StringComparison.Ordinal)
            && pl616.Contains("TitleFailureStore.Attach(db);", StringComparison.Ordinal)
            && pl616.Contains("TitleFailureStore.AttachLogger(logger);", StringComparison.Ordinal)
            && pl616.Contains("PackSeasonStore.Attach(db);", StringComparison.Ordinal)
            && pl616.Contains("AiredSeasonStore.Attach(db);", StringComparison.Ordinal)
            && pl616.Contains("TmdbAnswerStore.Attach(db);", StringComparison.Ordinal));
        Check("F-M616: kein Store wird mehr am Helfer vorbei angehaengt",
            !System.Text.RegularExpressions.Regex.IsMatch(pl616, @"(?<!AttachStores[\s\S]{0,1200})Registry\.(TmdbAnswer|AiredSeason|PackSeason|SourceRefusal|TitleFailure)Store\.Attach\(_sharedDbContext\.Context\);"));
        // --- F-M615: kein stiller Fehlschlag mehr ----------------------------------------------------
        var tfs615 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/TitleFailureStore.cs"));
        var pl615 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Plugin.cs"));
        Check("F-M615: der Titelspeicher hat ein Protokoll und meldet jeden Fehlschlag",
            tfs615.Contains("public static void AttachLogger(ILogger? log)", StringComparison.Ordinal)
            && tfs615.Contains("title store: closing {Key} failed", StringComparison.Ordinal)
            && tfs615.Contains("title store: writing {Key} failed", StringComparison.Ordinal)
            && tfs615.Contains("title store: reading {Key} failed", StringComparison.Ordinal));
        Check("F-M615: auch ein nicht angehaengter Store und ein fehlender Eintrag sind sichtbar",
            tfs615.Contains("NOT attached — the ladder can only count within the run", StringComparison.Ordinal)
            && (tfs615.Contains("NOT in the store — the ladder runs again (F-M618)", StringComparison.Ordinal)
                || tfs615.Contains("not in the store (F-M615)", StringComparison.Ordinal))
            && tfs615.Contains("is closed ({Reason}, attempts {Attempts})", StringComparison.Ordinal));
        Check("F-M615: das Plugin haengt das Protokoll beim Start an",
            pl615.Contains("TitleFailureStore.AttachLogger(logger);", StringComparison.Ordinal));

        // --- F-M614: der Store wird je Gegenstand geschrieben -----------------------------------------
        var reg614 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        Check("F-M614: Flush schreibt wirklich, statt leer zu bleiben",
            reg614.Contains("public void Flush() => _db.FlushPending();", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(reg614, @"public void Flush\(\)\s*\{\s*\}"));
        var dl614 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        int fl614d = dl614.IndexOf("Registry.Flush();", StringComparison.Ordinal);
        int la614d = fl614d > 0 ? dl614.IndexOf("LAUF download", fl614d, StringComparison.Ordinal) : -1;
        Check("F-M614: das gilt generell — auch die Download-Pipeline schreibt je Gegenstand",
            fl614d > 0 && la614d > fl614d && la614d - fl614d < 800);
        int fl614 = up605.IndexOf("Registry.Flush();", StringComparison.Ordinal);
        int la614 = fl614 > 0 ? up605.IndexOf("LAUF upload", fl614, StringComparison.Ordinal) : -1;
        Check("F-M614: die Pipeline schreibt am Ende jedes Gegenstands",
            fl614 > 0 && la614 > fl614 && la614 - fl614 < 800);

        // --- F-M613: die Pumpe bleibt gefuettert ------------------------------------------------------
        var la613 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/ExtractionLookahead.cs"));
        Check("F-M613: der Vorlauf wird an jedem Gegenstand angemeldet, auch wenn er uebersprungen wird",
            up605.Contains("private void MeldeVorlauf(", StringComparison.Ordinal)
            && up605.Contains("MeldeVorlauf(vorlauf, items, itemIndex, runCt);", StringComparison.Ordinal));
        Check("F-M613: die Anmeldung steht auch im Pfad 'item settled before extraction'",
            System.Text.RegularExpressions.Regex.IsMatch(up605, @"MeldeVorlauf\(vorlauf, items, itemIndex, runCt\);\s*summary\.SettledItems\+\+;"));
        Check("F-M613: es laufen nie zwei ffmpeg — vor einer frischen Extraktion wird die Pumpe freigegeben",
            la613.Contains("public void Freigeben()", StringComparison.Ordinal)
            && la613.Contains("F-M613: erst die Pumpe freigeben, dann selbst extrahieren", StringComparison.Ordinal)
            && la613.IndexOf("Freigeben();", la613.IndexOf("var frisch = await FfmpegTools.ExtractAllAsync", StringComparison.Ordinal) - 200, StringComparison.Ordinal) > 0);

        // --- F-M612: die tvst-Kennung wird nicht mehr nachgelesen -------------------------------------
        var tvstEp612 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/TvSubtitlesEndpoints.cs"));
        Check("F-M612: der tvst-Versand wartet nicht mehr auf eine Kennung, die es nicht gibt",
            tvstEp612.Contains("das Nachlesen der Kennung ist WEG", StringComparison.Ordinal)
            && !tvstEp612.Contains("FindUploadedIdAsync(", StringComparison.Ordinal)
            && tvstEp612.Contains("string? id = null;", StringComparison.Ordinal));
        Check("F-M612: die eigene Marke steht bereit, damit das Paar trotzdem geschlossen wird",
            up605.Contains("we write our own marker (F-M612)", StringComparison.Ordinal)
            && up605.Contains("Provenance.OwnUploadId", StringComparison.Ordinal));

        // --- F-M657: EIN Namensraum fuer Plan und Sammler --------------------------------------------
        // Befund 20.11.2026 (Operator: „Wichtiger waere dass der Speichermerker gesetzt ist … damit er
        // das naechste Mal nicht nochmal so lange braucht"): 522 Urteile wurden geschrieben, der Plan
        // fragte aber einen anderen Namensraum und sah keines davon.
        var store657 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/PackSeasonStore.cs"));
        Check("F-M657: der Namensraum des Fuellens steht EINMAL im Store, nicht in jeder Klasse",
            store657.Contains("public const string FillTagSuffix = \"|fill\";", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"))
                .Contains("private const string FillTagSuffix = Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.FillTagSuffix;", StringComparison.Ordinal));

        // --- F-M658: Positionen zaehlen nur die Spuren IM Container ---------------------------------
        // Befund 20.11.2026: Sampleland S02E02 hat zwei .de-SRT neben dem MKV; der Sammler bildete die
        // Position ueber Jellyfins Liste MIT den Beidateien, ffmpeg bekam `-map 0:s:2` auf einer Datei
        // mit zwei Untertitelspuren, antwortete „Stream map '' matches no streams." (exit 234) — und
        // die Spur galt als „extraction failed", obwohl sie im Container liegt.
        Check("F-M658: der Sammler zaehlt wie der Einzelpfad nur die Spuren im Container",
            uploadSource.Contains("private List<MediaStream> ContainerSubtitleStreams(BaseItem item)", StringComparison.Ordinal)
            && uploadSource.Contains(".Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal)", StringComparison.Ordinal)
            && uploadSource.Contains("allSubs = ContainerSubtitleStreams(item);", StringComparison.Ordinal)
            && uploadSource.Contains("var spurenDesItems = ContainerSubtitleStreams(item);", StringComparison.Ordinal));

        Check("F-M658: die Fassung des Fuell-Urteils steht im Schluessel — alte Fehlurteile werden neu geprueft",
            store657.Contains("public const string FillSignatureVersion = \"|fill=2\";", StringComparison.Ordinal)
            && store657.Contains("seasonSignature + FillSignatureVersion", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"))
                .Contains("PackSeasonStore.FillSignatureVersion", StringComparison.Ordinal));

        // --- F-M659: ein gescheiterter ffmpeg-Durchlauf ist kein Urteil ---------------------------------
        var iface659 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/IPackFiller.cs"));
        Check("F-M659: der Sammler wertet den Exit-Code des Durchlaufs aus und faesst einzeln nach",
            uploadSource.Contains("var (extracted, exit) = await FfmpegTools", StringComparison.Ordinal)
            && uploadSource.Contains("if (exit != 0)", StringComparison.Ordinal)
            && uploadSource.Contains("retried one by one", StringComparison.Ordinal));

        Check("F-M659: ein gescheiterter Durchlauf wird NICHT als Speichermerker geschrieben",
            iface659.Contains("bool LastFillInconclusive { get; }", StringComparison.Ordinal)
            && uploadSource.Contains("public bool LastFillInconclusive { get; private set; }", StringComparison.Ordinal)
            && uploadSource.Contains("LastFillInconclusive = false;", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"))
                .Contains("_filler?.LastFillInconclusive == true", StringComparison.Ordinal));

        Check("F-M659: die zwei toten Methoden mit der alten Zaehlweise sind weg",
            !uploadSource.Contains("ExtractAllStreamsAsync", StringComparison.Ordinal)
            && !uploadSource.Contains("private List<MediaStream> GetTextSubtitleStreams", StringComparison.Ordinal));

        // --- F-M661: ein leerer Korb verlaengert den Takt ---------------------------------------------
        var tracker661 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/DownloadSearchTracker.cs"));
        Check("F-M661: der Leer-Merker ist eine eigene spaete Spalte an media",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"))
                .Contains("public DateTime? LastSearchEmptyUtc { get; set; }", StringComparison.Ordinal)
            // F-M671: die Spalte steht in der DDL der Datei-Ebene, nicht mehr in einem ALTER-Nachzug.
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"))
                .Contains("+ \"LastSearchEmptyUtc TEXT NULL, \"", StringComparison.Ordinal));
        Check("F-M661: der Takt waechst nur bei leerer Antwort — Faktor sieben, gedeckelt bei 30 Tagen",
            tracker661.Contains("public const int EmptySearchFactor = 7;", StringComparison.Ordinal)
            && tracker661.Contains("Math.Min(30d, gap.TotalDays * EmptySearchFactor)", StringComparison.Ordinal)
            && tracker661.Contains("TimeSpan effective = media.LastSearchEmptyUtc != null ? EmptyGap(gap) : gap;", StringComparison.Ordinal));
        Check("F-M661: ein Treffer raeumt den Leer-Merker, die Reset-Wege raeumen mit",
            tracker661.Contains("media.LastSearchEmptyUtc = null;", StringComparison.Ordinal)
            && tracker661.Contains("public bool MarkSearchEmpty(string itemId)", StringComparison.Ordinal));
        Check("F-M661: gestempelt wird nur nach einer wirklich gestellten Frage und nie im Trockenlauf",
            downloadSource.Contains("&& gefragteQuellen.Contains(MainSearchSource)", StringComparison.Ordinal)
            && downloadSource.Contains("_searchTracker.MarkSearchEmpty(item.Id.ToString())", StringComparison.Ordinal)
            && downloadSource.Contains("SEARCH deferred {File} — the search came back empty", StringComparison.Ordinal));

        // --- F-M660: der Datei-Reset kann die Staffel-Urteile der Pack-Phase mitnehmen ---------------
        var reset660 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/ScribeReset.cs"));
        Check("F-M660: die Route nimmt 'pack' als eigenen Wert und meldet die Zahl zurueck",
            reset660.Contains("bool staffel = teile.Contains(\"pack\");", StringComparison.Ordinal)
            && reset660.Contains("ReopenSeasonVerdicts(hash)", StringComparison.Ordinal)
            && reset660.Contains("seasonVerdicts = staffelUrteile", StringComparison.Ordinal));
        Check("F-M660: der Staffel-Merker laesst sich je Serie und Staffel raeumen",
            store657.Contains("public static int ClearSeason(string imdbId, int season)", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"))
                .Contains("public int ReopenSeasonVerdicts(string? mediaHash)", StringComparison.Ordinal));

        Check("F-M657: Plan UND Sammler fragen beide Namensraeume",
            uploadSource.Contains("PackSeasonStore.StillImpossibleAny(", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"))
                .Contains("PackSeasonStore.StillImpossibleAny(", StringComparison.Ordinal)
            && store657.Contains("public static bool StillImpossibleAny(", StringComparison.Ordinal));

        // --- F-M611: das Pack-Urteil zum Fuellversuch speichern -------------------------------------
        var coord611 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check("F-M611: der Fuellversuch schreibt sein Urteil mit der Signatur der Staffel",
            coord611.Contains("public Func<string, int, string>? SeasonSignature { get; set; }", StringComparison.Ordinal)
            && coord611.Contains("private void Refuse(string imdbId, int season, string lang, bool hi, string reason)", StringComparison.Ordinal)
            && coord611.Contains("SeasonPack.LangTag(lang, hi) + FillTagSuffix", StringComparison.Ordinal)
            && coord611.Contains("Registry.PackSeasonStore.NoPack, reason);", StringComparison.Ordinal));
        Check("F-M611: ein gespeichertes Urteil verhindert die naechste Extraktion, solange die Signatur gleich bleibt",
            coord611.Contains("Registry.PackSeasonStore.StillImpossibleAny(", StringComparison.Ordinal)
            && coord611.Contains("LastSkipReason = $\"no pack ({altGrund}), the verdict is stored with the files' signature (F-M611)\"", StringComparison.Ordinal)
            && coord611.Contains("LastRefusal = null;", StringComparison.Ordinal));
        Check("F-M611: beide Abbruchwege (vor und nach der Extraktion) hinterlassen ein Urteil",
            coord611.Contains("Refuse(imdbId, season, lang, hi, $\"episode {episode} cannot join ({vorabGrund})\");", StringComparison.Ordinal)
            && coord611.Contains("Refuse(imdbId, season, lang, hi, $\"episode {episode} cannot join\");", StringComparison.Ordinal)
            && coord611.Contains("Refuse(imdbId, season, lang, hi, \"the files do not carry this language in every aired episode\");", StringComparison.Ordinal));
        Check("F-M611: die Pipeline reicht die Signatur aus der Planung an den Koordinator",
            up605.Contains("_saisonSignatur[(entry.Key.Item1, entry.Key.Item2)] = saisonSignatur;", StringComparison.Ordinal)
            && up605.Contains("packCoordinator.SeasonSignature =", StringComparison.Ordinal)
            && up605.Contains("packCoordinator.LastRefusal", StringComparison.Ordinal));

        // --- F-M610: nicht zu viel schliessen ----------------------------------------------------------
        var store610 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/TitleFailureStore.cs"));
        Check("F-M610: ein Beleg schliesst den Titel nicht — er wird beim Lesen sogar verworfen",
            store610.Contains("private static readonly string[] EvidenceReasons", StringComparison.Ordinal)
            && store610.Contains("\"created-id-not-readable\",", StringComparison.Ordinal)
            && store610.Contains("\"exists-id-not-readable\",", StringComparison.Ordinal)
            && store610.Contains("if (EvidenceReasons.Any(e => string.Equals(e, row.Reason, StringComparison.OrdinalIgnoreCase)))", StringComparison.Ordinal));
        // F-M734: die Leiter steht in eigener Datei, weil der Behaelter-Versand dieselbe faehrt.
        var leiter610 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/TitleLadder.cs"));
        Check("F-M610: eine Stoerung zaehlt nicht als Fehlschlag und schliesst nichts",
            leiter610.Contains("Func<T, bool>? stoerung,", StringComparison.Ordinal)
            && leiter610.Contains("if (!letzteStoerung)", StringComparison.Ordinal)
            && leiter610.Contains("if (!trockenlauf && !letzteStoerung && !nurAusDerAblage)", StringComparison.Ordinal)
            && leiter610.Contains("attempts without a verdict ({Reason}) — nothing closed, the next run asks again (F-M610)", StringComparison.Ordinal));
        Check("F-M610: 'existiert, Kennung nicht lesbar' gilt als Beleg, nicht als Fehlschlag der Leiter",
            leiter610.Contains("Func<T, bool>? schonBelegt,", StringComparison.Ordinal)
            && leiter610.Contains("if (gelungen(ergebnis) || (schonBelegt?.Invoke(ergebnis) ?? false))", StringComparison.Ordinal)
            // F-M734: beide Adressen melden den Beleg NEUTRAL aus dem Auftrag — Settled heisst
            // "Titel belegt, Kennung fehlt".
            && up605.Contains("a => a.Title.Settled", StringComparison.Ordinal)
            && up605.Contains("if (answer.Title.Settled)", StringComparison.Ordinal)
            && up605.Contains("return UploadResult.AcceptedWithoutId(\"second-source-show-created-id-unknown\");", StringComparison.Ordinal));
        Check("F-M610: ohne Urteil gibt es NoVerdict statt eines geschlossenen Titels",
            up605.Contains("return UploadResult.NoVerdict(\"film-source-title-no-verdict\");", StringComparison.Ordinal)
            && up605.Contains("return UploadResult.NoVerdict(\"second-source-title-no-verdict\");", StringComparison.Ordinal)
            && up605.Contains("a => a.Title.State == Sources.SourceTitleState.Unclear", StringComparison.Ordinal)
            && up605.Contains("if (!answer.Title.Resolved && !answer.Title.Settled)", StringComparison.Ordinal));

        // --- F-M609: drei Urteile — angenommen, abgelehnt, kein Urteil ---------------------------------
        var ur609 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/UploadResult.cs"));
        var api609 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/ScribeApiClient.cs"));
        Check("F-M609: das Urteil hat vier Werte, und ein Versuch ohne Antwort ist ein eigener Fall",
            ur609.Contains("public enum UploadVerdict", StringComparison.Ordinal)
            && ur609.Contains("Accepted,", StringComparison.Ordinal)
            && ur609.Contains("AcceptedWithoutId,", StringComparison.Ordinal)
            && ur609.Contains("Refused,", StringComparison.Ordinal)
            && ur609.Contains("NoVerdict,", StringComparison.Ordinal)
            // F-M685: derselbe Ausgang kennt jetzt zwei Spielarten des "ohne Antwort" — 429/Stundendeckel
            // sind voruebergehend (busy), Timeout/Netz nicht. Der Wert bleibt derselbe.
            && ur609.Contains("public static UploadResult NoVerdict(string reason, bool busy = false)", StringComparison.Ordinal)
            && ur609.Contains("Verdict = UploadVerdict.NoVerdict", StringComparison.Ordinal));
        Check("F-M609: Timeout, Ausnahme und Abbruch vor dem Senden sind KEIN Urteil",
            up605.Contains("return UploadResult.NoVerdict(\"timeout\");", StringComparison.Ordinal)
            && up605.Contains("return UploadResult.NoVerdict(ex.Message);", StringComparison.Ordinal)
            && up605.Contains("return UploadResult.NoVerdict(\"run cancelled before the send\");", StringComparison.Ordinal));
        Check("F-M609: 429 und 5xx sind eine Stoerung, 4xx eine Ablehnung",
            // F-M687: der Statusleser ist mit dem Endpunkt aus der Kette gefallen; die Regel steht jetzt
            // an EINER Stelle — der Ausgang der Grenze wird dort gedeutet.
            ur609.Contains("public static UploadResult FromAnswer(Sources.SourceSendAnswer answer)", StringComparison.Ordinal)
            && ur609.Contains("case Sources.SourceOutcome.Busy:", StringComparison.Ordinal)
            && ur609.Contains("return MitTitel(NoVerdict(answer.Reason ?? \"busy\", busy: true), answer);", StringComparison.Ordinal)
            && !up605.Contains("IstStoerung(", StringComparison.Ordinal));
        Check("F-M609: bei SubDL sind Stundendeckel, rate-limited und API-Fehler Stoerungen, kein Nein",
            api609.Contains("return UploadResult.NoVerdict(\"rate-limited\", busy: true);", StringComparison.Ordinal)
            && api609.Contains("return UploadResult.NoVerdict(\"upload-hourly-cap\", busy: true);", StringComparison.Ordinal)
            && api609.Contains("return UploadResult.NoVerdict($\"getNId failed: {Trim(nBody)}\");", StringComparison.Ordinal)
            // F-M685: die Dublette traegt jetzt den Klartext der Quelle mit (zweites Argument).
            && api609.Contains("return UploadResult.Skipped(\"duplicate-content\"", StringComparison.Ordinal));
        Check("F-M609: gemerkt wird nach URTEIL, nicht nach einer Liste von Wortanfaengen",
            refusal605.Contains("public static bool ShouldRemember(string? reason, Api.UploadVerdict verdict)", StringComparison.Ordinal)
            && refusal605.Contains("return verdict == Api.UploadVerdict.Refused;", StringComparison.Ordinal)
            && !refusal605.Contains("Remembered.Any(prefix", StringComparison.Ordinal));
        Check("F-M609: die Kopfzeile trennt abgelehnt von ohne Antwort",
            up605.Contains("public int Refused { get; set; }", StringComparison.Ordinal)
            && up605.Contains("public int NoVerdicts { get; set; }", StringComparison.Ordinal)
            && up605.Contains("{Refused} refused, {NoVerdict} without answer", StringComparison.Ordinal));

        // --- F-M608: Titel-Leiter fuer tvst und mst --------------------------------------------------
        var fail608 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/TitleFailureStore.cs"));
        Check("F-M608: die Pausen sind 2 s, 4 s, 8 s und der vierte Fehlschlag schliesst den Titel",
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/TitleLadder.cs"))
                .Contains("PausenMs = { 2000, 4000, 8000 };", StringComparison.Ordinal)
            && fail608.Contains("public const int ClosingAttempt = 4;", StringComparison.Ordinal)
            && fail608.Contains("public static void Close(string source, string titleId, string reason)", StringComparison.Ordinal));
        Check("F-M608: die Leiter leert vor jedem Zug den Speicher des Laufs, sonst wiederholt sie nur das Urteil",
            // F-M734: beide Adressen brauchen keinen Lauf-Speicher mehr — ihr Versuch ist der Auftrag,
            // und die Engine haelt nichts je Lauf. Zwei Aufrufstellen, beide mit leerem Zwischenschritt.
            up605.Split("() => { },").Length - 1 == 2
            && !up605.Contains("_tvstShows.BeginRun(),", StringComparison.Ordinal)
            && ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/TitleLadder.cs"))
                .Contains("int pause = PausenMs[Math.Min(zug - 2, PausenMs.Length - 1)];", StringComparison.Ordinal));
        Check("F-M608: beide Adressen mit Anlage fahren die Leiter, subdl nicht",
            up605.Contains("Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.IsClosed(\"moviesubtitles\", msubTitelId", StringComparison.Ordinal)
            && up605.Contains("Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.IsClosed(\"tvsubtitles\", tvstTitelId", StringComparison.Ordinal)
            && !up605.Contains("TitleFailureStore.IsClosed(\"subdl\"", StringComparison.Ordinal));
        Check("F-M608: ein im Lauf geschlossener Titel laeuft die Leiter nicht fuer jede Spur erneut",
            up605.Contains("private readonly System.Collections.Generic.HashSet<string> _titelGeschlossen", StringComparison.Ordinal)
            && up605.Contains("_titelGeschlossen.Contains(\"moviesubtitles|\" + msubTitelId)", StringComparison.Ordinal)
            && up605.Contains("_titelGeschlossen.Add(\"moviesubtitles|\" + msubTitelId);", StringComparison.Ordinal)
            && up605.Contains("_titelGeschlossen.Contains(\"tvsubtitles|\" + tvstTitelId)", StringComparison.Ordinal)
            && up605.Contains("_titelGeschlossen.Add(\"tvsubtitles|\" + tvstTitelId);", StringComparison.Ordinal));
        Check("F-M608: ein geschlossener Titel wird gar nicht mehr gefragt — auf beiden Seiten mit Grund",
            up605.Contains("return UploadResult.Skipped(\"film-source-title-closed\");", StringComparison.Ordinal)
            && up605.Contains("return UploadResult.Skipped(\"second-source-title-closed\");", StringComparison.Ordinal));

        // --- F-M607: anlegen, nachsuchen, bei Nichtfund das Paar schliessen --------------------------
        Check("F-M607: beide Belege der Seite (angelegt / fuehrt den Titel) schliessen das Paar, andere Neins nicht",
            up605.Contains("if (answer.Title.Settled)", StringComparison.Ordinal)
            && up605.Contains("return UploadResult.AcceptedWithoutId(\"film-source-movie-\" + answer.Title.State);", StringComparison.Ordinal));

        // --- F-M606: erst fragen, ob die Kennung bespielbar ist, dann senden --------------------------
        var msub606 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/MovieSubtitlesClient.cs"));
        Check("F-M606: die Film-Adresse fragt vor dem Senden, ob add.php?let=… das Formular liefert",
            msub606.Contains("public async Task<bool> FilmUsableForUploadAsync(int movieId, CancellationToken cancellationToken = default)", StringComparison.Ordinal)
            && msub606.Contains("{0}/add.php?let={1}", StringComparison.Ordinal));
        Check("F-M606: ohne bespielbare Kennung wird nichts gesendet — mit Grund",
            // F-M734: die Vorpruefung gehoert zur Seite und steht deshalb in der Engine; der Ablauf fragt
            // den Client nicht mehr selbst.
            ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Sources/Msub/MsubEngine.cs"))
                .Contains("FilmUsableForUploadAsync(film, ct)", StringComparison.Ordinal)
            && !up605.Contains("_msubClient.FilmUsableForUploadAsync", StringComparison.Ordinal));

        // --- F-M604: das Pack-Urteil liegt im Store, mit Signatur der Mitglieder ----------------------
        var store604 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/PackSeasonStore.cs"));
        var db604 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"));
        var up604 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M604: die Signatur besteht aus gelaufenen Folgen und je Datei Nummer, Groesse, Speicherdatum",
            store604.Contains("public static string SignatureOf(IEnumerable<int> aired, IEnumerable<(int Episode, long Size, DateTime Written)> files)", StringComparison.Ordinal)
            && store604.Contains("return \"aired=\" + gelaufen + \"|files=\" + mitglieder;", StringComparison.Ordinal)
            && store604.Contains("f.Written.ToUniversalTime().Ticks", StringComparison.Ordinal));
        Check("F-M604: ein Urteil gilt nur fuer genau dieselbe Signatur — sonst neuer Versuch",
            store604.Contains("public static bool StillImpossible(string imdbId, int season, string langTag, string currentSignature, out string reason)", StringComparison.Ordinal)
            && store604.Contains("if (!string.Equals(signature, currentSignature, StringComparison.Ordinal))", StringComparison.Ordinal));
        Check("F-M604: die Tabelle pack_seasons entsteht beim Start und wird unter demselben Namen gelesen",
            db604.Contains("CREATE TABLE IF NOT EXISTS pack_seasons (", StringComparison.Ordinal)
            && db604.Contains("e.ToTable(\"pack_seasons\");", StringComparison.Ordinal)
            && db604.Contains("public DbSet<PackSeasonEntity> PackSeasonRows", StringComparison.Ordinal));
        Check("F-M604: die Planung rechnet die Signatur aus einem stat je Folgendatei, nicht aus einem Lesevorgang",
            up604.Contains("private string SeasonSignatureOf(IEnumerable<BaseItem> items, IReadOnlyList<int> aired)", StringComparison.Ordinal)
            && up604.Contains("var datei = new System.IO.FileInfo(item.Path);", StringComparison.Ordinal)
            && up604.Contains("dateien.Add((folge, datei.Length, datei.LastWriteTimeUtc));", StringComparison.Ordinal));
        Check("F-M604: Staffel- und Paar-Ebene fragen das gespeicherte Urteil, die Spur-Liste des Stores steckt in der Paar-Signatur",
            up604.Contains("PackSeasonStore.StillImpossible(\n                    entry.Key.Item1, entry.Key.Item2, Jellyfin.Plugin.SubScribe.Registry.PackSeasonStore.SeasonTag, saisonSignatur", StringComparison.Ordinal)
            && up604.Contains("string paarSignatur = saisonSignatur + \"|store=\" + mitSprache.Count.ToString", StringComparison.Ordinal));

        // --- F-M602: die Schreibvorgaenge eines Laufs werden gebuendelt ------------------------------
        var reg602 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        var up602 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var down602 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check("F-M602: der Store bietet die Klammer an",
            reg602.Contains("public void BeginBatch() => _db.BeginBatch();", StringComparison.Ordinal)
            && reg602.Contains("public void EndBatch() => _db.EndBatch();", StringComparison.Ordinal));
        Check("F-M602: der Upload-Lauf oeffnet und schliesst sie",
            up602.Contains("Registry.BeginBatch(); // F-M602", StringComparison.Ordinal)
            && up602.Contains("Registry.EndBatch(); // F-M602", StringComparison.Ordinal));
        Check("F-M602: der Download-Lauf oeffnet und schliesst sie ebenfalls",
            down602.Contains("Registry.BeginBatch(); // F-M602", StringComparison.Ordinal)
            && down602.Contains("Registry.EndBatch(); // F-M602", StringComparison.Ordinal));

        // --- F-M603: ein angenommener Versuch ohne Kennung schliesst das Paar ------------------------
        var result603 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/UploadResult.cs"));
        var upload603 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M603: es gibt ein Ergebnis 'angenommen ohne Kennung' — SKIP, aber mit Beleg",
            result603.Contains("public bool SourceAccepted { get; private init; }", StringComparison.Ordinal)
            && result603.Contains("public static UploadResult AcceptedWithoutId(string reason)", StringComparison.Ordinal)
            && result603.Contains("SourceAccepted = true, Reason = reason, Verdict = UploadVerdict.AcceptedWithoutId", StringComparison.Ordinal));
        Check("F-M603: die Film-Adresse meldet 'angelegt, Id nicht lesbar' als angenommen",
            upload603.Contains("if (answer.Title.Settled)", StringComparison.Ordinal)
            && upload603.Contains("return UploadResult.AcceptedWithoutId(\"film-source-movie-\" + answer.Title.State);", StringComparison.Ordinal));
        Check("F-M603: die Serien-Adresse (tvst) genauso — derselbe Uploader, dieselbe Luecke",
            // F-M734: die Serie meldet ihren Beleg jetzt neutral aus dem Auftrag; die Marke heisst weiter so.
            upload603.Contains("if (answer.Title.Settled)", StringComparison.Ordinal)
            && upload603.Contains("return UploadResult.AcceptedWithoutId(\"second-source-show-created-id-unknown\");", StringComparison.Ordinal));
        Check("F-M603: die Marke wird geschrieben, BEVOR das Urteil faellt, und MarkOf laesst sie durch",
            upload603.Contains("if (result.SourceAccepted && result.Source is { } angenommeneQuelle)", StringComparison.Ordinal)
            && upload603.Contains("MarkAcceptedSource(", StringComparison.Ordinal)
            && upload603.Contains("(!ergebnis.Ok && !ergebnis.SourceAccepted)", StringComparison.Ordinal));
        Check("F-M603: die Statistik behauptet keinen Upload — das Ergebnis bleibt Ok=false",
            upload603.Contains("ACCEPTED {File} [{Lang}] — {Source} took the attempt without naming an id, the pair is settled (F-M603)", StringComparison.Ordinal));

        // --- F-M601: die Folgenzahl liegt im Store ---------------------------------------------------
        var upload601 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var store601 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/AiredSeasonStore.cs"));
        var ctx601 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"));
        var plugin601 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Plugin.cs"));
        Check("F-M601: der Store kann lesen, schreiben, anhaengen und decken",
            store601.Contains("public static class AiredSeasonStore", StringComparison.Ordinal)
            && store601.Contains("public static bool TryGet(string imdbId, int season, out IReadOnlyList<int> numbers)", StringComparison.Ordinal)
            && store601.Contains("public static void Put(string imdbId, int season, IReadOnlyList<int> numbers)", StringComparison.Ordinal)
            && store601.Contains("public static void Attach(ScribeSqliteContext? db)", StringComparison.Ordinal)
            && store601.Contains("public static bool Covers(IReadOnlyList<int> stored, IEnumerable<int> onDisk)", StringComparison.Ordinal));
        Check("F-M601: Tabellenname in Schema und Anlage ist DERSELBE",
            ctx601.Contains("CREATE TABLE IF NOT EXISTS aired_seasons (", StringComparison.Ordinal)
            && ctx601.Contains("e.ToTable(\"aired_seasons\");", StringComparison.Ordinal));
        Check("F-M601: erst der Store, dann IMDb/TMDb, dann einspeichern",
            upload601.Contains("if (AiredSeasonStore.TryGet(entry.Key.Item1, entry.Key.Item2, out var gespeichert)", StringComparison.Ordinal)
            && upload601.Contains("&& AiredSeasonStore.Covers(gespeichert, entry.Value)", StringComparison.Ordinal)
            && upload601.Contains("aired = await SeasonEpisodeIndex.AiredAsync(", StringComparison.Ordinal)
            && upload601.Contains("AiredSeasonStore.Put(entry.Key.Item1, entry.Key.Item2, aired);", StringComparison.Ordinal));
        Check("F-M601: der Store haengt an BEIDEN Stellen, an denen ein Kontext entsteht (F-M545, seit F-M616 ueber den Helfer)",
            plugin601.Contains("AiredSeasonStore.Attach(db);", StringComparison.Ordinal)
            && plugin601.Split("AttachStores(_sharedDbContext.Context, logger, DataFolderPath, Configuration.LogMode);").Length - 1 == 2);
        Check("F-M601: die Planungszeile nennt, wie viele Antworten aus dem Store kamen",
            upload601.Contains("({FromStore} from the store)", StringComparison.Ordinal)
            && upload601.Contains("airedAusStore++", StringComparison.Ordinal));

        // --- F-M600: die Pack-Phase fragt vorher, ob eine Folge ohnehin nicht beitreten kann ---------
        var ipack600 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/IPackFiller.cs"));
        var coord600 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        var upload600 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M600: der Pack-Filler kann vorab sagen, ob eine Folge nicht beitreten kann",
            ipack600.Contains("bool EpisodeCannotJoin(string imdbId, int season, int episode, string lang, bool hearingImpaired, out string reason);", StringComparison.Ordinal)
            && upload600.Contains("public bool EpisodeCannotJoin(string imdbId, int season, int episode, string lang, bool hearingImpaired, out string reason)", StringComparison.Ordinal));
        Check("F-M600: die Vorabfrage kostet keine Extraktion — Spurenliste und Store-Urteile",
            upload600.Contains("var positions = FindFillStreams(item, lang, hearingImpaired);", StringComparison.Ordinal)
            && upload600.Contains("SpurIstGesperrt(mediaHash, position, lang, hearingImpaired, isSeries: true, fuerPack: true)", StringComparison.Ordinal)
            && !upload600.Substring(upload600.IndexOf("public bool EpisodeCannotJoin(", StringComparison.Ordinal), 2600).Contains("ExtractAllAsync", StringComparison.Ordinal));
        Check("F-M600: die Staffel wird VOR der ersten Extraktion geprueft und der Grund genannt",
            coord600.Contains("season pack skipped before any extraction — {Imdb} S{Season} [{Lang}]: episode {Episode} cannot join ({Reason}), and {State} (F-M600).", StringComparison.Ordinal)
            && coord600.IndexOf("_filler.EpisodeCannotJoin(", StringComparison.Ordinal)
               < coord600.IndexOf("var group = new List<Candidate>(aired.Count);", StringComparison.Ordinal));
        Check("F-M600: das Urteil ist dasselbe wie vorher — es kommt nur frueher",
            upload600.Contains("reason = $\"every track of this language is already refused in the store ({gesperrt.Count})\"", StringComparison.Ordinal)
            && upload600.Contains("reason = \"no embedded text track for this language and variant\";", StringComparison.Ordinal)
            && upload600.Contains("return (null, \"every track of this language is already refused in the store\");", StringComparison.Ordinal));

        // --- F-M599: die Pack-Phase wird von innen vermessen ----------------------------------------
        var uploadSource599 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M599: die Pack-Planung nennt Bibliotheks-Walk, Folgenzahl-Abfragen und Datei-Sprachscan",
            uploadSource599.Contains("LAUF upload — {Count} item(s) collected and ordered after {Ms} ms (F-M598)", StringComparison.Ordinal)
            && uploadSource599.Contains("PACK — library walk {Count} item(s) in {Ms} ms (F-M599)", StringComparison.Ordinal)
            && uploadSource599.Contains("airedMs += paketTakt.ElapsedMilliseconds - airedVorher;", StringComparison.Ordinal)
            && uploadSource599.Contains("dateiMs += paketTakt.ElapsedMilliseconds - dateiVorher;", StringComparison.Ordinal));
        Check("F-M599: die Planung schliesst mit einer Summenzeile ab",
            uploadSource599.Contains("PACK — planning done: {Count} season(s), aired lookups {Calls} call(s) {Aired} ms ({FromStore} from the store), file language scans {Scans} {Datei} ms, total {Gesamt} ms (F-M601)", StringComparison.Ordinal));
        Check("F-M599: die Fuell-Phase (Netz) ist von der Planung getrennt gemessen",
            uploadSource599.Contains("var fuellTakt = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal)
            && uploadSource599.Contains("PACK — fill phase: {N} attempt(s) in {Ms} ms (F-M599)", StringComparison.Ordinal));
        Check("F-M599: die Pack-Zeilen liegen auf Detail",
            uploadSource599.Contains("LogUtil.Detail(_logger, \"[Sub Scribe] PACK — library walk", StringComparison.Ordinal)
            && uploadSource599.Contains("LogUtil.Detail(\n                _logger,\n                \"[Sub Scribe] PACK — fill phase", StringComparison.Ordinal));

        // --- F-M598: der ganze Lauf wird vermessen (Upload mit Pack-Phase, Download) ---------------
        var uploadSource598 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var downloadSource598 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check("F-M598: der Upload-Lauf nennt Item-Liste, Pack-Phase und Lauf-Ende mit Dauer",
            uploadSource598.Contains("var laufTakt = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal)
            && uploadSource598.Contains("LAUF upload — {Count} item(s) collected and ordered after {Ms} ms (F-M598)", StringComparison.Ordinal)
            && uploadSource598.Contains("LAUF upload — pack phase done after {Ms} ms (F-M598)", StringComparison.Ordinal)
            && uploadSource598.Contains("LAUF upload — done: item loop {Loop} ms, run total {Total} ms, {Items} item(s) (F-M598)", StringComparison.Ordinal));
        Check("F-M598: der Upload-Lauf vermisst auch jedes Item einzeln",
            uploadSource598.Contains("var itemTakt = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal)
            && uploadSource598.Contains("LAUF upload — item {Index}/{Count}", StringComparison.Ordinal)
            && uploadSource598.Contains("itemTakt.Restart();", StringComparison.Ordinal));
        Check("F-M598: der Download-Lauf hat dieselben Punkte",
            downloadSource598.Contains("var laufTakt = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal)
            && downloadSource598.Contains("LAUF download — {Count} item(s) collected after {Ms} ms (F-M598)", StringComparison.Ordinal)
            && downloadSource598.Contains("LAUF download — item {Index}/{Count}", StringComparison.Ordinal)
            && downloadSource598.Contains("LAUF download — done after {Ms} ms, {Items} item(s) (F-M598)", StringComparison.Ordinal));
        Check("F-M598: alle Lauf-Zeilen liegen auf Detail, nicht im Normalbetrieb",
            uploadSource598.Contains("LogUtil.Detail(_logger, \"[Sub Scribe] LAUF upload", StringComparison.Ordinal)
            && downloadSource598.Contains("LogUtil.Detail(_logger, \"[Sub Scribe-D] LAUF download", StringComparison.Ordinal)
            && !uploadSource598.Contains("LogUtil.Normal(_logger, \"[Sub Scribe] LAUF", StringComparison.Ordinal)
            && !downloadSource598.Contains("LogUtil.Normal(_logger, \"[Sub Scribe-D] LAUF", StringComparison.Ordinal));

        // --- F-M597: die Spur wird vermessen -------------------------------------------------------
        var uploadSource597 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M597: die Spur traegt eine Stoppuhr ueber den ganzen Weg",
            uploadSource597.Contains("var takt = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal)
            && uploadSource597.Contains("long qaMs = 0, quellenMs = 0, slotMs = 0, versandMs = 0;", StringComparison.Ordinal));
        Check("F-M597: jede der vier Phasen setzt ihre Marke",
            uploadSource597.Contains("qaMs = takt.ElapsedMilliseconds; // F-M597", StringComparison.Ordinal)
            && uploadSource597.Contains("quellenMs = takt.ElapsedMilliseconds; // F-M597", StringComparison.Ordinal)
            && uploadSource597.Contains("slotMs = takt.ElapsedMilliseconds; // F-M597", StringComparison.Ordinal)
            && uploadSource597.Contains("versandMs = takt.ElapsedMilliseconds; // F-M597", StringComparison.Ordinal));
        Check("F-M597: auch die Schleife zwischen zwei Spuren wird vermessen",
            uploadSource597.Contains("var abstand = System.Diagnostics.Stopwatch.StartNew();", StringComparison.Ordinal)
            && uploadSource597.Contains("long bisKette = abstand.ElapsedMilliseconds;", StringComparison.Ordinal)
            && uploadSource597.Contains("[Sub Scribe] TAKT-SPUR {Lang} — gap since the last track {Abstand} ms, of which the chain {Kette} ms (F-M597)", StringComparison.Ordinal)
            && uploadSource597.Contains("abstand.Restart();", StringComparison.Ordinal));
        Check("F-M597: die Zeile nennt alle vier Phasen und liegt auf Detail",
            uploadSource597.Contains("[Sub Scribe] TAKT {Name} [{Lang}] — qa {Qa} ms, sources {Quellen} ms, slot wait {Slot} ms, send {Versand} ms, total {Gesamt} ms (F-M597)", StringComparison.Ordinal)
            && uploadSource597.Contains("qaMs, quellenMs - qaMs, slotMs - quellenMs, versandMs - slotMs, takt.ElapsedMilliseconds", StringComparison.Ordinal)
            && uploadSource597.Contains("LogUtil.Detail(\n                _logger,\n                \"[Sub Scribe] TAKT", StringComparison.Ordinal));

        // --- F-M596: der Vorlauf (Extraktion der naechsten Datei) ---------------------------------
        var lookaheadSource596 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/ExtractionLookahead.cs"));
        var ffmpegSource596 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/FfmpegTools.cs"));
        var uploadSource596 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M596: der Vorlauf ist eine eigene Klasse mit Anmelden, Nehmen und Abraeumen",
            lookaheadSource596.Contains("public sealed class ExtractionLookahead : IDisposable", StringComparison.Ordinal)
            && lookaheadSource596.Contains("public void Plan(", StringComparison.Ordinal)
            && lookaheadSource596.Contains("public async Task<(Dictionary<int, string?> Texts, int ExitCode, bool FromLookahead)> TakeOrExtractAsync(", StringComparison.Ordinal)
            && lookaheadSource596.Contains("public void Dispose()", StringComparison.Ordinal));
        Check("F-M596: benutzt wird der Vorlauf nur bei DERSELBEN Datei und ALLEN Positionen",
            lookaheadSource596.Contains("_laeufe.TryGetValue(mediaPath, out var lauf)", StringComparison.Ordinal)
            && lookaheadSource596.Contains("positions.All(p => vorbereitetPositionen.Contains(p))", StringComparison.Ordinal));
        Check("F-M596: bis zu DREI Dateien im Voraus, nicht nur eine",
            lookaheadSource596.Contains("private const int MaxKandidaten = 3;", StringComparison.Ordinal)
            && lookaheadSource596.Contains(".Take(MaxKandidaten)", StringComparison.Ordinal));
        Check("F-M596: die Pumpe extrahiert NACHEINANDER — nie zwei ffmpeg gleichzeitig",
            lookaheadSource596.Contains("private async Task PumpeAsync(", StringComparison.Ordinal)
            && lookaheadSource596.Contains("await lauf.ConfigureAwait(false);", StringComparison.Ordinal)
            && lookaheadSource596.Contains("_plan.FirstOrDefault(p => !_laeufe.ContainsKey(p))", StringComparison.Ordinal));
        Check("F-M596: der Lauf meldet bis zu drei Kandidaten an und nimmt das Ergebnis am Extraktionsplatz",
            uploadSource596.Contains("using var vorlauf = new ExtractionLookahead();", StringComparison.Ordinal)
            && uploadSource596.Contains("for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)", StringComparison.Ordinal)
            && uploadSource596.Contains("await vorlauf", StringComparison.Ordinal)
            && uploadSource596.Contains("vorlauf.Plan(kandidaten, _ffmpegPath, _logger, _config, runCt);", StringComparison.Ordinal)
            && uploadSource596.Contains("kandidaten.Count < 3", StringComparison.Ordinal)
            && uploadSource596.Contains("QueueFilter != null && !QueueFilter.Contains(naechste.Id.ToString())", StringComparison.Ordinal));
        Check("F-M596: ein Abbruch beendet ffmpeg wirklich, nicht nur das Warten",
            ffmpegSource596.Contains("catch (OperationCanceledException)", StringComparison.Ordinal)
            && ffmpegSource596.Contains("BeendeHart(proc, logger, mediaPath);", StringComparison.Ordinal)
            && ffmpegSource596.Contains("proc.Kill(entireProcessTree: true);", StringComparison.Ordinal));

        // --- F-M595: die Statistik zaehlt nur die QA-Urteile --------------------------------------
        var deltaSource595 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/StatusCounterDelta.cs"));
        var uploadSource595 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var guiSource595 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        Check("F-M595: der Upload-Lauf fuehrt einen eigenen QA-Zaehler neben allen Ablehnungen",
            uploadSource595.Contains("public int RejectedQaFail { get; set; }", StringComparison.Ordinal)
            && uploadSource595.Contains("public int RejectedCandidates { get; set; }", StringComparison.Ordinal));
        Check("F-M595: nur das Urteil der Qualitaets-Gates setzt qaFail",
            uploadSource595.Contains("out bool qaFail)", StringComparison.Ordinal)
            && uploadSource595.Contains("qaFail = true;", StringComparison.Ordinal)
            && uploadSource595.Contains("if (qaFail)", StringComparison.Ordinal)
            && uploadSource595.Split("qaFail = true;").Length - 1 == 1);
        Check("F-M595: die Statistik liest den QA-Zaehler, nicht alle Ablehnungen",
            deltaSource595.Contains("rejectedUpload: uploadDry ? 0 : upload?.RejectedQaFail ?? 0", StringComparison.Ordinal)
            && !deltaSource595.Contains("upload?.RejectedCandidates", StringComparison.Ordinal));
        Check("F-M595: die Zeile heisst \"Uploads: QA fails not sent\"",
            guiSource595.Contains("['Uploads: QA fails not sent', s.RejectedUpload || 0]", StringComparison.Ordinal)
            && !guiSource595.Contains("Uploads: stopped before sending", StringComparison.Ordinal));

        // --- F-M594: der Takt als Slot vor dem Transfer -------------------------------------------
        var limiterSource594 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Api/GlobalRateLimiter.cs"));
        var upSource594 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var downSource594 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check("F-M594: der Limiter haelt einen Zeitanker und bietet Slot und Marke an",
            limiterSource594.Contains("private static DateTime _lastTransferDoneUtc = DateTime.MinValue;", StringComparison.Ordinal)
            && limiterSource594.Contains("public async Task WaitForTransferSlotAsync(CancellationToken ct)", StringComparison.Ordinal)
            && limiterSource594.Contains("public void MarkTransferDone()", StringComparison.Ordinal));
        Check("F-M594: gewartet wird nur die DIFFERENZ zum Ziel, in der Vergangenheit gar nicht",
            limiterSource594.Contains("TimeSpan warten = anchor.AddMilliseconds(interval) - DateTime.UtcNow;", StringComparison.Ordinal)
            && limiterSource594.Contains("if (warten <= TimeSpan.Zero)", StringComparison.Ordinal)
            && limiterSource594.Contains("await Task.Delay(warten, ct).ConfigureAwait(false);", StringComparison.Ordinal));
        Check("F-M594: der Upload belegt den Slot vor dem Versand und setzt die Marke nur nach einer Annahme",
            upSource594.Contains("await _limiter.WaitForTransferSlotAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal)
            && upSource594.Contains("if (jeQuelle.Any(r => r.Ok))", StringComparison.Ordinal)
            && upSource594.Contains("_limiter.MarkTransferDone();", StringComparison.Ordinal));
        Check("F-M594: der Download belegt den Slot vor dem Abruf und setzt die Marke erst bei Bytes",
            downSource594.Contains("await _limiter.WaitForTransferSlotAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal)
            && downSource594.Contains("if (rawAttempt.Bytes is { Length: >= 100 })", StringComparison.Ordinal)
            && downSource594.Contains("_limiter.MarkTransferDone();", StringComparison.Ordinal));

        // --- F-M592: Abruf und Meldungen, tote Quellen --------------------------------------------
        var downloadSource592 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check("F-M592: der Datei-Abruf faellt bei Timeout auf den naechsten Kandidaten zurueck",
            downloadSource592.Contains("did not answer while delivering the file (client timeout); trying the next candidate (F-M592)", StringComparison.Ordinal)
            && downloadSource592.Contains("attempt = await DownloadCandidateAsync(cand, season, episode, ct).ConfigureAwait(false);", StringComparison.Ordinal));
        Check("F-M592: eine ausgefallene Quelle bleibt fuer den Rest des Laufs draussen",
            downloadSource592.Contains("private readonly HashSet<Configuration.SubtitleProvider> _quellenTot = new();", StringComparison.Ordinal)
            && downloadSource592.Contains("private void MerkeQuelleTot(Configuration.SubtitleProvider quelle)", StringComparison.Ordinal)
            && downloadSource592.Split("MerkeQuelleTot(").Length - 1 >= 4
            && downloadSource592.Contains("candidate from {Source} skipped: that source stopped answering in this run (F-M592)", StringComparison.Ordinal));
        Check("F-M592: erst wenn keine Quelle mehr uebrig ist, endet der Lauf — offene Meldungen werden zurueckgestellt",
            downloadSource592.Contains("no download source is left — every one of them stopped answering; stopping the run early (F-M592)", StringComparison.Ordinal)
            && downloadSource592.Contains("no answer from the API (client timeout) — deferred to the next run (F-M592)", StringComparison.Ordinal)
            && downloadSource592.Contains("report flush stopped on a client timeout — the open reports stay due for the next run (F-M592)", StringComparison.Ordinal));

        // --- F-M591: derselbe Griff im Download --------------------------------------------------
        var downloadSource591 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/DownloadPipeline.cs"));
        Check("F-M591: der Filmweg des Downloads ist gegen einen Timeout abgesichert",
            downloadSource591.Contains("the film record did not answer (client timeout)", StringComparison.Ordinal)
            && downloadSource591.Contains("_letzteFrageOhneAntwort = true;", StringComparison.Ordinal));
        Check("F-M591: Suchversuch und Hauptsuche fallen bei Timeout auf die naechste Quelle zurueck",
            downloadSource591.Contains("no answer (client timeout) — the run keeps going with the other sources (F-M591)", StringComparison.Ordinal)
            && downloadSource591.Contains("no answer from the main search (client timeout) — asking the other sources (F-M591)", StringComparison.Ordinal)
            && downloadSource591.Split("catch (OperationCanceledException) when (!ct.IsCancellationRequested)").Length - 1 >= 3);
        Check("F-M591: erst drei Artikel ohne Antwort irgendeiner Quelle beenden den Lauf",
            downloadSource591.Contains("private bool _stopRunNoSources;", StringComparison.Ordinal)
            && downloadSource591.Contains("if (_quellenAusfaelleInFolge >= 3)", StringComparison.Ordinal)
            && downloadSource591.Contains("if (_stopRunNoSources)", StringComparison.Ordinal)
            && downloadSource591.Contains("nothing was searched, the items stay due for the next run (F-M591)", StringComparison.Ordinal));

        // --- F-M590: sofort notieren, Timeout je Quelle ------------------------------------------
        var uploadSource590 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M590: die Marke einer Quelle wird unmittelbar nach ihrer Antwort geschrieben",
            uploadSource590.Contains("private void MarkSourceUploadNow(", StringComparison.Ordinal)
            && uploadSource590.Contains("lock (_sofortMarkeLock)", StringComparison.Ordinal)
            && uploadSource590.Contains("MarkSourceUploadNow(quelle, versand.SourceId, mediaHash, subPos, lang,", StringComparison.Ordinal)
            && uploadSource590.Contains("the other source cannot take the record with it any more (F-M590)", StringComparison.Ordinal));
        Check("F-M590: ein Timeout ohne gesetztes ct ist ein Fehlschlag dieser Quelle, kein Lauf-Abbruch",
            uploadSource590.Contains("catch (OperationCanceledException) when (!ct.IsCancellationRequested)", StringComparison.Ordinal)
            // F-M609: ein Timeout ist kein Urteil der Quelle mehr — NoVerdict statt Failed.
            && uploadSource590.Contains("return UploadResult.NoVerdict(\"timeout\");", StringComparison.Ordinal)
            && uploadSource590.Contains("the other source keeps going.", StringComparison.Ordinal)
            && uploadSource590.Contains("// Ein echter Abbruch (Nutzer-Stop, Watchdog, Neustart) — der gilt fuer den ganzen Lauf.", StringComparison.Ordinal));
        Check("F-M590: erst drei Spuren ohne Antwort irgendeiner Quelle beenden den Lauf",
            uploadSource590.Contains("int _quellenAusfaelleInFolge;", StringComparison.Ordinal)
            && uploadSource590.Contains("bool alleImTimeout = jeQuelle.All(r => !r.Ok && r.Reason == \"timeout\");", StringComparison.Ordinal)
            && uploadSource590.Contains("if (_quellenAusfaelleInFolge >= 3)", StringComparison.Ordinal)
            && uploadSource590.Contains("bool alleQuellenWeg = false;", StringComparison.Ordinal));

        // --- F-M589: die Pause haengt an der Spur, nicht an der Datei ----------------------------
        var uploadSource589 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        Check("F-M589: das Pausen-Flag wird je Kandidat gesetzt, nicht je Datei",
            uploadSource589.Contains("bool dieseSpurHochgeladen = false;", StringComparison.Ordinal)
            && uploadSource589.Contains("dieseSpurHochgeladen = true; // F-M589", StringComparison.Ordinal));
        Check("F-M589: das Datei-Flag bleibt fuer die Laufzaehlung erhalten, traegt aber keine Pause mehr",
            uploadSource589.Contains("itemUploadedThisRun = true;", StringComparison.Ordinal)
            && uploadSource589.Contains("summary.FilesUploaded++;", StringComparison.Ordinal)
            && !uploadSource589.Contains("if (itemUploadedThisRun)\n                {\n                    await Task.Delay(", StringComparison.Ordinal));
        Check("F-M589/F-M594: der Takt steht als EIN Slot vor dem Versand, keine Pause mehr danach",
            uploadSource589.Split("await Task.Delay(_limiter.TransferPauseMs(), runCt)").Length - 1 == 0
            && uploadSource589.Split("await _limiter.WaitForTransferSlotAsync(ct)").Length - 1 == 1
            && uploadSource589.Contains("_limiter.MarkTransferDone();", StringComparison.Ordinal));

        // --- F-M588: die SubDL-Id einmal am Laufende ----------------------------------------------
        var uploadSource588 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var registrySource588 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        Check("F-M588: der Upload merkt die Spur vor, statt die Id sofort zu holen",
            uploadSource588.Contains("private sealed record PendingSubdlId(", StringComparison.Ordinal)
            && uploadSource588.Contains("private readonly List<PendingSubdlId> _offeneSubdlIds = new();", StringComparison.Ordinal)
            && uploadSource588.Contains("_offeneSubdlIds.Add(new PendingSubdlId(", StringComparison.Ordinal)
            && !uploadSource588.Contains("uploadId = await ResolveOwnUploadIdAsync(imdbId, season, episode, lang, releaseName, loosePath, ct)", StringComparison.Ordinal));
        Check("F-M588: EIN Listenabruf am Laufende traegt die Ids nach, jeden Eintrag genau einmal",
            uploadSource588.Contains("await ResolveDeferredUploadIdsAsync(runCt)", StringComparison.Ordinal)
            && uploadSource588.Contains("private async Task ResolveDeferredUploadIdsAsync(CancellationToken ct)", StringComparison.Ordinal)
            // F-M732: die Zeile kommt jetzt aus der Konto-Pflege — der Griff heisst `Handle` (neutral),
            // die Zeit `CreatedAtUtc`. Die Aussage bleibt: jeder Eintrag wird genau einmal vergeben.
            && uploadSource588.Contains("benutzt.Add(treffer.Handle);", StringComparison.Ordinal)
            && uploadSource588.Contains(".OrderBy(e => e.CreatedAtUtc)", StringComparison.Ordinal)
            && uploadSource588.Contains("deferred upload ids: {Hit} of {Open} assigned with one list fetch (F-M588)", StringComparison.Ordinal));
        Check("F-M588: die Id landet ueber den Marken-Schluessel in der Zeile, und Freigegebenes kommt aus der veroeffentlichten Liste",
            // F-M670: nachgetragen wird sie an der MARKEN-Zeile (`SetDeferredUploadId`), nicht mehr an
            // einer Id-Spalte der Spur — der Schluessel ist derselbe wie beim Upload.
            registrySource588.Contains("public int SetDeferredSubdlId(", StringComparison.Ordinal)
            && registrySource588.Contains("SourceStore.SetDeferredUploadId(", StringComparison.Ordinal)
            && uploadSource588.Contains("Registry.SetDeferredSubdlId(", StringComparison.Ordinal)
            && uploadSource588.Contains("new Sources.SourceSubmissionQuery(Published: true, MaxPages: 2)", StringComparison.Ordinal)
            && uploadSource588.Contains("their singles stay beside a pack that replaces them", StringComparison.Ordinal));

        // --- F-M587: Quellen gleichzeitig, mySubtitles nur Seite 1 --------------------------------
        var uploadSource587 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var packCoordSource587 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check("F-M587: die Kette sammelt die zustaendigen Quellen und startet sie gleichzeitig statt nacheinander",
            uploadSource587.Contains("zustaendig.Add(quelle);", StringComparison.Ordinal)
            && uploadSource587.Contains("Task.WhenAll(laeufe)", StringComparison.Ordinal)
            && !uploadSource587.Contains("ergebnisse[quelle] = await SendToSourceAsync(", StringComparison.Ordinal));
        Check("F-M587: die zweite Quelle startet mit 0-5 % Versatz des Transfertakts, kein Gleichstart",
            uploadSource587.Contains("int versatzMs = Math.Max(1, (int)(_limiter.TransferPauseMs() * 0.05));", StringComparison.Ordinal)
            && uploadSource587.Contains("await Task.Delay(Random.Shared.Next(1, versatzMs + 1), ct)", StringComparison.Ordinal)
            && uploadSource587.Contains("if (index > 0)", StringComparison.Ordinal));
        Check("F-M587: mySubtitles wird nur noch von Seite 1 gelesen, und ein Quellenfehler wird zurueckgegeben statt geworfen",
            // F-M732: gelesen wird ueber die Konto-Pflege; „nur Seite 1" heisst jetzt MaxPages: 1 in der Frage.
            uploadSource587.Contains("new Sources.SourceSubmissionQuery(MaxPages: 1)", StringComparison.Ordinal)
            && !uploadSource587.Contains("new Sources.SourceSubmissionQuery(MaxPages: 2)", StringComparison.Ordinal)
            && packCoordSource587.Contains("new Sources.SourceSubmissionQuery(MaxPages: 1)", StringComparison.Ordinal)
            && uploadSource587.Contains("the other source is still tried.", StringComparison.Ordinal)
            // F-M609: die Ausnahme ist keine Antwort — NoVerdict.
            && uploadSource587.Contains("return UploadResult.NoVerdict(ex.Message);", StringComparison.Ordinal));

        // --- F-M586: eine Quelle ohne diese Sprache zaehlt nicht als offen -------------------------
        var uploadSource586 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var registrySource586 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        var entitiesSource586 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Data/Entities.cs"));
        Check("F-M586: NimmtSprache ist die eine Stelle fuer Sprache x Quelle, und die Quelle ohne Sprache faellt aus der offenen Liste",
            uploadSource586.Contains("public static bool NimmtSprache(Configuration.SubtitleProvider quelle, string? language, bool? isSeries)", StringComparison.Ordinal)
            && uploadSource586.Contains(
                "Api.Endpoints.SourceOrder.KeyOf(Configuration.SubtitleProvider.TvSubtitles), language)", StringComparison.Ordinal)
            && uploadSource586.Contains(
                "Api.Endpoints.SourceOrder.KeyOf(Configuration.SubtitleProvider.MovieSubtitles), language)", StringComparison.Ordinal)
            && uploadSource586.Contains("&& isSeries != false,", StringComparison.Ordinal)
            && uploadSource586.Contains("&& isSeries != true,", StringComparison.Ordinal)
            && uploadSource586.Contains("bool nurSpracheAusgeschlossen = quellen.Count < alleQuellen.Count", StringComparison.Ordinal)
            && uploadSource586.Contains("alsoTakes: key => Api.Endpoints.Provenance.Provider(key) is { } quelle", StringComparison.Ordinal)
            && uploadSource586.Contains("&& NimmtSprache(quelle, language, isSeries)", StringComparison.Ordinal));
        Check("F-M586: faellt die letzte offene Quelle nur wegen ihrer Sprachliste weg, traegt das Urteil den Grund unsupported-language",
            entitiesSource586.Contains("public const string UnsupportedLanguage = \"unsupported-language\"", StringComparison.Ordinal)
            && uploadSource586.Contains("if (offen.Count == 0 && nurSpracheAusgeschlossen)", StringComparison.Ordinal)
            && uploadSource586.Contains("reason = RejectReason.UnsupportedLanguage;", StringComparison.Ordinal));
        Check("F-M586: PairIsDecided schliesst das Paar auf ein Sprachurteil",
            registrySource586.Contains("x.Reason == RejectReason.UnsupportedLanguage", StringComparison.Ordinal)
            && registrySource586.Contains("return spracheNirgends", StringComparison.Ordinal));

        // --- F-M585: Pack und Einzel schliessen sich nicht mehr aus -------------------------------
        var uploadSource584 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
        var packCoordSource585 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        Check("F-M585: kein Loeschaufruf mehr im Pack-Pfad, und keine Pack-Id auf den Mitgliedszeilen",
            !packCoordSource585.Contains("DeletePublishedSubtitleAsync(single.Id", StringComparison.Ordinal)
            && !packCoordSource585.Contains("subdlId: packId", StringComparison.Ordinal)
            && packCoordSource585.Contains("stay up as singles beside the pack", StringComparison.Ordinal));
        Check("F-M585: der Einzelpfad ueberspringt keine Pack-Position mehr, und der Pack-Beleg kommt aus der packs-Zeile",
            uploadSource584.Contains("sent as a single beside it (F-M585", StringComparison.Ordinal)
            && uploadSource584.Contains("private bool CoveredByPack(string? imdbId, int season, string language, bool hearingImpaired, int episode)", StringComparison.Ordinal)
            && uploadSource584.Contains("return pack.Episodes", StringComparison.Ordinal)
            && !uploadSource584.Contains("return UploadResult.Skipped(\"pack-carries-it\")", StringComparison.Ordinal));
        var configPage585 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Configuration/configPage.html"));
        // F-M719: das Kaestchen "Upload season packs" ist entfallen — die Pruefung haelt fest, dass es
        // nicht zurueckkommt (der Schalter "Packs" der Quellenkarte deckt beide Richtungen ab).
        Check("F-M719: die Upload-Seite fuehrt kein Kaestchen 'Upload season packs' mehr",
            !configPage585.Contains("id=\"UploadSeasonPacks\"", StringComparison.Ordinal)
            && !configPage585.Contains("<span>Upload season packs</span>", StringComparison.Ordinal));

        // --- F-M584: eine Ablehnung gilt je Quelle -----------------------------------------------
        Check("F-M584: ein Selbst-Echo sperrt die Spur nur, solange keine Quelle das Paar mehr verlangt",
            uploadSource584.Contains("private bool SpurIstGesperrt(", StringComparison.Ordinal)
            && uploadSource584.Contains("bool fuerPack = false)", StringComparison.Ordinal)
            && uploadSource584.Contains("if (reason == RejectReason.SelfEcho", StringComparison.Ordinal)
            && uploadSource584.Contains("&& AnyUploadSourceStillWants(mediaHash, language, hearingImpaired, isSeries, position))", StringComparison.Ordinal)
            && uploadSource584.Contains("return true;", StringComparison.Ordinal));
        Check("F-M584: der Pack-Fueller und der Kopf-Ausstieg fragen je Quelle",
            uploadSource584.Contains(".Where(position => SpurIstGesperrt(fillMediaHash, position, lang, hearingImpaired, isSeries: true, fuerPack: true))", StringComparison.Ordinal)
            && uploadSource584.Contains("bool nochEinPaarOffen = Registry.RecordedPairsByHash(mediaHash)", StringComparison.Ordinal)
            && uploadSource584.Contains("if (textPositions.Count > 0 && !nochEinPaarOffen", StringComparison.Ordinal));

        // --- F-M583: Urteile ohne Beleg werden verworfen ---------------------------------------
        var registrySource580 = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        var refreshSource = ReadText(Path.Combine(root2, "Jellyfin.Plugin.SubdlSync/ScheduledTasks/ScribeDatabaseRefreshTask.cs"));
        Check("F-M583: nur Urteile ohne Marke und ohne bekannte Herkunft werden verworfen — eingebettet und Sidecar",
            registrySource580.Contains("public int ClearVerdictsWithoutEvidence()", StringComparison.Ordinal)
            // F-M668: „Marke" heißt jetzt „irgendeine Quelle hat das Paar in der Gridfilm geschlossen".
            && registrySource580.Contains("bool marke = AnySourceClosed(mediaHash, language, hearingImpaired ?? false);", StringComparison.Ordinal)
            && registrySource580.Contains("return !marke && herkunft.Origin is null;", StringComparison.Ordinal)
            // F-M671: eine Spur hat keinen Einzelschluessel mehr (er ist (FileId, TrackId)) — das
            // Urteil faellt ueber das Objekt, nicht ueber eine Id.
            && registrySource580.Contains("_db.Tracks.Remove(zeile);", StringComparison.Ordinal)
            && registrySource580.Contains("_db.Sidecars.Remove(zeile);", StringComparison.Ordinal));
        Check("F-M583: der Database Refresh setzt zurueck und meldet die Zahl",
            refreshSource.Contains("removedUnbackedVerdicts = registry.ClearVerdictsWithoutEvidence();", StringComparison.Ordinal)
            && refreshSource.Contains("{Unbacked} verdict(s) without evidence (F-M583)", StringComparison.Ordinal)
            && refreshSource.Contains("removedUnbackedVerdicts,", StringComparison.Ordinal)
            // F-M622: die Zahl muss auch in der Summe der Worker-Zeile landen, sonst stehen zwei
            // Zahlen fuer einen Lauf nebeneinander (19.11.2026: 532 in der Zeile, 968 im Log).
            && refreshSource.Contains("+ removedUnbackedVerdicts;", StringComparison.Ordinal));

        // --- F-M582: "duplicate" gilt je Quelle -----------------------------------------------
        Check("F-M582: ohne Quellenbeleg faellt der Kandidat nicht weg — die Kette bekommt ihn",
            uploadSource.Contains("// F-M582 (Operator-Anweisung 17.11.2026", StringComparison.Ordinal)
            && uploadSource.Contains("if (darfNoch.Count > 0)", StringComparison.Ordinal)
            && uploadSource.Contains("return (ContentSettle.DueForAnotherSource, string.Join(\", \", darfNoch.Select(Api.Endpoints.Provenance.Name)));", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.GetOrigin(contentHash).Origin;", StringComparison.Ordinal));
        Check("F-M582: ein Inhalts-Urteil schliesst das Paar nur, wenn der Text nirgends mehr hoch darf",
            registrySource580.Contains("bool DarfNirgendsHoch(string? contentHash) =>", StringComparison.Ordinal)
            && registrySource580.Contains("&& DarfNirgendsHoch(x.ContentHash))", StringComparison.Ordinal)
            && registrySource580.Contains("GetOrigin(contentHash).Origin is not null;", StringComparison.Ordinal));

        // --- F-M581: die Paar-Frage prueft den Beleg, nicht die Umstaende ----------------------
        var paarStart = uploadSource.IndexOf("private bool AnyUploadSourceStillWants(", StringComparison.Ordinal);
        var paarEnde = uploadSource.IndexOf("private bool IrgendeineQuelleHat(", StringComparison.Ordinal);
        var paarFrage = paarStart >= 0 && paarEnde > paarStart ? uploadSource[paarStart..paarEnde] : string.Empty;
        Check("F-M581: die Paar-Frage fragt keinen Zustand des Laufs — weder Queue noch Sprachset der Seite",
            paarFrage.Length > 0
            && !paarFrage.Contains("QueuedForSource(", StringComparison.Ordinal)
            && !paarFrage.Contains("SiteLanguage(", StringComparison.Ordinal)
            // F-M669: die Paar-Frage reicht eine (optionale) Position durch und liest die Marke ueber
            // EINE Stelle — mit Position die Position, ohne die des Paares.
            && paarFrage.Contains("alreadyThere: key => Api.Endpoints.Provenance.Provider(key) is { } quelle", StringComparison.Ordinal)
            && paarFrage.Contains("&& Marke(quelle, mediaHash, language, hearingImpaired, subPos)", StringComparison.Ordinal));
        Check("F-M581: ein Selbst-Echo-Urteil braucht einen Beleg — sonst bleibt die Spur ohne Urteil offen",
            uploadSource.Contains("private bool IrgendeineQuelleHat(", StringComparison.Ordinal)
            && uploadSource.Contains("if (IrgendeineQuelleHat(mediaHash, lang, streamHi, isSeries, subPos))", StringComparison.Ordinal)
            && uploadSource.Contains("no source asks for this pair right now, no verdict written (F-M581)", StringComparison.Ordinal)
            && uploadSource.Contains("if (IrgendeineQuelleHat(mediaHash, cand.Lang, cand.HearingImpaired, isSeries,", StringComparison.Ordinal));

        // --- F-M580: ein gespeichertes Urteil schliesst das Paar ------------------------------
        Check("F-M580: ein gespeichertes Inhalts-Urteil schliesst das Paar — eingebettet und als Sidecar",
            registrySource580.Contains("public bool PairIsDecided(string? mediaHash, string language, bool hearingImpaired)", StringComparison.Ordinal)
            && registrySource580.Contains("&& x.Reason == RejectReason.DuplicateContent", StringComparison.Ordinal)
            && registrySource580.Contains("|| GetSidecars(mediaHash).Any(x => x.Language == language", StringComparison.Ordinal));
        Check("F-M580: die Paar-Frage fragt das Urteil VOR dem Quellentor, und ein gespeicherter Spruch zaehlt nicht erneut",
            uploadSource.Contains("!Registry.PairIsDecided(mediaHash, language, hearingImpaired)", StringComparison.Ordinal)
            && uploadSource.IndexOf("!Registry.PairIsDecided(mediaHash, language, hearingImpaired)", StringComparison.Ordinal)
                < uploadSource.IndexOf("&& Api.Endpoints.UploadGate.Decide(", StringComparison.Ordinal)
            && uploadSource.Contains("// F-M580: gespeichertes Urteil — keine neue Ablehnung, also kein neuer Zaehler.", StringComparison.Ordinal)
            && uploadSource.Contains("// F-M580: der Spruch steht schon im Store", StringComparison.Ordinal));

        // --- F-M573: die billigste Staffelfrage zuerst -------------------------------------------
        Check("F-M573: eine vollstaendig gepackte Staffel kostet keine TMDb-Frage und keine Datei",
            uploadSource.Contains("var staffelBekannt = LanguagesOf(entry.Key.Item1, entry.Key.Item2);", StringComparison.Ordinal)
            && uploadSource.Contains("all {Count} known pair(s) already up as a pack for {Sources}, no TMDb call, no file read (F-M654)", StringComparison.Ordinal)
            && uploadSource.IndexOf("staffelBekannt.All(", StringComparison.Ordinal)
                < uploadSource.IndexOf("SeasonEpisodeIndex.AiredAsync", StringComparison.Ordinal));

        // --- F-M571: Pack-Paare einer Staffel in EINER Frage -------------------------------------
        Check("F-M571: der Pack-Planer fragt die Staffel einmal, nicht je Paar",
            reg557.Contains("public HashSet<(string Language, bool HearingImpaired)> PacksInSeason(", StringComparison.Ordinal)
            && reg557.Contains("_db.Packs.FindAll()", StringComparison.Ordinal)
            && uploadSource.Contains("var gepackt = PacksOfAllSlotSources(entry.Key.Item1, entry.Key.Item2);", StringComparison.Ordinal)
            && uploadSource.Contains("private HashSet<(string Language, bool HearingImpaired)> PacksOf(string imdbId, int season)", StringComparison.Ordinal)
            && uploadSource.Contains("private HashSet<(string Language, bool HearingImpaired)> PacksOfAllSlotSources(string imdbId, int season)", StringComparison.Ordinal));

        // --- F-M556: der Kopf kennt auch die losen Seitenwagen ---------------------------------------
        // Befund 16.11.2026: 292 Wegwuerfe bei 0 Uploads im 21:28-Zyklus. Der Einreiher reihte Items ein,
        // deren eingebettete Spuren fertig waren, aber ein Seitenwagen noch offen; der Kopf sah nur die
        // eingebetteten Spuren und warf sie weg — der Seitenwagen wurde nie gesendet.
        Check("F-M556: die Seitenwagen-Regel steht EINMAL und wird von beiden Stellen gerufen",
            uploadSource.Contains("public static bool SidecarsNeedWorkFor(", StringComparison.Ordinal)
            && seederSource.Contains("return Pipeline.UploadPipeline.SidecarsNeedWorkFor(", StringComparison.Ordinal)
            && !seederSource.Contains("if (registry.IsSidecarUploadedTo(looseHash, quelle) || registry.SidecarRejectedReason(looseHash) != null)", StringComparison.Ordinal));
        Check("F-M556: der Kopf ueberspringt nur, wenn auch kein Seitenwagen offen ist",
            uploadSource.Contains("bool seitenwagenOffen = loseKandidaten.Count > 0", StringComparison.Ordinal)
            && uploadSource.Contains("&& SidecarsNeedWorkFor(loseKandidaten, earlyHash, quelle, Registry)", StringComparison.Ordinal)
            && uploadSource.Contains("if (!seitenwagenOffen)", StringComparison.Ordinal)
            && uploadSource.Contains("loose sidecar(s) still need a source (F-M556)", StringComparison.Ordinal));

        // --- F-M555: jede TMDb-Antwort wird an EINER Stelle abgelegt und von dort beantwortet --------
        // Befund 16.11.2026 ("Die tmdb requests sind komisch. Elle ist laengst durchgelaufen"):
        // 1221 Anfragen, 305 verschiedene Adressen, 916 Wiederholungen, der id test 2173x fuer 183
        // Titel. Der Speicher half nur an 9 von 13 Sendestellen.
        string registratorSource = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "PluginServiceRegistrator.cs")).ConfigureAwait(false);
        string? cacheHandler = null;
        try
        {
            cacheHandler = await File.ReadAllTextAsync(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "TmdbCacheHandler.cs")).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
        }

        Check("F-M555: ein Handler in der Kette beantwortet jede TMDb-Adresse aus dem Speicher",
            cacheHandler != null
            && cacheHandler.Contains("public sealed class TmdbCacheHandler : DelegatingHandler", StringComparison.Ordinal)
            // F-M773: seit 12.3.8.72 liegt die Rohantwort in der eigenen Ablage, nicht mehr unter den
            // Antworten des Resolvers.
            && cacheHandler.Contains("TmdbCache.TryGetBody(key, out string? stored)", StringComparison.Ordinal)
            && cacheHandler.Contains("TmdbCache.PutBody(key, body)", StringComparison.Ordinal));
        Check("F-M555: der Handler fasst nur TMDb an, und der Schluessel traegt keinen api_key",
            cacheHandler != null
            && cacheHandler.Contains("private const string TmdbHost = \"api.themoviedb.org\";", StringComparison.Ordinal)
            && cacheHandler.Contains("!string.Equals(request.RequestUri.Host, TmdbHost, StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal)
            && cacheHandler.Contains("part.StartsWith(\"api_key=\", StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal)
            && cacheHandler.Contains("private const string KeyPrefix = \"url:\";", StringComparison.Ordinal));
        Check("F-M555: er sitzt in BEIDEN HTTP-Ketten (sonst hilft er nur der Haelfte der Aufrufe)",
            registratorSource.Contains("var chain = new Api.TmdbCacheHandler(retry);", StringComparison.Ordinal)
            && CountOccurrences(registratorSource, "var chain = new Api.TmdbCacheHandler(retry);") == 2
            && CountOccurrences(registratorSource, "new HttpClient(chain)") + CountOccurrences(registratorSource, "new System.Net.Http.HttpClient(chain)") == 2);

        // --- F-M554: der Kopf urteilt ueber die SPUREN DER DATEI, nicht ueber die Zeilen des Stores ----
        // Befund 16.11.2026, 21:09-Zyklus: 285 Dateien mit "upload file-complete for every upload
        // source" und 0 Uploads. Der Store kannte nur einen Teil der Spuren (alte Zeilen), alle
        // bekannten waren erledigt — die Datei galt als fertig, obwohl die meisten Spuren nie
        // angesehen wurden. Der Einreiher fragte die echten Positionen und sagte "offen".
        Check("F-M554: der Kopf gibt die echten Spuren der Datei mit",
            uploadSource.Contains("var kopfAlleSubs = _mediaSourceManager.GetMediaStreams(item.Id)?", StringComparison.Ordinal)
            && uploadSource.Contains(".Where(Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsDialogueStream)", StringComparison.Ordinal)
            && uploadSource.Contains("var kopfPositionen = kopfAlleSubs", StringComparison.Ordinal));
        Check("F-M554: eine Datei gilt nur als fertig, wenn JEDE vorhandene Spur fuer diese Quelle erledigt ist",
            registrySource.Contains("public bool IsSubtitlesUploadedFor(string? mediaHash, SubtitleProvider provider, IEnumerable<int> requiredPositions)", StringComparison.Ordinal)
            && registrySource.Contains("return need.All(p => IsEmbedTerminalFor(mediaHash, p, provider));", StringComparison.Ordinal)
            && registrySource.Contains("if (need.Count == 0)", StringComparison.Ordinal));

        Check("das quellenweise Gegenstueck der Dateimarke existiert und filtert auf die Quelle (F-M499)",
            registrySource.Contains(
                "public bool IsSubtitlesUploadedFor(string? mediaHash, SubtitleProvider provider)",
                StringComparison.Ordinal)
            && registrySource.Contains(
                "tracks.All(track => IsEmbedTerminalFor(mediaHash, track.SubPos, provider))",
                StringComparison.Ordinal)
            // dieselbe Leseregel wie im Upload selbst: eigene Marke der Quelle, Ablehnung quellenneutral
            // F-M669: die Spur fragt IHRE Position — eine Marke je Position, nicht je Paar.
            && registrySource.Contains(
                "return IsUploadedToPosition(mediaHash, track.SubPos, track.Language, track.HearingImpaired ?? false, provider);",
                StringComparison.Ordinal));

        // --- Abschnitt 19: auch das SETTLE-Urteil fragt je Quelle (F-M500) ----------------------
        // Gemessen 15.11.2026, 16:24 im scharfen Lauf: 27 eingebettete FR-Spuren endeten als
        // "identical subtitle already uploaded earlier (F-M298)", weil der Inhalt bei SubDL lag —
        // tvsubtitles bekam nie einen Versuch, der Lauf meldete "0 uploaded | 34 rejected". Das Urteil
        // "schon hochgeladen" las Status=Uploaded und damit "irgendwo hochgeladen".
        Check("das Settle-Urteil nimmt die Quelle mit — vier Stellen, EINE Regel (F-M500)",
            registrySource.Contains("public bool IsContentUploadedFor(string? contentHash, SubtitleProvider provider)", StringComparison.Ordinal)
            && registrySource.Contains("public bool RecordSkippedContent(string? mediaHash, bool isLoose, int subPos, string lang,", StringComparison.Ordinal)
            && registrySource.Contains("IEnumerable<SubtitleProvider> settledSources,", StringComparison.Ordinal)
            && registrySource.Contains("Where(quelle => IsContentUploadedFor(contentHash, quelle))", StringComparison.Ordinal)
            && uploadSource.Contains("private (ContentSettle Verdict, string Open) SettleDuplicateContent(", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(uploadSource, @"SettleDuplicateContent\(").Count >= 5
            // die Zeile wird nur noch aus der EINEN Regel heraus geschrieben (zwei Aufrufe: Ablehnung/Settle)
            && System.Text.RegularExpressions.Regex.Matches(uploadSource, @"Registry\.RecordSkippedContent\(").Count == 2);

        Check("nur der Quellenbeleg schreibt die Marke — tvst zaehlt keine markenlose Zeile (F-M500)",
            registrySource.Contains("settledAsUploaded && quellen.Contains(SubtitleProvider.Subdl) ? DateTime.UtcNow : null", StringComparison.Ordinal)
            && registrySource.Contains("settledAsUploaded && quellen.Contains(SubtitleProvider.TvSubtitles) ? DateTime.UtcNow : null", StringComparison.Ordinal)
            // fuer tvsubtitles NUR seine eigene Marke: die markenlose Alt-/Pack-Zeile zaehlt nur bei SubDL
            // — F-M668: dieselbe Regel, jetzt in EINER Prüfstelle für Zeilen und Paar.
            && registrySource.Contains("if (provider != SubtitleProvider.Subdl || !string.Equals(status, SubtitleStatus.Uploaded, StringComparison.Ordinal))", StringComparison.Ordinal)
            // F-M670: der Altbeleg aus der Id-Spalte ist entfallen — die Uebernahme hat ihn in die
            // Gridfilm gehoben; geblieben ist die markenlose `uploaded`-Zeile (F-M500).
            && registrySource.Contains("return !AnySourceClosed(mediaHash, language, hearingImpaired);", StringComparison.Ordinal));

        // Der Umbau wird nicht nur gelesen, sondern gefahren: ein Inhalt bei SubDL darf tvst nicht
        // erledigen, zweimal bei derselben Quelle bleibt blockiert, und die Herkunft bleibt das Tor.
        string settleRoot = Path.Combine(Path.GetTempPath(), "endpoints-settle-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(settleRoot))
            {
                Directory.Delete(settleRoot, true);
            }

            Directory.CreateDirectory(settleRoot);
            using (var settleDb = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(Path.Combine(settleRoot, "db"), null))
            {
                var settleReg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(settleDb, null, null);

                const string fileA = "aaaa1111bbbb2222";
                const string fileB = "cccc3333dddd4444";
                const string de = "DE";
                string textDe = "1\n00:00:01,000 --> 00:00:03,000\nEin Satz.\n\n";
                string hashDe = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(textDe);

                // Der Inhalt liegt bei SubDL (Marke der Quelle, F-M490), unter ANDERER Dateiidentitaet.
                settleReg.EnsureFile(fileB, "tt7654321", "/media/films/film.mkv");
                settleReg.MarkEmbed(fileB, 0, de, false, Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Uploaded,
                    contentHash: hashDe, subdlUploadedAt: DateTime.UtcNow);
                settleReg.EnsureFile(fileA, "tt1234567", "/media/films/folge.mkv");

                Check("derselbe Inhalt bei SubDL erledigt tvsubtitles NICHT (F-M500)",
                    settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.Subdl)
                    && !settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.TvSubtitles)
                    && settleReg.IsContentUploadedForAnotherSource(hashDe, SubtitleProvider.TvSubtitles),
                    "subdl=" + settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.Subdl)
                    + " tvst=" + settleReg.IsContentUploadedFor(hashDe, SubtitleProvider.TvSubtitles));

                // Zweimal bei DERSELBEN Quelle bleibt blockiert: der zweite SubDL-Versuch wird wieder
                // als "schon hochgeladen" verbucht und schreibt KEINE Ablehnung.
                bool settleSubdl = settleReg.RecordSkippedContent(fileA, false, 7, de, false, false, hashDe,
                    Jellyfin.Plugin.SubScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.Subdl });
                bool settleTwice = settleReg.RecordSkippedContent(fileA, false, 7, de, false, false, hashDe,
                    Jellyfin.Plugin.SubScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.Subdl });
                var rowA = settleReg.GetEmbed(fileA, 7);
                Check("zweimal bei derselben Quelle bleibt blockiert — ohne Ablehnung (F-M500)",
                    settleSubdl && settleTwice
                    && rowA?.Status == Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Uploaded
                    && rowA?.Reason is null
                    && settleReg.IsUploadedTo(fileA, de, false, SubtitleProvider.Subdl)
                    // die andere Quelle ist auf DIESER Spur weiterhin offen
                    && !settleReg.IsUploadedTo(fileA, de, false, SubtitleProvider.TvSubtitles),
                    "settled=" + settleSubdl + "/" + settleTwice + " status=" + rowA?.Status + " grund=" + (rowA?.Reason ?? "-"));

                // Ohne Quellenbeleg (nur eine Ablehnung im Bestand) bleibt es bei der Ablehnung, und die
                // ist quellenneutral: sie haelt BEIDE Quellen — wie festgelegt (F-M499).
                string textFr = "1\n00:00:04,000 --> 00:00:06,000\nUne phrase.\n\n";
                string hashFr = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(textFr);
                settleReg.MarkEmbed(fileA, 8, "FR", false, Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Rejected,
                    contentHash: hashFr, reason: Jellyfin.Plugin.SubScribe.Data.RejectReason.DuplicateRemote);
                bool noEvidence = settleReg.RecordSkippedContent(fileA, false, 8, "FR", false, true, hashFr,
                    Jellyfin.Plugin.SubScribe.Data.RejectReason.DuplicateContent,
                    Array.Empty<SubtitleProvider>());
                var rowFr = settleReg.GetEmbed(fileA, 8);
                Check("ohne Quellenbeleg bleibt die Ablehnung quellenneutral (F-M499/F-M500)",
                    !noEvidence
                    && rowFr?.Status == Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Rejected
                    && rowFr?.Reason == Jellyfin.Plugin.SubScribe.Data.RejectReason.DuplicateContent);

                // Das Herkunftstor steht VOR diesem Weg und wird hier nicht weicher. Drei Faelle, wie
                // festgelegt (Operator 15.11.2026: "externe Quellen duerfen zu beiden hochgeladen werden"):
                // unbekannt/extern -> beide, subdl -> nur SubDL, tvsubtitles -> nur tvsubtitles.
                var torUnbekannt = UploadGate.Decide(true, true, origin: null, false, false, false, false);
                var markenText = "1\n00:00:20,000 --> 00:00:22,000\nZwei Marken.\n\n";
                string markenHash = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(markenText);
                settleReg.EnsureFile("eeee5555ffff6666", "tt9999999", "/media/films/beide.mkv");
                settleReg.MarkEmbed("eeee5555ffff6666", 0, de, false,
                    Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Uploaded, contentHash: markenHash,
                    subdlUploadId: "sd-1", subdlUploadedAt: DateTime.UtcNow);
                settleReg.MarkEmbed("eeee5555ffff6666", 0, de, false,
                    Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Uploaded, contentHash: markenHash,
                    tvSubtitlesUploadId: "tv-1", tvSubtitlesUploadedAt: DateTime.UtcNow);
                var beideRow = settleReg.GetEmbed("eeee5555ffff6666", 0);
                Check("unbekannte/externe Herkunft: BEIDE Quellen duerfen hochladen, jede mit eigener Marke (F-M487/F-M500)",
                    torUnbekannt.Subdl && torUnbekannt.TvSubtitles && torUnbekannt.SkipReason is null
                    && Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.MayUpload(null)
                    && Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.MayUpload(null)
                    // F-M668: die zwei Marken stehen als ZWEI Zeilen derselben Spur in der Gridfilm — je
                    // Quelle eine, jede mit ihrer eigenen Einsende-Id (vorher: zwei Spalten einer Zeile).
                    && beideRow != null
                    && Jellyfin.Plugin.SubScribe.Data.SourceStore.TrackMark(
                        settleDb, "eeee5555ffff6666", Jellyfin.Plugin.SubScribe.Data.SourceStore.Subdl, de, false).UploadId == "sd-1"
                    && Jellyfin.Plugin.SubScribe.Data.SourceStore.TrackMark(
                        settleDb, "eeee5555ffff6666", Jellyfin.Plugin.SubScribe.Data.SourceStore.TvSubtitles, de, false).UploadId == "tv-1"
                    && settleReg.IsUploadedTo("eeee5555ffff6666", de, false, SubtitleProvider.Subdl)
                    && settleReg.IsUploadedTo("eeee5555ffff6666", de, false, SubtitleProvider.TvSubtitles)
                    && uploadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal),
                    "subdl=" + torUnbekannt.Subdl + " tvst=" + torUnbekannt.TvSubtitles);

                // Gesperrte Herkuenfte: ein Download ist ein Fund, kein eigener Kandidat — er geht NIRGENDS
                // hoch (Operator 15.11.2026: "Herkunft subdl: nie zurueck zu subdl, nie zu tvst").
                string textHer = "1\n00:00:09,000 --> 00:00:11,000\nHerkunft.\n\n";
                string hashHer = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(textHer);
                settleReg.MarkDownloaded(hashHer, fileA, de, false,
                    origin: Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.Name(SubtitleProvider.Subdl),
                    fileName: "folge.de.srt", path: "/media/films/folge.de.srt");
                string? herkunft = settleReg.GetOrigin(hashHer).Origin;
                var torSubdl = UploadGate.Decide(true, true, herkunft, false, false, false, false);
                Check("Herkunft=subdl: weder zu SubDL (kein Reup) noch zu tvsubtitles (F-M487 verschaerft)",
                    herkunft == "subdl"
                    && !torSubdl.Subdl && !torSubdl.TvSubtitles
                    && torSubdl.SkipReason == "origin:subdl — downloaded there, never uploaded"
                    && !Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.MayUpload(herkunft)
                    && !Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.MayUpload(herkunft)
                    && !settleReg.IsContentUploadedFor(hashHer, SubtitleProvider.TvSubtitles)
                    // der Settle-Weg fragt genau dieses Tor, bevor er einen Kandidaten weiterlaufen laesst
                    && uploadSource.Contains("Api.Endpoints.Provenance.MayUpload(herkunft)", StringComparison.Ordinal)
                    && uploadSource.Contains("string? herkunft = Registry.GetOrigin(contentHash).Origin;", StringComparison.Ordinal),
                    "herkunft=" + (herkunft ?? "-") + " subdl=" + torSubdl.Subdl + " tvst=" + torSubdl.TvSubtitles);

                string textHer2 = "1\n00:00:12,000 --> 00:00:14,000\nAndere Herkunft.\n\n";
                string hashHer2 = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(textHer2);
                settleReg.MarkDownloaded(hashHer2, fileA, de, false,
                    origin: Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.Name(SubtitleProvider.TvSubtitles),
                    fileName: "folge2.de.srt", path: "/media/films/folge2.de.srt");
                string? herkunftTv = settleReg.GetOrigin(hashHer2).Origin;
                var torTv = UploadGate.Decide(true, true, herkunftTv, false, false, false, false);
                Check("Herkunft=tvsubtitles: weder zu tvsubtitles (kein Reup) noch zu SubDL (F-M487 verschaerft)",
                    herkunftTv == "tvsubtitles"
                    && !torTv.Subdl && !torTv.TvSubtitles
                    && torTv.SkipReason == "origin:tvsubtitles — downloaded there, never uploaded"
                    && !Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.MayUpload(herkunftTv)
                    && !Jellyfin.Plugin.SubScribe.Api.Endpoints.Provenance.MayUpload(herkunftTv)
                    // F-M668: die Inhaltsfrage antwortet über das PAAR dieser Zeile. Für SubDL ist das
                    // Paar (fileA, DE) bereits oben (Settle oben) — die Antwort ist deshalb „ja", und
                    // zwar aus demselben Grund, aus dem das Tor es sagen würde. Für tvst ist das Paar
                    // offen: der Text selbst liegt nirgends bei tvst.
                    && settleReg.IsContentUploadedFor(hashHer2, SubtitleProvider.Subdl)
                    && !settleReg.IsContentUploadedFor(hashHer2, SubtitleProvider.TvSubtitles),
                    "herkunft=" + (herkunftTv ?? "-") + " subdl=" + torTv.Subdl + " tvst=" + torTv.TvSubtitles);

                // Ein bekannter Fund ist keine Upload-Arbeit mehr: die Spur ist fuer JEDE Quelle erledigt,
                // sonst reihte der Seeder sie in jedem Lauf erneut ein (Operator: nie zurueck, nie hinueber).
                settleReg.MarkEmbed(fileA, 9, "IT", false,
                    Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Observed, contentHash: hashHer);
                string textFrei = "1\n00:00:30,000 --> 00:00:32,000\nOhne Herkunft.\n\n";
                string hashFrei = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(textFrei);
                settleReg.MarkEmbed(fileA, 10, "IT", false,
                    Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Observed, contentHash: hashFrei);
                Check("ein Download ist fuer BEIDE Quellen erledigt — die Spur bleibt nicht ewig offen (F-M500)",
                    settleReg.IsEmbedTerminalFor(fileA, 9, SubtitleProvider.Subdl)
                    && settleReg.IsEmbedTerminalFor(fileA, 9, SubtitleProvider.TvSubtitles)
                    // Negativkontrolle: ohne bekannte Herkunft ist die Spur NICHT terminal — die Regel greift nur dort
                    && !settleReg.IsEmbedTerminalFor(fileA, 10, SubtitleProvider.Subdl)
                    && !settleReg.IsEmbedTerminalFor(fileA, 10, SubtitleProvider.TvSubtitles)
                    && uploadSource.Contains("if (registry.GetOrigin(looseHash).Origin is not null)", StringComparison.Ordinal));

                // Die Queue fragt dieselbe Sache je Quelle (F-M500 im Seeder): ein Upload bei der anderen
                // Quelle haelt die zweite nicht mehr aus der Queue.
                Check("die Queue liest \"bekannt\" nicht mehr quellenblind (F-M500)",
                    uploadSource.Contains("registry.IsContentUploadedForAnotherSource(contentHashProbe, quelle)", StringComparison.Ordinal)
                    && !seederSource.Contains("if (registry.IsContentKnown(Registry.ContentHashRegistry.ComputeHash(content)))", StringComparison.Ordinal));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(settleRoot))
                {
                    Directory.Delete(settleRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // --- Abschnitt 20: die tvst-Seriennummer im Store und die Anlage fehlender Serien (F-M501) ---
        // Operator-Anweisung 15.11.2026: "Tmdb titel fuehrt.", "Tmdb titel fuehrt auch fuer
        // Doppeleintraege.", nachgeschaerft "Doch Aehnlichkeitssuche. Aber gross/Kleinschreibung. And =
        // und. Leerzeichen mehr oder weniger. Aehnlichkeit eng halten". Also: Name normalisieren und
        // dann auf GLEICHHEIT vergleichen — kein Teilstring, kein Aehnlichkeitsmass, kein
        // Artikel-Abschneiden.
        var namen = new Dictionary<int, string>
        {
            [512] = "The Sample",
            [422] = "Malcolm in the Sample",
            [701] = "The Sample Man",
            [700] = "Rick & Morty",
        };
        Check("tvst vergleicht eng: Gross/Kleinschreibung und Leerraum zaehlen nicht (F-M501)",
            TvSubtitlesClient.ResolveShow(namen, "the sample") == 512
            && TvSubtitlesClient.ResolveShow(namen, "  The   Sample ") == 512
            && TvSubtitlesClient.NormalizeShowName("The   Sample") == "the sample",
            "the middle=" + TvSubtitlesClient.ResolveShow(namen, "the sample")
            + " doppelraum=" + TvSubtitlesClient.ResolveShow(namen, "  The   Sample "));

        Check("tvst vergleicht eng: & = and = und (F-M501)",
            TvSubtitlesClient.ResolveShow(namen, "Rick and Morty") == 700
            && TvSubtitlesClient.ResolveShow(namen, "Rick und Morty") == 700
            && TvSubtitlesClient.ResolveShow(namen, "rick&morty") == 700
            // "und" nur als Wort: aus "Rundschau" darf kein "Randschau" werden
            && TvSubtitlesClient.NormalizeShowName("Rundschau") == "rundschau",
            "and=" + TvSubtitlesClient.ResolveShow(namen, "Rick and Morty"));

        Check("tvst vergleicht eng: kein Teilstring, kein Artikel-Abschneiden (F-M501)",
            // "The Sample" findet NICHT "The Sample Man" und NICHT "Malcolm in the Sample"
            TvSubtitlesClient.ResolveShow(namen, "The Sample") == 512
            // die fruehere "enthaelt"-Stufe haette hier 701 geliefert
            && TvSubtitlesClient.ResolveShow(namen, "Sample") is null
            && TvSubtitlesClient.ResolveShow(namen, "Sample Man") is null
            && TvSubtitlesClient.ResolveShow(namen, "the sample man") == 701,
            "Sample=" + (TvSubtitlesClient.ResolveShow(namen, "Sample")?.ToString() ?? "-"));

        // Der TMDb-Serien-Datensatz, auf dem die Aufloesung beruht (Titel, Jahr, Staffelzahl).
        var tmdbInfo = await new TmdbImdbResolver(
                new HttpClient(new JsonHandler(
                    "{\"name\":\"The Sample\",\"original_name\":\"The Sample\"," +
                    "\"first_air_date\":\"2009-09-30\",\"number_of_seasons\":9}")),
                "test-key")
            .SeriesInfoAsync("999001", CancellationToken.None).ConfigureAwait(false);
        Check("der TMDb-Serien-Datensatz traegt Titel, Erstausstrahlungsjahr und Staffelzahl (F-M501)",
            tmdbInfo is { Name: "The Sample", FirstAirYear: 2009, Seasons: 9 },
            tmdbInfo is null ? "kein Datensatz" : tmdbInfo.Name + "/" + tmdbInfo.FirstAirYear + "/" + tmdbInfo.Seasons);

        string tvstRoot = Path.Combine(Path.GetTempPath(), "endpoints-tvst-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(tvstRoot))
            {
                Directory.Delete(tvstRoot, true);
            }

            Directory.CreateDirectory(tvstRoot);
            using (var tvstDb = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(Path.Combine(tvstRoot, "db"), null))
            {
                var tvstReg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(tvstDb, null, null);
                var mitte = new TmdbSeriesInfo("The Sample", "The Sample", 2009, 9);

                // (a) F-M734: die gespeicherte Nummer fuehrt — der ABLAUF liest sie und gibt sie im
                // Auftrag mit; die Engine antwortet darauf „bereit", ohne die Seite zu fragen. Der
                // Nachschlageweg, der früher selbst in den Store sah und schrieb, ist entfallen.
                tvstReg.EnsureFile("aaaa000000000001", patch: m => m.TvstShowId = "512");
                Check("eine gespeicherte TvstShowId verhindert Suche UND Anlage (F-M501)",
                    uploadSource.Contains("Registry.GetMedia(mediaHash!)?.TvstShowId", StringComparison.Ordinal)
                    && uploadSource.Contains("Registry.TvstShowIdAtPath(first.Item?.Path)", StringComparison.Ordinal)
                    && uploadSource.Contains("MediaId = bekannteSerienId,", StringComparison.Ordinal)
                    && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Sources/Tvst/TvstEngine.cs"))
                        .Contains("SourceTitleState.Ready, showId.Value.ToString(CultureInfo.InvariantCulture)", StringComparison.Ordinal),
                    "Ablauf oder Engine kennt die gespeicherte Nummer nicht mehr");

                // (b) Ein Treffer in der Liste: die Nummer ist die Antwort, angelegt wird nichts. Den
                // Bestand schreibt der Ablauf (F-M734) — die Seite der Titel-Frage kennt ihn nicht.
                var handlerVorhanden = new TvstSiteHandler(512, "The Sample");
                var seiteVorhanden = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerVorhanden)));
                var vorhanden = await seiteVorhanden
                    .FindOrCreateAsync(mitte.Name, mitte.FirstAirYear, mitte.Seasons, "aaaa000000000002", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("der TMDb-Titel findet die Serie eng verglichen, ohne Anlage (F-M501)",
                    vorhanden.State == Jellyfin.Plugin.SubScribe.Sources.SourceTitleState.Ready && vorhanden.TitleId == "512"
                    && handlerVorhanden.Posts == 0 && handlerVorhanden.Gets == 1,
                    "state=" + vorhanden.State + " posts=" + handlerVorhanden.Posts);

                // (c) Kein Treffer: genau EINE Anlage, danach wird erneut gesucht.
                var neu = new TmdbSeriesInfo("Beispielserie Test", "Beispielserie Test", 2026, 3);
                var handlerNeu = new TvstSiteHandler(512, "The Sample") { AddAfterPost = (9001, "Beispielserie Test") };
                var seiteNeu = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerNeu)));
                var angelegt = await seiteNeu
                    .FindOrCreateAsync(neu.Name, neu.FirstAirYear, neu.Seasons, "aaaa000000000003", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("ohne Treffer wird genau einmal angelegt und danach erneut gesucht (F-M501)",
                    angelegt.State == Jellyfin.Plugin.SubScribe.Sources.SourceTitleState.Created && angelegt.TitleId == "9001"
                    && handlerNeu.Posts == 1 && handlerNeu.Gets == 2,
                    "state=" + angelegt.State + " posts=" + handlerNeu.Posts + " gets=" + handlerNeu.Gets);

                Check("die Anlage traegt Name, Jahr und Staffelzahl aus TMDb (F-M501)",
                    handlerNeu.LastBody is string rumpf
                    && rumpf.Contains("name=\"name\"\r\n\r\nBeispielserie Test", StringComparison.Ordinal)
                    && rumpf.Contains("name=\"year_s\"\r\n\r\n2026", StringComparison.Ordinal)
                    && rumpf.Contains("name=\"seasons\"\r\n\r\n3", StringComparison.Ordinal),
                    handlerNeu.LastBody is null ? "kein Rumpf" : "Rumpf ohne die drei Felder");

                // Staffelzahl: die Seite kennt nur 1..22 — 30 wird auf 22 gezogen, "keine Angabe" auf 1.
                var handlerViele = new TvstSiteHandler(512, "The Sample") { AddAfterPost = (9002, "Viele Staffeln") };
                var seiteViele = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerViele)));
                await seiteViele.FindOrCreateAsync("Viele Staffeln", 2001, 30, "aaaa000000000004", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                var handlerOhne = new TvstSiteHandler(512, "The Sample") { AddAfterPost = (9003, "Ohne Angabe") };
                var seiteOhne = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerOhne)));
                await seiteOhne.FindOrCreateAsync("Ohne Angabe", 2001, null, "aaaa000000000005", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("die Staffelzahl wird auf 1..22 begrenzt, und BuildAddShow weist 23 ab (F-M501)",
                    handlerViele.LastBody!.Contains("name=\"seasons\"\r\n\r\n22", StringComparison.Ordinal)
                    && handlerOhne.LastBody!.Contains("name=\"seasons\"\r\n\r\n1", StringComparison.Ordinal)
                    && TvSubtitlesClient.MaxSeason == 22
                    && Throws(() => TvSubtitlesClient.BuildAddShow("XY", 2026, 23))
                    && !Throws(() => TvSubtitlesClient.BuildAddShow("XY", 2026, 22)),
                    "30->" + (handlerViele.LastBody!.Contains("seasons\"\r\n\r\n22", StringComparison.Ordinal) ? "22" : "?"));

                // (d) Die Anlage scheitert: es gibt keine Kennung, und die Kette laeuft weiter (Skipped, kein
                // Wurf). Geschrieben wird hier ohnehin nichts — die Seite der Titel-Frage kennt den Bestand nicht.
                var handlerKaputt = new TvstSiteHandler(512, "The Sample") { PostStatus = 500 };
                var seiteKaputt = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerKaputt)));
                var kaputt = await seiteKaputt
                    .FindOrCreateAsync("Beispielserie Test", 2026, 3, "aaaa000000000006", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine gescheiterte Anlage blockiert die Kette nicht und schreibt nichts (F-M501/F-M516)",
                    kaputt.State == Jellyfin.Plugin.SubScribe.Sources.SourceTitleState.NotFound && kaputt.TitleId is null
                    && handlerKaputt.Posts == 1
                    // die Kette antwortet mit Skipped (die andere Quelle bekommt ihren Versuch), nicht mit Failed
                    // F-M734: die Kette antwortet mit Skipped (die andere Quelle bekommt ihren Versuch),
                    // nicht mit Failed — der Grund heisst jetzt "Titel zu".
                    && uploadSource.Contains("return UploadResult.Skipped(\"second-source-title-closed\");", StringComparison.Ordinal),
                    "state=" + kaputt.State + " posts=" + handlerKaputt.Posts);

                // ---- F-M538: jede Seite fuehrt ihr eigenes Sprachset --------------------------
                // Operator-Anweisung 16.11.2026: „Sprachen die tvst nicht unterstuetzt, nicht mehr in
                // die Kette fuer tvst. Fuer subdl gilt ein anderes sprachset."
                // F-M581: die Sprachtafel wird in der KETTE geprueft, nicht mehr in der Paar-Frage.
                // F-M735: die Kette fragt nicht mehr den HTTP-Client beim Namen, sondern die QUELLE über
                // ihre Sprachauskunft — je Adresse ihre eigene, weil der Schlüssel aus dem Anbieter kommt.
                Check("F-M538: both site families gate the chain by their OWN language table",
                    uploadSource.Contains("Sources.SourceLanguages.NimmtSprache(", StringComparison.Ordinal)
                    && uploadSource.Contains("SourceOrder.KeyOf(Configuration.SubtitleProvider.TvSubtitles), plan.Lang", StringComparison.Ordinal)
                    && uploadSource.Contains("SourceOrder.KeyOf(Configuration.SubtitleProvider.TvSubtitles), lang", StringComparison.Ordinal)
                    && uploadSource.Contains("SourceOrder.KeyOf(Configuration.SubtitleProvider.MovieSubtitles), lang", StringComparison.Ordinal)
                    // F-M799: die Sprachbruecke der Seite wird seit 12.3.8.88 fuer den DATEINAMEN gefragt,
                    // nie fuer das Tor — das Tor fragt NimmtSprache (Zeilen oben). Der Name je Quelle ist
                    // damit der zweite, eigene Beleg.
                    && uploadSource.Contains("SourceFileName(quelle,", StringComparison.Ordinal));
                Check("F-M538: a language the site does not carry is a skip with its own reason, not a failure",
                    uploadSource.Contains("return UploadResult.Skipped(\"second-source-no-language\")", StringComparison.Ordinal)
                    && uploadSource.Contains("the site has no such language (F-M538)", StringComparison.Ordinal));
                Check("F-M538: the site sets really are separate (Hindi is on neither site, Arabic on both)",
                    TvSubtitlesClient.SiteLanguage("hi") is null
                    && MovieSubtitlesClient.SiteLanguage("hi") is null
                    && TvSubtitlesClient.SiteLanguage("ar") == "ar"
                    && MovieSubtitlesClient.SiteLanguage("ar") == "ar",
                    "tvst(hi)=" + (TvSubtitlesClient.SiteLanguage("hi") ?? "-")
                    + " mst(hi)=" + (MovieSubtitlesClient.SiteLanguage("hi") ?? "-"));

                // ---- F-M539: erledigte Eintraege enden VOR dem ffmpeg-Durchgang ---------------
                // Operator-Anweisung 16.11.2026: „Das kann man doch sofort skippen, ohne weitere Pruefung."
                int iSettle = uploadSource.IndexOf("summary.SettledItems++", StringComparison.Ordinal);
                // F-M596: der ffmpeg-Durchlauf steht jetzt hinter dem Vorlauf (`vorlauf.TakeOrExtractAsync`),
                // die Extraktion selbst liegt in ExtractionLookahead/FfmpegTools. Der Anker im Lauf ist
                // also der Aufruf, nicht mehr der Methodenname.
                int iExtract = uploadSource.IndexOf("await vorlauf", StringComparison.Ordinal);
                Check("F-M539: the settled-item exit stands BEFORE the one ffmpeg pass over all streams",
                    iSettle > 0 && iExtract > 0 && iSettle < iExtract,
                    "SettledItems@" + iSettle + " ExtractAll@" + iExtract);
                Check("F-M539: it asks both questions — no pair still wanted AND every position terminal",
                    uploadSource.Contains("bool settledPairs = !Registry.RecordedPairsByHash(mediaHash)", StringComparison.Ordinal)
                    && uploadSource.Contains(".Any(pair => AnyUploadSourceStillWants(mediaHash, pair.Language, pair.HearingImpaired, isSeries));", StringComparison.Ordinal)
                    && uploadSource.Contains("if (settledPairs && Registry.ArePositionsTerminal(mediaHash, textPositions))", StringComparison.Ordinal));
                Check("F-M539: a switch change re-opens the item (the pair question reads the per-source switches)",
                    uploadSource.Contains("item settled before extraction", StringComparison.Ordinal)
                    && uploadSource.Contains("public int SettledItems { get; set; }", StringComparison.Ordinal)
                    // F-M721: die Paar-Frage liest die Schalter je Quelle aus der Registry, nicht aus
                    // zwei benannten Zeilen.
                    && uploadSource.Contains("switchedOn: SourceAllowed,", StringComparison.Ordinal)
                    && uploadSource.Contains("bool AnyUploadSourceStillWants(", StringComparison.Ordinal));

                // ---- F-M552: ein bekanntes Item wird aufgefrischt, nicht eingefroren ----------------
                string verteilerQuelle = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "ScribeEventDispatcher.cs")).ConfigureAwait(false);
                Check("F-M552: an item already in the queue gets the seeder's fresh source list",
                    verteilerQuelle.Contains("var vorhanden = queue.First(x => string.Equals(x.ItemId, c.ItemId, StringComparison.OrdinalIgnoreCase));", StringComparison.Ordinal)
                    && verteilerQuelle.Contains("vorhanden.Sources = c.Sources;", StringComparison.Ordinal));
                Check("F-M552: and an empty answer does not wipe a known one",
                    verteilerQuelle.Contains("if (c.Sources != null && c.Sources.Count > 0)", StringComparison.Ordinal));

                // ---- F-M551: auch die Download-Zeile nennt die Quelle -------------------------------
                Check("F-M551: a saved download names the source it came from",
                    downloadSource.Contains("\"[Sub Scribe-D] SAVED {Source} {Path}", StringComparison.Ordinal)
                    && downloadSource.Contains("Api.Endpoints.Provenance.Name(cand.Provider)", StringComparison.Ordinal));
                Check("F-M551: the saved HI copy names it too",
                    downloadSource.Split("SAVED {Source} HI", StringSplitOptions.None).Length - 1 == 2
                    && downloadSource.Contains("Api.Endpoints.Provenance.Name(hi.Provider)", StringComparison.Ordinal));

                // ---- F-M550: die Log-Zeile nennt die Quelle, und das Plugin heisst nicht SubDL ------
                string uploadResultSource = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "Api", "UploadResult.cs")).ConfigureAwait(false);
                Check("F-M550: an upload line names the source that answered",
                    uploadSource.Contains("QuellName(result)", StringComparison.Ordinal)
                    && uploadSource.Contains("jeQuelle[i].Source = zustaendig[i];", StringComparison.Ordinal)
                    && uploadResultSource.Contains("public Configuration.SubtitleProvider? Source { get; set; }", StringComparison.Ordinal));
                Check("F-M550: no log prefix still says SubDL",
                    !uploadSource.Contains("\"[SubDL", StringComparison.Ordinal)
                    && !downloadSource.Contains("\"[SubDL", StringComparison.Ordinal)
                    && !seederSource.Contains("\"[SubDL", StringComparison.Ordinal)
                    && uploadSource.Contains("\"[Sub Scribe] UPLOADED", StringComparison.Ordinal));
                Check("F-M550: an unknown source is named as unknown, not guessed",
                    uploadSource.Contains("\"no source answered\"", StringComparison.Ordinal));

                // ---- F-M549: die Quellen je Item ueberleben den Neustart --------------------------
                Check("F-M549: the queued item itself names the sources that queued it",
                    seederSource.Contains("public List<string> Sources { get; set; } = new();", StringComparison.Ordinal)
                    && seederSource.Contains("eintrag.Sources = todoQuellen.Select(quelle => quelle.ToString()).ToList();", StringComparison.Ordinal));
                Check("F-M549: the per-source queue is rebuilt from the stored queue, not from the last scan",
                    verteilerQuelle.Contains("RebuildUploadBySource();", StringComparison.Ordinal)
                    && verteilerQuelle.Split("RebuildUploadBySource();").Length - 1 == 2
                    && !verteilerQuelle.Contains("_uploadBySource = snapshot.UploadBySource;", StringComparison.Ordinal));
                Check("F-M549: an item without a source note falls back to all sources instead of guessing",
                    verteilerQuelle.Contains("if (item.Sources == null || item.Sources.Count == 0 || string.IsNullOrEmpty(item.ItemId))", StringComparison.Ordinal)
                    && verteilerQuelle.Contains("Enum.TryParse<Configuration.SubtitleProvider>(name, out var quelle)", StringComparison.Ordinal));

                // ---- F-M548: der Rhythmus haengt hinter der gezogenen Datei -------------------------
                Check("F-M548/F-M594: der Takt haengt am Abruf, nicht am Ablegen — kein Nachlauf mehr",
                    downloadSource.Split("TransferPauseMsLessFit()").Length - 1 == 0
                    && downloadSource.Split("await _limiter.WaitForTransferSlotAsync(ct)").Length - 1 == 1
                    && downloadSource.Contains("if (rawAttempt.Bytes is { Length: >= 100 })", StringComparison.Ordinal));
                // F-M686: der regulaere Abruf ist der Aufruf von FetchArchiveAsync (dort steht der
                // Auftrag an die Engine) — gemessen wird die Stelle, an der er im Lauf steht.
                int abrufStelle594 = downloadSource.IndexOf("await FetchArchiveAsync(cand.NId", StringComparison.Ordinal);
                string vorDemAbruf594 = abrufStelle594 > 400
                    ? downloadSource.Substring(0, abrufStelle594)
                    : string.Empty;
                Check("F-M548/F-M594: der Slot steht VOR dem Abruf",
                    vorDemAbruf594.Contains("await _limiter.WaitForTransferSlotAsync(ct)", StringComparison.Ordinal));
                int dryStelle = downloadSource.IndexOf("DRY-RUN HI", StringComparison.Ordinal);
                Check("F-M548: a dry run transfers nothing and therefore waits for nothing",
                    dryStelle > 0
                    && !downloadSource.Substring(Math.Max(0, dryStelle - 200), 400).Contains("WaitForTransferSlotAsync", StringComparison.Ordinal)
                    && !downloadSource.Substring(dryStelle - 800, 800).Contains("TransferPauseMsLessFit", StringComparison.Ordinal)
                    && !downloadSource.Substring(dryStelle, 800).Contains("TransferPauseMsLessFit", StringComparison.Ordinal));

                // ---- F-M547: Limits haengen an der externen Anfrage und an der echten Uebertragung ----
                string registratorQuelle547 = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "PluginServiceRegistrator.cs")).ConfigureAwait(false);
                string pacerQuelle = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "Api", "ExternalApiPacer.cs")).ConfigureAwait(false);
                string handlerQuelle = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "Api", "PacingHandler.cs")).ConfigureAwait(false);
                Check("F-M547: the API pause sits in every HTTP chain, inside the retry handler",
                    registratorQuelle547.Split("new Api.PacingHandler(").Length - 1 == 2,
                    (registratorQuelle547.Split("new Api.PacingHandler(").Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " Kette(n)");
                Check("F-M547: the pause is drawn after the call and defaults to half a second",
                    pacerQuelle.Contains("public const int DefaultPauseMs = 500;", StringComparison.Ordinal)
                    && pacerQuelle.Contains("await Task.Delay(ms, ct).ConfigureAwait(false);", StringComparison.Ordinal)
                    && handlerQuelle.Contains("await ExternalApiPacer.AfterCallAsync(cancellationToken).ConfigureAwait(false);", StringComparison.Ordinal)
                    && handlerQuelle.IndexOf("await base.SendAsync", StringComparison.Ordinal)
                       < handlerQuelle.IndexOf("await ExternalApiPacer.AfterCallAsync", StringComparison.Ordinal));
                Check("F-M547: a pause of zero is honoured — the pause can be switched off",
                    pacerQuelle.Contains("if (ms > 0 && !ct.IsCancellationRequested)", StringComparison.Ordinal)
                    && pacerQuelle.Contains("Volatile.Write(ref _pauseMs, ms < 0 ? 0 : ms);", StringComparison.Ordinal));
                Check("F-M547/F-M589/F-M594: the rhythm follows a REAL transfer of THIS track, not a processed file",
                    uploadSource.Contains("bool dieseSpurHochgeladen = false;", StringComparison.Ordinal)
                    && uploadSource.Contains("dieseSpurHochgeladen = true;", StringComparison.Ordinal)
                    && uploadSource.Split("await Task.Delay(_limiter.TransferPauseMs(), runCt)").Length - 1 == 0
                    && uploadSource.Contains("if (jeQuelle.Any(r => r.Ok))", StringComparison.Ordinal));
                Check("F-M547: the download search no longer pauses before the call",
                    !downloadSource.Contains("await _limiter.PauseAsync(ct).ConfigureAwait(false);", StringComparison.Ordinal));

                // ---- F-M546: eine Queue je Quelle -------------------------------------------------
                Check("F-M546: the seeder keeps one queue per source",
                    seederSource.Contains("public Dictionary<Configuration.SubtitleProvider, HashSet<string>> UploadBySource", StringComparison.Ordinal)
                    && seederSource.Contains("UploadTodoSources(item, mediaPath, config)", StringComparison.Ordinal)
                    && seederSource.Contains("menge.Add(qi.ItemId);", StringComparison.Ordinal));
                Check("F-M546: the run narrows its sources to the ones that queued the item",
                    uploadSource.Contains("public Dictionary<Configuration.SubtitleProvider, HashSet<string>>? QueueFilterBySource", StringComparison.Ordinal)
                    && uploadSource.Contains("private HashSet<Configuration.SubtitleProvider>? _queuedSourcesForItem;", StringComparison.Ordinal)
                    // F-M581: die Queue wirkt in der Kette (Quellenauswahl), nicht mehr in der Paar-Frage.
                    && uploadSource.Contains(".Where(QueuedForSource)", StringComparison.Ordinal));
                Check("F-M546: without a per-source queue nothing is narrowed away",
                    uploadSource.Contains("_queuedSourcesForItem == null || _queuedSourcesForItem.Contains(quelle)", StringComparison.Ordinal));
                Check("F-M546: the scan line names the queue per source instead of one bare number",
                    seederSource.Contains("PerSourceSummary(snapshot)", StringComparison.Ordinal));

                // ---- F-M545: der Store wird auf JEDEM Weg angehaengt, auf dem ein Kontext entsteht --
                // Am 16.11.2026 haengte nur RecreateDbContext an, der normale Start nicht — der Store
                // war leer, waehrend 42 von 42 TMDb-Fragen eines Zyklus Wiederholungen waren. Der
                // Prueflauf deckte den Store ab, nicht seine Verdrahtung; das holt dieser Check nach.
                string pluginQuelle = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "Plugin.cs")).ConfigureAwait(false);
                // F-M616: die Anhaenge stehen jetzt in einem Helfer, den beide Startwege rufen. Der Check
                // zaehlt deshalb die Aufrufe des Helfers und prueft, dass der Store darin haengt.
                Check("F-M545: the TMDb store is attached where the shared context is created",
                    pluginQuelle.Split("AttachStores(_sharedDbContext.Context, logger, DataFolderPath, Configuration.LogMode);").Length - 1 == 2
                    && pluginQuelle.Contains("TmdbAnswerStore.Attach(db);", StringComparison.Ordinal),
                    (pluginQuelle.Split("AttachStores(_sharedDbContext.Context, logger, DataFolderPath, Configuration.LogMode);").Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " Stelle(n)");
                string storeQuelle = await File.ReadAllTextAsync(Path.Combine(
                    root, "Jellyfin.Plugin.SubdlSync", "Registry", "TmdbAnswerStore.cs")).ConfigureAwait(false);
                Check("F-M545: and the store fetches a missing context itself instead of silently staying in memory",
                    storeQuelle.Contains("Plugin.Instance?.SharedDbContext?.Context", StringComparison.Ordinal));

                // ---- F-M544: die Medienart am Dateikopf kommt vom Item, nicht aus dem Raten --------
                Check("F-M544: the head of the run reads the media kind from the item",
                    uploadSource.Contains("bool? medienart = item switch", StringComparison.Ordinal)
                    && uploadSource.Contains("Episode => true,", StringComparison.Ordinal)
                    && uploadSource.Contains("Movie => false,", StringComparison.Ordinal)
                    && uploadSource.Contains("var quellen = UploadSources(medienart);", StringComparison.Ordinal));
                Check("F-M544: an unknown kind keeps the cautious reading of F-M509",
                    uploadSource.Contains("_ => (bool?)null,", StringComparison.Ordinal)
                    && uploadSource.Split("UploadSources(isSeries: null)").Length - 1 == 1);

                // ---- F-M541: eine Datei mit lauter verworfenen Spuren endet vor dem ffmpeg-Lauf -----
                int i541 = uploadSource.IndexOf("every text stream was rejected earlier", StringComparison.Ordinal);
                Check("F-M541: a file whose text streams were all rejected ends before the ffmpeg pass",
                    i541 > 0 && iExtract > 0 && i541 < iExtract
                    && uploadSource.Contains("textPositions.All(p => Registry.EmbedRejectedReason(mediaHash, p) != null)", StringComparison.Ordinal));
                Check("F-M541: it says why in one line per file instead of per stream",
                    uploadSource.Contains("SKIP {File} — every text stream was rejected earlier, nothing can be uploaded (F-M541)", StringComparison.Ordinal)
                    && uploadSource.Contains("summary.SettledItems++;", StringComparison.Ordinal));

                // ---- F-M540: die Queue fragt den Store je Paar, je Quelle ----------------------
                // Operator-Anweisung 16.11.2026: „Prüfung bereits im seeder ob sprache / sub bereits als
                // hochgeladen markiert ist. Wenn ja skip. Prüfung für subdl und tvst, jeweils eine eigene queue"
                Check("F-M540: the queue keeps asking per source (F-M499) and no longer guesses a settled item",
                    seederSource.Contains("registry.ArePositionsTerminalFor(mediaHash", StringComparison.Ordinal)
                    && seederSource.Contains("registry.CountKnownPairsFor(mediaHash, quelle)", StringComparison.Ordinal)
                    && seederSource.Contains("UploadTodoFor(item, mediaPath, config, quelle,", StringComparison.Ordinal));
                Check("F-M540: the queue asks whether the site carries the language at all",
                    seederSource.Contains("Sources.SourceLanguages.NimmtSprache(", StringComparison.Ordinal)
                    && seederSource.Contains("SourceOrder.KeyOf(quelle), mapped", StringComparison.Ordinal)
                    && !seederSource.Contains("Api.TvSubtitlesClient.SiteLanguage(", StringComparison.Ordinal)
                    && !seederSource.Contains("Api.MovieSubtitlesClient.SiteLanguage(", StringComparison.Ordinal)
                    && seederSource.Contains("return false; // diese Seite führt keine dieser Sprachen", StringComparison.Ordinal));
                Check("F-M540: an untagged stream keeps the item queued",
                    seederSource.Contains("bool anyUntagged = textStreams.Any(s => string.IsNullOrWhiteSpace(s.Language));", StringComparison.Ordinal)
                    && seederSource.Contains("if (!anyUntagged && !anyCarried)", StringComparison.Ordinal));

                // Anlegen hoechstens einmal je Medium und Lauf: derselbe Fehlschlag wird NICHT wiederholt.
                var zweiterVersuch = await seiteKaputt
                    .FindOrCreateAsync("Beispielserie Test", 2026, 3, "aaaa000000000006", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("ein Fehlschlag legt nicht bei jeder Folge erneut an (F-M501)",
                    zweiterVersuch.State == Jellyfin.Plugin.SubScribe.Sources.SourceTitleState.Unclear
                    && zweiterVersuch.TitleId is null
                    && handlerKaputt.Posts == 1,
                    "state=" + zweiterVersuch.State + " posts=" + handlerKaputt.Posts);

                // (e) Trockenlauf: nichts anlegen, nichts schreiben.
                var handlerTrocken = new TvstSiteHandler(512, "The Sample");
                var seiteTrocken = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerTrocken)));
                var trocken = await seiteTrocken
                    .FindOrCreateAsync("Beispielserie Test", 2026, 3, "aaaa000000000007", dryRun: true, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("der Trockenlauf legt nichts an und entscheidet nichts (F-M501)",
                    trocken.State == Jellyfin.Plugin.SubScribe.Sources.SourceTitleState.NotAskable
                    && trocken.TitleId is null
                    && handlerTrocken.Posts == 0,
                    "state=" + trocken.State + " posts=" + handlerTrocken.Posts);

                // (f) Unklare Seite: einmal nachfragen, dann NICHT schreiben und tvst ueberspringen.
                var handlerLeer = new TvstSiteHandler(512, "The Sample") { EmptyList = true };
                var seiteLeer = new TvstShowLookup(new TvSubtitlesClient(new HttpClient(handlerLeer)));
                var leer = await seiteLeer
                    .FindOrCreateAsync("Beispielserie Test", 2026, 3, "aaaa000000000008", dryRun: false, CancellationToken.None)
                    .ConfigureAwait(false);
                Check("eine unklare Serienliste wird nicht als Abwesenheit gelesen (F-M501)",
                    leer.State == Jellyfin.Plugin.SubScribe.Sources.SourceTitleState.Unclear && leer.TitleId is null
                    && handlerLeer.Posts == 0,
                    "state=" + leer.State + " gets=" + handlerLeer.Gets);

                // (g) Der Endpunkt mit Nummer im Ticket: die Serienliste wird gar nicht erst geholt.
                var handlerEndpunkt = new TvstSiteHandler(512, "The Sample");
                var endpunkt = new TvSubtitlesUploadEndpoint(
                    new TvSubtitlesClient(new HttpClient(handlerEndpunkt)), dryRun: false);
                var antwort = await endpunkt.SendAsync(
                        new UploadTicket(
                            "Ein Name, den die Seite NICHT fuehrt", "tt1442464", "1422", 2, 24, "de",
                            "folge.srt", Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:03,000\nSatz.\n"),
                            "WEB", "The.Sample.S02E24", false, false, false, TvstShowId: 512),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                // Die EINE Serienliste danach ist die Ruecklesung der eigenen Upload-Id (F-M488) — sie
                // kommt NACH dem Upload. Vor dem Upload wird nicht gesucht: die Nummer steht im Ticket,
                // obwohl der Name im Ticket eine Schreibweise ist, die die Seite nicht fuehrt.
                Check("mit TvstShowId im Ticket sucht der Endpunkt vor dem Upload nicht (F-M501)",
                    antwort.Ok && handlerEndpunkt.Uploads == 1
                    && handlerEndpunkt.Order.FirstOrDefault() == "UPLOAD"
                    && handlerEndpunkt.Order.Count(x => x == "UPLOAD") == 1
                    && handlerEndpunkt.LastUploadBody!.Contains("tvshow\"\r\n\r\n512", StringComparison.Ordinal),
                    "ok=" + antwort.Ok + " folge=" + string.Join(",", handlerEndpunkt.Order));

                // Die Kette selbst: kein Dateiname mehr als Serienname, die Nummer geht ins Ticket.
                Check("die Upload-Kette loest ueber TMDb und den Store auf, nicht ueber den Dateinamen (F-M501)",
                    uploadSource.Contains("var series = await _tmdb.SeriesInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
                    // F-M734: die Nummer kommt aus dem Bestand, das Anlegen ist ein Feld des Auftrags.
                    && uploadSource.Contains("Registry.GetMedia(mediaHash!)?.TvstShowId", StringComparison.Ordinal)
                    && uploadSource.Contains("MediaId = bekannteSerienId,", StringComparison.Ordinal)
                    && uploadSource.Contains("Registry.SetTvstShowId(mediaHash, mediaPath, serienNummer)", StringComparison.Ordinal)
                    // F-M734: auch der Behaelter-Weg fragt den Nachschlageweg nicht mehr — die Nummer
                    // steht im Auftrag, die Titel-Frage faehrt im Versand (SeasonPackCoordinator).
                    && !uploadSource.Contains("ResolveForMediaAsync(", StringComparison.Ordinal)
                    && uploadSource.Contains("Registry.TvstShowIdAtPath(first.Item?.Path)", StringComparison.Ordinal)
                    // der alte Weg (Serienname aus dem Dateinamen) steht im tvst-Zweig nicht mehr
                    && !uploadSource.Contains(
                        "var parsed = Pipeline.MediaNameParser.Parse(Path.GetFileNameWithoutExtension(releaseName))",
                        StringComparison.Ordinal));

            // (h) Die Migration selbst: eine Datei aus Schema 12 (media ohne TvstShowId) wird beim
            // Oeffnen um die Spalte erweitert, der Bestand bleibt lesbar.
            string legacyDir = Path.Combine(tvstRoot, "legacy");
            Directory.CreateDirectory(legacyDir);
            string legacyFile = Path.Combine(legacyDir, Jellyfin.Plugin.SubScribe.Data.DbFiles.Name);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyFile))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta (Id TEXT PRIMARY KEY, SchemaVersion INTEGER, PluginVersion TEXT, "
                    + "JellyfinVersion TEXT, Updated TEXT);"
                    + "INSERT INTO meta VALUES ('db', 12, '12.1.12.389', '10.11.0', '2026-11-15');"
                    + "CREATE TABLE media (Id TEXT PRIMARY KEY, Path TEXT, JellyfinItemId TEXT, ImdbId TEXT, "
                    + "TmdbId TEXT, SdId TEXT, IsSeries INTEGER, Season INTEGER, Episode INTEGER, "
                    + "LanguagesAvailable TEXT, SubtitlesUploadedAt TEXT, LastSearchUtc TEXT, "
                    + "LastSearchLanguages TEXT, LastSeen TEXT);"
                    // Jede Spalte, die das Modell NICHT nullen kann, bekommt einen Wert — sonst
                    // scheitert schon das Lesen des Bestands, und die Pruefung misse die Migration.
                    + "INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, SdId, IsSeries, Season, "
                    + "Episode, LanguagesAvailable, SubtitlesUploadedAt, LastSearchUtc, LastSearchLanguages, "
                    + "LastSeen) VALUES ('aaaa000000000009', '/data/serien/folge.mkv', 'jf-1', 'tt1442464', "
                    + "'1422', NULL, 1, 2, 24, 'DE', NULL, NULL, NULL, '2026-11-15T00:00:00');";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            using (var legacyDb = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(legacyDir, null))
            {
                var bestand = legacyDb.Files.FindById("aaaa000000000009");
                // Der Wert wird VOR dem Schreiben festgehalten: EF haelt eine Zeile im Kontext und
                // liefert beim zweiten Lesen DIESELBE Instanz — die nach dem Patch "512" traegt.
                string? vorherTvst = bestand?.TvstShowId;
                legacyDb.EnsureFile("aaaa000000000009", patch: m => m.TvstShowId = "512");
                var nachher = legacyDb.Files.FindById("aaaa000000000009");
                Check("Schema 12 -> 14: die Spalte wird beim Start nachgezogen, der Bestand bleibt gueltig (F-M501)",
                    bestand is { ImdbId: "tt1442464", TmdbId: "1422" }
                    && vorherTvst is null
                    && nachher?.TvstShowId == "512"
                    // F-M507: der Marker ist inzwischen 14 — die Datei wird auf den Stand DIESES Builds
                    // gehoben, und der Bestand muss das aushalten.
                    && legacyDb.Meta.FindById("db")?.SchemaVersion == 24,
                    "bestand=" + (bestand?.ImdbId ?? "-") + "/" + (bestand?.TmdbId ?? "-")
                    + "/tvst=" + (vorherTvst ?? "<null>")
                    + " nachher=" + (nachher?.TvstShowId ?? "-")
                    + " marker=" + legacyDb.Meta.FindById("db")?.SchemaVersion);
            }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tvstRoot))
                {
                    Directory.Delete(tvstRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // ---- 21. F-M504: eine leere erste Quelle beendet den Vorgang nicht mehr ---------------
        // Gemessen 15.11.2026 (Probelauf jf-test, DownloadLanguages=DE): 27 Zeilen
        // "no candidates on SubDL for [DE]" und KEINE tvst-Zeile, obwohl der tvst-Schalter an war —
        // der Ausstieg bei leerer SubDL-Antwort stand VOR der Stelle, die die zweite Quelle fragte.
        // Und die Frage lautet nicht "zwei Quellen", sondern "alle Quellen, deren Schalter an ist"
        // (Nutzerergaenzung 15.11.2026: "Dritte bis n quelle sind ebenfalls denkbar").
        var alleAnbieter = Enum.GetValues<SubtitleProvider>().ToList();

        // (a) F-M509 (Operator-Klarstellung 15.11.2026: „Seperate into subdl and tvst. Tvst is splitted
        // into tvst and moviesub itself"): die Reihenfolge ordnet ZWEI Quellen — SubDL und die tvst-
        // Familie. Die Familie erscheint in jeder Medienart mit der Adresse, die sie fuehrt: Serie
        // tvsubtitles.net, Film moviesubtitles.org. Deshalb steht in jeder Liste genau ZWEI Eintraege,
        // und die Einstellung hat weiterhin zwei Werte (keine dritte Position).
        var tvstZuerstSerie = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true);
        var tvstZuerstFilm = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false);
        var subdlZuerstSerie = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: true);
        var subdlZuerstFilm = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: false);
        Check("the query order names the family's page for the medium plus SubDL — the family leads in BOTH media (F-M509)",
            tvstZuerstSerie.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && tvstZuerstFilm.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            && subdlZuerstSerie.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles })
            && subdlZuerstFilm.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.MovieSubtitles })
            && Enum.GetValues<PluginConfiguration.DownloadSourceOrder>().Length == 2
            && SourceOrder.FamilyFirst(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst)
            && !SourceOrder.FamilyFirst(PluginConfiguration.DownloadSourceOrder.SubdlFirst)
            && SourceOrder.FamilyPage(true) == SubtitleProvider.TvSubtitles
            && SourceOrder.FamilyPage(false) == SubtitleProvider.MovieSubtitles,
            "serie/tvst=" + string.Join(",", tvstZuerstSerie) + " | film/tvst=" + string.Join(",", tvstZuerstFilm));

        // Die Anzahl der befragten Quellen ist die Anzahl der freigeschalteten — nachgebaut aus genau
        // den Bausteinen, die der Lauf benutzt. Eine fest verdrahtete Zwei faellt hier auf, sobald das
        // Enum eine dritte Quelle bekommt.
        var beideAn = Conf(c => { c.DownloadEnabled = true; c.TvSubtitlesDownloadEnabled = true; });
        var nurSubdl = Conf(c =>
        {
            c.DownloadEnabled = true;
            c.TvSubtitlesDownloadEnabled = false;
            c.MovieSubtitlesDownloadEnabled = false; // F-M507: die dritte Quelle ist im null-Fall an
        });
        // F-M509: gefragt wird je Medienart — die Adresse der Familie, die diese Art fuehrt, und SubDL.
        // Die Zahl der befragten Quellen ist damit ZWEI, unabhaengig davon, wie viele Adressen das Enum
        // fuehrt: die Familie ist eine Quelle.
        var freigeschaltetSerie = SourceOrder.ForMedia(beideAn.DownloadOrder, isSeries: true).Where(beideAn.IsDownloadEnabled).ToList();
        var freigeschaltetFilm = SourceOrder.ForMedia(beideAn.DownloadOrder, isSeries: false).Where(beideAn.IsDownloadEnabled).ToList();
        Check("per medium exactly two sources are asked — SubDL and the family's page for that medium (F-M504/F-M509)",
            freigeschaltetSerie.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && freigeschaltetFilm.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            && SourceOrder.ForMedia(nurSubdl.DownloadOrder, isSeries: true).Where(nurSubdl.IsDownloadEnabled).Single() == SubtitleProvider.Subdl
            && SourceOrder.ForMedia(nurSubdl.DownloadOrder, isSeries: false).Where(nurSubdl.IsDownloadEnabled).Single() == SubtitleProvider.Subdl,
            "serie=" + string.Join(",", freigeschaltetSerie) + " film=" + string.Join(",", freigeschaltetFilm));

        // (a) DIE AUFRUFREIHENFOLGE, nicht nur die Rangfolge (Nutzerkorrektur 15.11.2026: „Suchen
        // engines werden der Reihenfolge nach befragt. Zur zeit sind es 2"): bis hierher lief die
        // SubDL-Suche als fester Block zuerst, und die Einstellung entschied nur ueber den Rang. Der
        // Ablauf wird deshalb aus DENSELBEN Bausteinen nachgebaut, die der Lauf benutzt — die
        // eingestellte Reihenfolge aus SourceOrder.ForOrder und die Frage „ist das die Hauptsuche?"
        // ueber SourceOrder.MainSearch, also genau die Konstante, auf die die Pipeline verzweigt.
        static List<string> Ablauf(PluginConfiguration.DownloadSourceOrder order) =>
            SourceOrder.ForMedia(order, isSeries: true)
                .Select(p => p == SourceOrder.MainSearch ? "main-search" : "endpoint:" + Provenance.Name(p))
                .ToList();

        var ablaufTvst = Ablauf(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst);
        var ablaufSubdl = Ablauf(PluginConfiguration.DownloadSourceOrder.SubdlFirst);
        // F-M509: bei einem FILM fuehrt dieselbe Einstellung die Film-Adresse der Familie an — genau die
        // Luecke, die F-M507 offen liess (tvst war fuer Filme nie zustaendig, also fuehrte immer SubDL).
        var ablaufFilmTvst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
            .Select(p => p == SourceOrder.MainSearch ? "main-search" : "endpoint:" + Provenance.Name(p))
            .ToList();
        Check("tvst-first puts the family's FILM page before the SubDL search too (F-M509)",
            ablaufFilmTvst[0] == "endpoint:moviesubtitles"
            && ablaufFilmTvst.IndexOf("endpoint:moviesubtitles") < ablaufFilmTvst.IndexOf("main-search"),
            string.Join(" > ", ablaufFilmTvst));
        Check("tvst-first asks tvsubtitles BEFORE the SubDL search, subdl-first after it (F-M504)",
            ablaufTvst[0] == "endpoint:tvsubtitles"
            && ablaufTvst.IndexOf("endpoint:tvsubtitles") < ablaufTvst.IndexOf("main-search")
            && ablaufSubdl[0] == "main-search"
            && ablaufSubdl.IndexOf("main-search") < ablaufSubdl.IndexOf("endpoint:tvsubtitles"),
            "tvst-first=" + string.Join(" > ", ablaufTvst) + " | subdl-first=" + string.Join(" > ", ablaufSubdl));

        // Dieselbe Aussage am Quelltext: die SubDL-Suche steht NICHT mehr als eigener Block vor der
        // Schleife, sondern laeuft in der lokalen Funktion, die der Zweig „diese Quelle ist die
        // Hauptsuche" aufruft — also an der Stelle, die die Reihenfolge vorgibt. Der letzte Aufruf
        // von RunV2SearchAsync muss VOR der Schleife liegen, und es darf keinen danach geben.
        var mainSearchKopf = downloadSource.IndexOf("async Task<bool> RunMainSearchAsync()", StringComparison.Ordinal);
        var letzteSubdlSuche = downloadSource.LastIndexOf("await RunV2SearchAsync(", StringComparison.Ordinal);
        var schleife = downloadSource.IndexOf("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal);
        var hauptAufruf = downloadSource.IndexOf("hauptsucheLief = await RunMainSearchAsync().ConfigureAwait(false);", StringComparison.Ordinal);
        Check("the SubDL search runs where the order puts it — inside the ordered loop, not before it (F-M504)",
            mainSearchKopf > 0 && letzteSubdlSuche > mainSearchKopf && letzteSubdlSuche < schleife
            && mainSearchKopf < schleife && hauptAufruf > schleife
            && downloadSource.Contains("if (quelle == MainSearchSource)", StringComparison.Ordinal),
            "main-search head @" + mainSearchKopf + ", last call @" + letzteSubdlSuche
            + ", loop @" + schleife + ", ordered call @" + hauptAufruf);

        // (b) KEIN vorzeitiger Ausstieg: die Schleife fragt jede Quelle, und der Ausstieg „keine
        // Kandidaten" steht HINTER ihr. Im Rumpf gibt es kein `break` und keine Ausstiegszeile; die
        // beiden `continue` sind die dokumentierten Faelle (SubDL nicht konfiguriert, keine fehlende
        // Sprache) und ueberspringen eine Abfrage, nicht eine Quelle, die noch zu fragen waere.
        var schleifeEnde = downloadSource.IndexOf("// F-M47: search performed", StringComparison.Ordinal);
        var schleifenRumpf = schleife > 0 && schleifeEnde > schleife
            ? downloadSource.Substring(schleife, schleifeEnde - schleife)
            : string.Empty;
        Check("no source is skipped and nothing leaves the loop before the next one is asked (F-M504)",
            schleifenRumpf.Length > 0
            && schleifenRumpf.Contains("if (quelle == MainSearchSource)", StringComparison.Ordinal)
            && schleifenRumpf.Contains("await ForeignCandidatesAsync(quelle, title", StringComparison.Ordinal)
            && schleifenRumpf.Contains("gefragteQuellen.Add(quelle);", StringComparison.Ordinal)
            && !schleifenRumpf.Contains("break;", StringComparison.Ordinal)
            && !schleifenRumpf.Contains("no candidates for [{Langs}]", StringComparison.Ordinal)
            && !downloadSource.Substring(schleifeEnde).Contains("await ForeignCandidatesAsync(", StringComparison.Ordinal),
            "loop body " + schleifenRumpf.Length + " chars");

        // (d) ausgeschaltete Quelle: der Filter steht JE QUELLE in der Liste, die die Schleife abarbeitet.
        Check("a switched-off source is never asked — the loop runs over the switch-filtered order (F-M504)",
            downloadSource.Contains("Api.Endpoints.SourceOrder.Sequence(_config, isSeries)", StringComparison.Ordinal)
            && downloadSource.Contains(".Where(SourceAllowed)", StringComparison.Ordinal)
            && downloadSource.Contains("var quellenReihenfolge = DownloadQueryOrder(isSeries);", StringComparison.Ordinal)
            && downloadSource.Contains("foreach (var quelle in quellenReihenfolge)", StringComparison.Ordinal)
            && !downloadSource.Contains("foreach (var quelle in zusatzQuellen)", StringComparison.Ordinal)
            && !downloadSource.Contains("Configuration.SubtitleProvider.TvSubtitles)", StringComparison.Ordinal),
            "the list is built from the order AND the switch of each source");

        // (a) leere erste Antwort: die Fragen stehen VOR dem Ausstieg — beide, nicht nur die zweite.
        var frageStelle = downloadSource.IndexOf("await ForeignCandidatesAsync(", StringComparison.Ordinal);
        var ausstieg = downloadSource.IndexOf("no candidates for [{Langs}]", StringComparison.Ordinal);
        Check("an empty first source no longer ends the item — the next sources are asked first (F-M504)",
            frageStelle > 0 && ausstieg > frageStelle && ausstieg > letzteSubdlSuche
            && downloadSource.Contains("if (candidates.Count == 0 && !fremdProSprache.Values.Any(", StringComparison.Ordinal),
            "asked @" + frageStelle + ", subdl search @" + letzteSubdlSuche + ", exit @" + ausstieg);

        // (b) beide leer: das bisherige Ergebnis bleibt, und die Zeile nennt die gefragten Quellen.
        Check("when every asked source is empty the old verdict stands, and the line names them (F-M504)",
            downloadSource.Contains("summary.NotAvailable += missing.Count;", StringComparison.Ordinal)
            && downloadSource.Contains("summary.SkippedNoCandidates++;", StringComparison.Ordinal)
            && downloadSource.Contains("asked: {Sources}", StringComparison.Ordinal)
            && downloadSource.Contains("Api.Endpoints.Provenance.Name", StringComparison.Ordinal)
            && !downloadSource.Contains("[Sub Scribe-D] {File} — no candidates on ", StringComparison.Ordinal));

        // (c) kein Doppelabruf: EIN Aufruf je Quelle und Sprache, die Schleife liest nur noch.
        var abrufe = System.Text.RegularExpressions.Regex.Matches(downloadSource, @"await ForeignCandidatesAsync\(").Count;
        Check("every source is asked ONCE per language — the language loop only reads the answer (F-M504)",
            abrufe == 1
            && downloadSource.Contains("fremdProSprache[lang] = treffer;", StringComparison.Ordinal)
            && downloadSource.Contains("if (fremdProSprache.TryGetValue(lang, out var fremd) && fremd.Count > 0)", StringComparison.Ordinal),
            abrufe + " call site(s)");

        // Und der Abruf selbst hat die Zwei-Quellen-Annahme verloren: der Endpunkt kommt aus dem
        // Katalog, ueber denselben Helfer fuer Suche UND Abruf.
        Check("search and fetch resolve the endpoint of ANY source over the catalog (F-M504)",
            downloadSource.Contains("private Api.Endpoints.IDownloadEndpoint? EndpointFor(Configuration.SubtitleProvider provider) =>", StringComparison.Ordinal)
            && downloadSource.Contains("var endpoint = EndpointFor(quelle);", StringComparison.Ordinal)
            && downloadSource.Contains("var endpoint = EndpointFor(cand.Provider);", StringComparison.Ordinal)
            && !downloadSource.Contains("_tvDownload", StringComparison.Ordinal));

        // ---- 22. F-M507: die dritte Quelle — Zuordnung nach Medienart, Download-Pfad -------------
        // Nutzerauftrag 15.11.2026: „Nur das series halt nach tv show und movies nach moviesubtitels
        // gehen. Als upload und download" und „Was was ist, Aufloesung per tmdb". Geprueft wird die
        // Zuordnung selbst (nicht ihre Absicht): welche Quelle fuer welche Medienart zustaendig ist, dass
        // die jeweils andere Seite NICHT gefragt wird, dass ein ausgeschalteter Schalter die Quelle aus
        // der Liste nimmt, dass die Reihenfolge erhalten bleibt — und dass der UPLOAD die neue Quelle
        // nicht kennt (Operator-Auflage: „Bevor wir falsche Sprachen hochladen, laden wir nix hoch.").

        // (a) Die Schalter der dritten Quelle: Download default AN, Upload default AUS (F-M503-Regel),
        // ein gespeichertes false bleibt AUS.
        Check("moviesubtitles: Download ist im ungesetzten Fall an, Upload aus — ein gesetztes false bleibt aus (F-M507)",
            Conf(c => { }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            && !Conf(c => { }).IsUploadEnabled(SubtitleProvider.MovieSubtitles)
            && !Conf(c => c.MovieSubtitlesDownloadEnabled = false).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            && Conf(c => c.MovieSubtitlesDownloadEnabled = true).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            && Conf(c => c.MovieSubtitlesUploadEnabled = true).IsUploadEnabled(SubtitleProvider.MovieSubtitles),
            "download-null=" + Conf(c => { }).IsDownloadEnabled(SubtitleProvider.MovieSubtitles)
            + " upload-null=" + Conf(c => { }).IsUploadEnabled(SubtitleProvider.MovieSubtitles));

        // (b) Die Zuordnung steht an EINER Stelle und ist eine Aussage ueber die Quelle: SubDL
        // unterscheidet nicht (beides), tvsubtitles.net nur Serien, moviesubtitles.org nur Filme.
        Check("die Medienart entscheidet je Quelle: SubDL beides, tvst nur Serien, msub nur Filme (F-M507)",
            SourceOrder.AppliesTo(SubtitleProvider.Subdl, isSeries: true)
            && SourceOrder.AppliesTo(SubtitleProvider.Subdl, isSeries: false)
            && SourceOrder.AppliesTo(SubtitleProvider.TvSubtitles, isSeries: true)
            && !SourceOrder.AppliesTo(SubtitleProvider.TvSubtitles, isSeries: false)
            && !SourceOrder.AppliesTo(SubtitleProvider.MovieSubtitles, isSeries: true)
            && SourceOrder.AppliesTo(SubtitleProvider.MovieSubtitles, isSeries: false));

        // (c) Eine SERIE fragt SubDL und tvsubtitles.net — und moviesubtitles.org NICHT, in keiner der
        // beiden Reihenfolgen.
        var serieTvstZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true);
        var serieSubdlZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: true);
        Check("eine Serie fragt tvst und SubDL, moviesubtitles nie (F-M507)",
            serieTvstZuerst.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && serieSubdlZuerst.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles })
            && !serieTvstZuerst.Contains(SubtitleProvider.MovieSubtitles)
            && !serieSubdlZuerst.Contains(SubtitleProvider.MovieSubtitles),
            "tvst-first=" + string.Join(",", serieTvstZuerst) + " subdl-first=" + string.Join(",", serieSubdlZuerst));

        // (d) Ein FILM fragt SubDL und moviesubtitles.org — und tvsubtitles.net NICHT. Die konfigurierte
        // Quelle fuehrt, soweit sie fuer diese Medienart ueberhaupt zustaendig ist: bei „tvst zuerst" ist
        // das fuer einen Film SubDL, weil tvst hier nicht anwendbar ist.
        var filmZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false);
        var filmSubdlZuerst = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.SubdlFirst, isSeries: false);
        // F-M509 (Korrektur der Luecke aus F-M507): „tvst zuerst" wirkt jetzt AUCH bei Filmen — dann
        // fuehrt moviesubtitles.org. Vorher stand hier, ein Film frage in beiden Reihenfolgen SubDL
        // zuerst, weil tvst fuer Filme nie zustaendig ist; das war die Luecke, nicht die Absicht.
        Check("ein Film fragt moviesubtitles und SubDL — in der eingestellten Reihenfolge, tvsubtitles nie (F-M509)",
            filmZuerst.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            && filmSubdlZuerst.SequenceEqual(new[] { SubtitleProvider.Subdl, SubtitleProvider.MovieSubtitles })
            && !filmZuerst.Contains(SubtitleProvider.TvSubtitles)
            && !filmSubdlZuerst.Contains(SubtitleProvider.TvSubtitles),
            "tvst-first=" + string.Join(",", filmZuerst) + " subdl-first=" + string.Join(",", filmSubdlZuerst));

        // (e) Ein ausgeschalteter Schalter nimmt die Quelle aus der Liste — die andere Seite bleibt.
        var tvstAusCfg = Conf(c => c.TvSubtitlesDownloadEnabled = false);
        var msubAusCfg = Conf(c => c.MovieSubtitlesDownloadEnabled = false);
        Check("Schalter aus = nicht gefragt: tvst aus heisst nur SubDL fuer eine Serie, msub aus nur SubDL fuer einen Film (F-M507)",
            SourceOrder.ForMedia(tvstAusCfg.DownloadOrder, isSeries: true).Where(tvstAusCfg.IsDownloadEnabled).Single() == SubtitleProvider.Subdl
            && SourceOrder.ForMedia(msubAusCfg.DownloadOrder, isSeries: false).Where(msubAusCfg.IsDownloadEnabled).Single() == SubtitleProvider.Subdl,
            "serie=" + string.Join(",", SourceOrder.ForMedia(tvstAusCfg.DownloadOrder, true).Where(tvstAusCfg.IsDownloadEnabled))
            + " film=" + string.Join(",", SourceOrder.ForMedia(msubAusCfg.DownloadOrder, false).Where(msubAusCfg.IsDownloadEnabled)));

        // (f) Die Herkunft kennt die dritte Quelle: der Store-Wert laesst sich zuruecklesen, und eine
        // BEKANNTE Herkunft sperrt den Upload bei ALLEN Zielen (F-M487), die neue eingeschlossen.
        Check("moviesubtitles ist eine Herkunft wie die beiden anderen — bekannt sperrt ueberall (F-M507)",
            Provenance.Name(SubtitleProvider.MovieSubtitles) == "moviesubtitles"
            && Provenance.Parse("moviesubtitles") == SubtitleProvider.MovieSubtitles
            && Provenance.Parse("MovieSubtitles") == SubtitleProvider.MovieSubtitles
            && !Provenance.MayUpload("moviesubtitles")
            && !Provenance.MayUpload("moviesubtitles")
            && !Provenance.MayUpload("moviesubtitles")
            && Provenance.MayUpload(null)
            && Provenance.OriginBlockedReason("moviesubtitles")!.Contains("moviesubtitles", StringComparison.Ordinal));

        // (g) Operator-Auflage 15.11.2026 („Bevor wir falsche Sprachen hochladen, laden wir nix hoch."):
        // die neue Quelle steht NICHT in der Quellenliste des Uploads — auch nicht als toter Eintrag,
        // denn die Kette hat fuer „alles ausser SubDL" genau einen Zweig (tvst).
        // F-M508 (Etappe 2b): der Upload-Weg der Film-Adresse EXISTIERT jetzt. Er steht deshalb in der
        // Kette — aber nur fuer Filme, und nur wenn sein Schalter an ist (Vorgabe: aus). Fuer Serien bleibt
        // er draussen: die Medienart schneidet ihn heraus, nicht eine Fallunterscheidung im Code.
        var uploadKetteSerie = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true)
            .Where(SubtitleEndpointCatalog.HasUploadPath)
            .ToList();
        var uploadKetteFilm = SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
            .Where(SubtitleEndpointCatalog.HasUploadPath)
            .ToList();
        Check("the film address HAS an upload path and stands in the chain for films — and only there (F-M508)",
            SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.MovieSubtitles)
            && SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.Subdl)
            && SubtitleEndpointCatalog.HasUploadPath(SubtitleProvider.TvSubtitles)
            && uploadKetteSerie.SequenceEqual(new[] { SubtitleProvider.TvSubtitles, SubtitleProvider.Subdl })
            && uploadKetteFilm.SequenceEqual(new[] { SubtitleProvider.MovieSubtitles, SubtitleProvider.Subdl })
            // Vorgabe bleibt AUS: gebaut heisst nicht scharf
            && !Conf(c => { c.UploadEnabled = true; }).IsUploadEnabled(SubtitleProvider.MovieSubtitles)
            && uploadSource.Contains(
                ".Where(Api.Endpoints.SubtitleEndpointCatalog.HasUploadPath)", StringComparison.Ordinal),
            "serie=" + string.Join(",", uploadKetteSerie) + " film=" + string.Join(",", uploadKetteFilm));

        // (h) Der Lauf selbst: die Frage-Reihenfolge wird ueber die Medienart geschnitten, und der
        // Filmzweig bringt Titel, Jahr und die gespeicherte Filmnummer mit — Staffel und Folge bleiben 0.
        Check("der Lauf schneidet die Liste ueber die Medienart und fragt den Film ueber Titel, Jahr und Filmnummer (F-M507)",
            downloadSource.Contains("private List<Configuration.SubtitleProvider> DownloadQueryOrder(bool isSeries)", StringComparison.Ordinal)
            // F-M509: die Medienart schneidet die Liste, und sie tut es IN SourceOrder.ForMedia — die
            // Pipeline fragt nur noch nach der Reihenfolge dieser Medienart.
            && downloadSource.Contains("Api.Endpoints.SourceOrder.Sequence(_config, isSeries)", StringComparison.Ordinal)
            && downloadSource.Contains("var quellenReihenfolge = DownloadQueryOrder(isSeries);", StringComparison.Ordinal)
            && downloadSource.Contains("if (!Api.Endpoints.SourceOrder.AppliesTo(quelle, isSeries))", StringComparison.Ordinal)
            && downloadSource.Contains("if (!_config.IsConfiguredForDownload(quelle))", StringComparison.Ordinal)
            && downloadSource.Contains("if (isSeries && (season <= 0 || episode <= 0))", StringComparison.Ordinal)
            && downloadSource.Contains(".ResolveForMediaAsync(Registry, mediaHash, movie, _config.DownloadDryRun, ct, mediaPath)", StringComparison.Ordinal)
            && downloadSource.Contains("var movie = await _tmdb.MovieInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
            // F-M686: Titel, Jahr und die Nummer des Films gehen als Felder der Suchanfrage hinaus —
            // die Engine dieser Quelle braucht dafür keinen Store.
            && downloadSource.Contains("filmNummer = film.MovieId!.Value.ToString(", StringComparison.Ordinal)
            && downloadSource.Contains("mediaNumber: filmNummer,", StringComparison.Ordinal)
            && downloadSource.Contains("year: filmYear,", StringComparison.Ordinal)
            && downloadSource.Contains("MediaNumber = mediaNumber,", StringComparison.Ordinal)
            // F-M663: die Abbildung Treffer -> Kandidat steht jetzt in SplitByHearingImpaired; die
            // Medienart schneidet Staffel und Folge dort an der Aufrufstelle auf 0 — dieselbe Aussage
            // wie vorher, nur eine Ebene weiter vorn.
            && downloadSource.Contains("isSeries ? season : 0, isSeries ? episode : 0)", StringComparison.Ordinal));

        // (i) Der Endpunkt der dritten Quelle am Stueck: Suche ueber Titel und Jahr, EINE Filmseite je
        // Film und Lauf, die Sprache aus der Flagge des Eintrags.
        var msubHandler = new MsubSiteHandler();
        var msubEndpoint = new MovieSubtitlesDownloadEndpoint(
            new MovieSubtitlesClient(new HttpClient(msubHandler)));
        // Die Nummer kommt aus dem Store (der Lauf uebergibt sie, MsubMovieResolver) — dann kostet eine
        // zweite Sprache KEINE zweite Suche, und die Filmseite wird fuer den Lauf einmal geholt.
        var filmTreffer = await msubEndpoint
            .SearchAsync(
                new SubtitleQuery("Arena: Part One", null, "438631", 0, 0, "EN", 2021, MovieId: "102"),
                CancellationToken.None)
            .ConfigureAwait(false);
        var filmTrefferDe = await msubEndpoint
            .SearchAsync(
                new SubtitleQuery("Arena: Part One", null, "438631", 0, 0, "DE", 2021, MovieId: "102"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Check("der msub-Endpunkt loest ueber Titel und Jahr und holt die Filmseite EINMAL je Film (F-M507)",
            filmTreffer.Count == 2
            && filmTreffer.All(h => h.Provider == SubtitleProvider.MovieSubtitles)
            && filmTreffer.All(h => h.Language == "en")
            && filmTrefferDe.Count == 1
            && msubHandler.Gets == 1
            && msubHandler.Posts == 0
            && msubHandler.LastUrl!.EndsWith("movie-102.html", StringComparison.Ordinal),
            "hits=" + filmTreffer.Count + "/" + filmTrefferDe.Count + " gets=" + msubHandler.Gets
            + " url=" + msubHandler.LastUrl);

        // (j) Was NICHT gefragt wird, kostet keinen Aufruf: eine Serie (Staffel/Folge gesetzt) und eine
        // Sprache, die die Seite nicht fuehrt, antworten leer — ohne Anfrage.
        var msubHandlerLeer = new MsubSiteHandler();
        var msubLeer = new MovieSubtitlesDownloadEndpoint(
            new MovieSubtitlesClient(new HttpClient(msubHandlerLeer)));
        var serieBeiMsub = await msubLeer
            .SearchAsync(new SubtitleQuery("The Sample", null, null, 2, 24, "EN", 2011), CancellationToken.None)
            .ConfigureAwait(false);
        var zhBeiMsub = await msubLeer
            .SearchAsync(new SubtitleQuery("Arena: Part One", null, "438631", 0, 0, "ZH", 2021), CancellationToken.None)
            .ConfigureAwait(false);
        Check("eine Serie und eine Sprache ohne Seitenkuerzel werden bei msub ohne einen einzigen Aufruf verneint (F-M507)",
            serieBeiMsub.Count == 0 && zhBeiMsub.Count == 0 && msubHandlerLeer.Gets == 0,
            "gets=" + msubHandlerLeer.Gets);

        // (k) Der Store: die Filmnummer steht am Medium (MsubMovieId), wird beim zweiten Mal gelesen
        // statt gesucht, und der Marker steht auf 14.
        var msubStoreDir = new TempDir("msub-store");
        try
        {
            using (var msubStore = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(msubStoreDir.Path, null))
            {
                var msubAufloeser = new MsubMovieResolver(
                    new MovieSubtitlesClient(new HttpClient(msubHandler)), null);
                var filmAuf = await msubAufloeser
                    .ResolveForMediaAsync(
                        new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(msubStore, null, null),
                        "bbbb000000000001",
                        new TmdbMovieInfo("Arena: Part One", "Arena", 2021),
                        dryRun: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var gespeichert = msubStore.Files.FindById("bbbb000000000001")?.MsubMovieId;
                var zweiteRunde = await msubAufloeser
                    .ResolveForMediaAsync(
                        new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(msubStore, null, null),
                        "bbbb000000000001",
                        new TmdbMovieInfo("Arena: Part One", "Arena", 2021),
                        dryRun: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                Check("die Filmnummer steht am Medium (MsubMovieId) und wird beim zweiten Mal gelesen statt gesucht (F-M507)",
                    filmAuf.Resolved
                    && filmAuf.Status == MsubMovieStatus.Found
                    && gespeichert == "102"
                    && zweiteRunde.Status == MsubMovieStatus.Stored
                    && msubStore.Meta.FindById("db")?.SchemaVersion == 24,
                    "id=" + (gespeichert ?? "<null>") + " status=" + filmAuf.Status + "/" + zweiteRunde.Status
                    + " marker=" + msubStore.Meta.FindById("db")?.SchemaVersion);
            }
        }
        finally
        {
            msubStoreDir.Dispose();
        }

        // (l) Die Migration selbst: eine Datei aus Schema 13 (media ohne MsubMovieId, sidecars/embeds ohne
        // die drei Spalten) wird beim Oeffnen erweitert; der Bestand bleibt unveraendert lesbar.
        var msubLegacyDir = new TempDir("msub-legacy");
        try
        {
            string legacyFile = Path.Combine(msubLegacyDir.Path, Jellyfin.Plugin.SubScribe.Data.DbFiles.Name);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + legacyFile))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE meta (Id TEXT PRIMARY KEY, SchemaVersion INTEGER, PluginVersion TEXT, "
                    + "JellyfinVersion TEXT, Updated TEXT);"
                    + "INSERT INTO meta VALUES ('db', 13, '12.1.12.394', '10.11.0', '2026-11-15');"
                    + "CREATE TABLE media (Id TEXT PRIMARY KEY, Path TEXT, JellyfinItemId TEXT, ImdbId TEXT, "
                    + "TmdbId TEXT, TvstShowId TEXT, SdId TEXT, IsSeries INTEGER, Season INTEGER, Episode INTEGER, "
                    + "LanguagesAvailable TEXT, SubtitlesUploadedAt TEXT, LastSearchUtc TEXT, "
                    + "LastSearchLanguages TEXT, LastSeen TEXT);"
                    + "INSERT INTO media (Id, Path, JellyfinItemId, ImdbId, TmdbId, TvstShowId, SdId, IsSeries, "
                    + "Season, Episode, LanguagesAvailable, SubtitlesUploadedAt, LastSearchUtc, LastSearchLanguages, "
                    + "LastSeen) VALUES ('cccc000000000001', '/data/filme/dune.mkv', 'jf-2', 'tt1160419', "
                    + "'438631', NULL, NULL, 0, 0, 0, 'EN', NULL, NULL, NULL, '2026-11-15T00:00:00');"
                    // F-M671: eine Altdatei traegt die ALTEN Tabellen mit ihren ALTEN Schluesseln und
                    // Spalten — angelegt aus diesem DDL, nicht aus dem neuen Modell abgeleitet. Die
                    // Spalten der dritten Quelle (Moviesubtitles*) gibt es hier noch nicht.
                    + "CREATE TABLE sidecars (Id TEXT PRIMARY KEY, ContentHash TEXT, FileName TEXT, "
                    + "MediaHash TEXT NOT NULL DEFAULT '', Path TEXT, ImdbId TEXT, TmdbId TEXT, IsSeries INTEGER, "
                    + "Season INTEGER, Episode INTEGER, Language TEXT, HearingImpaired INTEGER, Forced INTEGER, "
                    + "Status TEXT, Reason TEXT, StatusAt TEXT, Origin TEXT, OriginId TEXT, "
                    + "SubdlDownloadId TEXT, SubdlUploadId TEXT, SubdlUploadedAt TEXT, "
                    + "TvSubtitlesDownloadId TEXT, TvSubtitlesUploadId TEXT, TvSubtitlesUploadedAt TEXT);"
                    + "CREATE TABLE embeds (Id TEXT PRIMARY KEY, MediaHash TEXT NOT NULL DEFAULT '', "
                    + "SubPos INTEGER NOT NULL DEFAULT 0, Language TEXT, HearingImpaired INTEGER, Forced INTEGER, "
                    + "ContentHash TEXT, Status TEXT, Reason TEXT, StatusAt TEXT, Origin TEXT, OriginId TEXT, "
                    + "DetectionAttemptedAt TEXT, DetectionOutcome TEXT, "
                    + "SubdlDownloadId TEXT, SubdlUploadId TEXT, SubdlUploadedAt TEXT, "
                    + "TvSubtitlesDownloadId TEXT, TvSubtitlesUploadId TEXT, TvSubtitlesUploadedAt TEXT);"
                    + "INSERT INTO sidecars (Id, ContentHash, FileName, MediaHash, Language, Status, StatusAt) "
                    + "VALUES ('dddd000000000001', 'dddd000000000001', 'dune.de.srt', 'cccc000000000001', "
                    + "'DE', 'downloaded', '2026-11-15T00:00:00');"
                    + "INSERT INTO embeds (Id, MediaHash, SubPos, Language, Status, StatusAt) "
                    + "VALUES ('cccc000000000001|2', 'cccc000000000001', 2, 'EN', 'observed', "
                    + "'2026-11-15T00:00:00');";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            string? bestandImdb;
            string? bestandTmdb;
            string? bestandMsub;
            int? marker;
            using (var legacy = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(msubLegacyDir.Path, null))
            {
                var bestand = legacy.Files.FindById("cccc000000000001");
                bestandImdb = bestand?.ImdbId;
                bestandTmdb = bestand?.TmdbId;
                bestandMsub = bestand?.MsubMovieId;
                marker = legacy.Meta.FindById("db")?.SchemaVersion;
            }

            var medienSpalten = Spalten(legacyFile, "file");
            var seitenSpalten = Spalten(legacyFile, "sidecar");
            var spurSpalten = Spalten(legacyFile, "track");
            var alteTabellen = Spalten(legacyFile, "media").Count + Spalten(legacyFile, "sidecars").Count
                + Spalten(legacyFile, "embeds").Count;
            Check("Schema 13 -> 14: die Spalten der dritten Quelle werden beim Start nachgezogen (F-M507)",
                // F-M668: die neun Marken-Spalten werden NICHT mehr nachgezogen — die Quelle ist ein WERT
                // in `source_matrix`. Was bleibt, ist die Nummer am Medium (MsubMovieId, keine Marke).
                // F-M671: sie steht in der DDL der DATEI-Ebene; die vier Alt-Tabellen sind danach weg.
                medienSpalten.Contains("MsubMovieId")
                && !seitenSpalten.Contains("MoviesubtitlesDownloadId")
                && !seitenSpalten.Contains("MoviesubtitlesUploadId")
                && !spurSpalten.Contains("MoviesubtitlesDownloadId")
                && alteTabellen == 0
                // F-M670: der Marker steht auf 21 (Alt-Tabelle und Id-Spalte sind entfallen).
                // F-M671: 22 — die eigenen Kennungen.
                && marker == 24
                && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"))
                    .Contains("\"RefusalSignature TEXT NULL, \"", StringComparison.Ordinal),
                "file=" + string.Join(",", medienSpalten.Where(x => x.StartsWith("Msub", StringComparison.Ordinal)))
                + " sidecar=" + string.Join(",", seitenSpalten.Where(x => x.StartsWith("Moviesub", StringComparison.Ordinal)))
                + " track=" + string.Join(",", spurSpalten.Where(x => x.StartsWith("Moviesub", StringComparison.Ordinal)))
                + " alteTabellen=" + alteTabellen
                + " marker=" + marker);
            Check("die Bestandszeile bleibt unangetastet und liest die neuen Felder als null (F-M507)",
                bestandImdb == "tt1160419" && bestandTmdb == "438631" && bestandMsub is null,
                "bestand=" + (bestandImdb ?? "-") + "/" + (bestandTmdb ?? "-") + " msub=" + (bestandMsub ?? "<null>"));

            // Und die Quellen-Bindung, damit ein spaeterer Umbau die Zuordnung nicht still entfernt.
            var dbSource = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"));
            var provenanceSource = ReadText(
                Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/Provenance.cs"));
            Check("Schema 14 ist der Marker, die Spalten stehen in EnsureReportColumns und EnsureMediaColumns (F-M507)",
                // F-M668: Schema 19. Die Marken-Spalten der dritten Quelle stehen NICHT mehr in den
                // Nachzieh-Listen; die Gridfilm führt die Quelle als Wert, und der Quellenname ist derselbe
                // wie in der API (eine Schreibweise, zwei Schichten).
                // F-M671: der Marker steht auf 22; die Nummer am Medium steht in der DDL der Datei.
                dbSource.Contains("public const int CurrentSchemaVersion = 24;", StringComparison.Ordinal)
                && !dbSource.Contains("ALTER TABLE sidecars ADD COLUMN MoviesubtitlesDownloadId", StringComparison.Ordinal)
                && !dbSource.Contains("ALTER TABLE embeds ADD COLUMN MoviesubtitlesUploadedAt", StringComparison.Ordinal)
                && dbSource.Contains("+ \"MsubMovieId TEXT NULL, \"", StringComparison.Ordinal)
                && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/SourceStore.cs"))
                    .Contains("public const string MovieSubtitles = \"moviesubtitles\";", StringComparison.Ordinal)
                && provenanceSource.Contains("public const string MovieSubtitles = \"moviesubtitles\";", StringComparison.Ordinal));
        }
        finally
        {
            msubLegacyDir.Dispose();
        }

        // ---- 23. F-M508/F-M509: der UPLOAD-Weg der Film-Adresse --------------------------------
        // Gebaut, verdrahtet, GESPERRT. Die Pruefungen halten beides fest: dass der Weg da ist (Endpunkt,
        // Kette, Tor, Marke) und dass er ohne ausdrueckliche Entscheidung nicht angetreten wird.
        Check("die Film-Adresse steht mit eigenem Auftrag, eigener Vorpruefung und eigener Marke in der Kette (F-M508/F-M687)",
            uploadSource.Contains("Configuration.SubtitleProvider.MovieSubtitles => SendToMovieSubtitlesAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("MediaId = bekannteFilmId,", StringComparison.Ordinal)
            && uploadSource.Contains("IsSeries: false,", StringComparison.Ordinal)
            && uploadSource.Contains("Year: movie.ReleaseYear ?? 0,", StringComparison.Ordinal)
            && uploadSource.Contains("if (isSeries)", StringComparison.Ordinal)
            && uploadSource.Contains("UploadResult.Skipped(\"film-source-films-only\")", StringComparison.Ordinal));

        Check("die Filmnummer kommt aus dem Store, gesucht wird nur ohne sie — und ohne Jahr gar nicht (F-M508/F-M513)",
            // F-M734: die Nummer kommt aus dem Bestand, das Anlegen ist ein Feld des Auftrags.
            uploadSource.Contains("Registry.GetMedia(mediaHash!)?.MsubMovieId", StringComparison.Ordinal)
            && uploadSource.Contains("EnsureTitle = true,", StringComparison.Ordinal)
            && uploadSource.Contains("Registry.SetMsubMovieId(mediaHash, mediaPath, filmNummer)", StringComparison.Ordinal)
            && !uploadSource.Contains(".ResolveOrCreateForMediaAsync(", StringComparison.Ordinal)
            && uploadSource.Contains("var movie = await _tmdb.MovieInfoAsync(tmdbId!, ct)", StringComparison.Ordinal)
            && uploadSource.Contains("no TMDb id, so there is no leading title (F-M508)", StringComparison.Ordinal)
            // ohne Titel/Nummer nennt der Endpunkt den Grund, statt zu raten
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MovieSubtitlesEndpoints.cs"))
                .Contains("a film title AND its year are needed", StringComparison.Ordinal));

        Check("kein Schalter wird in der Kette direkt gelesen — nur ueber SourceAllowed (F-M495/F-M508)",
            !uploadSource.Contains("MovieSubtitlesUploadEnabled", StringComparison.Ordinal)
            && !uploadSource.Contains("TvSubtitlesUploadEnabled", StringComparison.Ordinal)
            && !uploadSource.Contains("SubdlUploadEnabled", StringComparison.Ordinal)
            // F-M713: der eine Prüfpunkt fragt Schalter UND Ausfall — die Kette liest weiterhin keinen
            // Schalter direkt, sie fragt nur diesen Helfer.
            && uploadSource.Contains("_config.IsUploadEnabled(provider) && !_quellenAus.Contains(provider)", StringComparison.Ordinal));

        // Die Marke der Film-Adresse haelt nur fuer sie — und sie schreibt ihre eigenen Spalten.
        string msubMarkRoot = Path.Combine(Path.GetTempPath(), "endpoints-msubmark-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(msubMarkRoot))
            {
                Directory.Delete(msubMarkRoot, true);
            }

            Directory.CreateDirectory(msubMarkRoot);
            using (var markDb = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(Path.Combine(msubMarkRoot, "db"), null))
            {
                var markReg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(markDb, null, null);
                const string film = "ffff000011112222";
                string text = "1\n00:00:01,000 --> 00:00:02,000\nEin Satz.\n\n";
                string hash = Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry.ComputeHash(text);
                markReg.EnsureFile(film, null, "/media/films/film.mkv");
                markReg.MarkEmbed(film, 0, "DE", false, Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Uploaded,
                    contentHash: hash, moviesubtitlesUploadId: "own:moviesubtitles:20261115T180405Z:abcd1234",
                    moviesubtitlesUploadedAt: DateTime.UtcNow);

                Check("die Marke der Film-Adresse haelt nur fuer die Film-Adresse (F-M508)",
                    markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.MovieSubtitles)
                    && markReg.IsContentUploadedFor(hash, SubtitleProvider.MovieSubtitles)
                    && !markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.TvSubtitles)
                    && !markReg.IsContentUploadedFor(hash, SubtitleProvider.TvSubtitles)
                    && !markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.Subdl),
                    "msub=" + markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.MovieSubtitles)
                    + " tvst=" + markReg.IsUploadedTo(film, "DE", false, SubtitleProvider.TvSubtitles));

                // Und das Settle schreibt dieselbe Marke, wenn NUR die Film-Adresse den Inhalt haelt.
                markReg.EnsureFile("eeee000033334444", null, "/media/films/zweiter.mkv");
                bool gesetzt = markReg.RecordSkippedContent("eeee000033334444", false, 1, "DE", false, false, hash,
                    Jellyfin.Plugin.SubScribe.Data.RejectReason.DuplicateContent,
                    new[] { SubtitleProvider.MovieSubtitles });
                var zeile = markReg.GetEmbed("eeee000033334444", 1);
                var msubMarke = Jellyfin.Plugin.SubScribe.Data.SourceStore.TrackMark(
                    markDb, "eeee000033334444", Jellyfin.Plugin.SubScribe.Data.SourceStore.MovieSubtitles, "DE", false);
                Check("das Settle setzt den Zeitstempel der Film-Adresse, und nur ihn (F-M508)",
                    gesetzt && msubMarke.ClosedAt != null
                    && Jellyfin.Plugin.SubScribe.Data.SourceStore.TrackMark(
                        markDb, "eeee000033334444", Jellyfin.Plugin.SubScribe.Data.SourceStore.TvSubtitles, "DE", false).ClosedAt is null
                    && Jellyfin.Plugin.SubScribe.Data.SourceStore.TrackMark(
                        markDb, "eeee000033334444", Jellyfin.Plugin.SubScribe.Data.SourceStore.Subdl, "DE", false).ClosedAt is null
                    && zeile?.Status == Jellyfin.Plugin.SubScribe.Data.SubtitleStatus.Uploaded,
                    "settled=" + gesetzt + " msub=" + (msubMarke.ClosedAt?.ToString() ?? "<null>"));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(msubMarkRoot))
                {
                    Directory.Delete(msubMarkRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // ---- 18. F-M510: das Etikett der Familien-Zeile nennt die Adresse, die gefragt wurde ------
        // Gemessen 15.11.2026 im ersten Film-Lauf auf jf-test (Filmbibliothek "Movies_Test"):
        //   [V] [tvsubtitles-D] Samplemovie (2023).mkv [EN] — 2 moviesubtitles candidate(s), 9 subdl
        //       candidate(s), order moviesubtitles, subdl
        // Die Zeile zaehlte richtig (moviesubtitles stand da), trug aber das Etikett der SERIEN-Seite.
        // Der Text stand fest im Code; die Adresse kommt jetzt aus SourceOrder.FamilyPage(isSeries) —
        // der einen Stelle, an der die Medienart eine Seite der Familie auswaehlt.
        Check("die Familie hat je Medienart genau eine Adresse (Serie tvsubtitles, Film moviesubtitles) (F-M510)",
            SourceOrder.FamilyPage(isSeries: true) == SubtitleProvider.TvSubtitles
            && SourceOrder.FamilyPage(isSeries: false) == SubtitleProvider.MovieSubtitles
            && Provenance.Name(SourceOrder.FamilyPage(isSeries: false)) == "moviesubtitles"
            && Provenance.Name(SourceOrder.FamilyPage(isSeries: true)) == "tvsubtitles",
            "Film → " + Provenance.Name(SourceOrder.FamilyPage(false))
            + ", Serie → " + Provenance.Name(SourceOrder.FamilyPage(true)));

        Check("das Etikett der Sammelzeile ist kein fester Text mehr (F-M510)",
            !downloadSource.Contains("[tvsubtitles-D]", StringComparison.Ordinal)
            && downloadSource.Contains("\"[{Src}-D] {File} [{Lang}] — {Counts}, order {Order}\"", StringComparison.Ordinal));

        var etikettZeile = downloadSource.IndexOf("[{Src}-D] {File} [{Lang}]", StringComparison.Ordinal);
        var etikettArg = downloadSource.IndexOf(
            "Provenance.Name(Api.Endpoints.SourceOrder.FamilyPage(isSeries))", StringComparison.Ordinal);
        Check("die Zeile bekommt die Adresse als ERSTES Argument — vor Datei, Sprache und Zaehlung (F-M510)",
            etikettZeile > 0 && etikettArg > etikettZeile
            && downloadSource.IndexOf("Path.GetFileName(mediaPath), lang, zaehlung, orderText);", etikettArg,
                StringComparison.Ordinal) > etikettArg,
            "Zeile @" + etikettZeile + ", Argument @" + etikettArg);

        Check("die Aussage der Zeile bleibt: Kandidaten JE gefragter Quelle und die Frage-Reihenfolge (F-M510)",
            downloadSource.Contains("{Counts}, order {Order}", StringComparison.Ordinal)
            && downloadSource.Contains("quellenReihenfolge.Select(quelle =>", StringComparison.Ordinal)
            && downloadSource.Contains(" + Api.Endpoints.Provenance.Name(quelle) + \" candidate(s)\"", StringComparison.Ordinal));

        Check("das Etikett ist die Adresse, die in der Frage-Reihenfolge auch wirklich steht (F-M510)",
            SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)[0]
                == Provenance.Parse(Provenance.Name(SourceOrder.FamilyPage(false)))
            && SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: true)[0]
                == Provenance.Parse(Provenance.Name(SourceOrder.FamilyPage(true)))
            && SourceOrder.ForMedia(PluginConfiguration.DownloadSourceOrder.TvSubtitlesFirst, isSeries: false)
                .Contains(SourceOrder.MainSearch));

        // Der Trockenlauf nennt jetzt auch, WOHER der gewaehlte Kandidat kommt: bei zwei gefragten
        // Quellen ist "wer hat gewonnen" genau die Frage, die er beantworten soll.
        var dryZeile = downloadSource.IndexOf(
            "\"[Sub Scribe-D] DRY-RUN {File} [{Lang}] → {Release} ({Source}, score {Score}, hi={Hi})\"",
            StringComparison.Ordinal);
        var dryHerkunft = dryZeile < 0
            ? -1
            : downloadSource.IndexOf("Provenance.Name(cand.Provider)", dryZeile, StringComparison.Ordinal);
        Check("die DRY-RUN-Zeile nennt die Herkunft des Gewinners, im selben Aufruf (F-M510)",
            dryZeile > 0 && dryHerkunft > dryZeile && dryHerkunft - dryZeile < 400,
            "Zeile @" + dryZeile + ", Herkunft @" + dryHerkunft);
        Check("die alte Form ohne Herkunft ist verschwunden — score und hi behalten ihren Platz (F-M510)",
            !downloadSource.Contains("{Release} (score {Score}, hi={Hi})", StringComparison.Ordinal)
            && downloadSource.Contains("({Source}, score {Score}, hi={Hi})", StringComparison.Ordinal));

        Check("die geteilten Zeilen nennen die Quelle aus der Liste — feste Etiketten bleiben bei ihrem Endpunkt (F-M510)",
            downloadSource.Contains("Api.Endpoints.Provenance.Name(quelle) + \" candidate(s)\"", StringComparison.Ordinal)
            && downloadSource.Contains("Provenance.Name(cand.Provider)", StringComparison.Ordinal)
            && uploadSource.Contains(
                "\"[\" + Api.Endpoints.Provenance.Name(quelle) + \"] DRY-RUN would upload {Name} [{Lang}]\"",
                StringComparison.Ordinal)
            && downloadSource.Contains("\"[moviesubtitles] {File} [{Lang}] — skipped: no TMDb id", StringComparison.Ordinal)
            && !downloadSource.Contains("\"[tvsubtitles] {File", StringComparison.Ordinal));

        // ---- 19. F-M512: die Medienart des Einreihers kommt aus dem STORE (TMDb-korrigiert) ----------
        // Befund 15.11.2026: ScribeSeeder las `item is Episode ? true : item is Movie ? false : null` —
        // also Jellyfins Typ. Die Pipelines korrigieren die Art aber aus Dateiname und TMDb
        // (UploadPipeline:819-826, :886) und schreiben sie in die Medienzeile; der Einreiher sah das
        // nie. Folge bei Abweichung: JF sagt Film, TMDb sagt Serie → nur die Filmadresse wurde
        // erwogen, eine offene tvst-Quelle bekam das Item NIE; umgekehrt wurde eine nie erfuellbare
        // Adresse in jedem Scan neu eingereiht (No-op-Laeufe). Nutzerentscheidung: "Ja den typ
        // augrund tmdb bestimmen". Der Store wird EINE Abfrage je Item gefragt (Primaerschluessel),
        // ohne Netz und ohne TMDb-Request im Scan.
        string kindRoot = Path.Combine(Path.GetTempPath(), "endpoints-medienart-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(kindRoot))
            {
                Directory.Delete(kindRoot, true);
            }

            Directory.CreateDirectory(kindRoot);
            using (var kindDb = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(Path.Combine(kindRoot, "db"), null))
            {
                var kindReg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(kindDb, null, null);

                // Die Zeile, die ein Lauf fuer eine SERIE schreibt: Kennungen, Staffel/Folge, IsSeries.
                const string serieHash = "a1a1a1a1b2b2b2b2";
                kindReg.EnsureFile(serieHash, patch: media =>
                {
                    media.ImdbId = "tt0903747";
                    media.TmdbId = "1396";
                    media.IsSeries = true;
                    media.Season = 1;
                    media.Episode = 3;
                });

                // Die Zeile eines AUFGELOESTEN Films: Kennungen, keine Serie.
                const string filmHash = "c3c3c3c3d4d4d4d4";
                kindReg.EnsureFile(filmHash, patch: media =>
                {
                    media.ImdbId = "tt15398776";
                    media.TmdbId = "872585";
                });

                // Die Zeile, die JEDER Lauf anlegt (Beobachtung, MarkSearched) — sie sagt NICHTS ueber die Art.
                const string stummHash = "e5e5e5e5f6f6f6f6";
                kindReg.EnsureFile(stummHash, "0123456789abcdef0123456789abcdef", "/media/films/stumm.mkv");

                bool? artSerie = kindReg.MediaKindIsSeries(serieHash);
                bool? artFilm = kindReg.MediaKindIsSeries(filmHash);
                bool? artStumm = kindReg.MediaKindIsSeries(stummHash);

                Check("der Store nennt die Serie, den aufgeloesten Film — und schweigt, wenn niemand die Art geprueft hat (F-M512)",
                    artSerie == true && artFilm == false && artStumm is null,
                    "serie=" + (artSerie?.ToString() ?? "<null>") + " film=" + (artFilm?.ToString() ?? "<null>")
                    + " stumm=" + (artStumm?.ToString() ?? "<null>"));

                Check("ohne Medienzeile (und ohne Hash) sagt der Store nichts — der Jellyfin-Typ entscheidet (F-M512)",
                    kindReg.MediaKindIsSeries("ffff000011112222") is null
                    && kindReg.MediaKindIsSeries(null) is null
                    && kindReg.MediaKindIsSeries(string.Empty) is null);

                // Die Medienart schneidet die Adressen GENAU so, wie der Einreiher sie schneidet
                // (UploadSources: `isSeries is not { } art || SourceOrder.AppliesTo(quelle, art)`).
                var adressen = new[]
                {
                    SubtitleProvider.Subdl, SubtitleProvider.TvSubtitles, SubtitleProvider.MovieSubtitles,
                };
                List<SubtitleProvider> Erwogen(bool? art) => art is { } a
                    ? adressen.Where(q => SourceOrder.AppliesTo(q, a)).ToList()
                    : adressen.ToList();

                var jfFilmStoreSerie = Erwogen(artSerie);      // Jellyfin sagt Film, der Store sagt Serie
                var jfFolgeStoreFilm = Erwogen(artFilm);       // Jellyfin sagt Folge, der Store sagt Film
                var ohneArt = Erwogen(null);                   // nirgends belegt

                Check("Jellyfin=Movie + Store=Serie: erwogen wird die SERIEN-Adresse, nicht die Film-Adresse (F-M512)",
                    jfFilmStoreSerie.Contains(SubtitleProvider.TvSubtitles)
                    && !jfFilmStoreSerie.Contains(SubtitleProvider.MovieSubtitles)
                    && jfFilmStoreSerie.Contains(SubtitleProvider.Subdl),
                    string.Join(",", jfFilmStoreSerie.Select(Provenance.Name)));

                Check("Jellyfin=Episode + Store=Film: erwogen wird die FILM-Adresse, nicht die Serien-Adresse (F-M512)",
                    jfFolgeStoreFilm.Contains(SubtitleProvider.MovieSubtitles)
                    && !jfFolgeStoreFilm.Contains(SubtitleProvider.TvSubtitles)
                    && jfFolgeStoreFilm.Contains(SubtitleProvider.Subdl),
                    string.Join(",", jfFolgeStoreFilm.Select(Provenance.Name)));

                Check("ohne belegte Art bleiben BEIDE Adressen stehen — 'lieber ein Item zu viel' (F-M499/F-M512)",
                    ohneArt.Count == 3 && ohneArt.Contains(SubtitleProvider.TvSubtitles)
                    && ohneArt.Contains(SubtitleProvider.MovieSubtitles),
                    string.Join(",", ohneArt.Select(Provenance.Name)));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(kindRoot))
                {
                    Directory.Delete(kindRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // Die Reihenfolge ist die Behauptung: STORE zuerst, Item-Typ nur als Rueckfall.
        var artAufruf = seederSource.IndexOf("bool? isSeries = MediaKindFor(item, mediaPath);", StringComparison.Ordinal);
        var artHelfer = seederSource.IndexOf("private static bool? MediaKindFor(BaseItem item, string mediaPath)", StringComparison.Ordinal);
        var artRueckfall = artHelfer < 0
            ? -1
            : seederSource.IndexOf("return item is Episode ? true : item is Movie ? false : null;", artHelfer, StringComparison.Ordinal);
        var artStore = artHelfer < 0 ? -1 : seederSource.IndexOf("StoredMediaKind(mediaPath)", artHelfer, StringComparison.Ordinal);
        var artBlock = artHelfer < 0 ? string.Empty : seederSource.Substring(artHelfer, Math.Min(2600, seederSource.Length - artHelfer));
        Check("der Einreiher fragt den STORE vor dem Item-Typ, und der Rueckfall steht nur dahinter (F-M512)",
            artAufruf > 0 && artHelfer > 0 && artStore > artHelfer && artRueckfall > artStore
            && !seederSource.Contains("isSeries = item is Episode ? true : item is Movie ? false : null;", StringComparison.Ordinal));

        Check("die Store-Frage ist EINE Abfrage ueber den Primaerschluessel — kein Netz, kein TMDb im Scan (F-M512)",
            registrySource.Contains("public bool? MediaKindIsSeries(string? mediaHash)", StringComparison.Ordinal)
            && registrySource.Contains("var row = _db.Files.FindById(mediaHash);", StringComparison.Ordinal)
            // der Einreiher-Block selbst traegt keinen Aufruf, der hinausgeht
            && !artBlock.Contains("Async(", StringComparison.Ordinal)
            && !artBlock.Contains("HttpClient", StringComparison.Ordinal)
            && !artBlock.Contains("_tmdb", StringComparison.Ordinal));

        // ---- F-M514: ein abgewiesenes Anhaengen darf den Store nicht vergiften ---------------------
        // Vorfall 16.11.2026 (jf-test 02:51:48): Der Download schrieb eine Seitentraeger-Zeile fuer
        // eine Datei, deren OSHash noch nicht bekannt war. `EnsureFile(null)` baute daraus eine
        // Medienzeile OHNE Schluessel; Entity Framework wies das Anhaengen ab ("primary key property
        // 'Id' is null") und liess die Instanz seit EF Core 10 als Detached im Change Tracker stehen.
        // Von da an warf JEDES SaveChanges dieses Kontexts "Unexpected entry.EntityState: Detached",
        // die Aufzeichnung der Worker konnte nichts mehr schreiben, und der Upload-Zyklus endete ohne
        // Lauf ("cycle finished without an upload run", 03:03 und 03:07 wiederholt).
        var ghostRoot = Path.Combine(Path.GetTempPath(), "endpoints-geist-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(ghostRoot))
            {
                Directory.Delete(ghostRoot, true);
            }

            Directory.CreateDirectory(ghostRoot);

            const string geisterHash = "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";
            int medienVorher;
            bool warfSeitentraeger = false;

            using (var ghostDb = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(Path.Combine(ghostRoot, "db"), null))
            {
                var ghostReg = new Jellyfin.Plugin.SubScribe.Registry.ContentHashRegistry(ghostDb, null, null);
                var ghostWorker = new Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry(ghostDb, null);
                medienVorher = ghostDb.Files.Count();

                // (a) GENAU der Aufruf des Vorfalls: Seitentraeger ohne bekannten Medien-Hash.
                try
                {
                    ghostReg.MarkDownloaded(geisterHash, null!, "EN",
                        fileName: "Sample Film 2 (2015).en.srt",
                        path: "/media/films/Sample Film 2 (2015).en.srt");
                }
                catch (Exception)
                {
                    warfSeitentraeger = true;
                }

                var geisterZeile = ghostDb.Sidecars.FindById(geisterHash);
                Check("ein Seitentraeger ohne bekannten Medien-Hash wirft nicht und legt keine Medienzeile ohne Schluessel an (F-M514)",
                    !warfSeitentraeger
                    && ghostDb.Files.Count() == medienVorher
                    && ghostDb.Files.FindAll().All(m => !string.IsNullOrEmpty(m.OsHash))
                    && geisterZeile != null
                    && string.IsNullOrEmpty(geisterZeile.MediaHash),
                    "warf=" + warfSeitentraeger + " medien=" + ghostDb.Files.Count() + "/" + medienVorher
                    + " randzeile=" + (geisterZeile == null ? "<fehlt>" : "'" + geisterZeile.MediaHash + "'"));

                // (b) Die Aufzeichnung, die im Vorfall zuerst warnte, schreibt weiter eine Zeile.
                ghostWorker.Start(Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.SeederKey, "Seeder", "F-M514");
                ghostWorker.Finish(Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.SeederKey,
                    Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Ok, "F-M514 fertig");

                // (c) Eine Zeile ohne Schluessel wird abgewiesen, nicht angehaengt — in beiden Formen.
                bool warfOhneSchluessel = false;
                try
                {
                    ghostDb.Files.Upsert(new Jellyfin.Plugin.SubScribe.Data.FileEntity
                    {
                        OsHash = null!,
                        Path = "/media/films/ohne-schluessel.mkv",
                    });
                    ghostDb.Files.Upsert(new Jellyfin.Plugin.SubScribe.Data.FileEntity
                    {
                        OsHash = string.Empty,
                        Path = "/media/films/leerer-schluessel.mkv",
                    });
                }
                catch (Exception)
                {
                    warfOhneSchluessel = true;
                }

                Check("eine Zeile ohne Geschaeftsschluessel wird nicht geschrieben und wirft nicht (F-M514)",
                    !warfOhneSchluessel
                    && ghostDb.Files.Count() == medienVorher
                    && ghostDb.Files.FindAll().All(m => !string.IsNullOrEmpty(m.OsHash)),
                    "warf=" + warfOhneSchluessel + " medien=" + ghostDb.Files.Count());

                // (d) Und der Store bleibt danach schreibfaehig — das ist der Punkt, an dem der
                //     Vorfall den ganzen Upload-Zyklus beendet hat.
                bool warfDanach = false;
                try
                {
                    ghostWorker.Start(Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.UploadWorkerKey, "Upload", "F-M514 danach");
                    ghostWorker.Finish(Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.UploadWorkerKey,
                        Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Ok, "F-M514 danach fertig");
                }
                catch (Exception)
                {
                    warfDanach = true;
                }

                Check("nach einem abgewiesenen Schluessel schreibt der Store weiter (F-M514)",
                    !warfDanach);
            }

            // Aus der DATEI gelesen, nicht aus dem Speicher: beide Worker-Zeilen stehen da.
            using (var ghostRead = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(Path.Combine(ghostRoot, "db"), null))
            {
                var seederZeile = ghostRead.WorkerRuns.FindById(Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.SeederKey);
                var uploadZeile = ghostRead.WorkerRuns.FindById(Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.UploadWorkerKey);
                Check("ein normaler Aufzeichnungslauf schreibt weiter eine Zeile — vor und nach dem abgewiesenen Schluessel (F-M514)",
                    seederZeile?.Outcome == Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Ok
                    && uploadZeile?.Outcome == Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Ok,
                    "seeder=" + (seederZeile?.Outcome ?? "<fehlt>") + " upload=" + (uploadZeile?.Outcome ?? "<fehlt>"));
            }

            // (e) Die Selbstheilung: ein vergifteter Tracker wird wieder benutzbar, und die offene
            //     Aenderung ueberlebt die Reparatur (sonst waere der Fix ein stiller Datenverlust).
            var heilDir = Path.Combine(ghostRoot, "heil");
            Directory.CreateDirectory(heilDir);
            using (var seed = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(heilDir, null))
            {
                seed.WorkerRuns.Upsert(new Jellyfin.Plugin.SubScribe.Data.WorkerRunEntity
                {
                    Id = "seeder",
                    Name = "Seeder",
                    Outcome = Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Never,
                });
            }

            bool warfNachGift = false;
            using (var gift = new Jellyfin.Plugin.SubScribe.Data.ScribeSqliteContext(
                Path.Combine(heilDir, Jellyfin.Plugin.SubScribe.Data.DbFiles.Name)))
            {
                // Der einzige Weg, den Geist heute noch zu erzeugen: am Set vorbei anhaengen.
                try
                {
                    gift.Add(new Jellyfin.Plugin.SubScribe.Data.FileEntity { FileId = null!, Path = "/x.mkv" });
                }
                catch (InvalidOperationException)
                {
                    // genau die Abweisung, die den Geist hinterlaesst
                }

                var offen = gift.WorkerRunRows.First(r => r.Id == "seeder");
                offen.Outcome = Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Ok;
                offen.Detail = "ueberlebte die Reparatur";

                try
                {
                    SaveNow(gift);
                }
                catch (Exception)
                {
                    warfNachGift = true;
                }
            }

            using (var heilRead = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(heilDir, null))
            {
                var zeile = heilRead.WorkerRuns.FindById("seeder");
                Check("ein vergifteter Change Tracker heilt beim Speichern und die offene Aenderung geht nicht verloren (F-M514)",
                    !warfNachGift
                    && zeile?.Detail == "ueberlebte die Reparatur"
                    && zeile?.Outcome == Jellyfin.Plugin.SubScribe.Registry.WorkerRunRegistry.Outcome.Ok,
                    "warf=" + warfNachGift + " outcome=" + (zeile?.Outcome ?? "<fehlt>")
                    + " detail=" + (zeile?.Detail ?? "<fehlt>"));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(ghostRoot))
                {
                    Directory.Delete(ghostRoot, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }

        // Die Wege, die den Geist gar nicht erst entstehen lassen — am Quelltext, damit ein spaeterer
        // Umbau sie nicht still entfernt.
        var dbQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Data/ScribeDbContext.cs"));
        var registryQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/ContentHashRegistry.cs"));
        var msubQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MsubMovieResolver.cs"));

        Check("EnsureMedia baut ohne Medien-Hash gar keine Zeile — warnen statt anhaengen (F-M514)",
            // F-M671: die Datei-Ebene heisst `file`, die Methode `EnsureFile`; der Hash bleibt die Adresse.
            dbQuelle.Contains("public FileEntity EnsureFile(string? osHash", StringComparison.Ordinal)
            && dbQuelle.Contains("media row NOT written: no media hash known", StringComparison.Ordinal));

        Check("das gemeinsame Set weist Schluessellose Zeilen ab — fuer JEDE Area und damit jeden Worker (F-M514)",
            dbQuelle.Contains("a row without a business key was not written", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.Matches(
                dbQuelle, "string.IsNullOrEmpty\\(key\\)|string.IsNullOrEmpty\\(_keyText\\(e\\)\\)").Count >= 3);

        Check("SaveChanges repariert einen Detached-Geist, statt fuer immer zu scheitern (F-M514)",
            dbQuelle.Contains("public override int SaveChanges()", StringComparison.Ordinal)
            && dbQuelle.Contains("Unexpected entry.EntityState", StringComparison.Ordinal)
            && dbQuelle.Contains("ChangeTracker.Clear();", StringComparison.Ordinal));

        Check("MarkSidecar reicht 'kein Medium bekannt' als leeren Wert durch — die Spalte ist NOT NULL (F-M514)",
            registryQuelle.Contains("public void MarkSidecar(string? contentHash, string? mediaHash", StringComparison.Ordinal)
            && registryQuelle.Contains("mediaHash ??= string.Empty;", StringComparison.Ordinal));

        Check("die Logzeile behauptet kein Speichern, wenn kein Medium die Nummer aufnehmen konnte (F-M514/F-M517)",
            msubQuelle.Contains("ist Film {Id} — {Note}", StringComparison.Ordinal)
            && msubQuelle.Contains("MediaNumberNote.Of(", StringComparison.Ordinal)
            && !msubQuelle.Contains("ist Film {Id} — gespeichert.", StringComparison.Ordinal)
            // F-M517: der Grund nennt BEIDE Adressen, nicht mehr nur den fehlenden Hash
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/Endpoints/MediaNumberNote.cs"))
                .Contains("no media hash and no unique path row in the store", StringComparison.Ordinal));

        Console.WriteLine();

        // ---- 5xer sidecars never go up (operator order 17.11.2026) -------------------------------
        // The original block (slot 50–99) holds the unsynchronized copies a correction keeps beside
        // its result. They are working files: uploading them would put the raw take next to the
        // corrected one under the same episode. The answer comes from the name alone.
        const string ep = "/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.mkv";
        Check("F-M519: the reserved original block is refused as an upload candidate",
            SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.en.51.srt", ep)
            && SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.en.52.srt", ep)
            && SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.en.50.srt", ep)
            && SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.de.sdh.99.srt", ep));
        Check("F-M519: the corrected block (slots 1–49) stays an upload candidate",
            !SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.en.01.srt", ep)
            && !SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.en.49.srt", ep)
            && !SidecarNaming.IsOriginalSidecar("/media/The Sample.S01E05.MULTI.1080p.WEB-DL.H265-GRP.de.srt", ep));
        Check("F-M519: the upload candidate list drops the originals and keeps the corrected files",
            Jellyfin.Plugin.SubScribe.Pipeline.UploadPipeline.FindLooseSrts(ep)
                .All(x => !SidecarNaming.IsOriginalSidecar(x.Path, ep)));


        // ---- F-M521: die tvst-Familie schreibt ihre Anfragen mit (Operator 17.11.2026) -------------
        // Vorher schwieg beide Adressen: „0 tvsubtitles candidate(s)" sah aus wie ein Lauf, der die
        // Seite nie gefragt hat. Zwei Zeilen je Anfrage auf Debug, die Kennung in der Sammelzeile.
        Check("F-M521: the request line names method and path, never the query",
            SiteTrace.Call("GET", "https://www.tvsubtitles.net/search.php?q=Grey%27s+Anatomy")
                == "API call GET https://www.tvsubtitles.net/search.php");
        Check("F-M521: the response line repeats the path and carries the status",
            SiteTrace.Response("POST", "https://www.moviesubtitles.org/add.php?tvid=13392", 302)
                == "API response POST https://www.moviesubtitles.org/add.php → 302");
        Check("F-M521: the summary text says how the site answered",
            SiteTrace.Answer(200, 3) == "HTTP 200, 3 hit(s)"
            && SiteTrace.Answer(0, 0) == "no answer, 0 hit(s)");
        Check("F-M521: both addresses of the family carry a debug channel",
            typeof(TvSubtitlesClient).GetEvent("DebugTrace") is not null
            && typeof(MovieSubtitlesClient).GetEvent("DebugTrace") is not null
            && typeof(MovieSubtitlesClient).GetProperty("LastStatus") is not null);
        string dlSource = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "DownloadPipeline.cs")).ConfigureAwait(false);
        string ulSource = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs")).ConfigureAwait(false);
        Check("F-M521: both directions subscribe the channel — download and upload",
            dlSource.Contains("_tvClient.DebugTrace += OnApiTrace", StringComparison.Ordinal)
            && dlSource.Contains("_msubClient.DebugTrace += OnApiTrace", StringComparison.Ordinal)
            && ulSource.Contains("_tvstClient.DebugTrace += OnApiTrace", StringComparison.Ordinal)
            && ulSource.Contains("_msubClient.DebugTrace += OnApiTrace", StringComparison.Ordinal));

        // ---- F-M524: ein gescheiterter Zyklus nennt die Stelle -------------------------------------
        string dispatchQuelle = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "ScribeEventDispatcher.cs"))
            .ConfigureAwait(false);
        Check(
            "F-M524: a failed cycle logs the exception with its stack — the message alone named no site",
            dispatchQuelle.Contains("_logger.LogError(\n", StringComparison.Ordinal)
            && dispatchQuelle.Contains("MarkCycleFailure(ex.Message)", StringComparison.Ordinal)
            && dispatchQuelle.Contains("cycle failed: {Msg} (see the exception below for the site)", StringComparison.Ordinal));

        // ---- F-M620: eine Datei zurueck in die Queue, ohne den Store zu verwerfen ------------------
        string resetDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeReset.cs")).ConfigureAwait(false);
        string registryDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Registry", "ContentHashRegistry.cs")).ConfigureAwait(false);
        string guiDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Configuration", "configPage.html")).ConfigureAwait(false);
        Check(
            "F-M620: the single-file reset is a POST route taking confirm, path and what",
            resetDatei.Contains("[HttpPost]", StringComparison.Ordinal)
            && resetDatei.Contains("public IActionResult ResetPath(", StringComparison.Ordinal)
            && resetDatei.Contains("[FromQuery] string path = \"\"", StringComparison.Ordinal)
            && resetDatei.Contains("[FromQuery] string what = \"both\"", StringComparison.Ordinal)
            // Der Bediener bedient ihn aus der GUI (Expert -> Plugin database), nicht per curl:
            // Eingabefeld, Auswahl und Knopf muessen die Route auch wirklich aufrufen.
            && guiDatei.Contains("id=\"ResetPathInput\"", StringComparison.Ordinal)
            && guiDatei.Contains("id=\"ResetPathWhat\"", StringComparison.Ordinal)
            && guiDatei.Contains("id=\"ResetPathRun\"", StringComparison.Ordinal)
            && guiDatei.Contains("Plugins/SubdlReset/ResetPath", StringComparison.Ordinal));
        // F-M723 (Betreiberanweisung 24.11.2026): ein Weg, der ALLES zu einer Datei nimmt — Spur-Zeilen
        // jeden Status, ihre Seitenwagen, Stempel und die Marken der Subjekte. Das ist bewusst NICHT der
        // Retry-Pfad (der laesst echte Urteile stehen), sondern der ausdrueckliche Wunsch des Bedieners.
        // F-M726 (Befund 24.11.2026 an "Yaga.S01E01"): ein Abbruch beim Zuruecklesen ist kein Fehler.
        // Vorher schrieb der allgemeine catch je Spur eine ERR-Zeile (24 fuer einen abgebrochenen
        // Vorlauf) und die Schleife lief weiter; die Methode gab danach ExitCode 0 plus lauter nulls
        // zurueck — von "diese Spur hat keinen Text" nicht zu unterscheiden.
        string ffmpegDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "FfmpegTools.cs")).ConfigureAwait(false);
        int leseStart = ffmpegDatei.IndexOf("foreach (var (idx, outFile) in outputs)", StringComparison.Ordinal);
        int leseEnde = ffmpegDatei.IndexOf("return (result, proc.ExitCode);", leseStart, StringComparison.Ordinal);
        string leseRumpf = leseStart > 0 && leseEnde > leseStart ? ffmpegDatei[leseStart..leseEnde] : string.Empty;
        Check(
            "F-M726: a cancelled read-back goes through instead of being logged once per stream",
            leseRumpf.Length > 0
            && leseRumpf.Contains("catch (OperationCanceledException) when (ct.IsCancellationRequested)", StringComparison.Ordinal)
            && leseRumpf.Contains("read-back of the extracted streams cancelled", StringComparison.Ordinal)
            // der Abbruch-Fang steht VOR dem allgemeinen Fang und reicht weiter
            && leseRumpf.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal)
                < leseRumpf.IndexOf("logger.LogError(\"[Sub Scribe] reading extracted stream", StringComparison.Ordinal)
            && leseRumpf.Contains("throw;", StringComparison.Ordinal));
        string forgetRumpf = registryDatei[
            registryDatei.IndexOf("public (int Tracks, int Sidecars, int Stamps, int Marks, int FoundTracks", StringComparison.Ordinal)..];
        forgetRumpf = forgetRumpf[..Math.Max(0, forgetRumpf.IndexOf("\n    public ", StringComparison.Ordinal) is int e && e > 0 ? e : forgetRumpf.Length)];
        Check(
            "F-M723: 'everything of this file' drops all rows of the file, not just rejections",
            resetDatei.Contains("bool alles = teile.Contains(\"all\")", StringComparison.Ordinal)
            && resetDatei.Contains("registry.ForgetMedia(hash);", StringComparison.Ordinal)
            && registryDatei.Contains("public (int Tracks, int Sidecars, int Stamps, int Marks, int FoundTracks, int FoundSidecars, bool Known) ForgetMedia(", StringComparison.Ordinal)
            // alle Spuren, nicht nur die abgelehnten
            && registryDatei.Contains("var spuren = _db.Tracks.FindAll()", StringComparison.Ordinal)
            // F-M723a: die Datei-Kennung ist der verlaessliche Schluessel — der Medienhash einer Zeile
            // kann von dem der Datei abweichen (neu gepackt), dann lief die Suche leer.
            && registryDatei.Contains("|| string.Equals(t.FileId, media.FileId, StringComparison.Ordinal))", StringComparison.Ordinal)
            && registryDatei.Contains("|| string.Equals(w.FileId, media.FileId, StringComparison.Ordinal)", StringComparison.Ordinal)
            // F-M727a: `Delete(kennung)` vergleicht gegen den Schluessel der Menge, nicht gegen die
            // Kennung — damit fielen 0 Zeilen. `Remove` nimmt die Zeile selbst.
            && registryDatei.Contains("if (_db.Sidecars.Remove(wagenZeile))", StringComparison.Ordinal)
            && registryDatei.Contains("if (_db.Tracks.Remove(spur))", StringComparison.Ordinal)
            && !registryDatei.Contains("_db.Sidecars.Delete(wagenZeile.TrackId)", StringComparison.Ordinal)
            && !registryDatei.Contains("_db.Tracks.Delete(spur.TrackId)", StringComparison.Ordinal)
            && registryDatei.Contains("marken += SourceStore.ForgetSubject(_db, subjekt);", StringComparison.Ordinal)
            // die Marken zuerst, sonst findet sie ihr Subjekt nicht mehr
            // die Marken zuerst — geprüft IM Rumpf von ForgetMedia, denn der Aufruf kommt in der Datei
            // sonst auch an anderen Stellen vor
            && forgetRumpf.IndexOf("ForgetSubject(_db, subjekt)", StringComparison.Ordinal)
                < forgetRumpf.IndexOf("_db.Tracks.Remove(spur)", StringComparison.Ordinal)
            && guiDatei.Contains("<option value=\"all\">everything of this file</option>", StringComparison.Ordinal));
        // F-M727 (Betreiber-Anweisung 24.11.2026 „Alle 4"): „0 geloescht" war keine Auskunft. Der Reset
        // meldet jetzt, wie viele Zeilen er VORHER gefunden hat und ob ihm die Datei ueberhaupt bekannt
        // war; Seitenwagen werden zusaetzlich ueber den Pfad-Stamm der Mediendatei gefunden.
        Check(
            "F-M727: the reset reports what it found, and sidecars answer to the file's own path stem",
            registryDatei.Contains("bool GehoertZurDatei(string? pfad) =>", StringComparison.Ordinal)
            && registryDatei.Contains("pfad.StartsWith(stamm + \".\", StringComparison.Ordinal)", StringComparison.Ordinal)
            && registryDatei.Contains("|| GehoertZurDatei(w.Path))", StringComparison.Ordinal)
            && registryDatei.Contains("return (0, 0, 0, 0, 0, 0, false);", StringComparison.Ordinal)
            && registryDatei.Contains("return (spurWeg, wagenWeg, stempel, marken, spuren.Count, wagen.Count, true);", StringComparison.Ordinal)
            && resetDatei.Contains("foundTracks = spurGefunden,", StringComparison.Ordinal)
            && resetDatei.Contains("foundSidecars = wagenGefunden,", StringComparison.Ordinal)
            && resetDatei.Contains("unknownFiles = dateienUnbekannt,", StringComparison.Ordinal));
        Check(
            "F-M620: without confirm=true and a rooted path nothing is written",
            resetDatei.Contains("error = \"confirmation-missing\"", StringComparison.Ordinal)
            && resetDatei.Contains("error = \"path-missing-or-relative\"", StringComparison.Ordinal)
            && resetDatei.Contains("System.IO.Path.IsPathRooted(path)", StringComparison.Ordinal));
        Check(
            "F-M620: the stamps of all three sources and the track signature fall",
            registryDatei.Contains("public (int Stamps, int Verdicts) ReopenForRetry(", StringComparison.Ordinal)
            // F-M668: die Stempel sind `file`-Zeilen der Gridfilm — sie fallen mit ihrer Signatur, weil die
            // Signatur ihr Fingerabdruck ist.
            && registryDatei.Contains("stempel = SourceStore.ClearFileMarks(_db, mediaHash);", StringComparison.Ordinal)
            && !registryDatei.Contains("m.CompleteSubdl = null", StringComparison.Ordinal));
        int reopenStart = registryDatei.IndexOf(
            "public (int Stamps, int Verdicts) ReopenForRetry(", StringComparison.Ordinal);
        int reopenEnd = reopenStart < 0
            ? -1
            : registryDatei.IndexOf("/// <summary>", reopenStart, StringComparison.Ordinal);
        string reopenBody = reopenStart >= 0 && reopenEnd > reopenStart
            ? registryDatei[reopenStart..reopenEnd]
            : string.Empty;
        Check(
            "F-M620: only rejected rows are deleted — an upload stays proof, not a verdict",
            reopenBody.Contains(
                "DeleteMany(x => x.MediaHash == mediaHash && x.Status == SubtitleStatus.Rejected)",
                StringComparison.Ordinal)
            && !reopenBody.Contains("SubtitleStatus.Uploaded", StringComparison.Ordinal),
            reopenBody.Length == 0 ? "ReopenForRetry not found" : null);
        Check(
            "F-M620: one line per call and the queues are reloaded right after",
            resetDatei.Contains("(F-M620)", StringComparison.Ordinal)
            && resetDatei.Contains("LogUtil.Normal(", StringComparison.Ordinal)
            && resetDatei.Contains("ReloadQueues()", StringComparison.Ordinal));

        // ---- F-M621: eine Zeile, deren Prozess starb, wird beim Start geschlossen --------------------
        string workerDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Registry", "WorkerRunRegistry.cs")).ConfigureAwait(false);
        string pluginDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Plugin.cs")).ConfigureAwait(false);
        int closeStart = workerDatei.IndexOf("public int CloseOpenRunsOnStartup(", StringComparison.Ordinal);
        int closeEnd = closeStart < 0
            ? -1
            : workerDatei.IndexOf("/// <summary>", closeStart, StringComparison.Ordinal);
        string closeBody = closeStart >= 0 && closeEnd > closeStart
            ? workerDatei[closeStart..closeEnd]
            : string.Empty;
        Check(
            "F-M621: the startup sweep closes every row still marked as running",
            closeBody.Contains("_db.WorkerRuns.FindAll()", StringComparison.Ordinal)
            && closeBody.Contains("Outcome.Running", StringComparison.Ordinal)
            && closeBody.Contains("row.Outcome = Outcome.Failed", StringComparison.Ordinal)
            && closeBody.Contains("row.Ended = now", StringComparison.Ordinal),
            closeBody.Length == 0 ? "CloseOpenRunsOnStartup not found" : null);
        Check(
            "F-M621: a restart is not a user stop — the sweep never writes cancelled",
            closeBody.Length > 0 && !closeBody.Contains("Outcome.Cancelled", StringComparison.Ordinal));
        Check(
            "F-M621: the plugin runs the sweep at startup and says so in one line",
            pluginDatei.Contains("CloseOpenRunsOnStartup(", StringComparison.Ordinal)
            && pluginDatei.Contains("(F-M621)", StringComparison.Ordinal)
            && pluginDatei.IndexOf("ClearOnStartup", StringComparison.Ordinal)
                < pluginDatei.IndexOf("CloseOpenRunsOnStartup(", StringComparison.Ordinal));

        // ---- F-M623: der aufgeschobene Zyklus bekommt eine eigene Zeile ------------------------------
        string coordDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "ScribeSchedulerCoordinator.cs")).ConfigureAwait(false);
        string statusDatei = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeStatusController.cs")).ConfigureAwait(false);
        Check(
            "F-M623: the coordinator reports the deferred cycle with its reason and attempt",
            coordDatei.Contains("public (DateTime? DueUtc, string? Why, int Attempt) GetCycleDeferral()", StringComparison.Ordinal)
            && coordDatei.Contains("_refetchFireWhy = \"run holds the lock\"", StringComparison.Ordinal)
            && coordDatei.Contains("public static int RescheduleAttemptLimit => MaxRescheduleAttempts;", StringComparison.Ordinal));
        Check(
            "F-M623: the cycle deferral is NOT smuggled into the worker fire map",
            coordDatei.Contains("Kept out of", StringComparison.Ordinal)
            && coordDatei.Contains("GetPendingFires deliberately", StringComparison.Ordinal)
            && !coordDatei.Contains("result[\"PipelineCycle\"]", StringComparison.Ordinal));
        Check(
            "F-M623: the answer carries CycleDue, CycleWhy, attempt and limit, and the page prints one line",
            statusDatei.Contains("CycleDue = cycleDue?.ToString(\"O\"", StringComparison.Ordinal)
            && statusDatei.Contains("CycleWhy = cycleWhy", StringComparison.Ordinal)
            && statusDatei.Contains("CycleAttemptLimit = cycleAttemptLimit", StringComparison.Ordinal)
            && guiDatei.Contains("id=\"CycleDueLine\"", StringComparison.Ordinal)
            && guiDatei.Contains("'Next cycle '", StringComparison.Ordinal)
            && guiDatei.Contains("d.CycleAttemptLimit", StringComparison.Ordinal));

        // -----------------------------------------------------------------------------------------
        // F-M738 (Befund 24.11.2026): "TMDb-Kennung weicht ab" wurde als "es wurde korrigiert"
        // gemeldet, und bei fehlender IMDb im Treffer blieb die ALTE IMDb neben der NEUEN
        // TMDb-Kennung stehen — ein Paar aus zwei Serien (Too Good to Be True: IMDb der 2024er
        // Serie, TMDb-Kennung des 2003er Zweiteilers).
        // -----------------------------------------------------------------------------------------
        var ohneTrefferImdb = Jellyfin.Plugin.SubScribe.Api.TmdbImdbResolver.MergeVerifiedIds(
            "tt30291270", "331627", null, "245996", true, true);
        Check(
            "F-M738: ohne IMDb im Treffer wird die alte IMDb VERWORFEN, nicht mit der neuen TMDb-Kennung gemischt",
            ohneTrefferImdb.Imdb == null && string.Equals(ohneTrefferImdb.Tmdb, "245996", StringComparison.Ordinal),
            $"imdb={ohneTrefferImdb.Imdb ?? "null"} tmdb={ohneTrefferImdb.Tmdb}");

        var gleich = Jellyfin.Plugin.SubScribe.Api.TmdbImdbResolver.MergeVerifiedIds(
            "tt6773088", "250203", "tt6773088", "250203", true, true);
        Check(
            "F-M738: stimmt das Paar ueberein, meldet die Pruefung KEINE Korrektur",
            !gleich.Corrected,
            "Corrected=" + gleich.Corrected);

        var echtAnders = Jellyfin.Plugin.SubScribe.Api.TmdbImdbResolver.MergeVerifiedIds(
            "tt6155146", "66515", "tt6773088", "250203", true, true);
        Check(
            "F-M738: eine wirklich andere Serie liefert beide Kennungen des Treffers und meldet die Korrektur",
            echtAnders.Corrected
            && string.Equals(echtAnders.Imdb, "tt6773088", StringComparison.Ordinal)
            && string.Equals(echtAnders.Tmdb, "250203", StringComparison.Ordinal),
            $"{echtAnders.Imdb}/{echtAnders.Tmdb} corrected={echtAnders.Corrected}");

        var typWechsel = Jellyfin.Plugin.SubScribe.Api.TmdbImdbResolver.MergeVerifiedIds(
            "tt30291270", "245996", "tt30291270", "245996", false, true);
        Check(
            "F-M738: auch ein Typwechsel (Serie zu Film und zurueck) gilt als Korrektur",
            typWechsel.Corrected && !typWechsel.IsSeries,
            "Corrected=" + typWechsel.Corrected + " IsSeries=" + typWechsel.IsSeries);

#pragma warning disable CA1849 // Pruefpfad, kein Serverpfad: die Dateien werden je einmal gelesen.
        var resolverQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/TmdbImdbResolver.cs"));
        var resetQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Api/ScribeReset.cs"));
#pragma warning restore CA1849
        Check(
            "F-M738: die alte Mischzeile ist weg — der Resolver entscheidet ueber MergeVerifiedIds",
            resolverQuelle.Contains("var entschieden = MergeVerifiedIds(", StringComparison.Ordinal)
            && !resolverQuelle.Contains("return (strict.Imdb ?? jfImdb, strict.Tmdb, strict.IsSeries, true);", StringComparison.Ordinal),
            "Rueckgabe kommt aus der reinen Rechnung");

        // -----------------------------------------------------------------------------------------
        // F-M739: die Staffelurteile vergessen. Sie haengen am Schluessel imdb|staffel|sprachtag;
        // nach einer Identitaetskorrektur sind die alten Zeilen unerreichbar, blockieren aber den
        // Blick in die Tabelle und wachsen weiter.
        // -----------------------------------------------------------------------------------------
        Check(
            "F-M739: der Reset kennt den Bereich \"pack\" und vergisst die Staffelurteile",
            resetQuelle.Contains("scope.Equals(\"pack\", StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal)
            && resetQuelle.Contains("PackSeasonStore.ClearAll()", StringComparison.Ordinal)
            && resetQuelle.Contains("PackSeasonStore.ClearSeason(imdb, season)", StringComparison.Ordinal),
            "scope=pack, wahlweise auf eine Serie/Staffel begrenzt");
        Check(
            "F-M739: ohne Kennung fallen alle Urteile, mit Kennung nur die einer Serie",
            resetQuelle.Contains("string.IsNullOrWhiteSpace(imdb)" + Environment.NewLine + "                    ? Registry.PackSeasonStore.ClearAll()", StringComparison.Ordinal),
            "ClearAll oder ClearSeason(imdb, season)");
        Check(
            "F-M739: der Reset nennt die Zahl der vergessenen Urteile",
            resetQuelle.Contains("{Count} stored season verdict(s) forgotten", StringComparison.Ordinal)
            && resetQuelle.Contains("forgotten = vergessen", StringComparison.Ordinal),
            "Antwort und Logzeile tragen die Zahl");
        Check(
            "F-M739: der andere Bereich (upload) bleibt unberuehrt — ein Pack-Reset laedt nichts neu hoch",
            resetQuelle.Contains("else if (scope.Equals(\"upload\", StringComparison.OrdinalIgnoreCase))", StringComparison.Ordinal)
            && resetQuelle.IndexOf("scope.Equals(\"pack\"", StringComparison.Ordinal)
               != resetQuelle.IndexOf("scope.Equals(\"upload\"", StringComparison.Ordinal),
            "getrennte Zweige");

        // -----------------------------------------------------------------------------------------
        // F-M740: eine Staffel vollstaendig aus dem Store nehmen. Der Store fuehrt zu einer Staffel
        // ein Geflecht (Dateien, Spuren, Beiwerk, Marken-Gridfilm, Behaelter, Urteile, ausgestrahlte
        // Folgen, Absagen am Medienhash, Titel-Erschoepfung am quelle|titel-Schluessel) — eine
        // Tabelle zu leeren laesst den Rest stehen, und der naechste Lauf haelt die Staffel weiter
        // fuer erledigt.
        // -----------------------------------------------------------------------------------------
#pragma warning disable CA1849 // Pruefpfad, kein Serverpfad.
        var purgeQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Registry/SeasonPurge.cs"));
#pragma warning restore CA1849
        Check(
            "F-M740: der Purge zaehlt zehn Tabellen — Dateien, Spuren, Beiwerk, Marken, Verweise, Behaelter, Urteile, ausgestrahlte Folgen, Absagen, Titelfehler",
            new[] { "\"files\"", "\"tracks\"", "\"sidecars\"", "\"uploads\"", "\"references\"", "\"packs\"", "\"season verdicts\"", "\"aired counts\"", "\"source refusals\"", "\"title failures\"" }
                .All(k => purgeQuelle.Contains("zahlen[" + k + "]", StringComparison.Ordinal)),
            "zehn Schluessel in der Zaehlung");
        Check(
            "F-M740: die Kinder fallen vor den Eltern — Marken zuerst, Dateien zuletzt",
            purgeQuelle.IndexOf("db.UploadRows.RemoveRange(hochgeladen)", StringComparison.Ordinal) < purgeQuelle.IndexOf("db.FileRows.RemoveRange(dateien)", StringComparison.Ordinal)
            && purgeQuelle.IndexOf("db.TrackRows.RemoveRange(spuren)", StringComparison.Ordinal) < purgeQuelle.IndexOf("db.FileRows.RemoveRange(dateien)", StringComparison.Ordinal),
            "Uploads und Spuren vor den Dateien");
        Check(
            "F-M740: dryRun zaehlt nur und loescht nichts",
            purgeQuelle.Contains("if (dryRun)" + Environment.NewLine + "            {" + Environment.NewLine + "                return (zahlen, false);", StringComparison.Ordinal),
            "Rueckkehr vor jedem RemoveRange");
        Check(
            "F-M740: Absagen werden ueber den Medienhash gefunden, Urteile ueber den Staffelprafix",
            purgeQuelle.Contains("SourceRefusalRows.Where(r => r.Id.StartsWith(praefix))", StringComparison.Ordinal)
            && purgeQuelle.Contains("PackSeasonRows.Where(r => r.Id.StartsWith(urteilPraeferenz))", StringComparison.Ordinal),
            "Hash und Schluessel statt Spaltenraten");
        Check(
            "F-M740: keine Abfrage auf das ungemappte FileId — Spuren und Beiwerk werden ueber ihre eigene Kennung gefunden",
            !purgeQuelle.Contains(".FileId ==", StringComparison.Ordinal)
            && !purgeQuelle.Contains("x.FileId", StringComparison.Ordinal)
            && purgeQuelle.Contains("db.TrackRows.Where(t => t.TrackId.StartsWith(praefix))", StringComparison.Ordinal),
            "TrackId traegt die Dateikennung (#yyyyy#zzz, F-M676)");
        Check(
            "F-M740: der Reset bietet den Bereich \"season\" mit dry an, und der Store wird angehaengt",
            resetQuelle.Contains("scope.Equals(\"season\", StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal)
            && resetQuelle.Contains("Registry.SeasonPurge.Purge(imdb, season, dry)", StringComparison.Ordinal)
#pragma warning disable CA1849 // Pruefpfad.
            && File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Plugin.cs")).Contains("SeasonPurge.Attach(db)", StringComparison.Ordinal),
#pragma warning restore CA1849
            "scope=season, dry-Parameter, Attach in Plugin.cs");

        // -----------------------------------------------------------------------------------------
        // F-M741: die Sprachauskunft der Quelle gehoert VOR das Sammeln. Stand sie nur im
        // Sende-Schritt, entpackte der Sammler die ganze Staffel fuer eine Quelle, die den Behaelter
        // nie annehmen kann — und das Paar blieb offen, weil nie etwas abgelehnt wurde.
        // -----------------------------------------------------------------------------------------
#pragma warning disable CA1849 // Pruefpfad, kein Serverpfad.
        var koordinatorQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/SeasonPackCoordinator.cs"));
        var pipelineQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Pipeline/UploadPipeline.cs"));
#pragma warning restore CA1849
        Check(
            "F-M741: der Sammler nimmt Quellen ohne die Sprache VOR dem Sammeln aus dem Slot",
            koordinatorQuelle.Contains("var sprachLos = ohnePack.Where(s => !Sources.SourceLanguages.NimmtSprache(s, lang)).ToList();", StringComparison.Ordinal)
            && koordinatorQuelle.Contains("var fehlt = ohnePack.Where(s => Sources.SourceLanguages.NimmtSprache(s, lang)).ToList();", StringComparison.Ordinal),
            "sprachLos und fehlt getrennt aus ohnePack");
        Check(
            "F-M741: bleibt keine Quelle, wird nichts gesammelt und der Grund genannt",
            koordinatorQuelle.Contains("no source carries this language ({Sources}), nothing is extracted (F-M741)", StringComparison.Ordinal)
            && koordinatorQuelle.Contains("if (fehlt.Count == 0)", StringComparison.Ordinal),
            "eine Zeile je Paar, kein Entpacken");
        Check(
            "F-M741: das HasPack-Tor darueber meldet weiter 'already up as a pack' — erst ohnePack.Count == 0",
            koordinatorQuelle.Contains("if (ohnePack.Count == 0)", StringComparison.Ordinal)
            && koordinatorQuelle.IndexOf("if (ohnePack.Count == 0)", StringComparison.Ordinal)
               < koordinatorQuelle.IndexOf("no source carries this language", StringComparison.Ordinal),
            "Reihenfolge: Behaelter vorhanden, dann Sprache");
        Check(
            "F-M741: der Einzelweg fragt dieselbe Auskunft schon vor der Extraktion (AnyUploadSourceStillWants)",
            pipelineQuelle.Contains("alsoTakes: key => Api.Endpoints.Provenance.Provider(key) is { } quelle", StringComparison.Ordinal)
            && pipelineQuelle.Contains("&& NimmtSprache(quelle, language, isSeries)", StringComparison.Ordinal)
            && pipelineQuelle.IndexOf("if (!AnyUploadSourceStillWants(mediaHash, lang, streamHi, isSeries, subPos))", StringComparison.Ordinal) > 0,
            "Einzelpaare entscheiden vor dem Entpacken");

        // -----------------------------------------------------------------------------------------
        // F-M742 (Betreiber 24.11.2026: "Ampel bitte auf die alten Texte zurueck. English. Keine
        // thematische Ueberschneidungen mit den Balken"): die Selbstauskunft der Quellen spricht
        // wieder Englisch wie vor dem dritten Vertrag, und die SubDL-Ampel wiederholt NICHT die
        // Kontingentzahlen — die stehen in den Balken darueber.
        // -----------------------------------------------------------------------------------------
#pragma warning disable CA1849 // Pruefpfad, kein Serverpfad.
        var subdlStatusQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Sources/Subdl/SubdlStatus.cs"));
        var reachQuelle = File.ReadAllText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync/Sources/ReachabilityStatus.cs"));
#pragma warning restore CA1849
        Check(
            "F-M742: die SubDL-Ampel nennt keine Kontingentzahlen mehr — nur 'reachable'",
            subdlStatusQuelle.Contains(": \"reachable\")", StringComparison.Ordinal)
            && !subdlStatusQuelle.Contains("SearchUsed}/{zahlen.SearchLimit}", StringComparison.Ordinal)
            && !subdlStatusQuelle.Contains("DownloadsUsed}/{zahlen.DownloadsLimit}", StringComparison.Ordinal),
            "Zahlen gehoeren in die Balken");
        Check(
            "F-M742: die Erreichbarkeits-Ampel sagt 'reachable', nicht 'erreichbar (HTTP 200)'",
            reachQuelle.Contains("? \"reachable\" :", StringComparison.Ordinal)
            && reachQuelle.Contains("$\"HTTP {code}\"", StringComparison.Ordinal)
            && !reachQuelle.Contains("\"erreichbar", StringComparison.Ordinal),
            "Code nur, wenn er etwas zu sagen hat");
        Check(
            "F-M742: die Login-Ampel nennt die drei Faelle englisch, Erfolg ist 'OK'",
            subdlStatusQuelle.Contains("meldung = \"OK\";", StringComparison.Ordinal)
            && subdlStatusQuelle.Contains("\"wrong username/password\"", StringComparison.Ordinal)
            && subdlStatusQuelle.Contains("\"not authorized — check API key\"", StringComparison.Ordinal)
            && subdlStatusQuelle.Contains("\"rate-limited — try again later\"", StringComparison.Ordinal),
            "falsches Passwort, falscher Schluessel, zu schnell");
        Check(
            "F-M742: im Statusweg steht kein deutscher Meldungstext mehr",
            !new[] { subdlStatusQuelle, reachQuelle }.Any(q => q.Contains("Message: \"kein", StringComparison.Ordinal)
                || q.Contains("Message: \"die API", StringComparison.Ordinal)
                || q.Contains("meldung = \"angemeldet\"", StringComparison.Ordinal)),
            "Englisch wie vor dem dritten Vertrag");

        // ---- 22. F-M769: das Plugin misst seinen eigenen Speicher ----------------------------
        // Anlass: die Behauptung "Jellyfin frisst RAM, und davon das Plugin unkontrolliert" liess
        // sich von aussen nicht pruefen — es gab keine Zahl aus dem Prozess. Der Diagnose-Endpunkt
        // fuehrt sie jetzt: GC-Heap, Arbeitsseite, private Bytes, GC-Zaehler und die Zeilen, die der
        // geteilte Kontext mitfuehrt. Ohne TrackedRows waere der groesste eigene Posten unsichtbar.
        string diagQuelle = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeDiagnosticsController.cs")).ConfigureAwait(false);
        Check(
            "F-M769: the diagnostics endpoint reports the process memory it lives in (GcHeap, WorkingSet, Private, Gen0-2, TrackedRows)",
            diagQuelle.Contains("Memory = new", StringComparison.Ordinal)
            && diagQuelle.Contains("GcHeapBytes = GC.GetTotalMemory(false)", StringComparison.Ordinal)
            && diagQuelle.Contains("WorkingSetBytes = Environment.WorkingSet", StringComparison.Ordinal)
            && diagQuelle.Contains("PrivateBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64", StringComparison.Ordinal)
            && diagQuelle.Contains("Gen2 = GC.CollectionCount(2)", StringComparison.Ordinal),
            "vier Zahlen und drei Zaehler");
        Check(
            "F-M769: and it counts the tracked rows of the shared context — the biggest item the plugin holds itself",
            diagQuelle.Contains("TrackedRows = db.Context.ChangeTracker.Entries().Count()", StringComparison.Ordinal),
            "ChangeTracker am geteilten Kontext");
        Check(
            "F-M769: the reading takes no forced collection — it must not change what it measures",
            !diagQuelle.Contains("GC.Collect()", StringComparison.Ordinal)
            && diagQuelle.Contains("GC.GetTotalMemory(false)", StringComparison.Ordinal),
            "kein GC.Collect im Endpunkt");

        // ---- 23. F-M770: der Leser liest ohne Gedaechtnis --------------------------------------
        // Anlass: der Diagnose-Endpunkt holte Tracks, Sidecars und Files ueber den Schreibweg, der die
        // ganze Tabelle tracked und fuer die Lebensdauer des geteilten Kontexts haelt — rund 13.000
        // Zeilen, die erst ein Neustart freigab. Ein Leser darf nichts hinterlassen; der Schreibweg
        // bleibt, wo geschrieben wird.
        string speicherQuelle = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Data", "ScribeDbContext.cs")).ConfigureAwait(false);
        Check(
            "F-M770: the store offers a read-only snapshot that neither tracks nor fills the row memory",
            speicherQuelle.Contains("public List<TEntity> ReadOnlySnapshot()", StringComparison.Ordinal)
            && speicherQuelle.Contains("return _db.Set<TEntity>().AsNoTracking().ToList();", StringComparison.Ordinal),
            "ReadOnlySnapshot ohne Verfolgung");
        Check(
            "F-M770: and a count that does not pull the rows into the context at all",
            speicherQuelle.Contains("public int CountReadOnly()", StringComparison.Ordinal)
            && speicherQuelle.Contains("return _db.Set<TEntity>().Count();", StringComparison.Ordinal),
            "CountReadOnly");
        Check(
            "F-M770: the diagnostics endpoint reads every big area through the read-only path",
            !diagQuelle.Contains("db.Tracks.FindAll()", StringComparison.Ordinal)
            && !diagQuelle.Contains("db.Sidecars.FindAll()", StringComparison.Ordinal)
            && !diagQuelle.Contains("db.Files.FindAll()", StringComparison.Ordinal)
            && !diagQuelle.Contains("db.Counters.FindAll()", StringComparison.Ordinal)
            && diagQuelle.Contains("db.Tracks.ReadOnlySnapshot()", StringComparison.Ordinal)
            && diagQuelle.Contains("db.Sidecars.ReadOnlySnapshot()", StringComparison.Ordinal)
            && diagQuelle.Contains("db.Files.ReadOnlySnapshot()", StringComparison.Ordinal)
            && diagQuelle.Contains("db.Oshashes.CountReadOnly()", StringComparison.Ordinal),
            "kein FindAll mehr im Endpunkt");
        Check(
            "F-M770: and it says in the log what the read cost — rows, milliseconds, tracked rows afterwards",
            diagQuelle.Contains("Stopwatch.StartNew()", StringComparison.Ordinal)
            && diagQuelle.Contains("Pipeline.LogUtil.Detail(", StringComparison.Ordinal)
            && diagQuelle.Contains("{Tracked} row(s) tracked after it (F-M770)", StringComparison.Ordinal),
            "Debug-Zeile je Abruf");

        // ---- 24. F-M771: die Sperre ist eine Pause, kein Ende --------------------------------
        // Anlass: der Bediener sagte am 27.11.2026 „Naja, nach 7 Tagen könnte man erneut schauen." Bis
        // dahin hielt eine Titelsperre fuer immer, und weil ein geschlossener Titel die Leiter
        // ueberspringt, konnte ihn ausser einem Reset nichts mehr oeffnen. Die Pause wird gerechnet,
        // nicht geplant: kein Timer, keine zweite Tabelle.
        var sperrPause = Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ProbePause;
        var geschlossen = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var beiSchluesselA = Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ReopenAt(geschlossen, "tvsubtitles|tt0133093");
        var nochmalA = Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ReopenAt(geschlossen, "tvsubtitles|tt0133093");
        int versatzMinuten = (int)(beiSchluesselA - geschlossen - sperrPause).TotalMinutes;
        Check(
            "F-M771: a closed title stays closed for seven days, and the probe never opens before that",
            sperrPause == TimeSpan.FromDays(7) && beiSchluesselA >= geschlossen + sperrPause,
            $"{sperrPause.TotalDays:0} Tag(e), frueheste Probe {beiSchluesselA:HH:mm}");
        Check(
            "F-M771: the probe is spread over a few hours, and the offset is derived from the lock key so it does not wander",
            versatzMinuten >= 0
            && versatzMinuten < Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ProbeSpreadHours * 60
            && beiSchluesselA == nochmalA,
            $"Versatz {versatzMinuten} min, zweimal gelesen = gleich");
        var versaetze = new HashSet<int>();
        for (int i = 0; i < 200; i++)
        {
            var zeit = Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ReopenAt(geschlossen, "tvsubtitles|tt" + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture));
            versaetze.Add((int)(zeit - geschlossen - sperrPause).TotalMinutes);
        }

        Check(
            "F-M771: closed titles do not all ask in the same minute — the spread really spreads",
            versaetze.Count >= 150 && versaetze.Max() - versaetze.Min() > 180,
            $"{versaetze.Count} verschiedene Versaetze ueber {versaetze.Max() - versaetze.Min()} min");

        string leiter771 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "TitleLadder.cs"));
        string store771 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Registry", "TitleFailureStore.cs"));
        Check(
            "F-M771: a probe is one attempt and not four, and both ends say so in the log",
            leiter771.Contains("TitleFailureStore.IsProbe(quelle, titelId)", StringComparison.Ordinal)
            && store771.Contains("public static bool IsProbe(string source, string titleId)", StringComparison.Ordinal)
            && store771.Contains("reopened for a probe after {Days:0} day(s)", StringComparison.Ordinal)
            && store771.Contains("closed and written until {Until:yyyy-MM-dd HH:mm}", StringComparison.Ordinal),
            "ein Zug bei der Probe, zwei Logzeilen");

        // ---- 25. F-M772 bis F-M778: die eine TMDb-Ablage -------------------------------------
        // Anlass: drei halbe Speicher (Resolver-Fragen, HTTP-Kette, Staffel-Index) mit einer Tabelle und
        // globalen Fristen. Gemessen am 27.11.2026: eine Suche 2.327 B, ein Serien-Detail 55.963 B, die
        // Tatsache daraus 66 B. Jetzt: eigene Datei, Tatsachen und Rohantworten getrennt, Fristen nach Art.
        Check(
            "F-M775: an identifier TMDb stated lives a year, a guessed one a month, a miss a day, anything else half a year",
            Jellyfin.Plugin.SubScribe.Api.TmdbCache.LifetimeOfFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated, "tt0133093") == TimeSpan.FromDays(365)
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.LifetimeOfFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierGuessed, "603") == TimeSpan.FromDays(30)
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.LifetimeOfFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.Details, "136") == TimeSpan.FromDays(180)
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.LifetimeOfFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated, null) == TimeSpan.FromDays(1),
            "365 / 30 / 180 Tage, Niete 1");
        Check(
            "F-M776: a search answer is kept one day, every other raw answer three",
            Jellyfin.Plugin.SubScribe.Api.TmdbCache.LifetimeOfBody("url:https://api.themoviedb.org/3/search/tv?query=x") == TimeSpan.FromDays(1)
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.LifetimeOfBody("url:https://api.themoviedb.org/3/tv/1416?language=de-DE") == TimeSpan.FromDays(3),
            "Suche 1 Tag, sonst 3");
        Check(
            "F-M775: a question by number counts as stated, a question carrying a title as guessed",
            Jellyfin.Plugin.SubScribe.Api.TmdbCache.KindOfQuestion("id:1416|S") == Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.KindOfQuestion("imdb:tt0133093|S") == Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.KindOfQuestion("movie:603|M") == Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.KindOfQuestion("multi:CUNK ON CINEMA|2026|strict|S") == Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierGuessed
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.KindOfQuestion("q:CUNK ON CINEMA|2026|M") == Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierGuessed,
            "Schluessel entscheidet, nicht der Aufrufer");

        // Echter Durchlauf gegen eine eigene Datei: schreiben, lesen, Niete, putzen.
        string ablageOrdner773 = Path.Combine(Path.GetTempPath(), "tmdbcache-" + Guid.NewGuid().ToString("N"));
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Attach(ablageOrdner773, null);
        bool angelegt773 = Jellyfin.Plugin.SubScribe.Api.TmdbCache.Enabled
            && File.Exists(Path.Combine(ablageOrdner773, Jellyfin.Plugin.SubScribe.Api.TmdbCache.FileName));
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.PutFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated, "id:1416|S", "tt0133093");
        bool treffer = Jellyfin.Plugin.SubScribe.Api.TmdbCache.TryGetFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated, "id:1416|S", out string? gelesen773);
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.PutFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated, "id:999999999|S", null);
        bool niete773 = Jellyfin.Plugin.SubScribe.Api.TmdbCache.TryGetFact(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated, "id:999999999|S", out string? leer773);
        var stand773 = Jellyfin.Plugin.SubScribe.Api.TmdbCache.Stats();
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Clear();
        bool geputzt773 = !Jellyfin.Plugin.SubScribe.Api.TmdbCache.Enabled
            && !File.Exists(Path.Combine(ablageOrdner773, Jellyfin.Plugin.SubScribe.Api.TmdbCache.FileName));
        Check(
            "F-M772: the shelf is one file of its own, it keeps a fact, it knows a miss, and a purge removes the file",
            angelegt773 && treffer && gelesen773 == "tt0133093" && niete773 && leer773 == null && geputzt773,
            $"angelegt {angelegt773}, Treffer \"{gelesen773}\", Niete {niete773}, geputzt {geputzt773}");
        Check(
            "F-M778: it reports its state — rows and bytes per kind, file size, both ceilings",
            stand773.Enabled
            && stand773.FactRows >= 2
            && stand773.FactBytes > 0
            && stand773.FileBytes > 0
            && stand773.FactCeilingBytes == 16L * 1024 * 1024
            && stand773.BodyCeilingBytes == 16L * 1024 * 1024
            && stand773.FactsByKind.ContainsKey(Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated)
            && Jellyfin.Plugin.SubScribe.Api.TmdbCache.MemoryMirrorCap == 5000,
            $"{stand773.FactRows} Zeile(n), {stand773.FactBytes} B, Datei {stand773.FileBytes} B");

        string handler773 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "TmdbCacheHandler.cs"));
        string antwortStore = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Registry", "TmdbAnswerStore.cs"));
        string leiter777 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "TitleLadder.cs"));
        string plugin772 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Plugin.cs"));
        Check(
            "F-M773/F-M777: raw answers go through the shelf, the resolver's answers become facts, and the shelf never closes a title",
            handler773.Contains("TmdbCache.TryGetBody(key", StringComparison.Ordinal)
            && handler773.Contains("TmdbCache.PutBody(key, body)", StringComparison.Ordinal)
            && handler773.Contains("TmdbCache.NoteNetworkRequest()", StringComparison.Ordinal)
            && antwortStore.Contains("Api.TmdbCache.TryGetFact(", StringComparison.Ordinal)
            && antwortStore.Contains("Api.TmdbCache.PutFact(", StringComparison.Ordinal)
            && leiter777.Contains("Api.TmdbCache.NetworkRequests == anfragenVorher", StringComparison.Ordinal)
            && leiter777.Contains("counted, not closed (F-M777)", StringComparison.Ordinal)
            && plugin772.Contains("Api.TmdbCache.Attach(dataFolder, logger, logMode)", StringComparison.Ordinal),
            "Handler, Resolver, Leiter und Start haengen an der Ablage");

        // ---- 26. F-M779/F-M780: die zwei Eigenwege und der Doppelschreiber ----------------------
        // Anlass (Befund vom 27.11.2026): der Staffel-Index baute sich einen eigenen HttpClient und ging an
        // der Ablage vorbei, und der Antwort-Store schrieb jede Auskunft ZWEIMAL — in die Ablage und in die
        // alte Tabelle. Beides ist jetzt gerade gezogen; die Statusprobe bleibt die eine Ausnahme.
        string index779 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "SeasonEpisodeIndex.cs"));
        string status779 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeStatusController.cs"));
        int eigeneTmdbClients = System.Text.RegularExpressions.Regex.Matches(
            index779,
            @"new HttpClient\s*\{").Count;
        Check(
            "F-M779: the season index hangs on the same shelf instead of fetching past it",
            // F-M783 hat die Kette erweitert (Ablage aussen, Takt innen) — der Punkt bleibt: der
            // Staffel-Index holt nicht mehr am Regal vorbei.
            index779.Contains("new TmdbCacheHandler(", StringComparison.Ordinal)
            && eigeneTmdbClients == 1
            && status779.Contains("geht BEWUSST an der Ablage vorbei", StringComparison.Ordinal),
            $"{eigeneTmdbClients} nackte(r) Client(s) — nur der IMDb-Weg, die Statusprobe bleibt Ausnahme");
        string store780 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Registry", "TmdbAnswerStore.cs"));
        int putBlock = store780.IndexOf("public static void Put((string, bool) key", StringComparison.Ordinal);
        int putEnde = store780.IndexOf("private static void RetireOldTable()", putBlock, StringComparison.Ordinal);
        string put780 = store780[putBlock..putEnde];
        Check(
            "F-M780: an answer is written once — the old table is no longer filled",
            put780.Contains("Api.TmdbCache.PutFact(", StringComparison.Ordinal)
            && !put780.Contains("TmdbAnswerRows.Add", StringComparison.Ordinal)
            && !put780.Contains("SaveChanges", StringComparison.Ordinal),
            "nur noch die Ablage, kein zweiter Schreibweg");
        Check(
            "F-M781: the old answer table is dropped once — and nothing is taken over from it",
            store780.Contains("RetireOldTable()", StringComparison.Ordinal)
            && store780.Contains("DROP TABLE IF EXISTS {OldTable}", StringComparison.Ordinal)
            && !store780.Contains("SELECT Id, Value, Written", StringComparison.Ordinal)
            && !store780[store780.IndexOf("private static void RetireOldTable()", StringComparison.Ordinal)..].Contains("PutFact(", StringComparison.Ordinal),
            "nur DROP, keine Uebernahme in die Ablage");
        string db781 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Data", "ScribeDbContext.cs"));
        Check(
            "F-M781: the retired table is no longer created at startup",
            !db781.Contains("EnsureTmdbAnswersTable", StringComparison.Ordinal)
            && !db781.Contains("DbSet<TmdbAnswerEntity>", StringComparison.Ordinal)
            && !store780.Contains("TmdbAnswerEntity", StringComparison.Ordinal),
            "kein CREATE TABLE, kein DbSet, keine Entity");
        string refresh782 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "ScheduledTasks", "ScribeDatabaseRefreshTask.cs"));
        string cache782 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "TmdbCache.cs"));
        Check(
            "F-M782: the refresh folds the TMDb shelf with the same run, and the shelf reports file plus journal",
            refresh782.Contains("Api.TmdbCache.Compact()", StringComparison.Ordinal)
            && refresh782.Contains("TMDb shelf compacted", StringComparison.Ordinal)
            && cache782.Contains("VACUUM;", StringComparison.Ordinal)
            && cache782.Contains("PRAGMA wal_checkpoint(TRUNCATE)", StringComparison.Ordinal)
            && cache782.Contains("-wal", StringComparison.Ordinal),
            "Compact im Refresh, VACUUM in der Ablage, Groesse inkl. Journal");

        // F-M782 am echten Fall: eine Ablage, die Zeilen abgibt, haelt die Seiten — erst das Falten gibt sie zurueck.
        string kompaktOrdner = Path.Combine(Path.GetTempPath(), "tmdbcompact-" + Guid.NewGuid().ToString("N"));
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Attach(kompaktOrdner, null);
        for (int n = 0; n < 200; n++)
        {
            Jellyfin.Plugin.SubScribe.Api.TmdbCache.PutFact(
                Jellyfin.Plugin.SubScribe.Api.TmdbCache.Kinds.IdentifierStated,
                "id:" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|S",
                "tt" + n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DateTime.UtcNow.AddDays(-400));
        }

        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Trim();
        long vorFalten = Jellyfin.Plugin.SubScribe.Api.TmdbCache.Stats().FileBytes;
        var (faltenVor, faltenNach) = Jellyfin.Plugin.SubScribe.Api.TmdbCache.Compact();
        long nachFalten = Jellyfin.Plugin.SubScribe.Api.TmdbCache.Stats().FileBytes;
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Clear();
        Check(
            "F-M782: folding really returns the pages a shelf gave up — the file gets smaller",
            vorFalten > 0 && faltenNach < faltenVor && nachFalten <= faltenVor,
            $"{vorFalten} B vor dem Falten, {faltenNach} B danach");



        // ---- 27. F-M783: der Takt haengt an der ECHTEN Anfrage, nicht am Nachschlagen ---------------
        // Anlass (Operator-Anweisung 27.11.2026): „Aehnlich wie beim upload/download soll fuer tmdb das
        // rate limit nur gelten, wenn tmdb tatsaechlich angefragt wird, nicht wenn der cache benutzt
        // wird." Die Kette ist Ablage -> Retry -> Takt -> Netz, also beantwortet ein Treffer die Anfrage,
        // BEVOR der Takt ueberhaupt erreicht wird. Gemessen statt behauptet.
        string taktOrdner = Path.Combine(Path.GetTempPath(), "tmdbtakt-" + Guid.NewGuid().ToString("N"));
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Attach(taktOrdner, null);
        Jellyfin.Plugin.SubScribe.Api.ExternalApiPacer.Configure(0.5);
        var zaehler = new CountingInnerHandler();
        using var taktClient = new HttpClient(
            new Jellyfin.Plugin.SubScribe.Api.TmdbCacheHandler(
                new Jellyfin.Plugin.SubScribe.Api.PacingHandler(zaehler)));
        const string TaktUrl = "https://api.themoviedb.org/3/tv/1416?api_key=egal&language=de-DE";

        var uhr = System.Diagnostics.Stopwatch.StartNew();
        using (var erste = await taktClient.GetAsync(TaktUrl))
        {
            uhr.Stop();
        }

        long msFehltreffer = uhr.ElapsedMilliseconds;
        int aufrufeNachFehltreffer = zaehler.Calls;

        var uhr2 = System.Diagnostics.Stopwatch.StartNew();
        using (var zweite = await taktClient.GetAsync(TaktUrl))
        {
            uhr2.Stop();
        }

        long msTreffer = uhr2.ElapsedMilliseconds;
        int aufrufeNachTreffer = zaehler.Calls;
        Jellyfin.Plugin.SubScribe.Api.ExternalApiPacer.Configure(0);
        Jellyfin.Plugin.SubScribe.Api.TmdbCache.Clear();

        Check(
            "F-M783: a miss pays the API pause — the pause hangs on the real request",
            aufrufeNachFehltreffer == 1 && msFehltreffer >= 450,
            $"1 echter Abruf, {msFehltreffer} ms (Takt 500 ms)");
        Check(
            "F-M783: a cache hit pays nothing — the shelf answers before the pause is reached",
            aufrufeNachTreffer == 1 && msTreffer < 200,
            $"kein zweiter Abruf, {msTreffer} ms statt >= 500");
        Check(
            "F-M783: the season index and the status probe hang in the same order — shelf outside, pause inside",
            ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "SeasonEpisodeIndex.cs"))
                .Contains("new TmdbCacheHandler(\n                    new TransientRetryHandler(\n                        new PacingHandler(new SocketsHttpHandler())))", StringComparison.Ordinal)
            && ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeStatusController.cs"))
                .Contains("new HttpClient(new PacingHandler(new SocketsHttpHandler()))", StringComparison.Ordinal),
            "Ablage aussen, Takt innen — auch dort");


        // ---- 28. F-M784 bis F-M788: der Extraktions-Speicher ---------------------------------------
        // Anlass (Operator-Auftrag 27.11.2026): „Bau den extraktionscache. Kann der laufcache dann weg und
        // durch den extraktionscache ersetzt werden?" — ja: was ein Lauf auspackt, liegt jetzt im
        // gemeinsamen Speicher und ueberlebt den Lauf; der Lauf-Cache ist geloescht.
        string spurOrdner = Path.Combine(Path.GetTempPath(), "spuren-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(spurOrdner);
        string probeFilm = Path.Combine(spurOrdner, "probe.mkv");
        await File.WriteAllBytesAsync(probeFilm, new byte[1024]);
        var spurenSpeicher = new Jellyfin.Plugin.SubScribe.Api.ArchiveDiskCache(null);
        spurenSpeicher.Attach(null);
        spurenSpeicher.Configure(spurOrdner, 14, 64);
        string spurtText = "1\r\n00:00:01,000 --> 00:00:03,000\r\nHallo, Welt.\r\n";
        bool geschrieben = Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.Put(spurenSpeicher, "abc123", probeFilm, 3, spurtText);
        bool gelesen = Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.TryGet(spurenSpeicher, "abc123", probeFilm, 3, out string? zurueck);
        Check(
            "F-M784: a track survives the run — write it, read it back, byte for byte",
            geschrieben && gelesen && zurueck == spurtText,
            $"geschrieben {geschrieben}, gelesen {gelesen}, {zurueck?.Length ?? 0} Zeichen");
        byte[]? koerper784 = null;
        foreach (string datei in Directory.EnumerateFiles(Path.Combine(spurOrdner, "archive-cache"), "*.zip"))
        {
            koerper784 = await File.ReadAllBytesAsync(datei);
        }

        string? mitglied784 = null;
        int mitglieder784 = 0;
        if (koerper784 != null)
        {
            using var archiv = new System.IO.Compression.ZipArchive(new MemoryStream(koerper784), System.IO.Compression.ZipArchiveMode.Read);
            mitglieder784 = archiv.Entries.Count;
            if (mitglieder784 == 1)
            {
                mitglied784 = archiv.Entries[0].FullName;
            }
        }

        string spurSchluessel = Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.KeyFor("abc123", probeFilm, 3) ?? string.Empty;
        Check(
            "F-M784: one track is one ZIP with one member, and its name carries no path",
            mitglieder784 == 1
            && mitglied784 == Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.MemberName
            && spurSchluessel.StartsWith(Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.KeyPrefix, StringComparison.Ordinal)
            && !spurSchluessel.Contains("probe", StringComparison.OrdinalIgnoreCase)
            && !spurSchluessel.Contains("spuren-", StringComparison.OrdinalIgnoreCase),
            $"{mitglieder784} Mitglied(er), Name \"{mitglied784}\", Schluessel \"{spurSchluessel}\"");
        byte[] gleichA = Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.Pack(spurtText);
        byte[] gleichB = Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.Pack(spurtText);
        Check(
            "F-M787: the body is built deterministically, so a hit and a fresh extraction differ in nothing",
            gleichA.SequenceEqual(gleichB) && koerper784 != null && koerper784.SequenceEqual(gleichA) && mitglied784 == Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.MemberName,
            $"{gleichA.Length} B, zweimal gleich");
        await File.WriteAllBytesAsync(probeFilm, new byte[2048]);
        Check(
            "F-M785: a replaced container is a miss — the name carries size and times",
            !Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.TryGet(spurenSpeicher, "abc123", probeFilm, 3, out _),
            "nach dem Ersetzen kein Treffer");
        int vorher784 = spurenSpeicher.Count();
        string kaputt784 = Path.Combine(Path.GetDirectoryName(Directory.EnumerateFiles(Path.Combine(spurOrdner, "archive-cache"), "*.zip").First())!, "absichtlich-kaputt.zip");
        await File.WriteAllBytesAsync(kaputt784, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 });
        Check(
            "F-M787: an entry that will not unpack is removed, not repaired",
            Jellyfin.Plugin.SubScribe.Api.ExtractedTrackStore.Unpack(await File.ReadAllBytesAsync(kaputt784)) == null,
            $"{vorher784} Eintrag/Einträge vorher, der kaputte wird nicht als Treffer gewertet");
        string uploadQuelle = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs"));
        Check(
            "F-M785: the run cache is gone — no RunWorkFiles left, and the upload configures the shared store",
            !uploadQuelle.Contains("RunWorkFiles", StringComparison.Ordinal)
            && uploadQuelle.Contains("Api.ExtractedTrackStore.TryGet(", StringComparison.Ordinal)
            && uploadQuelle.Contains("Api.ExtractedTrackStore.Put(", StringComparison.Ordinal)
            && uploadQuelle.Contains("_api.ArchiveDiskCache.Configure(", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", "RunWorkFiles.cs")),
            "kein Laufcache mehr, Ablage eingerichtet");
        spurenSpeicher.Reset();


        // ---- 29. F-M789: ein Sprachcode im Client, uebersetzt wird an den Grenzen -----------------
        string uploadQuelle789 = ReadText(Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs"));
        string isoA = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("en") ?? string.Empty;
        string isoB = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("eng") ?? string.Empty;
        string isoC = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("EN") ?? string.Empty;
        Check(
            "F-M789: three spellings of one language meet in one form — en, eng and EN are all ENG",
            isoA == "ENG" && isoB == "ENG" && isoC == "ENG",
            $"\"en\"->{isoA}, \"eng\"->{isoB}, \"EN\"->{isoC}");
        int fileLanguagesStart = uploadQuelle789.IndexOf("public IReadOnlyCollection<(string Language, bool HearingImpaired)> FileLanguages", StringComparison.Ordinal);
        int fileLanguagesEnd = uploadQuelle789.IndexOf("private List<BaseItem> SeasonItems", fileLanguagesStart, StringComparison.Ordinal);
        string fileLanguages = fileLanguagesStart >= 0 && fileLanguagesEnd > fileLanguagesStart
            ? uploadQuelle789[fileLanguagesStart..fileLanguagesEnd]
            : string.Empty;
        Check(
            "F-M789: the file's languages are asked by id and named in ISO — not read off the object and not in the source's spelling",
            fileLanguages.Contains("ContainerSubtitleStreams(episode)", StringComparison.Ordinal)
            && fileLanguages.Contains("LanguageMapper.Canonical(stream.Language)", StringComparison.Ordinal)
            && !fileLanguages.Contains("episode.GetMediaStreams()", StringComparison.Ordinal)
            && !fileLanguages.Contains("MapToSubdl", StringComparison.Ordinal),
            $"{fileLanguages.Length} Zeichen geprüft");
        int packPairsStart = uploadQuelle789.IndexOf("private HashSet<(string Lang, bool Hi)> PackPairsOf", StringComparison.Ordinal);
        int packPairsEnd = uploadQuelle789.IndexOf("private", packPairsStart + 10, StringComparison.Ordinal);
        string packPairs = packPairsStart >= 0 && packPairsEnd > packPairsStart
            ? uploadQuelle789[packPairsStart..packPairsEnd]
            : string.Empty;
        int fillStart = uploadQuelle789.IndexOf("private List<int> FindFillStreams", StringComparison.Ordinal);
        string fillStreams = fillStart >= 0 && fillStart + 2600 <= uploadQuelle789.Length
            ? uploadQuelle789.Substring(fillStart, 2600)
            : string.Empty;
        Check(
            "F-M789: the pack plan carries ISO everywhere and compares ISO against ISO",
            packPairs.Contains("LanguageMapper.Canonical(stream.Language)", StringComparison.Ordinal)
            && !packPairs.Contains("MapToSubdl", StringComparison.Ordinal)
            && fillStreams.Contains("SameLanguageIso(stream.Language, lang)", StringComparison.Ordinal)
            && !fillStreams.Contains("MapToSubdl", StringComparison.Ordinal),
            $"{packPairs.Length} + {fillStreams.Length} Zeichen geprüft");
        int helper = uploadQuelle789.IndexOf("private static bool SameLanguageIso", StringComparison.Ordinal);
        string helperBody = helper >= 0 ? uploadQuelle789[helper..(helper + 420)] : string.Empty;
        Check(
            "F-M789: the comparison is strict — a track without a language is never a match",
            helperBody.Contains("a != null && b != null", StringComparison.Ordinal)
            && helperBody.Contains("LanguageMapper.Canonical", StringComparison.Ordinal),
            "leere Angabe = kein Treffer");


        // ---- 30. F-M790: eine Form ueberall — ISO 639-2/T -----------------------------------------
        string bauDeu = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Build(probeFilm, "DEU", false, 1);
        string bauDe = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Build(probeFilm, "DE", false, 1);
        string bauGer = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Build(probeFilm, "ger", false, 1);
        Check(
            "F-M790: the sidecar name carries the one form — T, lower case — whatever the caller hands in",
            bauDeu.EndsWith(".deu.01.srt", StringComparison.Ordinal)
            && bauDe == bauDeu
            && bauGer == bauDeu,
            $"{Path.GetFileName(bauDeu)} / {Path.GetFileName(bauDe)} / {Path.GetFileName(bauGer)}");
        string baseNameDeu = Path.GetFileNameWithoutExtension(probeFilm);
        var ausDeu = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Parse(baseNameDeu + ".deu.02", baseNameDeu);
        var ausDe = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Parse(baseNameDeu + ".de.02", baseNameDeu);
        var ausGer = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Parse(baseNameDeu + ".ger.02", baseNameDeu);
        var ausSdh = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.Parse(baseNameDeu + ".deu.sdh.03", baseNameDeu);
        Check(
            "F-M790: the reader answers in the same form for deu, de and ger — and the markers survive",
            ausDeu?.Lang == "DEU" && ausDe?.Lang == "DEU" && ausGer?.Lang == "DEU"
            && ausSdh?.Lang == "DEU" && ausSdh?.HearingImpaired == true,
            $"{ausDeu?.Lang} / {ausDe?.Lang} / {ausGer?.Lang} / {ausSdh?.Lang}+sdh");
        string tDeu = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("ger") ?? string.Empty;
        string tCes = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("cze") ?? string.Empty;
        string tNld = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("dut") ?? string.Empty;
        string tEng = Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("en") ?? string.Empty;
        Check(
            "F-M790: the old spelling is translated on reading — ger becomes DEU, cze becomes CES, dut becomes NLD",
            tDeu == "DEU" && tCes == "CES" && tNld == "NLD" && tEng == "ENG",
            $"ger->{tDeu}, cze->{tCes}, dut->{tNld}, en->{tEng}");
        var abdeckung = new Jellyfin.Plugin.SubScribe.Registry.SubtitleRef("ger", false);
        var abdeckungDeu = new Jellyfin.Plugin.SubScribe.Registry.SubtitleRef("deu", false);
        Check(
            "F-M790: coverage keys on the canon, so a sidecar, a store row and a wish list meet under one key",
            abdeckung.Language == "DEU" && abdeckung.Equals(abdeckungDeu),
            $"ger -> {abdeckung.Language}");

        // ---- 31. F-M790: die Sprachform der Staffelurteile wandert, sie verschwindet nicht ----------------
        string urteilOrdner = Path.Combine(Path.GetTempPath(), "urteilmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(urteilOrdner);
        var alt = new DateTime(2026, 11, 20, 5, 0, 0, DateTimeKind.Utc);
        using (var anlegen = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(urteilOrdner, null))
        {
            // einmal oeffnen, damit die Datei samt Tabellen da ist; die alten Zeilen kommen darunter rein
            _ = anlegen.Context.PackSeasonRows.Count();
        }

        using (var roh = new Jellyfin.Plugin.SubScribe.Data.ScribeSqliteContext(Path.Combine(urteilOrdner, "sub-scribe.sqlite")))
        {
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt790|1|GER non-HI", Signature = "sig-nonhi", Verdict = "no-pack",
                Reason = "keine Quelle", Written = alt
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt790|1|GER HI", Signature = "sig-hi", Verdict = "pack", Reason = "oben", Written = alt
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt790|1|ENG non-HI", Signature = "sig-eng", Verdict = "pack", Reason = "oben", Written = alt
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt790|1|", Signature = "sig-staffel", Verdict = "no-pack", Reason = "Staffel", Written = alt
            });
            await roh.SaveChangesAsync();
        }

        using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(urteilOrdner, null))
        {
            // Der Konstruktor laesst den Startlauf laufen — geprueft wird, was er hinterlaesst.
            var zeilen = db.Context.PackSeasonRows.ToList();
            var nachId = zeilen.ToDictionary(r => r.Id, StringComparer.Ordinal);
            Check(
                "F-M790: a verdict filed under GER non-HI sits under DEU non-HI afterwards — signature, verdict and date carried over",
                nachId.ContainsKey("tt790|1|DEU non-HI") && nachId.ContainsKey("tt790|1|DEU HI")
                && nachId["tt790|1|DEU non-HI"].Signature == "sig-nonhi"
                && nachId["tt790|1|DEU HI"].Verdict == "pack"
                && nachId["tt790|1|DEU HI"].Written == alt,
                string.Join(",", zeilen.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal)));
            Check(
                "F-M790: the old spelling is gone, ENG and the season-level verdict stay — four rows, four keys",
                !nachId.ContainsKey("tt790|1|GER non-HI") && !nachId.ContainsKey("tt790|1|GER HI")
                && nachId.ContainsKey("tt790|1|ENG non-HI") && nachId.ContainsKey("tt790|1|")
                && zeilen.Count == 4,
                zeilen.Count + " row(s)");
        }

        using (var roh = new Jellyfin.Plugin.SubScribe.Data.ScribeSqliteContext(Path.Combine(urteilOrdner, "sub-scribe.sqlite")))
        {
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt791|2|GER non-HI", Signature = "sig-juengst", Verdict = "pack",
                Reason = "inzwischen oben", Written = new DateTime(2026, 11, 26, 5, 0, 0, DateTimeKind.Utc)
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt791|2|DEU non-HI", Signature = "sig-aelter", Verdict = "no-pack",
                Reason = "damals keine Quelle", Written = new DateTime(2026, 11, 21, 5, 0, 0, DateTimeKind.Utc)
            });
            await roh.SaveChangesAsync();
        }

        using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(urteilOrdner, null))
        {
            var kollision = db.Context.PackSeasonRows.ToList()
                .Where(r => r.Id.StartsWith("tt791|2|", StringComparison.Ordinal)).ToList();
            Check(
                "F-M790: two spellings meeting on one key leave one row — and the younger statement wins",
                kollision.Count == 1 && kollision[0].Id == "tt791|2|DEU non-HI"
                && kollision[0].Verdict == "pack" && kollision[0].Signature == "sig-juengst",
                string.Join(",", kollision.Select(r => r.Id + "=" + r.Verdict)));
        }

        try { Directory.Delete(urteilOrdner, true); } catch { }

        // ---- 32. F-M790: KEINE halbe Wanderung — alle sechs Stellen, an denen der Store eine Sprache haelt -----
        string ganzOrdner = Path.Combine(Path.GetTempPath(), "sprachwander-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ganzOrdner);
        using (var anlegen = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(ganzOrdner, null))
        {
            _ = anlegen.Context.TrackRows.Count();
        }

        using (var roh = new Jellyfin.Plugin.SubScribe.Data.ScribeSqliteContext(Path.Combine(ganzOrdner, "sub-scribe.sqlite")))
        {
            roh.TrackRows.Add(new Jellyfin.Plugin.SubScribe.Data.TrackEntity { MediaHash = "hash-1", SubPos = 3, Language = "ger" });
            roh.SidecarRows.Add(new Jellyfin.Plugin.SubScribe.Data.SidecarEntity { ContentHash = "inhalt-1", Language = "cze" });
            roh.PackRows.Add(new Jellyfin.Plugin.SubScribe.Data.PackEntity { SeasonKey = "tt1|1|subdl", Language = "dut" });
            roh.BadAppleRows.Add(new Jellyfin.Plugin.SubScribe.Data.BadAppleEntity
            {
                SubjectId = "sub-1", Source = "subdl", SourceId = "42", Member = "a.srt", Language = "chi"
            });
            roh.FileRows.Add(new Jellyfin.Plugin.SubScribe.Data.FileEntity
            {
                OsHash = "datei-1", LanguagesAvailable = "EN,ger", LastSearchLanguages = "de, EN, ger"
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt1|1|FRE non-HI", Signature = "sig", Verdict = "no-pack", Reason = "keine Quelle", Written = alt
            });
            await roh.SaveChangesAsync();
        }

        static string StandDerSprachen(Jellyfin.Plugin.SubScribe.Data.ScribeDbContext db)
        {
            var teile = new List<string>
            {
                string.Join("/", db.Context.TrackRows.ToList().Select(r => r.Language).OrderBy(x => x, StringComparer.Ordinal)),
                string.Join("/", db.Context.SidecarRows.ToList().Select(r => r.Language).OrderBy(x => x, StringComparer.Ordinal)),
                string.Join("/", db.Context.PackRows.ToList().Select(r => r.Language).OrderBy(x => x, StringComparer.Ordinal)),
                string.Join("/", db.Context.BadAppleRows.ToList().Select(r => r.Language).OrderBy(x => x, StringComparer.Ordinal)),
                string.Join("/", db.Context.FileRows.ToList().Select(r => (r.LanguagesAvailable ?? "-") + ";" + (r.LastSearchLanguages ?? "-"))),
                string.Join("/", db.Context.PackSeasonRows.ToList().Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal))
            };
            return string.Join(" | ", teile);
        }

        string nachWanderung;
        using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(ganzOrdner, null))
        {
            nachWanderung = StandDerSprachen(db);
            var datei = db.Context.FileRows.ToList().Single();
            var urteil = db.Context.PackSeasonRows.ToList().Single();
            Check(
                "F-M790: all six places come out in the canon — track, sidecar, pack, bad apple, both text lists and the verdict key",
                db.Context.TrackRows.ToList().Single().Language == "DEU"
                && db.Context.SidecarRows.ToList().Single().Language == "CES"
                && db.Context.PackRows.ToList().Single().Language == "NLD"
                && db.Context.BadAppleRows.ToList().Single().Language == "ZHO"
                && datei.LanguagesAvailable == "DEU,ENG"
                && datei.LastSearchLanguages == "DEU,ENG"
                && urteil.Id == "tt1|1|FRA non-HI",
                nachWanderung);
        }

        using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(ganzOrdner, null))
        {
            Check(
                "F-M790: a second start changes nothing — the pass is a state, not an event",
                StandDerSprachen(db) == nachWanderung,
                StandDerSprachen(db));
        }

        Check(
            "F-M790: a stored list is brought in line token by token, duplicates fall away and the order stays",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.CanonicalList("ger, EN , de,EN") == "DEU,ENG"
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.CanonicalList(null) == string.Empty,
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.CanonicalList("ger, EN , de,EN"));

        try { Directory.Delete(ganzOrdner, true); } catch { }

        // ---- 33. F-M791: was die Tabelle nicht kennt, wird benannt statt erfunden ------------------------
        Check(
            "F-M791: the table knows de, it never knows zz — an unknown code is echoed, never invented",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.KnowsTwoLetter("de")
            && !Jellyfin.Plugin.SubScribe.Language.LanguageMapper.KnowsTwoLetter("zz")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("zz") == "ZZ",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("zz") ?? "null");
        Check(
            "F-M792: the three codes the store was carrying reach their three-letter form — eu, gl and mn",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("eu") == "EUS"
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("gl") == "GLG"
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("mn") == "MON",
            string.Join("/", new[] { "eu", "gl", "mn" }
                .Select(c => c + "=" + (Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical(c) ?? "null"))));
        Check(
            "F-M791: two letters is never the canon — such a value and such a list member are counted and named",
            Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheAusserKanon("EU")
            && !Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheAusserKanon("DEU")
            && Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheAusserKanon("ger")
            && Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.ListeAusserKanon("ARA,EU,DEU")
            && !Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.ListeAusserKanon("ARA,DEU"));

        // ---- 34. F-M793: bleibt ein Befund ohne Namen, ist die FORM der Liste schuld ------------------------
        Check(
            "F-M793: a list of canonical members that is merely unsorted is counted as form, not as a language",
            Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.ListeAusserKanon("ENG,DEU")
            && !Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheAusserKanon("ENG")
            && !Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheAusserKanon("DEU")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.CanonicalList("ENG,DEU") == "DEU,ENG");

        // ---- 35. F-M794: auch ein ZWEISTELLIGER Urteils-Tag wandert -------------------------------------
        string zweiOrdner = Path.Combine(Path.GetTempPath(), "urteil2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(zweiOrdner);
        using (var anlegen = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(zweiOrdner, null))
        {
            _ = anlegen.Context.PackSeasonRows.Count();
        }

        using (var roh = new Jellyfin.Plugin.SubScribe.Data.ScribeSqliteContext(Path.Combine(zweiOrdner, "sub-scribe.sqlite")))
        {
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt794|1|EN non-HI", Signature = "sig", Verdict = "pack", Reason = "oben", Written = alt
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt794|1|DE HI", Signature = "sig", Verdict = "no-pack", Reason = "keine Quelle", Written = alt
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt794|1|spa non-hi", Signature = "sig", Verdict = "pack", Reason = "oben", Written = alt
            });
            roh.PackSeasonRows.Add(new Jellyfin.Plugin.SubScribe.Registry.PackSeasonEntity
            {
                Id = "tt794|1|", Signature = "sig", Verdict = "no-pack", Reason = "Staffel", Written = alt
            });
            await roh.SaveChangesAsync();
        }

        using (var db = new Jellyfin.Plugin.SubScribe.Data.ScribeDbContext(zweiOrdner, null))
        {
            var ids794 = db.Context.PackSeasonRows.ToList().Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Check(
                "F-M794: a verdict tag written two-letter or in lower case moves to the one form — ENG, DEU and SPA, season level untouched",
                ids794.SequenceEqual(new[] { "tt794|1|", "tt794|1|DEU HI", "tt794|1|ENG non-HI", "tt794|1|SPA non-hi" }),
                string.Join(",", ids794));
        }

        try { Directory.Delete(zweiOrdner, true); } catch { }

        // ---- 36. F-M797/F-M798: die Tafel kommt aus der amtlichen Liste, und geraten wird nichts ----
        Check(
            "F-M797: the bridge is the official list — Fijian reaches FJ where the old guess said FI (Finnish)",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("fij") == "FJ"
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("fij") != "FI"
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("fij") == "FIJ",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("fij") ?? "null");
        Check(
            "F-M797: both three-letter forms are known — ger and deu meet in DEU, and DEU is not unknown",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.KnowsThreeLetter("ger")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.KnowsThreeLetter("deu")
            && !Jellyfin.Plugin.SubScribe.Language.LanguageMapper.KnowsThreeLetter("xyz")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("ger") == "DEU",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.Canonical("ger") ?? "null");
        Check(
            "F-M798: nothing is guessed — an unknown code, a language without a two-letter form and und all give null",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("xyz") is null
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("ace") is null
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("und") is null
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdlName("Klingon") is null,
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("ace") ?? "null");
        Check(
            "F-M798: the four codes that name no language are named as such",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.IsNoLanguage("und")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.IsNoLanguage("mul")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.IsNoLanguage("mis")
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.IsNoLanguage("zxx")
            && !Jellyfin.Plugin.SubScribe.Language.LanguageMapper.IsNoLanguage("deu"),
            "und/mul/mis/zxx");
        Check(
            "F-M797: the counter tells an unknown code from a known one, and a two-letter value is its own case",
            Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheUnbekannt("XYZ")
            && !Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheUnbekannt("DEU")
            && !Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheUnbekannt("EU")
            && Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheUnbekannt("YUE"),
            "XYZ known? " + Jellyfin.Plugin.SubScribe.Data.ScribeDbContext.SpracheUnbekannt("XYZ"));

        // ---- 37. F-M799: jede Quelle hört ihren eigenen Code im Dateinamen -----------------------------
        string hash799 = "0123456789abcdef0123456789abcdef";
        Check(
            "F-M799: the same subtitle is named CS for SubDL and CZ for tvsubtitles.net",
            Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("CES") == "CS"
            && Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.SiteLanguage("CES") == "cz"
            && Jellyfin.Plugin.SubScribe.Sources.SourceNeutral.SendFileName(true, "tt1", 1, 2, "CS", hash799) == "tt1.S01E02." + hash799 + ".CS.srt"
            && Jellyfin.Plugin.SubScribe.Sources.SourceNeutral.SendFileName(true, "tt1", 1, 2, "CZ", hash799) == "tt1.S01E02." + hash799 + ".CZ.srt",
            Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.SiteLanguage("CES") ?? "null");
        Check(
            "F-M799: Greek, Chinese and Japanese differ the same way, and a movie name carries no episode",
            Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.SiteLanguage("ELL") == "gr"
            && Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.SiteLanguage("ZHO") == "cn"
            && Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.SiteLanguage("JPN") == "jp"
            && Jellyfin.Plugin.SubScribe.Sources.SourceNeutral.SendFileName(false, "tt9", 0, 0, "GR", hash799) == "tt9." + hash799 + ".GR.srt",
            Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.SiteLanguage("JPN") ?? "null");
        Check(
            "F-M799: moviesubtitles.org writes br where SubDL writes PT, and takes no European Portuguese",
            Jellyfin.Plugin.SubScribe.Api.MovieSubtitlesClient.SiteLanguage("pt-BR") == "br"
            && Jellyfin.Plugin.SubScribe.Api.MovieSubtitlesClient.SiteLanguage("POR") is null
            && Jellyfin.Plugin.SubScribe.Language.LanguageMapper.MapToSubdl("POR") == "PT",
            Jellyfin.Plugin.SubScribe.Api.MovieSubtitlesClient.SiteLanguage("POR") ?? "null");

        // ---- 38. F-M585: ein schon hochgeladener Einzel darf zusaetzlich in den Pack ---------------------
        string uploadQuelle585 = await File.ReadAllTextAsync(
            Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "UploadPipeline.cs"));
        Check(
            "F-M585: both pack call sites ask the lock with the pack in mind",
            System.Text.RegularExpressions.Regex.Matches(uploadQuelle585, @"SpurIstGesperrt\([^)]*fuerPack: true\)").Count == 2
            && System.Text.RegularExpressions.Regex.Matches(uploadQuelle585, @"SpurIstGesperrt\(mediaHash|SpurIstGesperrt\(fillMediaHash").Count == 2,
            "Treffer: " + System.Text.RegularExpressions.Regex.Matches(uploadQuelle585, @"fuerPack: true").Count);
        Check(
            "F-M585: the pack exemption covers the two \"already there\" verdicts and nothing else",
            uploadQuelle585.Contains("if (fuerPack", StringComparison.Ordinal)
            && uploadQuelle585.Contains("reason == RejectReason.SelfEcho || reason == RejectReason.DuplicateContent", StringComparison.Ordinal)
            && uploadQuelle585.Contains("private bool SpurIstGesperrt(", StringComparison.Ordinal),
            "Exemption gefunden");

        // ---- 39. F-M800: der Pack steht als ein geschlossenes Papier ------------------------------------
        string packDoc = await File.ReadAllTextAsync(Path.Combine(root, "docs", "SEASON-PACK-UPLOAD.md"));
        Check(
            "F-M800: the pack paper names the seven checks before any unpacking and both switches",
            packDoc.Contains("Checks before anything is unpacked", StringComparison.Ordinal)
            && packDoc.Contains("`Upload enabled`", StringComparison.Ordinal)
            && packDoc.Contains("`preferSeasonZip`", StringComparison.Ordinal)
            && packDoc.Contains("A switch is only a permission", StringComparison.Ordinal),
            "Papier gelesen: " + packDoc.Length + " Zeichen");
        string spec800 = await File.ReadAllTextAsync(Path.Combine(root, "docs", "SPECIFICATION.md"));
        Check(
            "F-M800: the spec entry points at the paper and no longer narrows the origin to one source",
            spec800.Contains("**F-M800 [U]:**", StringComparison.Ordinal)
            && spec800.Contains("docs/SEASON-PACK-UPLOAD.md", StringComparison.Ordinal)
            && !spec800.Contains("originally comes from SubDL", StringComparison.Ordinal)
            && spec800.Contains("a download of one of our sources", StringComparison.Ordinal),
            "Eintrag und Wortlaut geprueft");
        string hashQuelle800 = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Registry", "ContentHashRegistry.cs"));
        string koordQuelle800 = await File.ReadAllTextAsync(Path.Combine(
            root, "Jellyfin.Plugin.SubdlSync", "Pipeline", "SeasonPackCoordinator.cs"));
        Check(
            "F-M397: the question is asked for any source, and no call site uses the old name",
            hashQuelle800.Contains("public bool IsDownloadedFromAnySource(string? contentHash)", StringComparison.Ordinal)
            && koordQuelle800.Contains("IsDownloadedFromAnySource(", StringComparison.Ordinal)
            && !koordQuelle800.Contains("IsSubdlOrigin(", StringComparison.Ordinal)
            && !hashQuelle800.Contains("IsSubdlOrigin(", StringComparison.Ordinal),
            "Name und Aufrufstelle geprueft");

        // ---- 40. F-M801: die vier Wege stehen je als ein geschlossenes Papier --------------------------
        string[] paare801 = { "SINGLE-UPLOAD", "SEASON-PACK-UPLOAD", "SEASON-PACK-DOWNLOAD", "SINGLE-DOWNLOAD" };
        var fehlend801 = new List<string>();
        string readme801 = await File.ReadAllTextAsync(Path.Combine(root, "README.md"));
        foreach (string name in paare801)
        {
            string pfad = Path.Combine(root, "docs", name + ".md");
            if (!File.Exists(pfad))
            {
                fehlend801.Add(name + " (fehlt)");
                continue;
            }

            string text801 = await File.ReadAllTextAsync(pfad);
            if (!text801.Contains("never does", StringComparison.Ordinal)
                || !text801.Contains("What the log says", StringComparison.Ordinal)
                || !text801.Contains("Where the rules live", StringComparison.Ordinal))
            {
                fehlend801.Add(name + " (Bauform)");
            }

            if (!readme801.Contains("docs/" + name + ".md", StringComparison.Ordinal))
            {
                fehlend801.Add(name + " (nicht im README)");
            }
        }

        Check(
            "F-M801: the four papers exist, keep the same shape, and the README points at each of them",
            fehlend801.Count == 0,
            fehlend801.Count == 0 ? "vier Papiere geprueft" : string.Join(", ", fehlend801));
        Check(
            "F-M801: the spec entry names all four papers",
            paare801.All(n => spec800.Contains("docs/" + n + ".md", StringComparison.Ordinal)),
            "Eintrag geprueft");

        // ---- 41. F-M805: jede Route der Plugin-API verlangt eine Anmeldung ----------------------------
        // Befund 28.11.2026 auf prod: saemtliche Plugin-Routen standen mit [AllowAnonymous] im Code,
        // also ohne jede Anmeldung erreichbar — lesbar und ausloesbar von jedem, der den Jellyfin-Port
        // erreicht. Lesen verlangt jetzt einen angemeldeten Nutzer, Schreiben und Loeschen zusaetzlich
        // die Admin-Rolle. Bewusst NICHT "LocalAccessOrRequiresElevation": diese Policy prueft die
        // Verbindungs-IP, und hinter einem Reverse-Proxy ist jede Anfrage lokal — sie waere wirkungslos.
        string[] controller805 =
        {
            "ScribeDiagnosticsController.cs",
            "ScribeStatusController.cs",
            "ScribePostprocessController.cs",
            "ScribeAdoptController.cs",
            "ScribeStopController.cs",
            "ScribeReset.cs",
            "ScribeQuotaService.cs"
        };
        var quellen805 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string datei805 in controller805)
        {
            quellen805[datei805] = (await File.ReadAllTextAsync(
                Path.Combine(root, "Jellyfin.Plugin.SubdlSync", "Api", datei805)))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        static bool Traegt805(string quelle, string attribut, string route)
            => quelle.Contains(attribut + "\n    [" + route, StringComparison.Ordinal);

        static int Zaehle805(string quelle, string nadel)
        {
            int n805 = 0;
            int at805 = 0;
            while ((at805 = quelle.IndexOf(nadel, at805, StringComparison.Ordinal)) >= 0)
            {
                n805++;
                at805 += nadel.Length;
            }

            return n805;
        }

        const string Lesen805 = "    [Authorize]";
        const string Schreiben805 = "    [Authorize(Policy = \"RequiresElevation\")]";

        Check(
            "F-M805: no route of the plugin API is anonymous any more",
            quellen805.Values.All(q => !q.Contains("[AllowAnonymous]", StringComparison.Ordinal)),
            string.Join(", ", quellen805.Where(p => p.Value.Contains("[AllowAnonymous]", StringComparison.Ordinal)).Select(p => p.Key)));
        Check(
            "F-M805: every action in the seven controllers carries an Authorize attribute",
            quellen805.All(p => Zaehle805(p.Value, "    [Http") == Zaehle805(p.Value, "    [Authorize")),
            string.Join(", ", quellen805.Where(p => Zaehle805(p.Value, "    [Http") != Zaehle805(p.Value, "    [Authorize")).Select(p => p.Key)));
        Check(
            "F-M805: the diagnostics dump needs a signed-in user",
            Traegt805(quellen805["ScribeDiagnosticsController.cs"], Lesen805, "HttpGet(\"Diagnostics\")]"),
            "Diagnostics geprueft");
        Check(
            "F-M805: Status, Stats and WorkerRuns need a signed-in user",
            Traegt805(quellen805["ScribeStatusController.cs"], Lesen805, "HttpGet(\"Status\")]")
            && Traegt805(quellen805["ScribeStatusController.cs"], Lesen805, "HttpGet(\"Stats\")]")
            && Traegt805(quellen805["ScribeStatusController.cs"], Lesen805, "HttpGet(\"WorkerRuns\")]"),
            "drei Lese-Routen geprueft");
        Check(
            "F-M805: the statistics reset is an administrator action",
            Traegt805(quellen805["ScribeStatusController.cs"], Schreiben805, "HttpPost(\"StatsReset\")]"),
            "StatsReset geprueft");
        Check(
            "F-M805: triggering an upload postprocess is an administrator action",
            Traegt805(quellen805["ScribePostprocessController.cs"], Schreiben805, "HttpPost(\"PostprocessUploads\")]"),
            "PostprocessUploads geprueft");
        Check(
            "F-M805: counting the own subtitles is an administrator action",
            Traegt805(quellen805["ScribePostprocessController.cs"], Schreiben805, "HttpPost(\"CountMySubtitles\")]"),
            "CountMySubtitles geprueft");
        Check(
            "F-M805: the postprocess status needs a signed-in user",
            Traegt805(quellen805["ScribePostprocessController.cs"], Lesen805, "HttpGet(\"PostprocessStatus\")]"),
            "PostprocessStatus geprueft");
        Check(
            "F-M805: stopping and resuming the pipeline are administrator actions",
            Traegt805(quellen805["ScribeStopController.cs"], Schreiben805, "HttpPost(\"Stop\")]")
            && Traegt805(quellen805["ScribeStopController.cs"], Schreiben805, "HttpPost(\"Resume\")]"),
            "Stop und Resume geprueft");
        Check(
            "F-M805: adopting uploads is an administrator action",
            Traegt805(quellen805["ScribeAdoptController.cs"], Schreiben805, "HttpPost(\"AdoptUploads\")]"),
            "AdoptUploads geprueft");
        Check(
            "F-M805: resetting state, resetting one path and restoring a backup are administrator actions",
            Zaehle805(quellen805["ScribeReset.cs"], Schreiben805) == 3,
            "drei schreibende Aktionen gezaehlt");
        Check(
            "F-M805: listing the backups needs a signed-in user",
            Traegt805(quellen805["ScribeReset.cs"], Lesen805, "HttpGet]"),
            "List geprueft");
        Check(
            "F-M805: the quota is readable for a signed-in user",
            Traegt805(quellen805["ScribeQuotaService.cs"], Lesen805, "HttpGet]"),
            "Quota geprueft");
        Check(
            "F-M805: the role decides, not the address - no route uses LocalAccessOrRequiresElevation",
            quellen805.Values.All(q => !q.Contains("LocalAccessOrRequiresElevation", StringComparison.Ordinal)),
            "IP-abhaengige Policy nirgends benutzt");

        if (_failures > 0)
        {
            Console.WriteLine($">>> ENDPUNKTE: {_failures} von {_checks} Pruefungen fehlgeschlagen");
            return 1;
        }

        Console.WriteLine($">>> ENDPUNKTE OK: {_checks} Pruefungen bestanden");
        return 0;
    }

    private static PluginConfiguration Conf(Action<PluginConfiguration> mutate)
    {
        var c = new PluginConfiguration();
        c.Username = "u";
        c.Password = "p";
        c.ApiKey = "k";
        c.TmdbApiKey = "t";
        c.SelectedLibraries = new List<string> { "Movies" };
        c.DownloadLanguages = "EN, DE";
        mutate(c);
        return c;
    }

    /// <summary>Liest eine Datei in einem nicht-async Helfer — CA1849 prueft nur den async-Pfad.</summary>
    private static string ReadText(string path) => File.ReadAllText(path);

    /// <summary>Speichert synchron aus einem nicht-async Helfer — CA1849 prueft nur den async-Pfad.</summary>
    private static void SaveNow(Jellyfin.Plugin.SubScribe.Data.ScribeSqliteContext ctx) => ctx.SaveChanges();

    /// <summary>Oeffnet einen ZIP-Eintrag im nicht-async Helfer (dieselbe CA1849-Regel).</summary>
    private static System.IO.Stream OpenForRead(this System.IO.Compression.ZipArchiveEntry entry) => entry.Open();

    /// <summary>Schreibt in einen Strom im nicht-async Helfer.</summary>
    private static void WriteAll(System.IO.Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

    /// <summary>F-M501: laeuft der Aufruf in eine Ausnahme? (Fuer die Grenzen von BuildAddShow.)</summary>
    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// F-M501: die Seiten von tvsubtitles.net, die die Aufloesung braucht — die Serienliste unter
    /// <c>/add.html</c> und das Anlege-Formular unter <c>/add-tvshow1.php</c>. Die Liste ist
    /// veraenderlich, damit "erst anlegen, dann erneut suchen" wirklich geprueft wird und nicht nur
    /// behauptet. Die Zaehler sind der Beweis fuer "kein Netzzugriff" bzw. "genau einmal angelegt".
    /// </summary>
    private sealed class TvstSiteHandler : HttpMessageHandler
    {
        private readonly List<(int Id, string Name)> _shows = new();

        public TvstSiteHandler(int id, string name) => _shows.Add((id, name));

        /// <summary>GETs auf die Serienliste.</summary>
        public int Gets { get; private set; }

        /// <summary>POSTs auf das Anlege-Formular.</summary>
        public int Posts { get; private set; }

        /// <summary>POSTs auf das Upload-Formular.</summary>
        public int Uploads { get; private set; }

        /// <summary>Der Rumpf der letzten Anlage.</summary>
        public string? LastBody { get; private set; }

        /// <summary>Der Rumpf des letzten Uploads.</summary>
        public string? LastUploadBody { get; private set; }

        /// <summary>Antwortet die Seite auf die Anlage mit diesem Status?</summary>
        public int PostStatus { get; set; } = 200;

        /// <summary>Wird die Serie nach erfolgreicher Anlage in die Liste aufgenommen?</summary>
        public (int Id, string Name)? AddAfterPost { get; set; }

        /// <summary>Antwortet die Seite ohne Auswahlfeld (unparsbare/leere Liste)?</summary>
        public bool EmptyList { get; set; }

        /// <summary>Die Anfragen in ihrer Reihenfolge — "GET", "POST" (Anlage) oder "UPLOAD".</summary>
        public List<string> Order { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _ = body;

            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith("add-tvshow1.php", StringComparison.Ordinal))
            {
                Order.Add("POST");
                Posts++;
                LastBody = body;
                if (PostStatus is < 200 or >= 300)
                {
                    return new HttpResponseMessage((HttpStatusCode)PostStatus);
                }

                if (AddAfterPost is { } add)
                {
                    _shows.Add(add);
                }

                // F-M516: so antwortet die Seite wirklich (gemessen 16.11.2026) — 302 auf
                // /add-tvshow.php?done=yes, leerer Rumpf, keine Id. Der Stub muss das spiegeln,
                // sonst prueft die Suite eine Antwort, die es nicht gibt.
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://www.tvsubtitles.net/add-tvshow.php?done=yes");
                return redirect;
            }

            if (request.Method == HttpMethod.Post)
            {
                Order.Add("UPLOAD");
                Uploads++;
                LastUploadBody = body;
                return Html("<html><body>ok</body></html>");
            }

            Order.Add("GET");
            Gets++;
            if (request.RequestUri!.AbsolutePath.EndsWith("search2.php", StringComparison.Ordinal))
            {
                // F-M516: die Seitensuche — Treffer sind Links /tvshow-<id>.html mit Zeitraum daneben.
                // F-M537: hinter qs= steht jetzt noch &client=<tag> — der Wert endet am naechsten &.
                var q = request.RequestUri.Query.Contains("qs=", StringComparison.Ordinal)
                    ? Uri.UnescapeDataString(
                        request.RequestUri.Query[(request.RequestUri.Query.IndexOf("qs=", StringComparison.Ordinal) + 3)..].Split('&')[0])
                    : string.Empty;
                var hits = _shows
                    .Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hits.Count == 0)
                {
                    return Html("<html><body><p class=\"description\">Search results</p><p>No results found</p></body></html>");
                }

                var sbHits = new StringBuilder("<html><body><p class=\"description\">Search results</p><p><ul>");
                foreach (var (id, name) in hits)
                {
                    sbHits.Append("<li><div style=\"\"><a href=\"/tvshow-").Append(id).Append(".html\">")
                        .Append(name).Append(" (2009-2018)</a></div></li>");
                }

                return Html(sbHits.Append("</ul></p></body></html>").ToString());
            }

            if (EmptyList)
            {
                return Html("<html><body><p>kein Formular</p></body></html>");
            }

            var sb = new StringBuilder("<select name=\"tvshow\" size=\"1\">");
            sb.Append("<option value=\"0\">- Select TV-Show -</option>");
            foreach (var (id, name) in _shows)
            {
                sb.Append("<option value=\"").Append(id).Append("\">").Append(name).Append("</option>");
            }

            sb.Append("</select>");
            return Html("<html><body>" + sb + "</body></html>");
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }

    /// <summary>F-M501: eine feste JSON-Antwort fuer den TMDb-Serien-Datensatz.</summary>
    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public JsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>F-M507: liest die Spaltenliste einer Tabelle — die Gegenprobe zur Migration.</summary>
    private static HashSet<string> Spalten(string dbFile, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbFile);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>F-M507: ein Wegwerf-Verzeichnis, das sich selbst wieder wegraeumt.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "subdl-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // liegen gebliebene Datei: kein Grund, die Suite rot zu machen
            }
        }
    }

    /// <summary>
    /// F-M507: die Seiten von moviesubtitles.org, die der Endpunkt braucht — die Suche (zwei Filme,
    /// „Arena" 1984 und „Arena: Part One" 2021) und die Filmseite mit drei Eintraegen (zwei englische,
    /// ein deutscher). Der Handler zaehlt jede Anfrage, damit „ein Abruf je Quelle und Sprache" messbar
    /// ist statt behauptet.
    /// </summary>
    private sealed class MsubSiteHandler : HttpMessageHandler
    {
        public int Gets { get; private set; }

        public int Posts { get; private set; }

        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            LastUrl = url;
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><b>ok</b></html>", Encoding.UTF8, "text/html"),
                });
            }

            Gets++;

            if (url.Contains("/search.php", StringComparison.Ordinal))
            {
                // Die Suchliste: das Jahr steht im Linktext, die Seite schreibt den Artikel hinter den
                // Komma ("Arena" hat keinen).
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><div class=\"movies\">"
                        + "<a href=\"/movie-101.html\">Arena (1984)</a>"
                        + "<a href=\"/movie-102.html\">Arena: Part One (2021)</a>"
                        + "</div></html>", Encoding.UTF8, "text/html"),
                });
            }

            if (url.EndsWith("movie-102.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FilmSeite, Encoding.UTF8, "text/html"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static readonly string FilmSeite =
            "<html><body>"
            + "<h2>Arena: Part One (2021)</h2>"
            + "<p class=\"description\"><img src=\"images/flags/en.gif\"> 3 subtitles, 12 downloads</p>"
            + Eintrag("5001", "en", "WEB-DL", "GROUPA")
            + Eintrag("5002", "en", "BluRay", "GROUPB")
            + Eintrag("5003", "de", "WEB-DL", "GROUPC")
            + "<span style=\"color:#666666\"><b>English  subtitles:</b></span>"
            + "<span style=\"color:#666666\"><b>German  subtitles:</b></span>"
            + "</body></html>";

        private static string Eintrag(string id, string lang, string rip, string release) =>
            "<a href=\"/subtitle-" + id + ".html\" title=\"Download Arena: Part One (2021) subtitles\">"
            + "<div class=\"subtitle\"><b>Arena: Part One 2021 " + lang + " subtitles (" + rip + "." + release
            + ")</b><table><tr>"
            + "<td title=\"lang\"><img src=\"images/flags/" + lang + ".gif\"></td>"
            + "<td title=\"Rip\">" + rip + "</td>"
            + "<td title=\"release\">" + release + "</td>"
            + "<td title=\"parts\">1</td>"
            + "<td title=\"uploaded\">2026-01-01</td>"
            + "<td title=\"downloaded\">12</td>"
            + "</tr></table></div></a>";
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine("  [PASS] " + what);
            return;
        }

        _failures++;
        Console.WriteLine("  [FAIL] " + what + (detail is null ? string.Empty : " — " + detail));
    }

    /// <summary>
    /// Serves the captured tvsubtitles pages and a small ZIP, so the tvsubtitles endpoint can be driven
    /// end to end without the live site.
    /// </summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string _dir;

        public FixtureHandler(string dir) => _dir = dir;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            var url = request.RequestUri!.ToString();
            // F-M537: der Client haengt client=<tag> an jede Anfrage — der Fixture-Name bleibt der Pfad.
            var path = url.Split('?')[0].Replace("https://www.tvsubtitles.net/", string.Empty, StringComparison.Ordinal);

            if (path == "add.html")
            {
                return Html(ReadText(Path.Combine(_dir, "add-tvshow-select.html")));
            }

            if (path.StartsWith("download-", StringComparison.Ordinal))
            {
                // The site answers the download URL with the ZIP in one of its two shapes (F-M482).
                using var buffer = new MemoryStream();
                using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("The Sample - 2x24 - Back To Summer.HDTV.GRP.en.srt");
                    using var stream = entry.OpenForRead();
                    var body = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nhello\n");
                    WriteAll(stream, body);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(buffer.ToArray()),
                };
            }

            var file = path switch
            {
                "tvshow-512.html" => "tvshow-512.html",
                "tvshow-512-2.html" => "tvshow-512-2.html",
                _ when path.StartsWith("episode-", StringComparison.Ordinal) => "episode-31911.html",
                _ when path.StartsWith("subtitle-", StringComparison.Ordinal) => "subtitle-page.html",
                _ => null,
            };

            if (file is null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Html(ReadText(Path.Combine(_dir, file)));
        }

        private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        };
    }

    /// <summary>Counts non-overlapping occurrences of a needle (F-M555 checks).</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        int count = 0;
        int at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}

/// <summary>
/// F-M783: ein Handler, der zaehlt, wie oft die Kette wirklich hinausgeht — und der ohne Netz antwortet.
/// </summary>
internal sealed class CountingInnerHandler : HttpMessageHandler
{
    /// <summary>Gets how many requests reached the network.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var antwort = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":1416}", System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = request
        };
        return Task.FromResult(antwort);
    }
}
