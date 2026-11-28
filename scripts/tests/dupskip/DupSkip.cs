// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// T113 (F-M298): ein duplicate-content-Skip darf eine bereits vorhandene uploaded-Marke NICHT
// ueberschreiben.
//
// Nachgestellt wird der Prod-Fall vom 06.10.2026 (Lanterns S01E08 "Dirt and Stars"):
//   1. Der Upload laeuft und die Sprache landet als "uploaded" in der Registry.
//   2. Ein spaeterer Seeder-Lauf schreibt Sprach-Tags in den Container; die Datei bekommt einen
//      NEUEN Media-Hash, und ReplaceMediaIdentity traegt die Zeilen auf die neue Identitaet um.
//   3. Der naechste Upload-Lauf sieht denselben Inhalt, IsContentKnown() meldet "bekannt" (eine
//      rejected-Zeile zaehlt dort genauso wie eine uploaded-Zeile) und der Skip schrieb "rejected"
//      ueber die mitgenommene uploaded-Marke. Ergebnis auf prod: 39x rejected, 0x uploaded — die
//      Datei galt als unerledigt und wurde in jedem weiteren Lauf neu extrahiert und neu abgelehnt.
//
// Der Test faehrt die ECHTE Entscheidung (ContentHashRegistry.RecordSkippedContent), nicht eine
// Kopie davon. Zwei Faelle:
//   A) Inhalt ist nachweislich oben  -> die Marke bleibt "uploaded", nichts wird gezaehlt.
//   B) Inhalt ist NICHT oben (nur eine rejected-Zeile) -> die Ablehnung wird geschrieben wie bisher.
//
// Fall B ist die Absicherung: ohne ihn wuerde ein Fix, der einfach jeden duplicate-content-Skip
// als uploaded verbucht, hier ebenfalls "OK" melden — und luegen.
using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Data;
using Jellyfin.Plugin.SubScribe.Registry;

internal static class DupSkip
{
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "dupskip");
        // T178 (operator finding 15.11.2026, second occurrence): this harness DELETES its working
        // directory. Handed a repository root by mistake — e.g. by a script that passes the checkout to
        // every suite — it removes the sources. A directory carrying a repo marker is not a workdir, and
        // refusing is the only safe direction.
        if (File.Exists(Path.Combine(root, "build.yaml"))
            || Directory.Exists(Path.Combine(root, ".git"))
            || File.Exists(Path.Combine(root, ".gitignore")))
        {
            Console.WriteLine($">>> FEHLER: '{root}' ist ein Repository, kein Arbeitsverzeichnis — dieser Lauf loescht dort alle Dateien.");
            return 3;
        }

        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        using var db = new ScribeDbContext(Path.Combine(root, "db"), null);
        var reg = new ContentHashRegistry(db, null, null);

        const string oldHash = "e621029b4734e366";
        const string newHash = "6c0dd8e8143a2e52";
        const string lang = "EN";
        string text = "\uFEFF1\r\n00:00:01,000 --> 00:00:03,000\r\nHello there.\r\n\r\n";
        string contentHash = ContentHashRegistry.ComputeHash(text);

        // ---- Zustand vor dem Tag-Schreiben: die Sprache ist oben und als uploaded vermerkt.
        reg.EnsureFile(oldHash, "tt1234567", "/media/films/Lanterns/S01/Lanterns.S01E08.mkv");
        reg.MarkEmbed(oldHash, 0, lang, false, SubtitleStatus.Uploaded, contentHash: contentHash);
        Console.WriteLine("Schritt 1  alt=" + oldHash + " uploaded-Zeilen=" + UploadedRows(db, contentHash));

        int moved = reg.ReplaceMediaIdentity(oldHash, newHash);
        Console.WriteLine("Schritt 2  identity moved, " + moved + " Zeile(n) umgetragen -> " + newHash);
        Console.WriteLine("           uploaded-Zeilen unter neuer Identitaet=" + UploadedRows(db, contentHash));

        // ---- Schritt 3a: der naechste Lauf sieht denselben Inhalt und ueberspringt ihn.
        // IsContentKnown() ist hier true — eine rejected-Zeile zaehlte genauso, deshalb feuert der Skip
        // auch dann, wenn der Inhalt laengst oben ist. Genau das war die Falle.
        Console.WriteLine();
        Console.WriteLine("Schritt 3  Inhalt bekannt (IsContentKnown) = " + reg.IsContentKnown(contentHash));
        bool settled = reg.RecordSkippedContent(
            newHash, false, 0, lang, false, false, contentHash, RejectReason.DuplicateContent,
            new[] { SubtitleProvider.Subdl });

        string statusAfter = reg.GetEmbed(newHash, 0)?.Status ?? "<keine Zeile>";
        Console.WriteLine("           RecordSkippedContent -> settled=" + settled + ", Status danach=" + statusAfter);
        Console.WriteLine("           uploaded-Zeilen=" + UploadedRows(db, contentHash));

        bool caseA = settled
            && statusAfter == SubtitleStatus.Uploaded
            && UploadedRows(db, contentHash) == 1;

        // ---- Fall B: Inhalt ist NICHT oben. Nur eine rejected-Zeile existiert (fremde Ablehnung,
        // z.B. duplicate-remote). Hier muss die Ablehnung stehen bleiben — sonst behauptet der Fix
        // einen Upload, den es nie gab.
        string otherText = "1\n00:00:05,000 --> 00:00:07,000\nSome other line.\n\n";
        string otherHash = ContentHashRegistry.ComputeHash(otherText);
        reg.MarkEmbed(newHash, 3, "DE", false, SubtitleStatus.Rejected,
            contentHash: otherHash, reason: RejectReason.DuplicateRemote);

        bool otherSettled = reg.RecordSkippedContent(
            newHash, false, 4, "DE", false, false, otherHash, RejectReason.DuplicateContent,
            new[] { SubtitleProvider.Subdl });

        string otherStatus = reg.GetEmbed(newHash, 4)?.Status ?? "<keine Zeile>";
        string otherReason = reg.GetEmbed(newHash, 4)?.Reason ?? "<kein Grund>";
        Console.WriteLine();
        Console.WriteLine("Fall B     Inhalt NICHT oben: settled=" + otherSettled + ", Status=" + otherStatus + ", Grund=" + otherReason);
        bool caseB = !otherSettled && otherStatus == SubtitleStatus.Rejected
            && otherReason == RejectReason.DuplicateContent;

        // ---- F-M728 / T391: das gemerkte NEIN einer Quelle schliesst die SPUR dieser Quelle.
        // Gemessen am 24.11.2026 an "Resident.Evil.2026": mst lehnt mit "film-source-id-not-usa" ab,
        // die Spur blieb offen, und die Datei wurde um 06:30, 08:12, 09:04 und 09:52 je erneut
        // entpackt und in einem ffmpeg-Pass ausgelesen — 37 Spuren, 112 DUE-Zeilen, 0 Uploads.
        SourceRefusalStore.Attach(db.Context);
        const string refusedText = "1\r\n00:00:10,000 --> 00:00:12,000\r\nRefused line.\r\n\r\n";
        string refusedHash = ContentHashRegistry.ComputeHash(refusedText);
        reg.MarkEmbed(newHash, 7, "EN", false, SubtitleStatus.Observed, contentHash: refusedHash);
        SourceRefusalStore.Put(newHash, 7, "EN non-HI", SubtitleProvider.MovieSubtitles.ToString(),
            refusedHash, "film-source-id-not-usable");

        bool closedForMst = reg.IsEmbedTerminalFor(newHash, 7, SubtitleProvider.MovieSubtitles);
        bool stillOpenForSubdl = !reg.IsEmbedTerminalFor(newHash, 7, SubtitleProvider.Subdl);

        // Ein NEUER Text auf derselben Spur traegt eine andere Signatur -> die Quelle wird wieder gefragt.
        string changedText = "1\r\n00:00:10,000 --> 00:00:12,000\r\nCorrected line.\r\n\r\n";
        string changedHash = ContentHashRegistry.ComputeHash(changedText);
        reg.MarkEmbed(newHash, 8, "EN", false, SubtitleStatus.Observed, contentHash: changedHash);
        SourceRefusalStore.Put(newHash, 8, "EN non-HI", SubtitleProvider.MovieSubtitles.ToString(),
            refusedHash, "film-source-id-not-usable");
        bool asksAgainOnNewText = !reg.IsEmbedTerminalFor(newHash, 8, SubtitleProvider.MovieSubtitles);

        // Der Seitenwagen (Position -1) haengt an derselben Regel — sonst bleibt eine Datei offen,
        // weil mst sie nie bekommt ("Sister.Act.2 ... still open: moviesubtitles").
        // Eigener Text, der im Store NICHT bekannt ist — sonst erledigt die Regel "Inhalt bekannt"
        // (F-M500) den Seitenwagen fuer jede Quelle, und der Test pruefte nicht mehr das Nein.
        string looseText = "1\r\n00:00:20,000 --> 00:00:22,000\r\nSidecar line.\r\n\r\n";
        string looseRefusedHash = ContentHashRegistry.ComputeHash(looseText);
        string loosePath = Path.Combine(root, "Loose.en.srt");
        File.WriteAllText(loosePath, looseText);
        var loose = new System.Collections.Generic.List<(string Path, string Lang, bool HearingImpaired, bool Forced)>
        {
            (loosePath, "EN", false, false),
        };
        // Eigenes Medium: auf `newHash` traegt Fall A bereits die EN-Marke fuer SubDL, und die
        // Paar-Frage wuerde den Seitenwagen fuer SubDL erledigen — der Test prueft dann nicht mehr das Nein.
        const string looseMedia = "b41d0c7a55e21f83";
        reg.EnsureFile(looseMedia, "tt1234567", "/media/films/Loose/Loose.mkv");
        SourceRefusalStore.Put(looseMedia, -1, "EN non-HI", SubtitleProvider.MovieSubtitles.ToString(),
            looseRefusedHash, "film-source-title-closed");
        bool looseClosedForMst = !Jellyfin.Plugin.SubScribe.Pipeline.UploadPipeline.SidecarsNeedWorkFor(
            loose, looseMedia, SubtitleProvider.MovieSubtitles, reg);
        bool looseOpenForSubdl = Jellyfin.Plugin.SubScribe.Pipeline.UploadPipeline.SidecarsNeedWorkFor(
            loose, looseMedia, SubtitleProvider.Subdl, reg);

        // ---- F-M804 / T391: der Reup-Riegel. Ein Seitenwagen, dessen INHALT dieses Plugin geholt hat,
        // ist an KEINE Quelle zurueckzugeben. Das behaltene Original (Slot 51–99) faellt schon beim
        // Einreihen durch seinen Namen heraus (F-M315); sein korrigierter Zwilling traegt andere Bytes
        // und braucht darum die Frage auf dem Inhalt. Geprueft wird der Inhalt, nicht die Quelle —
        // eine Sperre, die nur die eigene Quelle nennt, laesst die Kreuzkontamination durch (F-M489).
        string dlText = "1\r\n00:00:30,000 --> 00:00:32,000\r\nDownloaded line.\r\n\r\n";
        string dlHash = ContentHashRegistry.ComputeHash(dlText);
        const string dlMedia = "c9f2a1b7d0e45836";
        reg.EnsureFile(dlMedia, "tt1234567", "/media/films/Downloaded/Downloaded.mkv");
        reg.MarkDownloaded(dlHash, dlMedia, "EN", fileName: "Downloaded.en.srt",
            path: "/media/films/Downloaded/Downloaded.en.srt",
            origin: "subdl", originId: "12345");
        bool dlLockedAnySource = reg.IsDownloadedFromAnySource(dlHash);

        // Der korrigierte Zwilling: eigene Bytes, eigene Zeile, ebenfalls als Fund vermerkt (F-M493).
        string corrText = "1\r\n00:00:30,500 --> 00:00:32,500\r\nDownloaded line.\r\n\r\n";
        string corrHash = ContentHashRegistry.ComputeHash(corrText);
        reg.MarkDownloaded(corrHash, dlMedia, "EN", fileName: "Downloaded.en.01.srt",
            path: "/media/films/Downloaded/Downloaded.en.01.srt",
            origin: "subdl", originId: "12345");
        bool correctedLockedToo = corrHash != dlHash && reg.IsDownloadedFromAnySource(corrHash);

        // Die Gegenprobe: ein Inhalt, den WIR hochgeladen haben, darf der Riegel nicht treffen.
        bool uploadedNotLocked = !reg.IsDownloadedFromAnySource(contentHash);

        // Und die 5xer sind ueber ihren NAMEN ausgenommen, nicht ueber den Inhalt.
        bool reservedByName = Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsOriginalSlot(51)
            && Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsOriginalSlot(99)
            && !Jellyfin.Plugin.SubScribe.Registry.SidecarNaming.IsOriginalSlot(1);

        int checks = 0;
        void Case(bool ok, string what)
        {
            if (ok) checks++;
            Console.WriteLine(ok ? "  OK   " + what : "  FAIL " + what);
        }

        Console.WriteLine();
        Case(closedForMst, "T391: mst-Nein schliesst die Spur fuer mst");
        Case(stillOpenForSubdl, "T391: dieselbe Spur bleibt fuer SubDL offen");
        Case(asksAgainOnNewText, "T391: neuer Text auf derselben Spur wird wieder gefragt");
        Case(looseClosedForMst, "T391: Seitenwagen (Position -1) schliesst fuer mst");
        Case(looseOpenForSubdl, "T391: Seitenwagen bleibt fuer SubDL offen");
        Case(dlLockedAnySource, "F-M804: ein Fund ist an jede Quelle gesperrt (Inhalt, nicht Quelle)");
        Case(correctedLockedToo, "F-M804: der korrigierte 0x aus dem 5x ist ebenso gesperrt");
        Case(uploadedNotLocked, "F-M804: Gegenprobe — selbst Hochgeladenes bleibt frei");
        Case(reservedByName, "F-M804: die 5xer sind ueber den Namen ausgenommen (51-99, nicht 1)");

        Console.WriteLine();
        Console.WriteLine("Fall A (Inhalt oben -> uploaded bleibt): " + (caseA ? "OK" : "FEHLER"));
        Console.WriteLine("Fall B (Inhalt nicht oben -> rejected):  " + (caseB ? "OK" : "FEHLER"));
        Console.WriteLine();
        bool all = caseA && caseB && checks == 9;
        Console.WriteLine(all
            ? ">>> OK: ein duplicate-content-Skip loescht keine uploaded-Marke, erfindet aber auch keine;"
              + " das Nein einer Quelle schliesst die Spur dieser Quelle; kein Fund geht zurueck (F-M298, F-M728, F-M804)"
            : ">>> FEHLER: F-M298/F-M728/F-M804 nicht erfuellt");
        Console.WriteLine("dupskip: " + (checks + 2) + " checks");
        return all ? 0 : 1;
    }

    /// <summary>Zaehlt die uploaded-Zeilen zu diesem Inhalt im ganzen Bestand.</summary>
    private static int UploadedRows(ScribeDbContext db, string contentHash)
    {
        int n = db.Tracks.FindAll().Count(x => x.ContentHash == contentHash && x.Status == SubtitleStatus.Uploaded);
        return n + db.Sidecars.FindAll().Count(x => x.ContentHash == contentHash && x.Status == SubtitleStatus.Uploaded);
    }
}
