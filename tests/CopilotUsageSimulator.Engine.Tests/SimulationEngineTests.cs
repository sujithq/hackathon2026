using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Engine.Tests;

public sealed class SimulationEngineTests
{
    private static readonly DateTimeOffset CatalogDate = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly CopilotUsageSimulationEngine _engine =
        new(EngineConfigurationLoader.LoadDefault());

    [Fact]
    public void DefaultCatalogLoadsCompleteCoreDefinitions()
    {
        Assert.Equal("2026-08-31", _engine.Configuration.Version);
        Assert.Equal(7, _engine.Configuration.Plans.Count);
        Assert.True(_engine.Configuration.Models.Count >= 30);
        Assert.Contains(_engine.Configuration.Operations, x => x.Id == "cloud-agent");
    }

    [Fact]
    public void CalculatesAllTokenClassesAndRetainsFractionalCredits()
    {
        var result = _engine.Simulate(Scenario(
            Call(
                model: "gpt-5.6-luna",
                fresh: 1_000_000,
                cached: 1_000_000,
                cacheWrite: 1_000_000,
                output: 1_000_000)));

        Assert.Equal(SimulationDecision.Allowed, result.Decision);
        Assert.Equal(1.67m, result.Calls.Single().RawUsd);
        Assert.Equal(167m, result.Allocation.TotalCredits);
        Assert.Equal(167m, result.Allocation.IncludedCredits);
    }

    [Fact]
    public void AppliesConfiguredMultipliersInRequestedOrder()
    {
        var call = Call(
            model: "gpt-5.6-luna",
            fresh: 1_000_000,
            multipliers:
            [
                "auto-model-selection",
                "data-residency-fedramp"
            ]);

        var result = _engine.Simulate(Scenario(call));

        Assert.Equal(0.198m, result.Calls.Single().AdjustedUsd);
        Assert.Equal(19.8m, result.Allocation.TotalCredits);
    }

    [Fact]
    public void SelectsLongContextTier()
    {
        var result = _engine.Simulate(Scenario(
            Call(model: "gpt-5.6-luna", context: 200_001, fresh: 1_000_000)));

        Assert.Equal("long-context", result.Calls.Single().PriceTierId);
        Assert.Equal(40m, result.Allocation.TotalCredits);
    }

    [Fact]
    public void StopsAtFirstFailingAccessGate()
    {
        var scenario = Scenario(Call()) with
        {
            AccessGates = new Dictionary<string, AccessGateState>
            {
                ["network"] = new()
                {
                    Passed = false,
                    Reason = "Proxy timeout",
                    Remediation = "Allow the Copilot endpoint."
                },
                ["authentication"] = new() { Passed = false, Reason = "Expired token" }
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Blocked, result.Decision);
        Assert.Equal("network", result.FirstFailingGate);
        Assert.Empty(result.Calls);
        Assert.Contains(result.Explanation, x => x.Message.Contains("Proxy timeout"));
    }

    [Fact]
    public void UsesCanonicalGateIdWhenStrictGateIsUnspecified()
    {
        var configuration = EngineConfigurationLoader.LoadDefault();
        var strictConfiguration = configuration with
        {
            Gates = configuration.Gates
                .Select(x => x.Id == "network" ? x with { PassWhenUnspecified = false } : x)
                .ToArray()
        };
        var engine = new CopilotUsageSimulationEngine(strictConfiguration);

        var result = engine.Simulate(Scenario(Call()));

        Assert.Equal(SimulationDecision.Blocked, result.Decision);
        Assert.Equal("network", result.FirstFailingGate);
    }

    [Fact]
    public void UserBudgetAlwaysBlocksBeforePoolAllocation()
    {
        var scenario = Scenario(Call(fresh: 1_000_000)) with
        {
            Budgets = new BudgetState
            {
                IncludedPoolCreditsRemaining = 1_000m,
                UserBudgets = [new() { Scope = UserBudgetScope.Individual, CreditsRemaining = 10m }]
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Blocked, result.Decision);
        Assert.Equal("budget.user", result.FirstFailingGate);
        Assert.Equal(1_000m, result.Remaining.IncludedPoolCredits);
    }

    [Fact]
    public void SplitsRequestBetweenPoolAndMeteredUsage()
    {
        var scenario = Scenario(Call(fresh: 1_000_000)) with
        {
            Budgets = new BudgetState
            {
                IncludedPoolCreditsRemaining = 10m,
                PaidUsageEnabled = true,
                EnterpriseId = "enterprise-1",
                MeteredBudgets =
                [
                    new()
                    {
                        Id = "enterprise-budget",
                        Scope = MeteredBudgetScope.Enterprise,
                        ScopeId = "enterprise-1",
                        UsdRemaining = 10m,
                        StopUsageWhenLimitReached = true
                    }
                ]
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Allowed, result.Decision);
        Assert.Equal(10m, result.Allocation.IncludedCredits);
        Assert.Equal(10m, result.Allocation.MeteredCredits);
        Assert.Equal(0.10m, result.Allocation.MeteredUsd);
        Assert.Equal(9.90m, result.Remaining.MeteredBudgetUsd);
    }

    [Fact]
    public void CanMeterEntireRequestWhenPoolCannotCoverIt()
    {
        var configuration = EngineConfigurationLoader.LoadDefault() with
        {
            PoolOverflowBehavior = PoolOverflowBehavior.MeterEntireRequest
        };
        var engine = new CopilotUsageSimulationEngine(configuration);
        var scenario = Scenario(Call(fresh: 1_000_000)) with
        {
            Budgets = new BudgetState
            {
                IncludedPoolCreditsRemaining = 10m,
                PaidUsageEnabled = true
            }
        };

        var result = engine.Simulate(scenario);

        Assert.Equal(0m, result.Allocation.IncludedCredits);
        Assert.Equal(20m, result.Allocation.MeteredCredits);
        Assert.Equal(10m, result.Remaining.IncludedPoolCredits);
    }

    [Fact]
    public void IncludedUsageControlCanBlockOverflow()
    {
        var scenario = Scenario(Call(fresh: 1_000_000)) with
        {
            Budgets = new BudgetState
            {
                IncludedPoolCreditsRemaining = 100m,
                PaidUsageEnabled = true,
                IncludedUsageControl = new()
                {
                    CreditsRemaining = 10m,
                    OverflowBehavior = IncludedUsageOverflowBehavior.Block
                }
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Blocked, result.Decision);
        Assert.Equal("budget.included-usage-control", result.FirstFailingGate);
    }

    [Fact]
    public void AlertOnlyMeteredBudgetAllowsOverspend()
    {
        var scenario = Scenario(Call(fresh: 1_000_000)) with
        {
            Budgets = new BudgetState
            {
                IncludedPoolCreditsRemaining = 0m,
                PaidUsageEnabled = true,
                EnterpriseId = "enterprise-1",
                MeteredBudgets =
                [
                    new()
                    {
                        Id = "alert-only",
                        Scope = MeteredBudgetScope.Enterprise,
                        ScopeId = "enterprise-1",
                        UsdRemaining = 0.05m,
                        StopUsageWhenLimitReached = false
                    }
                ]
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Allowed, result.Decision);
        Assert.Equal(-0.15m, result.Remaining.MeteredBudgetUsd);
    }

    [Fact]
    public void AddsActionsCostForPrivateRepositoryCodeReview()
    {
        var scenario = Scenario(Call(), operation: "code-review") with
        {
            ActionsUsage = new ActionsUsageInput
            {
                RunnerId = "linux-2-core",
                Minutes = 10m,
                IncludedMinutesRemaining = 4m
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.NotNull(result.ActionsUsage);
        Assert.Equal(6m, result.ActionsUsage.BillableMinutes);
        Assert.Equal(0.036m, result.ActionsUsage.AdditionalUsd);
        Assert.Equal(0m, result.Remaining.ActionsIncludedMinutes);
    }

    [Fact]
    public void UnbilledOperationDoesNotRequireModelCalls()
    {
        var result = _engine.Simulate(Scenario(operation: "code-completion"));

        Assert.Equal(SimulationDecision.Allowed, result.Decision);
        Assert.Empty(result.Calls);
        Assert.Equal(0m, result.Allocation.TotalCredits);
    }

    [Fact]
    public void UnbilledOperationIgnoresBillingAndRuntimeGuardrails()
    {
        var scenario = Scenario(operation: "code-completion") with
        {
            BillingContext = new BillingContext
            {
                BillingEntityId = "enterprise-1",
                CycleStart = CatalogDate.AddDays(-1),
                CycleEnd = CatalogDate.AddDays(1)
            },
            RuntimeGuardrails = new RuntimeGuardrailSnapshot
            {
                MaximumDuration = TimeSpan.Zero,
                RequestedDuration = TimeSpan.FromMinutes(1)
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Allowed, result.Decision);
        Assert.Null(result.FirstFailingGate);
        Assert.Empty(result.AppliedGuardrails);
    }

    [Fact]
    public void UnbilledOperationStillEnforcesAccessGatesInFullMode()
    {
        var scenario = Scenario(operation: "code-completion") with
        {
            AccessGates = new Dictionary<string, AccessGateState>
            {
                ["network"] = new() { Passed = false, Reason = "Network unavailable." }
            }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Blocked, result.Decision);
        Assert.Equal("network", result.FirstFailingGate);
    }

    [Fact]
    public void DerivesDefaultPoolFromPlanAllowance()
    {
        var scenario = Scenario(Call(fresh: 1_000_000)) with
        {
            Budgets = new BudgetState { PaidUsageEnabled = false }
        };

        var result = _engine.Simulate(scenario);

        Assert.Equal(SimulationDecision.Allowed, result.Decision);
        Assert.Equal(20m, result.Allocation.IncludedCredits);
        Assert.Equal(1_880m, result.Remaining.IncludedPoolCredits);
    }

    [Fact]
    public void RejectsDuplicateConfigurationIds()
    {
        var configuration = new EngineConfiguration
        {
            Operations =
            [
                new() { Id = "chat" },
                new() { Id = "CHAT" }
            ]
        };

        var exception = Assert.Throws<ConfigurationException>(
            () => EngineConfigurationValidator.Validate(configuration));

        Assert.Contains("Duplicate operation", exception.Message);
    }

    private static SimulationScenario Scenario(
        ModelCallInput? call = null,
        string operation = "chat") =>
        new()
        {
            OperationId = operation,
            PlanId = "business",
            Timestamp = CatalogDate,
            RepositoryVisibility = RepositoryVisibility.Private,
            Calls = call is null ? [] : [call],
            Budgets = new BudgetState
            {
                IncludedPoolCreditsRemaining = 10_000m,
                PaidUsageEnabled = false
            }
        };

    private static ModelCallInput Call(
        string model = "gpt-5.6-luna",
        long context = 1_000,
        long fresh = 1_000,
        long cached = 0,
        long cacheWrite = 0,
        long output = 0,
        IReadOnlyList<string>? multipliers = null) =>
        new()
        {
            ModelId = model,
            ContextTokens = context,
            FreshInputTokens = fresh,
            CachedInputTokens = cached,
            CacheWriteTokens = cacheWrite,
            OutputTokens = output,
            EnabledMultiplierIds = multipliers ?? []
        };
}
