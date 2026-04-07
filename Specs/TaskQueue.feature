Feature: Task Queue Lifecycle
  The task queue is the execution backbone of the Nightshift engine.
  It governs how work items move from waiting to running to done,
  ensures crashed runs don't leave work permanently stuck, and
  prevents two engine instances from stepping on each other.

  A task represents a single workflow step to be performed on a card.
  Tasks flow through a strict lifecycle: pending → claimed → complete or failed.
  Only one active task (pending or claimed) may exist per card at any time.

  Background:
    Given a project "Acme" with a registered workflow
    And the workflow has steps "design", "implement", "review" in sequence

  # ── Claiming ──────────────────────────────────────────────────

  Scenario: Engine claims the highest-priority pending task
    Given a queued card "Feature A" with priority 3 and a pending task for step "design"
    And a queued card "Feature B" with priority 1 and a pending task for step "design"
    When the engine attempts to claim the next task
    Then the task for "Feature B" is claimed
    And no task for "Feature A" is claimed

  Scenario: Among equal-priority cards, the oldest task is claimed first
    Given a queued card "Feature A" with priority 2 and a pending task created at "2026-04-01"
    And a queued card "Feature B" with priority 2 and a pending task created at "2026-04-03"
    When the engine attempts to claim the next task
    Then the task for "Feature A" is claimed

  Scenario: Tasks for cards with incomplete dependencies are not claimable
    Given a queued card "Prerequisite" with status "in_progress"
    And a queued card "Dependent" with priority 1 and a pending task for step "design"
    And "Dependent" depends on "Prerequisite"
    When the engine attempts to claim the next task
    Then no task is claimed

  Scenario: Tasks become claimable once all dependencies complete
    Given a complete card "Prerequisite"
    And a queued card "Dependent" with priority 1 and a pending task for step "design"
    And "Dependent" depends on "Prerequisite"
    When the engine attempts to claim the next task
    Then the task for "Dependent" is claimed

  Scenario: Only cards in queued or in-progress status have their tasks claimed
    Given a blocked card "Stuck Work" with a pending task for step "design"
    When the engine attempts to claim the next task
    Then no task is claimed

  Scenario: Claiming a task records the claim timestamp
    Given a queued card "Feature A" with a pending task for step "design"
    When the engine claims that task
    Then the task status is "claimed"
    And the task has a non-null claimed_at timestamp

  # ── Concurrency Safety ────────────────────────────────────────

  Scenario: Two engine instances cannot claim the same task
    Given a queued card "Feature A" with a single pending task for step "design"
    When two engine instances attempt to claim the next task simultaneously
    Then exactly one instance receives the task
    And the other instance receives no task

  Scenario: Only one active task may exist per card at any time
    Given a queued card "Feature A" with a pending task for step "design"
    When a second pending task is inserted for "Feature A"
    Then the insert is rejected by the one-active-task constraint

  # ── Task Completion ───────────────────────────────────────────

  Scenario: Completing a task advances the card to the next workflow step
    Given an in-progress card "Feature A" at step "design" with a claimed task
    And the agent returns a successful outcome for step "design"
    When the engine processes the task
    Then the task status is "complete"
    And the task has a non-null completed_at timestamp
    And the card's current step is "implement"
    And the card's status is "in_progress"
    And a new pending task exists for step "implement"

  Scenario: Completing the final workflow step marks the card complete
    Given an in-progress card "Feature A" at step "review" with a claimed task
    And the agent returns an approved outcome for step "review"
    And the workflow transitions "review" with outcome "APPROVED" to "COMPLETE"
    When the engine processes the task
    Then the task status is "complete"
    And the card's status is "complete"
    And the card has a non-null completed_at timestamp
    And no new pending task is created

  Scenario: Task completion and card advancement happen atomically
    Given an in-progress card "Feature A" at step "design" with a claimed task
    When the engine completes the task and advances the card
    Then the task completion, card update, and next task enqueue are in the same transaction
    And either all three succeed or none do

  # ── Task Failure ──────────────────────────────────────────────

  Scenario: A task is failed when the card it references no longer exists
    Given a claimed task referencing a non-existent card
    When the engine processes the task
    Then the task status is "failed"

  Scenario: A task is failed when no workflow is found for the project
    Given a claimed task for a card whose project has no workflow
    When the engine processes the task
    Then the task status is "failed"

  Scenario: A task is failed when the step name is not in the workflow
    Given a claimed task for step "nonexistent_step"
    When the engine processes the task
    Then the task status is "failed"

  Scenario: An unhandled exception during processing marks the task failed
    Given an in-progress card "Feature A" at step "design" with a claimed task
    And the agent invocation throws an unhandled exception
    When the engine processes the task
    Then the task status is "failed"
    And the engine continues running

  Scenario: A task routed to the Foreman that escalates blocks the card
    Given an in-progress card "Feature A" at step "design" with a claimed task
    And the agent returns a failure outcome
    And the Foreman decides to escalate
    When the engine processes the task
    Then the task status is "failed"
    And the card's status is "blocked"
    And a blocker record is created for the card at step "design"

  Scenario: A task with no valid transition blocks the card
    Given an in-progress card "Feature A" at step "design" with a claimed task
    And the agent returns an outcome with no matching transition in the workflow
    When the engine processes the task
    Then the task status is "failed"
    And the card's status is "blocked"

  # ── Orphan Recovery ───────────────────────────────────────────

  Scenario: Orphaned claimed tasks are reset to pending on engine startup
    Given a task in "claimed" status from a previous engine run that crashed
    When the engine starts up
    Then the task status is reset to "pending"
    And the task's claimed_at timestamp is cleared

  Scenario: Multiple orphaned tasks are all recovered on startup
    Given 3 tasks in "claimed" status from a crashed engine run
    When the engine starts up
    Then all 3 tasks have status "pending"

  Scenario: Completed and failed tasks are not affected by orphan recovery
    Given a task in "complete" status
    And a task in "failed" status
    When the engine starts up
    Then both tasks retain their original status

  # ── Unblocked Card Activation ─────────────────────────────────

  Scenario: A queued card with all dependencies met and no active tasks gets activated
    Given a queued card "Feature A" with no active tasks
    And all of "Feature A"'s dependencies are complete
    When the engine checks for unblocked cards
    Then a pending task is created for "Feature A"'s first workflow step
    And higher-priority cards are activated before lower-priority ones

  Scenario: A queued card with incomplete dependencies is not activated
    Given a queued card "Feature A" that depends on an in-progress card
    When the engine checks for unblocked cards
    Then no task is created for "Feature A"

  Scenario: A queued card that already has an active task is not re-activated
    Given a queued card "Feature A" with an existing pending task
    When the engine checks for unblocked cards
    Then no additional task is created for "Feature A"

  # ── Task Lifecycle States ─────────────────────────────────────

  Scenario Outline: Tasks may only be in valid lifecycle states
    When a task exists with status "<status>"
    Then the status is accepted by the database

    Examples:
      | status   |
      | pending  |
      | claimed  |
      | complete |
      | failed   |

  Scenario: A task with an invalid status is rejected by the database
    When an attempt is made to set a task's status to "cancelled"
    Then the database rejects the update with a constraint violation

  Scenario: A pending task has no claimed_at or completed_at timestamps
    Given a newly enqueued task
    Then the task status is "pending"
    And claimed_at is null
    And completed_at is null

  Scenario: A completed task has both claimed_at and completed_at timestamps
    Given a task that was claimed and then completed
    Then claimed_at is not null
    And completed_at is not null
