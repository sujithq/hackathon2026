using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
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
