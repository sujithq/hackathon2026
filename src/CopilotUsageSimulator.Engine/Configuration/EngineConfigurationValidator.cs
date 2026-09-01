namespace CopilotUsageSimulator.Engine.Configuration;

public static class EngineConfigurationValidator
{
    public static void Validate(EngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.UsdPerCredit <= 0)
        {
            throw new ConfigurationException("usdPerCredit must be greater than zero.");
        }

        Require(configuration.Plans, "plans");
        Require(configuration.Models, "models");
        Require(configuration.Operations, "operations");
        Require(configuration.Gates, "gates");
        Require(configuration.Multipliers, "multipliers");
        Require(configuration.ActionsRunners, "actionsRunners");
        RequireItems(configuration.Plans, "plans");
        RequireItems(configuration.Models, "models");
        RequireItems(configuration.Operations, "operations");
        RequireItems(configuration.Gates, "gates");
        RequireItems(configuration.Multipliers, "multipliers");
        RequireItems(configuration.ActionsRunners, "actionsRunners");

        EnsureUnique(configuration.Plans.Select(x => x.Id), "plan");
        EnsureUnique(configuration.Models.Select(x => x.Id), "model");
        EnsureUnique(configuration.Operations.Select(x => x.Id), "operation");
        EnsureUnique(configuration.Gates.Select(x => x.Id), "gate");
        EnsureUnique(configuration.Multipliers.Select(x => x.Id), "multiplier");
        EnsureUnique(configuration.ActionsRunners.Select(x => x.Id), "Actions runner");

        var operationIds = configuration.Operations.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelIds = configuration.Models.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var gate in configuration.Gates)
        {
            Require(gate.ApplicableOperationIds, $"gates['{gate.Id}'].applicableOperationIds");
            EnsureReferences(gate.ApplicableOperationIds, operationIds, $"gate '{gate.Id}' operation");
        }

        foreach (var multiplier in configuration.Multipliers)
        {
            Require(
                multiplier.ApplicableOperationIds,
                $"multipliers['{multiplier.Id}'].applicableOperationIds");
            Require(
                multiplier.ApplicableModelIds,
                $"multipliers['{multiplier.Id}'].applicableModelIds");
            if (multiplier.Factor < 0)
            {
                throw new ConfigurationException($"Multiplier '{multiplier.Id}' cannot have a negative factor.");
            }

            EnsureReferences(multiplier.ApplicableOperationIds, operationIds, $"multiplier '{multiplier.Id}' operation");
            EnsureReferences(multiplier.ApplicableModelIds, modelIds, $"multiplier '{multiplier.Id}' model");
        }

        foreach (var runner in configuration.ActionsRunners.Where(x => x.UsdPerMinute < 0))
        {
            throw new ConfigurationException($"Actions runner '{runner.Id}' cannot have a negative price.");
        }

        foreach (var model in configuration.Models)
        {
            Require(model.PricePeriods, $"models['{model.Id}'].pricePeriods");
            RequireItems(model.PricePeriods, $"models['{model.Id}'].pricePeriods");
            if (model.PricePeriods.Count == 0)
            {
                throw new ConfigurationException($"Model '{model.Id}' must define at least one price period.");
            }

            var orderedPeriods = model.PricePeriods.OrderBy(x => x.EffectiveFrom).ToArray();
            for (var index = 1; index < orderedPeriods.Length; index++)
            {
                if (orderedPeriods[index - 1].EffectiveTo is null ||
                    orderedPeriods[index].EffectiveFrom < orderedPeriods[index - 1].EffectiveTo)
                {
                    throw new ConfigurationException($"Model '{model.Id}' has overlapping price periods.");
                }
            }

            foreach (var period in model.PricePeriods)
            {
                Require(period.Tiers, $"models['{model.Id}'].pricePeriods.tiers");
                RequireItems(period.Tiers, $"models['{model.Id}'].pricePeriods.tiers");
                if (period.EffectiveTo <= period.EffectiveFrom)
                {
                    throw new ConfigurationException($"Model '{model.Id}' has an invalid effective date range.");
                }

                if (period.Tiers.Count == 0)
                {
                    throw new ConfigurationException($"Model '{model.Id}' has a price period without tiers.");
                }

                EnsureUnique(period.Tiers.Select(x => x.Id), $"tier for model '{model.Id}'");
                var orderedTiers = period.Tiers
                    .OrderBy(x => x.MinimumContextTokensExclusive ?? long.MinValue)
                    .ToArray();
                for (var index = 1; index < orderedTiers.Length; index++)
                {
                    var previousMaximum = orderedTiers[index - 1].MaximumContextTokensInclusive;
                    var currentMinimum = orderedTiers[index].MinimumContextTokensExclusive;
                    if (previousMaximum is null || currentMinimum is null || currentMinimum < previousMaximum)
                    {
                        throw new ConfigurationException(
                            $"Model '{model.Id}' has overlapping context tiers '{orderedTiers[index - 1].Id}' and '{orderedTiers[index].Id}'.");
                    }
                }

                foreach (var tier in period.Tiers)
                {
                    if (tier.MinimumContextTokensExclusive < 0 || tier.MaximumContextTokensInclusive < 0)
                    {
                        throw new ConfigurationException($"Model '{model.Id}' tier '{tier.Id}' has a negative context boundary.");
                    }

                    if (tier.MinimumContextTokensExclusive >= tier.MaximumContextTokensInclusive)
                    {
                        throw new ConfigurationException($"Model '{model.Id}' tier '{tier.Id}' has an invalid context range.");
                    }

                    if (tier.InputUsdPerMillion < 0 || tier.CachedInputUsdPerMillion < 0 ||
                        tier.CacheWriteUsdPerMillion < 0 || tier.OutputUsdPerMillion < 0)
                    {
                        throw new ConfigurationException($"Model '{model.Id}' tier '{tier.Id}' has a negative token price.");
                    }
                }
            }
        }
    }

    private static T Require<T>(T? value, string path) where T : class =>
        value ?? throw new ConfigurationException(
            $"Configuration property '{path}' cannot be null.",
            ConfigurationException.InvalidContractCode);

    private static void RequireItems<T>(IReadOnlyList<T> values, string path) where T : class
    {
        for (var index = 0; index < values.Count; index++)
        {
            Require(values[index], $"{path}[{index}]");
        }
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        var duplicate = values
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ConfigurationException($"Duplicate {label} id '{duplicate.Key}'.");
        }
    }

    private static void EnsureReferences(
        IEnumerable<string> references,
        IReadOnlySet<string> knownValues,
        string label)
    {
        var unknown = references.FirstOrDefault(reference => !knownValues.Contains(reference));
        if (unknown is not null)
        {
            throw new ConfigurationException($"Unknown {label} id '{unknown}'.");
        }
    }
}

public sealed class ConfigurationException(
    string message,
    string code = ConfigurationException.InvalidConfigurationCode) : Exception(message)
{
    public const string InvalidConfigurationCode = "configuration-invalid";
    public const string InvalidContractCode = "configuration-contract-invalid";

    public string Code { get; } = code;
}
