# Copilot Usage Simulator Engine

A configurable, UI-agnostic .NET 11 class library for simulating GitHub Copilot AI-credit usage, request blocking, budget allocation, and GitHub Actions runner charges.

The implementation follows [Copilot-Token-Usage-Simulator-Flows.md](Copilot-Token-Usage-Simulator-Flows.md). It can be hosted by a CLI, ASP.NET Core API, desktop application, test harness, notebook, or background service without changing the engine.

## Projects

| Project | Purpose |
|---|---|
| `src/CopilotUsageSimulator.Engine` | Pure simulation domain and JSON configuration loader |
| `tests/CopilotUsageSimulator.Engine.Tests` | Contract and calculation tests |

## Use the engine

```csharp
using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Simulation;

var configuration = EngineConfigurationLoader.LoadDefault();
ICopilotUsageSimulationEngine engine = new CopilotUsageSimulationEngine(configuration);

var result = engine.Simulate(new SimulationScenario
{
    OperationId = "chat",
    PlanId = "business",
    Timestamp = DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
    Calls =
    [
        new ModelCallInput
        {
            ModelId = "gpt-5.6-luna",
            ContextTokens = 25_000,
            FreshInputTokens = 10_000,
            CachedInputTokens = 15_000,
            OutputTokens = 2_000,
            EnabledMultiplierIds = new HashSet<string> { "auto-model-selection" }
        }
    ],
    Budgets = new BudgetState
    {
        IncludedPoolCreditsRemaining = 1_900,
        PaidUsageEnabled = false
    }
});

Console.WriteLine($"{result.Decision}: {result.Allocation.TotalCredits} credits");
```

`SimulationResult.Explanation` contains an ordered decision and calculation trace suitable for direct presentation. `FirstFailingGate` is a stable machine-readable identifier.

## Configuration

The embedded `Configuration/default-catalog.json` contains effective-dated:

- plans and included-credit allowances;
- billed and unbilled operations;
- ordered access gates and operation applicability;
- model prices and context tiers;
- generic cost multipliers;
- GitHub Actions runner prices.

Load a replacement catalog without modifying the engine:

```csharp
var configuration = EngineConfigurationLoader.Load("my-catalog.json");
var engine = new CopilotUsageSimulationEngine(configuration);
```

Configuration is validated at load and engine construction. Unknown references, duplicate IDs, invalid effective periods, invalid context ranges, and negative prices are rejected.

An empty applicability set means "all." IDs are compared case-insensitively. A model may have several non-overlapping `pricePeriods`, making promotions and future price changes data changes rather than code changes. Undocumented future prices should be omitted; the engine then fails explicitly with `pricing-not-effective`.

## Access integration

The engine does not call GitHub, an identity provider, or a network probe. A host evaluates those real-world conditions and supplies their state:

```csharp
AccessGates = new Dictionary<string, AccessGateState>
{
    ["license-seat"] = new() { Passed = true },
    ["network"] = new()
    {
        Passed = false,
        Reason = "Copilot endpoint timed out.",
        Remediation = "Check proxy and allowlist configuration."
    }
}
```

Unspecified gates use the catalog's `passWhenUnspecified` setting. Set it to `false` in strict deployments that require every gate to be supplied.

## Budget integration

`BudgetState` is an immutable snapshot supplied with each request. The engine returns projected remaining balances but does not persist them. This keeps simulations deterministic and lets the host use a database, browser state, files, or no persistence.

The budget engine supports:

- individual, cost-center, and universal user-budget precedence;
- shared included pools;
- cost-center included-usage controls;
- paid-usage enablement;
- cost-center, organization, and enterprise metered budgets;
- hard-stop and alert-only metered budgets;
- split allocation between the remaining pool and metered usage.

Set `poolOverflowBehavior` to `split` or `meterEntireRequest` in the catalog. When a scenario omits `IncludedPoolCreditsRemaining`, the engine derives the initial allowance from its plan; provide an explicit balance for an in-progress billing cycle.

Persist returned balances only after the host accepts a simulation as actual usage. Concurrent consumers should apply their own optimistic concurrency or transaction boundary.

### Rich guardrails

For enterprise attribution and concurrent guardrails, supply `BillingContext`, `Attribution`, and `EconomicGuardrails` together. This opt-in path derives pooled entitlement from effective Business and Enterprise seats, resolves cost centers and licensing organizations, selects the effective ULB, and evaluates every applicable cost-center, organization, and enterprise spending constraint.

```csharp
var scenario = new SimulationScenario
{
    OperationId = "chat",
    PlanId = "business",
    ProductId = "github-copilot",
    SkuId = "copilot-ai-credits",
    Timestamp = cycleTimestamp,
    Calls = calls,
    BillingContext = billingContext,
    Attribution = attributionInput,
    EconomicGuardrails = economicSnapshot,
    RuntimeGuardrails = runtimeSnapshot,
    ActionsGuardrails = actionsSnapshot
};

var result = engine.Simulate(scenario);
```

`AppliedGuardrails` reports every evaluated constraint, `EffectiveUlb` identifies the selected individual, cost-center, or universal limit, and `Alerts` contains thresholds crossed only by accepted charges. Actions access and spending are evaluated before AI-credit allocation, so denied or waiting workflows leave both meters unchanged. Existing callers can continue using `BudgetState`; adding any rich economic input requires all three rich inputs.

## Build

```powershell
dotnet test CopilotUsageSimulator.slnx
```

The repository pins `.NET SDK 11.0.100-preview.7.26381.103` through `global.json`. The SDK is installed in `.dotnet` and the system-wide installation remains unchanged.
