# Repository Guidelines

## Project Structure & Tooling

BoxPilot has no source, tests, build manifest, or Git metadata. Keep root files project-wide. Put production code in `src/`, tests in `tests/`, assets in `assets/`, and tools in `scripts/`; mirror source paths under `tests/`. The first implementation must provide reproducible commands—prefer `make setup`, `make run`, `make test`, and `make lint`—and document dependencies here. Never claim an unimplemented command works.

## Mandatory Code Style

Modern [kerneltoast](https://github.com/kerneltoast) Linux/Android style is merge-blocking; older casual changes are not precedent.

- For C, use Linux kernel layout: tabs (8-column stops), function braces on the next line, control braces on the same line, and roughly 80-column lines. Use `snake_case` for functions/variables, lowercase struct names, and `UPPER_SNAKE_CASE` for macros and enum values.
- Prefer small, direct implementations and existing helpers over open-coded loops or speculative abstractions. Remove redundant work; keep patches narrowly scoped.
- Make ownership, lifetime, error paths, locking, and concurrency assumptions explicit. Use early returns or labeled cleanup to limit nesting.
- Comments explain rationale, races, units, thresholds, or API constraints—not obvious syntax. Performance claims require traces, benchmarks, or before/after numbers. Avoid hot-path allocations and verbose logging unless justified.
- For non-C code, use the ecosystem formatter while preserving descriptive names, minimal control flow, explicit failures, and measured optimization.

## Testing Guidelines

Every behavior change requires deterministic tests, including relevant failure and concurrency paths. Name tests after observable behavior, such as `test_rejects_expired_token`. Bug fixes require a regression test. Until a framework exists, state manual verification precisely; once added, `make test` must run the complete suite.

## Mandatory Commit & Pull Request Style

Use kerneltoast's current kernel-style subjects: subsystem prefix, colon, then an imperative summary, for example `sched/fair: Fix stale capacity updates`. Use sentence case, no trailing period, and about 72 characters. After a blank line, explain the problem, root cause or constraint, change, and observable effect. Wrap prose near 72–75 columns; include relevant traces, warnings, reproduction steps, or measurements.

Keep one logical change per commit and sign authored commits with `git commit -s`. Add `Fixes:` for regressions and preserve review/provenance trailers. Reverts use `Revert "..."`, identify the original commit, and justify reversal. Imports name the source tree or artifact; use `Change-Id` only for Gerrit.

Pull requests must describe the problem and solution, list exact verification, link issues, and include screenshots, logs, or performance evidence when applicable. Never commit secrets; provide sanitized examples such as `.env.example`.
