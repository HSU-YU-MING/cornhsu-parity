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
| The latest `2.x` release | ✅ |
| `1.x`, `0.x` and anything older | ❌ |

Fixes land on the newest release only — there are no patch branches for older versions, and no
further 1.x releases. Upgrading to the newest `2.x` is always the fix. The six interfaces (config,
CLI and exit codes, Action inputs, `report.json`, the baseline database, the snapshot format) are
frozen within a major — the rules are in the CHANGELOG's 1.0.0 entry and restated for 2.x in its
2.0.0 entry — so a 2.x upgrade does not break them. Coming from 1.x, work through the 2.0.0 entry's
**升級必做 (upgrade required)** and breaking-change lists first. Anything else a release asks of
you — re-installing Chromium, a newer Node for the npm channel, a changed path inside the uploaded
artifact — is listed at the top of that release's CHANGELOG entry under **升級必做 (upgrade
required)**. If you reference the Action as `@v2` you receive new releases automatically, so check
that heading whenever a release ships — or pin an exact version and upgrade on your own schedule.
(`@v1` stays at 1.1.0, whose action installs the newest release of any major when `version` is
empty; switch to `@v2`.)

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

- **Reference the Action as `@v2`** (tracks the latest 2.x automatically — the interfaces are
  frozen within the major; new releases list anything you must do under **升級必做 (upgrade
  required)** in the CHANGELOG), or pin an exact version (`HSU-YU-MING/cornhsu-parity@v2.0.0`) if
  your policy requires immutable refs.
- **Give the Figma token only `file_content:read`.** Nothing else is used. Pass it through
  `secrets`, never inline in `parity.config.json` — the config's `designToken` field takes
  `env:FIGMA_TOKEN` precisely so the value never has to sit in a committed file.
- **Never wire the Action's `config`, `target`, `version` or `artifact-name` inputs to
  attacker-controlled text** (a PR title, an issue body, a branch name). Those are paths, version
  strings and names; nothing that arrives from outside your repository belongs in them.
- **`pull_request_target` gives a fork's code a write-scoped token.** Parity does not need it —
  a plain `pull_request` trigger is enough. Posting the comment will 403 on fork PRs, which the
  Action tolerates on purpose; the gate still runs.
