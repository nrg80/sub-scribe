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
using System.Text;

namespace Jellyfin.Plugin.SubScribe.Pipeline;

/// <summary>
/// F-M724 (24.11.2026): ONE fold for every name comparison — styled Unicode letters and digits
/// back to ASCII, so a name that dresses its episode marker ("Ｓ０２Ｅ０７", "ꜱ01ᴇ05",
/// "𝐒𝟎𝐄𝟏𝟎") is read the same as the plain one.
/// </summary>
/// <remarks>
/// Deliberately arithmetic instead of a Unicode normalisation call:
/// <see cref="string.Normalize(System.Text.NormalizationForm)"/> needs ICU and throws under
/// invariant globalization, which a Jellyfin host may well run. Measured 24.11.2026: the media
/// parser's <c>Normalize(FormKD)</c> silently did NOTHING in the test harness
/// (<c>InvariantGlobalization</c>), so "the name is folded" was true only on hosts that happened to
/// ship ICU — two different answers for the same name, depending on the host. Here folding is
/// arithmetic: it behaves the same everywhere.
/// <para>
/// Fold on BOTH sides of a comparison and never store the result — the fold is a reading aid, not
/// a correction of anybody's file name. <see cref="Fold"/> returns the argument itself (same
/// instance) when there is nothing to fold, so ordinary names pass through byte-identical.
/// </para>
/// <para>
/// Covered: the mathematical alphanumerics U+1D400–U+1D7FF (13 letter styles plus the digit runs),
/// the italic small h U+210E, the fullwidth forms U+FF01–U+FF5E, circled letters U+24B6–U+24E9,
/// squared letters U+1F130–U+1F149, superscript/subscript DIGITS only, and the small capitals
/// (a scattered set, mapped slot by slot). NOT covered on purpose: the modifier letters
/// U+02B0–U+02FF — "ᵃ"/"ʸ" also stand for phonetic notation, so folding them would read a
/// pronunciation hint as a letter.
/// </para>
/// </remarks>
public static class TextFold
{
    /// <summary>F-M457: the letter slots of each mathematical style block, A–Z then a–z (52 each).
    /// The blocks keep that order even where Unicode leaves a slot unassigned because a letterlike
    /// character stands in for it, so an offset maps cleanly.</summary>
    private static readonly int[] LetterBlockStarts =
    {
        0x1D400, 0x1D434, 0x1D468, 0x1D49C, 0x1D4D0, 0x1D504, 0x1D538,
        0x1D56C, 0x1D5A0, 0x1D5D4, 0x1D608, 0x1D63C, 0x1D670,
    };

    /// <summary>
    /// F-M724: the small capitals are NOT one block — they are scattered over five ranges of the
    /// phonetic extensions, because Unicode sorted them by sound and not by letter. Mapped slot by
    /// slot; no small capital X exists in Unicode, so "X" has no entry here and stays as it is.
    /// </summary>
    private static readonly Dictionary<int, char> SmallCapitals = new()
    {
        [0x1D00] = 'A', [0x0299] = 'B', [0x1D04] = 'C', [0x1D05] = 'D', [0x1D07] = 'E',
        [0xA730] = 'F', [0x0262] = 'G', [0x029C] = 'H', [0x026A] = 'I', [0x1D0A] = 'J',
        [0x1D0B] = 'K', [0x029F] = 'L', [0x1D0D] = 'M', [0x0274] = 'N', [0x1D0F] = 'O',
        [0x1D18] = 'P', [0xA7AF] = 'Q', [0x0280] = 'R', [0xA731] = 'S', [0x1D1B] = 'T',
        [0x1D1C] = 'U', [0x1D20] = 'V', [0x1D21] = 'W', [0x028F] = 'Y', [0x1D22] = 'Z',
    };

    /// <summary>
    /// F-M724: true when the text carries at least one character this fold would change.
    /// </summary>
    /// <param name="text">File or release name.</param>
    /// <returns>True when <see cref="Fold"/> would return a different string.</returns>
    public static bool NeedsFold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // The one rule again — no second list of ranges that could drift away from it.
                if (Folded(char.ConvertToUtf32(c, text[i + 1])) != '\0')
                {
                    return true;
                }

                i++;
                continue;
            }

            if (Folded(c) != '\0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// F-M724: folds styled letters and digits to their ASCII reading.
    /// </summary>
    /// <param name="text">File or release name.</param>
    /// <returns>The same name with the styling folded away — the very same instance when there is
    /// nothing to fold.</returns>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool changed = false;
        for (int i = 0; i < text.Length; i++)
        {
            int width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length
                        && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            int cp = width == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
            char folded = Folded(cp);

            if (folded == '\0')
            {
                sb.Append(text, i, width);
            }
            else
            {
                sb.Append(folded);
                changed = true;
            }

            i += width - 1;
        }

        return changed ? sb.ToString() : text;
    }

    /// <summary>Maps one code point to its ASCII reading, or '\0' when the fold leaves it alone.</summary>
    /// <param name="cp">The code point.</param>
    /// <returns>The ASCII character, or '\0'.</returns>
    private static char Folded(int cp)
    {
        // Mathematical alphanumerics: the digit runs sit behind the letter blocks.
        if (cp >= 0x1D7CE && cp <= 0x1D7FF)
        {
            return (char)('0' + ((cp - 0x1D7CE) % 10));
        }

        if (cp >= 0x1D400 && cp <= 0x1D7CB)
        {
            foreach (int start in LetterBlockStarts)
            {
                if (cp < start || cp >= start + 52)
                {
                    continue;
                }

                int off = cp - start;
                return (char)(off < 26 ? 'A' + off : 'a' + (off - 26));
            }

            return '\0';
        }

        if (cp == 0x210E)
        {
            return 'h'; // the italic small h lives outside the block
        }

        // Fullwidth forms: the whole ASCII range shifted by one block, so the punctuation the
        // episode patterns need ("S01E05", "．", "－") comes back too.
        if (cp >= 0xFF01 && cp <= 0xFF5E)
        {
            return (char)(cp - 0xFEE0);
        }

        if (cp >= 0x24B6 && cp <= 0x24CF)
        {
            return (char)('A' + (cp - 0x24B6));
        }

        if (cp >= 0x24D0 && cp <= 0x24E9)
        {
            return (char)('a' + (cp - 0x24D0));
        }

        if (cp >= 0x1F130 && cp <= 0x1F149)
        {
            return (char)('A' + (cp - 0x1F130));
        }

        // Superscript and subscript DIGITS only — ⁰⁴…⁹ / ₀…₉ sit in runs of ten; ¹²³ live in
        // Latin-1 and ⁰ outside the run, so they are named one by one.
        if (cp == 0x2070)
        {
            return '0'; // ⁱ (U+2071) sits in that run and is a LETTER — not folded
        }

        if (cp >= 0x2074 && cp <= 0x2079)
        {
            return (char)('0' + (cp - 0x2070));
        }

        if (cp >= 0x2080 && cp <= 0x2089)
        {
            return (char)('0' + (cp - 0x2080));
        }

        if (cp == 0x00B2)
        {
            return '2';
        }

        if (cp == 0x00B3)
        {
            return '3';
        }

        if (cp == 0x00B9)
        {
            return '1';
        }

        return SmallCapitals.TryGetValue(cp, out char small) ? small : '\0';
    }
}
