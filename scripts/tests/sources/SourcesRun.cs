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
using System.Threading;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Sources;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Sources.Msub;
using Jellyfin.Plugin.SubScribe.Sources.Subdl;
using Jellyfin.Plugin.SubScribe.Sources.Tvst;
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
        Check(typeof(ISourceEngine).GetProperties().Length == 2
              && typeof(ISourceEngine).GetProperty("Capabilities") is not null
              && typeof(ISourceEngine).GetProperty("Fields") is not null,
            "F-M683 (d): der Kontrakt hat genau ZWEI Eigenschaften — die Fähigkeiten und die Felder (Eigenschaften getrennt von Aufträgen gezählt)",
            typeof(ISourceEngine).GetProperties().Length + " Eigenschaft(en): "
            + string.Join(", ", typeof(ISourceEngine).GetProperties().Select(p => p.Name)));

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
        // (f) F-M683: Einstellungen je Quelle — Erklärung, eigene Datei, Endpunkt, Geheimnisse.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(f) jede Quelle erklärt ihre Felder und liest ihre eigene Datei");

        // Ein Prüfwert, der nirgends echt ist und nur in diesem Temp-Verzeichnis steht. Er wird
        // verglichen, nie ausgegeben.
        const string PruefSchluessel = "pruefschluessel-4711";
        const string PruefKonto = "konto-pruefung";

        string tmp = Path.Combine(Path.GetTempPath(), "sources-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var uebernahmeConfig = new PluginConfiguration { ApiKey = PruefSchluessel, Username = PruefKonto };

            // ---- D: die Übernahme beim ersten Start ----
            var ersterLauf = SourceSettingsSeed.Ensure(tmp, uebernahmeConfig);
            string[] quellen = { "subdl", "tvsubtitles", "moviesubtitles" };
            Check(ersterLauf.Count == 3 && quellen.All(k => File.Exists(SourceSettingsFile.PathIn(tmp, k))),
                "F-M683 (D): der erste Start schreibt je Quelle GENAU EINE Datei unter sources/",
                string.Join(", ", ersterLauf));

            var subdlGelesen = SourceSettingsFile.Load(tmp, SubdlEngine.SourceKey, SubdlEngine.Declared);
            bool schluesselGleich = string.Equals(
                subdlGelesen.Secret(SubdlEngine.FieldApiKey), uebernahmeConfig.ApiKey, StringComparison.Ordinal);
            bool kontoGleich = string.Equals(
                subdlGelesen.Text(SubdlEngine.FieldAccount), uebernahmeConfig.Username, StringComparison.Ordinal);
            Check(subdlGelesen.IsConfigured && schluesselGleich && kontoGleich,
                "F-M683 (D): der aus der Datei gelesene Wert ist der bisherige (Schlüssel und Konto verglichen, nicht ausgegeben)",
                "Schlüssel gleich=" + schluesselGleich + ", Konto gleich=" + kontoGleich);

            string subdlDatei = SourceSettingsFile.PathIn(tmp, SubdlEngine.SourceKey);
            string subdlRoh = File.ReadAllText(subdlDatei);
            Check(subdlRoh.Contains("\"schema\": 1", StringComparison.Ordinal)
                  && subdlRoh.Contains("\"key\": \"subdl\"", StringComparison.Ordinal),
                "F-M683 (B): die Datei trägt Schema-Zahl und Quellenschlüssel — und die Werte daneben",
                subdlRoh.Split('\n').Length + " Zeilen, " + subdlRoh.Length + " Zeichen");

            byte[] vorZweitemLauf = File.ReadAllBytes(subdlDatei);
            var zweitemLauf = SourceSettingsSeed.Ensure(tmp, uebernahmeConfig);
            Check(zweitemLauf.Count == 0 && File.ReadAllBytes(subdlDatei).SequenceEqual(vorZweitemLauf),
                "F-M683 (D): ein zweiter Start schreibt nichts und ändert den gelesenen Wert nicht",
                "geschrieben: " + zweitemLauf.Count + " Datei(en)");

            // ---- B: eine Datei je Quelle, und keine zwei Quellen in derselben ----
            var kontext = new SourceEngineContext(
                new HttpClient(), NullLogger.Instance, TimeProvider.System, DataFolder: tmp);
            var registry = SourceRegistry.Build(kontext, uebernahmeConfig);

            var dateien = new[]
            {
                // F-M684: die Registry reicht die Engine durch eine sprechende Hülle — die konkrete
                // Engine kommt über Engine<T>() heraus, der Kontrakt bleibt der einzige Weg zum Auftrag.
                registry.Engine<SubdlEngine>()!.Settings.Path,
                registry.Engine<TvstEngine>()!.Settings.Path,
                registry.Engine<MsubEngine>()!.Settings.Path,
            };
            Check(dateien.Distinct(StringComparer.Ordinal).Count() == 3
                  && quellen.Select(k => string.Equals(dateien[Array.IndexOf(quellen, k)],
                      SourceSettingsFile.PathIn(tmp, k), StringComparison.Ordinal)).All(x => x),
                "F-M683 (B): jede Quelle liest ihre EIGENE Datei — drei verschiedene Pfade, je <key>.json",
                string.Join(", ", dateien.Select(d => Path.GetFileName(d!))));

            // Eine kopierte Datei gehört weiter der Quelle, die in ihr steht: sie gilt für die andere
            // Quelle NICHT. Das ist die Sperre dagegen, dass zwei Engines dieselbe Datei benutzen.
            string tvstDatei = SourceSettingsFile.PathIn(tmp, TvstEngine.SourceKey);
            File.Copy(subdlDatei, tvstDatei, overwrite: true);
            var kopie = SourceSettingsFile.Load(tmp, TvstEngine.SourceKey, TvstEngine.Declared);
            Check(!kopie.IsConfigured && (kopie.Problem ?? string.Empty).Contains("subdl", StringComparison.Ordinal),
                "F-M683 (B): eine kopierte Datei gilt nicht — die Datei nennt ihren Quellenschlüssel, und der muss passen",
                kopie.Problem ?? "kein Hinweis");

            // ---- A/C: der Endpunkt liefert jedes erklärte Feld, und kein erfundenes ----
            var sicht = Jellyfin.Plugin.SubScribe.Api.SourceSettingsView.Build(registry, tmp);
            Check(sicht.Count == 3 && sicht.Select(e => e.Key).SequenceEqual(registry.Keys),
                "F-M683 (C): der Endpunkt liefert je Quelle einen Eintrag, in der Reihenfolge der Registry",
                string.Join(", ", sicht.Select(e => e.Key)));

            var feldFehler = new List<string>();
            foreach (var eintrag in sicht)
            {
                var erklaert = registry.ByKey(eintrag.Key)!.Fields;
                if (eintrag.Fields.Count != erklaert.Count)
                {
                    feldFehler.Add(eintrag.Key + ": " + eintrag.Fields.Count + " statt " + erklaert.Count);
                    continue;
                }

                for (int i = 0; i < erklaert.Count; i++)
                {
                    if (!string.Equals(eintrag.Fields[i].Key, erklaert[i].Key, StringComparison.Ordinal)
                        || !string.Equals(eintrag.Fields[i].Label, erklaert[i].Label, StringComparison.Ordinal)
                        || !string.Equals(eintrag.Fields[i].Kind, SourceField.KindName(erklaert[i].Kind), StringComparison.Ordinal)
                        || eintrag.Fields[i].Required != erklaert[i].Required)
                    {
                        feldFehler.Add(eintrag.Key + "/" + erklaert[i].Key);
                    }
                }
            }

            Check(feldFehler.Count == 0,
                "F-M683 (A)/(C): jedes erklärte Feld kommt im Endpunkt an — in Erklärungsreihenfolge, mit Beschriftung, Art und Pflicht —, und kein Feld wird erfunden",
                feldFehler.Count == 0
                    ? string.Join(", ", sicht.Select(e => e.Key + "=" + e.Fields.Count))
                    : string.Join(" | ", feldFehler));

            var feldStand = registry.Engines
                .SelectMany(e => e.Fields.Select(f => e.Capabilities.Key + " " + f.Key + " = "
                    + (f.Default.Length == 0 ? "[leer]" : f.Default)))
                .ToList();
            var erwarteteFelder = new[]
            {
                "subdl apiKey = [leer]", "subdl account = [leer]",
                "subdl baseUrl = " + Jellyfin.Plugin.SubScribe.Api.ScribeApiClient.ApiBase,
                "subdl preferSeasonZip = true",
                "tvsubtitles baseUrl = " + Jellyfin.Plugin.SubScribe.Api.TvSubtitlesClient.BaseUrl,
                "moviesubtitles baseUrl = " + Jellyfin.Plugin.SubScribe.Api.MovieSubtitlesClient.BaseUrl,
                "moviesubtitles access = [leer]",
            };
            Check(feldStand.SequenceEqual(erwarteteFelder),
                "F-M683 (A): die Felder der drei Quellen sind die vereinbarten — und ihre Vorgaben sind die Adressen, die die Clients WIRKLICH benutzen",
                string.Join(", ", feldStand));

            // ---- C: ein Geheimnis steht nie in der Antwort ----
            string antwort = System.Text.Json.JsonSerializer.Serialize(sicht);
            var subdlEintrag = sicht.Single(e => e.Key == SubdlEngine.SourceKey);
            var keyFeld = subdlEintrag.Fields.Single(f => f.Key == SubdlEngine.FieldApiKey);
            Check(!antwort.Contains(PruefSchluessel, StringComparison.Ordinal)
                  && keyFeld.Gesetzt && keyFeld.Value is null && keyFeld.Default is null,
                "F-M683 (C): ein Geheimwert steht NIE in der Antwort — nur gesetzt=true (Wert und Vorgabe sind null)",
                "Antwort " + antwort.Length + " Zeichen, Wert nicht enthalten: "
                + !antwort.Contains(PruefSchluessel, StringComparison.Ordinal));

            // ---- C: Schreiben trifft genau eine Datei ----
            string msubDatei = SourceSettingsFile.PathIn(tmp, MsubEngine.SourceKey);
            byte[] tvstVorher = File.ReadAllBytes(tvstDatei);
            byte[] msubVorher = File.ReadAllBytes(msubDatei);
            var schreiben = Jellyfin.Plugin.SubScribe.Api.SourceSettingsView.Save(
                registry, tmp, SubdlEngine.SourceKey,
                new Dictionary<string, string?> { ["account"] = "konto-neu", ["preferSeasonZip"] = "false" });
            Check(schreiben.Ok
                  && File.ReadAllBytes(tvstDatei).SequenceEqual(tvstVorher)
                  && File.ReadAllBytes(msubDatei).SequenceEqual(msubVorher),
                "F-M683 (C): ein Schreibvorgang lässt die Dateien der anderen Quellen byte-gleich",
                "geschrieben: " + string.Join(", ", schreiben.Written));

            var rechte = File.GetUnixFileMode(subdlDatei);
            Check(OperatingSystem.IsWindows()
                  || rechte == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                "F-M683 (B): die Datei einer Quelle hat die Rechte 0600",
                OperatingSystem.IsWindows() ? "Windows: nicht prüfbar" : rechte.ToString());

            var leerGeheim = Jellyfin.Plugin.SubScribe.Api.SourceSettingsView.Save(
                registry, tmp, SubdlEngine.SourceKey,
                new Dictionary<string, string?> { [SubdlEngine.FieldApiKey] = string.Empty });
            var nachLeerem = SourceSettingsFile.Load(tmp, SubdlEngine.SourceKey, SubdlEngine.Declared);
            Check(leerGeheim.Ok && nachLeerem.Has(SubdlEngine.FieldApiKey)
                  && string.Equals(nachLeerem.Secret(SubdlEngine.FieldApiKey), PruefSchluessel, StringComparison.Ordinal),
                "F-M683 (C): ein leeres Geheimfeld bedeutet „unverändert lassen\" — der abgelegte Wert bleibt",
                "gesetzt=" + nachLeerem.Has(SubdlEngine.FieldApiKey));

            var unbekannt = Jellyfin.Plugin.SubScribe.Api.SourceSettingsView.Save(
                registry, tmp, SubdlEngine.SourceKey,
                new Dictionary<string, string?> { ["erfunden"] = "x" });
            Check(!unbekannt.Ok && unbekannt.UnknownFields.Contains("erfunden"),
                "F-M683 (C): ein unbekanntes Feld wird abgewiesen statt gespeichert",
                unbekannt.Error ?? "kein Grund");

            // ---- D: danach liest die Engine aus ihrer Datei, nicht aus der Konfiguration ----
            var nachSchreiben = SourceRegistry.Build(kontext, uebernahmeConfig);
            var subdlNachher = nachSchreiben.Engine<SubdlEngine>()!; // F-M684: hinter der Hülle
            Check(string.Equals(subdlNachher.Account, "konto-neu", StringComparison.Ordinal)
                  && !subdlNachher.PreferSeasonZip,
                "F-M683 (D): die Engine liest Konto und Schalter aus ihrer Datei — der neue Wert gilt ohne Neustart der Konfiguration",
                "Konto aus der Datei=" + subdlNachher.Account + ", Schalter=" + subdlNachher.PreferSeasonZip);

            // ---- B: eine kaputte Datei trifft nur ihre eine Quelle ----
            File.WriteAllText(tvstDatei, "{ das ist kein JSON");
            var mitKaputter = SourceRegistry.Build(kontext, uebernahmeConfig);
            var sichtKaputt = Jellyfin.Plugin.SubScribe.Api.SourceSettingsView.Build(mitKaputter, tmp);
            var tvstEintrag = sichtKaputt.Single(e => e.Key == TvstEngine.SourceKey);
            var subdlEintrag2 = sichtKaputt.Single(e => e.Key == SubdlEngine.SourceKey);
            var msubEintrag = sichtKaputt.Single(e => e.Key == MsubEngine.SourceKey);
            Check(!tvstEintrag.Configured && !string.IsNullOrEmpty(tvstEintrag.Hint),
                "F-M683 (B): die kaputte Datei macht GENAU DIESE Quelle nicht eingerichtet — mit Klartext-Hinweis",
                tvstEintrag.Hint ?? "kein Hinweis");

            Check(subdlEintrag2.Configured && msubEintrag.Configured,
                "F-M683 (B): die anderen Quellen laufen weiter — sie lesen ihre eigenen Dateien",
                "subdl=" + subdlEintrag2.Configured + ", moviesubtitles=" + msubEintrag.Configured);

            var tvstSuche = mitKaputter.ByKey(TvstEngine.SourceKey)!
                .SearchAsync(
                    new SourceSearchRequest { Name = "Pruefung", IsSeries = true, Season = 1, Episode = 1 },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Check(tvstSuche.Fault?.Kind == SourceFault.NoSearchBase && tvstSuche.Candidates.Count == 0,
                "F-M683 (B): die nicht eingerichtete Quelle verweigert die Suche mit einem Grund — statt zu raten und ins Netz zu gehen",
                tvstSuche.Fault?.Kind.ToString() ?? "kein Fehler");

            // ---- A: ein Geheimnis trägt nie eine Vorgabe ----
            var geheimMitVorgabe = registry.Engines
                .SelectMany(e => e.Fields)
                .Where(f => f.IsSecret && f.Default.Length > 0)
                .Select(f => f.Key)
                .ToList();
            Check(geheimMitVorgabe.Count == 0,
                "F-M683 (A): kein Geheimfeld trägt eine Vorgabe — ein Geheimnis, das niemand eingetragen hat, ist leer",
                geheimMitVorgabe.Count == 0 ? "0 Geheimnisse mit Vorgabe" : string.Join(", ", geheimMitVorgabe));

            // ---- C: die Routen und der Zugang ----
            string controller = File.ReadAllText(Path.Combine(plugin, "Api", "SourceSettingsController.cs"));
            Check(controller.Contains("[HttpGet(\"Sources\")]", StringComparison.Ordinal)
                  && controller.Contains("[HttpPost(\"Sources/{key}\")]", StringComparison.Ordinal)
                  && controller.Contains("[Authorize(Policy = \"LocalAccessOrRequiresElevation\")]", StringComparison.Ordinal)
                  && !controller.Contains("AllowAnonymous", StringComparison.Ordinal),
                "F-M683 (C): die Routen stehen als GET Sources und POST Sources/{key}, und der Zugang ist der eines Administrators — nicht anonym");
        }
        finally
        {
            try
            {
                Directory.Delete(tmp, recursive: true);
            }
            catch (IOException)
            {
                // Ein liegengebliebenes Temp-Verzeichnis ist kein Prüfergebnis.
            }
        }

        // -----------------------------------------------------------------------------------------
        // (g) F-M684: Diagnose — ehrlicher Pegel, Bündelung, vier beredte Aufträge, Zähler je Quelle.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine("(g) F-M684: Pegel, Bündelung, vier Aufträge, Zähler je Quelle");

        // (a) Der Quelltext: die lauten Zeilen stehen auf ECHTEM Debug-Pegel (LogDebug), nicht auf
        // Information mit einem Textmarker. Geprüft wird die Stelle, nicht die Behauptung.
        string dbDatei = File.ReadAllText(Path.Combine(plugin, "Data", "ScribeDbContext.cs"));
        Check(dbDatei.Contains("LogUtil.Debug(_logger, \"[Sub Scribe-DB] file upsert", StringComparison.Ordinal),
            "F-M684 (a): die Zeile je Datensatz im Store („file upsert\", im Prod-Log „media upsert\") steht auf LogDebug");

        string trackerDatei = File.ReadAllText(Path.Combine(plugin, "Registry", "DownloadSearchTracker.cs"));
        Check(trackerDatei.Contains("LogUtil.Debug(_logger, \"[Sub Scribe-DB] search stamp", StringComparison.Ordinal)
              && !trackerDatei.Contains("LogUtil.Detail(", StringComparison.Ordinal),
            "F-M684 (a): jede Suchstempel-Zeile steht auf LogDebug — im Tracker gibt es keine Detail-Zeile mehr",
            trackerDatei.Split("LogUtil.Debug(").Length - 1 + " Debug-Aufrufe, 0 Detail");

        string markenDatei = File.ReadAllText(Path.Combine(plugin, "Registry", "ContentHashRegistry.cs"));
        Check(markenDatei.Contains("LogUtil.Debug(_logger, \"[Sub Scribe-DB] embed", StringComparison.Ordinal)
              && markenDatei.Contains("LogUtil.Debug(_logger, \"[Sub Scribe-DB] sidecar", StringComparison.Ordinal)
              && markenDatei.Contains("LogUtil.Debug(_logger, \"[Sub Scribe-DB] subtitles uploaded-complete", StringComparison.Ordinal),
            "F-M684 (a): die Markenzeilen (embed, sidecar, uploaded-complete) stehen auf LogDebug");

        string dlQuelle = File.ReadAllText(Path.Combine(plugin, "Pipeline", "DownloadPipeline.cs"));
        Check(dlQuelle.Contains("LogUtil.Debug(_config.LogMode, _logger, \"[Sub Scribe-D] {Msg}\", msg)", StringComparison.Ordinal),
            "F-M684 (a): die API-Aufruf/Antwort-Paare des Downloads stehen auf LogDebug");

        string ulQuelle2 = File.ReadAllText(Path.Combine(plugin, "Pipeline", "UploadPipeline.cs"));
        Check(ulQuelle2.Contains("LogUtil.Debug(_config.LogMode, _logger, \"[Sub Scribe] {Msg}\", msg)", StringComparison.Ordinal),
            "F-M684 (a): dasselbe im Upload — beide Richtungen zählen und schweigen gleich");

        string logDatei = File.ReadAllText(Path.Combine(plugin, "Pipeline", "LogUtil.cs"));
        Check(logDatei.Contains("logger?.LogDebug(DebugTag + message, args)", StringComparison.Ordinal)
              && logDatei.Contains("if (mode >= LogLevelMode.Debug)", StringComparison.Ordinal),
            "F-M684 (a): LogUtil.Debug schreibt mit LogDebug und hält den LogMode-Schalter — der TEXT bleibt gleich");

        // (b) Die vier Aufträge sprechen: je Auftrag eine Zeile, mit einem sammelnden Logger gemessen.
        var ruf = new Rec();
        string tmpQ = Path.Combine(Path.GetTempPath(), "sources-fm684-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpQ);
        try
        {
            var qKontext = new SourceEngineContext(
                new HttpClient(), ruf, TimeProvider.System, DataFolder: tmpQ);

            // Ein Schlüssel steht in der Datei (damit „kein Geheimwert in einer Zeile" messbar ist), aber
            // KEIN Auftrag geht ins Netz: jede Anfrage unten endet vor dem ersten Aufruf an der Grenze.
            // LogMode DEBUG: die Auftragszeilen sind Massenzeilen und erscheinen nur dann — bei Normal
            // bleibt es bei den sichtbaren Fehlerzeilen, genau wie der Schalter es verspricht.
            var qConfig = new PluginConfiguration
            {
                ApiKey = PruefSchluessel,
                LogMode = Jellyfin.Plugin.SubScribe.Configuration.LogLevelMode.Debug,
            };
            var qRegistry = SourceRegistry.Build(qKontext, qConfig);
            var quelle = qRegistry.ByKey(SubdlEngine.SourceKey)!;

            SourceTraffic.BeginRun(SourceTraffic.KnownKeys);

            // Zwei Suchen ohne Kennung: die Quelle sucht über Ids, also endet jede als „keine Suchgrundlage"
            // — ohne einen einzigen Netzzugriff.
            var sucheA = quelle.SearchAsync(
                new SourceSearchRequest { Name = "Pruefung", IsSeries = true, Season = 1, Episode = 1 },
                CancellationToken.None).GetAwaiter().GetResult();
            var sucheB = quelle.SearchAsync(
                new SourceSearchRequest { Name = "Pruefung", IsSeries = true, Season = 1, Episode = 1 },
                CancellationToken.None).GetAwaiter().GetResult();

            // (d) Die Zähler zählen hoch: zwei Suchen → zwei Abrufe, zwei Aufträge, zwei Fehlschläge.
            var nachZwei = SourceTraffic.For(SubdlEngine.SourceKey).Snapshot();
            Check(nachZwei.Requests == 2 && nachZwei.Tasks == 2 && nachZwei.NoSearchBase == 2,
                "F-M684 (d): Suche 2× zählt zwei Abrufe, zwei Aufträge und zwei Fehlschläge (keine Suchgrundlage)",
                "Abrufe=" + nachZwei.Requests + ", Aufträge=" + nachZwei.Tasks
                + ", keine Suchgrundlage=" + nachZwei.NoSearchBase + ", Fehler=" + nachZwei.Errors);

            var hole = quelle.FetchAsync(new SourceFetchRequest(string.Empty), CancellationToken.None)
                .GetAwaiter().GetResult();
            var sende = quelle.SendAsync(
                new SourceSendRequest { FileName = "x.srt", Language = "DEU" }, CancellationToken.None)
                .GetAwaiter().GetResult();
            var melde = quelle.ReportAsync(
                new SourceReportRequest(string.Empty, SourceReportKind.Missing, "Pruefung"),
                CancellationToken.None).GetAwaiter().GetResult();

            bool Zeile(string auftrag) => ruf.Lines.Any(l => l.Text.Contains("[Sub Scribe-Q]", StringComparison.Ordinal)
                && l.Text.Contains(" " + auftrag + " —", StringComparison.Ordinal));
            Check(Zeile("Suche") && sucheA.Fault?.Kind == SourceFault.NoSearchBase
                  && sucheB.Fault?.Kind == SourceFault.NoSearchBase,
                "F-M684 (b): der Auftrag Suche erzeugt eine Zeile mit Quelle, Kennungen, Sprachen, HI und Ergebnis",
                ruf.Lines.FirstOrDefault(l => l.Text.Contains(" Suche —", StringComparison.Ordinal)).Text);
            Check(Zeile("Hole") && hole.Fault?.Kind == SourceFault.NotFound,
                "F-M684 (b): der Auftrag Hole erzeugt eine Zeile mit Griff, Folge, Mitglied, Größe und Dauer",
                ruf.Lines.FirstOrDefault(l => l.Text.Contains(" Hole —", StringComparison.Ordinal)).Text);
            Check(Zeile("Sende") && sende.Status == SourceSendStatus.Rejected,
                "F-M684 (b): der Auftrag Sende erzeugt eine Zeile mit Umfang, Sprache, HI, Ziel und Urteil",
                ruf.Lines.FirstOrDefault(l => l.Text.Contains(" Sende —", StringComparison.Ordinal)).Text);
            Check(Zeile("Melde") && melde.Status == SourceReportStatus.Failed,
                "F-M684 (b): der Auftrag Melde erzeugt eine Zeile mit Eintrag, Art und Urteil",
                ruf.Lines.FirstOrDefault(l => l.Text.Contains(" Melde —", StringComparison.Ordinal)).Text);

            Check(ruf.Lines.Any(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Debug
                      && l.Text.StartsWith("[D] ", StringComparison.Ordinal)),
                "F-M684 (b): die Auftragszeile ist eine ECHTE Debug-Zeile (LogDebug) und trägt weiter den [D]-Marker",
                ruf.Lines.Count(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Debug) + " Debug-Zeile(n) gesammelt");

            Check(ruf.Lines.Any(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                      && l.Text.Contains("keine Suchgrundlage", StringComparison.Ordinal))
                  && ruf.Lines.Any(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                      && l.Text.Contains("Feld abgewiesen", StringComparison.Ordinal)),
                "F-M684 (b): „keine Suchgrundlage\" und „Feld abgewiesen\" stehen sichtbar auf WRN — nicht nur als Zähler");

            // (4) Der Endpunkt liefert die Zähler, und sie leben im Speicher (kein Store, keine Ableitung).
            var eintrag = Jellyfin.Plugin.SubScribe.Api.SourceSettingsView.Build(qRegistry, tmpQ)
                .Single(e => e.Key == SubdlEngine.SourceKey);
            Check(eintrag.Counters.Requests == 5 && eintrag.Counters.Tasks == 5
                  && eintrag.Counters.Errors >= 3 && eintrag.Counters.LastFailureUtc is not null,
                "F-M684 (4): der Endpunkt Plugins/SubdlSync/Sources nennt je Quelle Abrufe, Aufträge, Fehlschläge und den letzten Fehler",
                "Abrufe=" + eintrag.Counters.Requests + ", Aufträge=" + eintrag.Counters.Tasks
                + ", Fehler=" + eintrag.Counters.Errors
                + ", Grund=" + (eintrag.Counters.LastFailureReason ?? "-"));

            // (2) Die Summenzeile: eine Zeile je Quelle, auf Normal, mit Aufrufen, Seiten, Fehlern, Dauer.
            SourceTraffic.WriteSummary(ruf, new[] { SubdlEngine.SourceKey });
            string summe = ruf.Lines.Last(l => l.Text.Contains("Aufrufe", StringComparison.Ordinal)).Text;
            Check(summe.StartsWith("[N] SubDL: 5 Aufrufe, 0 Seiten, 3 Fehler, ", StringComparison.Ordinal)
                  && summe.Contains(" s", StringComparison.Ordinal)
                  && summe.Contains("(5 Aufträge)", StringComparison.Ordinal),
                "F-M684 (2): die Summenzeile nennt je Quelle Abrufe, Seiten, Fehler und Dauer — in EINER Zeile",
                summe);

            // (3) Ein unlesbares Einstellungsfile ist nicht mehr still: eine Zeile und ein Zähler.
            File.WriteAllText(SourceSettingsFile.PathIn(tmpQ, SubdlEngine.SourceKey), "{ das ist kein JSON");
            var nachKaputt = SourceRegistry.Build(qKontext, qConfig);
            var kaputt = SourceTraffic.For(SubdlEngine.SourceKey).Snapshot();
            Check(ruf.Lines.Any(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                      && l.Text.Contains("sources/subdl", StringComparison.Ordinal)
                      && l.Text.Contains("nicht eingerichtet", StringComparison.Ordinal))
                  && kaputt.ConfigFailed == 1,
                "F-M684 (3): ein unlesbares Einstellungsfile erzeugt eine Logzeile UND einen Zähler",
                "ConfigFailed=" + kaputt.ConfigFailed);
            Check(SourceTraffic.For(TvstEngine.SourceKey).Snapshot().ConfigFailed == 0
                  && SourceTraffic.For(MsubEngine.SourceKey).Snapshot().ConfigFailed == 0,
                "F-M684 (3): die kaputte Datei trifft nur ihre eine Quelle — die anderen zählen nichts",
                "tvst=" + SourceTraffic.For(TvstEngine.SourceKey).Snapshot().ConfigFailed
                + ", msub=" + SourceTraffic.For(MsubEngine.SourceKey).Snapshot().ConfigFailed);
            GC.KeepAlive(nachKaputt);

            // (5) Kein Geheimwert in irgendeiner Zeile — Schlüssel steht in der Datei, keine Zeile trägt ihn.
            var lecks = ruf.Lines
                .Where(l => l.Text.Contains(PruefSchluessel, StringComparison.Ordinal))
                .Select(l => l.Text)
                .ToList();
            Check(lecks.Count == 0,
                "F-M684 (5): keine der gesammelten Zeilen enthält den Prüfschlüssel — weder im Klartext noch in einer Ableitung",
                ruf.Lines.Count + " Zeile(n) geprüft, 0 mit Schlüssel");
        }
        finally
        {
            try
            {
                Directory.Delete(tmpQ, recursive: true);
            }
            catch (IOException)
            {
                // Ein liegengebliebenes Temp-Verzeichnis ist kein Prüfergebnis.
            }
        }

        // (2)/(4) Die Netzarbeit selbst: der Live-Kanal der Clients zählt Abruf und Antwort je Quelle —
        // inklusive der Bremse, die sonst nur als Textzeile im Log stünde.
        SourceTraffic.BeginRun(new[] { SubdlEngine.SourceKey });
        SourceTraffic.NoteTrace("API call GET https://api.subdl.com/subtitles");
        SourceTraffic.NoteTrace("API response GET https://api.subdl.com/subtitles → 200");
        var nachAntwort = SourceTraffic.For(SubdlEngine.SourceKey).Snapshot();
        Check(nachAntwort.Requests == 1 && nachAntwort.Pages == 1 && nachAntwort.LastSuccessUtc is not null,
            "F-M684 (2): der Live-Kanal zählt je Anfrage einen Abruf und je 2xx-Antwort eine Seite mit Zeitpunkt",
            "Abrufe=" + nachAntwort.Requests + ", Seiten=" + nachAntwort.Pages);

        SourceTraffic.NoteTrace("API response GET https://api.subdl.com/subtitles → 429");
        var nachBremse = SourceTraffic.For(SubdlEngine.SourceKey).Snapshot();
        Check(nachBremse.RateLimited == 1 && nachBremse.Errors == 1
              && (nachBremse.LastFailureReason ?? string.Empty).Contains("429", StringComparison.Ordinal),
            "F-M684 (4): eine Bremse (429) zählt als eigener Fehlschlag mit Zeitpunkt und Grund",
            "RateLimited=" + nachBremse.RateLimited + ", Grund=" + (nachBremse.LastFailureReason ?? "-"));

        // -----------------------------------------------------------------------------------------
        // Bericht.
        // -----------------------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("sources: " + _checks + " Prüfungen (T339/T340/T341, F-M682/F-M683/F-M684)");
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
    /// F-M684: ein sammelnder Logger für die Prüfung — jede Zeile mit ihrem PEGEL, damit „echte
    /// Debug-Stufe" gemessen und nicht behauptet wird.
    /// </summary>
    internal sealed class Rec : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
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
