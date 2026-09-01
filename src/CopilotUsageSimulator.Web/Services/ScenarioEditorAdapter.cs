using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Web.Services;

public sealed class ScenarioEditorAdapter(
    WorkloadEditorAdapter workload,
    AttributionEditorAdapter attribution,
    EconomicEditorAdapter economic,
    RuntimeEditorAdapter runtime,
    ActionsEditorAdapter actions)
{
    public ScenarioEditorState MapFromScenario(
        SimulationScenario scenario,
        EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(configuration);

        return new ScenarioEditorState
        {
            Workload = workload.MapFromScenario(scenario, configuration),
            Attribution = attribution.MapFromScenario(scenario),
            Economic = economic.MapFromScenario(scenario),
            Runtime = runtime.MapFromScenario(scenario),
            Actions = actions.MapFromScenario(scenario)
        };
    }

    public SimulationScenario ApplyToScenario(
        SimulationScenario scenario,
        ScenarioEditorState state,
        EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(configuration);

        var patched = workload.ApplyToScenario(scenario, state.Workload);
        patched = attribution.ApplyToScenario(patched, state.Attribution);
        patched = economic.ApplyToScenario(patched, state.Economic);
        patched = actions.ApplyToScenario(patched, state.Actions, configuration);
        return runtime.ApplyToScenario(patched, state.Runtime);
    }
}
