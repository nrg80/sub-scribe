// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version.
// Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the implied
// warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// T385 / F-M724 (24.11.2026): ONE fold for every name comparison.
//
// THE CASE: a release can dress its episode marker — "Ｓ０２Ｅ０７" (fullwidth), "ꜱ01ᴇ05" (small
// capitals), "𝐒𝟎𝐄𝟏𝟎" (mathematical bold). Two readers answered differently: the archive path had
// its own fold table, and the media parser used Normalize(FormKD), which needs ICU and silently does
// NOTHING where the runtime is invariant. Worse, the member comparison compared an archive entry
// byte for byte with a name from the candidate list, so a styled entry was reported as "no bytes".
//
// THE RULES asserted here:
//   1. one fold, one place (Text.TextFold, ueber den Paketen) — arithmetic, so it answers the same on every host;
//   2. every comparison folds BOTH sides, and nothing is stored folded;
//   3. an ordinary name passes through byte-identical (same instance, no copy);
//   4. what the fold cannot read, it must not pretend to read (modifier letters stay, and letters
//      alone never become an episode number).
//
// Usage: dotnet build Jellyfin.Plugin.SubdlSync -c Release
//        dotnet run --project scripts/tests/textfold/textfold.csproj -c Release -- <repo-root>

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.SubScribe.Api;
using Jellyfin.Plugin.SubScribe.Pipeline;
using Jellyfin.Plugin.SubScribe.Text;

namespace TextFoldRun;

internal static class Program
{
    private static int _checks;
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what, string actual = "")
    {
        _checks++;
        if (!ok)
        {
            Failures.Add(what);
            Console.WriteLine("  FAIL  " + what + (actual.Length > 0 ? ": " + actual : string.Empty));
        }
    }

    private static SubtitleUnpackFile Member(string name, int season, int episode, bool hi)
        => new()
        {
            Name = name,
            Url = "/subtitle/Xhfe8mD1w5/" + name.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Season = season,
            Episode = episode,
            HearingImpaired = hi
        };

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return buffer.ToArray();
    }

    private static int Main(string[] args)
    {
        string repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        while (!Directory.Exists(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync"))
               && Path.GetDirectoryName(repo) is { Length: > 3 } up)
        {
            repo = up;
        }

        // -------------------------------------------------------------------------------------
        // 1. The fold itself — one rule, every family in the table.
        // -------------------------------------------------------------------------------------
        Console.WriteLine("F-M724 — the fold reads the styling, not the name");

        Check(TextFold.Fold("[𝐂𝐨𝐟𝐟𝐞𝐞𝐏𝐫𝐢𝐬𝐨𝐧] A Bona Fide Killer 𝐒𝟎𝐄𝟏𝟎 VK WEB EN.srt")
              == "[CoffeePrison] A Bona Fide Killer S0E10 VK WEB EN.srt",
            "F-M457 stays: mathematical bold letters and bold digits fold to ASCII",
            TextFold.Fold("[𝐂𝐨𝐟𝐟𝐞𝐞𝐏𝐫𝐢𝐬𝐨𝐧] A Bona Fide Killer 𝐒𝟎𝐄𝟏𝟎 VK WEB EN.srt"));

        Check(TextFold.Fold("Show ꜱ01ᴇ05 ᴡᴇʙ") == "Show S01E05 WEB",
            "F-M724: small capitals fold — \"ꜱ01ᴇ05\" is S01E05",
            TextFold.Fold("Show ꜱ01ᴇ05 ᴡᴇʙ"));

        Check(TextFold.Fold("Ｍｙ Ｓｈｏｗ Ｓ０２Ｅ０７.ｓｒｔ") == "My Show S02E07.srt",
            "F-M724: fullwidth forms fold, including the fullwidth dot and the extension",
            TextFold.Fold("Ｍｙ Ｓｈｏｗ Ｓ０２Ｅ０７.ｓｒｔ"));

        Check(TextFold.Fold("Ⓢ01Ⓔ05 ⒶⒷ") == "S01E05 AB",
            "F-M724: circled letters fold",
            TextFold.Fold("Ⓢ01Ⓔ05 ⒶⒷ"));

        Check(TextFold.Fold("🄰🄱🅂") == "ABS",
            "F-M724: squared letters fold (the astral block behind U+1F130)",
            TextFold.Fold("🄰🄱🅂"));

        Check(TextFold.Fold("S⁰¹ᴇ³") == "S01E3" && TextFold.Fold("S₀₁E₉") == "S01E9",
            "F-M724: superscript and subscript DIGITS fold",
            TextFold.Fold("S⁰¹ᴇ³") + " / " + TextFold.Fold("S₀₁E₉"));

        // The deliberate gap: modifier letters stand for phonetic notation as well, so reading them
        // as letters would turn a pronunciation hint into an episode marker. They stay untouched,
        // and the gate must not claim otherwise.
        Check(TextFold.Fold("ᵃᵇᶜ") == "ᵃᵇᶜ" && !TextFold.NeedsFold("ᵃᵇᶜ"),
            "F-M724: modifier letters are NOT folded and not even claimed as foldable");

        Check(TextFold.NeedsFold("Ｓ０２Ｅ０７") && TextFold.NeedsFold("ꜱ01ᴇ05") && !TextFold.NeedsFold("A Bona Fide Killer S01E10"),
            "F-M724: NeedsFold says exactly what Fold would change");

        string plain = "The.Middle.S09E23.1080p.WEB-DL.H265-FTMVHD.mkv";
        string folded = TextFold.Fold(plain);
        Check(ReferenceEquals(folded, plain) && folded == plain,
            "F-M724: an ordinary name comes back as the SAME instance — byte-identical, no copy",
            ReferenceEquals(folded, plain) ? "same instance" : "copy: " + folded);

        // -------------------------------------------------------------------------------------
        // 2. The member search of a candidate (the real reader, not a copy).
        // -------------------------------------------------------------------------------------
        Console.WriteLine("\nF-M724 — the pack search finds a styled member");

        var smallcaps = new SubtitleCandidate { ReleaseName = "Small caps", Url = "x.zip", Language = "EN" };
        smallcaps.UnpackFiles.Add(Member("Show ꜱ01ᴇ05 ᴡᴇʙ EN.srt", 0, 0, hi: false));
        smallcaps.UnpackFiles.Add(Member("Show ꜱ01ᴇ06 ᴡᴇʙ EN.srt", 0, 0, hi: false));
        Check(smallcaps.FindUnpackFile(1, 5)?.Name.StartsWith("Show ꜱ01ᴇ05", StringComparison.Ordinal) == true,
            "F-M724: \"ꜱ01ᴇ05\" inside a pack is E5 — and E6 is not handed out for it",
            smallcaps.FindUnpackFile(1, 5)?.Name ?? "null");
        Check(smallcaps.FindUnpackFile(1, 7) == null,
            "F-M724: an episode the pack does not carry stays absent");

        var fullwidth = new SubtitleCandidate { ReleaseName = "Fullwidth", Url = "x.zip", Language = "EN" };
        fullwidth.UnpackFiles.Add(Member("Ｍｙ Ｓｈｏｗ Ｓ０２Ｅ０７.ｓｒｔ", 0, 0, hi: false));
        Check(fullwidth.FindUnpackFile(2, 7) != null,
            "F-M457 regression: fullwidth forms still resolve (Ｓ０２Ｅ０７)");

        // Documented refusal: folding letters does not invent numbers. A name made only of circled
        // letters carries no episode, and the search must say so instead of guessing.
        var circledOnly = new SubtitleCandidate { ReleaseName = "Circled", Url = "x.zip", Language = "EN" };
        circledOnly.UnpackFiles.Add(Member("ⒶⒷ ⒸⒹ.srt", 0, 0, hi: false));
        Check(circledOnly.FindUnpackFile(1, 5) == null,
            "F-M724: \"ⒶⒷ\" folds to letters but names no episode — refused, not guessed");

        // -------------------------------------------------------------------------------------
        // 3. The archive unpacker: both sides of the comparison fold.
        // -------------------------------------------------------------------------------------
        Console.WriteLine("\nF-M724 — the unpacker compares folded names on both sides");

        var unpack = typeof(ScribeApiClient).GetMethod("UnpackAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Check(unpack != null, "the archive unpacker is reachable");
        if (unpack != null)
        {
            byte[] archive = Zip(
                ("[𝐂𝐨𝐟𝐟𝐞𝐞𝐏𝐫𝐢𝐬𝐨𝐧] A Bona Fide Killer 𝐒𝟎𝐄𝟏𝟎 VK WEB EN.srt", "1\n00:00:01,000 --> 00:00:02,000\nstyled\n"));

            object? Result(byte[] bytes, int season, int episode, string? member)
            {
                var task = (System.Threading.Tasks.Task)unpack.Invoke(
                    null, new object?[] { bytes, season, episode, CancellationToken.None, member })!;
                task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result")!.GetValue(task);
            }

            static byte[]? Content(object? tuple)
                => (byte[]?)tuple!.GetType().GetField("Item1")!.GetValue(tuple);

            // The candidate list gives the PLAIN name, the archive carries the STYLED one — the exact
            // shape that used to end as "no bytes (no request made)".
            var byStyled = Content(Result(archive, 1, 10, "[CoffeePrison] A Bona Fide Killer S0E10 VK WEB EN.srt"));
            Check(byStyled != null && Encoding.UTF8.GetString(byStyled).Contains("styled", StringComparison.Ordinal),
                "F-M724: a styled archive entry is found by its plain name — no \"archive_without_member\"",
                byStyled is null ? "null" : "bytes");

            var unknown = Content(Result(archive, 1, 10, "Some Other File.srt"));
            Check(unknown is null,
                "F-M724: a name that is not in the archive is still a null, never a guess at another file");

            var plainName = Content(Result(archive, 1, 10, "[𝐂𝐨𝐟𝐟𝐞𝐞𝐏𝐫𝐢𝐬𝐨𝐧] A Bona Fide Killer 𝐒𝟎𝐄𝟏𝟎 VK WEB EN.srt"));
            Check(plainName != null,
                "F-M724: and the styled name the list reports still matches the styled entry");
        }

        // -------------------------------------------------------------------------------------
        // 4. tvsubtitles: pack members and the episode list of a zip.
        // -------------------------------------------------------------------------------------
        Console.WriteLine("\nF-M724 — the tvsubtitles pack readers fold too");

        byte[] tvstZip = Zip(("ＭｏｂＬａｎｄ - ２ｘ０５ - Ｂｉｒｄｌａｎｄ.ｅｎ.ｓｒｔ", "1\n00:00:01,000 --> 00:00:02,000\nx\n"));
        var members = TvSubtitlesClient.ReadSeasonArchive(tvstZip);
        Check(members.Count == 1 && members[0].Season == 2 && members[0].Episode == 5 && members[0].Language == "en",
            "F-M724: a fullwidth tvst member is read as 2x05 en instead of being dropped",
            members.Count == 0 ? "no member" : members[0].Season + "x" + members[0].Episode + " " + members[0].Language);
        Check(members.Count == 1 && members[0].Name.StartsWith("ＭｏｂＬａｎｄ", StringComparison.Ordinal),
            "F-M724: the member keeps the name the archive gave it — nothing is stored folded",
            members.Count == 0 ? "no member" : members[0].Name);

        // The upload side reads the SxxExx form, so its fixture names the episode that way.
        byte[] tvstPack = Zip(("ＭｏｂＬａｎｄ - Ｓ０２Ｅ０５ - Ｂｉｒｄｌａｎｄ.ｅｎ.ｓｒｔ", "1\n00:00:01,000 --> 00:00:02,000\nx\n"));
        var episodes = TvSubtitlesClient.EpisodesInZip(tvstPack);
        Check(episodes.Count == 1 && episodes[0] == 5,
            "F-M724: EpisodesInZip names episode 5 for a fullwidth SxxExx marker",
            string.Join(",", episodes));

        // -------------------------------------------------------------------------------------
        // 5. The media parser reads the same fold — without ICU, which the old call needed.
        // -------------------------------------------------------------------------------------
        Console.WriteLine("\nF-M724 — the media parser folds without ICU");

        var parser = typeof(TextFold).Assembly.GetType("Jellyfin.Plugin.SubScribe.Pipeline.MediaNameParser");
        var parse = parser?.GetMethod("Parse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(parse != null, "the media parser is reachable");
        if (parse != null)
        {
            string dressed = "[\U0001D41C\U0001D405\U0001D429\U0001D42D] \U0001D403\U0001D400\U0001D405\U0001D413 \U0001D412\U0001D413\U0001D400\U0001D413\U0001D404 (\U0001D7DA\U0001D7D8\U0001D7DA\U0001D7DC) \U0001D40F\U0001D411\U0001D408\U0001D40C\U0001D404 - \U0001D413\U0001D41B\U0001D415";

            object? Parsed(string name) => parse.Invoke(null, new object[] { name });
            string? Field(string name, string property)
            {
                var parsed = Parsed(name)!;
                return parsed.GetType().GetProperty(property)!.GetValue(parsed)?.ToString();
            }

            // This harness runs with InvariantGlobalization — the old Normalize(FormKD) was a no-op
            // here, so this check is only meaningful since the fold became arithmetic.
            Check(Field(dressed, "Title") == "DAFT STATE",
                "F-M724: the dressed title is read on a runtime WITHOUT ICU (this harness is invariant)",
                Field(dressed, "Title") ?? "null");
            Check(Field(dressed, "Year") == "2024",
                "F-M724: and the bold year becomes a year",
                Field(dressed, "Year") ?? "null");
            Check(Field("Show ꜱ01ᴇ05 ᴡᴇʙ 1080p", "Title") == "Show" && Field("Show ꜱ01ᴇ05 ᴡᴇʙ 1080p", "Season") == "1",
                "F-M724: small capitals in a media name give title and season",
                (Field("Show ꜱ01ᴇ05 ᴡᴇʙ 1080p", "Title") ?? "null") + " / " + (Field("Show ꜱ01ᴇ05 ᴡᴇʙ 1080p", "Season") ?? "null"));
        }

        // The ONE source assertion kept on purpose: two fold implementations existed, and the one
        // that needed ICU must not come back.
        string mediaParser = Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Pipeline", "MediaNameParser.cs");
        Check(File.Exists(mediaParser) && !File.ReadAllText(mediaParser).Contains("NormalizationForm", StringComparison.Ordinal),
            "F-M724: the media parser no longer calls Normalize( — there is one fold, not two");

        // ---- F-M736: die eine Eingangsnormierung ------------------------------------------------
        Check(TextFold.Normalize("  Ｍｙ   Ｓｈｏｗ  ") == "My Show",
            "F-M736: die Normierung faltet, trimmt und fasst inneren Weissraum zusammen",
            "[" + TextFold.Normalize("  Ｍｙ   Ｓｈｏｗ  ") + "]");

        Check(TextFold.Normalize(null) == string.Empty && TextFold.Normalize("   ") == string.Empty,
            "F-M736: leerer Eingang bleibt leer und wird nie null",
            "[" + TextFold.Normalize(null) + "]");

        string sauber = "Show.S01E05.1080p.WEB-DL";
        Check(ReferenceEquals(TextFold.Normalize(sauber), sauber),
            "F-M736: ist nichts zu tun, kommt DIESELBE Instanz zurueck",
            "instanzgleich=" + ReferenceEquals(TextFold.Normalize(sauber), sauber));

        Check(TextFold.Same("Show ꜱ01ᴇ05", "show s01e05")
              && TextFold.Same("Ｍｙ  Ｓｈｏｗ", "my show")
              && !TextFold.Same("Show S01E05", "Show S01E06")
              && !TextFold.Same(null, "Show"),
            "F-M736: Same normiert BEIDE Seiten — Schreibweise und Stil zaehlen nicht, der Inhalt schon",
            "styled=" + TextFold.Same("Show ꜱ01ᴇ05", "show s01e05")
            + " fullwidth=" + TextFold.Same("Ｍｙ  Ｓｈｏｗ", "my show"));

        Check(TextFold.Normalize(TextFold.Normalize("  Ｓ01Ｅ05  ")) == TextFold.Normalize("  Ｓ01Ｅ05  "),
            "F-M736: die Normierung ist idempotent (zweimal anwenden aendert nichts)",
            "[" + TextFold.Normalize(TextFold.Normalize("  Ｓ01Ｅ05  ")) + "]");

        Check(File.ReadAllText(mediaParser).Contains("TextFold.Normalize(", StringComparison.Ordinal)
              && File.ReadAllText(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Api", "TvSubtitlesClient.cs"))
                  .Contains("TextFold.Normalize(entry.Name)", StringComparison.Ordinal)
              && File.ReadAllText(Path.Combine(repo, "Jellyfin.Plugin.SubdlSync", "Api", "ScribeApiClient.cs"))
                  .Contains("TextFold.Same(", StringComparison.Ordinal),
            "F-M736: die Routinen rufen die eine Normierung — Parser, Archivleser und Mitgliedersuche",
            "Parser, tvst-Archiv und SubDL-Archiv nachgezogen");

        // The summary line carries the suite NAME: docs/counts binds the number from the requirements
        // document to exactly this line, so it must stay in the shape "<name>: <n> checks".
        Console.WriteLine("textfold: " + _checks + " checks, " + Failures.Count + " failed");
        Console.WriteLine(Failures.Count == 0 ? ">>> TEXT-FOLD OK" : ">>> TEXT-FOLD FAILED");
        return Failures.Count == 0 ? 0 : 1;
    }
}
