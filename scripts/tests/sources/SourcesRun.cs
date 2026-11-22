// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the Free
// Software Foundation, either version 3 of the License, or (at your option) any
// later version. Sub Scribe is distributed WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR
// PURPOSE. See the GNU General Public License for more details.
//
// T339 / F-M682: die Grenze zwischen Plugin und Quellen-Engine.
//
// WARUM DIESE SUITE SO MISST UND NICHT ANDERS
//
//   Eine Grenze, die nur BESCHRIEBEN ist, hält nicht: sie verrottet beim ersten Umbau, weil niemand
//   merkt, dass eine Quelldatei plötzlich den Store kennt oder ein Pipeline-Zweig den Klassennamen
//   einer Engine. Diese Suite liest deshalb die Dateien auf beiden Seiten der Grenze und zählt die
//   öffentlichen Mitglieder des Kontrakts — und sie baut die ECHTE Registry, um die eine Frage zu
//   stellen, die der Ablauf stellt: „welche Quellen stehen im Pack-Slot?"
//
//   Die Prüfungen sind absichtlich Text- und Zählprüfungen an den Dateien, die die Grenze bilden:
//     (a) keine Datei unter Sources/<Quelle>/ nennt den Store oder die Pipeline,
//     (b) keine Datei in Pipeline/ oder Data/ nennt einen Engine-Namensraum,
//     (c) jede Engine ist über die Registry erreichbar und nennt Key und Capabilities,
//     (d) die vier Aufträge sind die EINZIGEN öffentlichen Methoden des Kontrakts,
//     (e) der HttpClient wird von außen gereicht — kein `new HttpClient` unter Sources/.
//
//   Kein Netz, kein Upload, keine Zugangsdaten: die Suite spricht mit keiner Quelle.
//
// Usage: dotnet run --project scripts/tests/sources/sources.csproj -c Release -- [repo-root]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace SourcesRun;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what, string? detail = null)
    {
        _checks++;
        Console.WriteLine((ok ? "  OK    " : "  FAIL  ") + what
            + (string.IsNullOrEmpty(detail) ? string.Empty : "  [" + detail + "]"));
        if (!ok)
        {
            Failures.Add(what);
        }
    }

    private static int Main(string[] args)
    {
        string root = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        string plugin = Path.Combine(root, "Jellyfin.Plugin.SubdlSync");
        string sourcesDir = Path.Combine(plugin, "Sources");
        if (!Directory.Exists(sourcesDir))
        {
            Console.WriteLine(">>> SOURCES FEHLER: " + sourcesDir + " fehlt — ist " + root + " der Repo-Wurzelpfad?");
            return 2;
        }

        // -----------------------------------------------------------------------------------------
        // (a) Keine Datei unter Sources/<Quelle>/ nennt den Store oder die Pipeline.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(a) die Engine-Seite kennt weder Store noch Pipeline");
        var engineDateien = Directory.GetFiles(sourcesDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetDirectoryName(f), sourcesDir, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Check(engineDateien.Count >= 3,
            "F-M682: unter Sources/<Quelle>/ liegt je Quelle eine Datei (drei Adapter)",
            engineDateien.Count + " Datei(en): " + string.Join(", ", engineDateien.Select(Path.GetFileName)));

        var storeNamen = new[] { "Data.", "Pipeline." };
        var storeTreffer = engineDateien
            .SelectMany(f => storeNamen.SelectMany(n => TrefferZeilen(f, n)))
            .ToList();
        Check(storeTreffer.Count == 0,
            "F-M682 (a): keine Engine-Datei nennt `Data.` oder `Pipeline.`",
            storeTreffer.Count == 0 ? "0 Treffer" : string.Join(" | ", storeTreffer.Take(4)));

        var kennungsNamen = new[]
        {
            "ScribeDbContext", "SourceStore", "ContentHashRegistry", "ScribeIds", "MarkKind",
            "PackEntity", "PackKey", "OwnUploadId",
        };
        var kennungsTreffer = engineDateien
            .SelectMany(f => kennungsNamen.SelectMany(n => TrefferZeilen(f, n)))
            .ToList();
        Check(kennungsTreffer.Count == 0,
            "F-M682 (a)/(c): keine Engine-Datei nennt den Store oder eine unserer Kennungsformen",
            kennungsTreffer.Count == 0 ? "0 Treffer" : string.Join(" | ", kennungsTreffer.Take(4)));

        // Die Marken selbst ([xxxx], #yyyyy, #yyyyy#zzz, #yyyyy&zzz) dürfen in einer Engine nicht
        // gebaut werden — gemessen an ihrer Gestalt, nicht an einem Kommentar darüber.
        var markenTreffer = engineDateien
            .SelectMany(f => MarkenTreffer(f))
            .ToList();
        Check(markenTreffer.Count == 0,
            "F-M682 (c): keine Engine-Datei baut eine unserer Marken (`[xxxx]`, `#yyyyy`)",
            markenTreffer.Count == 0 ? "0 Treffer" : string.Join(" | ", markenTreffer.Take(4)));

        // -----------------------------------------------------------------------------------------
        // (b) Keine Datei in Pipeline/ oder Data/ nennt einen Engine-Namensraum.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(b) die Ablauf-Seite nennt nur den Kontrakt, keinen Engine-Namensraum");
        var ablaufDateien = Directory.GetFiles(Path.Combine(plugin, "Pipeline"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(plugin, "Data"), "*.cs", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var engineNamen = new[]
        {
            "Sources.Subdl", "Sources.Tvst", "Sources.Msub",
            "SubdlEngine", "TvstEngine", "MsubEngine",
        };
        var ablaufTreffer = ablaufDateien
            .SelectMany(f => engineNamen.SelectMany(n => TrefferZeilen(f, n)))
            .ToList();
        Check(ablaufTreffer.Count == 0,
            "F-M682 (b): keine Datei in Pipeline/ oder Data/ nennt eine Engine (nur den Kontrakt)",
            ablaufTreffer.Count == 0 ? "0 Treffer in " + ablaufDateien.Count + " Datei(en)" : string.Join(" | ", ablaufTreffer.Take(4)));

        // Der Kontrakt selbst darf dort stehen — sonst wäre die Grenze nicht benutzbar.
        string pipelineQuelle = File.ReadAllText(Path.Combine(plugin, "Pipeline", "UploadPipeline.cs"));
        Check(pipelineQuelle.Contains("Sources.SourceRegistry.Build(", StringComparison.Ordinal)
              && pipelineQuelle.Contains("Sources.SourceEngineContext(", StringComparison.Ordinal),
            "F-M682: der Ablauf baut die Registry und reicht den Kontext hinein — der Kontrakt ist benutzt, nicht zitiert");

        // -----------------------------------------------------------------------------------------
        // (c) Jede Engine ist über die Registry erreichbar und nennt Key + Capabilities.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(c) jede Engine ist in der Registry erreichbar und nennt Key + Capabilities");
        string registryQuelle = File.ReadAllText(Path.Combine(sourcesDir, "SourceRegistry.cs"));
        var engineKlassen = engineDateien
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var nichtEingetragen = engineKlassen
            .Where(k => !registryQuelle.Contains(k, StringComparison.Ordinal))
            .ToList();
        Check(nichtEingetragen.Count == 0,
            "F-M682 (c): jede Engine-Datei wird in der Registry ausdrücklich gebaut (kein Reflection-Scan)",
            nichtEingetragen.Count == 0 ? string.Join(", ", engineKlassen) : "fehlt: " + string.Join(", ", nichtEingetragen));

        Check(!registryQuelle.Contains("GetTypes(", StringComparison.Ordinal)
              && !registryQuelle.Contains("Activator.CreateInstance", StringComparison.Ordinal)
              && !registryQuelle.Contains("IServiceProvider", StringComparison.Ordinal),
            "F-M682 (c): die Registry scannt nicht, sie baut — kein Reflection, kein DI-Container, kein Service-Locator");

        var config = new PluginConfiguration();
        var context = new SourceEngineContext(
            new HttpClient(),
            NullLogger.Instance,
            TimeProvider.System);

        config.TvSubtitlesUploadEnabled = true;
        var mitTvst = SourceRegistry.Build(context, config);
        config.TvSubtitlesUploadEnabled = false;
        var ohneTvst = SourceRegistry.Build(context, config);

        Check(mitTvst.Engines.Count == 3,
            "F-M682 (c): die Registry gibt die drei Engines als Liste heraus",
            string.Join(", ", mitTvst.Keys));

        Check(mitTvst.Keys.SequenceEqual(new[] { "subdl", "tvsubtitles", "moviesubtitles" }),
            "F-M682 (c): die Schlüssel sind die Werte, die der Bestand in seiner Quellen-Spalte führt",
            string.Join(", ", mitTvst.Keys));

        var ohneKey = mitTvst.Engines
            .Where(e => string.IsNullOrWhiteSpace(e.Capabilities.Key) || string.IsNullOrWhiteSpace(e.Capabilities.DisplayName))
            .Select(e => e.GetType().Name)
            .ToList();
        Check(ohneKey.Count == 0,
            "F-M682 (c): jede Engine nennt ihren Key und ihren Anzeigenamen",
            ohneKey.Count == 0 ? "3 von 3" : "ohne: " + string.Join(", ", ohneKey));

        var nichtNachschlagbar = mitTvst.Keys
            .Where(k => mitTvst.ByKey(k) is null)
            .ToList();
        Check(nichtNachschlagbar.Count == 0 && mitTvst.ByKey("gibtesnicht") is null,
            "F-M682 (c): ByKey findet jede Engine und erfindet keine",
            nichtNachschlagbar.Count == 0 ? "3 von 3, unbekannt → null" : "fehlt: " + string.Join(", ", nichtNachschlagbar));

        var sprachlos = mitTvst.Engines
            .Where(e => e.Capabilities.LanguageCode("DEU") is not { Length: > 0 })
            .Select(e => e.Capabilities.Key)
            .ToList();
        Check(sprachlos.Count == 0,
            "F-M682: jede Engine bildet unsere ISO-Sprachen auf ihre Codes ab (DEU → Code der Quelle)",
            string.Join(", ", mitTvst.Engines.Select(e => e.Capabilities.Key + "→" + e.Capabilities.LanguageCode("DEU"))));

        var subdlCap = mitTvst.ByKey("subdl")!.Capabilities;
        var tvstCap = mitTvst.ByKey("tvsubtitles")!.Capabilities;
        var msubCap = mitTvst.ByKey("moviesubtitles")!.Capabilities;
        Check(subdlCap.SearchesByImdb && !subdlCap.SearchesByName && subdlCap.CanSeasonPacks
              && tvstCap.SearchesByName && !tvstCap.SearchesByImdb
              && msubCap.SearchesByName && !msubCap.CanSeasonPacks && !msubCap.CanReport,
            "F-M682: die Fähigkeiten beschreiben die Quellen ehrlich (SubDL über Id, tvst über Namen, Film ohne Packs und ohne Meldeweg)",
            "subdl imdb=" + subdlCap.SearchesByImdb + " packs=" + subdlCap.CanSeasonPacks
            + ", tvst name=" + tvstCap.SearchesByName + ", msub packs=" + msubCap.CanSeasonPacks
            + " report=" + msubCap.CanReport);

        // -----------------------------------------------------------------------------------------
        // (d) Die vier Aufträge sind die EINZIGEN öffentlichen Methoden des Kontrakts.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(d) vier Aufträge, nicht mehr");
        // Die Eigenschaft bringt ihren Getter als Methode mit (get_Capabilities); die Zählung der
        // AUFTRÄGE lässt genau diese Zugriffsmethoden weg und zählt nur, was ein Auftrag ist.
        MethodInfo[] auftraege = typeof(ISourceEngine).GetMethods()
            .Where(m => !m.IsSpecialName)
            .ToArray();
        var namen = auftraege.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Check(auftraege.Length == 4,
            "F-M682 (d): der Kontrakt hat genau VIER öffentliche Methoden (gezählt, nicht behauptet)",
            auftraege.Length + ": " + string.Join(", ", namen));
        Check(namen.SequenceEqual(new[] { "FetchAsync", "ReportAsync", "SearchAsync", "SendAsync" }),
            "F-M682 (d): es sind Suche, Hole, Sende und Melde — kein fünfter Auftrag",
            string.Join(", ", namen));
        Check(typeof(ISourceEngine).GetProperties().Length == 1
              && typeof(ISourceEngine).GetProperty("Capabilities") is not null,
            "F-M682 (d): die Fähigkeiten sind eine Eigenschaft, kein Auftrag",
            typeof(ISourceEngine).GetProperties().Length + " Eigenschaft(en)");

        // -----------------------------------------------------------------------------------------
        // Die Anfrage trägt ALLES, was wir über den Titel wissen — das ist Teil des Auftrags.
        // -----------------------------------------------------------------------------------------
        var suchFelder = new[]
        {
            nameof(SourceSearchRequest.ImdbId), nameof(SourceSearchRequest.TmdbId),
            nameof(SourceSearchRequest.Name), nameof(SourceSearchRequest.OriginalName),
            nameof(SourceSearchRequest.Year), nameof(SourceSearchRequest.IsSeries),
            nameof(SourceSearchRequest.Season), nameof(SourceSearchRequest.Episode),
            nameof(SourceSearchRequest.OsHash), nameof(SourceSearchRequest.Languages),
            nameof(SourceSearchRequest.HearingImpaired), nameof(SourceSearchRequest.WantsPack),
        };
        var fehlend = suchFelder
            .Where(n => typeof(SourceSearchRequest).GetProperty(n) is null)
            .ToList();
        Check(fehlend.Count == 0,
            "F-M682 (1): die Suchanfrage trägt alles über den Titel (Id, Name, Original, Jahr, Art, Staffel, Folge, Hash, Sprachen, HI, Pack)",
            fehlend.Count == 0 ? suchFelder.Length + " Felder" : "fehlt: " + string.Join(", ", fehlend));

        Check(typeof(SourceCandidate).GetProperty("Handle") is not null
              && typeof(SourceCandidate).GetProperty("IsPack") is not null
              && typeof(SourceFetchRequest).GetProperty("MemberName") is not null
              && typeof(SourceSendRequest).GetProperty("Comment") is not null
              && typeof(SourceSendRequest).GetProperty("Target") is not null
              && typeof(SourceSendRequest).GetProperty("Members") is not null
              && typeof(SourceReportRequest).GetProperty("RemoteId") is not null,
            "F-M682 (2)-(4): Griff, Pack-Zugehörigkeit, Mitgliedsname, Kommentar, Ziel, Mitglieder und fremde Kennung stehen in den Aufträgen");

        Check(typeof(SourceEngineContext).GetProperty("Http") is not null
              && typeof(SourceEngineContext).GetProperty("Logger") is not null
              && typeof(SourceEngineContext).GetProperty("Clock") is not null
              && typeof(SourceEngineContext).GetProperty("ArchiveCache") is not null,
            "F-M682 (b): der Kontext trägt HttpClient, Logger, Zeitgeber und den gemeinsamen Archiv-Cache");

        var fehlerArten = Enum.GetNames<SourceFault>();
        Check(new[] { "NotFound", "RateLimited", "AuthFailed", "Rejected", "Transient", "NoSearchBase" }
                .All(fehlerArten.Contains),
            "F-M682: die sechs normalisierten Fehler stehen im Kontrakt (NotFound, RateLimited, AuthFailed, Rejected, Transient, NoSearchBase)",
            string.Join(", ", fehlerArten));

        // -----------------------------------------------------------------------------------------
        // (e) Der HttpClient wird von außen gereicht.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(e) kein eigener HttpClient unter Sources/");
        var eigeneClients = Directory.GetFiles(sourcesDir, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => TrefferZeilen(f, "new HttpClient(").Concat(TrefferZeilen(f, "new SocketsHttpHandler(")))
            .ToList();
        Check(eigeneClients.Count == 0,
            "F-M682 (e): keine Datei unter Sources/ legt einen eigenen HttpClient an",
            eigeneClients.Count == 0 ? "0 Treffer" : string.Join(" | ", eigeneClients.Take(4)));

        // -----------------------------------------------------------------------------------------
        // Der EINE Verdrahtungspunkt: die Frage nach dem Pack-Slot läuft über die Registry.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("Verdrahtung: der Pack-Slot kommt aus der Registry");
        Check(pipelineQuelle.Contains("private List<string> PackSlotSources() => _sources.PackSlot().ToList();", StringComparison.Ordinal),
            "F-M682: `PackSlotSources()` fragt die Registry statt zwei Namen zu verdrahten");
        Check(pipelineQuelle.Contains("var draussen = _sources.PackSlotExcluded();", StringComparison.Ordinal)
              && !pipelineQuelle.Contains("slotQuellen.Contains(PackEntity.TvSubtitlesSource)", StringComparison.Ordinal),
            "F-M682: auch der GRUND („nicht im Slot“) kommt aus der Registry — kein Quellenname in der Log-Zeile");
        Check(!pipelineQuelle.Contains("quellen.Add(PackEntity.TvSubtitlesSource)", StringComparison.Ordinal)
              && !pipelineQuelle.Contains("new List<string> { PackEntity.SubdlSource }", StringComparison.Ordinal),
            "F-M682: die beiden fest verdrahteten Namen sind aus der Slot-Frage verschwunden");

        Check(ohneTvst.PackSlot().SequenceEqual(new[] { "subdl" }),
            "F-M682: ohne tvst-Upload-Schalter ist der Slot `subdl` — wie vor der Umstellung",
            string.Join(", ", ohneTvst.PackSlot()));
        Check(mitTvst.PackSlot().SequenceEqual(new[] { "subdl", "tvsubtitles" }),
            "F-M682/F-M680: mit eingeschaltetem tvst-Schalter ist der Slot `subdl, tvsubtitles` — dieselbe Antwort wie vorher",
            string.Join(", ", mitTvst.PackSlot()));

        // -----------------------------------------------------------------------------------------
        // Bericht.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("sources: " + _checks + " Prüfungen (T339, F-M682)");
        if (Failures.Count == 0)
        {
            Console.WriteLine(">>> SOURCES OK: " + _checks + " Prüfungen bestanden");
            return 0;
        }

        Console.WriteLine(">>> SOURCES FEHLER: " + Failures.Count + " von " + _checks + " Prüfungen fehlgeschlagen");
        foreach (string f in Failures)
        {
            Console.WriteLine("   - " + f);
        }

        return 1;
    }

    /// <summary>
    /// F-M682 (c): die Gestalt unserer Marken in einer Datei — <c>[xxxx]</c> (vier Zeichen in eckigen
    /// Klammern) und <c>#yyyyy</c> (Raute mit Ziffern).
    /// </summary>
    /// <param name="datei">Die Datei.</param>
    /// <returns>Die Treffer als <c>Datei:Zeile: Inhalt</c>.</returns>
    private static IEnumerable<string> MarkenTreffer(string datei)
    {
        string[] zeilen = File.ReadAllLines(datei);
        var muster = new[]
        {
            new System.Text.RegularExpressions.Regex(@"\[[0-9a-z]{4}\]"),
            new System.Text.RegularExpressions.Regex(@"#\d{4,}"),
        };
        for (int i = 0; i < zeilen.Length; i++)
        {
            if (muster.Any(m => m.IsMatch(zeilen[i])))
            {
                yield return Path.GetFileName(datei) + ":" + (i + 1).ToString() + ": " + zeilen[i].Trim();
            }
        }
    }

    /// <summary>Zeilen einer Datei, die einen Text enthalten — mit Dateiname und Zeilennummer.</summary>
    /// <param name="datei">Die Datei.</param>
    /// <param name="text">Der gesuchte Text.</param>
    /// <returns>Die Treffer als <c>Datei:Zeile: Inhalte</c>.</returns>
    private static IEnumerable<string> TrefferZeilen(string datei, string text)
    {
        string[] zeilen = File.ReadAllLines(datei);
        for (int i = 0; i < zeilen.Length; i++)
        {
            if (zeilen[i].Contains(text, StringComparison.Ordinal))
            {
                yield return Path.GetFileName(datei) + ":" + (i + 1).ToString() + ": " + zeilen[i].Trim();
            }
        }
    }
}
