namespace CopilotUsageSimulator.Engine.Simulation;

public static class SimulationScenarioValidator
{
    public const string InvalidContractCode = "scenario-contract-invalid";

    public static void Validate(SimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        Require(scenario.Calls, "calls");
        Require(scenario.AccessGates, "accessGates");
        Require(scenario.Metadata, "metadata");
        foreach (var gate in scenario.AccessGates)
        {
            Require(gate.Value, $"accessGates['{gate.Key}']");
        }

        for (var index = 0; index < scenario.Calls.Count; index++)
        {
            var call = Require(scenario.Calls[index], $"calls[{index}]");
            Require(call.EnabledMultiplierIds, $"calls[{index}].enabledMultiplierIds");
            Require(call.Metadata, $"calls[{index}].metadata");
            RequireItems(call.EnabledMultiplierIds, $"calls[{index}].enabledMultiplierIds");
        }

        if (scenario.BillingContext is not null)
        {
            Require(scenario.BillingContext.SeatAssignments, "billingContext.seatAssignments");
            RequireItems(scenario.BillingContext.SeatAssignments, "billingContext.seatAssignments");
        }

        if (scenario.Attribution is not null)
        {
            Require(scenario.Attribution.DirectAssignments, "attribution.directAssignments");
            Require(scenario.Attribution.TeamAssignments, "attribution.teamAssignments");
            Require(scenario.Attribution.LicensingOrganizationIds, "attribution.licensingOrganizationIds");
            Require(scenario.Attribution.OrganizationAssignments, "attribution.organizationAssignments");
            RequireItems(scenario.Attribution.DirectAssignments, "attribution.directAssignments");
            RequireItems(scenario.Attribution.TeamAssignments, "attribution.teamAssignments");
            RequireItems(
                scenario.Attribution.LicensingOrganizationIds,
                "attribution.licensingOrganizationIds");
            RequireItems(scenario.Attribution.OrganizationAssignments, "attribution.organizationAssignments");
        }

        if (scenario.EconomicGuardrails is not null)
        {
            var economic = scenario.EconomicGuardrails;
            Require(economic.UserLevelBudgets, "economicGuardrails.userLevelBudgets");
            Require(economic.IncludedUsageControls, "economicGuardrails.includedUsageControls");
            Require(economic.PaidUsage, "economicGuardrails.paidUsage");
            Require(economic.SpendingBudgets, "economicGuardrails.spendingBudgets");
            Require(
                economic.EnterpriseBudgetExcludedCostCenterIds,
                "economicGuardrails.enterpriseBudgetExcludedCostCenterIds");
            RequireItems(economic.UserLevelBudgets, "economicGuardrails.userLevelBudgets");
            RequireItems(economic.IncludedUsageControls, "economicGuardrails.includedUsageControls");
            RequireItems(economic.SpendingBudgets, "economicGuardrails.spendingBudgets");
            Require(economic.PaidUsage.ProductIds, "economicGuardrails.paidUsage.productIds");
            Require(economic.PaidUsage.SkuIds, "economicGuardrails.paidUsage.skuIds");
            RequireItems(
                economic.EnterpriseBudgetExcludedCostCenterIds,
                "economicGuardrails.enterpriseBudgetExcludedCostCenterIds");
            RequireItems(economic.PaidUsage.ProductIds, "economicGuardrails.paidUsage.productIds");
            RequireItems(economic.PaidUsage.SkuIds, "economicGuardrails.paidUsage.skuIds");

            for (var index = 0; index < economic.SpendingBudgets.Count; index++)
            {
                var budget = economic.SpendingBudgets[index];
                Require(budget.ProductIds, $"economicGuardrails.spendingBudgets[{index}].productIds");
                Require(budget.SkuIds, $"economicGuardrails.spendingBudgets[{index}].skuIds");
                RequireItems(
                    budget.ProductIds,
                    $"economicGuardrails.spendingBudgets[{index}].productIds");
                RequireItems(
                    budget.SkuIds,
                    $"economicGuardrails.spendingBudgets[{index}].skuIds");
            }
        }

        if (scenario.ActionsGuardrails is not null)
        {
            Require(scenario.ActionsGuardrails.Budgets, "actionsGuardrails.budgets");
            RequireItems(scenario.ActionsGuardrails.Budgets, "actionsGuardrails.budgets");
        }
    }

    private static T Require<T>(T? value, string path) where T : class =>
        value ?? throw Invalid(path);

    private static void RequireItems<T>(IEnumerable<T> values, string path) where T : class
    {
        var index = 0;
        foreach (var value in values)
        {
            Require(value, $"{path}[{index}]");
            index++;
        }
    }

    private static SimulationException Invalid(string path) =>
        new($"Scenario property '{path}' cannot be null.", InvalidContractCode);
}