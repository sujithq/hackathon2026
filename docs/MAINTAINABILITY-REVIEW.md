# Maintainability Review Findings

Reviewed: 2026-09-02  
Scope: Entire solution, including Common, Engine, Web, tests, configuration, workflows, and documentation  
Status: Findings captured for implementation planning

## Review Principles

- Keep reusable simulation and business behavior in the client-neutral Engine.
- Keep Web limited to rendering, browser persistence, UI state, and client orchestration.
- Preserve deterministic behavior, stable identifiers, ordered explanations, first-failing-gate semantics, and projected balances.
- Resolve applicability, entitlement, and state transitions from the same selected entity identity.

## Findings

### F-01: Plan entitlement depends on the client

- Severity: High
- Effort: Medium
- Evidence: [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) validates that `Scenario.PlanId` exists but does not reconcile it with the effective seat. [`WorkloadEditorAdapter.cs`](../src/CopilotUsageSimulator.Web/Services/WorkloadEditorAdapter.cs) performs that synchronization only for Web.
- Impact: Other Engine clients can calculate an entitlement from a seat plan that conflicts with the scenario's selected plan. Equivalent user intent can therefore produce client-dependent results.
- Recommended direction: Define and enforce the selected-plan/effective-seat invariant in Engine. Leave Web responsible only for editing scenario input.
- Planning acceptance: Add Engine tests for matching, conflicting, missing, and ineffective seat assignments; remove reliance on Web-only normalization.

### F-02: Actions budget failures precede economic failures

- Severity: Medium
- Effort: Small
- Evidence: [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) evaluates Actions budgets before economic guardrails. The canonical phases in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) put the secondary Actions meter after economic allocation.
- Impact: When both meters fail, the result reports the wrong first failing constraint and produces a trace inconsistent with the documented pipeline.
- Recommended direction: Retain Actions access preflight before execution, but evaluate calculated Actions usage and budgets after the economic decision.
- Planning acceptance: Add a simultaneous economic/Actions failure test that asserts the canonical first failure and explanation order.

### F-03: Repeated unbilled operations consume runtime state

- Severity: Medium
- Effort: Small
- Evidence: [`SimulationSessionRunner.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationSessionRunner.cs) advances model-call count and requested duration after every allowed result. [`CopilotUsageSimulationEngine.cs`](../src/CopilotUsageSimulator.Engine/CopilotUsageSimulationEngine.cs) intentionally skips runtime evaluation for unbilled operations.
- Impact: Repetition can exhaust runtime controls for work that did not pass through those controls.
- Recommended direction: Advance runtime state from usage actually evaluated and represented by the result, not directly from scenario requests.
- Planning acceptance: Cover billed, unbilled, cost-only, and partially simulated repeated operations.

### F-04: Import and restoration are not transactional

- Severity: Medium
- Effort: Small to medium
- Evidence: [`HomePageModel.cs`](../src/CopilotUsageSimulator.Web/Services/HomePageModel.cs) mutates catalog/scenario state before all deserialization, adapter mapping, validation, and simulation steps succeed. Its import and reload filters do not cover every failure produced before Engine validation. Failed catalog application does not stop saved-scenario restoration.
- Impact: Malformed but syntactically valid input can escape normal UI error handling or leave mixed old/new state while reporting a successful load.
- Recommended direction: Build and validate temporary catalog, Engine, scenario, form, and result state; commit them only after the complete operation succeeds.
- Planning acceptance: Add malformed import, invalid saved catalog, invalid saved scenario, and previous-state-preservation tests.

### F-05: The result balance contract is incomplete

- Severity: Medium
- Effort: Medium to large
- Evidence: [`SimulationResult.cs`](../src/CopilotUsageSimulator.Engine/Simulation/SimulationResult.cs) cannot represent remaining spending-budget headroom. [`EconomicBalanceCalculator.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicBalanceCalculator.cs) supplies only partial unchanged balances on terminal paths. The minimum result contract in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) requires every spending budget and Actions headroom.
- Impact: Clients cannot consistently render or persist complete projected state, especially for blocked and indeterminate outcomes.
- Recommended direction: Expand the Engine result contract to expose all applicable unchanged/projected balances on every terminal path.
- Planning acceptance: Add contract tests for allowed, blocked, soft-stopped, partially simulated, and indeterminate outcomes.

### F-06: `TrackingStartedAt` is persisted but ignored

- Severity: Medium
- Effort: Medium
- Evidence: [`EconomicGuardrails.cs`](../src/CopilotUsageSimulator.Engine/Guardrails/EconomicGuardrails.cs) defines `TrackingStartedAt`, but Engine applicability and consumption do not use it. The flow document states that first-cycle pre-creation usage must not count toward the budget.
- Impact: First-cycle budget decisions can incorrectly include consumption from before tracking began, or callers can assume semantics the Engine does not implement.
- Recommended direction: Define and enforce tracking-baseline semantics in Engine, or remove the field and documentation until the required consumption history is representable.
- Planning acceptance: Add before-baseline, at-baseline, after-baseline, and later-cycle tests.

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

### F-09: Pages deployment has no test gate

- Severity: Low
- Effort: Small
- Evidence: [`.github/workflows/deploy-pages.yml`](../.github/workflows/deploy-pages.yml) publishes the Web project without first running the solution tests.
- Impact: Engine, Common, or Web regressions can reach GitHub Pages whenever publish itself succeeds.
- Recommended direction: Run the Release solution test suite before publish.
- Planning acceptance: The deployment job must depend on a successful Release test step.

### F-10: Gap-analysis documentation presents superseded behavior as current

- Severity: Low
- Effort: Small
- Evidence: [`Copilot-Guardrail-Gap-Analysis.md`](../Copilot-Guardrail-Gap-Analysis.md) and the pre-implementation matrix in [`Copilot-Token-Usage-Simulator-Flows.md`](../Copilot-Token-Usage-Simulator-Flows.md) describe behaviors that have since been implemented or changed.
- Impact: Maintainers can design new work from an obsolete view of Engine capabilities.
- Recommended direction: Refresh the matrices or clearly mark them as historical snapshots with dates.
- Planning acceptance: Every claimed current gap must match the current Engine contract and tests.

## Low-Hanging Fruit

| Order | Finding | Change | Severity | Effort |
|---:|---|---|---|---|
| 1 | F-02 | Restore canonical economic-before-Actions budget ordering | Medium | Small |
| 2 | F-03 | Stop advancing runtime state for unevaluated usage | Medium | Small |
| 3 | F-09 | Add Release tests to Pages deployment | Low | Small |
| 4 | F-10 | Mark or update stale gap matrices | Low | Small |

## Planning Dependencies

- Resolve F-01 before expanding entitlement or plan-selection behavior in other clients.
- Design F-05 before changing terminal-path balance projections; it defines the shared client contract.
- Decide F-06 semantics before changing spending-budget persistence or historical simulation.
- F-04 can proceed independently in Web after Engine input-validation expectations are fixed.
- F-02, F-03, F-07, F-08, F-09, and F-10 can be planned as isolated changes.

## Verification Baseline

At review time:

- Worktree was clean.
- Release tests passed: 144 total (`Common` 6, `Engine` 99, `Web` 39).
- Release build succeeded without errors.
- No vulnerable direct or transitive NuGet packages were reported.

Passing tests do not invalidate these findings; the affected cross-client contracts, combined-failure ordering, malformed-input paths, and terminal balance projections are not currently covered.