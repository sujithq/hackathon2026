using CopilotUsageSimulator.Engine.Guardrails;

namespace CopilotUsageSimulator.Engine.Simulation;

internal sealed class SimulationPipelineContext(
    SimulationScenario scenario,
    EconomicBalanceCalculator balances)
{
    public List<ExplanationEntry> Explanation { get; } = [];
    public List<string> Assumptions { get; } = [];
    public List<AppliedGuardrail> AppliedGuardrails { get; } = [];
    public List<ThresholdEvent> Alerts { get; } = [];
    public IReadOnlyList<ModelCallCharge> Calls { get; set; } = [];
    public CreditAllocation Allocation { get; set; } = new();
    public ActionsUsageResult? ActionsUsage { get; set; }
    public AttributionResult? Attribution { get; set; }
    public EffectiveUserLevelBudgetResult? EffectiveUlb { get; set; }
    public RemainingState? Remaining { get; set; }

    public SimulationResult Complete(
        SimulationDecision decision,
        string? failingGate = null) =>
        new()
        {
            Decision = decision,
            FirstFailingGate = failingGate,
            Calls = Calls,
            Allocation = Allocation,
            ActionsUsage = ActionsUsage,
            Attribution = Attribution,
            EffectiveUlb = EffectiveUlb,
            AppliedGuardrails = AppliedGuardrails,
            Alerts = Alerts,
            Remaining = Remaining ?? balances.CreateUnchangedRemaining(scenario),
            Assumptions = Assumptions,
            Explanation = Explanation
        };
}
