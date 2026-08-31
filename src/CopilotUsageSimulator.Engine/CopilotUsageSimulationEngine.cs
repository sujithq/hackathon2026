using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Engine;

public sealed class CopilotUsageSimulationEngine : ICopilotUsageSimulationEngine
{
    private const decimal Million = 1_000_000m;
    private readonly EngineConfiguration _configuration;

    public CopilotUsageSimulationEngine(EngineConfiguration configuration)
    {
        EngineConfigurationValidator.Validate(configuration);
        _configuration = configuration;
    }

    public EngineConfiguration Configuration => _configuration;

    public SimulationResult Simulate(SimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ValidateScenario(scenario);

        var explanation = new List<ExplanationEntry>();
        var assumptions = new List<string>();
        var appliedGuardrails = new List<AppliedGuardrail>();
        var alerts = new List<ThresholdEvent>();
        var operation = Find(_configuration.Operations, scenario.OperationId, x => x.Id, "operation");
        var plan = Find(_configuration.Plans, scenario.PlanId, x => x.Id, "plan");
        var richGuardrails = scenario.BillingContext is not null ||
            scenario.Attribution is not null ||
            scenario.EconomicGuardrails is not null;
        AttributionResult? attribution = null;

        if (richGuardrails)
        {
            if (scenario.BillingContext is null || scenario.Attribution is null ||
                scenario.EconomicGuardrails is null)
            {
                throw new SimulationException(
                    "BillingContext, Attribution, and EconomicGuardrails must be supplied together.",
                    "incomplete-rich-guardrails");
            }

            attribution = new AttributionResolver().Resolve(scenario.Attribution, scenario.Timestamp);
            explanation.Add(Entry("attribution", attribution.Rule.ToString(), attribution.Explanation));
            if (attribution.Outcome == GuardrailOutcome.Indeterminate)
            {
                return new SimulationResult
                {
                    Decision = SimulationDecision.Indeterminate,
                    FirstFailingGate = "attribution",
                    Attribution = attribution,
                    Remaining = CreateUnchangedRemaining(scenario),
                    Explanation = explanation
                };
            }
        }

        var runtimeEvaluator = new RuntimeGuardrailEvaluator();
        var runtimePreflight = runtimeEvaluator.EvaluateBeforeCalls(scenario);
        appliedGuardrails.AddRange(runtimePreflight.AppliedGuardrails);
        if (runtimePreflight.Decision != SimulationDecision.Allowed)
        {
            return new SimulationResult
            {
                Decision = runtimePreflight.Decision,
                FirstFailingGate = runtimePreflight.FailingGuardrailId,
                Attribution = attribution,
                AppliedGuardrails = appliedGuardrails,
                Remaining = CreateUnchangedRemaining(scenario),
                Explanation = explanation
            };
        }

        var requiresActions = operation.ActionsMetering != ActionsMeteringMode.None;
        var actionsEvaluator = new ActionsGuardrailEvaluator();
        if (requiresActions && scenario.ActionsGuardrails is not null)
        {
            var actionsAccess = actionsEvaluator.EvaluateAccess(scenario.ActionsGuardrails);
            appliedGuardrails.AddRange(actionsAccess.AppliedGuardrails);
            if (actionsAccess.Decision != SimulationDecision.Allowed)
            {
                return new SimulationResult
                {
                    Decision = actionsAccess.Decision,
                    FirstFailingGate = actionsAccess.FailingGuardrailId,
                    Attribution = attribution,
                    AppliedGuardrails = appliedGuardrails,
                    Alerts = actionsAccess.Alerts,
                    Remaining = CreateUnchangedRemaining(scenario),
                    Explanation = explanation
                };
            }
        }

        var gateFailure = EvaluateGates(operation, scenario, explanation);
        if (gateFailure is not null)
        {
            return Blocked(
                gateFailure.Value.GateId,
                scenario,
                explanation,
                attribution,
                appliedGuardrails);
        }

        if (!operation.IsBilled)
        {
            explanation.Add(Entry("usage", "unbilled-operation", $"Operation '{operation.Id}' does not consume AI credits."));
            return new SimulationResult
            {
                Decision = SimulationDecision.Allowed,
                Attribution = attribution,
                AppliedGuardrails = appliedGuardrails,
                Remaining = CreateUnchangedRemaining(scenario),
                Explanation = explanation
            };
        }

        if (scenario.Calls.Count == 0)
        {
            assumptions.Add("No model calls were supplied, so token cost could not be calculated.");
            explanation.Add(Entry("usage", "missing-calls", "The billed operation has no model-call inputs."));
            return new SimulationResult
            {
                Decision = SimulationDecision.PartiallySimulated,
                Attribution = attribution,
                AppliedGuardrails = appliedGuardrails,
                Remaining = CreateUnchangedRemaining(scenario),
                Assumptions = assumptions,
                Explanation = explanation
            };
        }

        var calls = scenario.Calls
            .Select((call, index) => CalculateCall(operation, scenario.Timestamp, call, index + 1, explanation))
            .ToArray();
        var totalCredits = calls.Sum(x => x.Credits);

        assumptions.Add("Fractional AI credits are retained because GitHub does not document billing rounding.");
        var runtimeCredits = runtimeEvaluator.EvaluateCredits(scenario.RuntimeGuardrails, totalCredits);
        appliedGuardrails.AddRange(runtimeCredits.AppliedGuardrails);
        if (runtimeCredits.Decision == SimulationDecision.SoftStopped)
        {
            return new SimulationResult
            {
                Decision = SimulationDecision.SoftStopped,
                FirstFailingGate = runtimeCredits.FailingGuardrailId,
                Calls = calls,
                Attribution = attribution,
                AppliedGuardrails = appliedGuardrails,
                Remaining = CreateUnchangedRemaining(scenario),
                Assumptions = assumptions,
                Explanation = explanation
            };
        }

        var actionsUsage = CalculateActions(operation, scenario, explanation);
        if (actionsUsage is not null && scenario.ActionsGuardrails is not null)
        {
            var actionsBudget = actionsEvaluator.EvaluateBudgets(scenario.ActionsGuardrails, actionsUsage);
            appliedGuardrails.AddRange(actionsBudget.AppliedGuardrails);
            alerts.AddRange(actionsBudget.Alerts);
            if (actionsBudget.Decision != SimulationDecision.Allowed)
            {
                return new SimulationResult
                {
                    Decision = actionsBudget.Decision,
                    FirstFailingGate = actionsBudget.FailingGuardrailId,
                    Calls = calls,
                    ActionsUsage = actionsUsage,
                    Attribution = attribution,
                    AppliedGuardrails = appliedGuardrails,
                    Alerts = alerts,
                    Remaining = CreateUnchangedRemaining(scenario),
                    Assumptions = assumptions,
                    Explanation = explanation
                };
            }
        }

        BudgetEvaluation? legacyBudgetResult = null;
        EconomicGuardrailEvaluation? economicResult = null;
        if (richGuardrails)
        {
            economicResult = new EconomicGuardrailEvaluator(_configuration)
                .Evaluate(scenario, attribution!, totalCredits);
            appliedGuardrails.AddRange(economicResult.AppliedGuardrails);
            alerts.AddRange(economicResult.Alerts);
            if (economicResult.Message is not null)
            {
                explanation.Add(Entry("guardrail", economicResult.FailingGuardrailId ?? "indeterminate", economicResult.Message));
            }

            if (economicResult.Decision != SimulationDecision.Allowed)
            {
                return new SimulationResult
                {
                    Decision = economicResult.Decision,
                    FirstFailingGate = economicResult.FailingGuardrailId,
                    Calls = calls,
                    Allocation = economicResult.Allocation,
                    Attribution = attribution,
                    EffectiveUlb = economicResult.EffectiveUlb,
                    AppliedGuardrails = appliedGuardrails,
                    Alerts = alerts,
                    Remaining = economicResult.Remaining,
                    Assumptions = assumptions,
                    Explanation = explanation
                };
            }
        }
        else
        {
            legacyBudgetResult = AllocateBudgets(totalCredits, plan, scenario, explanation);
            if (legacyBudgetResult.FailingGate is not null)
            {
                return new SimulationResult
                {
                    Decision = SimulationDecision.Blocked,
                    FirstFailingGate = legacyBudgetResult.FailingGate,
                    Calls = calls,
                    Allocation = legacyBudgetResult.Allocation,
                    AppliedGuardrails = appliedGuardrails,
                    Remaining = legacyBudgetResult.Remaining,
                    Assumptions = assumptions,
                    Explanation = explanation
                };
            }
        }

        var allocation = economicResult?.Allocation ?? legacyBudgetResult!.Allocation;
        var remaining = (economicResult?.Remaining ?? legacyBudgetResult!.Remaining) with
        {
            ActionsIncludedMinutes = actionsUsage is null
                ? null
                : Math.Max(
                    0m,
                    (scenario.ActionsGuardrails is null
                        ? scenario.ActionsUsage!.IncludedMinutesRemaining
                        : scenario.ActionsGuardrails.IncludedMinutes -
                          scenario.ActionsGuardrails.ConsumedIncludedMinutes) -
                    actionsUsage.IncludedMinutes)
        };

        explanation.Add(Entry("result", "allowed", "All access and budget checks passed."));
        return new SimulationResult
        {
            Decision = SimulationDecision.Allowed,
            Calls = calls,
            Allocation = allocation,
            ActionsUsage = actionsUsage,
            Attribution = attribution,
            EffectiveUlb = economicResult?.EffectiveUlb,
            AppliedGuardrails = appliedGuardrails,
            Alerts = alerts,
            Remaining = remaining,
            Assumptions = assumptions,
            Explanation = explanation
        };
    }

    private GateFailure? EvaluateGates(
        OperationDefinition operation,
        SimulationScenario scenario,
        List<ExplanationEntry> explanation)
    {
        foreach (var gate in _configuration.Gates.OrderBy(x => x.Sequence))
        {
            if (gate.ApplicableOperationIds.Count > 0 &&
                !gate.ApplicableOperationIds.Contains(operation.Id, StringComparer.OrdinalIgnoreCase))
            {
                explanation.Add(Entry("access", "gate-not-applicable", $"Gate '{gate.Id}' does not apply."));
                continue;
            }

            var supplied = TryGet(scenario.AccessGates, gate.Id, out var state);
            var passed = supplied ? state!.Passed : gate.PassWhenUnspecified;
            if (!passed)
            {
                var reason = state?.Reason ?? $"Gate '{gate.Id}' did not pass.";
                var remediation = string.IsNullOrWhiteSpace(state?.Remediation)
                    ? string.Empty
                    : $" Remediation: {state.Remediation}";
                explanation.Add(Entry("access", gate.Id, reason + remediation));
                return new GateFailure(gate.Id);
            }

            explanation.Add(Entry("access", "gate-passed", $"Gate '{gate.Id}' passed."));
        }

        return null;
    }

    private ModelCallCharge CalculateCall(
        OperationDefinition operation,
        DateTimeOffset timestamp,
        ModelCallInput call,
        int index,
        List<ExplanationEntry> explanation)
    {
        var model = Find(_configuration.Models, call.ModelId, x => x.Id, "model");
        var period = model.PricePeriods.SingleOrDefault(x =>
            timestamp >= x.EffectiveFrom && (x.EffectiveTo is null || timestamp < x.EffectiveTo))
            ?? throw new SimulationException(
                $"Model '{model.Id}' has no pricing effective at {timestamp:O}.",
                "pricing-not-effective");
        var tier = period.Tiers.SingleOrDefault(x =>
            (x.MinimumContextTokensExclusive is null || call.ContextTokens > x.MinimumContextTokensExclusive) &&
            (x.MaximumContextTokensInclusive is null || call.ContextTokens <= x.MaximumContextTokensInclusive))
            ?? throw new SimulationException(
                $"Model '{model.Id}' has no tier for {call.ContextTokens} context tokens.",
                "pricing-tier-not-found");

        var freshInputUsd = call.FreshInputTokens * tier.InputUsdPerMillion / Million;
        var cachedInputUsd = call.CachedInputTokens * tier.CachedInputUsdPerMillion / Million;
        var cacheWriteUsd = call.CacheWriteTokens * tier.CacheWriteUsdPerMillion / Million;
        var outputUsd = call.OutputTokens * tier.OutputUsdPerMillion / Million;
        var rawUsd = freshInputUsd + cachedInputUsd + cacheWriteUsd + outputUsd;
        var adjustedUsd = rawUsd;
        var multipliers = new List<AppliedMultiplier>();

        foreach (var multiplierId in call.EnabledMultiplierIds)
        {
            var multiplier = Find(_configuration.Multipliers, multiplierId, x => x.Id, "multiplier");
            if (!Applies(multiplier.ApplicableOperationIds, operation.Id) ||
                !Applies(multiplier.ApplicableModelIds, model.Id))
            {
                throw new SimulationException(
                    $"Multiplier '{multiplier.Id}' does not apply to operation '{operation.Id}' and model '{model.Id}'.",
                    "multiplier-not-applicable");
            }

            adjustedUsd *= multiplier.Factor;
            multipliers.Add(new AppliedMultiplier { Id = multiplier.Id, Factor = multiplier.Factor });
        }

        var credits = adjustedUsd / _configuration.UsdPerCredit;
        explanation.Add(Entry(
            "pricing",
            "model-call-priced",
            $"Call {index} used model '{model.Id}', tier '{tier.Id}', and consumed {credits:G29} credits."));

        return new ModelCallCharge
        {
            CallIndex = index,
            ModelId = model.Id,
            PriceTierId = tier.Id,
            FreshInputUsd = freshInputUsd,
            CachedInputUsd = cachedInputUsd,
            CacheWriteUsd = cacheWriteUsd,
            OutputUsd = outputUsd,
            RawUsd = rawUsd,
            AdjustedUsd = adjustedUsd,
            Credits = credits,
            AppliedMultipliers = multipliers
        };
    }

    private BudgetEvaluation AllocateBudgets(
        decimal totalCredits,
        PlanDefinition plan,
        SimulationScenario scenario,
        List<ExplanationEntry> explanation)
    {
        var budgets = scenario.Budgets;
        var effectiveUserBudget = budgets.UserBudgets
            .OrderByDescending(x => x.Scope)
            .FirstOrDefault();

        if (effectiveUserBudget is not null && effectiveUserBudget.CreditsRemaining < totalCredits)
        {
            explanation.Add(Entry("budget", "user-budget-exceeded", "The effective user-level budget cannot cover the request."));
            return BudgetEvaluation.Blocked(
                "budget.user",
                totalCredits,
                CreateUnchangedRemaining(scenario) with
                {
                    EffectiveUserBudgetCredits = effectiveUserBudget.CreditsRemaining
                });
        }

        var includedPool = budgets.IncludedPoolCreditsRemaining ?? plan.IncludedCreditsPerUser ?? 0m;
        var includedAvailable = Math.Max(0m, includedPool);
        var control = budgets.IncludedUsageControl;
        if (control is not null)
        {
            includedAvailable = Math.Min(includedAvailable, Math.Max(0m, control.CreditsRemaining));
        }

        var splitAllocation = _configuration.PoolOverflowBehavior == PoolOverflowBehavior.Split;
        var includedCredits = totalCredits <= includedAvailable
            ? totalCredits
            : splitAllocation ? includedAvailable : 0m;
        var meteredCredits = totalCredits - includedCredits;

        if (control is not null &&
            control.OverflowBehavior == IncludedUsageOverflowBehavior.Block &&
            totalCredits > control.CreditsRemaining)
        {
            explanation.Add(Entry("budget", "included-control-exceeded", "The cost-center included-usage control blocks overflow."));
            return BudgetEvaluation.Blocked(
                "budget.included-usage-control",
                totalCredits,
                CreateUnchangedRemaining(scenario) with
                {
                    EffectiveUserBudgetCredits = effectiveUserBudget?.CreditsRemaining,
                    IncludedUsageControlCredits = control.CreditsRemaining
                });
        }

        if (meteredCredits > 0 && !budgets.PaidUsageEnabled)
        {
            explanation.Add(Entry("budget", "paid-usage-disabled", "The included pool cannot cover the request and paid usage is disabled."));
            return BudgetEvaluation.Blocked(
                "budget.paid-usage",
                totalCredits,
                CreateUnchangedRemaining(scenario) with
                {
                    EffectiveUserBudgetCredits = effectiveUserBudget?.CreditsRemaining,
                    IncludedUsageControlCredits = control?.CreditsRemaining
                });
        }

        var meteredUsd = meteredCredits * _configuration.UsdPerCredit;
        var meteredBudget = meteredCredits > 0 ? ResolveMeteredBudget(budgets) : null;
        if (meteredBudget is not null &&
            meteredBudget.StopUsageWhenLimitReached &&
            meteredBudget.UsdRemaining < meteredUsd)
        {
            explanation.Add(Entry("budget", "metered-budget-exceeded", $"Metered budget '{meteredBudget.Id}' blocks the charge."));
            return BudgetEvaluation.Blocked(
                $"budget.{meteredBudget.Scope.ToString().ToLowerInvariant()}",
                totalCredits,
                CreateUnchangedRemaining(scenario) with
                {
                    EffectiveUserBudgetCredits = effectiveUserBudget?.CreditsRemaining,
                    IncludedUsageControlCredits = control?.CreditsRemaining,
                    MeteredBudgetUsd = meteredBudget.UsdRemaining
                });
        }

        var allocation = new CreditAllocation
        {
            TotalCredits = totalCredits,
            IncludedCredits = includedCredits,
            MeteredCredits = meteredCredits,
            MeteredUsd = meteredUsd,
            MeteredBudgetId = meteredBudget?.Id
        };
        var remaining = new RemainingState
        {
            IncludedPoolCredits = Math.Max(0m, includedPool - includedCredits),
            EffectiveUserBudgetCredits = effectiveUserBudget is null
                ? null
                : effectiveUserBudget.CreditsRemaining - totalCredits,
            IncludedUsageControlCredits = control is null
                ? null
                : Math.Max(0m, control.CreditsRemaining - includedCredits),
            MeteredBudgetUsd = meteredBudget is null
                ? null
                : meteredBudget.UsdRemaining - meteredUsd
        };

        explanation.Add(Entry(
            "budget",
            "credits-allocated",
            $"Allocated {includedCredits:G29} included credits and {meteredCredits:G29} metered credits."));
        return new BudgetEvaluation(null, allocation, remaining);
    }

    private ActionsUsageResult? CalculateActions(
        OperationDefinition operation,
        SimulationScenario scenario,
        List<ExplanationEntry> explanation)
    {
        var shouldMeter = operation.ActionsMetering switch
        {
            ActionsMeteringMode.None => false,
            ActionsMeteringMode.Always => true,
            ActionsMeteringMode.PrivateRepositories => scenario.RepositoryVisibility != RepositoryVisibility.Public,
            _ => throw new SimulationException("Unknown Actions metering mode.", "actions-mode-invalid")
        };

        if (!shouldMeter)
        {
            return null;
        }

        var usage = scenario.ActionsUsage
            ?? throw new SimulationException(
                $"Operation '{operation.Id}' requires Actions usage input.",
                "actions-usage-required");
        var runner = Find(_configuration.ActionsRunners, usage.RunnerId, x => x.Id, "Actions runner");
        var includedRemaining = scenario.ActionsGuardrails is null
            ? usage.IncludedMinutesRemaining
            : Math.Max(
                0m,
                scenario.ActionsGuardrails.IncludedMinutes -
                scenario.ActionsGuardrails.ConsumedIncludedMinutes);
        var includedMinutes = Math.Min(usage.Minutes, includedRemaining);
        var billableMinutes = usage.Minutes - includedMinutes;
        var result = new ActionsUsageResult
        {
            RunnerId = runner.Id,
            TotalMinutes = usage.Minutes,
            IncludedMinutes = includedMinutes,
            BillableMinutes = billableMinutes,
            AdditionalUsd = billableMinutes * runner.UsdPerMinute
        };
        explanation.Add(Entry("actions", "actions-priced", $"Actions usage adds {result.AdditionalUsd:C} in runner charges."));
        return result;
    }

    private static MeteredBudget? ResolveMeteredBudget(BudgetState state)
    {
        if (state.CostCenterId is not null)
        {
            return FindBudget(state, MeteredBudgetScope.CostCenter, state.CostCenterId);
        }

        if (state.OrganizationId is not null)
        {
            return FindBudget(state, MeteredBudgetScope.Organization, state.OrganizationId);
        }

        return FindBudget(state, MeteredBudgetScope.Enterprise, state.EnterpriseId);
    }

    private static MeteredBudget? FindBudget(BudgetState state, MeteredBudgetScope scope, string? scopeId) =>
        state.MeteredBudgets.FirstOrDefault(x =>
            x.Scope == scope &&
            (x.ScopeId is null || string.Equals(x.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase)));

    private static bool Applies(IReadOnlySet<string> configuredIds, string actualId) =>
        configuredIds.Count == 0 || configuredIds.Contains(actualId, StringComparer.OrdinalIgnoreCase);

    private SimulationResult Blocked(
        string failingGate,
        SimulationScenario scenario,
        IReadOnlyList<ExplanationEntry> explanation,
        AttributionResult? attribution = null,
        IReadOnlyList<AppliedGuardrail>? appliedGuardrails = null)
    {
        return new SimulationResult
        {
            Decision = SimulationDecision.Blocked,
            FirstFailingGate = failingGate,
            Attribution = attribution,
            AppliedGuardrails = appliedGuardrails ?? [],
            Remaining = CreateUnchangedRemaining(scenario),
            Explanation = explanation
        };
    }

    private RemainingState CreateUnchangedRemaining(SimulationScenario scenario)
    {
        if (scenario.BillingContext is not null && scenario.EconomicGuardrails is not null)
        {
            var entitlement = scenario.BillingContext.SeatAssignments
                .Where(x => scenario.Timestamp >= x.EffectiveFrom &&
                    (x.EffectiveTo is null || scenario.Timestamp < x.EffectiveTo))
                .Select(x => _configuration.Plans.SingleOrDefault(
                    plan => string.Equals(plan.Id, x.PlanId, StringComparison.OrdinalIgnoreCase)))
                .Where(plan => plan?.IsPooled == true)
                .Sum(plan => plan!.IncludedCreditsPerUser ?? 0m);
            return new RemainingState
            {
                IncludedPoolCredits = Math.Max(
                    0m,
                    entitlement - scenario.EconomicGuardrails.EnterprisePoolConsumedCredits),
                ActionsIncludedMinutes = scenario.ActionsGuardrails is null
                    ? scenario.ActionsUsage?.IncludedMinutesRemaining
                    : Math.Max(
                        0m,
                        scenario.ActionsGuardrails.IncludedMinutes -
                        scenario.ActionsGuardrails.ConsumedIncludedMinutes)
            };
        }

        var effective = scenario.Budgets.UserBudgets.OrderByDescending(x => x.Scope).FirstOrDefault();
        return new RemainingState
        {
            IncludedPoolCredits = scenario.Budgets.IncludedPoolCreditsRemaining ?? 0m,
            EffectiveUserBudgetCredits = effective?.CreditsRemaining,
            IncludedUsageControlCredits = scenario.Budgets.IncludedUsageControl?.CreditsRemaining,
            ActionsIncludedMinutes = scenario.ActionsUsage?.IncludedMinutesRemaining
        };
    }

    private static T Find<T>(
        IEnumerable<T> values,
        string id,
        Func<T, string> idSelector,
        string label) =>
        values.SingleOrDefault(x => string.Equals(idSelector(x), id, StringComparison.OrdinalIgnoreCase))
        ?? throw new SimulationException($"Unknown {label} '{id}'.", $"{label.Replace(' ', '-')}-not-found");

    private static bool TryGet<T>(
        IReadOnlyDictionary<string, T> dictionary,
        string key,
        out T? value)
    {
        var match = dictionary.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        value = match.Value;
        return match.Key is not null;
    }

    private static ExplanationEntry Entry(string stage, string code, string message) =>
        new() { Stage = stage, Code = code, Message = message };

    private static void ValidateScenario(SimulationScenario scenario)
    {
        foreach (var call in scenario.Calls)
        {
            if (call.ContextTokens < 0 || call.FreshInputTokens < 0 || call.CachedInputTokens < 0 ||
                call.CacheWriteTokens < 0 || call.OutputTokens < 0)
            {
                throw new SimulationException("Token counts cannot be negative.", "negative-token-count");
            }
        }

        if (scenario.Budgets.IncludedPoolCreditsRemaining < 0 ||
            scenario.Budgets.UserBudgets.Any(x => x.CreditsRemaining < 0) ||
            scenario.Budgets.IncludedUsageControl is { CreditsRemaining: < 0 } ||
            scenario.Budgets.MeteredBudgets.Any(x => x.UsdRemaining < 0) ||
            scenario.ActionsUsage is { Minutes: < 0 } ||
            scenario.ActionsUsage is { IncludedMinutesRemaining: < 0 })
        {
            throw new SimulationException("Usage and remaining balances cannot be negative.", "negative-balance");
        }
    }

    private sealed record BudgetEvaluation(
        string? FailingGate,
        CreditAllocation Allocation,
        RemainingState Remaining)
    {
        public static BudgetEvaluation Blocked(
            string gate,
            decimal totalCredits,
            RemainingState remaining) =>
            new(gate, new CreditAllocation { TotalCredits = totalCredits }, remaining);
    }

    private readonly record struct GateFailure(string GateId);
}

public sealed class SimulationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
