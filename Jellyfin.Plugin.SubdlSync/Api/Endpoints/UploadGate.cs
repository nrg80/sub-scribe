// This file is part of Sub Scribe (https://github.com/nrg80/sub-scribe)
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Sub Scribe is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 3 of the License, or (at your
// option) any later version. Sub Scribe is distributed WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
// PARTICULAR PURPOSE. See the GNU General Public License for more details.

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubScribe.Configuration;
using Jellyfin.Plugin.SubScribe.Sources;

namespace Jellyfin.Plugin.SubScribe.Api.Endpoints;

/// <summary>
/// F-M489 (operator order 15.11.2026: "Reup und Kreuzkontamination sollen verhindert werden. Also auch
/// rein reup von tvst auf tvst. Kein reup von subdl auf subdl"): which sources may still take THIS
/// subtitle, decided in ONE place.
/// <para>
/// Zwei unabhaengige Gruende koennen eine Quelle sperren, und beide werden JE QUELLE beantwortet:
/// </para>
/// <list type="bullet">
/// <item><description>ihr Schalter steht aus, oder die Herkunft ist BEKANNT (F-M487 verschaerft: ein
/// Download geht nirgends hoch — weder zurueck zur eigenen Quelle noch zur anderen);</description></item>
/// <item><description>diese Quelle hat den Untertitel SCHON — ihre eigene Marke liegt im Bestand
/// (F-M488). Ein Reup auf dieselbe Quelle ist so unerwuenscht wie eine Kreuzkontamination.</description></item>
/// </list>
/// <para>
/// F-M721 (Ansage 23.11.2026: "Eine regel statt 3 ist besser. Geht mehr darum den code so zu formulieren,
/// das er auch mit einer vierten, fuenften, etc... Endpoint funktioniert"): das Tor zaehlt Quellen NICHT
/// mehr auf. Es bekommt eine Liste <see cref="UploadTarget"/> und laeuft sie durch; die eine Regel steht
/// in <see cref="Provenance.MayUpload"/> und gilt fuer jedes Ziel gleich. Wer die Liste
/// baut, fragt die Registry (<see cref="TargetsFrom"/>) — eine vierte Engine, die dort haengt, ist damit
/// ohne Aenderung an diesem Tor dabei.
/// </para>
/// </summary>
public static class UploadGate
{
    /// <summary>
    /// F-M721: die Entscheidung ueber eine beliebig lange Quellenliste. Je Ziel dieselben zwei Fragen
    /// (Schalter an? Marke schon da?) und dieselbe eine Herkunftsregel.
    /// </summary>
    /// <param name="origin">Die Herkunft, wie sie im Bestand steht (null/leer = unbekannt).</param>
    /// <param name="targets">Die Quellen dieses Laufs, in Frage-Reihenfolge.</param>
    /// <returns>Welche Quellen duerfen, und warum keine darf.</returns>
    public static UploadGateResult Decide(string? origin, IReadOnlyList<UploadTarget> targets)
    {
        // F-M722: die Herkunft wird EINMAL gefragt, nicht je Ziel — sie haengt nicht vom Ziel ab.
        // Danach entscheidet je Quelle nur noch ihr Schalter und ihre Marke.
        var herkunftErlaubt = Provenance.MayUpload(origin);

        var offen = new List<UploadTarget>();
        var darf = new List<string>();
        foreach (var ziel in targets)
        {
            if (!ziel.SwitchedOn)
            {
                continue;
            }

            offen.Add(ziel);
            if (herkunftErlaubt && !ziel.AlreadyThere)
            {
                darf.Add(ziel.Key);
            }
        }

        if (darf.Count > 0)
        {
            return new UploadGateResult(darf, null);
        }

        // Keine Quelle darf: die Herkunft nennt den Grund, wenn sie ihn kennt (F-M500 — eine bekannte
        // Herkunft sperrt ALLE Quellen, nicht nur die fremde).
        if (!herkunftErlaubt)
        {
            return new UploadGateResult(
                Array.Empty<string>(), Provenance.OriginBlockedReason(origin) ?? "no-upload-source");
        }

        if (offen.Count == 0)
        {
            return new UploadGateResult(Array.Empty<string>(), "no-upload-source");
        }

        // Sonst haben die offenen Quellen den Text schon — nur sie werden genannt, denn eine
        // abgeschaltete Quelle ist nicht "schon hochgeladen", sondern aus.
        return new UploadGateResult(
            Array.Empty<string>(), "already-uploaded:" + string.Join("+", offen.Select(ziel => ziel.Key)));
    }

    /// <summary>
    /// F-M508/F-M721: die Ziele dieses Laufs — aus der REGISTRY, nicht aus einer Aufzaehlung im Tor.
    /// <para>
    /// Je Engine: kann sie ueberhaupt hochladen, fuehrt sie diese Medienart (die Quelle erklaert es
    /// selbst, F-M696), ist ihr Schalter offen, hat sie den Text schon. Eine vierte Engine, die die
    /// Registry baut, erscheint hier automatisch.
    /// </para>
    /// </summary>
    /// <param name="sources">Die Registry dieses Laufs.</param>
    /// <param name="isSeries">Ist die Medienart eine Serie?</param>
    /// <param name="switchedOn">Der Upload-Schalter dieser Quelle (Schluessel).</param>
    /// <param name="alreadyThere">Hat diese Quelle den Text schon (Schluessel)?</param>
    /// <param name="alsoTakes">Weitere Bedingung des Aufrufers, etwa die Sprache (Schluessel).</param>
    /// <returns>Die Ziele in Frage-Reihenfolge.</returns>
    public static IReadOnlyList<UploadTarget> TargetsFrom(
        SourceRegistry sources,
        bool isSeries,
        Func<string, bool> switchedOn,
        Func<string, bool> alreadyThere,
        Func<string, bool>? alsoTakes = null)
    {
        var ziele = new List<UploadTarget>();
        foreach (var engine in sources.Engines)
        {
            var faehig = engine.Capabilities;
            if (!faehig.CanUpload)
            {
                continue;
            }

            // F-M696: die Medienart ist eine Auskunft der Quelle, kein Namensvergleich.
            if (isSeries ? !faehig.ServesSeries : !faehig.ServesMovies)
            {
                continue;
            }

            var key = faehig.Key;
            var an = switchedOn(key) && (alsoTakes is null || alsoTakes(key));
            ziele.Add(new UploadTarget(key, an, alreadyThere(key)));
        }

        return ziele;
    }

    /// <summary>
    /// F-M489/F-M508: die alte Form mit den drei festen Quellen. Sie uebersetzt sich in die generische
    /// Liste und enthaelt selbst keine Regel; neue Aufrufer bauen ihre Ziele mit <see cref="TargetsFrom"/>.
    /// </summary>
    /// <param name="subdlSwitch">Ist der Upload-Schalter von SubDL offen?</param>
    /// <param name="tvSubtitlesSwitch">Ist der von tvsubtitles.net offen?</param>
    /// <param name="origin">Die Herkunft.</param>
    /// <param name="subdlAlready">Hat SubDL den Text schon?</param>
    /// <param name="tvSubtitlesAlready">Hat tvsubtitles.net ihn schon?</param>
    /// <param name="movieSubtitlesSwitch">Ist der von moviesubtitles.org offen?</param>
    /// <param name="movieSubtitlesAlready">Hat moviesubtitles.org ihn schon?</param>
    /// <returns>Dasselbe Urteil wie <see cref="Decide(string?, IReadOnlyList{UploadTarget})"/>.</returns>
    public static UploadGateResult Decide(
        bool subdlSwitch, bool tvSubtitlesSwitch, string? origin, bool subdlAlready, bool tvSubtitlesAlready,
        bool movieSubtitlesSwitch, bool movieSubtitlesAlready)
        => Decide(
            origin,
            new[]
            {
                new UploadTarget(Provenance.Name(SubtitleProvider.Subdl), subdlSwitch, subdlAlready),
                new UploadTarget(Provenance.Name(SubtitleProvider.TvSubtitles), tvSubtitlesSwitch, tvSubtitlesAlready),
                new UploadTarget(Provenance.Name(SubtitleProvider.MovieSubtitles), movieSubtitlesSwitch, movieSubtitlesAlready),
            });

    /// <summary>
    /// F-M508: darf DIESE Quelle in diesem Lauf? Die Kette liest ihre Antwort hier, statt drei Felder
    /// abzufragen — ein vierter Anbieter braucht damit keine Zeile in der Kette.
    /// </summary>
    /// <param name="result">Das Urteil von <see cref="Decide(string?, IReadOnlyList{UploadTarget})"/>.</param>
    /// <param name="provider">Die gefragte Quelle.</param>
    /// <returns>Wahr, wenn diese Quelle den Untertitel nehmen darf.</returns>
    public static bool May(this UploadGateResult result, SubtitleProvider provider)
        => result.May(Provenance.Name(provider));

    /// <summary>
    /// F-M721: dieselbe Frage mit dem Quellenschluessel — das ist die Form, die ohne Aufzaehlung
    /// funktioniert.
    /// </summary>
    /// <param name="result">Das Urteil.</param>
    /// <param name="key">Der Quellenschluessel.</param>
    /// <returns>Wahr, wenn diese Quelle darf.</returns>
    public static bool May(this UploadGateResult result, string key)
        => result.Allowed.Contains(key, StringComparer.Ordinal);
}

/// <summary>
/// F-M721: eine Quelle, wie das Tor sie fragt — Schluessel, Schalter, Marke. Mehr weiss das Tor nicht,
/// und mehr braucht es nicht, damit eine weitere Quelle ohne Aenderung mitlaeuft.
/// </summary>
/// <param name="Key">Der Quellenschluessel (etwa <c>subdl</c>).</param>
/// <param name="SwitchedOn">Ist der Upload dieser Quelle offen?</param>
/// <param name="AlreadyThere">Hat diese Quelle den Text schon?</param>
public readonly record struct UploadTarget(string Key, bool SwitchedOn, bool AlreadyThere);

/// <summary>
/// F-M489/F-M721: the result of <see cref="UploadGate.Decide(string?, IReadOnlyList{UploadTarget})"/>.
/// </summary>
/// <param name="Allowed">Die Quellen, die den Text nehmen duerfen — leer heisst: keine.</param>
/// <param name="SkipReason">Warum keine Quelle darf, oder null, wenn mindestens eine darf.</param>
public readonly record struct UploadGateResult(IReadOnlyList<string> Allowed, string? SkipReason)
{
    /// <summary>Gets F-M508: wie viele Quellen ihn nehmen duerfen — 0 heisst "nichts laeuft".</summary>
    public int AllowedCount => Allowed.Count;

    /// <summary>Gets F-M489: darf er zu SubDL?</summary>
    public bool Subdl => Allowed.Contains("subdl", StringComparer.Ordinal);

    /// <summary>Gets F-M489: darf er zu tvsubtitles.net?</summary>
    public bool TvSubtitles => Allowed.Contains("tvsubtitles", StringComparer.Ordinal);

    /// <summary>Gets F-M508: darf er zu moviesubtitles.org?</summary>
    public bool MovieSubtitles => Allowed.Contains("moviesubtitles", StringComparer.Ordinal);
}
