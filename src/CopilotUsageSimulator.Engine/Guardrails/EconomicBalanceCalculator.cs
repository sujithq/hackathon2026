using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Engine.Guardrails;

public sealed class EconomicBalanceCalculator(EngineConfiguration configuration)
{
    private readonly EconomicGuardrailApplicabilityResolver _applicability = new();

    public SelectedPlanSeatResolution ResolveSelectedPlanSeat(
        SimulationScenario scenario,
        AttributionResult attribution)
    {
        var effectiveSeats = scenario.BillingContext!.SeatAssignments
            .Where(seat =>
                string.Equals(seat.UserId, attribution.UserId, StringComparison.OrdinalIgnoreCase) &&
                EconomicGuardrailApplicabilityResolver.IsEffective(
                    seat.EffectiveFrom,
                    seat.EffectiveTo,
                    scenario.Timestamp))
            .ToArray();

        if (effectiveSeats.Length == 0)
        {
            return SelectedPlanSeatResolution.Missing();
        }

        if (effectiveSeats.Length > 1)
        {
            return SelectedPlanSeatResolution.Ambiguous();
        }

        var seat = effectiveSeats[0];
        if (!configuration.Plans.Any(plan => IdEquals(plan.Id, seat.PlanId)))
        {
            return SelectedPlanSeatResolution.UnknownPlan(seat);
        }

        return IdEquals(seat.PlanId, scenario.PlanId)
            ? SelectedPlanSeatResolution.Matched(seat)
            : SelectedPlanSeatResolution.Conflicting(seat);
    }

    public SeatEntitlement CalculatePoolEntitlement(
        BillingContext billing,
        DateTimeOffset timestamp) =>
        SumSeatEntitlements(
            billing.SeatAssignments.Where(seat =>
                EconomicGuardrailApplicabilityResolver.IsEffective(
                    seat.EffectiveFrom,
                    seat.EffectiveTo,
                    timestamp)));

    public SeatEntitlement CalculateCostCenterEntitlement(
        BillingContext billing,
        string costCenterId,
        DateTimeOffset timestamp) =>
        SumSeatEntitlements(
            billing.SeatAssignments.Where(seat =>
                string.Equals(
                    seat.CostCenterId,
                    costCenterId,
                    StringComparison.OrdinalIgnoreCase) &&
                EconomicGuardrailApplicabilityResolver.IsEffective(
                    seat.EffectiveFrom,
                    seat.EffectiveTo,
                    timestamp)));

    public SeatInventoryFailure? FindSeatInventoryFailure(
        SimulationScenario scenario,
        AttributionResult attribution)
    {
        var billing = scenario.BillingContext!;
        if (CalculatePoolEntitlement(billing, scenario.Timestamp).IsKnown)
        {
            return null;
        }

        var control = _applicability.ResolveIncludedUsageControl(
            scenario.EconomicGuardrails!,
            attribution,
            scenario.Timestamp);
        if (control is { IsAmbiguous: false, Value: not null } &&
            !CalculateCostCenterEntitlement(
                billing,
                attribution.CostCenterId!,
                scenario.Timestamp).IsKnown)
        {
            return new SeatInventoryFailure(
                "included-control.seat-inventory",
                "The cost-center seat inventory contains an unknown plan allowance.");
        }

        return new SeatInventoryFailure(
            "pool.seat-inventory",
            "An active pooled seat references a plan with an unknown included-credit allowance.");
    }

    public RemainingState CreateUnchangedRemaining(SimulationScenario scenario)
    {
        var actionsRemaining = scenario.ActionsGuardrails is null
            ? scenario.ActionsUsage?.IncludedMinutesRemaining
            : Available(
                scenario.ActionsGuardrails.IncludedMinutes,
                scenario.ActionsGuardrails.ConsumedIncludedMinutes);

        if (scenario.BillingContext is null || scenario.EconomicGuardrails is null)
        {
            return new RemainingState { ActionsIncludedMinutes = actionsRemaining };
        }

        return new RemainingState
        {
            IncludedPoolCredits = Available(
                CalculateKnownPoolEntitlement(scenario.BillingContext, scenario.Timestamp),
                scenario.EconomicGuardrails.EnterprisePoolConsumedCredits),
            ActionsIncludedMinutes = actionsRemaining
        };
    }

    public RemainingState ApplyActionsUsage(
        RemainingState remaining,
        SimulationScenario scenario,
        ActionsUsageResult? usage) =>
        remaining with
        {
            ActionsIncludedMinutes = usage is null
                ? null
                : Available(
                    scenario.ActionsGuardrails?.IncludedMinutes ??
                        scenario.ActionsUsage!.IncludedMinutesRemaining,
                    (scenario.ActionsGuardrails?.ConsumedIncludedMinutes ?? 0m) +
                        usage.IncludedMinutes)
        };

    public EconomicGuardrailSnapshot ApplyAllocation(
        EconomicGuardrailSnapshot snapshot,
        SimulationResult result) =>
        snapshot with
        {
            EnterprisePoolConsumedCredits =
                snapshot.EnterprisePoolConsumedCredits + result.Allocation.IncludedCredits,
            UserLevelBudgets = snapshot.UserLevelBudgets
                .Select(budget => IdEquals(budget.Id, result.EffectiveUlb?.Id)
                    ? budget with
                    {
                        ConsumedCredits =
                            budget.ConsumedCredits + result.Allocation.TotalCredits
                    }
                    : budget)
                .ToArray(),
            IncludedUsageControls = snapshot.IncludedUsageControls
                .Select(control => IdEquals(
                    control.Id,
                    result.Allocation.IncludedUsageControlId)
                    ? control with
                    {
                        ConsumedCredits =
                            control.ConsumedCredits + result.Allocation.IncludedCredits
                    }
                    : control)
                .ToArray(),
            SpendingBudgets = snapshot.SpendingBudgets
                .Select(budget => ContainsId(
                    result.Allocation.MeteredBudgetRemainingUsd,
                    budget.Id)
                    ? budget with
                    {
                        ConsumedUsd = budget.ConsumedUsd + result.Allocation.MeteredUsd
                    }
                    : budget)
                .ToArray()
        };

    public static decimal Available(decimal limit, decimal consumed) =>
        Math.Max(0m, limit - consumed);

    public static decimal Headroom(decimal limit, decimal consumed) =>
        limit - consumed;

    private decimal CalculateKnownPoolEntitlement(
        BillingContext billing,
        DateTimeOffset timestamp) =>
        billing.SeatAssignments
            .Where(seat => EconomicGuardrailApplicabilityResolver.IsEffective(
                seat.EffectiveFrom,
                seat.EffectiveTo,
                timestamp))
            .Select(seat => configuration.Plans.SingleOrDefault(plan =>
                IdEquals(plan.Id, seat.PlanId)))
            .Where(plan => plan?.IsPooled == true)
            .Sum(plan => plan!.IncludedCreditsPerUser ?? 0m);

    private SeatEntitlement SumSeatEntitlements(IEnumerable<EffectiveSeatAssignment> seats)
    {
        var total = 0m;
        foreach (var seat in seats)
        {
            var plan = configuration.Plans.SingleOrDefault(plan =>
                string.Equals(plan.Id, seat.PlanId, StringComparison.OrdinalIgnoreCase));
            if (plan is null)
            {
                return SeatEntitlement.Unknown(seat.PlanId);
            }

            if (!plan.IsPooled)
            {
                continue;
            }

            if (plan.IncludedCreditsPerUser is null)
            {
                return SeatEntitlement.Unknown(plan.Id);
            }

            total += plan.IncludedCreditsPerUser.Value;
        }

        return SeatEntitlement.Known(total);
    }

    private static bool IdEquals(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsId(
        IReadOnlyDictionary<string, decimal> values,
        string id) =>
        values.Keys.Any(key => IdEquals(key, id));
}

public readonly record struct SeatEntitlement(
    bool IsKnown,
    decimal Credits,
    string? UnknownPlanId)
{
    public static SeatEntitlement Known(decimal credits) =>
        new(true, credits, null);

    public static SeatEntitlement Unknown(string planId) =>
        new(false, 0m, planId);
}

public readonly record struct SeatInventoryFailure(string GuardrailId, string Message);

public readonly record struct SelectedPlanSeatResolution(
    SelectedPlanSeatStatus Status,
    EffectiveSeatAssignment? Seat)
{
    public static SelectedPlanSeatResolution Matched(EffectiveSeatAssignment seat) =>
        new(SelectedPlanSeatStatus.Matched, seat);

    public static SelectedPlanSeatResolution Missing() =>
        new(SelectedPlanSeatStatus.Missing, null);

    public static SelectedPlanSeatResolution Ambiguous() =>
        new(SelectedPlanSeatStatus.Ambiguous, null);

    public static SelectedPlanSeatResolution Conflicting(EffectiveSeatAssignment seat) =>
        new(SelectedPlanSeatStatus.Conflicting, seat);

    public static SelectedPlanSeatResolution UnknownPlan(EffectiveSeatAssignment seat) =>
        new(SelectedPlanSeatStatus.UnknownPlan, seat);
}

public enum SelectedPlanSeatStatus
{
    Matched,
    Missing,
    Ambiguous,
    Conflicting,
    UnknownPlan
}
