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
/// The window is a SHARE of the runtime with a FIXED floor under it: twelve percent of a long film is
/// a wider window than twelve percent of a short episode, and the floor keeps a short item from being
/// measured with a share that has shrunk to nothing. Each kind carries its own floor — an episode
/// four minutes, a feature film seven — because a film's credits run longer than an episode's. The
/// share is the only setting; the floors are constants of this class.
/// </para>
/// <para>
/// The share never tightens the floor. The check stays symmetric: a span that runs past the runtime
/// is as suspicious as one that stops early.
/// </para>
/// </summary>
public static class RuntimeWindow
{
    /// <summary>Share of the item runtime that the window grows by (F-M43), in percent.</summary>
    public const int DefaultSharePercent = 12;

    /// <summary>Default floor for a feature film in seconds: 7 minutes (F-M43).</summary>
    public const int FilmFloorSeconds = 420;

    /// <summary>Default floor for an episode in seconds: 4 minutes (F-M43).</summary>
    public const int EpisodeFloorSeconds = 240;

    /// <summary>
    /// The floor that belongs to this item: the episode floor for an episode, the film floor for a
    /// feature. Both are constants — the operator sets the share, never the floor.
    /// </summary>
    /// <param name="episode">True for an episode, false for a film.</param>
    /// <returns>The floor in seconds.</returns>
    public static int FloorFor(bool episode) => episode ? EpisodeFloorSeconds : FilmFloorSeconds;

    /// <summary>
    /// The effective window in seconds: the floor, or the share of the runtime when that is larger.
    /// </summary>
    /// <param name="floorSec">Fixed floor for this item in seconds.</param>
    /// <param name="runtimeSec">Item runtime in seconds.</param>
    /// <param name="sharePercent">Share of the runtime in percent (0 = the floor alone).</param>
    /// <returns>Effective window in seconds.</returns>
    public static long ToleranceSeconds(int floorSec, long runtimeSec, int sharePercent)
    {
        long share = runtimeSec <= 0 || sharePercent <= 0
            ? 0
            : (long)Math.Round(runtimeSec * sharePercent / 100.0, MidpointRounding.AwayFromZero);
        return Math.Max(floorSec, share);
    }

    /// <summary>
    /// Whether a cue span sits inside the window for this item. A missing runtime or a disabled
    /// check passes everything, exactly as the gate did before the window grew with the item.
    /// </summary>
    /// <param name="spanSec">Cue span in seconds (last end minus first start).</param>
    /// <param name="runtimeSec">Item runtime in seconds.</param>
    /// <param name="floorSec">Fixed floor for this item in seconds.</param>
    /// <param name="sharePercent">Share of the runtime in percent (0 = the floor alone).</param>
    /// <returns>True when the span is inside the window.</returns>
    public static bool Covers(long spanSec, long runtimeSec, int floorSec, int sharePercent)
    {
        long tolerance = ToleranceSeconds(floorSec, runtimeSec, sharePercent);
        if (tolerance <= 0 || runtimeSec <= 0)
        {
            return true;
        }

        return Math.Abs(spanSec - runtimeSec) <= tolerance;
    }
}
