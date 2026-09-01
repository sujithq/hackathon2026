using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Web.Services;

public sealed class ScenarioEditorPatcher(ScenarioEditorMapper mapper)
{
    public SimulationScenario ApplyToScenario(
        SimulationScenario scenario,
        ScenarioEditorState state,
        EngineConfiguration configuration)
    {
        var call = scenario.Calls.FirstOrDefault() ?? new ModelCallInput { ModelId = state.ModelId };
        call = call with
        {
            ModelId = state.ModelId,
            ContextTokens = state.ContextTokens,
            FreshInputTokens = state.FreshInputTokens,
            CachedInputTokens = state.CachedInputTokens,
            CacheWriteTokens = state.CacheWriteTokens,
            OutputTokens = state.OutputTokens
        };

        var metadata = new Dictionary<string, string>(scenario.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["task"] = state.Task,
            ["repeatCount"] = state.RepeatCount.ToString()
        };
        var licensingOrganizations = PatchAt(
            scenario.Attribution?.LicensingOrganizationIds ?? [],
            state.LicensingOrganizationIndex,
            !string.IsNullOrWhiteSpace(state.OrganizationId),
            _ => state.OrganizationId,
            () => state.OrganizationId);
        var directAssignments = PatchAt(
            scenario.Attribution?.DirectAssignments ?? [],
            state.DirectAssignmentIndex,
            !string.IsNullOrWhiteSpace(state.CostCenterId),
            assignment => assignment with { CostCenterId = state.CostCenterId },
            () => new EffectiveCostCenterAssignment { CostCenterId = state.CostCenterId });
        var attribution = scenario.Attribution is null
            ? null
            : scenario.Attribution with
            {
                LicensingOrganizationIds = licensingOrganizations,
                DirectAssignments = directAssignments
            };
        var billing = scenario.BillingContext is null
            ? null
            : scenario.BillingContext with
            {
                SeatAssignments = PatchEffectiveSeat(
                    scenario.BillingContext.SeatAssignments,
                    scenario.Attribution?.UserId,
                    scenario.Timestamp,
                    NullIfWhiteSpace(state.CostCenterId))
            };
        var operation = configuration.Operations.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, state.OperationId, StringComparison.OrdinalIgnoreCase));
        var operationDefaults = operation?.ActionsMetering != ActionsMeteringMode.None
            ? ExampleScenarioFactory.Create(configuration, state.OperationId)
            : null;
        var sourceActionsUsage = scenario.ActionsUsage ?? operationDefaults?.ActionsUsage;
        var actionsUsage = sourceActionsUsage is null
            ? null
            : sourceActionsUsage with { Minutes = state.ActionsMinutes };

        var patchedScenario = mapper.ApplyPlanSelection(
            scenario with
            {
                OperationId = state.OperationId,
                CheckScope = state.CostChecksOnly
                    ? SimulationCheckScope.CostRelatedOnly
                    : SimulationCheckScope.All,
                RepositoryVisibility = state.RepositoryVisibility,
                Calls = PatchFirst(scenario.Calls, true, _ => call, () => call),
                Metadata = metadata,
                Attribution = attribution,
                BillingContext = billing,
                EconomicGuardrails = UpdateEconomic(scenario.EconomicGuardrails, attribution?.UserId, state),
                ActionsUsage = actionsUsage,
                ActionsGuardrails = UpdateActionsGuardrails(
                    scenario.ActionsGuardrails ?? operationDefaults?.ActionsGuardrails,
                    state),
                RuntimeGuardrails = UpdateRuntimeGuardrails(scenario.RuntimeGuardrails, state)
            },
            state.PlanId,
            NullIfWhiteSpace(state.CostCenterId));
        CapturePatchedIds(patchedScenario, state);
        return patchedScenario;
    }

    private static EconomicGuardrailSnapshot? UpdateEconomic(
        EconomicGuardrailSnapshot? economic,
        string? userId,
        ScenarioEditorState state)
    {
        if (economic is null)
        {
            return null;
        }

        var budgets = PatchSpendingBudget(
            economic.SpendingBudgets,
            state.CostCenterBudgetId,
            state.UseCostCenterBudget,
            "budget-cost-center",
            SpendingBudgetScope.CostCenter,
            state.CostCenterId,
            state.CostCenterBudgetLimit,
            state.CostCenterBudgetConsumed,
            state.CostCenterBudgetEnforcement);
        budgets = PatchSpendingBudget(
            budgets,
            state.OrganizationBudgetId,
            state.UseOrganizationBudget,
            "budget-organization",
            SpendingBudgetScope.Organization,
            state.OrganizationId,
            state.OrganizationBudgetLimit,
            state.OrganizationBudgetConsumed,
            state.OrganizationBudgetEnforcement);
        budgets = PatchSpendingBudget(
            budgets,
            state.EnterpriseBudgetId,
            state.UseEnterpriseBudget,
            "budget-enterprise",
            SpendingBudgetScope.Enterprise,
            null,
            state.EnterpriseBudgetLimit,
            state.EnterpriseBudgetConsumed,
            state.EnterpriseBudgetEnforcement);

        var ulbs = PatchById(
            economic.UserLevelBudgets,
            state.UniversalUlbId,
            state.UseUniversalUlb,
            "ulb-universal",
            budget => budget.Id,
            budget => budget with
            {
                Kind = UserLevelBudgetKind.Universal,
                TargetId = null,
                LimitCredits = state.UniversalUlbLimit,
                ConsumedCredits = state.UniversalUlbConsumed
            },
            id => new UserLevelBudget
            {
                Id = id,
                Kind = UserLevelBudgetKind.Universal,
                LimitCredits = state.UniversalUlbLimit,
                ConsumedCredits = state.UniversalUlbConsumed
            });
        ulbs = PatchById(
            ulbs,
            state.CostCenterUlbId,
            state.UseCostCenterUlb && !string.IsNullOrWhiteSpace(state.CostCenterId),
            "ulb-cost-center",
            budget => budget.Id,
            budget => budget with
            {
                Kind = UserLevelBudgetKind.CostCenter,
                TargetId = state.CostCenterId,
                LimitCredits = state.CostCenterUlbLimit,
                ConsumedCredits = state.CostCenterUlbConsumed
            },
            id => new UserLevelBudget
            {
                Id = id,
                Kind = UserLevelBudgetKind.CostCenter,
                TargetId = state.CostCenterId,
                LimitCredits = state.CostCenterUlbLimit,
                ConsumedCredits = state.CostCenterUlbConsumed
            });
        ulbs = PatchById(
            ulbs,
            state.IndividualUlbId,
            state.UseIndividualUlb,
            "ulb-individual",
            budget => budget.Id,
            budget => budget with
            {
                Kind = UserLevelBudgetKind.Individual,
                TargetId = userId,
                LimitCredits = state.UlbLimit,
                ConsumedCredits = state.UlbConsumed
            },
            id => new UserLevelBudget
            {
                Id = id,
                Kind = UserLevelBudgetKind.Individual,
                TargetId = userId,
                LimitCredits = state.UlbLimit,
                ConsumedCredits = state.UlbConsumed
            });
        var includedControls = PatchById(
            economic.IncludedUsageControls,
            state.IncludedControlId,
            state.UseIncludedControl && !string.IsNullOrWhiteSpace(state.CostCenterId),
            "included-cost-center",
            control => control.Id,
            control => control with
            {
                CostCenterId = state.CostCenterId,
                ConsumedCredits = state.IncludedControlConsumed,
                OverflowBehavior = state.IncludedOverflow
            },
            id => new CostCenterIncludedUsageControl
            {
                Id = id,
                CostCenterId = state.CostCenterId,
                ConsumedCredits = state.IncludedControlConsumed,
                OverflowBehavior = state.IncludedOverflow
            });

        return economic with
        {
            EnterprisePoolConsumedCredits = state.PoolConsumed,
            PaidUsage = economic.PaidUsage with { State = state.PaidUsage },
            SpendingBudgets = budgets,
            UserLevelBudgets = ulbs,
            IncludedUsageControls = includedControls
        };
    }

    private static ActionsGuardrailSnapshot? UpdateActionsGuardrails(
        ActionsGuardrailSnapshot? snapshot,
        ScenarioEditorState state)
    {
        if (snapshot is null)
        {
            return null;
        }

        return snapshot with
        {
            Budgets = PatchById(
                snapshot.Budgets,
                state.ActionsBudgetId,
                state.UseActionsBudget,
                "actions-budget",
                budget => budget.Id,
                budget => budget with
                {
                    LimitUsd = state.ActionsBudgetLimit,
                    ConsumedUsd = state.ActionsBudgetConsumed,
                    Enforcement = state.ActionsBudgetEnforcement
                },
                id => new ActionsSpendingBudget
                {
                    Id = id,
                    LimitUsd = state.ActionsBudgetLimit,
                    ConsumedUsd = state.ActionsBudgetConsumed,
                    Enforcement = state.ActionsBudgetEnforcement
                })
        };
    }

    private static RuntimeGuardrailSnapshot? UpdateRuntimeGuardrails(
        RuntimeGuardrailSnapshot? snapshot,
        ScenarioEditorState state)
    {
        if (!state.UseRuntimeGuardrails)
        {
            return null;
        }

        return (snapshot ?? new RuntimeGuardrailSnapshot()) with
        {
            MaximumModelCalls = state.MaximumModelCalls,
            ModelCallsConsumed = state.ModelCallsConsumed,
            MaximumSubagentDepth = state.MaximumSubagentDepth,
            RequestedSubagentDepth = state.RequestedSubagentDepth,
            MaximumDuration = state.MaximumDurationMinutes is null
                ? null
                : TimeSpan.FromMinutes((double)state.MaximumDurationMinutes.Value),
            ElapsedDuration = TimeSpan.FromMinutes((double)state.ElapsedDurationMinutes),
            RequestedDuration = TimeSpan.FromMinutes((double)state.RequestedDurationMinutes),
            CliSoftCreditLimit = state.CliSoftCreditLimit,
            CliCreditsConsumed = state.CliCreditsConsumed
        };
    }

    private static IReadOnlyList<SpendingBudget> PatchSpendingBudget(
        IReadOnlyList<SpendingBudget> budgets,
        string? selectedId,
        bool enabled,
        string defaultId,
        SpendingBudgetScope scope,
        string? scopeId,
        decimal limit,
        decimal consumed,
        GuardrailEnforcement enforcement) =>
        PatchById(
            budgets,
            selectedId,
            enabled,
            defaultId,
            budget => budget.Id,
            budget => budget with
            {
                Scope = scope,
                ScopeId = scopeId,
                LimitUsd = limit,
                ConsumedUsd = consumed,
                Enforcement = enforcement
            },
            id => new SpendingBudget
            {
                Id = id,
                Scope = scope,
                ScopeId = scopeId,
                LimitUsd = limit,
                ConsumedUsd = consumed,
                Enforcement = enforcement
            });

    private static IReadOnlyList<T> PatchById<T>(
        IReadOnlyList<T> values,
        string? selectedId,
        bool enabled,
        string defaultId,
        Func<T, string> idSelector,
        Func<T, T> update,
        Func<string, T> create)
    {
        var patched = values.ToList();
        var index = string.IsNullOrWhiteSpace(selectedId)
            ? -1
            : patched.FindIndex(value =>
                string.Equals(idSelector(value), selectedId, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            if (enabled)
            {
                patched[index] = update(patched[index]);
            }
            else
            {
                patched.RemoveAt(index);
            }
        }
        else if (enabled)
        {
            patched.Add(create(defaultId));
        }

        return patched;
    }

    private static IReadOnlyList<T> PatchFirst<T>(
        IReadOnlyList<T> values,
        bool enabled,
        Func<T, T> update,
        Func<T> create)
    {
        var patched = values.ToList();
        if (patched.Count > 0)
        {
            if (enabled)
            {
                patched[0] = update(patched[0]);
            }
            else
            {
                patched.RemoveAt(0);
            }
        }
        else if (enabled)
        {
            patched.Add(create());
        }

        return patched;
    }

    private static IReadOnlyList<T> PatchAt<T>(
        IReadOnlyList<T> values,
        int? selectedIndex,
        bool enabled,
        Func<T, T> update,
        Func<T> create)
    {
        var patched = values.ToList();
        if (selectedIndex is >= 0 && selectedIndex < patched.Count)
        {
            if (enabled)
            {
                patched[selectedIndex.Value] = update(patched[selectedIndex.Value]);
            }
            else
            {
                patched.RemoveAt(selectedIndex.Value);
            }
        }
        else if (enabled)
        {
            patched.Add(create());
        }

        return patched;
    }

    private static IReadOnlyList<EffectiveSeatAssignment> PatchEffectiveSeat(
        IReadOnlyList<EffectiveSeatAssignment> seats,
        string? userId,
        DateTimeOffset timestamp,
        string? costCenterId)
    {
        var patched = seats.ToList();
        var index = patched.FindIndex(seat =>
            string.Equals(seat.UserId, userId, StringComparison.OrdinalIgnoreCase) &&
            timestamp >= seat.EffectiveFrom &&
            (seat.EffectiveTo is null || timestamp < seat.EffectiveTo));
        if (index >= 0)
        {
            patched[index] = patched[index] with { CostCenterId = costCenterId };
        }

        return patched;
    }

    private static void CapturePatchedIds(
        SimulationScenario scenario,
        ScenarioEditorState state)
    {
        state.LicensingOrganizationIndex = ResolvePatchedIndex(
            scenario.Attribution?.LicensingOrganizationIds.Count ?? 0,
            state.LicensingOrganizationIndex,
            !string.IsNullOrWhiteSpace(state.OrganizationId));
        state.DirectAssignmentIndex = ResolvePatchedIndex(
            scenario.Attribution?.DirectAssignments.Count ?? 0,
            state.DirectAssignmentIndex,
            !string.IsNullOrWhiteSpace(state.CostCenterId));

        var economic = scenario.EconomicGuardrails;
        if (economic is not null)
        {
            state.UniversalUlbId = ResolvePatchedId(
                economic.UserLevelBudgets,
                state.UniversalUlbId,
                state.UseUniversalUlb,
                "ulb-universal",
                budget => budget.Id);
            state.CostCenterUlbId = ResolvePatchedId(
                economic.UserLevelBudgets,
                state.CostCenterUlbId,
                state.UseCostCenterUlb,
                "ulb-cost-center",
                budget => budget.Id);
            state.IndividualUlbId = ResolvePatchedId(
                economic.UserLevelBudgets,
                state.IndividualUlbId,
                state.UseIndividualUlb,
                "ulb-individual",
                budget => budget.Id);
            state.IncludedControlId = ResolvePatchedId(
                economic.IncludedUsageControls,
                state.IncludedControlId,
                state.UseIncludedControl,
                "included-cost-center",
                control => control.Id);
            state.CostCenterBudgetId = ResolvePatchedId(
                economic.SpendingBudgets,
                state.CostCenterBudgetId,
                state.UseCostCenterBudget,
                "budget-cost-center",
                budget => budget.Id);
            state.OrganizationBudgetId = ResolvePatchedId(
                economic.SpendingBudgets,
                state.OrganizationBudgetId,
                state.UseOrganizationBudget,
                "budget-organization",
                budget => budget.Id);
            state.EnterpriseBudgetId = ResolvePatchedId(
                economic.SpendingBudgets,
                state.EnterpriseBudgetId,
                state.UseEnterpriseBudget,
                "budget-enterprise",
                budget => budget.Id);
        }

        if (scenario.ActionsGuardrails is not null)
        {
            state.ActionsBudgetId = ResolvePatchedId(
                scenario.ActionsGuardrails.Budgets,
                state.ActionsBudgetId,
                state.UseActionsBudget,
                "actions-budget",
                budget => budget.Id);
        }
    }

    private static string? ResolvePatchedId<T>(
        IReadOnlyList<T> values,
        string? selectedId,
        bool enabled,
        string defaultId,
        Func<T, string> idSelector)
    {
        if (!enabled)
        {
            return null;
        }

        return values
            .Select(idSelector)
            .FirstOrDefault(id => string.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? values
                .Select(idSelector)
                .FirstOrDefault(id => string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase));
    }

    private static int? ResolvePatchedIndex(int count, int? selectedIndex, bool enabled)
    {
        if (!enabled || count == 0)
        {
            return null;
        }

        return selectedIndex is >= 0 && selectedIndex < count
            ? selectedIndex
            : count - 1;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
