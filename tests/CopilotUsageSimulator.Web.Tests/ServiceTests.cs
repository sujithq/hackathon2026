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
}
