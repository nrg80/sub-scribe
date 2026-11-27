using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Pipeline;

/// <summary>
/// F-M608 (Betreiber-Vorgabe 19.11.2026: „nach dem vierten Fehlschlag ist der Titel zu"): die
/// Wiederholungsleiter um die Titel-Frage. Sie ist die EINE Stelle, die zaehlt und schliesst — wer sie
/// benutzt, entscheidet nur, WAS ein Versuch ist.
/// <para>
/// Sie steht hier und nicht im Ablauf, weil zwei Stellen sie brauchen: die Einzelabgabe (dort ist der
/// Versuch der Sende-Auftrag fuer eine Folge) und der Staffel-Behälter (dort ist der Versuch derselbe
/// Auftrag, nur mit dem fertigen Zip — er geht erst raus, wenn der Titel steht, F-M643/F-M734).
/// </para>
/// <para>
/// Sie zaehlt nur Urteile: eine Stoerung (kein Urteil, z.B. 429 oder 5xx) zaehlt NICHT als Fehlshlag und
/// schliesst nichts (F-M610); ein Beleg ohne lesbare Kennung ist ebenfalls kein Fehlschlag, sondern
/// schliesst das PAAR (F-M603).
/// </para>
/// </summary>
internal static class TitleLadder
{
    /// <summary>Die Pausen zwischen den Zuegen; der letzte Wert gilt fuer jeden weiteren Zug.</summary>
    internal static readonly int[] PausenMs = { 2000, 4000, 8000 };

    /// <summary>
    /// F-M608: faehrt die Leiter und meldet, was geschah.
    /// </summary>
    /// <typeparam name="T">Was ein Zug zurueckgibt (die Antwort des Auftrags).</typeparam>
    /// <param name="quelle">Der Schluessel der Quelle — er ist Teil des Schluessels der Sperre.</param>
    /// <param name="titelId">Die Kennung des Titels, um den es geht.</param>
    /// <param name="versuch">Ein Zug: der Sende-Auftrag mit der Titel-Frage.</param>
    /// <param name="gelungen">Steht der Titel? Dann ist die Leiter fertig.</param>
    /// <param name="grund">Der Klartext des Zuges fuer Protokoll und Sperre.</param>
    /// <param name="schonBelegt">F-M603/F-M610: belegt, aber ohne lesbare Kennung — kein Fehlschlag.</param>
    /// <param name="stoerung">F-M610: kein Urteil — kein Fehlschlag, schliesst nichts.</param>
    /// <param name="zwischenZug">Was vor jedem weiteren Zug zu tun ist (z.B. einen Lauf-Speicher leeren).</param>
    /// <param name="trockenlauf">Ein Trockenlauf faehrt genau einen Zug und schliesst nichts.</param>
    /// <param name="logMode">Wie laut der Lauf protokolliert.</param>
    /// <param name="logger">Das Protokoll.</param>
    /// <param name="ct">Abbruch.</param>
    /// <returns>Das Ergebnis des letzten Zuges, ob der Titel steht, und ob er geschlossen wurde.</returns>
    internal static async Task<(T Ergebnis, bool Gelungen, bool Geschlossen)> FahreAsync<T>(
        string quelle,
        string titelId,
        Func<Task<T>> versuch,
        Func<T, bool> gelungen,
        Func<T, string?> grund,
        Func<T, bool>? schonBelegt,
        Func<T, bool>? stoerung,
        Action zwischenZug,
        bool trockenlauf,
        Jellyfin.Plugin.SubScribe.Configuration.LogLevelMode logMode,
        ILogger logger,
        CancellationToken ct)
    {
        T ergebnis = default!;
        string? letzterGrund = null;
        bool letzteStoerung = false;

        // F-M771: ist die Sperre abgelaufen und laeuft die Probe, dann genau EIN Zug. Sonst wuerde eine
        // Probe wieder die vollen vier Zuege fahren, und der Titel waere nach einer Woche genauso
        // geschlossen wie vorher — nur mit mehr Aufwand.
        int zuege = trockenlauf
            ? 1
            : Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.IsProbe(quelle, titelId)
                ? 1
                : Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.ClosingAttempt;

        // F-M777: die Ablage darf keinen Titel schliessen. Wurde waehrend der Zuege kein einziges Mal
        // wirklich gefragt — alle Antworten kamen aus der Ablage —, dann ist das kein Urteil ueber den
        // Titel, sondern eine alte Auskunft. Dann wird gezaehlt, aber nicht geschlossen.
        long ausDerAblageVorher = Api.TmdbCache.ServedFromShelf;
        long anfragenVorher = Api.TmdbCache.NetworkRequests;

        for (int zug = 1; zug <= zuege; zug++)
        {
            if (zug > 1)
            {
                int pause = PausenMs[Math.Min(zug - 2, PausenMs.Length - 1)];
                LogUtil.PerItem(logMode, logger,
                    "[Sub Scribe] {Source} title {Title}: attempt {Zug}/{Zuege} failed ({Reason}) — waiting {Pause} ms (F-M608)",
                    quelle, titelId, zug - 1, zuege, letzterGrund ?? "?", pause);
                await Task.Delay(pause, ct).ConfigureAwait(false);
                zwischenZug();
            }

            ergebnis = await versuch().ConfigureAwait(false);

            // F-M610: ein Beleg ohne Aufloesung („existiert, Kennung nicht lesbar") ist KEIN Fehlschlag
            // der Leiter — das Paar wird ueber F-M603 geschlossen, der Titel bleibt offen.
            if (gelungen(ergebnis) || (schonBelegt?.Invoke(ergebnis) ?? false))
            {
                Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.Clear(quelle, titelId);
                return (ergebnis, true, false);
            }

            letzterGrund = grund(ergebnis) ?? "?";
            letzteStoerung = stoerung?.Invoke(ergebnis) ?? false;
            if (!letzteStoerung)
            {
                // F-M610: eine Stoerung zaehlt nicht als Fehlschlag — sie ist kein Urteil.
                Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.Note(quelle, titelId, letzterGrund);
            }
        }

        bool nurAusDerAblage = !trockenlauf
            && Api.TmdbCache.ServedFromShelf > ausDerAblageVorher
            && Api.TmdbCache.NetworkRequests == anfragenVorher;
        if (nurAusDerAblage)
        {
            LogUtil.PerItem(logMode, logger,
                "[Sub Scribe] {Source} title {Title}: every attempt was answered from the shelf, not by TMDb — counted, not closed (F-M777)",
                quelle, titelId);
        }

        if (!trockenlauf && !letzteStoerung && !nurAusDerAblage)
        {
            Jellyfin.Plugin.SubScribe.Registry.TitleFailureStore.Close(quelle, titelId, letzterGrund ?? "?");
            LogUtil.Normal(logger,
                "[Sub Scribe] {Source} title {Title}: {Zuege} attempts failed ({Reason}) — every pair of this title is closed for good (F-M608)",
                quelle, titelId, zuege, letzterGrund ?? "?");
            return (ergebnis, false, true);
        }

        if (letzteStoerung)
        {
            LogUtil.PerItem(logMode, logger,
                "[Sub Scribe] {Source} title {Title}: {Zuege} attempts without a verdict ({Reason}) — nothing closed, the next run asks again (F-M610)",
                quelle, titelId, zuege, letzterGrund ?? "?");
        }

        return (ergebnis, false, false);
    }
}
