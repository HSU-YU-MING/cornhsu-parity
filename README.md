# Parity

[![NuGet](https://img.shields.io/nuget/v/Cornhsu.Parity.svg?label=Cornhsu.Parity)](https://www.nuget.org/packages/Cornhsu.Parity)
[![npm](https://img.shields.io/npm/v/cornhsu-parity.svg?label=cornhsu-parity)](https://www.npmjs.com/package/cornhsu-parity)
[![Marketplace](https://img.shields.io/badge/GitHub_Marketplace-Action-2ea44f?logo=github)](https://github.com/marketplace/actions/parity-design-fidelity-check)
[![CI](https://github.com/HSU-YU-MING/cornhsu-parity/actions/workflows/ci.yml/badge.svg)](https://github.com/HSU-YU-MING/cornhsu-parity/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[Project write-up](https://cornhsu.com/parity) · [GitHub Marketplace](https://github.com/marketplace/actions/parity-design-fidelity-check) · [npm](https://www.npmjs.com/package/cornhsu-parity) · [NuGet](https://www.nuget.org/packages/Cornhsu.Parity) · [繁體中文](README.zh-Hant.md) · MIT**

## GitHub Action

```yaml
- uses: HSU-YU-MING/cornhsu-parity@v0.12.0   # pin exact versions while 0.x; a floating @v1 arrives with 1.0
  with:
    config: parity.config.json      # optional, defaults to parity.config.json
    target: /pricing                # optional, check only this route (omit for all)
```

Fails CI when the gap exceeds the gate threshold, and posts the fidelity report as a PR
comment that updates in place instead of piling up.

> **Design fidelity checking that can actually gate your CI (property-level design QA).**
> It compares the **real numeric values on both sides** — your Figma file and the actually
> rendered page — programmatically: "padding is 8px, the design says 12px." Because the
> output is numbers rather than an impression, it can be a gate: **a PR that drifts from the
> design turns red**, with the fidelity report posted straight onto the PR.

**Other tools help you *see* design drift. Parity *blocks* it.**

Most comparable tools (Uiprobe, Loupe, OverlayQA, Floto Design Diff, PixelProof…) do numeric
comparison these days. The real difference is not "numbers vs. overlays" — it is **whether the
whole thing can be automated**:

|  | Manual spot-check tools<br>(Uiprobe / OverlayQA / Loupe …) | **Parity** |
|---|---|---|
| Who runs it | A person opens the tool, clicks around, reads a report | **CI runs it**, nobody has to be present |
| After a gap is found | File a ticket yourself, or paste the result into the PR | **Exit code blocks the PR** + a bot comment that updates in place |
| Regression gating | None | **A baseline gates only new/worsened gaps** — projects with existing debt can adopt it today |
| When there is no Figma file | Unusable | **Snapshot mode**: freeze today's rendering as the baseline so refactors cannot drift |
| Desktop apps | Not supported | **Electron** (attaches to the live window over CDP) |
| Price | $39–249 / month (Loupe is free but is a desktop GUI — not automatable) | **Free, MIT** |

> Being honest about the boundaries: localhost and authenticated pages are **no longer a
> differentiator** — Uiprobe and OverlayQA support them too. Neither is "free and open source" —
> Loupe is also MIT. **What is unique to Parity is the intersection: free × automatable.**

## Screenshots

**Report UI (`parity serve --watch`)** — the left column lists every node, matching or not;
the right side overlays the design boxes and the implementation boxes directly on the real
screenshot. Click any row to locate it; saving a file triggers a re-scan.

![Parity report UI: diff list on the left, design and implementation boxes overlaid on the page](https://raw.githubusercontent.com/HSU-YU-MING/cornhsu-parity/master/docs/serve-ui.png)

**Fidelity report** — each row is a concrete "expected → actual" pair with its severity and a
suggested fix (including the matching design token). The GitHub Action posts this verbatim as a
PR comment, updating the same comment rather than adding new ones.

![Parity fidelity report: each row an expected→actual numeric gap with a suggested fix](https://raw.githubusercontent.com/HSU-YU-MING/cornhsu-parity/master/docs/report.png)

## At a glance

| | |
|---|---|
| Releases | 22 on NuGet (v0.1.0 → v0.12.0). Pushing a tag publishes via OIDC Trusted Publishing — zero long-lived keys in the repo |
| Compared properties | Size, padding, spacing, typography, color (CIEDE2000 ΔE), relative position — **absolute coordinates are deliberately excluded** (they are guaranteed false positives in a flexible layout) |
| Design sources | 4: the Figma API, a rendering snapshot, an image + annotations (pixel sampling — any tool that exports a PNG works), or JSON |
| Implementation sources | The web (including **shadow DOM, same-origin iframes, and multiple responsive breakpoints**) plus **Electron** (attached to the live window over CDP) |
| Tests | **160**, covering the CIEDE2000 reference data set (Sharma), match disambiguation, position false-positive guards, and image sampling |
| Proven in CI | The GitHub Action is validated by **real PRs in an external repo**: blocking the PR, commenting automatically (updating in place), and baseline regression gating |
| Proven in production | **All 21 pages of cornhsu.com are gated by Parity itself** — dogfooding has already found and fixed three flaky root causes (most recently a scroll-triggered entrance animation; see 0.11.1) |

## Install

Two channels — pick whichever runtime you already have. **They are functionally identical.**

```sh
# If you have Node (common in front-end projects): no .NET install needed
npx cornhsu-parity check

# If you have .NET
dotnet tool install -g Cornhsu.Parity
```

> The npm build is the same self-contained native executable; you only download the one for
> your platform (win32-x64 / win32-arm64 / linux-x64 / linux-arm64 / darwin-x64 / darwin-arm64).
> Playwright normally bundles its own copy of Node; the npm build strips that out and uses the
> Node you already have, which saves about 88 MB per platform package.

## Quick start

```sh
parity init                # Generate a parity.config.json template
parity install-browser     # First run only: download Playwright Chromium
export FIGMA_TOKEN=...     # Only the file_content:read scope is needed
parity check               # Compare; writes a report and sets the exit code
parity report              # Re-render the Markdown report from an existing report.json
                           #   (no re-scan; --md writes a file, otherwise it prints to stdout)
parity snapshot            # Freeze the currently rendered UI as the design baseline —
                           #   a refactor/redesign guard that needs no Figma at all
parity lint                # Design lint: are the design file's values inside the allowed
                           #   design-token set? (design only, no implementation)
parity check --reverse     # Reverse view, for a designer redrawing an existing page:
                           #   how does my draft differ from what ships? (never gates)
```

When working inside this repo:

```sh
dotnet run --project src/Parity.Cli -- check --config samples/demo/parity.config.json
```

`samples/demo` is a fully **offline demo** (the design source is a local JSON file, so no Figma
token is needed): `index.html` is deliberately broken in a few places, `parity check` reports
each gap and the unmatched list precisely, and returns exit code 1 (GATE FAIL) because of the
serious-severity gaps — which is exactly the behaviour you want from a CI gate.

## Configuration: `parity.config.json`

```jsonc
{
  "figmaFileKey": "abcd1234",
  "designToken": "env:FIGMA_TOKEN",
  "mapFile": "parity.map.json",              // manual fallback mapping (layer name → selector)
  "tokensFile": "tokens.json",               // optional: design tokens (name → value);
                                             //   suggested fixes will name the matching token
  "targets": [
    { "route": "/", "frame": "10:2", "url": "http://localhost:8080/" }
  ],
  "compare": { "position": "relative" },     // relative = compare relative position (default); none = skip position
  "tolerances": { "sizePx": 2, "spacingPx": 2, "colorDeltaE": 2.0, "positionPx": 4 },
  "ignore": ["[data-parity-ignore]"],
  "gate": {
    "failOn": ["critical", "serious"],
    "minMatchRate": 0                        // optional: FAIL outright below this match rate (0–1); 0 = no threshold
  }
}
```

> Besides gaps, the gate also validates **match integrity**: zero matches (or zero design nodes)
> is always a GATE FAIL — with nothing matched there are no gaps to catch, so a silent PASS
> would be a false pass, usually caused by a wrong url or frame. `--baseline` mode is not exempt.

## Targets: a web page or an Electron desktop app

`target.url` decides what the implementation side is; the commands and the report are identical:

| url form | What it means |
|---|---|
| `http(s)://…` | A normal web page, or internal staging |
| `file://…` (or a relative path) | A local HTML file |
| `cdp:http://host:port` | **An already-running Electron desktop app** (reads the DOM of the live window) |

For Electron, start the app with a remote debugging port and point `url` at it. Parity attaches
and reads the current screen — it does not navigate, and does not disturb the app:

```sh
electron . --remote-debugging-port=9222      # your app, with this one extra flag
```
```jsonc
{ "route": "/", "frame": "20:5", "url": "cdp:http://localhost:9222" }
```

> Why Electron is nearly free: its UI *is* a Chromium renderer, so it uses the same DOM/CSS
> measurement path as the web. Mobile native, Flutter and native desktop do not go through a
> DOM, so they are left for v2.0.

## Design sources: Figma, a snapshot, or just an image

| Design source | config | Who it is for |
|---|---|---|
| **Figma** (primary) | `figmaFileKey` + `FIGMA_TOKEN` | The normal workflow, when you have a Figma file |
| **Snapshot** (`parity snapshot`) | `designFile` pointing at the generated snapshot JSON | **Refactor/redesign guard**: today's rendering is correct, so freeze it as the baseline and later checks prove nothing drifted (the numeric version of visual regression). Matching goes through selector identity, so it is 100% deterministic |
| **An image + annotations** | `designImage` (PNG/JPG) + `designFile` (annotations) | When all you have is a picture: an outsourced PNG, or a legacy project down to screenshots. **This is also the path for XD / Sketch / Photoshop and anything else that can export an image** — a universal adapter. The annotations are DesignNode JSON, and `fill` may be omitted: the engine samples the color from the corresponding region of the image. (Text color is deliberately not sampled — anti-aliasing blends it and the reading is unreliable — but you can fill it in by hand.) |
| Hand-written JSON | `designFile` | Offline demos and tests |

```sh
# The three-step refactor guard:
parity snapshot            # 1. Freeze the current rendering (writes parity.snapshot.json + a reference screenshot)
#    Point designFile at it in the config, and set each target.frame to its route
parity check               # 2. Refactor fearlessly; 3. check proves it still matches the snapshot
```

## What gets compared, and what does not

Only the things that should hold no matter how the layout flows:

- **Intrinsic size** (width/height — except on TEXT nodes, where text-box measurement is
  inherently different between Figma and a browser and comparing it produces a flood of noise)
  - The auto-layout **HUG (fit content) / FILL (fill parent)** axis is skipped too: Figma's
    measured width necessarily differs from the browser's rendered width there. Only **FIXED**
    axes are compared.
- **Padding / spacing**: all four padding edges, and auto-layout `itemSpacing` against the real
  gap between child elements.
- **Relative position** (`offsetX`/`offsetY`): for children of freely positioned (non-auto-layout)
  containers, the offset relative to the nearest reliable sibling or parent edge — which is what
  catches "size and color are perfect, but it is in the wrong place." Only reliable edges are used
  as references (TEXT/HUG boxes are never references, TEXT is never a target, and Y is honestly
  skipped when everything above is text), so line-height drift in a flowing layout does not
  produce false positives. Turn it off with `compare.position: "none"`.
- **Typography**: size, weight, line-height and letter-spacing are compared exactly;
  font-family is a **soft gap** and never gates.
- **Color**: thresholded on CIEDE2000 (ΔE), not hex equality. Parsing covers modern syntax
  (`rgb(37 99 235 / .5)`, `color(srgb …)`, `oklch()`, `color(display-p3 …)`).
- **Absolute x/y is deliberately never compared**: it legitimately differs in a flexible layout,
  so comparing it means false positives, which means losing trust.

> When the design source is Figma, layer names in the report (both the Markdown and the local UI)
> **link back to that node in Figma** — a designer clicks once and lands on the layer.

## Two workflows for designers

**Keeping a new page inside the design system: `parity lint`.** It looks only at the design side
and checks whether each node's values are in the allowed set from `tokensFile`: color (a hit
within the ΔE tolerance passes), and fontSize / padding / itemSpacing / cornerRadius (equal to
any size token passes — spacing, font sizes and corner radii share one scale). Every violation
carries "the nearest token", so the message is "change it to this", not merely "you are wrong".
Violations exit 1, so it can run in CI. Dimensions you have not defined tokens for are not linted.

**Redrawing against what ships: `parity check --reverse`.** The direction flips: "expected"
becomes the current implementation and "actual" becomes your draft. That gives a designer a list
of "how my draft differs from what is live today". It never gates, and always exits 0.

## Responsive breakpoints

One URL, several breakpoints, each mapped to its own Figma frame — **the render viewport is the
frame's size**, so a 375-wide phone frame makes your media queries fire naturally:

```jsonc
"targets": [
  { "route": "/desktop", "frame": "10:2",  "url": "http://localhost:8080/" },
  { "route": "/mobile",  "frame": "10:99", "url": "http://localhost:8080/" }   // a 375-wide phone frame
]
```

`route` is only a label in the report, so pick something recognizable. You only need `width` /
`height` on a target when the frame's width is not the viewport width you want to test.

## Shadow DOM / iframes

Capture walks the **composed tree**: open shadow roots, content slotted through `<slot>`, and
same-origin iframes (including `srcdoc`) are all visible and all compared — web-component sites
are no longer wholly invisible. Selectors for elements inside a shadow root or iframe are written
as `host >>> inner path`.

Limitations, stated honestly: closed shadow roots and cross-origin iframes are unreachable and
are skipped; map-file selectors cannot reach inside a shadow root (the `data-parity` attribute is
unaffected and works as usual).

## Matching strategy (anchored on the design side)

1. **Automatic text anchoring**: design TEXT content ↔ page text (matched only when unique;
   when several elements share the text, the layer name disambiguates — and it still refuses to force a match)
2. **Layer name ↔ id / class / aria-label**: `CTA Button` matches `class="cta-button"` automatically
3. **Container inference**: an unmatched container is inferred from the lowest common ancestor of
   its already-matched descendants (pure structure — no guessing)
4. **Manual fallback**: add `data-parity="Layer Name"` in your HTML, or write
   `{ "Layer Name": "CSS selector" }` in `parity.map.json`
5. Still no match → **honestly listed as unmatched**, so you only fill in what genuinely needs a human

## Architecture

One engine (`Parity.Engine`, a pure library) plus several shells. The engine only ever compares
two normalized trees:

```
IDesignSource ──→ DesignNode tree ─┐
  (Figma / JSON / later XD, Sketch) ├─→ Normalizer → Matcher → DiffEngine → FidelityReport
IImplementationSource → RenderedNode ┘
  (Playwright / later WPF, desktop)
```

```
src/Parity.Engine/        The engine; FidelityEngine is the only entry point
  DesignSources/          IDesignSource + Figma (REST + local cache) / Json
  ImplementationSources/  IImplementationSource + Web (Playwright)
  Comparison/             Normalizer / Matcher / DiffEngine / ColorDelta (CIEDE2000)
src/Parity.Cli/           The dotnet tool shell: parity check / init / install-browser
tests/Parity.Tests/       Unit tests (including the CIEDE2000 reference data)
samples/demo/             Offline demo: a deliberately broken page + a design JSON
```

## Local report UI (M3)

```sh
parity serve --watch    # http://127.0.0.1:4321, re-scans on file changes (live updates over SSE)
parity map              # Interactive matching: click an unmatched layer → click the element on the
                        #   screenshot → writes parity.map.json
```

- A gap list sorted by severity, with exact numbers, color swatches and ΔE
- A screenshot overlay: solid = implementation box (colored by severity), blue dashed = design
  box, red dashed = unmatched
- **Binds 127.0.0.1 only**: the report contains your site's structure, so it must not be
  reachable from the local network
- The UI is a zero-build static SPA, so the dotnet tool needs no Node toolchain

## In CI (M4)

Gating CI is the whole differentiator, and this repo is itself a composite action:

```yaml
# .github/workflows/design-check.yml (in your project)
name: Design fidelity
on: [pull_request]
jobs:
  parity:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      pull-requests: write   # lets the action post the fidelity report as a PR comment
    steps:
      - uses: actions/checkout@v4
      # Start your site (or deploy to a preview URL and point the config's url at it)
      - run: |
          npm ci && npm run build
          npm run preview &   # e.g. serving on localhost:8080
      - uses: HSU-YU-MING/cornhsu-parity@v0.12.0   # pin exact versions while 0.x
        with:
          config: parity.config.json
          figma-token: ${{ secrets.FIGMA_TOKEN }}   # omit when the design source is a local JSON file
```

Behaviour:

- **PR comment**: posts a fidelity report (score + gap table + **suggested fixes**) and keeps
  updating that same comment instead of adding new ones — so PMs and reviewers see it without
  ever touching the tool.
- **Blocks the PR**: gaps beyond `gate.failOn` → exit 1 → red PR. (**The comment is posted
  first, then the gate is enforced.**)
- **Artifacts**: `report.json` plus the Markdown report are uploaded for download.

Action inputs: `config` / `target` / `working-directory` / `version` / `figma-token` /
`baseline` (regression mode) / `comment` (turn off the PR comment) / `upload-report`.

> The action installs the tool with `dotnet tool install -g Cornhsu.Parity` (published on
> nuget.org). This repo's own `.github/workflows/ci.yml` instead **builds from source** and runs
> the offline demo as a self-check, so it does not depend on a published release.

## Regression gating: baselines (M5)

A project that already has a pile of gaps cannot start at "zero gaps or no merge". A baseline
lets you **gate only new and worsened gaps**:

```sh
parity baseline save     # Store the current gaps + fidelity score as a baseline snapshot
                         #   (SQLite, in parity.baseline.db, tagged with the git commit)
parity check --baseline  # Compare against the newest baseline: only new or worsened gaps GATE FAIL,
                         #   and the score trend is shown (baseline 75 → now 83 ↑)
parity baseline list     # Browse snapshot history (the score column is the fidelity trend, for PMs)
```

> **To use `--baseline` in CI, remember to `git add parity.baseline.db`.** It deliberately lives
> at the repo root rather than in `.parity/`, which is usually gitignored — otherwise CI cannot
> find the baseline and silently falls back to the normal gate. The path is configurable via
> `baselineFile`.

- **New** (absent from the baseline, present now) or **worsened** (higher severity) → exit 1, PR blocked
- **Fixed** (in the baseline, gone now) is listed as encouragement; **unchanged** existing gaps do not gate
- Ideal for gradual adoption: record reality with `baseline save`, then run `check --baseline` in
  CI, and all the team has to commit to is "do not make fidelity worse"

The storage layer is a separate `Parity.Storage` (EF Core + SQLite, with `Pooling=False` so the
file is released immediately), and the engine's `BaselineComparer` is a pure, unit-testable function.

## Milestones

- [x] **M1** Engine + CLI prototype: two numeric trees, design side and implementation side
- [x] **M2** Comparison engine: matching + numeric diffing + tolerances + unmatched list + gate exit codes
- [x] **M3** Local report UI (`parity serve --watch`, Kestrel bound to 127.0.0.1) + `parity map` interactive matching
- [x] **M4** GitHub Action: a reusable composite action (`action.yml`) + this repo's own CI (build / test / offline-demo self-check)
- [x] **M5** EF Core + SQLite baselines and history (regression gating + score trend) + `ImageDesignSource` (image + annotations + pixel sampling) + `parity snapshot` (freeze the current state as the baseline)
- [ ] **M6** (optional) Cloud shell: public-URL scanning + SSRF protection

> Open work, known blind spots and the next priorities are in [ROADMAP.md](ROADMAP.md);
> version history is in [CHANGELOG.md](CHANGELOG.md).

## Security

- The Figma token comes from an environment variable (`env:FIGMA_TOKEN`); it never reaches a log
  and never goes in a URL (it is sent as the `X-Figma-Token` header).
- Fetched frames are cached in `.parity/cache` (gitignored), so re-runs do not hit Figma and can
  compare offline.

## See also

[**XamlContrast**](https://github.com/HSU-YU-MING/cornhsu-xamlcontrast) — a sibling project by the
same author, built on the same philosophy: numeric checks that gate CI. Parity answers "**does the
implementation match the design?**" (fidelity); XamlContrast answers "**can anyone actually see
what we built?**" (WCAG contrast, scanned statically from XAML source). One guards fidelity, the
other readability — and its static color resolution is also the groundwork for the expected values
of this project's future `WpfImplementationSource` desktop adapter.

## License

MIT
