# Nightshift Backlog

| # | Item | Priority | Status | Source |
|---|------|----------|--------|--------|
| N001 | Progress-based timeout (kill-on-idle, not kill-on-clock) | High | Not started | wakeup-2026-04-07g |
| N002 | Dogfooding BDD specs — Dan review of 4 feature files | Medium | Blocked (Dan) | wakeup-2026-04-07g |
| N003 | Blueprint sourcing — copy to bin dir at publish time | Low | Not started | wakeup-2026-04-07g |
| N004 | Recurring workflows + multi-project support | High | Not started | 2026-04-08 design conversation |

---

## N001 — Progress-Based Timeout

Static timeouts are always wrong. Too short kills productive work (P073 timed
out twice at 30min), too long babysits dead agents. Current band-aid: 90min
across builder/qe/builder_response steps.

**What it should do:** Monitor agent output activity. If an agent hasn't
produced output or made a tool call in N minutes, it's stuck — kill it. If
it's actively writing files, let it run. This is an engine feature, not a
config knob.

**DB config (current band-aid):**
`nightshift.workflow_step.timeout_seconds` = 5400 for builder, qe,
builder_response. The `DefaultTimeoutSeconds` in appsettings.json is dead
config — not actually read.

## N002 — Dogfooding BDD Specs

4 retroactive feature files committed alongside blueprint edits (commit
`0ac5024`):
- CardLifecycle
- WorkflowEngine
- EngineLifecycle
- TaskQueue

These are descriptive specs of existing behavior, not new requirements.
Waiting on Dan's review before they become authoritative / enforceable.

## N003 — Blueprint Sourcing

Blueprints are read from disk at runtime via `File.ReadAllText` from
`/workspace/Nightshift/blueprints/`. Edits to blueprint files on disk affect
the running engine immediately. Ideally blueprints should be copied to the
bin directory at publish time so a running engine isn't affected by live edits.

Not urgent — just means don't edit blueprints while the engine is processing
a card.

## N004 — Recurring Workflows + Multi-Project Support

Nightshift needs to grow beyond one-shot code cards against a single repo.

**Motivating use case:** Figment (separate Claude Code instance) owns
Palimpsest. His documentation may drift, accumulate errors, or go stale.
Dan wants Nightshift to run a periodic doc review — analyze docs, produce a
report with recommended fixes, surface it for Dan.

**What this requires from the engine:**

1. **Recurring cards.** A card type that re-queues itself on a schedule
   (daily, weekly, etc.) after completion. Not an external cron inserting
   rows — the engine owns the schedule.

2. **Alternative workflows.** The current workflow (rte → build → qe →
   review → govern → merge) assumes code changes + PR. A doc review workflow
   is different: read → analyze → report. No branch, no merge. The engine
   needs to support multiple workflow definitions with different step chains.

3. **Multi-project.** The `project` table already exists with `repo_path`,
   but the engine has only ever run against LeoBloom. Palimpsest (and
   potentially other repos) need to be registered projects with their own
   workflows and blueprints.

4. **Non-merge artifacts.** Work product isn't always a PR. Could be a
   report file, a diff summary, a list of recommended changes. Need a
   standard output mechanism for review workflows.

**Open questions:**
- Does a recurring card track its history (run #1, run #2, ...) or is each
  run a fresh card?
- Should report-type workflows be able to escalate to code-change workflows
  if the findings warrant it? (e.g., doc review finds broken links → spawns
  a fix card)
- Blueprint structure for non-builder agents — what does a "reviewer" agent
  look like when there's no code to review, just docs to audit?
