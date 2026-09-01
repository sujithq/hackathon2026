namespace CopilotUsageSimulator.Engine.Simulation;

public sealed class SimulationSessionRunner
{
    public SimulationSessionResult Run(
        ICopilotUsageSimulationEngine engine,
        SimulationScenario scenario,
        int repeatCount)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(scenario);

        if (repeatCount is < 1 or > 1000)
        {
            throw new SimulationException("Repeat task must be between 1 and 1000.", "repeat-count-invalid");
        }

        var runs = new List<SimulationResult>();
        var current = scenario;
        for (var iteration = 0; iteration < repeatCount; iteration++)
        {
            var result = engine.Simulate(current);
            runs.Add(result);
            if (result.Decision != SimulationDecision.Allowed)
            {
                break;
            }

            current = AdvanceScenario(current, result);
        }

        return new SimulationSessionResult(runs, current);
    }

    private static SimulationScenario AdvanceScenario(
        SimulationScenario scenario,
        SimulationResult result)
    {
        var economic = scenario.EconomicGuardrails;
        if (economic is not null)
        {
            economic = economic with
            {
                EnterprisePoolConsumedCredits =
                    economic.EnterprisePoolConsumedCredits + result.Allocation.IncludedCredits,
                UserLevelBudgets = economic.UserLevelBudgets
                    .Select(budget => IdEquals(budget.Id, result.EffectiveUlb?.Id)
                        ? budget with
                        {
                            ConsumedCredits = budget.ConsumedCredits + result.Allocation.TotalCredits
                        }
                        : budget)
                    .ToArray(),
                IncludedUsageControls = economic.IncludedUsageControls
                    .Select(control => IdEquals(control.Id, result.Allocation.IncludedUsageControlId)
                        ? control with
                        {
                            ConsumedCredits = control.ConsumedCredits + result.Allocation.IncludedCredits
                        }
                        : control)
                    .ToArray(),
                SpendingBudgets = economic.SpendingBudgets
                    .Select(budget => ContainsId(result.Allocation.MeteredBudgetRemainingUsd, budget.Id)
                        ? budget with
                        {
                            ConsumedUsd = budget.ConsumedUsd + result.Allocation.MeteredUsd
                        }
                        : budget)
                    .ToArray()
            };
        }

        var runtime = scenario.RuntimeGuardrails;
        if (runtime is not null && scenario.CheckScope == SimulationCheckScope.All)
        {
            runtime = runtime with
            {
                ModelCallsConsumed = runtime.ModelCallsConsumed + scenario.Calls.Count,
                ElapsedDuration = runtime.ElapsedDuration + runtime.RequestedDuration,
                CliCreditsConsumed = runtime.CliCreditsConsumed + result.Allocation.TotalCredits
            };
        }

        var actions = scenario.ActionsGuardrails;
        if (actions is not null && result.ActionsUsage is not null)
        {
            actions = actions with
            {
                ConsumedIncludedMinutes =
                    actions.ConsumedIncludedMinutes + result.ActionsUsage.IncludedMinutes,
                Budgets = actions.Budgets
                    .Select(budget => budget with
                    {
                        ConsumedUsd = budget.ConsumedUsd + result.ActionsUsage.AdditionalUsd
                    })
                    .ToArray()
            };
        }

        return scenario with
        {
            EconomicGuardrails = economic,
            RuntimeGuardrails = runtime,
            ActionsGuardrails = actions
        };
    }

    private static bool IdEquals(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsId(
        IReadOnlyDictionary<string, decimal> values,
        string id) =>
        values.Keys.Any(key => IdEquals(key, id));
}

public sealed record SimulationSessionResult(
    IReadOnlyList<SimulationResult> Runs,
    SimulationScenario NextScenario)
{
    public int CompletedRuns => Runs.Count(run => run.Decision == SimulationDecision.Allowed);
}