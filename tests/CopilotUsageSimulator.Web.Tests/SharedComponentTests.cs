using Bunit;
using CopilotUsageSimulator.Web.Shared;

namespace CopilotUsageSimulator.Web.Tests;

public sealed class SharedComponentTests : BunitContext
{
    [Fact]
    public void DocsLinkProvidesAccessibleSafeExternalNavigation()
    {
        var cut = Render<DocsLink>(parameters => parameters
            .Add(component => component.Url, "https://docs.github.com/en/copilot/concepts/billing")
            .Add(component => component.Topic, "Copilot billing"));

        var link = cut.Find("a");
        Assert.Equal("Open GitHub documentation for Copilot billing", link.GetAttribute("aria-label"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
    }

    [Fact]
    public void BudgetCardToggleNotifiesParentAndRevealsFields()
    {
        var enabled = false;
        var cut = Render<BudgetCard>(parameters => parameters
            .Add(component => component.Title, "Enterprise budget")
            .Add(component => component.Help, "Enterprise spending limit.")
            .Add(component => component.Enabled, enabled)
            .Add(component => component.EnabledChanged, value => enabled = value));

        Assert.Empty(cut.FindAll(".budget-fields"));

        cut.Find("input[type=checkbox]").Change(true);
        cut.Render(parameters => parameters
            .Add(component => component.Title, "Enterprise budget")
            .Add(component => component.Help, "Enterprise spending limit.")
            .Add(component => component.Enabled, enabled)
            .Add(component => component.EnabledChanged, value => enabled = value));

        Assert.True(enabled);
        Assert.NotEmpty(cut.FindAll(".budget-fields"));
        Assert.Contains("Limit (USD)", cut.Markup);
    }
}
