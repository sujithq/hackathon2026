using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

namespace CopilotUsageSimulator.Web.Services;

public static class ExampleScenarioFactory
{
    public static SimulationScenario Create(EngineConfiguration configuration, string template = "cloud-agent")
    {
        var timestamp = DateTimeOffset.UtcNow;
        var operation = template == "code-review" ? "code-review" : template == "chat" ? "chat" : "cloud-agent";
        var usesActions = operation is "cloud-agent" or "code-review";
        var task = operation switch
        {
            "code-review" => "Review the repository and explain the highest-risk defects.",
            "chat" => "Explain the architecture and propose a safe implementation plan.",
            _ => "Implement the requested feature, run checks, and report the result."
        };

        return new SimulationScenario
        {
            OperationId = operation,
            PlanId = "business",
            ProductId = "github-copilot",
            SkuId = "copilot-ai-credits",
            Timestamp = timestamp,
            CheckScope = SimulationCheckScope.CostRelatedOnly,
            RepositoryVisibility = RepositoryVisibility.Private,
            Metadata = new Dictionary<string, string> { ["task"] = task, ["estimate"] = "expected" },
            Calls =
            [
                new ModelCallInput
                {
                    ModelId = "gpt-5.6-luna",
                    ContextTokens = 45_000,
                    FreshInputTokens = 30_000,
                    CachedInputTokens = 10_000,
                    CacheWriteTokens = 2_000,
                    OutputTokens = 8_000
                }
            ],
            AccessGates = configuration.Gates.ToDictionary(
                gate => gate.Id,
                _ => new AccessGateState { Passed = true },
                StringComparer.OrdinalIgnoreCase),
            BillingContext = new BillingContext
            {
                BillingEntityId = "enterprise-1",
                CycleStart = new DateTimeOffset(timestamp.Year, timestamp.Month, 1, 0, 0, 0, TimeSpan.Zero),
                CycleEnd = new DateTimeOffset(timestamp.Year, timestamp.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1),
                SeatAssignments =
                [
                    new EffectiveSeatAssignment
                    {
                        UserId = "user-1",
                        PlanId = "business",
                        CostCenterId = "cc-engineering"
                    },
                    new EffectiveSeatAssignment
                    {
                        UserId = "user-2",
                        PlanId = "enterprise",
                        CostCenterId = "cc-engineering"
                    }
                ]
            },
            Attribution = new AttributionInput
            {
                UserId = "user-1",
                LicensingOrganizationIds = ["org-engineering"],
                DirectAssignments = [new EffectiveCostCenterAssignment { CostCenterId = "cc-engineering" }]
            },
            EconomicGuardrails = new EconomicGuardrailSnapshot
            {
                EnterprisePoolConsumedCredits = 500m,
                UserLevelBudgets =
                [
                    new UserLevelBudget
                    {
                        Id = "ulb-universal",
                        Kind = UserLevelBudgetKind.Universal,
                        LimitCredits = 2_000m,
                        ConsumedCredits = 250m
                    },
                    new UserLevelBudget
                    {
                        Id = "ulb-cost-center",
                        Kind = UserLevelBudgetKind.CostCenter,
                        TargetId = "cc-engineering",
                        LimitCredits = 1_200m,
                        ConsumedCredits = 150m
                    },
                    new UserLevelBudget
                    {
                        Id = "ulb-user-1",
                        Kind = UserLevelBudgetKind.Individual,
                        TargetId = "user-1",
                        LimitCredits = 750m,
                        ConsumedCredits = 100m
                    }
                ],
                IncludedUsageControls =
                [
                    new CostCenterIncludedUsageControl
                    {
                        Id = "included-cc-engineering",
                        CostCenterId = "cc-engineering",
                        ConsumedCredits = 400m,
                        OverflowBehavior = IncludedOverflowBehavior.PaidUsage
                    }
                ],
                PaidUsage = new PaidUsageAuthorization
                {
                    State = GuardrailValue.Enabled,
                    ProductIds = new HashSet<string> { "github-copilot" },
                    SkuIds = new HashSet<string> { "copilot-ai-credits" }
                },
                SpendingBudgets =
                [
                    new SpendingBudget
                    {
                        Id = "budget-cost-center",
                        Scope = SpendingBudgetScope.CostCenter,
                        ScopeId = "cc-engineering",
                        LimitUsd = 100m,
                        ConsumedUsd = 25m,
                        Enforcement = GuardrailEnforcement.HardStop
                    },
                    new SpendingBudget
                    {
                        Id = "budget-organization",
                        Scope = SpendingBudgetScope.Organization,
                        ScopeId = "org-engineering",
                        LimitUsd = 250m,
                        ConsumedUsd = 40m,
                        Enforcement = GuardrailEnforcement.AlertOnly
                    },
                    new SpendingBudget
                    {
                        Id = "budget-enterprise",
                        Scope = SpendingBudgetScope.Enterprise,
                        LimitUsd = 500m,
                        ConsumedUsd = 75m,
                        Enforcement = GuardrailEnforcement.HardStop
                    }
                ]
            },
            RuntimeGuardrails = new RuntimeGuardrailSnapshot
            {
                MaximumModelCalls = 20,
                MaximumSubagentDepth = 5,
                RequestedSubagentDepth = 2,
                MaximumDuration = TimeSpan.FromMinutes(60),
                RequestedDuration = TimeSpan.FromMinutes(10),
                CliSoftCreditLimit = 2_000m
            },
            ActionsUsage = usesActions
                ? new ActionsUsageInput
                {
                    RunnerId = "linux-2-core",
                    Minutes = 10m,
                    IncludedMinutesRemaining = 1_000m
                }
                : null,
            ActionsGuardrails = usesActions
                ? new ActionsGuardrailSnapshot
                {
                    ActionsEnabled = GuardrailValue.Enabled,
                    RunnerAvailable = GuardrailValue.Enabled,
                    WorkflowApproved = GuardrailValue.Enabled,
                    RepositoryRulesPermitRun = GuardrailValue.Enabled,
                    IncludedMinutes = 2_000m,
                    ConsumedIncludedMinutes = 500m,
                    Budgets =
                    [
                        new ActionsSpendingBudget
                        {
                            Id = "actions-enterprise",
                            LimitUsd = 100m,
                            ConsumedUsd = 10m,
                            Enforcement = GuardrailEnforcement.HardStop
                        }
                    ]
                }
                : null
        };
    }
}
