namespace CopilotUsageSimulator.Engine.Simulation;

public sealed record BudgetState
{
    public IReadOnlyList<UserBudget> UserBudgets { get; init; } = [];
    public decimal? IncludedPoolCreditsRemaining { get; init; }
    public IncludedUsageControl? IncludedUsageControl { get; init; }
    public bool PaidUsageEnabled { get; init; }
    public IReadOnlyList<MeteredBudget> MeteredBudgets { get; init; } = [];
    public string? CostCenterId { get; init; }
    public string? OrganizationId { get; init; }
    public string? EnterpriseId { get; init; }
}

public sealed record UserBudget
{
    public UserBudgetScope Scope { get; init; }
    public decimal CreditsRemaining { get; init; }
}

public enum UserBudgetScope
{
    Universal,
    CostCenter,
    Individual
}

public sealed record IncludedUsageControl
{
    public decimal CreditsRemaining { get; init; }
    public IncludedUsageOverflowBehavior OverflowBehavior { get; init; }
}

public enum IncludedUsageOverflowBehavior
{
    Block,
    PaidUsage
}

public sealed record MeteredBudget
{
    public required string Id { get; init; }
    public MeteredBudgetScope Scope { get; init; }
    public string? ScopeId { get; init; }
    public decimal UsdRemaining { get; init; }
    public bool StopUsageWhenLimitReached { get; init; }
}

public enum MeteredBudgetScope
{
    Enterprise,
    Organization,
    CostCenter
}
