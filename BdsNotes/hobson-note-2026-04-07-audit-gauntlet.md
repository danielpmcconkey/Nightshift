# Note from Hobson — 2026-04-07

## The Audit Gauntlet

Dan and I have built a nine-agent audit suite for LeoBloom. The blueprints
live in the ai-dev-playbook:

```
/media/dan/fdrive/codeprojects/ai-dev-playbook/Tooling/DansAgents/GAAP-Assessment/
```

### The agents

| # | Agent | What it reads | What it's looking for |
|---|---|---|---|
| 1 | GAAP Mapper | Accounting specs | GAAP principle coverage (enforced / implied / absent) |
| 2 | Ledger Tracer | Accounting specs | Arithmetic correctness via T-account tracing |
| 3 | Omission Hunter | All specs | Missing scenarios and edge cases |
| 4 | Slop Detector | All specs | AI-generated tests that prove nothing |
| 5 | Domain Invariant Auditor | Non-GAAP specs + backlog cards | Business rule enforcement for non-accounting domains |
| 6 | Tech Debt Auditor | Full codebase | Ownership and operational risk — "what lands me in the boss's office" |
| 7 | Test Harness Auditor | Test infrastructure + test files | Test isolation, shared state, scalability |
| 8 | Consistency Auditor | Full source tree | Pattern drift between authors/sessions |
| 9 | DRY Auditor | Full source tree + tests | Semantic duplication across independently-built features |

Agents 1–4 existed before. 5–9 are new as of today.

Each agent has a reusable blueprint with a calibration anchor, core method,
classification taxonomy, and invocation template. They're domain-agnostic —
designed to work on any project, not just LeoBloom.

We ran the first assessment (agents 1–4) on 2026-04-05 covering specs for
Projects 001–018. The results are in `LeoBloom/HobsonsNotes/GaapAssessment/`.
We're about to run the full nine-agent sweep against the current codebase
(all 43 feature files + full source tree). Results will go in
`LeoBloom/HobsonsNotes/GaapAssessment-2026-04-07/`.

## Dan's idea: Nightshift audit workflows

Dan wants to explore adding an **audit workflow** to Nightshift — distinct
from the current SDLC build workflow. The concept:

- **Periodic execution.** Audit workflows run on a schedule (or on demand),
  not triggered by backlog cards. Think of it as a recurring quality gate
  that runs against the current state of a project.

- **Per-project configuration.** Each project defines its own audit workflow:
  which agents to run, what scope each agent gets, and any project-specific
  overrides to the base blueprints. LeoBloom's audit includes GAAP-specific
  agents that wouldn't apply to, say, COYS or Palimpsest. A game project
  might substitute gameplay-loop or performance auditors.

- **Agent overrides.** The base blueprints in ai-dev-playbook are the
  defaults. A project can override the invocation — narrowing scope,
  adjusting severity thresholds, adding project-specific context to the
  prompt. The blueprint stays generic; the workflow config makes it specific.

- **Synthesis step.** After all agents report, a synthesis agent reads
  the individual reports and produces a unified assessment — cross-referencing
  findings, resolving contradictions, prioritising recommendations. Currently
  Hobson does this manually. Should be automatable.

This is exploratory — Dan hasn't committed to a timeline or design. He wants
you to be aware of the direction so you can factor it into Nightshift's
architecture as it evolves. The audit blueprints are stable and tested; the
workflow orchestration is the open question.

— Hobson
