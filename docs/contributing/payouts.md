# Contributor payout chart

This file is the **authoritative payout chart** for paid contributions to Cove. It is checked into
source on purpose: every change to the numbers is tracked in git history, so the chart is fully
transparent and auditable over time.

A delivered feature pays out the chart value for its **Complexity** and **Value**, using the version
of this chart **in effect on the date the feature's PR was merged**. (Complexity and Value are
assigned by a maintainer when the issue is approved; see the rubrics below.)

For *how* contributors qualify and *when* payouts happen, see the
[Contribution Guide](../../CONTRIBUTING.md#getting-paid-for-contributions).

## Payout chart (USD)

Find the row for **Complexity** and the column for **Value**.


| Complexity / Value | Value 1 | Value 2 | Value 3 | Value 4 | Value 5 |
| --- | --- | --- | --- | --- | --- |
| **Complexity 1** | $5 | $10 | $15 | $25 | $35 |
| **Complexity 2** | $10 | $25 | $45 | $65 | $95 |
| **Complexity 3** | $30 | $50 | $75 | $100 | $150 |
| **Complexity 4** | $50 | $95 | $150 | $250 | $350 |
| **Complexity 5** | $75 | $125 | $200 | $350 | $450 |

A **0** in either Complexity or Value means the work is **not a paid item** (payout $0). Those
contributions are still welcome as ordinary open-source PRs.

> These amounts depend on the project's financial health and may be revised over time. Because each
> payout is locked to the chart in effect at merge, a later change never reduces (or increases) a
> payout that was already earned.

## Complexity rubric (effort, difficulty, and risk)

| Score | Meaning |
| --- | --- |
| **0** | Trivial: typo, comment, or one-line config. Not a paid item. |
| **1** | Small: Roughly a few hours of work, little risk. |
| **2** | Moderate: a contained feature or fix in one area, frequently spanning multiple files on the front and backend. |
| **3** | Medium: A fairly large new feature spanning accross multiple areas or of high complexity in a more contained area. |
| **4** | Large: cross-cutting work touching backend, UI, and/or data; may include migrations or a new system; careful testing required. Not recommended for first time contributors |
| **5** | Very large: architecturally significant. A major overhaul, massive new feature, etc. Very highly discouraged for first-time contributors |

## Value rubric (impact on users and the project)

| Score | Meaning |
| --- | --- |
| **0** | None: no user-facing or project value. Not a paid item. |
| **1** | Minor: a niche nicety or small polish. |
| **2** | Useful: helps a subset of users; a modest improvement. |
| **3** | Valuable: clearly improves the product for many users. |
| **4** | High impact: a significant feature or fix that many people want. |
| **5** | Critical / major: a headline feature or an essential fix that moves the project forward. |

## How an amount is decided

1. A maintainer evaluates the issue and sets **Complexity** and **Value** (each 0&ndash;5).
2. The evaluated issue is labeled `approved` and shows its Complexity, Value. (which will determine payout when completed)
3. Complexity or Value can be adjusted based on enough complexity evidence or from community requests/engagement.
3. When the implementing PR is **merged**, the payout is the chart cell above for that
   (Complexity, Value), using the chart as it stands on the merge date.