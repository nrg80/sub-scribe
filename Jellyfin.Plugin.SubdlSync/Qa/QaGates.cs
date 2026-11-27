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
using System.Collections.Frozen;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Jellyfin.Plugin.SubScribe.Language;

namespace Jellyfin.Plugin.SubScribe.Qa;

/// <summary>
/// QA gates for the upload pipeline (B2, F-M13–M16). Pure static logic — no I/O,
/// easily unit-testable. NOTE: the release-aware duplicate match (F-M17a) is no
/// longer called from the pipeline (F-M184 removed the pre-check); ReleaseMatches
/// is retained for the QA unit tests.
/// </summary>
public static class QaGates
{
    /// <summary>
    /// Parsed SRT cue statistics — the shared basis for F-M13 (validity),
    /// F-M14 (sync plausibility) and F-M16 (minimum size/cue count).
    /// </summary>
    public sealed class SrtStats
    {
        /// <summary>Number of cues found.</summary>
        public int CueCount;

        /// <summary>First cue start in ms (-1 when no cues).</summary>
        public long FirstStartMs = -1;

        /// <summary>Last cue end in ms (-1 when no cues).</summary>
        public long LastEndMs = -1;

        /// <summary>True when cue starts are non-decreasing (overlapping cues are legal SRT).</summary>
        public bool Monotonic = true;

        /// <summary>Shortest cue duration in ms (long.MaxValue when no cues).</summary>
        public long MinCueMs = long.MaxValue;

        /// <summary>Longest cue duration in ms.</summary>
        public long MaxCueMs;
    }

    private static readonly Regex CueRegex = new(@"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})", RegexOptions.Compiled);

    /// <summary>
    /// F-M13 basis: parses all cues and computes statistics. Returns null only when
    /// the content contains no cue timing at all.
    /// </summary>
    /// <param name="content">SRT text.</param>
    /// <returns>Cue statistics, or null when no timing line matched.</returns>
    public static SrtStats? ParseSrt(string content)
    {
        var stats = new SrtStats();
        long lastStart = -1;
        bool any = false;
        foreach (Match m in CueRegex.Matches(content))
        {
            any = true;
            long start = ToMs(m, 1);
            long end = ToMs(m, 5);
            stats.CueCount++;
            if (stats.FirstStartMs < 0)
            {
                stats.FirstStartMs = start;
            }

            if (lastStart >= 0 && start < lastStart)
            {
                stats.Monotonic = false; // F-M13: timestamps must grow monotonically
            }

            lastStart = start;
            long dur = end - start;
            stats.MinCueMs = Math.Min(stats.MinCueMs, dur);
            stats.MaxCueMs = Math.Max(stats.MaxCueMs, dur);
            stats.LastEndMs = Math.Max(stats.LastEndMs, end);
        }

        return any ? stats : null;
    }

    private static long ToMs(Match m, int offset) =>
        (long.Parse(m.Groups[offset].Value, System.Globalization.CultureInfo.InvariantCulture) * 3600
         + long.Parse(m.Groups[offset + 1].Value, System.Globalization.CultureInfo.InvariantCulture) * 60
         + long.Parse(m.Groups[offset + 2].Value, System.Globalization.CultureInfo.InvariantCulture)) * 1000
        + long.Parse(m.Groups[offset + 3].Value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// (user decision 10.09.2026): language detection via the LanguageDetection
    /// NuGet (Cybozu n-gram port, Apache-2.0, offline) — replaces the hand-rolled
    /// Unicode-block + Latin stopword scan that produced 18/19 false positives on
    /// real-world tracks (TR→NL, DA→DE, FI→NL; verified against the detector's
    /// langdetect on the same corpus: langdetect 18/19 correct vs. stopwords 18/19 wrong).
    /// Deterministic: fixed seed, mirroring langdetect's DetectorFactory.seed = 0.
    /// Returns null when no confident verdict is possible — gate stays silent.
    /// </summary>
    /// <param name="content">Subtitle text.</param>
    /// <returns>The house form of the detected language (ISO 639-2/T, e.g. "DEU", "ENG"), or null
    /// when undetectable. F-M802: the library speaks ISO 639-1 and nothing else, so its answer is
    /// translated at this seam before any consumer compares it.</returns>
    public static string? DetectLanguage(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var detector = new global::LanguageDetection.LanguageDetector();
            detector.RandomSeed = 0; // deterministic — same text always same verdict
            detector.AddAllLanguages();
            string detected = detector.Detect(content);
            if (string.IsNullOrEmpty(detected))
            {
                return null;
            }

            string upper = detected.ToUpperInvariant();

            // Normalize the few multi-variant codes the stream tags use as a single family:
            // pt-pt/pt-br both compare against PT-tagged streams; zh-cn/zh-tw → ZH.
            if (upper.StartsWith("PT", StringComparison.Ordinal)) { upper = "PT"; }
            else if (upper.StartsWith("ZH", StringComparison.Ordinal)) { upper = "ZH"; }

            // F-M802: the library answers in ISO 639-1 ("DE", "SV"); the house speaks ISO 639-2/T
            // ("DEU", "SWE"). Translating HERE is what keeps every comparison downstream true — the
            // pack join, the single upload's file name, the download gate, untagged tracks and loose
            // sidecars all read one form. An unknown code comes back upper-cased, never invented.
            return LanguageMapper.Canonical(upper);
        }
        catch
        {
            return null; // no confident verdict — gate stays silent (fail-open)
        }
    }

    /// <summary>
    /// (user decision 10.09.2026): family exceptions for the F-M15 language gate.
    /// Some stream-tag languages are either (a) so close to a relative that the CLD2
    /// detector legitimately lands on the relative, or (b) not supported by CLD2 at
    /// all, so the content is FORCED to be attributed to some neighbouring language —
    /// in both cases a "mismatch" verdict would reject perfectly fine subtitles.
    /// A reject now requires the detected language to lie OUTSIDE the family.
    /// Live cases that motivated this (registry 10.09.2026): MS->ID (Bahasa pair,
    /// 17×), CA->ES / GL->ES (Iberian neighbours), EU->ID (CLD2 has no Basque).
    /// Deliberately NOT included: main languages (DE/EN/NL/FR/IT/ES/PT/RU/PL...) —
    /// real mismatches there stay rejects.
    /// </summary>
    private static readonly FrozenDictionary<string, string[]> LangFamilyMap = new Dictionary<string, string[]>
    {
        // F-M802: keys and values speak ISO 639-2/T like every other table in the house. Before
        // 12.3.8.92 they were two-letter codes while the tag was three — the exception never fired,
        // and every tagged track looked like a mismatch.
        // The detector cannot emit these at all — accept the neighbouring attribution:
        ["MSA"] = new[] { "IND" },                          // Malay ≈ Indonesian (Bahasa family)
        ["EUS"] = new[] { "SPA", "POR", "FRA", "IND", "ITA" }, // Basque unsupported by the detector
        ["GLG"] = new[] { "SPA", "POR" },                   // Galician unsupported by the detector
        // Genuine close relatives the detector mixes up:
        ["CAT"] = new[] { "SPA", "POR", "ITA", "FRA" },     // Catalan vs Iberian neighbours
        ["DAN"] = new[] { "NOR", "SWE" },                   // Scandinavian congruence
        ["NOR"] = new[] { "DAN", "SWE" },
        ["SWE"] = new[] { "DAN", "NOR" },
        ["HRV"] = new[] { "SRP", "BOS", "SLV" },            // Serbo-Croatian cluster
        ["SRP"] = new[] { "HRV", "BOS", "SLV" },
        ["BOS"] = new[] { "HRV", "SRP", "SLV" },
        ["SLV"] = new[] { "HRV", "SRP", "BOS" },
        ["CES"] = new[] { "SLK" },                          // Czech vs Slovak
        ["SLK"] = new[] { "CES" },
        ["IND"] = new[] { "MSA" },
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// F-M375: the language a subtitle must be uploaded under when the stream tag does not match the text.
    /// Returns the detected language when the content clearly speaks another language than
    /// the tag claims, and null when the tag holds (or when there is no confident verdict —
    /// the gate stays fail-open, as it always did).
    /// </summary>
    /// <param name="tag">The language tag the container gave the stream.</param>
    /// <param name="content">The subtitle text.</param>
    /// <returns>Replacement language, or null when the tag stands.</returns>
    public static string? CorrectedLanguage(string tag, string content)
    {
        string? detected = DetectLanguage(content);
        if (detected == null || IsFamilyMatch(tag, detected))
        {
            return null;
        }

        return detected;
    }

    /// <summary>True when <paramref name="detected"/> is an accepted relative
    /// of the tagged <paramref name="lang"/> (or identical).</summary>
    public static bool IsFamilyMatch(string lang, string detected)
    {
        // F-M802: both sides are translated before they are compared. A caller may still hand in a
        // two-letter tag or a library code; without this the comparison is false and silent.
        string? links = LanguageMapper.Canonical(lang);
        string? rechts = LanguageMapper.Canonical(detected);
        if (links == null || rechts == null)
        {
            return false;
        }

        if (string.Equals(links, rechts, StringComparison.Ordinal))
        {
            return true;
        }

        return LangFamilyMap.TryGetValue(links, out var fam)
            && fam.Contains(rechts, StringComparer.Ordinal);
    }

    /// <summary>
    /// F-M17a: does an existing SubDL candidate match our own release? Same group tag
    /// plus sufficient token overlap (same fuzzy logic as the download score, F-M44).
    /// A different release (different cut, group or tokens) is NOT a duplicate.
    /// </summary>
    /// <param name="candidateRelease">Release name of the existing SubDL sub.</param>
    /// <param name="ownRelease">Release name of our media file.</param>
    /// <returns>True when both describe the same release.</returns>
    public static bool ReleaseMatches(string candidateRelease, string ownRelease)
    {
        if (string.IsNullOrEmpty(candidateRelease) || string.IsNullOrEmpty(ownRelease))
        {
            return false;
        }

        var candTokens = Tokenize(candidateRelease);
        var ownTokens = Tokenize(ownRelease);
        if (ownTokens.Count == 0)
        {
            return false;
        }

        int overlap = 0;
        foreach (var t in ownTokens)
        {
            if (candTokens.Contains(t))
            {
                overlap++;
            }
        }

        double overlapPct = (double)overlap / ownTokens.Count;

        string? candGroup = candidateRelease.Contains('-', StringComparison.Ordinal) ? candidateRelease.Split('-')[^1].Trim() : null;
        string? ownGroup = ownRelease.Contains('-', StringComparison.Ordinal) ? ownRelease.Split('-')[^1].Trim() : null;
        if (candGroup != null && ownGroup != null)
        {
            // same group tag AND most tokens shared → same release
            return string.Equals(candGroup, ownGroup, StringComparison.OrdinalIgnoreCase) && overlapPct >= 0.6;
        }

        // one side has no group tag — require near-identical names instead
        return overlapPct >= 0.9;
    }

    /// <summary>Splits a release name into comparison tokens (F-M44 logic).</summary>
    public static HashSet<string> Tokenize(string s) =>
        new(s.Split(new[] { '.', '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
}