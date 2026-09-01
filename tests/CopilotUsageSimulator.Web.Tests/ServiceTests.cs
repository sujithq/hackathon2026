using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;
using CopilotUsageSimulator.Web.Services;

namespace CopilotUsageSimulator.Web.Tests;

public sealed class ServiceTests
{
    private readonly EngineConfiguration _configuration =
        EngineConfigurationLoader.LoadDefault();

    [Theory]
    [InlineData("cloud-agent", true)]
    [InlineData("code-review", true)]
    [InlineData("chat", false)]
    public void TemplatesProduceRunnableCostOnlyScenarios(
        string template,
        bool expectsActions)
    {
        var scenario = ExampleScenarioFactory.Create(_configuration, template);

        Assert.Equal(template, scenario.OperationId);
        Assert.Equal(SimulationCheckScope.CostRelatedOnly, scenario.CheckScope);
        Assert.Equal(expectsActions, scenario.ActionsUsage is not null);
        Assert.Equal(expectsActions, scenario.ActionsGuardrails is not null);
        Assert.NotEmpty(scenario.Calls);
        Assert.NotNull(scenario.BillingContext);
        Assert.NotNull(scenario.Attribution);
        Assert.NotNull(scenario.EconomicGuardrails);
    }

    [Fact]
    public void CustomCatalogDerivesExampleReferencesWithoutKnownIds()
    {
        var configuration = new EngineConfiguration
        {
            ExampleScenario = new ExampleScenarioDefinition
            {
                ProductId = "custom-product",
                SkuId = "custom-sku",
                PreferredOperationId = "custom-operation"
            },
            Plans =
            [
                new PlanDefinition
                {
                    Id = "custom-plan",
                    IsPooled = true,
                    IncludedCreditsPerUser = 1_000m
                }
            ],
            Operations =
            [
                new OperationDefinition
                {
                    Id = "custom-operation",
                    ActionsMetering = ActionsMeteringMode.Always,
                    ExampleLabel = "Custom operation",
                    ExampleTask = "Execute the custom operation."
                }
            ],
            Models =
            [
                new ModelDefinition
                {
                    Id = "custom-model",
                    PricePeriods =
                    [
                        new ModelPricePeriod
                        {
                            EffectiveFrom = DateTimeOffset.MinValue,
                            Tiers =
                            [
                                new TokenPriceTier
                                {
                                    Id = "custom-tier",
                                    InputUsdPerMillion = 1m,
                                    OutputUsdPerMillion = 1m
                                }
                            ]
                        }
                    ]
                }
            ],
            ActionsRunners =
            [
                new ActionsRunnerDefinition { Id = "custom-runner", UsdPerMinute = 0.01m }
            ]
        };
        var engine = new CopilotUsageSimulationEngine(configuration);

        var scenario = ExampleScenarioFactory.Create(configuration, "custom-operation");
        var result = engine.Simulate(scenario);

        Assert.Equal("custom-operation", scenario.OperationId);
        Assert.Equal("custom-plan", scenario.PlanId);
        Assert.Equal("custom-product", scenario.ProductId);
        Assert.Equal("custom-sku", scenario.SkuId);
        Assert.Equal("custom-model", Assert.Single(scenario.Calls).ModelId);
        Assert.Equal("custom-runner", scenario.ActionsUsage?.RunnerId);
        Assert.Equal("Execute the custom operation.", scenario.Metadata["task"]);
        Assert.Equal(SimulationDecision.Allowed, result.Decision);
    }

    [Fact]
    public void UnknownTemplateOperationIsRejectedInsteadOfCoerced()
    {
        var exception = Assert.Throws<ConfigurationException>(() =>
            ExampleScenarioFactory.Create(_configuration, "unknown-operation"));

        Assert.Contains("Unknown example operation", exception.Message);
    }

    [Fact]
    public void ScenarioJsonRoundTripsGuidedAndAdvancedState()
    {
        var serializer = new ScenarioJson();
        var original = ExampleScenarioFactory.Create(_configuration, "cloud-agent") with
        {
            CheckScope = SimulationCheckScope.All,
            Metadata = new Dictionary<string, string>
            {
                ["task"] = "Exercise serialization",
                ["repeatCount"] = "7"
            }
        };

        var roundTripped = serializer.Deserialize(serializer.Serialize(original));

        Assert.Equal(original.OperationId, roundTripped.OperationId);
        Assert.Equal(SimulationCheckScope.All, roundTripped.CheckScope);
        Assert.Equal("Exercise serialization", roundTripped.Metadata["task"]);
        Assert.Equal("7", roundTripped.Metadata["repeatCount"]);
        Assert.Equal(
            original.EconomicGuardrails!.EnterprisePoolConsumedCredits,
            roundTripped.EconomicGuardrails!.EnterprisePoolConsumedCredits);
        Assert.Equal(
            original.EconomicGuardrails.UserLevelBudgets,
            roundTripped.EconomicGuardrails.UserLevelBudgets);
        Assert.Equal(
            original.EconomicGuardrails.IncludedUsageControls,
            roundTripped.EconomicGuardrails.IncludedUsageControls);
        Assert.Equal(
            original.EconomicGuardrails.SpendingBudgets.Select(budget => (
                budget.Id,
                budget.Scope,
                budget.ScopeId,
                budget.LimitUsd,
                budget.ConsumedUsd,
                budget.Enforcement)),
            roundTripped.EconomicGuardrails.SpendingBudgets.Select(budget => (
                budget.Id,
                budget.Scope,
                budget.ScopeId,
                budget.LimitUsd,
                budget.ConsumedUsd,
                budget.Enforcement)));
        Assert.Equal(
            original.EconomicGuardrails.PaidUsage.State,
            roundTripped.EconomicGuardrails.PaidUsage.State);
        Assert.True(original.EconomicGuardrails.PaidUsage.ProductIds.SetEquals(
            roundTripped.EconomicGuardrails.PaidUsage.ProductIds));
        Assert.True(original.EconomicGuardrails.PaidUsage.SkuIds.SetEquals(
            roundTripped.EconomicGuardrails.PaidUsage.SkuIds));
    }

    [Fact]
    public void CatalogJsonRoundTripsDefaultConfiguration()
    {
        var serializer = new ScenarioJson();

        var roundTripped = serializer.DeserializeConfiguration(
            serializer.SerializeConfiguration(_configuration));

        Assert.Equal(_configuration.Version, roundTripped.Version);
        Assert.Equal(_configuration.Operations.Count, roundTripped.Operations.Count);
        Assert.Equal(_configuration.Models.Count, roundTripped.Models.Count);
        Assert.Equal(_configuration.Gates.Count, roundTripped.Gates.Count);
    }

    [Fact]
    public void ScenarioJsonRejectsExplicitNullCollectionsWithDomainError()
    {
        var serializer = new ScenarioJson();

        var exception = Assert.Throws<SimulationException>(() => serializer.Deserialize(
            """{"operationId":"chat","planId":"business","calls":null}"""));

        Assert.Equal(SimulationScenarioValidator.InvalidContractCode, exception.Code);
        Assert.Contains("calls", exception.Message);
    }

    [Fact]
    public void CatalogJsonRejectsExplicitNullCollectionsWithDomainError()
    {
        var serializer = new ScenarioJson();

        var exception = Assert.Throws<ConfigurationException>(() =>
            serializer.DeserializeConfiguration("""{"operations":null}"""));

        Assert.Equal(ConfigurationException.InvalidContractCode, exception.Code);
        Assert.Contains("operations", exception.Message);
    }

    [Fact]
    public void ScenarioEditorAdapterResolvesEffectiveScenarioValues()
    {
        var scenario = ExampleScenarioFactory.Create(_configuration, "cloud-agent") with
        {
            Metadata = new Dictionary<string, string>
            {
                ["task"] = "Map this scenario",
                ["repeatCount"] = "7"
            }
        };

        var state = CreateScenarioEditorAdapter().MapFromScenario(scenario, _configuration);

        Assert.Equal("Map this scenario", state.Workload.Task);
        Assert.Equal(7, state.Workload.RepeatCount);
        Assert.Equal(scenario.OperationId, state.Workload.OperationId);
        Assert.Equal(scenario.Attribution?.UserId, scenario.BillingContext?.SeatAssignments[0].UserId);
        Assert.NotNull(state.Attribution.DirectAssignmentIndex);
        Assert.NotEmpty(state.Attribution.CostCenterId);
    }

    [Fact]
    public void ScenarioEditorAdapterPreservesUnselectedAdvancedRecords()
    {
        var scenario = ExampleScenarioFactory.Create(_configuration, "cloud-agent");
        var historicalBudget = new SpendingBudget
        {
            Id = "historical-budget",
            Scope = SpendingBudgetScope.CostCenter,
            ScopeId = "historical-cost-center",
            LimitUsd = 99m,
            ConsumedUsd = 12m,
            EffectiveTo = scenario.Timestamp
        };
        scenario = scenario with
        {
            EconomicGuardrails = scenario.EconomicGuardrails! with
            {
                SpendingBudgets = [.. scenario.EconomicGuardrails.SpendingBudgets, historicalBudget]
            }
        };
        var adapter = CreateScenarioEditorAdapter();
        var state = adapter.MapFromScenario(scenario, _configuration);
        state.Workload.Task = "Updated task";
        state.Economic.PoolConsumed = 321m;

        var patched = adapter.ApplyToScenario(scenario, state, _configuration);

        Assert.Equal("Updated task", patched.Metadata["task"]);
        Assert.Equal(321m, patched.EconomicGuardrails!.EnterprisePoolConsumedCredits);
        Assert.Contains(patched.EconomicGuardrails.SpendingBudgets, budget =>
            budget == historicalBudget);
    }

    [Fact]
    public void ScenarioEditorAdapterAppliesSectionsInDependentOrder()
    {
        var scenario = ExampleScenarioFactory.Create(_configuration, "cloud-agent");
        var adapter = CreateScenarioEditorAdapter();
        var state = adapter.MapFromScenario(scenario, _configuration);
        state.Workload.Task = "Exercise every editor section";
        state.Workload.PlanId = "enterprise";
        state.Attribution.CostCenterId = "cost-center-updated";
        state.Attribution.OrganizationId = "organization-updated";
        state.Economic.PoolConsumed = 432m;
        state.Economic.UseOrganizationBudget = true;
        state.Economic.OrganizationBudgetId = "budget-organization";
        state.Actions.Minutes = 27m;
        state.Runtime.Enabled = true;
        state.Runtime.MaximumModelCalls = 12;

        var patched = adapter.ApplyToScenario(scenario, state, _configuration);

        Assert.Equal("Exercise every editor section", patched.Metadata["task"]);
        Assert.Equal("enterprise", patched.PlanId);
        Assert.Equal(
            "enterprise",
            Assert.Single(patched.BillingContext!.SeatAssignments, seat =>
                seat.UserId == patched.Attribution!.UserId).PlanId);
        Assert.Equal(
            "cost-center-updated",
            patched.Attribution!.DirectAssignments[state.Attribution.DirectAssignmentIndex!.Value].CostCenterId);
        Assert.Equal("organization-updated", patched.Attribution.LicensingOrganizationIds[
            state.Attribution.LicensingOrganizationIndex!.Value]);
        Assert.Equal(432m, patched.EconomicGuardrails!.EnterprisePoolConsumedCredits);
        Assert.Equal(
            "cost-center-updated",
            Assert.Single(patched.EconomicGuardrails.SpendingBudgets, budget =>
                budget.Id == state.Economic.CostCenterBudgetId).ScopeId);
        Assert.Equal(
            "organization-updated",
            Assert.Single(patched.EconomicGuardrails.SpendingBudgets, budget =>
                budget.Id == state.Economic.OrganizationBudgetId).ScopeId);
        Assert.Equal(27m, patched.ActionsUsage!.Minutes);
        Assert.Equal(12, patched.RuntimeGuardrails!.MaximumModelCalls);
    }

    [Fact]
    public void SectionAdaptersPreserveUnownedScenarioContracts()
    {
        var scenario = ExampleScenarioFactory.Create(_configuration, "cloud-agent");

        var workload = new WorkloadEditorAdapter();
        var workloadState = workload.MapFromScenario(scenario, _configuration);
        workloadState.Task = "Updated workload";
        var workloadPatched = workload.ApplyToScenario(scenario, workloadState);
        Assert.Same(scenario.Attribution, workloadPatched.Attribution);
        Assert.Same(scenario.EconomicGuardrails, workloadPatched.EconomicGuardrails);
        Assert.Same(scenario.ActionsUsage, workloadPatched.ActionsUsage);
        Assert.Same(scenario.ActionsGuardrails, workloadPatched.ActionsGuardrails);
        Assert.Same(scenario.RuntimeGuardrails, workloadPatched.RuntimeGuardrails);

        var attribution = new AttributionEditorAdapter();
        var attributionState = attribution.MapFromScenario(scenario);
        attributionState.CostCenterId = "cost-center-updated";
        var attributionPatched = attribution.ApplyToScenario(scenario, attributionState);
        Assert.Same(scenario.Calls, attributionPatched.Calls);
        Assert.Same(scenario.Metadata, attributionPatched.Metadata);
        Assert.Same(scenario.EconomicGuardrails, attributionPatched.EconomicGuardrails);
        Assert.Same(scenario.ActionsUsage, attributionPatched.ActionsUsage);
        Assert.Same(scenario.ActionsGuardrails, attributionPatched.ActionsGuardrails);
        Assert.Same(scenario.RuntimeGuardrails, attributionPatched.RuntimeGuardrails);

        var economic = new EconomicEditorAdapter();
        var economicState = economic.MapFromScenario(scenario);
        economicState.PoolConsumed = 321m;
        var economicPatched = economic.ApplyToScenario(scenario, economicState);
        Assert.Same(scenario.Calls, economicPatched.Calls);
        Assert.Same(scenario.Metadata, economicPatched.Metadata);
        Assert.Same(scenario.BillingContext, economicPatched.BillingContext);
        Assert.Same(scenario.Attribution, economicPatched.Attribution);
        Assert.Same(scenario.ActionsUsage, economicPatched.ActionsUsage);
        Assert.Same(scenario.ActionsGuardrails, economicPatched.ActionsGuardrails);
        Assert.Same(scenario.RuntimeGuardrails, economicPatched.RuntimeGuardrails);

        var actions = new ActionsEditorAdapter();
        var actionsState = actions.MapFromScenario(scenario);
        actionsState.Minutes = 27m;
        var actionsPatched = actions.ApplyToScenario(scenario, actionsState, _configuration);
        Assert.Same(scenario.Calls, actionsPatched.Calls);
        Assert.Same(scenario.Metadata, actionsPatched.Metadata);
        Assert.Same(scenario.BillingContext, actionsPatched.BillingContext);
        Assert.Same(scenario.Attribution, actionsPatched.Attribution);
        Assert.Same(scenario.EconomicGuardrails, actionsPatched.EconomicGuardrails);
        Assert.Same(scenario.RuntimeGuardrails, actionsPatched.RuntimeGuardrails);

        var runtime = new RuntimeEditorAdapter();
        var runtimeState = runtime.MapFromScenario(scenario);
        runtimeState.Enabled = true;
        runtimeState.MaximumModelCalls = 12;
        var runtimePatched = runtime.ApplyToScenario(scenario, runtimeState);
        Assert.Same(scenario.Calls, runtimePatched.Calls);
        Assert.Same(scenario.Metadata, runtimePatched.Metadata);
        Assert.Same(scenario.BillingContext, runtimePatched.BillingContext);
        Assert.Same(scenario.Attribution, runtimePatched.Attribution);
        Assert.Same(scenario.EconomicGuardrails, runtimePatched.EconomicGuardrails);
        Assert.Same(scenario.ActionsUsage, runtimePatched.ActionsUsage);
        Assert.Same(scenario.ActionsGuardrails, runtimePatched.ActionsGuardrails);
    }

    private static ScenarioEditorAdapter CreateScenarioEditorAdapter() =>
        new(
            new WorkloadEditorAdapter(),
            new AttributionEditorAdapter(),
            new EconomicEditorAdapter(),
            new RuntimeEditorAdapter(),
            new ActionsEditorAdapter());

    [Fact]
    public void SimulationResultsStateClearsResultAndRunHistory()
    {
        var engine = new CopilotUsageSimulationEngine(_configuration);
        var result = engine.Simulate(
            ExampleScenarioFactory.Create(_configuration, "cloud-agent"));
        var state = new SimulationResultsState();
        state.SetRuns([result]);

        state.Clear();

        Assert.Null(state.Result);
        Assert.Empty(state.Runs);
        Assert.Equal(0, state.CompletedRuns);
    }
}
