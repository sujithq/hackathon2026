using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace CopilotUsageSimulator.Web.Services;

public sealed class BrowserScenarioPersistence(IJSRuntime js)
{
    private const string ScenarioStorageKey = "copilot-usage-simulator.scenario.v1";
    private const string CatalogStorageKey = "copilot-usage-simulator.catalog.v1";
    private const string PreferenceStorageKey = "copilot-usage-simulator.preferences.v1";
    private const long MaximumImportSize = 2 * 1024 * 1024;

    public async Task SaveAsync(
        string scenarioJson,
        string catalogJson,
        DisplayPreferences preferences)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", ScenarioStorageKey, scenarioJson);
            await js.InvokeVoidAsync("localStorage.setItem", CatalogStorageKey, catalogJson);
            await js.InvokeVoidAsync(
                "localStorage.setItem",
                PreferenceStorageKey,
                JsonSerializer.Serialize(preferences));
        }
        catch (JSException exception)
        {
            throw new BrowserPersistenceException("Browser storage is unavailable.", exception);
        }
    }

    public async Task<BrowserScenarioState?> LoadAsync()
    {
        try
        {
            var scenarioJson = await js.InvokeAsync<string?>("localStorage.getItem", ScenarioStorageKey);
            if (string.IsNullOrWhiteSpace(scenarioJson))
            {
                return null;
            }

            var catalogJson = await js.InvokeAsync<string?>("localStorage.getItem", CatalogStorageKey);
            var preferencesJson = await js.InvokeAsync<string?>("localStorage.getItem", PreferenceStorageKey);
            var preferences = string.IsNullOrWhiteSpace(preferencesJson)
                ? null
                : JsonSerializer.Deserialize<DisplayPreferences>(preferencesJson);
            return new BrowserScenarioState(scenarioJson, catalogJson, preferences);
        }
        catch (JSException exception)
        {
            throw new BrowserPersistenceException("Browser storage is unavailable.", exception);
        }
    }

    public async ValueTask ExportAsync(string scenarioJson)
    {
        try
        {
            await js.InvokeVoidAsync("simulator.downloadText", "copilot-simulation.json", scenarioJson);
        }
        catch (JSException exception)
        {
            throw new BrowserPersistenceException("The scenario could not be exported.", exception);
        }
    }

    public static async Task<string> ReadImportAsync(IBrowserFile file)
    {
        await using var stream = file.OpenReadStream(MaximumImportSize);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

public sealed record BrowserScenarioState(
    string ScenarioJson,
    string? CatalogJson,
    DisplayPreferences? Preferences);

public sealed class BrowserPersistenceException(string message, JSException innerException)
    : Exception(message, innerException);
