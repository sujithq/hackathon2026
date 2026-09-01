using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Engine.Tests;

public sealed class SimulationSessionRunnerTests
{
    [Fact]
    public void RepeatedRunsAdvanceAllWorkingBalancesUsingCaseInsensitiveIds()
    {
        var scenario = new SimulationScenario
        {
            OperationId = "operation",
            PlanId = "plan",
            CheckScope = SimulationCheckScope.All,
            Calls = [new ModelCallInput { ModelId = "model" }],
            EconomicGuardrails = new EconomicGuardrailSnapshot
            {
                EnterprisePoolConsumedCredits = 10m,
                UserLevelBudgets =
                [
                    new UserLevelBudget
                    {
                        Id = "USER-BUDGET",
                        LimitCredits = 100m,
                        ConsumedCredits = 1m
                    }
                ],
                IncludedUsageControls =
                [
                    new CostCenterIncludedUsageControl
                    {
                        Id = "control",
                        CostCenterId = "COST-CENTER",
                        ConsumedCredits = 2m
                    }
                ],
                SpendingBudgets =
                [
                    new SpendingBudget
                    {
                        Id = "SPENDING-BUDGET",
                        LimitUsd = 100m,
                        ConsumedUsd = 3m
                    }
                ]
            },
            RuntimeGuardrails = new RuntimeGuardrailSnapshot
            {
                ModelCallsConsumed = 4,
                ElapsedDuration = TimeSpan.FromMinutes(5),
                RequestedDuration = TimeSpan.FromMinutes(2),
                CliCreditsConsumed = 6m
            },
            ActionsGuardrails = new ActionsGuardrailSnapshot
            {
                IncludedMinutes = 100m,
                ConsumedIncludedMinutes = 7m,
                Budgets =
                [
                    new ActionsSpendingBudget
                    {
                        Id = "actions-budget",
                        LimitUsd = 100m,
                        ConsumedUsd = 8m
                    }
                ]
            }
        };
        var engine = new StubEngine(_ => AllowedResult());

        var session = new SimulationSessionRunner().Run(engine, scenario, repeatCount: 2);

        Assert.Equal(2, session.CompletedRuns);
        Assert.Equal(2, session.Runs.Count);
        Assert.Equal(16m, session.NextScenario.EconomicGuardrails!.EnterprisePoolConsumedCredits);
        Assert.Equal(11m, session.NextScenario.EconomicGuardrails.UserLevelBudgets.Single().ConsumedCredits);
        Assert.Equal(8m, session.NextScenario.EconomicGuardrails.IncludedUsageControls.Single().ConsumedCredits);
        Assert.Equal(11m, session.NextScenario.EconomicGuardrails.SpendingBudgets.Single().ConsumedUsd);
        Assert.Equal(6, session.NextScenario.RuntimeGuardrails!.ModelCallsConsumed);
        Assert.Equal(TimeSpan.FromMinutes(9), session.NextScenario.RuntimeGuardrails.ElapsedDuration);
        Assert.Equal(16m, session.NextScenario.RuntimeGuardrails.CliCreditsConsumed);
        Assert.Equal(15m, session.NextScenario.ActionsGuardrails!.ConsumedIncludedMinutes);
        Assert.Equal(12m, session.NextScenario.ActionsGuardrails.Budgets.Single().ConsumedUsd);
    }

    [Fact]
    public void StopsAtFirstDisallowedRunAndReturnsLastCommittedScenario()
    {
        var calls = 0;
        var engine = new StubEngine(_ => ++calls == 1
            ? AllowedResult()
            : new SimulationResult { Decision = SimulationDecision.Blocked });
        var scenario = new SimulationScenario
        {
            OperationId = "operation",
            PlanId = "plan",
            EconomicGuardrails = new EconomicGuardrailSnapshot
            {
                EnterprisePoolConsumedCredits = 10m
            }
        };

        var session = new SimulationSessionRunner().Run(engine, scenario, repeatCount: 5);

        Assert.Equal(2, session.Runs.Count);
        Assert.Equal(1, session.CompletedRuns);
        Assert.Equal(13m, session.NextScenario.EconomicGuardrails!.EnterprisePoolConsumedCredits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void RejectsRepeatCountsOutsideSupportedRange(int repeatCount)
    {
        var exception = Assert.Throws<SimulationException>(() =>
            new SimulationSessionRunner().Run(
                new StubEngine(_ => AllowedResult()),
                new SimulationScenario { OperationId = "operation", PlanId = "plan" },
                repeatCount));

        Assert.Equal("repeat-count-invalid", exception.Code);
    }

    private static SimulationResult AllowedResult() =>
        new()
        {
            Decision = SimulationDecision.Allowed,
            Allocation = new CreditAllocation
            {
                TotalCredits = 5m,
                IncludedCredits = 3m,
                MeteredUsd = 4m,
                MeteredBudgetRemainingUsd = new Dictionary<string, decimal>
                {
                    ["spending-budget"] = 50m
                }
            },
            EffectiveUlb = new EffectiveUserLevelBudgetResult
            {
                Id = "user-budget",
                LimitCredits = 100m,
                ReservedCredits = 5m,
                RemainingCredits = 95m
            },
            Attribution = new AttributionResult
            {
                UserId = "user",
                CostCenterId = "cost-center",
                Outcome = GuardrailOutcome.Passed,
                Explanation = "Resolved."
            },
            ActionsUsage = new ActionsUsageResult
            {
                RunnerId = "runner",
                IncludedMinutes = 4m,
                AdditionalUsd = 2m
            }
        };

    private sealed class StubEngine(Func<SimulationScenario, SimulationResult> simulate)
        : ICopilotUsageSimulationEngine
    {
        public EngineConfiguration Configuration { get; } = new();

        public SimulationResult Simulate(SimulationScenario scenario) => simulate(scenario);
    }
}
