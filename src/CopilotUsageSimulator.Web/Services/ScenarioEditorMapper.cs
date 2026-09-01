using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Web.Services;

public sealed class ScenarioEditorMapper
{
    public ScenarioEditorState MapFromScenario(
        SimulationScenario scenario,
        EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(configuration);

        var call = scenario.Calls.FirstOrDefault();
        var directAssignments = scenario.Attribution?.DirectAssignments ?? [];
        var directAssignmentIndex = directAssignments
            .Select((assignment, index) => new { assignment, index })
            .Where(item =>
                scenario.Timestamp >= item.assignment.EffectiveFrom &&
                (item.assignment.EffectiveTo is null || scenario.Timestamp < item.assignment.EffectiveTo))
            .Select(item => (int?)item.index)
            .FirstOrDefault() ?? (directAssignments.Count > 0 ? 0 : null);
        var costCenterId = directAssignmentIndex is null
            ? null
            : directAssignments[directAssignmentIndex.Value].CostCenterId;
        var resolvedAttribution = scenario.Attribution is null
            ? null
            : new AttributionResolver().Resolve(scenario.Attribution, scenario.Timestamp);
        costCenterId = resolvedAttribution?.Outcome == GuardrailOutcome.Passed
            ? resolvedAttribution.CostCenterId
            : costCenterId;

        var applicability = new EconomicGuardrailApplicabilityResolver();
        var economic = scenario.EconomicGuardrails;
        var universalUlb = economic is null || resolvedAttribution is null
            ? null
            : applicability.ResolveUserLevelBudget(
                economic,
                resolvedAttribution,
                UserLevelBudgetKind.Universal,
                scenario.Timestamp).Value;
        var costCenterUlb = economic is null || resolvedAttribution is null
            ? null
            : applicability.ResolveUserLevelBudget(
                economic,
                resolvedAttribution,
                UserLevelBudgetKind.CostCenter,
                scenario.Timestamp).Value;
        var individualUlb = economic is null || resolvedAttribution is null
            ? null
            : applicability.ResolveUserLevelBudget(
                economic,
                resolvedAttribution,
                UserLevelBudgetKind.Individual,
                scenario.Timestamp).Value;
        var includedControl = economic is null || resolvedAttribution is null
            ? null
            : applicability.ResolveIncludedUsageControl(
                economic,
                resolvedAttribution,
                scenario.Timestamp).Value;
        var spendingBudgets = economic is null || resolvedAttribution is null
            ? []
            : applicability.ResolveSpendingBudgets(
                economic,
                resolvedAttribution,
                scenario.ProductId,
                scenario.SkuId,
                scenario.Timestamp);
        var enterpriseBudget = EconomicGuardrailApplicabilityResolver.Select(
            spendingBudgets.Where(budget => budget.Scope == SpendingBudgetScope.Enterprise)).Value;
        var costCenterBudget = EconomicGuardrailApplicabilityResolver.Select(
            spendingBudgets.Where(budget => budget.Scope == SpendingBudgetScope.CostCenter)).Value;
        var organizationBudget = EconomicGuardrailApplicabilityResolver.Select(
            spendingBudgets.Where(budget => budget.Scope == SpendingBudgetScope.Organization)).Value;
        var actionsBudget = scenario.ActionsGuardrails?.Budgets.FirstOrDefault();
        var organizationId = resolvedAttribution?.LicensingOrganizationId ??
            scenario.Attribution?.LicensingOrganizationIds.FirstOrDefault();
        var licensingOrganizationIndex = scenario.Attribution?.LicensingOrganizationIds
            .Select((id, index) => new { id, index })
            .Where(item => string.Equals(item.id, organizationId, StringComparison.OrdinalIgnoreCase))
            .Select(item => (int?)item.index)
            .FirstOrDefault();

        return new ScenarioEditorState
        {
            Task = scenario.Metadata.GetValueOrDefault("task", ""),
            OperationId = scenario.OperationId,
            PlanId = scenario.PlanId,
            CostChecksOnly = scenario.CheckScope == SimulationCheckScope.CostRelatedOnly,
            RepositoryVisibility = scenario.RepositoryVisibility,
            ModelId = call?.ModelId ?? configuration.Models.First().Id,
            ContextTokens = call?.ContextTokens ?? 0,
            FreshInputTokens = call?.FreshInputTokens ?? 0,
            CachedInputTokens = call?.CachedInputTokens ?? 0,
            CacheWriteTokens = call?.CacheWriteTokens ?? 0,
            OutputTokens = call?.OutputTokens ?? 0,
            CostCenterId = costCenterId ?? "",
            DirectAssignmentIndex = directAssignmentIndex,
            OrganizationId = organizationId ?? "",
            LicensingOrganizationIndex = licensingOrganizationIndex,
            PoolConsumed = economic?.EnterprisePoolConsumedCredits ?? 0,
            UseUniversalUlb = universalUlb is not null,
            UniversalUlbId = universalUlb?.Id,
            UniversalUlbLimit = universalUlb?.LimitCredits ?? 0,
            UniversalUlbConsumed = universalUlb?.ConsumedCredits ?? 0,
            UseCostCenterUlb = costCenterUlb is not null,
            CostCenterUlbId = costCenterUlb?.Id,
            CostCenterUlbLimit = costCenterUlb?.LimitCredits ?? 0,
            CostCenterUlbConsumed = costCenterUlb?.ConsumedCredits ?? 0,
            UseIndividualUlb = individualUlb is not null,
            IndividualUlbId = individualUlb?.Id,
            UlbLimit = individualUlb?.LimitCredits ?? 0,
            UlbConsumed = individualUlb?.ConsumedCredits ?? 0,
            UseIncludedControl = includedControl is not null,
            IncludedControlId = includedControl?.Id,
            IncludedControlConsumed = includedControl?.ConsumedCredits ?? 0,
            IncludedOverflow = includedControl?.OverflowBehavior ?? IncludedOverflowBehavior.PaidUsage,
            PaidUsage = economic?.PaidUsage.State ?? GuardrailValue.Unknown,
            UseCostCenterBudget = costCenterBudget is not null,
            CostCenterBudgetId = costCenterBudget?.Id,
            CostCenterBudgetLimit = costCenterBudget?.LimitUsd ?? 0,
            CostCenterBudgetConsumed = costCenterBudget?.ConsumedUsd ?? 0,
            CostCenterBudgetEnforcement = costCenterBudget?.Enforcement ?? GuardrailEnforcement.HardStop,
            UseOrganizationBudget = organizationBudget is not null,
            OrganizationBudgetId = organizationBudget?.Id,
            OrganizationBudgetLimit = organizationBudget?.LimitUsd ?? 0,
            OrganizationBudgetConsumed = organizationBudget?.ConsumedUsd ?? 0,
            OrganizationBudgetEnforcement = organizationBudget?.Enforcement ?? GuardrailEnforcement.HardStop,
            UseEnterpriseBudget = enterpriseBudget is not null,
            EnterpriseBudgetId = enterpriseBudget?.Id,
            EnterpriseBudgetLimit = enterpriseBudget?.LimitUsd ?? 0,
            EnterpriseBudgetConsumed = enterpriseBudget?.ConsumedUsd ?? 0,
            EnterpriseBudgetEnforcement = enterpriseBudget?.Enforcement ?? GuardrailEnforcement.HardStop,
            ActionsMinutes = scenario.ActionsUsage?.Minutes ?? 0,
            UseActionsBudget = actionsBudget is not null,
            ActionsBudgetId = actionsBudget?.Id,
            ActionsBudgetLimit = actionsBudget?.LimitUsd ?? 0,
            ActionsBudgetConsumed = actionsBudget?.ConsumedUsd ?? 0,
            ActionsBudgetEnforcement = actionsBudget?.Enforcement ?? GuardrailEnforcement.HardStop,
            UseRuntimeGuardrails = scenario.RuntimeGuardrails is not null,
            MaximumModelCalls = scenario.RuntimeGuardrails?.MaximumModelCalls,
            ModelCallsConsumed = scenario.RuntimeGuardrails?.ModelCallsConsumed ?? 0,
            MaximumSubagentDepth = scenario.RuntimeGuardrails?.MaximumSubagentDepth,
            RequestedSubagentDepth = scenario.RuntimeGuardrails?.RequestedSubagentDepth ?? 0,
            MaximumDurationMinutes = scenario.RuntimeGuardrails?.MaximumDuration is null
                ? null
                : (decimal)scenario.RuntimeGuardrails.MaximumDuration.Value.TotalMinutes,
            ElapsedDurationMinutes = (decimal)(scenario.RuntimeGuardrails?.ElapsedDuration.TotalMinutes ?? 0),
            RequestedDurationMinutes = (decimal)(scenario.RuntimeGuardrails?.RequestedDuration.TotalMinutes ?? 0),
            CliSoftCreditLimit = scenario.RuntimeGuardrails?.CliSoftCreditLimit,
            CliCreditsConsumed = scenario.RuntimeGuardrails?.CliCreditsConsumed ?? 0,
            RepeatCount = ReadRepeatCount(scenario.Metadata)
        };
    }

    private static int ReadRepeatCount(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("repeatCount", out var value) &&
        int.TryParse(value, out var count) &&
        count is >= 1 and <= 1000
            ? count
            : 1;
}
