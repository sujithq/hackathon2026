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

## Low-Hanging Fruit

All identified low-hanging findings are resolved.

## Planning Dependencies

- Resolve F-01 before expanding entitlement or plan-selection behavior in other clients.
- Design F-05 before changing terminal-path balance projections; it defines the shared client contract.
- Decide F-06 semantics before changing spending-budget persistence or historical simulation.
- F-04 can proceed independently in Web after Engine input-validation expectations are fixed.
- F-02, F-03, F-07, F-08, F-09, and F-10 were resolved as isolated changes.

## Verification Baseline

At review time:

- Worktree was clean.
- Release tests passed: 144 total (`Common` 6, `Engine` 99, `Web` 39).
- Release build succeeded without errors.
- No vulnerable direct or transitive NuGet packages were reported.

Passing tests do not invalidate these findings; the affected cross-client contracts, combined-failure ordering, malformed-input paths, and terminal balance projections are not currently covered.