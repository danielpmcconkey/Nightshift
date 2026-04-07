Feature: Workflow Engine — Step Transitions, Outcomes, and Review Gates
  The Nightshift engine processes work items (cards) through a defined workflow
  pipeline. Each workflow is a sequence of steps — from initial setup through
  building, testing, review, and final merge. The engine enforces strict
  transition rules, quality gates with reviewer/governor loops, and escalation
  caps to prevent infinite rework cycles. When autonomous resolution fails,
  work is blocked for human intervention.

  Background:
    Given a project with the default "bdd_pipeline" workflow
    And the workflow has been validated at engine startup
    And a card exists in "queued" status

  # ──────────────────────────────────────────────────
  # 1. Step sequencing — steps execute in defined order
  # ──────────────────────────────────────────────────

  Scenario: Card progresses through the full pipeline on all successes
    When every step in the pipeline returns a successful outcome
    Then the card should advance through the steps in this order:
      | step             |
      | rte_setup        |
      | po_kickoff       |
      | planner          |
      | gherkin_writer   |
      | builder          |
      | qe               |
      | reviewer         |
      | governor         |
      | po_signoff       |
      | rte_merge        |
    And the card should be marked "complete"

  Scenario: Engine processes consecutive steps in a single pass without re-queuing
    Given the card is at the "builder" step
    When the builder returns SUCCESS
    Then the engine should immediately proceed to "qe" without releasing the card
    And a run history entry should be logged for both "builder" and "qe"

  Scenario: Only one task may be active per card at any time
    Given the card already has a pending task in the queue
    When another task is enqueued for the same card
    Then the system should reject the duplicate task

  # ──────────────────────────────────────────────────
  # 2. Outcome-based transitions
  # ──────────────────────────────────────────────────

  Scenario Outline: Allowed outcomes route to the correct next step
    Given the card is at the "<step>" step
    When the agent returns outcome "<outcome>"
    Then the card should advance to "<next_step>"

    Examples:
      | step           | outcome  | next_step       |
      | rte_setup      | SUCCESS  | po_kickoff      |
      | po_kickoff     | SUCCESS  | planner         |
      | planner        | SUCCESS  | gherkin_writer  |
      | gherkin_writer | SUCCESS  | builder         |
      | builder        | SUCCESS  | qe              |
      | qe             | SUCCESS  | reviewer        |
      | reviewer       | APPROVED | governor        |
      | governor       | APPROVED | po_signoff      |
      | po_signoff     | APPROVED | rte_merge       |
      | rte_merge      | SUCCESS  | COMPLETE        |

  Scenario: Terminal step marks the card complete
    Given the card is at the "rte_merge" step
    When the agent returns outcome "SUCCESS"
    Then the card status should be "complete"
    And the task should be marked "complete"
    And no further tasks should be enqueued

  Scenario: Outcome not in the allowed list triggers foreman escalation
    Given the card is at the "builder" step
    And the allowed outcomes for "builder" are SUCCESS and FAIL
    When the agent returns outcome "APPROVED"
    Then the outcome should be treated as unexpected
    And the foreman should be invoked to assess the situation

  Scenario: No transition found for an outcome blocks the card
    Given the card is at a step with no matching transition for the outcome
    When the engine cannot determine the next step
    Then the card should be marked "blocked"
    And the task should be marked "failed"

  # ──────────────────────────────────────────────────
  # 3. Review gates — reviewer and governor loops
  # ──────────────────────────────────────────────────

  Scenario: Reviewer approves and the card advances to the governor
    Given the card is at the "reviewer" step
    When the reviewer returns "APPROVED"
    Then the card should advance to "governor"
    And the reviewer gate counter should be reset to zero

  Scenario: Governor approves and the card advances to product owner signoff
    Given the card is at the "governor" step
    When the governor returns "APPROVED"
    Then the card should advance to "po_signoff"
    And the governor gate counter should be reset to zero

  Scenario: Reviewer returns CONDITIONAL and foreman authorizes a retry
    Given the card is at the "reviewer" step
    And the reviewer gate counter is below the loop cap
    When the reviewer returns "CONDITIONAL"
    And the foreman decides to retry
    Then the card should be sent to "builder_response" for rework
    And the reviewer gate counter should be incremented
    And the foreman's feedback should be injected as context for the rework step

  Scenario: Governor returns CONDITIONAL and foreman authorizes a retry
    Given the card is at the "governor" step
    And the governor gate counter is below the loop cap
    When the governor returns "CONDITIONAL"
    And the foreman decides to retry
    Then the card should be sent to "builder_response" for rework
    And the governor gate counter should be incremented

  Scenario: Builder response loops back to the originating reviewer
    Given the card was sent to "builder_response" after a reviewer CONDITIONAL
    When the builder response returns "SUCCESS"
    Then the card should return to the "reviewer" step for re-evaluation

  Scenario: Stale downstream artifacts are cleaned on rework
    Given the card is at a review gate
    When the foreman authorizes a retry
    Then all artifacts from steps downstream of the failed step should be removed
    And the rework step should start with a clean slate

  # ──────────────────────────────────────────────────
  # 4. Foreman escalation and the 3-loop cap
  # ──────────────────────────────────────────────────

  Scenario: Foreman escalates and blocks the card
    Given the card is at the "reviewer" step
    When the reviewer returns "CONDITIONAL"
    And the foreman decides the issue requires human judgment
    Then a blocker should be created with the foreman's assessment
    And the card should be marked "blocked"
    And the task should be marked "failed"

  Scenario: Three-loop cap prevents infinite rework cycles
    Given the card is at the "reviewer" step
    And the reviewer gate counter has already reached 3
    When the reviewer returns "CONDITIONAL"
    And the foreman wants to retry
    Then the engine should override the foreman and escalate
    And a blocker should be created citing the 3-loop cap
    And the card should be marked "blocked"

  Scenario: Unparseable foreman response triggers automatic escalation
    Given the card is at any step that has been routed to the foreman
    When the foreman returns a response that cannot be parsed
    Then the engine should treat it as an escalation
    And a blocker should be created noting the unparseable response

  Scenario: FAIL outcome at any step routes through the foreman
    Given the card is at the "<step>" step
    When the agent returns "FAIL"
    Then the foreman should be invoked to decide between retry and escalation

  Scenario: JUDGMENT_NEEDED outcome routes through the foreman
    Given the card is at any step
    When the agent returns "JUDGMENT_NEEDED"
    Then the foreman should be invoked to assess the situation

  Scenario: Non-review-gate step retries itself on foreman retry
    Given the card is at the "builder" step which is not a review gate
    When the agent returns "FAIL"
    And the foreman decides to retry
    Then the card should be sent back to the "builder" step
    And no response step or rewind target should be used

  # ──────────────────────────────────────────────────
  # 5. Step timeout behavior
  # ──────────────────────────────────────────────────

  Scenario: Each step has a configurable timeout
    Given the workflow defines a timeout of 1800 seconds per step
    When a step's agent invocation exceeds its timeout
    Then the step should be terminated
    And the outcome should be treated as a failure

  # ──────────────────────────────────────────────────
  # 6. Workflow validation
  # ──────────────────────────────────────────────────

  Scenario: Valid workflow passes all validation checks
    Given a workflow with all steps properly configured
    And every step has a corresponding blueprint file on disk
    And all transition targets reference existing steps or COMPLETE
    And all steps are reachable from the first step
    And at least one step transitions to COMPLETE
    When the workflow is validated
    Then validation should pass without errors

  Scenario: Workflow with no steps fails validation
    Given a workflow with zero steps defined
    When the workflow is validated
    Then validation should fail with "has no steps"

  Scenario: Missing blueprint file fails validation
    Given a workflow step references blueprint "nonexistent"
    And no file "nonexistent.md" exists in the blueprint directory
    When the workflow is validated
    Then validation should fail citing the missing blueprint

  Scenario: Transition to unknown step fails validation
    Given a workflow step has a transition map targeting step "phantom_step"
    And no step named "phantom_step" exists in the workflow
    When the workflow is validated
    Then validation should fail citing the unknown step "phantom_step"

  Scenario: Unreachable step fails validation
    Given a workflow where step "orphan_step" exists
    But no other step's transitions or review gate links lead to "orphan_step"
    When the workflow is validated
    Then validation should fail citing "orphan_step" as unreachable

  Scenario: Workflow with no terminal step fails validation
    Given a workflow where no step transitions to COMPLETE
    When the workflow is validated
    Then validation should fail with "no terminal step"

  Scenario: Review gate without a response step fails validation
    Given a workflow step marked as a review gate
    But the step has no response_step configured
    When the workflow is validated
    Then validation should fail requiring a response_step for the review gate

  Scenario: Review gate without a rewind target fails validation
    Given a workflow step marked as a review gate
    But the step has no rewind_target configured
    When the workflow is validated
    Then validation should fail requiring a rewind_target for the review gate

  Scenario: Non-review-gate step with a response step fails validation
    Given a workflow step that is not a review gate
    But the step has a response_step configured
    When the workflow is validated
    Then validation should fail stating response_step must be null for non-review-gates

  Scenario: Review gate links to nonexistent steps fail validation
    Given a review gate with response_step "missing_response"
    And no step named "missing_response" exists in the workflow
    When the workflow is validated
    Then validation should fail citing "missing_response" as not found

  # ──────────────────────────────────────────────────
  # 7. Engine kill switch
  # ──────────────────────────────────────────────────

  Scenario: Engine stops processing between steps when disabled
    Given the card is at the "builder" step
    And the engine is disabled mid-pipeline
    When the builder completes successfully
    Then the next step should be enqueued
    But the engine should stop processing and exit the loop

  # ──────────────────────────────────────────────────
  # 8. Project-workflow binding
  # ──────────────────────────────────────────────────

  Scenario: Project with no explicit workflow uses the default
    Given a project with no workflow_id assigned
    When the engine loads the workflow for this project
    Then the default workflow should be used

  Scenario: Project with an explicit workflow uses its own
    Given a project assigned to workflow "custom_pipeline"
    When the engine loads the workflow for this project
    Then the "custom_pipeline" workflow should be used

  Scenario: Missing card halts processing for that task
    Given a task referencing a card that no longer exists
    When the engine attempts to process the task
    Then the task should be marked "failed"
    And no further processing should occur for that task

  Scenario: Missing project halts processing for that task
    Given a task whose card references a project that no longer exists
    When the engine attempts to process the task
    Then the task should be marked "failed"
