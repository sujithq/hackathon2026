# Copilot usage simulator user guide

The web app estimates whether an agent task can run under a given GitHub Copilot billing and guardrail configuration. It runs entirely in the browser and does not send scenario data to a server.

## Quick start

1. Choose **Cloud agent**, **Code review**, or **Chat** as a starting template.
2. Describe the task the agent is expected to perform.
3. Select the operation, plan, repository visibility, and model.
4. Enter expected context, fresh input, cached input, cache-write, and output tokens.
5. Override the cost center, consumed pool, ULB, paid usage, enterprise budget, and Actions minutes.
6. Select **Apply overrides and simulate**.
7. Review the decision, first failing check, cost estimate, attribution, remaining balances, and ordered guardrail checks.

Set **Repeat task** above one to run the same workload sequentially. Successful runs carry their AI-credit pool, ULB, included-control, spending-budget, runtime, Actions-minute, and Actions-budget consumption into the next run. Simulation stops at the first non-allowed result, and the run history identifies exactly which repetition was blocked.

Successful clicks on **Apply overrides and simulate** also advance the current working balances. Clicking it again continues from the previous result. Choose a template or load a saved/imported scenario to reset the starting state. A blocked, waiting, soft-stopped, or indeterminate run does not consume balances.

The task description records intent; it does not automatically predict tokens. For uncertainty, save separate low, expected, and high workload scenarios.

To simulate without a cost center, clear the **Cost center** field. The user is then attributed at organization/enterprise level. Cost-center ULBs, included controls, and budgets do not apply, while applicable organization and enterprise constraints remain active.

Optional guardrails are configured with plain-language cards. Turn off the switch for an individual ULB, included-usage control, cost-center budget, organization budget, enterprise budget, or Actions budget to omit it from the simulation. Each enabled budget exposes its limit, current spending, and stop/alert behavior.

## Result decisions

| Decision | Meaning |
|---|---|
| Allowed | Every required check passed. |
| Blocked | A hard-stop guardrail rejected the task. |
| Soft stopped | A runtime or client limit stopped further work. |
| Waiting | Approval or another external condition is required. |
| Indeterminate | Required billing or policy data is unknown or ambiguous. |
| Partially simulated | The supplied information cannot produce a complete calculation. |

`FirstFailingGate` identifies the check that stopped the simulation. Blocked usage is not deducted from returned balances.

When a run stops, start with the **Why it stopped** card. It names the blocking
scope in plain language, such as agent runtime, individual ULB, cost-center control,
or spending budget. It also shows the configured limit, already-used amount, current
request, and projected remainder. Later checks may not appear because evaluation stops
at the first blocking guardrail.

## Cost and attribution

- **AI credits** is the calculated request total.
- **Included credits** come from the applicable included pool.
- **Metered credits** are paid overage.
- **Metered cost** is the overage charge in USD.
- **Actions cost** is the additional runner charge.
- **Attribution** shows the user, licensing organization, cost center, and effective ULB.

Each visible guardrail row reports its outcome, enforcement, limit, prior consumption, requested usage, projected remainder, and explanation. Display all checks, issues only, failures only, or selected categories.

## Developer scenario configuration

Most simulations do not require JSON. The optional complete scenario JSON editor exposes every engine input when a developer needs a setting that is not available in the guided form:

- task metadata, operation, plan, product, SKU, timestamp, and repository visibility;
- model calls, token classes, and multipliers;
- access gates;
- billing cycle and effective seat assignments;
- direct, team, and organization cost-center attribution;
- individual, cost-center, and universal ULBs;
- included pool consumption and cost-center included-usage controls;
- paid-usage authorization and product/SKU applicability;
- cost-center, organization, and enterprise spending budgets;
- enterprise budget exclusions;
- runtime call, depth, duration, and CLI credit limits;
- Actions access, runner, approval, repository, included-minute, and spending controls.

Select **Simulate JSON** after editing it. Select **Load JSON into guided fields** to synchronize common values back into the form.

## Pricing and policy catalog

The catalog editor controls plans, included allowances, operations, model price periods, context tiers, access gates, multipliers, pool overflow behavior, and Actions runner prices. Select **Apply catalog** before rerunning the scenario. Invalid catalogs are rejected rather than silently using defaults.

## Local storage and files

- **Save locally** stores the scenario, catalog, and visibility preferences in browser storage.
- **Load saved** restores the latest local state.
- **Export JSON** downloads the current scenario.
- **Import JSON** loads and simulates a scenario file.

Clearing browser site data removes saved configurations. Export scenarios that must be retained or shared.
