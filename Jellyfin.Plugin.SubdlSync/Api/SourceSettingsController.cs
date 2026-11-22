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
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.SubScribe.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubScribe.Api;

/// <summary>
/// F-M683: die Einstellungen JE QUELLE lesen und schreiben — ein Feldbeschreiber zum Anzeigen, ein Weg
/// zum Speichern. Die übergeordneten Einstellungen (Sprachen, Takt, Richtungen, Packs, HI) bleiben in
/// der Plugin-Konfiguration und werden hier nicht berührt; die Oberfläche bleibt in diesem Schritt
/// unverändert.
/// <para>
/// WARUM EIN EIGENER ENDPUNKT UND NICHT <c>updatePluginConfiguration</c>. Die Seite speichert heute
/// alles in einem Rutsch, und das ist genau der Grund, warum bisher niemand eine Quelle einzeln
/// einstellen konnte: ein Schreibvorgang für eine Quelle hätte die Werte aller anderen mitgeschickt.
/// Hier schreibt ein Aufruf GENAU EINE Datei (<c>sources/&lt;key&gt;.json</c>) — die anderen Quelldateien
/// werden byte-gleich bleiben, und das prüft die Suite.
/// </para>
/// <para>
/// ZUGANG: wie die übrigen Endpunkte des Plugins unter <c>Plugins/SubdlSync</c>, aber NICHT anonym —
/// hier stehen Zugänge. Die Jellyfin-Regel <c>LocalAccessOrRequiresElevation</c> lässt nur einen
/// angemeldeten Administrator (oder den Rechner selbst) heran.
/// </para>
/// </summary>
[ApiController]
[Route("Plugins/SubdlSync")]
[Authorize(Policy = "LocalAccessOrRequiresElevation")]
public class SourceSettingsController : ControllerBase
{
    /// <summary>
    /// Der HttpClient, mit dem die Registry für diese Antwort gebaut wird. Die Engines brauchen einen,
    /// um überhaupt zu existieren (Grenzregel b: er wird gereicht, nie selbst gebaut) — hier wird KEINE
    /// Anfrage gemacht. Einer für alle Antworten, damit nicht jede Seitenanzeige einen neuen anlegt.
    /// </summary>
    private static readonly System.Net.Http.HttpClient CatalogHttp =
        ScribeApiClient.NewHttpClient(TimeSpan.FromSeconds(30));

    private readonly ILogger<SourceSettingsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceSettingsController"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public SourceSettingsController(ILogger<SourceSettingsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// F-M683: je Quelle Schlüssel, Anzeigename, Fähigkeiten, die erklärten Felder in Reihenfolge und die
    /// aktuellen Werte. Für ein Geheimfeld steht NIE der Wert in der Antwort, nur <c>gesetzt</c>.
    /// </summary>
    /// <returns>Die Quellen mit ihren Einstellungen.</returns>
    [HttpGet("Sources")]
    public IActionResult GetSources()
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        try
        {
            SourceRegistry registry = Registry(plugin, _logger);
            return Ok(new
            {
                sources = SourceSettingsView.Build(registry, plugin.DataFolderPath),
                timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Sub Scribe] reading the source settings failed");
            return StatusCode(500, new { error = "source settings could not be read" });
        }
    }

    /// <summary>
    /// F-M683: speichert die Einstellungen GENAU EINER Quelle. Übergebene Felder werden geschrieben,
    /// leere Geheimfelder bedeuten „unverändert lassen", unbekannte Felder werden abgewiesen.
    /// <para>
    /// Der Rumpf ist ein flaches Objekt aus Feldname → Wert (<c>{"account": "…", "preferSeasonZip": false}</c>).
    /// </para>
    /// </summary>
    /// <param name="key">Der Quellenschlüssel.</param>
    /// <param name="body">Die zu schreibenden Felder.</param>
    /// <returns>Der Stand der Quelle nach dem Schreiben, oder der Grund der Ablehnung.</returns>
    [HttpPost("Sources/{key}")]
    public IActionResult SaveSource(string key, [FromBody] JsonElement body)
    {
        Plugin? plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(503, new { error = "Plugin instance not available" });
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { error = "the body must be an object of field name → value" });
        }

        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (JsonProperty feld in body.EnumerateObject())
        {
            switch (feld.Value.ValueKind)
            {
                case JsonValueKind.String:
                    updates[feld.Name] = feld.Value.GetString();
                    break;
                case JsonValueKind.True:
                    updates[feld.Name] = "true";
                    break;
                case JsonValueKind.False:
                    updates[feld.Name] = "false";
                    break;
                case JsonValueKind.Number:
                    updates[feld.Name] = feld.Value.GetRawText();
                    break;
                case JsonValueKind.Null:
                    updates[feld.Name] = null;
                    break;
                default:
                    return BadRequest(new { error = "field '" + feld.Name + "' carries no simple value" });
            }
        }

        try
        {
            SourceSaveResult ergebnis = SourceSettingsView.Save(
                Registry(plugin, _logger), plugin.DataFolderPath, key, updates);

            if (!ergebnis.Ok)
            {
                return ergebnis.UnknownFields.Count > 0
                    ? BadRequest(new { error = ergebnis.Error, unknown = ergebnis.UnknownFields })
                    : BadRequest(new { error = ergebnis.Error });
            }

            return Ok(new
            {
                key,
                written = ergebnis.Written,

                // F-M683: der Stand nach dem Schreiben — mit derselben Regel für Geheimfelder: kein Wert.
                source = ergebnis.Entry,
            });
        }
        catch (Exception ex)
        {
            // Kein Zugang und kein Wert in der Meldung: der Fehler geht ins Log, der Aufrufer bekommt
            // nur, dass es nicht ging.
            _logger.LogError(ex, "[Sub Scribe] writing the settings of a source failed");
            return StatusCode(500, new { error = "source settings could not be written" });
        }
    }

    /// <summary>
    /// F-M683: die Registry dieses Aufrufs — aus der Konfiguration und mit dem Datenordner, damit die
    /// Übernahme beim ersten Start auch dann läuft, wenn dieser Endpunkt der erste Zugriff ist.
    /// </summary>
    /// <param name="plugin">Die Plugin-Instanz.</param>
    /// <param name="logger">Der Logger, den die Engines gereicht bekommen.</param>
    /// <returns>Die Registry.</returns>
    private static SourceRegistry Registry(Plugin plugin, ILogger logger)
        => SourceRegistry.Build(
            new SourceEngineContext(
                CatalogHttp,
                logger,
                TimeProvider.System,
                DataFolder: plugin.DataFolderPath),
            plugin.Configuration);
}
