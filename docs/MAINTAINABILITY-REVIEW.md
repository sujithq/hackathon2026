# Maintainability Review Findings

Reviewed: 2026-09-12

Scope: Accuracy of the user-supplied AI-credit decision-flow SVG and `sujithq/ghccp`'s `docs/decision-flow.md`, compared with current official billing documentation and this solution's Engine contracts, applicability, preview client, tests, and supported-scope documentation. The earlier 2026-09-02 whole-solution findings are preserved below; this is not a fresh whole-solution security audit.

Status: No open findings. F-20 through F-25 were resolved upstream and verified against current official evidence, this repository's canonical flow, and Engine behavior.

## Review Principles

- Keep reusable simulation and business behavior in the client-neutral Engine.
- Keep Web limited to rendering, browser persistence, UI state, and client orchestration.
- Preserve deterministic behavior, stable identifiers, ordered explanations, first-failing-gate semantics, and projected balances.
- Resolve applicability, entitlement, and state transitions from the same selected entity identity.

## Current Decision-Flow Findings

Reviewed source: [external `docs/decision-flow.md`](https://github.com/sujithq/ghccp/blob/main/docs/decision-flow.md), retrieved 2026-09-12. Its own research date remains 2026-08-25. Line numbers below refer to that retrieved 92-line Markdown source, not a file in this checkout. The attached single-line SVG has matching nodes and edges but abbreviated labels; SHA-256: `221DB10A4A4B8285F30FD0302A9270B700F80C5672534791CC1296DB6EF804D3`.

The core budget sequence remains useful: effective ULB precedence, cost-center included controls, shared pool, paid authorization only for overflow, scoped spending budgets, and enterprise exclusions. The following changes are necessary before presenting the diagram as a precise request evaluator or current Compass execution flow.

### F-20: Capacity checks do not consistently use remaining, request-sized headroom

- Severity: High
- Effort: Small
- Status: Resolved upstream 2026-09-12.
- Evidence: External `docs/decision-flow.md:21-24,36-46,56-61` compares X with an "allowance"/ULB, asks whether a cost-center cap is already reached, and permits a hard budget that "has room." Current Engine checks the requested credits against remaining ULB/control capacity in `src/CopilotUsageSimulator.Engine/Guardrails/EconomicGuardrailEvaluator.cs:77-123,162-199`, and metered USD against remaining budget headroom at `338-344`.
- Impact: A request can fit the total monthly limit, or find some positive headroom, yet exceed the remaining amount. A partially remaining cost-center cap must still constrain the included portion of a crossing request.
- Required correction: Define X as the incremental requested credits; subtract existing consumption at every capacity check. Ask whether this request would exceed the remaining cap. Compute a provisional included/metered split using both pool and applicable control headroom. A hard budget must cover the proposed metered charge, not merely be nonzero.
- Example: A USD 0.40 proposed charge does not fit a USD 0.20 remaining hard budget. For the Compass demo, 100 required credits with 60 remaining requires 40 metered credits, not 100.
- Dependencies: Preserve effective ULB selection and the first-failing-stage contract. Do not present simulator request sizing as a documented guarantee of GitHub's internal billing granularity.
- Resolution: External `docs/decision-flow.md` now defines X as incremental requested credits, compares individual allowance and effective ULB requests with `limit - consumed`, calculates included headroom from the shared pool and applicable cost-center cap, derives the provisional metered remainder, and requires every hard budget to cover the complete proposed metered charge. The canonical repository flow already documents the same rules, and the Engine required no runtime change.
- Verification: Upstream commit [`5e16df4`](https://github.com/sujithq/ghccp/commit/5e16df4a9a56c036a458ab61e74bb486b1792b05), blob `d580ea65417116cd01fd6ef01edbb422103bdba0`, was re-read after commit. Its predicates match [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md), [`EconomicGuardrailEvaluator.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicGuardrailEvaluator.cs), and the existing Compass preview regressions.

### F-21: The diagram consumes partial included credits before later rejection

- Severity: High
- Effort: Small
- Status: Resolved upstream 2026-09-12.
- Evidence: External `docs/decision-flow.md:45-50` routes "Consume any remaining pool credits" into a paid-policy gate that can reject the request. Current `src/CopilotUsageSimulator.Engine/Simulation/SimulationPipelineContext.cs:24-46` returns zero accepted allocation/reservations/alerts on non-allowed results. `src/CopilotUsageSimulator.Engine/Simulation/SimulationPreviewService.cs:12-49` evaluates a copied snapshot without advancing the input.
- Impact: Used literally as a Compass algorithm, the diagram spends remaining credits on a rejected preview and conflates required usage with accepted consumption.
- Required correction: Rename pre-approval consumption to "Project included allocation and metered remainder." Show acceptance only after every modeled gate succeeds; a blocked Compass preview retains balances. Live work already performed and billed is a separate concern, not something this diagram establishes.
- Dependencies: F-20 defines the split; preserve immutable preview versus explicit session advancement.
- Resolution: External `docs/decision-flow.md` now treats the included/metered split as a neutral projection, labels successful terminal paths as acceptance, and states that paid-policy or spending-budget rejection accepts zero credits and leaves balances unchanged. It also distinguishes transactional simulator preview semantics from live work already performed and billed.
- Verification: Upstream commit [`3e42a5e`](https://github.com/sujithq/ghccp/commit/3e42a5e6c7608b1eafddd11077cc80eb5857b3e0), blob `2cb0c358cb53da902899f506f6092488958ad0a1`, was re-read after commit. The result matches [`SimulationPipelineContext.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationPipelineContext.cs), [`SimulationPreviewService.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationPreviewService.cs), and existing blocked-preview regressions.

### F-22: Metered-budget exits overstate both blocking and unrestricted usage

- Severity: Medium
- Effort: Small
- Status: Resolved upstream 2026-09-12.
- Evidence: External `docs/decision-flow.md:29-33` makes a personal budget shortfall a block without representing alert-only enforcement. Lines `59-61` combine "$0 budget" with hard stops and label a missing/disabled stop "uncapped." The current official [budget setup guide](https://docs.github.com/en/billing/how-tos/set-up-budgets) distinguishes alerts from stops for personal and organizational budgets. The [individual](https://docs.github.com/en/copilot/concepts/billing-and-usage/individuals/billing) and [organizational](https://docs.github.com/en/copilot/concepts/billing-and-usage/organizations-and-enterprises/billing) billing pages both say additional usage may be capped and may require payment before continuing.
- Impact: Disabling one budget's stop can appear to override another applicable hard budget or guarantee unlimited billable usage. Conversely, an alert-only personal budget can appear to deny usage.
- Required correction: First evaluate every applicable hard limit, then distinguish alert-only/missing budgets. Use "No cap from these configured spending budgets; other account/payment/service limits still apply," not unconditional "uncapped." Keep additional-usage authorization distinct from a budget's enforcement mode.
- Qualification: The official organizational budget page contains both broad "any $0 budget" wording and metered-only/stop-enabled descriptions. Do not resolve that ambiguity by claiming every USD 0 control blocks fully included requests. The current Engine explicitly honors enforcement mode; ULBs are always hard stops.
- Dependencies: F-20; account/cohort-specific payment limits remain outside the Compass profile.
- Resolution: External `docs/decision-flow.md` now keeps additional-usage authorization separate from budget enforcement, evaluates all applicable hard budgets before alert-only or missing-budget outcomes, blocks a USD 0 budget only when it is a hard stop, and qualifies nonblocking outcomes with other account, payment, and service limits.
- Verification: Upstream commit [`771aa1f`](https://github.com/sujithq/ghccp/commit/771aa1f2fec2501e433712a27396c0ef9ef07de1), blob `5081d00eca64fde2a9c4a729b14877cf24951036`, was re-read after commit. Mixed hard-stop and alert-only paths preserve F-20 request-sized checks and F-21 transactional acceptance.

### F-23: "Direct cost center" omits valid resolved attribution routes

- Severity: Medium
- Effort: Small
- Status: Resolved upstream 2026-09-12.
- Evidence: External `docs/decision-flow.md:52-54` restricts the cost-center branch label to direct assignment. The official [budget guidance](https://docs.github.com/en/copilot/concepts/billing-and-usage/organizations-and-enterprises/budgets) allows assignment directly, through an enterprise team, or through an organization. `src/CopilotUsageSimulator.Engine/Guardrails/EconomicGuardrailApplicabilityResolver.cs:64-107` consumes the resolved cost center and selects its applicable budgets before the organization fallback and non-excluded enterprise restriction.
- Impact: Team- or organization-assigned members can be routed to the wrong budget. The billing family must also follow the billed identity/licensing source rather than an unrelated personal subscription.
- Required correction: Use "Resolved cost center / applicable cost-center budget" and make prior billing attribution an explicit prerequisite. Preserve documented enterprise-budget exclusions and the existing organization fallback.
- Dependencies: Preserve F-01 selected-seat identity and the existing attribution resolver; no new parallel client-side resolution logic.
- Resolution: External `docs/decision-flow.md` now resolves billed identity, licensing source, and cost-center attribution before ULB or allocation checks; identifies direct-user, enterprise-team, and licensing-organization routes; and uses "Resolved cost center" for metered-budget applicability. The same resolved identity governs downstream ULB, included-control, budget, and enterprise-exclusion decisions.
- Verification: Upstream commit [`5d8d42f`](https://github.com/sujithq/ghccp/commit/5d8d42f3330bf52db6547684337c6bf30dfdf544), blob `fdccd5c1415a2e8ec2edeffd5f7c0f1e94463cdf`, was re-read after commit and compared with [`EconomicGuardrailApplicabilityResolver.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicGuardrailApplicabilityResolver.cs) and the F-01 selected-seat invariant.

### F-24: Financial approval is presented without explicit execution and product boundaries

- Severity: Medium
- Effort: Small for labels; larger only if unsupported cases are implemented.
- Status: Resolved upstream 2026-09-12.
- Evidence: External `docs/decision-flow.md:7-17,25,32,48,61,80-87` covers multiple billing families and ends at "served" or "metered." `src/CopilotUsageSimulator.Engine/Simulation/CompassPreviewProfile.cs:11-74` deliberately limits Compass to B/E September scenarios and supported workloads. `src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs:36-245,253-321` includes attribution/seat, runtime/access, tariff, economics, and a separate later Actions spending check. Current official [code-review documentation](https://docs.github.com/en/copilot/concepts/agents/code-review) includes organization-paid unlicensed reviews outside the ordinary user's included-pool/ULB path.
- Impact: Readers may mistake an AI-budget overview for a complete guarantee that a task runs, that no separate Actions charge exists, or that Compass implements the personal/legacy branches.
- Required correction: Label the diagram an AI-credit financial subflow, with access, model availability/eligibility, effective pricing, and workload costing as prerequisites; exclude Actions and special review attribution explicitly. Use "AI-credit funding permitted" rather than unconditional "served." If used to describe Compass, mark personal/legacy branches unsupported and retain indeterminate/waiting/soft-stop/partial and unpriced outcomes outside this subflow.
- Dependencies: Keep Engine-owned trace and preview contracts authoritative; no reusable rules duplicated in Web.
- Resolution: External `docs/decision-flow.md` is now labeled an AI-credit financial subflow; identifies access/runtime, model availability/eligibility, pricing, and workload costing as prerequisites; uses funding-permitted terminals; and states that execution is not guaranteed. Boundaries separately identify Actions metering, special unlicensed code-review attribution, unsupported Compass personal/legacy branches, and Engine outcomes omitted from the compact diagram.
- Verification: Upstream commit [`f03476d`](https://github.com/sujithq/ghccp/commit/f03476df85cc7ac0fb9472b02e00bc5ae15830eb), blob `b6e12c27a569170405abc5e8d4010abbdfa21ed6`, was re-read after commit and compared with the Engine stage ordering and [`CompassPreviewProfile.cs`](../src/CopilotUsageSimulator.Engine/Simulation/CompassPreviewProfile.cs).

### F-25: Time-sensitive allowance and migration claims lack a current evidence boundary

- Severity: Low
- Effort: Small
- Status: Resolved upstream 2026-09-12.
- Evidence: External `docs/decision-flow.md:3,16-17,20-29` is dated 2026-08-25, gives personal totals without base/flex qualification, and asserts an exclusion for current/former Mobile subscribers plus mandatory annual-term downgrade. The current official [individual billing page](https://docs.github.com/en/copilot/concepts/billing-and-usage/individuals/billing) confirms 1,500/7,000/20,000 totals but identifies flex as variable. Its reset is 00:00 UTC on the first calendar day, not the subscription invoice date.
- Required correction: Keep the currently correct totals, label them dated base-plus-variable-flex values, and spell out the reset boundary. Preserve historic migration branches only with an applicable dated official source and cohort. The current/former Mobile exclusion and mandatory legacy annual downgrade were not reconfirmed on the current individual billing, plan, or plan-management pages checked in this review; absence is not proof that those rules were repealed.
- Dependencies: Use the existing dated source manifest for Compass. Do not overwrite the preserved pre-implementation analysis or invent personal-plan support to match the diagram.
- Resolution: External `docs/decision-flow.md` and its source-of-truth `docs/research.md` now use a 2026-09-12 evidence date, identify individual totals as fixed base plus variable flex, state the `00:00:00 UTC` first-calendar-day reset, and scope the automatic downgrade to the existing annual Pro/Pro+ legacy cohort documented by GitHub's legacy billing page. The unconfirmed current/former GitHub Mobile exclusion was removed in favor of account-specific additional-usage authorization.
- Verification: Decision-flow commit [`6a294c6`](https://github.com/sujithq/ghccp/commit/6a294c631842c6005195190995a3e0fdda84c37f), blob `6afce95bcbeeef22c8ba6045966e9db25128be96`, and research commits [`8a5d747`](https://github.com/sujithq/ghccp/commit/8a5d747a758b8b0665c4a2971b74f22aacbd7ee7) and [`091c833`](https://github.com/sujithq/ghccp/commit/091c833f07d3189ae9bec9408f38008f00e410ac), final blob `9b146879db5cadcd3f561029d900229c51d776ec`, were re-read after commit. Claims were checked against the current official individual billing, plans, plan-management, and cohort-specific legacy billing pages.

## Resolved Whole-Solution Findings

### F-01: Plan entitlement depends on the client

- Severity: High
- Effort: Medium
- Status: Resolved 2026-09-02
- Original evidence: [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) validated that `Scenario.PlanId` existed but did not reconcile it with the effective seat. [`WorkloadEditorAdapter.cs`](../src/CopilotUsageSimulator.Web/Services/WorkloadEditorAdapter.cs) performed that synchronization only for Web.
- Impact: Other Engine clients can calculate an entitlement from a seat plan that conflicts with the scenario's selected plan. Equivalent user intent can therefore produce client-dependent results.
- Resolution: Engine now resolves the attributed user's effective seat at the simulation timestamp and enforces selected-plan consistency independently of Web. A conflicting known plan is rejected as an invalid scenario contract; missing or ambiguous seats produce explicit indeterminate outcomes; unknown seat plans retain the existing seat-inventory outcome.
- Verification: Engine tests cover matching plans in both simulation scopes, case-insensitive identity, conflicting plans, missing assignments, future and expired assignments, unrelated-user assignments, and multiple effective assignments.

### F-02: Actions budget failures precede economic failures

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) evaluated Actions budgets before economic guardrails. The canonical phases in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) put the secondary Actions meter after economic allocation.
- Impact: When both meters fail, the result reports the wrong first failing constraint and produces a trace inconsistent with the documented pipeline.
- Resolution: Actions access preflight remains early, while the calculated Actions budget is now evaluated only after economic guardrails allow the operation.
- Verification: A simultaneous-failure regression asserts that the economic budget is first, and the existing Actions-only rejection test confirms that rejected operations do not allocate AI credits.

### F-03: Repeated unbilled operations consume runtime state

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`SimulationSessionRunner.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationSessionRunner.cs) advanced model-call count and requested duration after every allowed result. [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) intentionally skips runtime evaluation for unbilled operations.
- Impact: Repetition can exhaust runtime controls for work that did not pass through those controls.
- Resolution: Full-scope sessions now advance runtime state only when the result contains evaluated model calls, and consumed call count comes from the result.
- Verification: Session tests cover billed, unevaluated/unbilled, cost-only, and partially simulated repeated operations.

### F-04: Import and restoration are not transactional

- Severity: Medium
- Effort: Small to medium
- Status: Resolved 2026-09-02
- Original evidence: [`HomePageModel.cs`](../src/CopilotUsageSimulator.Web/Services/HomePageModel.cs) mutated catalog/scenario state before all deserialization, adapter mapping, validation, and simulation steps succeeded. Its import and reload filters did not cover every failure produced before Engine validation. Failed catalog application did not stop saved-scenario restoration.
- Impact: Malformed but syntactically valid input can escape normal UI error handling or leave mixed old/new state while reporting a successful load.
- Resolution: Catalog application, saved-state restoration, and import now prepare configuration, Engine, scenario, form, and results before committing live page state. Guided reload handles the same domain failures without replacing the existing form.
- Verification: Focused tests cover malformed imports, invalid saved catalogs, invalid saved scenarios, and preservation of configuration, catalog JSON, scenario JSON, form, results, and display preferences.

### F-05: The result balance contract is incomplete

- Severity: Medium
- Effort: Medium to large
- Status: Resolved 2026-09-02
- Original evidence: [`SimulationResult.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationResult.cs) could not represent remaining spending-budget headroom. [`EconomicBalanceCalculator.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicBalanceCalculator.cs) supplied only partial unchanged balances on terminal paths. The minimum result contract in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) requires every spending budget and Actions headroom.
- Impact: Clients cannot consistently render or persist complete projected state, especially for blocked and indeterminate outcomes.
- Resolution: `RemainingState` now exposes AI and Actions spending-budget headroom by stable budget ID. A centralized Engine snapshot supplies applicable unchanged pool, ULB, included-control, AI budget, Actions-minute, and Actions-budget balances to terminal paths; allowed operations replace those values with projected balances only after the corresponding meter permits the charge. The allocation-level AI balance dictionary remains available for compatibility.
- Verification: Engine contract tests cover allowed AI and Actions projections, multiple applicable budgets, alert-only negative headroom, economic and Actions blocks, soft stops, waiting, partial simulation, indeterminate paid-usage state, and preservation of known pooled balance when other seat inventory is unknown.

### F-06: `TrackingStartedAt` is persisted but ignored

- Severity: Medium
- Effort: Medium
- Status: Resolved 2026-09-02
- Original evidence: [`EconomicGuardrails.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicGuardrails.cs) defined `TrackingStartedAt`, but Engine applicability and consumption did not use it. The flow document states that first-cycle pre-creation usage must not count toward the budget.
- Impact: First-cycle budget decisions can incorrectly include consumption from before tracking began, or callers can assume semantics the Engine does not implement.
- Resolution: Spending-budget applicability now starts inclusively at `TrackingStartedAt` and continues across later billing cycles while the budget remains effective. Before the baseline, the budget is neither evaluated nor included in remaining balances. `ConsumedUsd` is explicitly defined as consumption tracked since the baseline because scenarios do not contain dated usage history.
- Verification: Resolver tests cover timestamps before, at, after, and well after the tracking baseline. Engine tests confirm pre-baseline metered usage is not blocked, exact-boundary usage is evaluated, later-cycle usage remains constrained, and F-05 remaining balances use the same applicability decision.

### F-07: Configuration accepts undefined numeric enum values

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`EngineConfigurationLoader.cs`](../src/CopilotUsageSimulator.Engine/Configuration/EngineConfigurationLoader.cs) allowed integer enum deserialization, while [`EngineConfigurationValidator.cs`](../src/CopilotUsageSimulator.Engine/Configuration/EngineConfigurationValidator.cs) did not validate all configuration enum values.
- Impact: Unknown values can silently enter fallback branches and change charging behavior rather than rejecting malformed configuration.
- Resolution: String enum conversion now rejects integers, and the validator checks every configuration enum at the programmatic boundary.
- Verification: Focused tests cover numeric and unknown string catalog values plus undefined programmatic values for both configuration enums.

### F-08: Duplicate scenario guardrail IDs are accepted

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`SimulationScenarioValidator.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationScenarioValidator.cs) validated individual IDs but not collection uniqueness. [`EconomicBalanceCalculator.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicBalanceCalculator.cs) applies allocations by ID.
- Impact: One allocation can mutate multiple records with the same ID, making repeated simulation state ambiguous and non-reproducible.
- Resolution: Scenario validation now rejects IDs case-insensitively within every mutable guardrail collection.
- Verification: A focused contract test covers ULBs, included controls, spending budgets, and Actions budgets.

### F-11: Plan allowances are not effective-dated

- Severity: Medium
- Effort: Large
- Status: Resolved 2026-09-02
- Original evidence: [`EngineConfiguration.cs`](../src/CopilotUsageSimulator.Engine/Configuration/EngineConfiguration.cs) gave each plan one timeless `IncludedCreditsPerUser` value, and [`EconomicBalanceCalculator.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicBalanceCalculator.cs) applied it to every scenario timestamp. The lifecycle contract in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) requires effective-dated allowances.
- Impact: Historical and future simulations use the catalog's single current allowance even when a different allowance applied at the simulated time.
- Resolution: Plans now define non-overlapping allowance periods with inclusive start and exclusive end timestamps. Pool and cost-center entitlement resolve the period at the simulation timestamp. Missing periods and null allowances produce explicit unknown seat inventory, while empty, overlapping, invalid, and negative allowance periods are rejected as invalid configuration. The timeless allowance property was removed without a legacy fallback.
- Verification: Configuration tests cover empty, overlapping, invalid, and negative periods. Calculator tests cover historical values, exact boundaries, later values, gaps, pooled cost-center entitlement, and non-pooled plans. An Engine regression confirms that a missing effective allowance returns an indeterminate seat-inventory outcome.

### F-12: Duplicate call multipliers compound charges

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`SimulationScenarioValidator.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationScenarioValidator.cs) validated multiplier identifiers but not uniqueness within a call. [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) applies each list entry in sequence.
- Impact: Repeating the same case-insensitive multiplier ID in imported JSON silently multiplies the charge more than once.
- Resolution: Scenario validation now rejects duplicate enabled multiplier IDs case-insensitively before calculation.
- Verification: Focused contract tests cover exact-case duplicates, mixed-case duplicates, and distinct multiplier IDs.

### F-13: Configuration accepts blank stable identifiers

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`EngineConfigurationValidator.cs`](../src/CopilotUsageSimulator.Engine/Configuration/EngineConfigurationValidator.cs) checked uniqueness and references for plan, model, operation, gate, multiplier, Actions runner, and tier IDs without first requiring nonblank identifiers.
- Impact: A catalog can pass Engine construction while containing entities that valid scenarios cannot reference reliably.
- Resolution: Configuration validation now requires nonblank stable IDs before uniqueness and reference processing, including nested price-tier IDs.
- Verification: Focused direct-validation and JSON-loader tests cover plans, models, operations, gates, multipliers, Actions runners, and tiers.

### F-14: Overlapping effective seats inflate shared entitlement

- Severity: Medium
- Effort: Small to medium
- Status: Resolved 2026-09-02
- Original evidence: [`SimulationScenarioValidator.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationScenarioValidator.cs) validated each seat period independently but did not reject overlapping periods for the same user. [`EconomicBalanceCalculator.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicBalanceCalculator.cs) summed every effective pooled seat when deriving enterprise and cost-center entitlement.
- Impact: Two effective pooled seats for one non-attributed user are counted as two licensed seats. This can overstate included-credit headroom and allow usage that should be metered or blocked. The attributed user is protected by F-01 ambiguity handling, but other pool members are not.
- Resolution: Scenario validation now rejects overlapping seat-assignment periods per user, using case-insensitive identity and inclusive-start/exclusive-end intervals. Adjacent and non-overlapping historical periods remain valid.
- Verification: Validator tests cover exact-boundary adjacency, partial and open-ended overlap, case-insensitive identity, distinct users, and non-overlapping history. Engine regressions prove duplicate effective seats cannot inflate enterprise-pool or cost-center entitlement.

### F-15: Browser save can persist a mixed state

- Severity: Medium
- Effort: Medium
- Status: Resolved 2026-09-02
- Original evidence: [`BrowserScenarioPersistence.cs`](../src/CopilotUsageSimulator.Web/Services/BrowserScenarioPersistence.cs) wrote scenario, catalog, and preferences to three independent local-storage keys in sequence.
- Impact: If the second or third write fails because storage is unavailable or full, the browser retains a new scenario with an old catalog or preferences. The next load can reject the mismatched state or restore behavior different from what the user saved.
- Resolution: Browser persistence now serializes scenario, catalog, and preferences into one versioned envelope and commits it with one `localStorage.setItem` call. Loading validates the envelope version and completeness before returning state to the existing transactional restoration path. Legacy three-key state is not loaded.
- Verification: Web service tests cover complete round trips, one-key storage, missing state, unsupported and incomplete envelopes, malformed-state preservation, invalid scenario and catalog preservation, and simulated write failure retaining the previously committed envelope.

### F-16: The Pages build job receives deployment credentials

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`.github/workflows/deploy-pages.yml`](../.github/workflows/deploy-pages.yml) granted `pages: write` and `id-token: write` at workflow scope, so checkout, restore, test, publish, and artifact preparation inherited permissions needed only by deployment.
- Impact: Build scripts and restored tooling execute with unnecessary ability to request an OIDC token and write Pages deployment state, increasing supply-chain blast radius.
- Resolution: Workflow-level permissions now grant only `contents: read`. The deploy job alone receives `pages: write` and `id-token: write`, while retaining read access for the pinned deployment action.
- Verification: YAML validation confirms the build job inherits only read-only contents permission, the deploy job has the two required deployment permissions, and every action remains pinned to a full commit SHA.

### F-17: Empty required catalogs pass configuration validation

- Severity: Medium
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`EngineConfigurationValidator.cs`](../src/CopilotUsageSimulator.Engine/Configuration/EngineConfigurationValidator.cs) rejected null collection items but did not require `Plans` or `Operations` to contain an item. [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) nevertheless requires every scenario to resolve both an operation and a plan.
- Impact: Engine construction succeeds for a configuration that cannot simulate any scenario, moving a catalog contract failure to every later request.
- Resolution: Configuration validation now requires at least one plan and one operation. Models, runners, gates, and multipliers remain optional.
- Verification: Direct-validator and JSON-loader tests reject empty plan and operation catalogs, while a focused regression confirms that optional catalogs may remain empty without references.

### F-09: Pages deployment has no test gate

- Severity: Low
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`.github/workflows/deploy-pages.yml`](../.github/workflows/deploy-pages.yml) published the Web project without first running the solution tests.
- Impact: Engine, Common, or Web regressions can reach GitHub Pages whenever publish itself succeeds.
- Resolution: The Pages build job now runs the Release solution test suite before configuring or publishing the Web project.
- Verification: The workflow's test command passes all solution tests, and publish remains downstream in the same fail-fast job.

### F-10: Gap-analysis documentation presents superseded behavior as current

- Severity: Low
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`Copilot-Guardrail-Gap-Analysis.md`](../Copilot-Guardrail-Gap-Analysis.md) and the pre-implementation matrix in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) described behaviors that have since been implemented or changed.
- Impact: Maintainers can design new work from an obsolete view of Engine capabilities.
- Resolution: Both matrices are now explicitly labeled as historical snapshots of the Engine state on 31 August 2026 and link to this review for current findings.
- Verification: Present-tense current-state matrix labels were removed from both historical documents.

### F-18: README reverses the current budget phase order

- Severity: Low
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`README.md`](../README.md) said Actions access and spending were evaluated before AI-credit allocation. After F-02, Actions access preflight remains early, economic guardrails and allocation run next, and the Actions spending budget runs only after economic approval.
- Impact: Integrators can reproduce the superseded sequence and report the wrong first failing constraint when both economic and Actions budgets fail.
- Resolution: The README now separates Actions access preflight from the economic and Actions spending phases and documents their current Engine order.
- Verification: The documented sequence matches the F-02 simultaneous-failure regression and [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md).

### F-19: README usage sample does not compile

- Severity: Low
- Effort: Small
- Status: Resolved 2026-09-02
- Original evidence: [`README.md`](../README.md) assigned `new HashSet<string>` to `ModelCallInput.EnabledMultiplierIds`, whose contract is `IReadOnlyList<string>`.
- Impact: Consumers copying the primary Engine sample receive a compile-time conversion error before they can evaluate the library.
- Resolution: The sample now uses an ordered collection expression compatible with `IReadOnlyList<string>`.
- Verification: The complete README sample compiles against the current Engine project with zero warnings or errors.

## Low-Hanging Fruit

No open findings remain from this decision-flow review.

## Planning Dependencies

- F-20 through F-25 are resolved in the maintained remote Markdown and supporting research. The separately supplied SVG remains the original reviewed snapshot and was not regenerated; `sujithq/ghccp` does not store a generated SVG under `docs`.
- The [maintained local financial flow](decision-flow.md) now records those upstream resolutions and the preview/Actions clarifications. Its [unchanged upstream snapshot](reference/decision-flow.upstream-2026-09-12.md) is provenance only, not a second maintained specification.
- Preserve the F-01 selected-plan/effective-seat invariant when expanding entitlement or plan-selection behavior in other clients.
- Preserve the F-05 shared balance contract when changing terminal-path projections.
- Preserve the F-06 inclusive tracking-baseline semantics when changing spending-budget persistence or historical simulation.
- Preserve F-11 inclusive-start/exclusive-end allowance semantics when adding historical entitlement behavior or catalog periods.
- F-01 through F-25 are resolved.

## Verification Baseline

Initial review baseline:

- Worktree was clean.
- Release tests passed: 153 total.
- Release build succeeded with zero warnings and errors.
- No vulnerable direct or transitive NuGet packages were reported.

Historical 2026-09-02 implementation baseline:

- Worktree was clean before the F-17 implementation.
- Release tests passed: 218 total.
- Release build succeeded with zero warnings and errors.
- No vulnerable or deprecated direct or transitive NuGet packages were reported.
- `git diff --check` passed.

2026-09-12 decision-flow review baseline:

- Current checkout was clean at `a117c25` (`fix(engine): enforce declared pricing and eligibility constraints`) before this required ledger update.
- Read the attached SVG's node/edge labels and the supplied raw Mermaid source. Rechecked the official organization budgets/billing, individual billing/plans, budget setup, plan management, and code-review pages on 2026-09-12.
- Compared the financial sequence with current Engine allocation, applicability, terminal outcomes, profile, and immutable preview code, plus Web preview wiring and existing regression coverage.
- The prior session's completed Release baseline was 465 passing tests (Common 6, Engine 347, Web 112), with zero build warnings/errors. Tests/build were not rerun for this documentation-only review; this is not a fresh runtime or dependency-security certification.
- Only this ledger was edited; the analysis snapshots, implementation, attached SVG, and remote source were preserved.

Post-fix review at `4af0d7c` (`docs(flows): clarify request-sized capacity checks`):

- The new canonical-flow formulas and examples match the existing Engine behavior and introduce no implementation issue.
- F-20 remained open at that point because its reviewed source was the unchanged external Markdown/SVG; the local clarification was recorded as mitigation rather than resolution.
- `git diff --check` passed and VS Code reported no Markdown errors. Tests/build were not rerun for this documentation-only review.

Post-fix review of upstream F-20 resolution:

- Commits [`0b5dec1`](https://github.com/sujithq/ghccp/commit/0b5dec11c944ad3aca499efff5bc39f30cb3c3d4), [`6732d72`](https://github.com/sujithq/ghccp/commit/6732d72d5d3326d80eb9e92ef70445a7e2e7da77), and [`5e16df4`](https://github.com/sujithq/ghccp/commit/5e16df4a9a56c036a458ab61e74bb486b1792b05) applied the correction and two review repairs directly to `sujithq/ghccp` `main`.
- Review caught and repaired an uncontrolled-pool path that could appear to take the cost-center block edge, then tightened the block predicate to require X to exceed the cost-center control's own remaining headroom.
- The exact final commit and blob were re-read after the last repair. No remaining F-20 issue was found; at that checkpoint, F-21 through F-25 were still independently open.

Post-fix review of upstream F-21 resolution:

- Commit [`3e42a5e`](https://github.com/sujithq/ghccp/commit/3e42a5e6c7608b1eafddd11077cc80eb5857b3e0) changed the crossing-request allocation from a success-styled consumption step to a neutral projection and documented transactional rejection semantics.
- The exact committed blob was re-read against the Engine's terminal-result and immutable-preview contracts. No remaining F-21 issue or F-20 regression was found; at that checkpoint, F-22 through F-25 were still independently open.

Post-fix review of upstream F-22 resolution:

- Commit [`771aa1f`](https://github.com/sujithq/ghccp/commit/771aa1f2fec2501e433712a27396c0ef9ef07de1) separated paid authorization, hard stops, alert-only budgets, and missing-budget outcomes for individual and organization-paid paths.
- Review checked mixed hard-stop/alert-only applicability, USD 0 enforcement, and nonbudget account/payment/service limits. No remaining F-22 issue or F-20/F-21 regression was found; at that checkpoint, F-23 through F-25 were still independently open.

Post-fix review of upstream F-23 resolution:

- Commit [`5d8d42f`](https://github.com/sujithq/ghccp/commit/5d8d42f3330bf52db6547684337c6bf30dfdf544) added billed-identity/licensing-source attribution before ULB resolution and replaced the direct-only metered branch with resolved cost-center applicability.
- Review traced the selected identity through ULB, included-control, spending-budget, organization fallback, and enterprise-exclusion decisions. No remaining F-23 issue or F-20 through F-22 regression was found; at that checkpoint, F-24 and F-25 were still independently open.

Post-fix review of upstream F-24 resolution:

- Commit [`f03476d`](https://github.com/sujithq/ghccp/commit/f03476df85cc7ac0fb9472b02e00bc5ae15830eb) recast the diagram as a financial subflow with explicit prerequisites, funding-only terminals, product boundaries, and Compass support limits.
- Review checked stage ordering, transactional allocation, selected-identity applicability, Actions separation, special review attribution, and omitted Engine outcomes. No remaining F-24 issue or F-20 through F-23 regression was found; at that checkpoint, F-25 was still independently open.

Post-fix review of upstream F-25 resolution:

- Commit [`6a294c6`](https://github.com/sujithq/ghccp/commit/6a294c631842c6005195190995a3e0fdda84c37f) dated and qualified the decision-flow claims. Commits [`8a5d747`](https://github.com/sujithq/ghccp/commit/8a5d747a758b8b0665c4a2971b74f22aacbd7ee7) and [`091c833`](https://github.com/sujithq/ghccp/commit/091c833f07d3189ae9bec9408f38008f00e410ac) aligned the supporting research and repaired a stale USD 0 budget contradiction found during review.
- Review rechecked allowance totals/composition, reset timing, legacy cohort scope, Mobile-rule absence, request-sized headroom, transactional allocation, enforcement modes, attribution, and financial-subflow boundaries. No remaining F-25 issue or F-20 through F-24 regression was found.

Local copy/update requested 2026-09-12 at `588ed8d`:

- Saved the latest upstream `docs/decision-flow.md` at revision `6a294c631842c6005195190995a3e0fdda84c37f` unchanged as `docs/reference/decision-flow.upstream-2026-09-12.md`; its Git blob matches `6afce95bcbeeef22c8ba6045966e9db25128be96`.
- Added the maintained `docs/decision-flow.md` adaptation and README link without reverting any upstream finding resolutions. Fully included allocation is now explicitly provisional; personal hard budgets are checked before alert-only outcomes; remaining Actions approval and immutable-preview semantics are explicit.
- Reconfirmed the cohort-specific annual downgrade using the official legacy billing page, preserving F-25's resolution rather than reviving the earlier unverified claim.
- Implementation code, saved analyses, original SVG, and the remote repository were not modified. This is documentation-only work; no runtime test/build rerun is required.
- Snapshot bytes match the pinned upstream Git blob; the updated diagram's 54 node declarations, 68 edges, class references, reachability, and local document links passed structural checks. `git diff --check` passed.
