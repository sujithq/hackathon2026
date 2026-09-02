# Copilot Usage Simulator Engine

A configurable, UI-agnostic .NET 11 class library for simulating GitHub Copilot AI-credit usage, request blocking, budget allocation, and GitHub Actions runner charges.

The implementation follows [Copilot-Token-Usage-Simulator-Flows.md](Copilot-Token-Usage-Simulator-Flows.md). It can be hosted by a CLI, ASP.NET Core API, desktop application, test harness, notebook, or background service without changing the engine.

## Projects

| Project | Purpose |
|---|---|
| `src/CopilotUsageSimulator.Common` | Shared guardrail metadata, stable identifiers, cost classification, and GitHub documentation links |
| `src/CopilotUsageSimulator.Engine` | Pure simulation domain and JSON configuration loader |
| `src/CopilotUsageSimulator.Web` | Standalone Blazor WebAssembly simulator for GitHub Pages |
| `tests/CopilotUsageSimulator.Common.Tests` | Shared metadata contract and documentation-link tests |
| `tests/CopilotUsageSimulator.Engine.Tests` | Contract and calculation tests |
| `tests/CopilotUsageSimulator.Web.Tests` | bUnit component, guided-workflow, and serialization tests |

## Use the engine

```csharp
using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Guardrails;
using CopilotUsageSimulator.Engine.Simulation;

var configuration = EngineConfigurationLoader.LoadDefault();
ICopilotUsageSimulationEngine engine = new CopilotUsageSimulationEngine(configuration);

var result = engine.Simulate(new SimulationScenario
{
    OperationId = "chat",
    PlanId = "business",
    ProductId = "github-copilot",
    SkuId = "copilot-ai-credits",
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
            EnabledMultiplierIds = ["auto-model-selection"]
        }
    ],
    BillingContext = new BillingContext
    {
        BillingEntityId = "enterprise-1",
        CycleStart = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        CycleEnd = DateTimeOffset.Parse("2026-09-01T00:00:00Z"),
        SeatAssignments =
        [
            new EffectiveSeatAssignment { UserId = "user-1", PlanId = "business" }
        ]
    },
    Attribution = new AttributionInput { UserId = "user-1" },
    EconomicGuardrails = new EconomicGuardrailSnapshot
    {
        PaidUsage = new PaidUsageAuthorization { State = GuardrailValue.Disabled }
    }
});

Console.WriteLine($"{result.Decision}: {result.Allocation.TotalCredits} credits");
```

`SimulationResult.Explanation` contains an ordered decision and calculation trace suitable for direct presentation. `FirstFailingGate` is a stable machine-readable identifier. Each applied guardrail carries a shared `MetadataKey`; hosts can resolve it through `GuardrailMetadataCatalog` for a consistent label, category, settings anchor, documentation link, unit, and cost classification.

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

`BillingContext`, `Attribution`, and `EconomicGuardrails` are immutable snapshots supplied with every billed request. The engine returns projected remaining balances but does not persist them. This keeps simulations deterministic and lets the host use a database, browser state, files, or no persistence.

The budget engine supports:

- individual, cost-center, and universal user-budget precedence;
- shared included pools;
- cost-center included-usage controls;
- paid-usage enablement;
- cost-center, organization, and enterprise metered budgets;
- hard-stop and alert-only metered budgets;
- split allocation between the remaining pool and metered usage.

Set `poolOverflowBehavior` to `split` or `meterEntireRequest` in the catalog. The engine derives the pool entitlement from effective seat assignments and subtracts `EnterprisePoolConsumedCredits` for the in-progress billing cycle.

Persist returned balances only after the host accepts a simulation as actual usage. Concurrent consumers should apply their own optimistic concurrency or transaction boundary.

### Economic guardrails

The single economic model derives pooled entitlement from effective Business and Enterprise seats, resolves cost centers and licensing organizations, selects the effective ULB, and evaluates every applicable cost-center, organization, and enterprise spending constraint.

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

`AppliedGuardrails` reports every evaluated constraint, `EffectiveUlb` identifies the selected individual, cost-center, or universal limit, and `Alerts` contains thresholds crossed only by accepted charges. Actions access is checked before economic evaluation. Economic guardrails and AI-credit allocation run next, followed by the Actions spending budget only after economic approval. This preserves the economic constraint as the first failure when both spending meters would reject the request.

## Web simulator

The standalone web client runs the engine entirely in the browser. It provides guided task, workload, cost-center, ULB, budget, and Actions overrides, plus complete JSON editors for every scenario and catalog setting. Every evaluated check is visible by default and can be filtered by outcome or category.

See the [end-user guide](docs/USER-GUIDE.md), which is also available from the **User guide** link inside the app.

```powershell
dotnet run --project src\CopilotUsageSimulator.Web
```

Scenarios, custom catalogs, and display preferences can be saved to browser storage. Scenario JSON can also be imported and exported. No usage data is sent to a server.

The workflow in `.github\workflows\deploy-pages.yml` publishes the static WebAssembly output to GitHub Pages on pushes to `main`. Enable **Settings → Pages → Source: GitHub Actions** in the destination repository.

## Build and test

```powershell
dotnet test CopilotUsageSimulator.slnx
```

The web tests run through bUnit without starting a browser. They cover template rendering,
cost-only visibility, repeated simulations, blocking-setting highlights, validation errors,
official documentation links, shared controls, and JSON round trips.

The repository pins `.NET SDK 11.0.100-preview.7.26381.103` through `global.json`. The SDK is installed in `.dotnet` and the system-wide installation remains unchanged.
