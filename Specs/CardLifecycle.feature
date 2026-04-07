Feature: Card Lifecycle and Dependencies
  The engine processes work items called cards. Each card follows a defined
  workflow from creation to completion. Cards may declare dependencies on
  other cards, preventing them from starting until prerequisites are met.
  The engine automatically detects when blocked cards become eligible and
  kicks them off, always prioritizing the most important work first.

  Background:
    Given a project with a configured workflow
    And the workflow has steps "setup", "build", "review", and "merge" in that order

  # ------------------------------------------------------------------
  # Status transitions
  # ------------------------------------------------------------------

  Scenario: A new card starts in queued status
    When a card is created for the project
    Then the card status is "queued"
    And the card has no current step
    And the card has no completion timestamp

  Scenario: A queued card with no dependencies is activated automatically
    Given a queued card with no dependencies
    When the engine checks for activatable cards
    Then the first workflow step is enqueued for that card

  Scenario: A card moves to in-progress when its first step begins
    Given a queued card whose first step has been claimed by the engine
    When the engine advances the card to step "setup"
    Then the card status is "in_progress"
    And the card's current step is "setup"

  Scenario: A card is marked complete when its final step succeeds
    Given an in-progress card on its last workflow step "merge"
    When the step finishes with outcome "SUCCESS" and the transition target is "COMPLETE"
    Then the card status is "complete"
    And the card has a completion timestamp

  Scenario: Valid card statuses are restricted
    Then a card may only have one of these statuses:
      | queued      |
      | in_progress |
      | blocked     |
      | complete    |

  # ------------------------------------------------------------------
  # Dependency blocking
  # ------------------------------------------------------------------

  Scenario: A card blocked by an incomplete dependency cannot be picked up
    Given card "Deploy Service" depends on card "Build Library"
    And card "Build Library" is in status "in_progress"
    When the engine looks for the next task to claim
    Then no task for card "Deploy Service" is claimed

  Scenario: A card with multiple dependencies waits for all of them
    Given card "Integration Test" depends on card "Build Service A"
    And card "Integration Test" depends on card "Build Service B"
    And card "Build Service A" is in status "complete"
    And card "Build Service B" is in status "in_progress"
    When the engine looks for the next task to claim
    Then no task for card "Integration Test" is claimed

  Scenario: A card becomes claimable once all dependencies complete
    Given card "Deploy Service" depends on card "Build Library"
    And card "Build Library" has just been marked complete
    When the engine checks for activatable cards
    Then the first workflow step is enqueued for card "Deploy Service"

  Scenario: Completing a dependency unblocks multiple downstream cards
    Given card "Deploy A" depends on card "Shared Lib"
    And card "Deploy B" depends on card "Shared Lib"
    And card "Shared Lib" has just been marked complete
    When the engine checks for activatable cards
    Then the first workflow step is enqueued for card "Deploy A"
    And the first workflow step is enqueued for card "Deploy B"

  # ------------------------------------------------------------------
  # Activation of unblocked cards
  # ------------------------------------------------------------------

  Scenario: Activation enqueues the first step by workflow sequence order
    Given a queued card with no dependencies and no pending tasks
    When the engine activates unblocked cards
    Then a task is created for the card's first workflow step
    And no tasks are created for later workflow steps

  Scenario: A card that already has a pending task is not activated again
    Given a queued card with an existing pending task
    When the engine checks for activatable cards
    Then no additional task is enqueued for that card

  Scenario: A card that already has a claimed task is not activated again
    Given a queued card with an existing claimed task
    When the engine checks for activatable cards
    Then no additional task is enqueued for that card

  Scenario: Only one active task is allowed per card at a time
    Given a card with a pending or claimed task
    When another task is enqueued for the same card
    Then the system rejects the duplicate task

  Scenario: Activation respects priority ordering
    Given queued card "Low Priority Feature" with priority 4
    And queued card "Critical Bugfix" with priority 1
    And neither card has dependencies
    When the engine activates unblocked cards
    Then card "Critical Bugfix" is activated before card "Low Priority Feature"

  # ------------------------------------------------------------------
  # Priority ordering during claim
  # ------------------------------------------------------------------

  Scenario: The engine claims the highest-priority pending task first
    Given a pending task for card "Routine Work" with priority 3
    And a pending task for card "Urgent Fix" with priority 1
    And neither card has incomplete dependencies
    When the engine claims the next task
    Then the claimed task belongs to card "Urgent Fix"

  Scenario: Cards with equal priority are claimed in creation order
    Given a pending task for card "First Created" with priority 2 created earlier
    And a pending task for card "Second Created" with priority 2 created later
    And neither card has incomplete dependencies
    When the engine claims the next task
    Then the claimed task belongs to card "First Created"

  Scenario Outline: Priority values represent urgency levels
    Given a card with priority <priority>
    Then the priority is valid

    Examples:
      | priority |
      | 1        |
      | 2        |
      | 3        |
      | 4        |
      | 5        |

  Scenario: Priority values outside the allowed range are rejected
    When a card is created with a priority less than 1 or greater than 5
    Then the system rejects the card

  # ------------------------------------------------------------------
  # Edge cases: dependency integrity
  # ------------------------------------------------------------------

  Scenario: A card cannot depend on itself
    When card "Feature X" is set to depend on itself
    Then the system rejects the self-dependency

  Scenario: Adding a duplicate dependency is silently ignored
    Given card "B" already depends on card "A"
    When the dependency from card "B" to card "A" is added again
    Then no error occurs
    And only one dependency record exists between them

  Scenario: A dependency can be removed
    Given card "B" depends on card "A"
    When the dependency from card "B" to card "A" is removed
    Then card "B" no longer depends on card "A"

  Scenario: Dependency status check returns current status of each prerequisite
    Given card "C" depends on card "A" with status "complete"
    And card "C" depends on card "B" with status "in_progress"
    When the dependency statuses for card "C" are retrieved
    Then the result includes card "A" as "complete"
    And the result includes card "B" as "in_progress"

  # ------------------------------------------------------------------
  # Task lifecycle edge cases
  # ------------------------------------------------------------------

  Scenario: Claimed tasks are recovered as pending after an engine crash
    Given a task was claimed but the engine crashed before completing it
    When the engine starts up
    Then the orphaned task is reset to pending status
    And the task's claimed timestamp is cleared

  Scenario: A failed task does not block the card indefinitely
    Given a task that failed during processing
    Then the task is marked as failed with a completion timestamp
    And the card remains eligible for further action

  Scenario: Concurrent workers cannot claim the same task
    Given multiple engine workers polling for tasks simultaneously
    When both attempt to claim the next pending task
    Then only one worker successfully claims it
    And the other worker receives no task
