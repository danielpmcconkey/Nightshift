# Nightshift — Brainstorm

**Date:** 2026-04-06
**Status:** Ready for planning
**Source:** Dan's vision doc (`docs/dans_idea.md`) + collaborative Q&A

## What We're Building

An autonomous software development engine. Nightshift pulls cards off a
Postgres kanban board, dispatches them through an SDLC pipeline of AI agents
(Claude CLI subprocesses), and delivers merged PRs. When it can't make a
decision, it blocks the card and moves on. Dan resolves blockers in the
morning over a conversation with BD.

Nightshift is not a copilot. It's a build crew that works the night shift.

## Why This Approach

Three proven systems converge:

- **OGRE** — a 28-node Python state machine that reverse-engineers ETL jobs
  via AI agents. Proved that a dumb orchestrator + smart agents + fresh
  contexts is the right architecture. Task queue, retry logic, transition
  tables, two-artifact-stream pattern, per-node model selection — all
  battle-tested.

- **LeoBloom pipeline** — a manual SDLC agent pipeline (PO -> Planner ->
  Gherkin Writer -> Builder -> QE -> Reviewer -> Governor -> RTE) that
  shipped 6 projects in 2 days. The pipeline is mechanical. BD's
  orchestration is mechanical. The judgment calls are rare and well-defined.
  This is ripe for automation.

- **The insight** — BD is already just following a workflow.md file and
  spawning agents in order. Replace BD's orchestration with a deterministic
  engine, keep the agents exactly as they are.

## Key Decisions

### Architecture

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# / .NET 10 | Dan's preference. Strong typing. Console app. |
| Database | Postgres (new `nightshift` database) | Own DB at `172.18.0.1:5432`. Own role — not the shared `claude` user. |
| Agent runtime | Claude CLI (`claude -p`) | Proven in OGRE. Fresh contexts. Structured JSON. Per-step model selection. |
| UI | None | BD is the UI. Direct SQL for admin. |
| Hosting | Docker container | Same sandbox where BD lives. |
| Secrets | Environment variables | Connection string, API keys, etc. Standard Docker pattern. |
| Logging | Serilog (or similar .NET logging framework) | Structured logging to console + file. |

### Engine Design

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Concurrency | Single-threaded | One card at a time. Add concurrency later when proven. |
| Lifecycle | Batch (CLI) | Manually kicked off. Runs until queue is drained or all cards blocked. |
| Clutch | DB-based stop flag | Single-row config table. Clutch disengaged = finish current card and stop. Same pattern as OGRE. |
| State machine | Deterministic transition table | Engine follows the table. No LLM in the engine. All intelligence in agents. |

### Workflow & Pipeline

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Default workflow | BDD pipeline (includes Gherkin Writer) | Forces BDD adoption across all projects. Non-BDD repos define custom workflows. |
| Workflow storage | DB tables | No code deploy to change a workflow. Insert rows. |
| Workflow override | Per-project, full replacement | No inheritance/merge. Use default or define your own. |
| Workflow changes | Offline only | No runtime workflow modification. Engine must be stopped. |

### Agents & Blueprints

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Blueprint storage | Nightshift repo (`blueprints/`) | Copied from `/workspace/ai-dev-playbook/Tooling/DansAgents/`. Versioned with engine. |
| Per-repo addenda | Target repo (e.g., `DSWF/qe.md`) | Owned by the repo, not Nightshift. Injected alongside standard blueprint. |
| Foreman | Built into engine plumbing | Full invocation on JUDGMENT_NEEDED. Jurisdiction doc loading. Resolve/escalate branching. Blueprint written by Dan separately. |
| Foreman on CONDITIONAL/FAIL | Always invoked | Foreman sees every rejection/failure. Decides retry vs escalate. Engine enforces hard cap of 3 loops regardless of Foreman's opinion. |
| Agent blueprints | Out of scope for this build | Already exist at ai-dev-playbook. Copied in, not authored. |

### Git & PRs

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Branch creation | RTE agent (start of pipeline) | RTE bookends: creates branch at start, merges PR at end. Engine stays git-ignorant. |
| Git auth | Inherited from container | Same setup as LeoBloom. `GIT_SSH_COMMAND` + gh PAT from existing config. |

### Kanban Board

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Old Kanban board | Build from scratch | Old schema is SQL Server, trivially simple (4 tables), no workflow concepts. Not worth porting. |
| Card intake | BD inserts via SQL | Morning conversation is the intake mechanism. |
| Card priority | 5 levels, FIFO within each | Engine always grabs highest-priority oldest card first. |
| Card completion | Status = COMPLETE, stays forever | Cards are historical records. Never deleted. |
| Project registration | Explicit repo_path on project record | No magic path derivation. Set it when you register a project. |

### Artifacts

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Process artifacts | Filesystem, gitignored | `artifacts/{card_id}/` in Nightshift repo. Inter-agent comms, plans, reviewer findings. Inspectable, not version-controlled. |
| Gherkin specs | Target repo | Specs are real deliverables, not process files. Live in the target project's repo. Part of the BDD push. |
| Code | Target repo branch | Builder writes code in the feature branch. RTE merges it. |

### Observability

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Run history | DB table (`run_history`) | Row per agent invocation: card_id, step, outcome, timestamps, notes. |
| Run history cleanup | Auto-delete 30 days after card completion | Table will get big. Prune based on card completion date + 30 days. |
| Engine logging | Serilog or similar | Structured file + console logging. Stack traces, engine state, operational telemetry. |

## Default Workflow (BDD Pipeline)

```
rte_setup -> po_kickoff -> planner -> gherkin_writer -> builder -> qe ->
reviewer -> governor -> po_signoff -> rte_merge
```

Transitions:
- `*.SUCCESS -> next step`
- `*.CONDITIONAL -> foreman` (Foreman decides: retry or escalate)
- `*.FAIL -> foreman` (Foreman decides: rebuild or escalate)
- `foreman.RETRY -> builder_response -> reviewer` (bounded by 3-loop cap)
- `foreman.ESCALATE -> BLOCKED`
- `*.JUDGMENT_NEEDED -> foreman`
- Engine hard cap: 3 review loops per card, then BLOCKED regardless of Foreman

## Components

### 1. Database Schema (Postgres)

**Tables:**
- `project` — id, name, repo_path, workflow_id (nullable FK, null = use default), addenda_subpath (default `DSWF`, convention: `{repo_path}/{addenda_subpath}/{blueprint_name}.md`)
- `workflow` — id, name, is_default
- `workflow_step` — id, workflow_id, step_name, sequence, blueprint_name, model_tier, allowed_outcomes[], transition_map (jsonb)
- `card` — id, project_id, title, description, priority (1-5), status (queued/in_progress/blocked/complete/failed), current_step, created_at, updated_at, completed_at
- `blocker` — id, card_id, step_name, agent_response (text), foreman_assessment (text), context (text), created_at, resolved_at, resolution (text)
- `run_history` — id, card_id, step_name, model, started_at, completed_at, outcome, notes
- `engine_config` — single-row: clutch_engaged (bool)

### 2. Engine (C# Console App)

```
outer loop:
  1. Check clutch — if disengaged, exit gracefully
  2. Poll for next card (highest priority, oldest, unblocked, queued/in_progress)
  3. If no card found, exit (queue drained)
  4. Claim the card (status -> in_progress)
  5. Load workflow for card's project (project override or default)

  inner loop (step-by-step through the pipeline):
    6. Find current step in workflow
    7. Load blueprint (standard + per-repo addendum if exists)
    8. Invoke Claude CLI subprocess with blueprint + prompt + prior artifacts
    9. Parse JSON response, extract outcome
    10. Log to run_history
    11. If outcome in normal transition map -> advance card to next step, continue inner loop
    12. If CONDITIONAL or FAIL -> invoke Foreman
        - Foreman says retry + under 3-loop cap -> route to builder_response, continue inner loop
        - Foreman escalates or at 3-loop cap -> create blocker, mark card blocked, break to outer loop
    13. If JUDGMENT_NEEDED -> invoke Foreman (same resolve/escalate logic)
    14. If final step completes -> mark card COMPLETE, break to outer loop
```

### 3. Agent Runtime

Each agent is a fresh Claude CLI subprocess:
```bash
claude -p \
  --append-system-prompt <blueprint_text> \
  --output-format json \
  --model <model_tier> \
  --dangerously-skip-permissions \
  --no-session-persistence \
  <prompt>
```

Agents return JSON with an `outcome` field. Everything else is saved as artifacts.

### 4. Morning Conversation

Not automated. Dan opens Claude Code, says "good morning, what's blocked?"
BD reads the blocker table, walks through each with full context, Dan makes
calls, BD updates the board, Nightshift picks them up on the next run.

## Prior Art Reference

| Source | What to reuse |
|--------|--------------|
| OGRE (`/workspace/EtlReverseEngineering`) | Transition table pattern, SELECT FOR UPDATE SKIP LOCKED, agent subprocess invocation, retry/escalation logic, clutch mechanism, two-artifact-stream |
| LeoBloom (`/workspace/LeoBloom`) | SDLC pipeline sequence, DSWF addenda pattern, role definitions, artifact flow conventions |
| Agent blueprints (`/workspace/ai-dev-playbook/Tooling/DansAgents/`) | ba.md, builder.md, gherkin-writer.md, governor.md, planner.md, po.md, qe.md, reviewer.md, rte.md — copy into Nightshift repo |

## Open Questions

None. All resolved during brainstorm.
