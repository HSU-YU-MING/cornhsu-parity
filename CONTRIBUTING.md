# Contributing

This project is maintained by one person. The most useful thing you can send is not a pull
request — it is **a report from a design and a page I have never seen**.

Parity's measurement rules were shaped by running it against real sites and real Figma files:
the HUG/FILL axes that can't be compared, padding built out of a child's margin, text boxes that
Figma and browsers will never measure the same way. Each of those started as a gap that was
reported wrongly. Your design system, your components and your CSS are a sample I don't have.

## The most valuable reports

**A misjudgment** — Parity reported a difference that isn't there, graded it too high or too
low, compared the design node with the wrong page element, or said nothing about a real gap.
This is the highest-value report there is: a fidelity tool people stop believing is a tool people
turn off.

**A bug** — a crash, a wrong exit code, a report that doesn't render.

Two shortcuts open the right form with the details already filled in:

- the **report** itself has a link to report a misjudgment on the gap you're looking at;
- **`parity bug-report`** (experimental) prints your version, OS, Node and Chromium details with a
  pre-filled issue link. It connects to nothing itself.

Or start from the [issue forms](https://github.com/HSU-YU-MING/cornhsu-parity/issues/new/choose).
**Write in English or 中文, whichever is easier.**

**One exception: security.** Anything an attacker could use — content from a page or a design
file escaping into a PR comment, a crafted config reaching outside its paths — should be reported
privately. See [SECURITY.md](SECURITY.md).

### Before you open one

Read [What gets compared, and what does not](README.md#what-gets-compared-and-what-does-not).
Some differences are deliberately not compared (text-box size, HUG/FILL axes). If your case is one
of those, a report is still welcome — it tells me the gap hurts in practice — just say so.

### What makes a report reproducible

- **Mask first.** Reports and screenshots carry the page's structure, selectors and text. Replace
  internal URLs, customer data and real copy with placeholders; the property and the two values
  are what matter.
- **The property and both values** as the report shows them — design and implementation.
- **Why you think the judgment is wrong**: devtools shows the computed value matches, a CSS
  transform or zoom is involved, the design node is a variant sheet rather than the screen…
- **Your tolerances and gate settings** if you changed them (only those sections of the config).
- **The version** (`parity version`, or the Action ref you use).

## If you want to send code

Measurement changes need evidence: **a pull request that changes what is compared or how needs
the real design and page that prove it** — and, because Parity reads pixels, a before/after run on
real pages. Every change needs a test; a bug fix gets a test that fails before the change.

```bash
dotnet test Parity.slnx                    # needs a Chromium for the browser tests
pwsh scripts/verify-readme-facts.ps1       # README numbers and Action refs still match
```

Interfaces are frozen within a major version (config schema, CLI and exit codes, Action inputs,
`report.json`, baseline and snapshot formats). A change that breaks one of them waits for the next
major.
