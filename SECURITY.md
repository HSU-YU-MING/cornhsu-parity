# Security Policy

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Use GitHub's private reporting instead:
[**Report a vulnerability**](https://github.com/HSU-YU-MING/cornhsu-parity/security/advisories/new)
(repository → *Security* → *Advisories* → *Report a vulnerability*). Only the maintainer sees it,
and it stays private until a fix ships.

Please include:

- the output of `parity version` (it carries the commit hash, e.g. `parity 0.13.1+db22749`)
- the channel you installed from — `dotnet tool` (NuGet), `npx` (npm), the GitHub Action, or a
  source build — and your OS
- what an attacker gains, and the smallest reproduction you can manage

This is a single-maintainer project. Expect an acknowledgement within about a week, and a fix or a
concrete plan within 30 days for anything confirmed. If a report goes unanswered for two weeks,
opening a *non-specific* public issue ("filed a private security report, no reply") is a fair
escalation.

## Supported versions

| Version | Supported |
|---|---|
| The latest `0.x` release | ✅ |
| Anything older | ❌ |

Parity is pre-1.0 and the public interfaces are not frozen yet, so fixes land on the newest
release only — there are no patch branches for older versions. Upgrading to the newest `0.x` is
always the fix. From 1.0 onward this table will name a real support window.

## What is in scope

Anything that lets someone reach further than the person running Parity intended:

- **Leaking `FIGMA_TOKEN`** (or any other credential) into logs, reports, artifacts, or PR comments
- **Command or template injection** through config values, layer names, CLI arguments, or the
  GitHub Action's `inputs` — including anything that turns a value into a command on a CI runner
- **Path traversal** when reading a config / design JSON / snapshot, or when writing a report,
  a screenshot, or the baseline database
- **`parity serve`** becoming reachable from outside the machine, or serving files from outside
  the report directory
- **Anything the tool executes that a design file or a scanned page can influence** — Parity drives
  a real browser over Playwright and parses untrusted-ish input on both sides

## What is not a vulnerability

These are deliberate, documented behaviour:

- **`samples/demo` fails on purpose.** The demo page is broken by design so that `parity check`
  returns exit code 1 — that is the gate working, not a bug.
- **`parity check` exiting non-zero.** Blocking CI is the product.
- **The report contains your site's structure** (selectors, text, a screenshot). That is what a
  fidelity report is. It is why `parity serve` binds `127.0.0.1` only, and why you should treat
  `report.json` and the uploaded artifact as being as sensitive as the page they describe.
- **Findings in a dependency with no path to Parity.** Please still tell us — but as an issue, not
  a private report — so we can pin or drop it. Dependabot already watches for these.

## Hardening notes for users

- **Pin the Action to an exact version** while Parity is on `0.x`
  (`HSU-YU-MING/cornhsu-parity@v0.13.1`). A floating `@v1` arrives with 1.0.
- **Give the Figma token only `file_content:read`.** Nothing else is used. Pass it through
  `secrets`, never inline in `parity.config.json` — the config's `designToken` field takes
  `env:FIGMA_TOKEN` precisely so the value never has to sit in a committed file.
- **Never wire the Action's `config`, `target` or `version` inputs to attacker-controlled text**
  (a PR title, an issue body, a branch name). Those are paths and version strings; nothing that
  arrives from outside your repository belongs in them.
- **`pull_request_target` gives a fork's code a write-scoped token.** Parity does not need it —
  a plain `pull_request` trigger is enough. Posting the comment will 403 on fork PRs, which the
  Action tolerates on purpose; the gate still runs.
