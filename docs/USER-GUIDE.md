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

The task description records intent; it does not automatically predict tokens. For uncertainty, save separate low, expected, and high workload scenarios.

To simulate without a cost center, clear the **Cost center** field. The user is then attributed at organization/enterprise level. Cost-center ULBs, included controls, and budgets do not apply, while applicable organization and enterprise constraints remain active.

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

## Cost and attribution

- **AI credits** is the calculated request total.
- **Included credits** come from the applicable included pool.
- **Metered credits** are paid overage.
- **Metered cost** is the overage charge in USD.
- **Actions cost** is the additional runner charge.
- **Attribution** shows the user, licensing organization, cost center, and effective ULB.

Each visible guardrail row reports its outcome, enforcement, limit, prior consumption, requested usage, projected remainder, and explanation. Display all checks, issues only, failures only, or selected categories.

## Advanced scenario configuration

The complete scenario JSON editor exposes every engine input:

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
