# Nightshift — Brainstorm

## What Is This

An autonomous software development engine. Nightshift pulls tickets off a
centralized kanban board, dispatches them through an SDLC pipeline of AI
agents, and delivers merged PRs. When it hits a decision it can't make, it
stops, logs a blocker, and moves on to the next card. Dan resolves blockers
in the morning over a conversation with BD.

Nightshift is not a copilot. It's a build crew that works the night shift.

## Origin Story

### The LeoBloom Pipeline (Proven Pattern)

Over the course of building LeoBloom (F#/.NET personal accounting system),
we developed and battle-tested an agent pipeline:

```
PO -> Planner -> Gherkin Writer -> Builder -> QE -> Reviewer -> Governor -> PO Signoff -> RTE
```

Each agent is a Claude subagent with:
- A **generic role blueprint** (the subagent_type — "builder", "qe", etc.)
- An optional **per-repo addendum** (DSWF files — domain knowledge, test
  conventions, file paths, project-specific rules)
- A **fresh context** every invocation (no state carried between agents)

BD (the basement dweller Claude instance) orchestrates the pipeline manually
today — spawning agents, reading workflow.md, passing artifacts between
steps. The pipeline is mechanical. BD's orchestration is mechanical. The
judgment calls are rare and well-defined.

This works. We shipped 6 projects through it in 2 days. The bottleneck is
BD's context window and Dan's availability to greenlight the next project.

### OGRE (Proven Engine)

The EtlReverseEngineering project ("Ogre") built a 28-node state machine
engine in Python that reverse-engineers legacy ETL jobs using AI agents.

Key lessons from OGRE:
- **The orchestrator must be dumb.** POC5 used an LLM orchestrator that
  suffered from context rot — it fabricated results as conversations grew.
  The fix: deterministic state machine. All intelligence in the agents.
- **Agents get fresh contexts.** Each node invokes Claude CLI as a
  subprocess. No conversation history. Blueprint + prompt + structured
  JSON response.
- **Postgres task queue with `SELECT ... FOR UPDATE SKIP LOCKED`.** Thread-safe
  work claiming. Concurrent workers. One active task per job.
- **Two-artifact-stream pattern.** Process files (inter-agent JSON) vs
  deliverable artifacts. Process files are ephemeral; artifacts persist.
- **Review loops.** CONDITIONAL outcome → response node → re-review. Bounded
  by max-conditional count to prevent infinite loops.
- **Per-node model selection.** Opus for spec/review/judgment. Sonnet for
  build/test. Haiku for mechanical execution.

### The Kanban Board (Existing Asset)

Dan has an existing C# Blazor kanban board (Jira clone) built against SQL
Server. The UI is expendable but the data model and board mechanics might
have salvageable bones.

## Architecture

### Component Overview

```
+------------------+     +-------------------+     +------------------+
|   Kanban Board   |     |  Workflow Engine   |     |  Agent Runtime   |
|   (Postgres)     |<--->|  (C# Console App) |---->|  (Claude CLI)    |
|                  |     |                   |     |                  |
|  - Cards         |     |  - State machine  |     |  - Fresh context |
|  - Projects      |     |  - Task claiming  |     |  - Blueprint +   |
|  - Blockers      |     |  - Transitions    |     |    addenda       |
|  - Workflow defs |     |  - Foreman calls  |     |  - JSON response |
+------------------+     +-------------------+     +------------------+
```

### The Board (Postgres)

No UI. Straight Postgres. Interaction is via:
- BD in Claude Code conversations ("good morning, whatcha got")
- The workflow engine (claiming cards, updating state, logging blockers)
- Direct SQL for admin/debugging

Each development repo gets a **project identifier** in the board. This tells
the engine what repo a card belongs to, where to find the code, and whether
the project has workflow or blueprint overrides.

A card carries:
- Title and description (the backlog item / user story)
- Project identifier (which repo)
- Current SDLC state (which workflow step it's on)
- Status (queued, in-progress, blocked, complete, failed)
- Blocker log (if blocked, why, and what context the foreman captured)
- Run history (which agents ran, what they returned, timestamps)

### The Engine (C# Console App)

A deterministic processing loop. No LLM in the engine itself.

```
loop:
  1. Poll board for unblocked cards in "queued" or "in-progress" status
  2. Claim the next card (thread-safe via DB locking)
  3. Look up the card's current SDLC state
  4. Load the workflow definition for this card's project
     (project-specific override, or default workflow)
  5. Find the workflow step for the current state
  6. Invoke the agent:
     - Load the step's blueprint
     - Load per-repo addenda if they exist
     - Invoke Claude CLI with blueprint + prompt + context from prior steps
     - Parse the structured JSON response
  7. Read the agent's outcome
  8. If outcome is in the step's normal transition map:
     -> Update card state, save artifacts, enqueue next step
  9. If outcome is JUDGMENT_NEEDED:
     -> Invoke the Foreman agent with context + jurisdiction rules
     -> If Foreman resolves it: apply the Foreman's decision, continue
     -> If Foreman escalates: log blocker on card, mark card blocked,
        move on to next card
  10. If outcome is FAIL:
     -> Retry logic (bounded, per OGRE pattern)
     -> After max retries: log blocker, mark card blocked
```

The engine doesn't make architectural decisions. It follows the transition
table.

### Workflow Definitions (DB-Driven)

A workflow is a list of steps stored in the database. Each step has:

| Field | Description |
|-------|-------------|
| Step name | Unique identifier within the workflow (e.g., "builder") |
| Sequence | Ordering (for display/reasoning, not execution — transitions govern flow) |
| Blueprint name | Which .md blueprint to load |
| Model tier | opus, sonnet, or haiku |
| Allowed outcomes | List of valid return values from the agent |
| Transition map | outcome -> next step name (or COMPLETE, or BLOCKED) |

The **default workflow** is the proven LeoBloom pipeline:

```
po_kickoff -> planner -> gherkin_writer -> builder -> qe -> reviewer ->
governor -> po_signoff -> rte
```

With transitions like:
- `builder.SUCCESS -> qe`
- `reviewer.APPROVED -> governor`
- `reviewer.CONDITIONAL -> builder_response`
- `builder_response.SUCCESS -> reviewer`
- `reviewer.FAIL -> builder` (full rebuild)
- `*.JUDGMENT_NEEDED -> foreman`

A project can **override the entire workflow** by defining its own steps.
Or it can use the default. No inheritance/merge — either you use the
standard workflow or you define your own. Keeps it simple.

Workflow changes only happen while the engine is off. No runtime workflow
modification.

### Agent Invocation

Same pattern as OGRE. Each agent is a fresh Claude CLI subprocess:

```bash
claude -p \
  --append-system-prompt <blueprint_text> \
  --output-format json \
  --model <step's model tier> \
  --dangerously-skip-permissions \
  --no-session-persistence \
  <prompt>
```

The prompt includes:
- The card's title and description
- Artifacts from prior steps (plans, specs, code references, test results)
- The per-repo addendum for this role (if it exists)
- Instructions to return structured JSON with an `outcome` field

Agents return JSON. The engine parses the outcome and follows the
transition map. Everything else in the response is saved as artifacts.

### Blueprint System

Two layers, same as LeoBloom's DSWF pattern:

1. **Standard blueprints** — ship with Nightshift. One per SDLC role.
   These are the generic agent instructions (what a Builder does, what a
   QE does, what a Reviewer checks for). Live in the Nightshift repo.

2. **Per-repo addenda** — live in the target repo (e.g., `DSWF/qe.md` in
   LeoBloom). Injected into the agent's context alongside the standard
   blueprint. This is where repo-specific conventions live (test patterns,
   file paths, domain knowledge, year reservation tables, GAAP rules,
   whatever).

A repo doesn't need addenda. The standard blueprints work alone. Addenda
just make the agents smarter about that specific codebase.

### The Foreman

New role. Not in the current pipeline. This is the agent that replaces
BD as orchestrator for judgment calls.

**When invoked:** Any agent can return `JUDGMENT_NEEDED` as an outcome,
along with a structured description of what decision is needed and what
options it sees.

**What the Foreman does:**
1. Reads the judgment request
2. Reads a **jurisdiction document** that defines what calls it can make
   autonomously (e.g., "informational reviewer findings can be dismissed,"
   "test failures require a rebuild," "scope changes require human input")
3. If the decision is within jurisdiction: makes the call, returns a
   resolution that the engine applies
4. If the decision exceeds jurisdiction: returns ESCALATE with context
   for Dan

**What the Foreman does NOT do:**
- Write code
- Modify artifacts
- Override agent outputs
- Make scope decisions
- Approve anything that affects production data

The Foreman is a traffic cop, not an architect. Its jurisdiction is
deliberately narrow. The default answer is "escalate."

### The Blocker Protocol

When a card gets blocked (Foreman escalation, max retries exhausted,
unrecoverable agent failure):

1. Card status set to BLOCKED
2. Card's SDLC state preserved (so it can resume from where it stopped)
3. Blocker record created with:
   - What step was running
   - What the agent returned
   - What the Foreman's assessment was (if applicable)
   - Full context needed for Dan to make a decision
4. Engine moves on to the next unblocked card

### The Morning Conversation

Not a CLI tool. Not a dashboard. A conversation.

Dan opens Claude Code and says "good morning, what's blocked?" BD reads
the blocker table, walks through each blocked card with full context, and
Dan makes the calls. BD updates the board, unblocks the cards, and
Nightshift picks them up on the next run.

This is the human-in-the-loop. It's deliberate, it's bounded, and it
happens on Dan's schedule.

## Technology Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Language | C# | Dan's preference. Strong typing. Good for the state machine + DB layer. |
| Database | Postgres | Already running in the Docker environment. OGRE and LeoBloom both use it. |
| Agent runtime | Claude CLI (`claude -p`) | Proven in OGRE. Fresh contexts. Structured JSON. Model selection per node. |
| UI | None | BD is the UI. Direct SQL for admin. |
| Hosting | Docker container | Same sandbox environment where BD lives. |
| Workflow storage | DB tables | No code deploy to change a workflow. Insert rows. |
| Blueprint storage | Markdown files in repo | Version controlled. Same pattern as OGRE. |
| Per-repo addenda | Markdown files in target repo | Owned by the repo, not by Nightshift. |

## What We Can Salvage

### From OGRE
- Task queue pattern (Postgres, `SELECT ... FOR UPDATE SKIP LOCKED`)
- Agent invocation pattern (Claude CLI subprocess, structured JSON)
- Retry/bounded-loop logic
- Two-artifact-stream pattern (process files vs deliverables)
- Per-node model mapping
- The core insight: dumb orchestrator, smart agents, fresh contexts

### From the Kanban Board
- TBD — need to evaluate the data model and board mechanics. The C#/Blazor
  code is old and Dan says "much of it sucks." The schema might have good
  bones for card lifecycle management. Worth a look before building from
  scratch.

### From LeoBloom's Pipeline
- The SDLC workflow definition (which roles, what order, what artifacts)
- The DSWF addenda pattern
- The agent spawning protocol (subagent_type + prompt template)
- Battle-tested blueprints for every pipeline role
- The insight that the pipeline is mechanical — BD's orchestration today
  is already just following workflow.md

## Open Questions

1. **Concurrency model.** OGRE runs N concurrent workers across jobs.
   Nightshift could do the same (multiple cards in flight). But do we want
   that from day one, or start single-threaded and add concurrency later?

2. **Artifact storage.** OGRE uses the filesystem (jobs/{job_id}/). Nightshift
   could do the same, or store artifacts in Postgres (bytea/text columns),
   or use the target repo's own file system (branches). The target repo's
   branch is probably the right answer — the Builder is already writing code
   there.

3. **How does the engine interact with git?** The RTE agent today creates
   branches, commits, pushes, creates PRs, merges. Does the engine set up
   the branch before dispatching to the first agent? Or does the PO/Planner
   agent do it?

4. **Kanban board salvage assessment.** Haven't looked at the schema yet.
   Might be worth 30 minutes to evaluate before deciding build-from-scratch
   vs adapt.

5. **Where do standard blueprints come from?** We have battle-tested prompts
   embedded in BD's agent spawning calls today. Those need to be extracted
   into standalone blueprint .md files that work without BD's orchestration
   context.

6. **Testing strategy.** OGRE has a `--stubs` flag for testing the engine
   without invoking real agents. Nightshift needs the same. What does the
   test harness look like?

7. **Engine lifecycle.** Is this a long-running daemon, or a "run once and
   process everything" batch job? OGRE is batch (runs until timeout or all
   jobs complete). Nightshift could be either.

8. **Multiple repos sharing a Postgres instance.** LeoBloom already uses
   `172.18.0.1:5432`. Nightshift's board tables would live in the same
   Postgres instance, different schema. Need to confirm there's no
   collision.

## What This Is NOT

- **Not a CI/CD system.** It doesn't deploy. It develops.
- **Not a copilot.** It doesn't pair with a human. It works alone.
- **Not an AI wrapper.** The engine has zero LLM in it. It's a state
  machine that happens to invoke LLM agents.
- **Not opinionated about the SDLC.** The default workflow is our proven
  pipeline, but a repo can define whatever workflow it wants.
