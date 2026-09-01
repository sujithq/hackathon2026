using AngleSharp.Dom;
using Bunit;
using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Web.Pages;
using CopilotUsageSimulator.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotUsageSimulator.Web.Tests;

public sealed class HomeTests : BunitContext
{
    public HomeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = EngineConfigurationLoader.LoadDefault();
        Services.AddSingleton(configuration);
        Services.AddSingleton<ICopilotUsageSimulationEngine>(
            new CopilotUsageSimulationEngine(configuration));
        Services.AddSingleton<ScenarioJson>();
    }

    [Fact]
    public void DefaultScenarioRendersAllowedInCostOnlyMode()
    {
        var cut = Render<Home>();

        Assert.True(cut.Find(".scope-card input[type=checkbox]").IsChecked());
        Assert.Contains("Allowed", cut.Find(".decision-banner").TextContent);
        Assert.DoesNotContain("Runtime guardrails", cut.Markup);
        Assert.Contains("Cost-related checks only", cut.Markup);
    }

    [Fact]
    public void DisablingCostOnlyModeRevealsRuntimeControls()
    {
        var cut = Render<Home>();

        cut.Find(".scope-card input[type=checkbox]").Change(false);

        Assert.Contains("Runtime guardrails", cut.Markup);
        Assert.Contains("Maximum model calls", cut.Markup);
        Assert.Contains("Agent runtime limits", cut.Markup);
    }

    [Fact]
    public void RepeatedSimulationAdvancesWorkingBalances()
    {
        var cut = Render<Home>();
        FindInputByLabel(cut, "Repeat task").Change("3");

        FindButton(cut, "Apply overrides and simulate").Click();

        Assert.Contains("3 / 3", cut.Markup);
        Assert.Contains("Working balances advanced by 3 successful run(s).", cut.Markup);
        Assert.Contains("Run history (3 evaluated)", cut.Markup);
    }

    [Fact]
    public void BlockingUlbHighlightsItsGuidedSetting()
    {
        var cut = Render<Home>();
        FindInputByLabel(cut, "Credit limit", "#individual-ulb-settings").Input("101");

        FindButton(cut, "Apply overrides and simulate").Click();

        var individualUlb = cut.Find("#individual-ulb-settings");
        Assert.True(individualUlb.ClassList.Contains("impacted-setting"));
        Assert.Contains("Why it stopped", cut.Markup);
        Assert.Contains("ulb-individual", cut.Find(".blocking-explanation").TextContent);
        Assert.Contains("Individual ULB", cut.Find(".blocking-explanation").TextContent);
        Assert.Contains(
            "The effective ULB for this user stopped the run.",
            cut.Find(".blocking-explanation").TextContent);
    }

    [Fact]
    public void InvalidRepeatCountShowsSimulationError()
    {
        var cut = Render<Home>();
        FindInputByLabel(cut, "Repeat task").Change("0");

        FindButton(cut, "Apply overrides and simulate").Click();

        var error = cut.Find(".message.error").TextContent;
        Assert.Contains("repeat-count-invalid", error);
        Assert.Contains("between 1 and 1000", error);
    }

    [Fact]
    public void DocumentationLinksUseOfficialGitHubPagesSafely()
    {
        var cut = Render<Home>();
        var links = cut.FindAll("a.docs-link");

        Assert.True(links.Count >= 20);
        Assert.All(links, link =>
        {
            var uri = new Uri(link.GetAttribute("href")!);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("docs.github.com", uri.Host);
            Assert.Equal("_blank", link.GetAttribute("target"));
            Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
            Assert.StartsWith("Open GitHub documentation for", link.GetAttribute("title"));
        });
    }

    [Fact]
    public void CostRelatedInputsAndGroupsAreMarked()
    {
        var cut = Render<Home>();
        var badges = cut.FindAll(".cost-related-badge");

        Assert.True(badges.Count >= 8);
        Assert.All(badges, badge => Assert.Equal("Cost related", badge.TextContent.Trim()));
        Assert.Contains("Cost related", cut.Find("label[for=operation]").TextContent);
        Assert.Contains("Cost related", cut.Find("label[for=plan]").TextContent);
        Assert.Contains("Cost related", cut.Find("label[for=model]").TextContent);
        Assert.DoesNotContain("Cost related", cut.Find("label[for=task]").TextContent);

        cut.Find(".scope-card input[type=checkbox]").Change(false);

        var runtimeHeading = cut.FindAll("h3")
            .Single(heading => heading.TextContent.Contains("Runtime guardrails", StringComparison.Ordinal));
        Assert.DoesNotContain("Cost related", runtimeHeading.TextContent);
    }

    [Fact]
    public void ChatTemplateHidesActionsSpecificInputs()
    {
        var cut = Render<Home>();

        FindButton(cut, "Chat").Click();

        Assert.Equal("chat", cut.Find("#operation").GetAttribute("value"));
        Assert.Contains("Allowed", cut.Find(".decision-banner").TextContent);
        Assert.Empty(cut.FindAll("#visibility"));
        Assert.Empty(cut.FindAll("#actions-minutes-settings"));
        Assert.DoesNotContain("Actions budget", cut.Markup);
        Assert.DoesNotContain("actions-budget", cut.Find(".check-list").TextContent);
    }

    public static IEnumerable<object[]> OperationCapabilities()
    {
        var configuration = EngineConfigurationLoader.LoadDefault();
        return configuration.Operations.Select(operation => new object[]
        {
            operation.Id,
            operation.IsBilled,
            operation.ActionsMetering != ActionsMeteringMode.None,
            operation.ActionsMetering == ActionsMeteringMode.PrivateRepositories
        });
    }

    [Theory]
    [MemberData(nameof(OperationCapabilities))]
    public void OperationSelectionShowsOnlyApplicableFields(
        string operationId,
        bool isBilled,
        bool usesActions,
        bool usesRepositoryVisibility)
    {
        var cut = Render<Home>();

        cut.Find("#operation").Change(operationId);

        Assert.Equal(isBilled, cut.FindAll("#model").Count == 1);
        Assert.Equal(isBilled, cut.FindAll("#individual-ulb-settings").Count == 1);
        Assert.Equal(isBilled, cut.Markup.Contains("Cost-center budget", StringComparison.Ordinal));
        Assert.Equal(usesActions, cut.FindAll("#actions-minutes-settings").Count == 1);
        Assert.Equal(usesActions, cut.Markup.Contains("Actions budget", StringComparison.Ordinal));
        Assert.Equal(usesRepositoryVisibility, cut.FindAll("#visibility").Count == 1);

        cut.Find(".scope-card input[type=checkbox]").Change(false);
        Assert.Equal(isBilled, cut.FindAll("#runtime-settings").Count == 1);
    }

    private static IElement FindInputByLabel(
        IRenderedComponent<Home> cut,
        string label,
        string scope = ".configuration-panel")
    {
        var field = cut.FindAll($"{scope} .field")
            .Single(element =>
                string.Equals(
                    element.QuerySelector("label")?.TextContent.Trim(),
                    label,
                    StringComparison.Ordinal));
        return field.QuerySelector("input")
            ?? throw new InvalidOperationException($"No input found for '{label}'.");
    }

    private static IElement FindButton(IRenderedComponent<Home> cut, string text) =>
        cut.FindAll("button")
            .Single(button =>
                string.Equals(button.TextContent.Trim(), text, StringComparison.Ordinal));
}
