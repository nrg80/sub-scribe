// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation, either version 3 of the License, or (at your option) any later
// version. Sub Scribe is distributed WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.

using System;

namespace Jellyfin.Plugin.SubScribe.Qa;

/// <summary>
/// F-M43 stage 2: how far a subtitle's cue span may sit from the item runtime.
/// <para>
/// The window is a FLOOR plus a SHARE. The floor depends on what is being matched: an episode gets
/// 4 minutes, a feature film 7 minutes — an operator who wants another film floor sets it, and `0`
/// switches the whole check off.
/// The share is 12 % of the item runtime and applies whenever it is the larger of the two: a long
/// film carries long credits, and a subtitle that stops when the dialogue stops is complete, not
/// broken. A fixed window measures the credits of a short film and the credits of a long film with
/// the same ruler, and so rejects the long one for a gap that is not a defect.
/// </para>
/// <para>
/// The share never tightens the floor — an operator who sets 600 s keeps at least 600 s. The check
/// stays symmetric: a span that runs past the runtime is as suspicious as one that stops early.
/// </para>
/// </summary>
public static class RuntimeWindow
{
    /// <summary>Share of the item runtime that is always allowed (F-M43).</summary>
    public const double Share = 0.12;

    /// <summary>Floor for a feature film in seconds: 7 minutes (F-M43).</summary>
    public const int FilmFloorSeconds = 420;

    /// <summary>Floor for an episode in seconds: 4 minutes (F-M43).</summary>
    public const int EpisodeFloorSeconds = 240;

    /// <summary>
    /// The effective window in seconds: the configured floor, or the share of the runtime when that
    /// is larger.
    /// </summary>
    /// <param name="floorSec">Configured film floor in seconds (0 or less = check off).</param>
    /// <param name="runtimeSec">Item runtime in seconds.</param>
    /// <param name="episode">True for an episode, false for a film.</param>
    /// <returns>Effective window in seconds.</returns>
    public static long ToleranceSeconds(int floorSec, long runtimeSec, bool episode)
    {
        if (floorSec <= 0)
        {
            return 0;
        }

        // An episode gets the smaller floor, but never more than the operator allowed for films.
        long floor = episode ? Math.Min(EpisodeFloorSeconds, floorSec) : floorSec;
        long share = runtimeSec <= 0 ? 0 : (long)Math.Round(runtimeSec * Share, MidpointRounding.AwayFromZero);
        return Math.Max(floor, share);
    }

    /// <summary>
    /// Whether a cue span sits inside the window for this item. A missing runtime or a disabled
    /// check passes everything, exactly as the gate did before the window grew with the item.
    /// </summary>
    /// <param name="spanSec">Cue span in seconds (last end minus first start).</param>
    /// <param name="runtimeSec">Item runtime in seconds.</param>
    /// <param name="floorSec">Configured film floor in seconds (0 or less = check off).</param>
    /// <param name="episode">True for an episode, false for a film.</param>
    /// <returns>True when the span is inside the window.</returns>
    public static bool Covers(long spanSec, long runtimeSec, int floorSec, bool episode)
    {
        long tolerance = ToleranceSeconds(floorSec, runtimeSec, episode);
        if (tolerance <= 0 || runtimeSec <= 0)
        {
            return true;
        }

        return Math.Abs(spanSec - runtimeSec) <= tolerance;
    }
}
