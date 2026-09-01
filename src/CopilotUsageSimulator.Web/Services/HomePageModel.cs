using System.Text.Json;
using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Simulation;
using Microsoft.AspNetCore.Components.Forms;

namespace CopilotUsageSimulator.Web.Services;

public sealed class HomePageModel
{
    private readonly ICopilotUsageSimulationEngine defaultEngine;
    private readonly EngineConfiguration defaultConfiguration;
    private readonly ScenarioJson scenarioJson;
    private readonly ScenarioEditorAdapter editorAdapter;
    private readonly SimulationSessionRunner sessionRunner;
    private readonly BrowserScenarioPersistence browserPersistence;
    private EngineConfiguration activeConfiguration;
    private ICopilotUsageSimulationEngine activeEngine;

    public HomePageModel(
        ICopilotUsageSimulationEngine defaultEngine,
        EngineConfiguration defaultConfiguration,
        ScenarioJson scenarioJson,
        ScenarioEditorAdapter editorAdapter,
        SimulationSessionRunner sessionRunner,
        BrowserScenarioPersistence browserPersistence)
    {
        this.defaultEngine = defaultEngine;
        this.defaultConfiguration = defaultConfiguration;
        this.scenarioJson = scenarioJson;
        this.editorAdapter = editorAdapter;
        this.sessionRunner = sessionRunner;
        this.browserPersistence = browserPersistence;
        activeEngine = defaultEngine;
        activeConfiguration = defaultConfiguration;
    }

    public ScenarioEditorState Form { get; private set; } = new();
    public SimulationResultsState Results { get; } = new();
    public string ScenarioJsonText { get; set; } = "";
    public string CatalogJson { get; set; } = "";
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public EngineConfiguration ActiveConfiguration => activeConfiguration;
    public OperationDefinition? SelectedOperation => ActiveConfiguration.Operations.FirstOrDefault(
        operation => string.Equals(operation.Id, Form.Workload.OperationId, StringComparison.OrdinalIgnoreCase));
    public IEnumerable<OperationDefinition> ExampleOperations =>
        ActiveConfiguration.Operations.Where(operation => !string.IsNullOrWhiteSpace(operation.ExampleLabel));
    public bool OperationConsumesAiCredits => SelectedOperation?.IsBilled == true;
    public bool OperationUsesActions => SelectedOperation?.ActionsMetering != ActionsMeteringMode.None;
    public bool OperationUsesRepositoryVisibility =>
        SelectedOperation?.ActionsMetering == ActionsMeteringMode.PrivateRepositories;
    public string OperationContext => SelectedOperation switch
    {
        null => "This operation is not present in the active catalog.",
        { IsBilled: false } =>
            "This operation does not consume AI credits. Token, billing, runtime, and spending controls are hidden and not evaluated.",
        { ActionsMetering: ActionsMeteringMode.Always } =>
            "This operation consumes AI credits and always uses GitHub Actions. Repository visibility does not change runner metering.",
        { ActionsMetering: ActionsMeteringMode.PrivateRepositories } =>
            "This operation consumes AI credits. GitHub Actions charges apply to private and internal repositories, so repository visibility is required.",
        _ =>
            "This operation consumes AI credits but does not use GitHub Actions. Runner and repository controls are hidden and not evaluated."
    };

    public void Initialize()
    {
        ResetCatalog();
        LoadTemplate(InitialExampleOperationId);
    }

    public void LoadTemplate(string template)
    {
        var scenario = ExampleScenarioFactory.Create(ActiveConfiguration, template);
        ScenarioJsonText = scenarioJson.Serialize(scenario);
        LoadForm(scenario);
        RunScenario(scenario, advanceWorkingState: false);
        Notice = null;
    }

    public void ApplyOverrides()
    {
        try
        {
            var scenario = editorAdapter.ApplyToScenario(
                scenarioJson.Deserialize(ScenarioJsonText),
                Form,
                ActiveConfiguration);
            ScenarioJsonText = scenarioJson.Serialize(scenario);
            Error = null;
            Notice = "Guided overrides applied to the complete scenario.";
        }
        catch (Exception exception) when (exception is JsonException or SimulationException)
        {
            Error = exception.Message;
            Notice = null;
        }
    }

    public void ApplyAndRun()
    {
        ApplyOverrides();
        if (Error is null)
        {
            RunJson();
        }
    }

    public void RunJson() => RunJsonCore(advanceWorkingState: true);

    public void ReloadGuidedFields()
    {
        try
        {
            LoadForm(scenarioJson.Deserialize(ScenarioJsonText));
            Error = null;
            Notice = "Guided fields refreshed from JSON.";
        }
        catch (JsonException exception)
        {
            Error = exception.Message;
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            await browserPersistence.SaveAsync(ScenarioJsonText, CatalogJson, Results.GetPreferences());
            Notice = "Scenario saved in this browser.";
            Error = null;
        }
        catch (BrowserPersistenceException exception)
        {
            Error = $"{exception.Message} {exception.InnerException?.Message}";
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var saved = await browserPersistence.LoadAsync();
            if (saved is null)
            {
                Error = "No saved scenario was found in this browser.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(saved.CatalogJson))
            {
                CatalogJson = saved.CatalogJson;
                ApplyCatalog();
            }

            if (saved.Preferences is not null)
            {
                Results.ApplyPreferences(saved.Preferences);
            }

            ScenarioJsonText = saved.ScenarioJson;
            ReloadGuidedFields();
            RunJsonCore(advanceWorkingState: false);
            Notice = "Saved scenario loaded.";
        }
        catch (BrowserPersistenceException exception)
        {
            Error = $"{exception.Message} {exception.InnerException?.Message}";
        }
    }

    public async Task ExportAsync()
    {
        try
        {
            await browserPersistence.ExportAsync(ScenarioJsonText);
        }
        catch (BrowserPersistenceException exception)
        {
            Error = $"{exception.Message} {exception.InnerException?.Message}";
        }
    }

    public async Task ImportAsync(IBrowserFile file)
    {
        try
        {
            ScenarioJsonText = await BrowserScenarioPersistence.ReadImportAsync(file);
            ReloadGuidedFields();
            RunJsonCore(advanceWorkingState: false);
            Notice = $"Imported {file.Name}.";
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Error = exception.Message;
        }
    }

    public void ApplyCatalog()
    {
        try
        {
            activeConfiguration = scenarioJson.DeserializeConfiguration(CatalogJson);
            activeEngine = new CopilotUsageSimulationEngine(activeConfiguration);
            LoadTemplate(InitialExampleOperationId);
            Error = null;
            Notice = $"Catalog '{activeConfiguration.Version}' applied.";
        }
        catch (Exception exception) when (exception is JsonException or ConfigurationException)
        {
            Error = exception.Message;
            Notice = null;
        }
    }

    public void ResetCatalog()
    {
        activeConfiguration = defaultConfiguration;
        activeEngine = defaultEngine;
        CatalogJson = scenarioJson.SerializeConfiguration(defaultConfiguration);
        Error = null;
    }

    private string InitialExampleOperationId =>
        ActiveConfiguration.ExampleScenario.PreferredOperationId
        ?? ExampleOperations.FirstOrDefault()?.Id
        ?? ActiveConfiguration.Operations.FirstOrDefault()?.Id
        ?? throw new ConfigurationException("The active catalog does not define an operation.");

    private void RunJsonCore(bool advanceWorkingState)
    {
        try
        {
            RunScenario(scenarioJson.Deserialize(ScenarioJsonText), advanceWorkingState);
        }
        catch (Exception exception) when (exception is JsonException or SimulationException or InvalidOperationException)
        {
            Results.Clear();
            Error = exception is SimulationException simulation
                ? $"{simulation.Code}: {simulation.Message}"
                : exception.Message;
            Notice = null;
        }
    }

    private void RunScenario(SimulationScenario scenario, bool advanceWorkingState)
    {
        var session = sessionRunner.Run(activeEngine, scenario, Form.Workload.RepeatCount);
        Results.SetRuns(session.Runs);
        if (advanceWorkingState)
        {
            ScenarioJsonText = scenarioJson.Serialize(session.NextScenario);
            LoadForm(session.NextScenario);
            Notice = Results.CompletedRuns == 0
                ? "No balances changed because the first run was not allowed."
                : $"Working balances advanced by {Results.CompletedRuns} successful run(s).";
        }

        Error = null;
    }

    private void LoadForm(SimulationScenario scenario)
    {
        Form = editorAdapter.MapFromScenario(scenario, ActiveConfiguration);
    }
}
