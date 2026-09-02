using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Engine;

public sealed class CopilotUsageSimulationEngine : ICopilotUsageSimulationEngine
{
    private const decimal Million = 1_000_000m;
    private readonly EngineConfiguration _configuration;
    private readonly EconomicBalanceCalculator _balances;
    private readonly EconomicGuardrailEvaluator _economicEvaluator;

    public CopilotUsageSimulationEngine(EngineConfiguration configuration)
    {
        EngineConfigurationValidator.Validate(configuration);
        _configuration = configuration;
        _balances = new EconomicBalanceCalculator(configuration);
        _economicEvaluator = new EconomicGuardrailEvaluator(configuration, _balances);
    }

    public EngineConfiguration Configuration => _configuration;

    public SimulationResult Simulate(SimulationScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        SimulationScenarioValidator.Validate(scenario);

        var context = new SimulationPipelineContext(scenario, _balances);
        var explanation = context.Explanation;
        var operation = Find(_configuration.Operations, scenario.OperationId, x => x.Id, "operation");
        _ = Find(_configuration.Plans, scenario.PlanId, x => x.Id, "plan");
        var costChecksOnly = scenario.CheckScope == SimulationCheckScope.CostRelatedOnly;

        if (operation.IsBilled)
        {
            if (scenario.BillingContext is null || scenario.Attribution is null ||
                scenario.EconomicGuardrails is null)
            {
                throw new SimulationException(
                    "Billed operations require BillingContext, Attribution, and EconomicGuardrails.",
                    "economic-context-required");
            }

            context.Attribution = new AttributionResolver().Resolve(
                scenario.Attribution,
                scenario.Timestamp);
            explanation.Add(Entry(
                "attribution",
                context.Attribution.Rule.ToString(),
                context.Attribution.Explanation));
            if (context.Attribution.Outcome == GuardrailOutcome.Indeterminate)
            {
                return context.Complete(SimulationDecision.Indeterminate, "attribution");
            }

            if (scenario.Timestamp >= scenario.BillingContext.CycleStart &&
                scenario.Timestamp < scenario.BillingContext.CycleEnd)
            {
                var inventoryFailure = _balances.FindSeatInventoryFailure(
                    scenario,
                    context.Attribution);
                if (inventoryFailure is not null)
                {
                    context.Remaining = new RemainingState();
                    explanation.Add(Entry(
                        "guardrail",
                        inventoryFailure.Value.GuardrailId,
                        inventoryFailure.Value.Message));
                    return context.Complete(
                        SimulationDecision.Indeterminate,
                        inventoryFailure.Value.GuardrailId);
                }
            }
        }

        var runtimeEvaluator = new RuntimeGuardrailEvaluator();
        if (!costChecksOnly && operation.IsBilled)
        {
            var runtimePreflight = runtimeEvaluator.EvaluateBeforeCalls(scenario);
            context.AppliedGuardrails.AddRange(runtimePreflight.AppliedGuardrails);
            if (runtimePreflight.Decision != SimulationDecision.Allowed)
            {
                return context.Complete(
                    runtimePreflight.Decision,
                    runtimePreflight.FailingGuardrailId);
            }
        }

        var requiresActions = operation.ActionsMetering != ActionsMeteringMode.None;
        var actionsEvaluator = new ActionsGuardrailEvaluator();
        if (!costChecksOnly && requiresActions && scenario.ActionsGuardrails is not null)
        {
            var actionsAccess = actionsEvaluator.EvaluateAccess(scenario.ActionsGuardrails);
            context.AppliedGuardrails.AddRange(actionsAccess.AppliedGuardrails);
            if (actionsAccess.Decision != SimulationDecision.Allowed)
            {
                context.Alerts.AddRange(actionsAccess.Alerts);
                return context.Complete(
                    actionsAccess.Decision,
                    actionsAccess.FailingGuardrailId);
            }
        }

        var gateFailure = costChecksOnly ? null : EvaluateGates(operation, scenario, explanation);
        if (gateFailure is not null)
        {
            return context.Complete(
                SimulationDecision.Blocked,
                gateFailure.Value.GateId);
        }

        if (!operation.IsBilled)
        {
            explanation.Add(Entry("usage", "unbilled-operation", $"Operation '{operation.Id}' does not consume AI credits."));
            return context.Complete(SimulationDecision.Allowed);
        }

        if (scenario.Calls.Count == 0)
        {
            context.Assumptions.Add("No model calls were supplied, so token cost could not be calculated.");
            explanation.Add(Entry("usage", "missing-calls", "The billed operation has no model-call inputs."));
            return context.Complete(SimulationDecision.PartiallySimulated);
        }

        context.Calls = scenario.Calls
            .Select((call, index) => CalculateCall(operation, scenario.Timestamp, call, index + 1, explanation))
            .ToArray();
        var totalCredits = context.Calls.Sum(x => x.Credits);

        context.Assumptions.Add("Fractional AI credits are retained because GitHub does not document billing rounding.");
        if (!costChecksOnly)
        {
            var runtimeCredits = runtimeEvaluator.EvaluateCredits(scenario.RuntimeGuardrails, totalCredits);
            context.AppliedGuardrails.AddRange(runtimeCredits.AppliedGuardrails);
            if (runtimeCredits.Decision == SimulationDecision.SoftStopped)
            {
                return context.Complete(
                    SimulationDecision.SoftStopped,
                    runtimeCredits.FailingGuardrailId);
            }
        }

        context.ActionsUsage = CalculateActions(operation, scenario, explanation);
        var economicResult = _economicEvaluator.Evaluate(
            scenario,
            context.Attribution!,
            totalCredits);
        context.AppliedGuardrails.AddRange(economicResult.AppliedGuardrails);
        if (economicResult.Message is not null)
        {
            explanation.Add(Entry("guardrail", economicResult.FailingGuardrailId ?? "indeterminate", economicResult.Message));
        }

        if (economicResult.Decision != SimulationDecision.Allowed)
        {
            context.Alerts.AddRange(economicResult.Alerts);
            context.Allocation = economicResult.Allocation;
            context.EffectiveUlb = economicResult.EffectiveUlb;
            context.Remaining = economicResult.Remaining;
            return context.Complete(
                economicResult.Decision,
                economicResult.FailingGuardrailId);
        }

        if (context.ActionsUsage is not null && scenario.ActionsGuardrails is not null)
        {
            var actionsBudget = actionsEvaluator.EvaluateBudgets(
                scenario.ActionsGuardrails,
                context.ActionsUsage);
            context.AppliedGuardrails.AddRange(actionsBudget.AppliedGuardrails);
            context.Alerts.AddRange(actionsBudget.Alerts);
            if (actionsBudget.Decision != SimulationDecision.Allowed)
            {
                return context.Complete(
                    actionsBudget.Decision,
                    actionsBudget.FailingGuardrailId);
            }
        }

        context.Alerts.InsertRange(0, economicResult.Alerts);
        context.Allocation = economicResult.Allocation;
        context.EffectiveUlb = economicResult.EffectiveUlb;
        context.Remaining = _balances.ApplyActionsUsage(
            economicResult.Remaining,
            scenario,
            context.ActionsUsage);

        explanation.Add(Entry("result", "allowed", "All access and budget checks passed."));
        return context.Complete(SimulationDecision.Allowed);
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

    private static bool Applies(IReadOnlySet<string> configuredIds, string actualId) =>
        configuredIds.Count == 0 || configuredIds.Contains(actualId, StringComparer.OrdinalIgnoreCase);

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

    private readonly record struct GateFailure(string GateId);
}

public sealed class SimulationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
