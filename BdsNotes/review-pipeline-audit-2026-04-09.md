# Review Pipeline Audit — 2026-04-09

Dan asked BD to investigate whether the Nightshift review pipeline is doing
real work or performing theater. Every card (24 total) had shipped without a
single human escalation. This doc captures findings and open design questions.

## Findings

### What's working

The **foreman** and **reviewer** are doing genuine work. Real bugs caught:

- **Card 2**: Reviewer found 3 defects (scope creep, time-bombed tests, missing
  SQL date filter). Builder fixed all 3 plus a 4th the reviewer missed.
- **Card 4**: Foreman kicked back for stale references (false positives, but
  the machinery worked correctly).
- **Card 9**: Builder timed out at 97% (970/973 tests). Foreman gave surgical
  retry instructions for the 3 remaining failures.
- **Card 11**: Reviewer caught a dangerous migration file that would've broken
  30+ tests.
- **Card 16**: Reviewer caught hardcoded dates across 3 rounds of back-and-forth.
- **Card 20**: QE caught a builder regression that broke 10 tests. Foreman gave
  exact fix instructions with all 5 handler names. Best example of the pipeline
  catching a real bug.

The reviewer also makes sharp observations: fragile `.Replace()` alias swaps,
TOCTOU race conditions, dead code, FK cleanup ordering risks.

### What's not working

**Structural approval bias.** 19 of 19 cards that reached review gates ended
APPROVED across all three review agents (reviewer, governor, PO). The rejection
pathway only fires through the foreman retry mechanism when something is
obviously broken.

**Agent-by-agent assessment:**

| Agent | Verdict | Problem |
|-------|---------|---------|
| Reviewer | Real work | Finds issues but categorizes everything as "non-blocking." Never blocks in its own artifact. |
| Governor | Real but formulaic | Does criterion-by-criterion verification. But card 7 it caught QE overstating coverage (~15 of ~40 Gherkin scenarios lacked tests) and still approved. |
| PO | Theater | Weakest link. Most cards get 1-3 sentences parroting the reviewer and governor. Almost zero product-level judgment. |
| Foreman | Actually gatekeeping | Only fires on failures, but precise and actionable when it does. |

### Root cause: the PO got decontextualized

Compared Nightshift's PO output to the P011-012 PO review in LeoBloom
(`Projects/Project011-012-FinancialStatements/Project011-012-po-review.md`).

The P011-012 PO review:
- Built a requirement traceability table mapping every business requirement to
  specific plan elements
- Caught a real SQL bug (HAVING clause that could never filter anything because
  `amount` is always positive in the schema)
- Distinguished blocking conditions from observations
- 95 lines of substantive analysis

Nightshift card 14's PO: "Clean delivery. No issues found. Ready for merge."

**The PO blueprint didn't change between LeoBloom and Nightshift.** The
difference was operator context. When BD ran the PO role manually, it had:
- `BdsNotes/workflow.md` with GAAP compliance section
- Schema knowledge from accumulated session context
- Domain awareness from prior conversations with Dan
- Full tool access (Bash, Agent, etc.)

When Nightshift spawns a PO agent, it gets the blueprint, the card description,
and prior artifact paths. No domain context, no schema awareness, limited tools
(Read/Write/Glob/Grep only — no Bash, no Agent). It can only do string-level
comparison between plan text and card text.

## Design options explored

### Option 1: Enrich the PO

Write a better generic PO blueprint + LeoBloom-specific DSWF/po.md addendum.
The generic blueprint would add:
- Requirement traceability table (mandatory)
- "Play the scenario forward" step
- Explicit mandate to read source code

The DSWF addendum would add:
- GAAP as domain authority
- Schema awareness (amount always positive, entry_type discriminates, etc.)
- "Hobson is entering real data" stakes reminder

**Problem:** PO currently has Read/Write/Glob/Grep only. Can't run code, can't
spawn sub-agents. Would need Bash at minimum to verify plan assumptions against
actual code.

**Cost:** Enhanced PO adds ~$0.40/card (from ~$0.17 to ~$0.56). Total card
cost is $1.70-3.60, so this is 12-16%.

### Option 2: Collapse reviewer + governor + PO into one uber-agent

One Opus pass that does adversarial review, spec verification, plan-to-intent
traceability, and sign-off.

**Savings:** ~$0.75-1.50/card (25-40% of total).

**Problem:** This creates an agent that sits above the deterministic orchestrator.
The engine owns control flow — agents run, write JSON, die. An uber-reviewer
that finds issues, kicks to builder, and re-verifies is doing the engine's job.

If it plays by the rules (run once, verdict, die), it still needs:
1. First review pass
2. Builder response (if issues)
3. Second review pass (cold start)

So the invocation savings are smaller than they look — you drop PO signoff and
avoid governor re-reviewing what the reviewer already approved, but you still
need multiple passes for the retry loop.

### Option 3: Punt and observe (CHOSEN)

Wait to see how Hobson's experience with LeoBloom in production surfaces (or
doesn't surface) quality issues. If Hobson finds problems that the review
pipeline should have caught, revisit Options 1 or 2 with concrete failure data.
If not, the current pipeline may be adequate despite the PO being weak.

## Token economics reference

PO runs on Opus ($15/MTok input, $75/MTok output). Invoked twice per card
(po_kickoff, po_signoff).

| Version | Per-card PO cost |
|---------|-----------------|
| Current (rubber stamp) | ~$0.17 |
| Enhanced (real traceability) | ~$0.56 |
| Removed entirely | $0.00 |

Full happy-path card cost estimate: $1.70-3.60 (11 steps, 4 on Opus, 7 on
Sonnet).

## Open questions for future sessions

1. If we do enrich the PO, can we write a DSWF addendum rich enough that a
   cold-start Claude produces P011-012-quality reviews? Or does that quality
   require accumulated session context that a one-shot agent can't replicate?
2. Should the PO get Bash access? It's a document-review agent by design, but
   the best PO review we have (P011-012) required reading source code.
3. The governor approved card 7 despite finding 15 of 40 Gherkin scenarios
   lacked direct test coverage. Is that the right call? If so, should the
   governor's blueprint acknowledge "coverage gaps in card specs are acceptable
   if load-bearing specs are intact"?
4. The foreman's escalation criteria (rules 2, 6, 7, 9 in the jurisdiction doc)
   have never fired. Is the threshold too high, or have the cards genuinely been
   well-scoped?
5. How does Hobson's production experience change the calculus? If real-world
   usage surfaces no issues, the pipeline is adequate. If it surfaces issues
   the review agents should have caught, we know exactly where to invest.
