using CopilotUsageSimulator.Common.Guardrails;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Engine.Guardrails;

public sealed class EconomicGuardrailEvaluator(EngineConfiguration configuration)
{
    private static readonly decimal[] AlertThresholds = [75m, 90m, 100m];
    private readonly EconomicGuardrailApplicabilityResolver _applicability = new();

    public EconomicGuardrailEvaluation Evaluate(
        SimulationScenario scenario,
        AttributionResult attribution,
        decimal requestedCredits)
    {
        var billing = scenario.BillingContext
            ?? throw new SimulationException("BillingContext is required for rich guardrail evaluation.", "billing-context-required");
        var snapshot = scenario.EconomicGuardrails
            ?? throw new SimulationException("EconomicGuardrails is required for rich guardrail evaluation.", "economic-guardrails-required");
        var applied = new List<AppliedGuardrail>();
        var alerts = new List<ThresholdEvent>();

        if (scenario.Timestamp < billing.CycleStart || scenario.Timestamp >= billing.CycleEnd)
        {
            return EconomicGuardrailEvaluation.Stop(
                SimulationDecision.Indeterminate,
                "billing-cycle.timestamp",
                applied,
                alerts,
                new RemainingState(),
                message: "The simulation timestamp is outside the supplied billing cycle.");
        }

        var poolEntitlement = CalculatePoolEntitlement(billing, scenario.Timestamp);
        if (poolEntitlement is null)
        {
            var inventoryControl = _applicability.ResolveIncludedUsageControl(
                snapshot,
                attribution,
                scenario.Timestamp);
            var failingGate = inventoryControl is { IsAmbiguous: false, Value: not null } &&
                CalculateCostCenterEntitlement(
                    billing,
                    attribution.CostCenterId!,
                    scenario.Timestamp) is null
                    ? "included-control.seat-inventory"
                    : "pool.seat-inventory";
            return EconomicGuardrailEvaluation.Stop(
                SimulationDecision.Indeterminate,
                failingGate,
                applied,
                alerts,
                new RemainingState(),
                message: failingGate == "included-control.seat-inventory"
                    ? "The cost-center seat inventory contains an unknown plan allowance."
                    : "An active pooled seat references a plan with an unknown included-credit allowance.");
        }

        var poolRemaining = Math.Max(0m, poolEntitlement.Value - snapshot.EnterprisePoolConsumedCredits);
        var unchangedRemaining = new RemainingState { IncludedPoolCredits = poolRemaining };
        var ulbResolution = _applicability.ResolveEffectiveUserLevelBudget(
            snapshot,
            attribution,
            scenario.Timestamp);
        if (ulbResolution.IsAmbiguous)
        {
            return EconomicGuardrailEvaluation.Stop(
                SimulationDecision.Indeterminate,
                "ulb.ambiguous",
                applied,
                alerts,
                unchangedRemaining,
                message: "Multiple effective ULBs of the same precedence apply.");
        }

        EffectiveUserLevelBudgetResult? effectiveUlb = null;
        if (ulbResolution.Value is not null)
        {
            var budget = ulbResolution.Value;
            var remaining = budget.LimitCredits - budget.ConsumedCredits;
            var blocked = requestedCredits > remaining;
            applied.Add(new AppliedGuardrail
            {
                Id = budget.Id,
                MetadataKey = budget.Kind switch
                {
                    UserLevelBudgetKind.Individual => GuardrailMetadataKeys.UlbIndividual,
                    UserLevelBudgetKind.CostCenter => GuardrailMetadataKeys.UlbCostCenter,
                    _ => GuardrailMetadataKeys.UlbUniversal
                },
                Category = GuardrailCategories.UserLevelBudget,
                Enforcement = GuardrailEnforcement.HardStop,
                Outcome = blocked ? GuardrailOutcome.Blocked : GuardrailOutcome.Passed,
                Limit = budget.LimitCredits,
                ConsumedBefore = budget.ConsumedCredits,
                Requested = requestedCredits,
                RemainingAfter = blocked ? remaining : remaining - requestedCredits,
                Message = blocked
                    ? "The effective user-level budget cannot cover the request."
                    : "The effective user-level budget reserved the request credits."
            });
            effectiveUlb = new EffectiveUserLevelBudgetResult
            {
                Id = budget.Id,
                Kind = budget.Kind,
                LimitCredits = budget.LimitCredits,
                ConsumedBeforeCredits = budget.ConsumedCredits,
                ReservedCredits = blocked ? 0m : requestedCredits,
                RemainingCredits = blocked ? remaining : remaining - requestedCredits
            };
            unchangedRemaining = unchangedRemaining with
            {
                EffectiveUserBudgetCredits = remaining
            };
            if (blocked)
            {
                return EconomicGuardrailEvaluation.Stop(
                    SimulationDecision.Blocked,
                    budget.Id,
                    applied,
                    alerts,
                    unchangedRemaining,
                    effectiveUlb);
            }
        }

        var includedAvailable = poolRemaining;
        var includedControl = _applicability.ResolveIncludedUsageControl(
            snapshot,
            attribution,
            scenario.Timestamp);
        if (includedControl.IsAmbiguous)
        {
            return EconomicGuardrailEvaluation.Stop(
                SimulationDecision.Indeterminate,
                "included-control.ambiguous",
                applied,
                alerts,
                unchangedRemaining,
                effectiveUlb,
                "Multiple included-usage controls apply.");
        }

        decimal? controlRemaining = null;
        if (includedControl.Value is not null)
        {
            var costCenterEntitlement = CalculateCostCenterEntitlement(
                billing,
                attribution.CostCenterId!,
                scenario.Timestamp);
            if (costCenterEntitlement is null)
            {
                return EconomicGuardrailEvaluation.Stop(
                    SimulationDecision.Indeterminate,
                    "included-control.seat-inventory",
                    applied,
                    alerts,
                    unchangedRemaining,
                    effectiveUlb,
                    "The cost-center seat inventory contains an unknown plan allowance.");
            }

            controlRemaining = Math.Max(0m, costCenterEntitlement.Value - includedControl.Value.ConsumedCredits);
            unchangedRemaining = unchangedRemaining with
            {
                IncludedUsageControlCredits = controlRemaining
            };
            includedAvailable = Math.Min(includedAvailable, controlRemaining.Value);
            var controlBlocks = requestedCredits > controlRemaining &&
                includedControl.Value.OverflowBehavior == IncludedOverflowBehavior.Block;
            applied.Add(new AppliedGuardrail
            {
                Id = includedControl.Value.Id,
                MetadataKey = GuardrailMetadataKeys.IncludedUsageControl,
                Category = GuardrailCategories.IncludedUsageControl,
                Enforcement = controlBlocks ? GuardrailEnforcement.HardStop : GuardrailEnforcement.ObserveOnly,
                Outcome = controlBlocks ? GuardrailOutcome.Blocked : GuardrailOutcome.Passed,
                Limit = costCenterEntitlement,
                ConsumedBefore = includedControl.Value.ConsumedCredits,
                Requested = requestedCredits,
                RemainingAfter = controlBlocks
                    ? controlRemaining
                    : Math.Max(0m, controlRemaining.Value - Math.Min(requestedCredits, includedAvailable)),
                Message = controlBlocks
                    ? "The cost-center included-usage control blocks overflow."
                    : "The cost-center included-usage control permits this allocation or paid overflow."
            });
            if (controlBlocks)
            {
                return EconomicGuardrailEvaluation.Stop(
                    SimulationDecision.Blocked,
                    includedControl.Value.Id,
                    applied,
                    alerts,
                    unchangedRemaining,
                    effectiveUlb);
            }
        }

        var split = configuration.PoolOverflowBehavior == PoolOverflowBehavior.Split;
        var includedCredits = requestedCredits <= includedAvailable
            ? requestedCredits
            : split ? includedAvailable : 0m;
        var meteredCredits = requestedCredits - includedCredits;
        var meteredUsd = meteredCredits * configuration.UsdPerCredit;

        applied.Add(new AppliedGuardrail
        {
            Id = "enterprise-shared-pool",
            MetadataKey = GuardrailMetadataKeys.IncludedPool,
            Category = GuardrailCategories.IncludedPool,
            Enforcement = GuardrailEnforcement.ObserveOnly,
            Outcome = GuardrailOutcome.Passed,
            Limit = poolEntitlement,
            ConsumedBefore = snapshot.EnterprisePoolConsumedCredits,
            Requested = includedCredits,
            RemainingAfter = poolRemaining - includedCredits,
            Message = $"Allocated {includedCredits:G29} credits from the enterprise pool."
        });

        if (meteredCredits > 0)
        {
            if (!EconomicGuardrailApplicabilityResolver.Matches(
                    snapshot.PaidUsage.ProductIds,
                    scenario.ProductId) ||
                !EconomicGuardrailApplicabilityResolver.Matches(
                    snapshot.PaidUsage.SkuIds,
                    scenario.SkuId))
            {
                return EconomicGuardrailEvaluation.Stop(
                    SimulationDecision.Blocked,
                    "paid-usage.not-applicable",
                    applied,
                    alerts,
                    unchangedRemaining,
                    effectiveUlb,
                    "Paid usage is not authorized for this product and SKU.");
            }

            if (snapshot.PaidUsage.State == GuardrailValue.Unknown)
            {
                return EconomicGuardrailEvaluation.Stop(
                    SimulationDecision.Indeterminate,
                    "paid-usage.unknown",
                    applied,
                    alerts,
                    unchangedRemaining,
                    effectiveUlb,
                    "Paid-usage authorization is unknown.");
            }

            if (snapshot.PaidUsage.State == GuardrailValue.Disabled)
            {
                applied.Add(AuthorizationGuardrail(GuardrailOutcome.Blocked));
                return EconomicGuardrailEvaluation.Stop(
                    SimulationDecision.Blocked,
                    "paid-usage",
                    applied,
                    alerts,
                    unchangedRemaining,
                    effectiveUlb);
            }

            applied.Add(AuthorizationGuardrail(GuardrailOutcome.Passed));
        }

        var applicableBudgets = meteredCredits == 0
            ? []
            : _applicability.ResolveSpendingBudgets(
                snapshot,
                attribution,
                scenario.ProductId,
                scenario.SkuId,
                scenario.Timestamp);
        var budgetAlerts = new List<ThresholdEvent>();
        var budgetRemaining = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        SpendingBudget? blockingBudget = null;
        decimal? lowestHeadroom = null;

        foreach (var budget in applicableBudgets)
        {
            var headroom = budget.LimitUsd - budget.ConsumedUsd;
            var remainingAfter = headroom - meteredUsd;
            var blocks = budget.Enforcement == GuardrailEnforcement.HardStop && meteredUsd > headroom;
            var outcome = blocks ? GuardrailOutcome.Blocked : GuardrailOutcome.Passed;
            applied.Add(new AppliedGuardrail
            {
                Id = budget.Id,
                MetadataKey = budget.Scope switch
                {
                    SpendingBudgetScope.CostCenter => GuardrailMetadataKeys.MeteredBudgetCostCenter,
                    SpendingBudgetScope.Organization => GuardrailMetadataKeys.MeteredBudgetOrganization,
                    _ => GuardrailMetadataKeys.MeteredBudgetEnterprise
                },
                Category = GuardrailCategories.MeteredSpendingBudget,
                Enforcement = budget.Enforcement,
                Outcome = outcome,
                Limit = budget.LimitUsd,
                ConsumedBefore = budget.ConsumedUsd,
                Requested = meteredUsd,
                RemainingAfter = remainingAfter,
                Message = blocks
                    ? "This hard-stop spending budget cannot cover the metered charge."
                    : "This spending budget permits or only observes the metered charge."
            });
            budgetRemaining[budget.Id] = remainingAfter;
            AddAlerts(budgetAlerts, budget.Id, budget.LimitUsd, budget.ConsumedUsd, meteredUsd);

            if (blocks && (lowestHeadroom is null || headroom < lowestHeadroom))
            {
                blockingBudget = budget;
                lowestHeadroom = headroom;
            }
        }

        var allocation = new CreditAllocation
        {
            TotalCredits = requestedCredits,
            IncludedCredits = includedCredits,
            IncludedUsageControlId = includedControl.Value?.Id,
            MeteredCredits = meteredCredits,
            MeteredUsd = meteredUsd,
            MeteredBudgetId = blockingBudget?.Id ?? applicableBudgets.FirstOrDefault()?.Id,
            MeteredBudgetRemainingUsd = budgetRemaining
        };
        var remainingState = new RemainingState
        {
            IncludedPoolCredits = poolRemaining - includedCredits,
            EffectiveUserBudgetCredits = effectiveUlb?.RemainingCredits,
            IncludedUsageControlCredits = controlRemaining is null
                ? null
                : Math.Max(0m, controlRemaining.Value - includedCredits)
        };

        if (blockingBudget is null)
        {
            alerts.AddRange(budgetAlerts);
        }

        if (blockingBudget is null)
        {
            return new EconomicGuardrailEvaluation(
                SimulationDecision.Allowed,
                null,
                allocation,
                remainingState,
                effectiveUlb,
                applied,
                alerts,
                null);
        }

        var rejectedUlb = effectiveUlb is null
            ? null
            : effectiveUlb with
            {
                ReservedCredits = 0m,
                RemainingCredits = effectiveUlb.LimitCredits - effectiveUlb.ConsumedBeforeCredits
            };
        return new EconomicGuardrailEvaluation(
            SimulationDecision.Blocked,
            blockingBudget.Id,
            new CreditAllocation { TotalCredits = requestedCredits },
            unchangedRemaining,
            rejectedUlb,
            applied,
            alerts,
            null);

        AppliedGuardrail AuthorizationGuardrail(GuardrailOutcome outcome) =>
            new()
            {
                Id = "paid-usage",
                MetadataKey = GuardrailMetadataKeys.PaidUsage,
                Category = GuardrailCategories.PaidUsageAuthorization,
                Enforcement = GuardrailEnforcement.HardStop,
                Outcome = outcome,
                Requested = meteredCredits,
                Message = outcome == GuardrailOutcome.Passed
                    ? "Paid usage is authorized."
                    : "Paid usage is disabled."
            };
    }

    private decimal? CalculatePoolEntitlement(BillingContext billing, DateTimeOffset timestamp) =>
        SumSeatEntitlements(
            billing.SeatAssignments.Where(x =>
                EconomicGuardrailApplicabilityResolver.IsEffective(
                    x.EffectiveFrom,
                    x.EffectiveTo,
                    timestamp)));

    private decimal? CalculateCostCenterEntitlement(
        BillingContext billing,
        string costCenterId,
        DateTimeOffset timestamp) =>
        SumSeatEntitlements(
            billing.SeatAssignments.Where(x =>
                string.Equals(x.CostCenterId, costCenterId, StringComparison.OrdinalIgnoreCase) &&
                EconomicGuardrailApplicabilityResolver.IsEffective(
                    x.EffectiveFrom,
                    x.EffectiveTo,
                    timestamp)));

    private decimal? SumSeatEntitlements(IEnumerable<EffectiveSeatAssignment> seats)
    {
        var total = 0m;
        foreach (var seat in seats)
        {
            var plan = configuration.Plans.SingleOrDefault(x =>
                string.Equals(x.Id, seat.PlanId, StringComparison.OrdinalIgnoreCase));
            if (plan is null)
            {
                return null;
            }

            if (!plan.IsPooled)
            {
                continue;
            }

            if (plan.IncludedCreditsPerUser is null)
            {
                return null;
            }

            total += plan.IncludedCreditsPerUser.Value;
        }

        return total;
    }

    private static void AddAlerts(
        ICollection<ThresholdEvent> alerts,
        string id,
        decimal limit,
        decimal consumed,
        decimal requested)
    {
        if (limit <= 0)
        {
            return;
        }

        var before = consumed / limit * 100m;
        var after = (consumed + requested) / limit * 100m;
        foreach (var threshold in AlertThresholds.Where(x => before < x && after >= x))
        {
            alerts.Add(new ThresholdEvent
            {
                GuardrailId = id,
                ThresholdPercent = threshold,
                BeforePercent = before,
                AfterPercent = after
            });
        }
    }

}

public sealed record EconomicGuardrailEvaluation(
    SimulationDecision Decision,
    string? FailingGuardrailId,
    CreditAllocation Allocation,
    RemainingState Remaining,
    EffectiveUserLevelBudgetResult? EffectiveUlb,
    IReadOnlyList<AppliedGuardrail> AppliedGuardrails,
    IReadOnlyList<ThresholdEvent> Alerts,
    string? Message)
{
    public static EconomicGuardrailEvaluation Stop(
        SimulationDecision decision,
        string id,
        IReadOnlyList<AppliedGuardrail> applied,
        IReadOnlyList<ThresholdEvent> alerts,
        RemainingState remaining,
        EffectiveUserLevelBudgetResult? effectiveUlb = null,
        string? message = null) =>
        new(
            decision,
            id,
            new CreditAllocation(),
            remaining,
            effectiveUlb,
            applied,
            alerts,
            message);
}
