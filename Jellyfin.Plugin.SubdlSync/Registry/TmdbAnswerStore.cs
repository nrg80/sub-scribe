using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using Jellyfin.Plugin.SubScribe.Data;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SubScribe.Registry;

/// <summary>
/// Die Antworten des Resolvers — der Name ist geblieben, der Ort ist ein anderer.
/// <para>
/// F-M542 (Operator-Anweisung 16.11.2026: „Ok. In den store damit"): die TMDb-Antworten liegen im Store
/// statt nur im Speicher. Gemessen am 16.11.2026: 555 TMDb-Anfragen an einem Tag, davon 272 verschiedene —
/// die Wiederholungen kamen nicht aus einem Lauf, sondern aus den 31 Plugin-Starts des Tages.
/// <see cref="Api.TmdbImdbResolver"/> hielt seine Antworten in einem statischen Wörterbuch; jeder Neustart
/// warf sie weg und dieselben Titel, Ids und Serien wurden erneut bei TMDb erfragt.
/// </para>
/// <para>
/// F-M772 (seit 12.3.8.72): der Ort ist die eigene TMDb-Ablage, nicht mehr eine Tabelle in der
/// Plugin-Datenbank. Diese Klasse ist deshalb nur noch die Übersetzung zwischen der Frage des Resolvers
/// und einer Tatsache der Ablage: der Schlüssel ist die Frage, die Art der Tatsache folgt aus ihrem
/// Aussehen (F-M775), und die Frist bringt die Ablage mit.
/// </para>
/// <para>
/// F-M781 (seit 12.3.8.74): die alte Tabelle <c>tmdb_answers</c> gibt es nicht mehr. Was beim Anhängen noch
/// darin steht, wird in die Ablage gehoben und die Tabelle danach fallen gelassen; angelegt wird sie nicht
/// mehr. Bis dahin wurde sie bei jedem Start erzeugt, obwohl niemand mehr hineinschrieb — eine leere
/// Tabelle, die nur Platz in der Datei hielt.
/// </para>
/// </summary>
public static class TmdbAnswerStore
{
    /// <summary>Name of the table that is being retired.</summary>
    private const string OldTable = "tmdb_answers";

    private static readonly object Gate = new();

    private static ScribeSqliteContext? _db;

    private static bool _oldTableGone;

    /// <summary>
    /// Attaches the store. Called once when the plugin builds its data context; without it the resolver
    /// keeps working exactly as before, memory only.
    /// </summary>
    /// <param name="db">The plugin's data context — needed only to retire the old table.</param>
    public static void Attach(ScribeSqliteContext? db)
    {
        lock (Gate)
        {
            _db = db;
        }

        RetireOldTable();
    }

    /// <summary>
    /// Reads an answer — memory first, then the shelf. An expired fact is removed by the shelf and answers
    /// <c>false</c>, so a stale answer never counts as one.
    /// </summary>
    /// <param name="key">The resolver's question and its series flag.</param>
    /// <param name="value">The answer, or null for "asked and found nothing".</param>
    /// <returns>True when a fresh answer exists.</returns>
    public static bool TryGet((string, bool) key, out string? value)
    {
        // F-M775: die Art der Tatsache folgt aus dem Aussehen der Frage, nicht aus dem Aufrufer. Eine von
        // TMDb belegte Kennung lebt 365 Tage, eine aus Titel und Jahr geratene 30, eine Niete einen Tag.
        return Api.TmdbCache.TryGetFact(Api.TmdbCache.KindOfQuestion(key.Item1), KeyOf(key), out value);
    }

    /// <summary>
    /// Writes an answer. There is exactly one place an answer lands: the shelf.
    /// </summary>
    /// <param name="key">The resolver's question and its series flag.</param>
    /// <param name="value">The answer, or null for "found nothing".</param>
    public static void Put((string, bool) key, string? value)
    {
        // F-M780: bis 12.3.8.72 schrieb jede Antwort ZWEIMAL — in die Ablage und in die Tabelle
        // tmdb_answers, die dieselbe Auskunft unter anderen Fristen hielt. Die Plugin-Datenbank wuchs also
        // weiter mit, obwohl niemand mehr daraus las.
        Api.TmdbCache.PutFact(Api.TmdbCache.KindOfQuestion(key.Item1), KeyOf(key), value);
    }

    /// <summary>
    /// F-M772: räumt die Ablage auf — abgelaufene Zeilen und beide Decken. Der Name ist der alte; die
    /// Arbeit macht die Ablage.
    /// </summary>
    public static void Prune() => Api.TmdbCache.Trim();

    /// <summary>
    /// F-M781: removes the old answer table once and for all — no takeover.
    /// <para>
    /// Concept decision (operator order 27.11.2026: „Ich will keine Übernahme für den cache. Einfach
    /// einmal entfernen."): what still sits in <c>tmdb_answers</c> is thrown away, not lifted into the
    /// shelf. The rows are answers written under lifetimes that no longer exist, and a takeover would carry
    /// that doubt into the new shelf. The cost is visible and small: the first run after this asks TMDb
    /// again for whatever it needs, and the shelf fills itself from those answers.
    /// </para>
    /// </summary>
    private static void RetireOldTable()
    {
        ScribeSqliteContext? db = Store();
        if (db == null || _oldTableGone)
        {
            return;
        }

        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            {
                conn.Open();
            }

            using (var drop = conn.CreateCommand())
            {
                drop.CommandText = $"DROP TABLE IF EXISTS {OldTable};";
                drop.ExecuteNonQuery();
            }

            _oldTableGone = true;
            Api.TmdbCache.NoteRetiredTable();
        }
        catch (Exception ex)
        {
            // Ein Fehler darf den Start nicht verhindern — dann steht die Tabelle bis zum nächsten Mal.
            System.Diagnostics.Debug.WriteLine($"[Sub Scribe] dropping {OldTable} failed: {ex.GetType().Name}: {ex.Message} (F-M781)");
        }
    }

    /// <summary>
    /// F-M545: der angehaengte Kontext, notfalls nachgeholt. Am 16.11.2026 fehlte der Anhang im
    /// normalen Startpfad (nur <c>RecreateDbContext</c> haengte an) — der Store war die ganze Zeit
    /// leer, waehrend 42 von 42 TMDb-Fragen eines Zyklus Wiederholungen waren.
    /// </summary>
    /// <returns>The context, or null when there is none.</returns>
    private static ScribeSqliteContext? Store()
    {
        lock (Gate)
        {
            if (_db == null)
            {
                try
                {
                    _db = Plugin.Instance?.SharedDbContext?.Context;
                }
                catch (Exception)
                {
                    // kein Plugin-Kontext erreichbar: dann bleibt es beim Speicher
                }
            }

            return _db;
        }
    }

    /// <summary>
    /// The store key: the resolver's question plus whether the answer was asked for a series. Kept as a
    /// readable string so the shelf can be inspected in a plain SQLite viewer.
    /// </summary>
    /// <param name="key">Question key of the resolver.</param>
    /// <returns>Subject inside the shelf.</returns>
    private static string KeyOf((string, bool) key) => string.Concat(key.Item1, key.Item2 ? "|S" : "|M");
}
