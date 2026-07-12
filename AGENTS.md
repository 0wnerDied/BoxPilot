# Repository Guidelines

## Project Structure & Tooling

BoxPilot is a .NET 10/Avalonia 12 desktop application. `src/BoxPilot.App/` contains XAML, MVVM view models, themes, localization, tray behavior, and assets. Platform-neutral process, profile, subscription, and Clash API logic belongs in `src/BoxPilot.Core/`. Mirror behavior in `tests/BoxPilot.Core.Tests/`; keep automation in `scripts/` and architecture notes in `docs/`. Never place subscriptions, generated profiles, `bin/`, `obj/`, or `dist/` in Git.

Use the top-level interface:

- `make setup` restores dependencies.
- `make run` launches the development GUI.
- `make build` builds Release artifacts.
- `make test` runs all xUnit tests, including installed-core validation.
- `make lint` runs `dotnet format --verify-no-changes`.
- `./scripts/publish.sh <RID>` creates a self-contained macOS or Windows package.

## Mandatory Code Style

Modern [kerneltoast](https://github.com/kerneltoast) Linux/Android style is merge-blocking: prefer small, direct implementations, standard helpers, explicit failure paths, and measured optimization. Older casual commits are not precedent.

For C#, use four spaces, file-scoped namespaces, nullable annotations, `PascalCase` public members, `camelCase` locals, and `Async` suffixes. Favor immutable records, cancellation tokens, atomic writes, and bounded collections. Make ownership, lifetime, locking, and concurrency assumptions visible. Use early returns or labeled cleanup to limit nesting. XAML and JSON use two spaces. User-visible text must come from `LocalizationService`; add both `zh-CN` and `en-US` entries. Never block the Avalonia UI thread.

Comments explain rationale, races, units, thresholds, or platform/API constraints—not syntax. Performance claims require traces, benchmarks, or before/after numbers. Avoid hot-path allocation and verbose logging unless justified. Run the formatter; do not hand-align code against it.

## Testing Guidelines

Add deterministic xUnit coverage for critical logic: parsing and configuration generation, persistence, subscription conversion, process lifecycle, and failure, cancellation, or concurrency paths. Add a regression test when a defect is likely to recur and the test can assert public behavior. Do not add brittle tests for prose, static style values, simple XAML layout, or packaging file lists; verify those changes with build, lint, and a focused manual smoke test instead. Name tests after observable behavior, such as `ParseClashYamlConvertsVlessGroupsAndRules`. Tests must use temporary directories and must not change system proxy settings. Validate generated configurations with `sing-box check` when available.

## Mandatory Commit & Pull Request Style

Use a subsystem prefix and imperative summary, for example `subscriptions: Preserve Clash selector groups`. Use sentence case, no period, and about 72 characters. After a blank line, explain problem, root cause or constraint, change, and effect; wrap prose near 72–75 columns and include relevant evidence.

Keep one logical change per commit and sign it with `git commit -s`. Use `Fixes:` for regressions. Reverts identify the original commit and justify reversal. PRs must describe the solution, exact verification, linked issues, and UI screenshots or performance evidence when applicable. Never commit credentials, API keys, subscription URLs, or fetched node data.
