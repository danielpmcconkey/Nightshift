# Agent Conventions

## Purpose

These agents form a software development pod. They are project-agnostic —
they work on any repo that follows Dan's spec-driven development flow.

## Pod Structure

| Role | Agent | Description |
|---|---|---|
| Product Owner | `po` | Approves artifacts, manages backlog, structured judgment |
| Business Analyst | `ba` | Writes BRDs and BDDs using CE research |
| Project Planner | `planner` | Creates implementation plans from approved specs |
| Builder | `builder` | Implements from plan, spec-faithful, no improvisation |
| Technical Reviewer | `reviewer` | Adversarial code review via CE |
| Project Governor | `governor` | Verifies BDD criteria against delivered work |
| Release Train Engineer | `rte` | Git branch management, commits, PRs, merges |
| Migration Prod Executor | `migration-prod-executor` | Reviews and executes migrations against prod (Hobson's side only) |

## Outcome Enum

All review/approval agents use these outcomes:

| Value | Used By | Meaning |
|---|---|---|
| `SUCCESS` | Work agents (BA, Builder, Planner, RTE) | Did the work, wrote the deliverable |
| `FAIL` | Work agents | Couldn't complete, reason explains why |
| `APPROVED` | Review agents (PO, Reviewer, Governor) | Deliverable passes review |
| `CONDITIONAL` | Review agents | Passes with caveats, conditions list required fixes |
| `REJECTED` | Review agents | Fails review, reason explains why |

## Evidence Requirements

- All claims cite source: requirement IDs, file paths, or artifact references.
- No unsupported assertions.
- Reviewers MUST verify cited evidence exists in actual files.
- "I checked" is not evidence. "File X at line Y says Z" is evidence.

## Numbering Conventions

- BRD requirements: `BRD-NNN` (project-scoped, sequential)
- BDD acceptance criteria: `BDD-NNN` (project-scoped, sequential)
- Feature IDs: `@FT-{category}-{seq}` (global, permanent, tagged on Gherkin scenarios)

BRD and BDD IDs are ephemeral — they live and die with the project.
Feature IDs survive code wipes and project boundaries.

## Traceability

Every deliverable traces forward and backward:

```
Backlog Item → BRD (BRD-NNN) → BDD (BDD-NNN) → Plan → Code → Tests → Test Results
```

- Every BRD requirement traces to a backlog need
- Every BDD criterion traces to a BRD requirement
- Every test traces to a BDD criterion
- Test results map BDD IDs to Feature IDs

## Spec-Driven Development

Specs are the product. Code is disposable. The rebuild manifest is:
BRD + BDD doc + feature files. If you can nuke the code and rebuild
from specs alone, the specs are good enough.

## Compound Engineering (CE)

CE is the toolchain, not a role. Agents use CE skills as tools:

| CE Skill | Used By |
|---|---|
| `/ce:brainstorm` | BA (requirements exploration) |
| `/ce:plan` | Planner (implementation planning) |
| `/compound-engineering:deepen-plan` | Planner (research depth) |
| `/ce:work` | Builder (implementation) |
| `/ce:review` | Reviewer (multi-agent code review) |

## Project File Layout

Agents must respect the project's file organization:

| Directory | Contents |
|---|---|
| `Projects/ProjectNNN-Name/` | BRD, BDD, plan, test-results |
| `Specs/{Category}/` | Feature files (pure Gherkin, nothing else) |
| `Documentation/` | Repo routing table |
| `Src/` | All source code |

## Rejection Handling

When a work agent is re-invoked after CONDITIONAL or REJECTED:
- The rejection reason and conditions are provided in the task prompt
- The agent regenerates the FULL artifact, not a patch
- Only the most recent feedback matters, no accumulated errata
