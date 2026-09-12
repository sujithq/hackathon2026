using System.Text.Json.Nodes;
using AngleSharp.Dom;
using Bunit;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;
using CopilotUsageSimulator.Web.Pages;
using CopilotUsageSimulator.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotUsageSimulator.Web.Tests;

public sealed class CompassTests : BunitContext
{
    public CompassTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineConfigurationLoader.LoadDefault());
        Services.AddSingleton<ScenarioJson>();
        Services.AddSingleton<WorkloadEditorAdapter>();
        Services.AddSingleton<AttributionEditorAdapter>();
        Services.AddSingleton<EconomicEditorAdapter>();
        Services.AddSingleton<RuntimeEditorAdapter>();
        Services.AddSingleton<ActionsEditorAdapter>();
        Services.AddSingleton<ScenarioEditorAdapter>();
        Services.AddSingleton<CompassBundleCodec>();
        Services.AddScoped<CompassBrowserPersistence>();
        Services.AddScoped<CompassPageModel>();
    }

    [Fact]
    public void DefaultDemoShowsActualBlockerRequiredCreditsAndZeroAcceptedConsumption()
    {
        var cut = Render<Compass>();

        Assert.Equal(3, cut.FindAll("main.cp-workspace > section").Count);
        Assert.Contains("Blocked", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Contains("paid-usage", cut.Find("[data-testid=compass-blocker]").TextContent);
        Assert.Equal("100", cut.Find("[data-testid=required-credits]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=accepted-credits]").TextContent);
        Assert.Contains("60 / 60", cut.Markup);
        Assert.Contains("11 Sep 2026", cut.Markup);
        Assert.DoesNotContain("data-testid=\"compass-actions\"", cut.Markup);
    }

    [Fact]
    public void SimulateIsRepeatableAndDoesNotAdvanceBalances()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        var json = Services.GetRequiredService<ScenarioJson>();
        var before = json.Serialize(model.Scenario);

        cut.Find("#cc-simulate").Click();
        cut.Find("#cc-simulate").Click();

        Assert.Equal(before, json.Serialize(model.Scenario));
        Assert.Equal(5_740m, model.Scenario.EconomicGuardrails!.EnterprisePoolConsumedCredits);
        Assert.Equal("100", cut.Find("[data-testid=required-credits]").TextContent);
    }

    [Fact]
    public void EditingChildConfigurationInvalidatesParentVerdictAndComparisons()
    {
        var cut = Render<Compass>();
        cut.Find("#cc-compare").Click();

        cut.Find("#cc-output").Input("24000");

        Assert.Contains("Needs simulation", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=required-credits]"));
        Assert.Empty(cut.FindAll("[data-comparison]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("999999999999999999999999999999")]
    public void InvalidTokenInputDoesNotSilentlyReuseASuccessfulValue(string value)
    {
        var cut = Render<Compass>();
        cut.Find("#cc-paid").Change("Enabled");
        cut.Find("#cc-simulate").Click();
        Assert.Contains("Allowed", cut.Find("[data-testid=compass-verdict]").TextContent);

        cut.Find("#cc-input").Input(value);

        Assert.Equal("true", cut.Find("#cc-input").GetAttribute("aria-invalid"));
        Assert.True(cut.Find("#cc-simulate").HasAttribute("disabled"));
        Assert.Contains("Needs simulation", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.NotEmpty(cut.FindAll("[role=alert]"));

        cut.Find("#cc-input").Input("30000");
        Assert.False(cut.Find("#cc-simulate").HasAttribute("disabled"));
    }

    [Fact]
    public void FullyIncludedRequestIsNotBlockedMerelyBecausePaidUsageIsDisabled()
    {
        var cut = Render<Compass>();

        cut.Find("#cc-input").Input("30000");
        cut.Find("#cc-output").Input("24000");
        cut.Find("#cc-simulate").Click();

        Assert.Contains("Allowed", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Equal("60", cut.Find("[data-testid=required-credits]").TextContent);
        Assert.Equal("Disabled", cut.Find("#cc-paid").GetAttribute("value"));
    }

    [Fact]
    public void EnablingPaidUsageReevaluatesTheLaterBudgetInsteadOfPromisingSuccess()
    {
        var cut = Render<Compass>();
        cut.Find("#cc-budget-limit").Input("0.20");
        cut.Find("#cc-budget-used").Input("0");
        cut.Find("#cc-simulate").Click();
        Assert.Contains("paid-usage", cut.Find("[data-testid=compass-blocker]").TextContent);

        cut.Find("#cc-paid").Change("Enabled");
        cut.Find("#cc-simulate").Click();

        Assert.Contains("budget-cost-center", cut.Find("[data-testid=compass-blocker]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=accepted-credits]").TextContent);
        Assert.Equal("$0.4000", cut.Find("[data-testid=potential-usd]").TextContent);
    }

    [Fact]
    public void RaisingSpendingBudgetDoesNotResolveAnEarlierUserLimit()
    {
        var cut = Render<Compass>();
        cut.Find("#cc-problem").Change("UserBudget");
        cut.Find("#cc-budget-limit").Input("99999");
        cut.Find("#cc-simulate").Click();

        Assert.Contains("ulb-user-1", cut.Find("[data-testid=compass-blocker]").TextContent);
    }

    [Fact]
    public void AlternativesAreVerifiedAndAppliedOnlyToTheLocalDraft()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        var serializer = Services.GetRequiredService<ScenarioJson>();
        var baseline = serializer.Serialize(model.Scenario);

        cut.Find("#cc-compare").Click();

        Assert.Equal(baseline, serializer.Serialize(model.Scenario));
        var smaller = model.Comparisons.First(comparison =>
            comparison.Resolved &&
            comparison.CandidateScenario.EconomicGuardrails!.PaidUsage.State == GuardrailValue.Disabled);
        cut.Find($"[data-comparison='{smaller.Id}'] button").Click();

        Assert.Contains("Needs simulation", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.NotEqual(baseline, serializer.Serialize(model.Scenario));
        Assert.Equal(5_740m, model.Scenario.EconomicGuardrails!.EnterprisePoolConsumedCredits);
        cut.Find("#cc-simulate").Click();
        Assert.Contains("Allowed", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Equal("$0.0000", cut.Find("[data-testid=potential-usd]").TextContent);
    }

    [Fact]
    public void ExplicitlyOmittingAnInvalidOptionalControlRemovesItsValidationError()
    {
        var cut = Render<Compass>();
        cut.Find("#cc-ulb-limit").Input("");
        Assert.True(cut.Find("#cc-simulate").HasAttribute("disabled"));

        cut.Find("#cc-use-ulb").Change(false);

        Assert.False(cut.Find("#cc-simulate").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("#cc-ulb-limit"));
    }

    [Fact]
    public void CostReducingCandidateForAnAlreadyAllowedRequestIsNotLabeledBlocked()
    {
        var cut = Render<Compass>();
        cut.Find("#cc-paid").Change("Enabled");
        cut.Find("#cc-simulate").Click();

        cut.Find("#cc-compare").Click();

        var comparison = cut.Find("[data-comparison=reduce-to-included]");
        Assert.Contains("Allowed alternative", comparison.TextContent);
        Assert.DoesNotContain("Still limited", comparison.TextContent);
    }

    [Fact]
    public void CloudAgentAndCliPresetControlsUseDifferentMeters()
    {
        var cut = Render<Compass>();

        cut.Find("[data-preset=cloud-agent]").Click();
        Assert.Single(cut.FindAll("[data-testid=compass-actions]"));
        Assert.Contains("already-accounted", cut.Markup);
        cut.Find("#cc-simulate").Click();
        Assert.Contains("Allowed", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Equal("$0.0480", cut.Find("[data-testid=potential-usd]").TextContent);

        cut.Find("[data-preset=cli]").Click();
        Assert.Empty(cut.FindAll("[data-testid=compass-actions]"));
        cut.Find("#cc-simulate").Click();
        Assert.Contains("Allowed", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Equal("$0.0000", cut.Find("[data-testid=potential-usd]").TextContent);
    }

    [Fact]
    public void PlanAndUserSelectionKeepTheSameSeatIdentityAndOtherUsersRecords()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();

        cut.Find("#cc-user").Change("user-2");
        Assert.Equal("enterprise", model.Form.Workload.PlanId);
        Assert.False(model.Form.Economic.UseIndividualUlb);
        cut.Find("#cc-plan").Change("business");
        cut.Find("#cc-simulate").Click();

        Assert.Equal("user-2", model.Scenario.Attribution!.UserId);
        Assert.Equal("business", model.Scenario.PlanId);
        Assert.All(model.Scenario.BillingContext!.SeatAssignments, seat => Assert.Equal("business", seat.PlanId));
        Assert.Contains(model.Scenario.EconomicGuardrails!.UserLevelBudgets,
            budget => budget.Id == "ulb-user-1" && budget.TargetId == "user-1" && budget.ConsumedCredits == 100m);
    }

    [Fact]
    public void CostOnlyTraceExplicitlyExcludesAccessAndRuntime()
    {
        var cut = Render<Compass>();

        cut.Find("#cc-cost-only").Change(true);
        cut.Find("#cc-simulate").Click();

        Assert.NotEmpty(cut.FindAll("[data-state=Excluded]"));
        Assert.Contains("Excluded (cost-only)", cut.Markup);
        Assert.NotEmpty(cut.FindAll("[data-state=NotEvaluated]"));
    }

    [Fact]
    public void CostCenterEditsKeepTheDisplayedControlAndSelectedSeatAligned()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();

        cut.Find("#cc-cost-center").Input("cc-research");
        cut.Find("#cc-simulate").Click();

        Assert.Equal("cc-research", model.Scenario.Attribution!.DirectAssignments.Single().CostCenterId);
        Assert.Equal("cc-research", model.Scenario.BillingContext!.SeatAssignments.Single(seat => seat.UserId == "user-1").CostCenterId);
        Assert.Equal("cc-engineering", model.Scenario.BillingContext.SeatAssignments.Single(seat => seat.UserId == "user-2").CostCenterId);
        Assert.Equal("cc-research", model.Scenario.EconomicGuardrails!.SpendingBudgets.Single(budget => budget.Id == "budget-cost-center").ScopeId);
        Assert.Equal("cc-research", model.Preview!.Result!.Attribution!.CostCenterId);
    }

    [Fact]
    public void ExportCapturesUnsimulatedFormChangesAndMatchingReference()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        cut.Find("#cc-input").Input("12345");

        cut.Find("#cc-inspect-bundle").Click();

        var bundle = cut.Find("#cc-bundle-text").GetAttribute("value") ?? cut.Find("#cc-bundle-text").TextContent;
        var imported = Services.GetRequiredService<CompassBundleCodec>().Import(bundle, model.Configuration);
        Assert.Equal(12_345, imported.Scenario.Calls[0].FreshInputTokens);
        Assert.Equal(model.Configuration.Version, imported.Catalog.Version);
        Assert.Null(model.Preview);
    }

    [Fact]
    public void MalformedBundlePreservesScenarioAndCatalogButInvalidatesVerdict()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        var serializer = Services.GetRequiredService<ScenarioJson>();
        var before = serializer.Serialize(model.Scenario);
        var fingerprint = model.CatalogFingerprint;

        cut.Find("#cc-bundle-text").Input("{not valid");
        cut.Find("#cc-import-text").Click();

        Assert.Equal(before, serializer.Serialize(model.Scenario));
        Assert.Equal(fingerprint, model.CatalogFingerprint);
        Assert.Contains("preserved", cut.Find("[role=alert]").TextContent);
        Assert.Contains("Needs simulation", cut.Find("[data-testid=compass-verdict]").TextContent);
    }

    [Fact]
    public void MismatchedCatalogBundleCannotReplaceWorkingState()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        var originalCatalog = model.Configuration;
        var bundle = JsonNode.Parse(model.CreateBundle()!)!;
        bundle["catalog"]!["usdPerCredit"] = .02m;

        cut.Find("#cc-bundle-text").Input(bundle.ToJsonString());
        cut.Find("#cc-import-text").Click();

        Assert.Same(originalCatalog, model.Configuration);
        Assert.Contains("fingerprint", cut.Find("[role=alert]").TextContent);
        Assert.Null(model.Preview);
    }

    [Fact]
    public void ImportedMissingCallsRemainPartiallySimulatedInsteadOfManufacturingAFreeCall()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        ImportLegacy(cut, model.Scenario with { Calls = [] });

        cut.Find("#cc-simulate").Click();

        Assert.Empty(model.Scenario.Calls);
        Assert.Equal(SimulationDecision.PartiallySimulated, model.Preview!.Result!.Decision);
        Assert.Contains("Partially simulated", cut.Find("[data-testid=compass-verdict]").TextContent);
        Assert.Equal("Not priced", cut.Find("[data-testid=required-credits]").TextContent);
    }

    [Fact]
    public void ImportedMissingSeatIsNotAutomaticallyAssignedOnPreview()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        ImportLegacy(cut, model.Scenario with
        {
            BillingContext = model.Scenario.BillingContext! with
            {
                SeatAssignments = model.Scenario.BillingContext!.SeatAssignments
                    .Where(seat => seat.UserId != "user-1").ToArray()
            }
        });

        cut.Find("#cc-output").Input("24000");
        cut.Find("#cc-simulate").Click();

        Assert.DoesNotContain(model.Scenario.BillingContext!.SeatAssignments, seat => seat.UserId == "user-1");
        Assert.Equal(SimulationDecision.Indeterminate, model.Preview!.Result!.Decision);
        Assert.Equal("seat-assignment.missing", model.Preview.Result.FirstFailingGate);
    }

    [Fact]
    public void ImportedOrganizationAttributionDoesNotBecomeAnUnrequestedDirectAssignment()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        ImportLegacy(cut, model.Scenario with
        {
            Attribution = model.Scenario.Attribution! with
            {
                DirectAssignments = [],
                OrganizationAssignments =
                [
                    new EffectiveOrganizationCostCenterAssignment
                    {
                        OrganizationId = "org-engineering",
                        CostCenterId = "cc-engineering"
                    }
                ]
            }
        });

        cut.Find("#cc-budget-limit").Input("50");
        cut.Find("#cc-simulate").Click();

        Assert.Empty(model.Scenario.Attribution!.DirectAssignments);
        Assert.Equal(AttributionRule.LicensingOrganization, model.Preview!.Result!.Attribution!.Rule);
    }

    [Fact]
    public void ImportedMissingActionsPermissionsAreNotReplacedWithPassingDefaults()
    {
        var cut = Render<Compass>();
        var model = Services.GetRequiredService<CompassPageModel>();
        ImportLegacy(cut, CompassPresetFactory.Create(model.Configuration, "cloud-agent") with { ActionsGuardrails = null });
        cut.Find("#cc-actions-minutes").Input("20");

        cut.Find("#cc-simulate").Click();

        Assert.Null(model.Scenario.ActionsGuardrails);
        Assert.NotNull(model.Preview!.ErrorCode);
        Assert.Null(model.Preview.Result);
        Assert.Contains("No supported estimate", cut.Find("[data-testid=compass-verdict]").TextContent);
    }

    [Fact]
    public void ResetClearsInvalidFieldsAndRestoresPinnedDemoWithoutTouchingBrowserSave()
    {
        var cut = Render<Compass>();
        cut.Find("#cc-input").Input("");

        cut.Find("#cc-reset").Click();
        cut.Find("#cc-simulate").Click();

        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.False(cut.Find("#cc-simulate").HasAttribute("disabled"));
        Assert.Equal("100", cut.Find("[data-testid=required-credits]").TextContent);
        Assert.Contains("paid-usage", cut.Find("[data-testid=compass-blocker]").TextContent);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
    }

    [Fact]
    public void AllEditableControlsHaveAssociatedLabels()
    {
        var cut = Render<Compass>();

        foreach (var input in cut.FindAll("input, select, textarea"))
        {
            var id = input.Id;
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.NotEmpty(cut.FindAll($"label[for='{id}']"));
        }
    }

    [Fact]
    public void CompassOwnsRootAndOriginalSimulatorHasAdvancedRoute()
    {
        var compassRoutes = typeof(Compass).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>();
        var advancedRoutes = typeof(Home).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>();

        Assert.Contains(compassRoutes, route => route.Template == "/");
        Assert.Contains(compassRoutes, route => route.Template == "/compass");
        Assert.Contains(advancedRoutes, route => route.Template == "/advanced");
        Assert.DoesNotContain(advancedRoutes, route => route.Template == "/");
    }

    private void ImportLegacy(IRenderedComponent<Compass> cut, SimulationScenario scenario)
    {
        cut.Find("#cc-bundle-text").Input(Services.GetRequiredService<ScenarioJson>().Serialize(scenario));
        cut.Find("#cc-import-text").Click();
    }
}
