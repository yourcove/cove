# Contributing to Cove

Thanks for your interest in making Cove better! Contributions of all kinds are welcome. Cove also **pays contributors** for delivered work
on evaluated issues; see [Getting paid for contributions](#getting-paid-for-contributions).

By participating, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md).

## Ways to contribute

- **Code**: fix bugs and build features (see the paid workflow below).
- **Extensions, themes, scrapers, and downloaders**: start from the
  [extension templates](https://github.com/yourcove/single-extension-repo-template).
- **Docs**: improve the [documentation site](https://yourcove.net/docs/) or the in-app manual.
- **Report bugs or request features**: use the issue templates.
- **Spread the word**: star the repo, join the [Discord](https://discord.gg/MECDFRkzgG), and
  consider supporting the project on [Open Collective](https://opencollective.com/yourcove).

## Development setup

You'll need:

- **.NET 10 SDK**
- **Node.js 22+**

Run Cove in development mode:

```bash
# Terminal 1 — frontend
cd ui
npm install
npm run dev

# Terminal 2 — backend
cd src
dotnet run --project Cove.Api
```

For a production-style build of the frontend first:

```bash
cd ui && npm install && npm run build
cd ../src && dotnet run --project Cove.Api
```

See the [local development guide](https://yourcove.net/docs/developer/getting-started/local-development/)
for more detail.

### Repo at a glance

- `src/` &mdash; .NET backend, data layer, plugins, SDK, and tests
- `ui/` &mdash; React frontend
- `docker/` &mdash; Dockerfiles and compose setups
- `docs/` &mdash; internal notes and project documentation

## Submitting a pull request

1. Find or open an issue describing the work. For **paid** work, the issue must be labeled
   `evaluated` first (see below). Feel free to ask a maintainer to evaluate a `needs-evaluation` issue
   if you want to determine whether to start work on it.
2. Create a branch and make a **focused** change. Don't bundle unrelated work.
3. Add or update tests where it makes sense, and make sure the build and existing tests pass.
4. Open a PR and fill out the [pull request template](.github/pull_request_template.md).
5. A maintainer reviews and merges or may ask questions or for changes.

## AI policy

Using AI tools to help write code is allowed and encouraged. AI is a tool, not a
substitute for engineering judgment. Every change you submit must have a human author who understands
it, has verified it works, and stands behind its design and architecture. *"The AI wrote it"* is
never an explanation for code you can't justify. PRs that read as unreviewed AI output such as wrong
abstractions, nonsense logic, tests that don't actually test, or solutions that don't
fit Cove's architecture will be closed. **Disclose AI use and the model(s) in your PR.** You
own everything you submit, exactly as if you'd typed it yourself.

## Issue lifecycle and labels

Feature and improvement issues move through these stages:

- **`needs-evaluation`**: newly filed; not yet eligible for paid work.
- **`evaluated`**: a maintainer has set a **Complexity** (0&ndash;5) and **Value** (0&ndash;5).
  These two numbers determine the payout (see the [payout chart](docs/contributing/payouts.md)). Only
  evaluated issues are eligible for payment.
- **Delivered**: the implementing PR is merged and accepted.

## Getting paid for contributions

Cove shares donations with the people who build it.

### How the money flows

Donations support the project through [Open Collective](https://opencollective.com/yourcove). Each
month:

- **20%** is distributed to **maintainers**, split by hours worked.
- **80%** goes into the **contributor payout pool**, which is used to pay for delivered features.

### Becoming a Contributor

Payments are reserved for trusted **Contributors**. You earn that status by building a track record:

- **3 or more** merged PRs on `evaluated` issues, **and**
- a combined **Complexity of 8 or more** across total PRs, **and**
- at least **one** PR with **Complexity 2 or higher**, **and**
- a maintainer vouches for the quality of your work (and your adherence to the policies).

So the number of PRs you need depends on their complexity. A few substantial PRs can qualify
you, or several smaller ones.

Once you qualify, your **qualifying PRs are paid retroactively**: they enter the payout queue at their
original merge dates.

### How much a feature pays

The payout for a delivered feature is the value in the
**[payout chart](docs/contributing/payouts.md)** for the issue's Complexity and Value, using the chart
in effect **on the date the PR was merged**. The chart and its rubrics are checked into source, so the
amount for any (Complexity, Value) pair is public and auditable. A `0` in either dimension means the
work isn't a paid item.

### Payout schedule

- Payouts are made on a **monthly** schedule from the contributor payout pool.
- The pool pays delivered features in **chronological order by merge date** (oldest first).
- If the pool can't fully cover the next payout in line, that payout **waits** and is paid in a later
  month when there are enough funds.

### Ongoing responsibility

Quality doesn't end at merge. **A Contributor is responsible for fixing bugs in features they have
previously delivered before becoming eligible for new payouts.** Keeping your past work healthy comes
first.

### Transparency

- The payout chart and every change to it live in [git history](docs/contributing/payouts.md).
- Each `evaluated` issue shows its Complexity, Value, and resulting payout.
- Donations and balances are public on [Open Collective](https://opencollective.com/yourcove).

## License

Cove is licensed under the [GNU AGPL v3](LICENSE). By contributing, you agree that your contributions
are licensed under the same terms.
