Feature: Engine Lifecycle — Startup, Shutdown, Idle Behavior, and Agent Invocation

  The Nightshift engine is an autonomous SDLC worker that polls for work,
  invokes AI agents to process workflow steps on cards, and manages its own
  lifecycle through a database-driven enable/disable flag. It communicates
  with agents exclusively through artifact files on disk, never through
  stdout parsing.

  # ── Startup ──────────────────────────────────────────────────────────

  Scenario: Engine recovers orphaned tasks on startup
    Given the engine was previously running and crashed
    And tasks exist in "claimed" status from the prior run
    When the engine starts up
    Then it resets all claimed tasks back to "pending" status
    So that no work item is permanently stuck due to an unclean shutdown

  Scenario: Engine cleans up old run history on startup
    Given completed cards have run history entries older than 30 days
    When the engine starts up
    Then it deletes run history entries for cards completed more than 30 days ago
    And it processes deletions in batches to avoid locking the database

  Scenario: Engine validates all workflows on startup
    Given workflows are registered in the database
    When the engine starts up
    Then it validates every workflow before entering the main loop
    And validation confirms each step references an existing blueprint file
    And validation confirms all transition map targets reference existing steps
    And validation confirms review gates have both a response step and a rewind target
    And validation confirms non-review-gate steps do not have response steps or rewind targets
    And validation confirms every step is reachable from the first step
    And validation confirms at least one step transitions to COMPLETE

  Scenario: Engine rejects a workflow with unreachable steps
    Given a workflow exists with a step that no transition map or review gate link reaches
    When the engine starts up
    Then it refuses to start
    And it reports which steps are unreachable

  Scenario: Engine rejects a workflow missing a terminal step
    Given a workflow exists where no step transitions to COMPLETE
    When the engine starts up
    Then it refuses to start
    And it reports the workflow has no terminal step

  Scenario: Engine rejects a workflow referencing a missing blueprint
    Given a workflow step references a blueprint file that does not exist on disk
    When the engine starts up
    Then it refuses to start
    And it reports the missing blueprint path

  Scenario: Engine validates all project repository paths on startup
    Given projects are registered in the database
    When the engine starts up
    Then it validates each project's repository path is absolute
    And it validates each project's repository path is under /workspace/
    And it validates each project's repository path contains no parent-directory traversal

  Scenario: Engine rejects a project with a relative repository path
    Given a project exists with a relative repository path
    When the engine starts up
    Then it refuses to start

  Scenario: Engine rejects a project with a path outside the workspace
    Given a project exists with a repository path outside /workspace/
    When the engine starts up
    Then it refuses to start

  # ── Shutdown ─────────────────────────────────────────────────────────

  Scenario: Engine shuts down when the enabled flag is turned off before polling
    Given the engine is running and idle
    And an operator sets the engine_enabled flag to false
    When the engine checks the flag at the top of its polling loop
    Then it exits gracefully

  Scenario: Engine shuts down when the enabled flag is turned off during idle wait
    Given the engine is running and found no work to do
    And the engine is in its 60-second idle sleep
    And an operator sets the engine_enabled flag to false
    When the engine wakes from the idle sleep and rechecks the flag
    Then it exits gracefully

  Scenario: Engine shuts down when the enabled flag is turned off between steps
    Given the engine is processing a card with multiple workflow steps
    And an operator sets the engine_enabled flag to false
    When the engine finishes the current step and checks the flag
    Then it stops processing further steps for the card
    And it exits gracefully

  Scenario: Engine shuts down on host cancellation
    Given the engine is running
    When the host process sends a cancellation signal
    Then the engine stops within the 60-second shutdown timeout
    And it logs that cancellation was requested

  Scenario: Engine treats a missing config row as disabled
    Given the engine_config table has no row with id 1
    When the engine checks the enabled flag
    Then it treats the engine as disabled
    And it exits as a fail-safe measure

  # ── Idle Polling ─────────────────────────────────────────────────────

  Scenario: Engine sleeps when there is no pending work
    Given the engine is running
    And there are no pending tasks in the queue
    And there are no unblocked cards ready for activation
    When the engine completes a polling cycle
    Then it waits 60 seconds before checking again

  Scenario: Engine activates unblocked cards before sleeping
    Given a card has status "queued"
    And all of the card's dependencies are complete
    And the card has no pending or claimed tasks
    When the engine finds no claimable tasks
    Then it enqueues the first workflow step for the unblocked card
    And it immediately loops back to claim the new task without sleeping

  Scenario: Engine respects card priority when activating unblocked cards
    Given multiple cards are queued with all dependencies satisfied
    When the engine activates unblocked cards
    Then it processes them in ascending priority order
    And within the same priority, it processes them in creation order

  Scenario: Engine skips cards with incomplete dependencies
    Given a card has status "queued"
    And the card depends on another card that is not yet complete
    When the engine checks for unblocked cards
    Then it does not enqueue any task for the blocked card

  # ── Task Claiming ────────────────────────────────────────────────────

  Scenario: Engine claims the highest-priority pending task
    Given multiple pending tasks exist in the queue
    When the engine claims the next task
    Then it selects the task whose card has the lowest priority number
    And within the same priority, it selects the oldest task
    And it atomically updates the task status to "claimed"

  Scenario: Engine skips tasks for cards with unmet dependencies
    Given a pending task exists for a card that depends on an incomplete card
    When the engine attempts to claim the next task
    Then it does not claim that task

  # ── Agent Invocation ─────────────────────────────────────────────────

  Scenario: Engine loads a blueprint and optional project addendum for each step
    Given a card is being processed at a workflow step
    And the step specifies a blueprint name
    When the engine prepares to invoke an agent
    Then it loads the blueprint markdown file from the blueprints directory
    And it checks the project's repository for a matching addendum file
    And if an addendum exists, it appends it to the system prompt

  Scenario: Engine resolves model tier to a specific model identifier
    Given a workflow step specifies a model tier such as "opus", "sonnet", or "haiku"
    When the engine prepares to invoke an agent
    Then it resolves the tier to a concrete model identifier
    And environment variable overrides take precedence over configuration

  Scenario: Engine provides card context and artifact references to the agent
    Given a card has prior step artifacts from earlier workflow steps
    And the card has a scratchpad with notes from prior agents
    When the engine builds the agent prompt
    Then the prompt includes the card title, description, project, and current step
    And the prompt references prior step artifact file paths
    And the prompt includes the scratchpad contents
    And the prompt specifies the exact file path for the agent to write its outcome

  Scenario: Engine spawns an agent as an external CLI process
    Given the engine has prepared the prompt and system prompt
    When it invokes the agent
    Then it launches a "claude" CLI process with the prompt as an argument
    And it sets the working directory to the project's repository path
    And it passes only whitelisted environment variables to the child process
    And it disables interactive permissions and session persistence

  Scenario: Agent communicates its outcome through a file, not stdout
    Given an agent is invoked for a workflow step
    When the agent completes
    Then the engine reads the outcome from the process artifact JSON file
    And agent stdout is saved as a debug log, not parsed for routing decisions

  Scenario: Agent writes a valid outcome artifact
    Given an agent finishes its work
    When it writes its process artifact JSON file with a recognized outcome
    Then the engine reads the outcome, reason, and notes from the file
    And it uses the outcome to determine the next workflow transition

  Scenario: Agent fails to write an outcome artifact
    Given an agent finishes its work
    But it did not write the required process artifact JSON file
    When the engine checks for the artifact
    Then it treats the result as an unknown outcome
    And it routes the card to the Foreman for review

  Scenario: Agent writes malformed JSON as its outcome
    Given an agent writes its process artifact file
    But the file contents are not valid JSON
    When the engine parses the artifact
    Then it treats the result as a failure
    And it routes the card to the Foreman for review

  Scenario: Agent exceeds its step timeout
    Given a workflow step has a configured timeout
    When the agent does not complete within that timeout
    Then the engine cancels the agent process
    And it records the outcome as a failure with a timeout reason

  Scenario: Engine injects Foreman context on retry
    Given the Foreman decided to retry a step and provided additional context
    When the engine re-invokes the agent for that step
    Then the prompt includes the Foreman's injected context
    So that the agent has guidance on what to do differently

  # ── Foreman and Review Loop ──────────────────────────────────────────

  Scenario: Non-standard outcome routes to Foreman
    Given an agent returns an outcome that is CONDITIONAL, FAIL, JUDGMENT_NEEDED,
      unknown, or not in the step's allowed outcomes list
    When the engine evaluates the outcome
    Then it invokes the Foreman agent to decide whether to retry or escalate

  Scenario: Foreman decides to retry
    Given the Foreman evaluates a failed step
    When the Foreman writes an artifact with outcome "RETRY"
    Then the engine routes the card back to the appropriate step
    And it cleans stale downstream artifacts from prior attempts

  Scenario: Foreman decides to escalate
    Given the Foreman evaluates a failed step
    When the Foreman writes an artifact with outcome "ESCALATE"
    Then the engine creates a blocker record for the card
    And it marks the card as blocked
    And it marks the task as failed

  Scenario: Foreman fails to respond
    Given the Foreman is invoked but does not write its artifact file
    When the engine checks for the Foreman's response
    Then it auto-escalates the card
    And it creates a blocker record noting the Foreman was unresponsive

  Scenario: Foreman times out
    Given the Foreman is invoked for a failed step
    When the Foreman does not complete within 10 minutes
    Then the engine auto-escalates the card

  Scenario: Review gate enforces a 3-loop retry cap
    Given a review gate step has already been retried 3 times
    When the Foreman recommends another retry
    Then the engine overrides the Foreman and escalates the card
    And it records that the hard cap was exceeded

  Scenario: Review gate counter resets on approval
    Given a review gate step previously had a non-zero retry count
    When the agent returns an APPROVED outcome at that gate
    Then the engine resets the gate's retry counter to zero

  # ── Step Transitions ─────────────────────────────────────────────────

  Scenario: Successful step advances to the next step
    Given an agent returns an outcome that is in the step's allowed outcomes
    And the transition map specifies a next step for that outcome
    When the engine processes the outcome
    Then it marks the current task as complete
    And it advances the card to the next step
    And it enqueues a new task for the next step
    And it continues processing within the same execution cycle

  Scenario: Final step completes the card
    Given an agent returns an allowed outcome
    And the transition map routes that outcome to COMPLETE
    When the engine processes the outcome
    Then it marks the card as complete
    And it marks the task as complete

  Scenario: Missing transition blocks the card
    Given an agent returns an outcome that has no entry in the transition map
    And the outcome was in the allowed outcomes list
    When the engine looks up the next step
    Then it marks the card as blocked
    And it marks the task as failed
    And it logs an error indicating the workflow configuration gap

  # ── Run History ──────────────────────────────────────────────────────

  Scenario: Engine logs the start of each agent invocation
    Given the engine is about to invoke an agent for a workflow step
    When it begins processing the step
    Then it records a run history entry with the card ID, step name, and model

  Scenario: Engine logs the completion of each agent invocation
    Given an agent invocation has finished
    When the engine records the result
    Then it updates the run history entry with a completion timestamp
    And it records the outcome and any notes from the agent

  Scenario: Run history entries are retained for 30 days after card completion
    Given a card was completed more than 30 days ago
    When the engine runs its startup cleanup
    Then it deletes the run history entries associated with that card

  Scenario: Run history cleanup preserves entries for active cards
    Given a card is still in progress
    And it has run history entries older than 30 days
    When the engine runs its startup cleanup
    Then it retains those run history entries
    Because the card has not yet completed

  # ── Error Handling ───────────────────────────────────────────────────

  Scenario: Unhandled error during task processing fails the task
    Given the engine has claimed a task and is processing it
    When an unexpected error occurs during processing
    Then the engine marks the task as failed
    So that the task does not remain in "claimed" status indefinitely

  Scenario: Failure to mark a task as failed is logged but does not crash the engine
    Given the engine encounters an error processing a task
    And the subsequent attempt to mark the task as failed also errors
    When the engine handles both failures
    Then it logs both errors
    And it continues polling for the next task

  Scenario: Engine logs a fatal error and terminates on unrecoverable crash
    Given the engine encounters an exception outside the main loop's error handling
    When the exception propagates
    Then it logs a fatal error
    And the engine process terminates

  Scenario: Task fails when its card does not exist
    Given a task references a card ID that is not in the database
    When the engine claims and begins processing the task
    Then it marks the task as failed
    And it logs an error about the missing card
    And it continues polling for the next task

  Scenario: Task fails when its project does not exist
    Given a task references a card whose project is not in the database
    When the engine claims and begins processing the task
    Then it marks the task as failed
    And it logs an error about the missing project
    And it continues polling for the next task

  Scenario: Task fails when its workflow step does not exist
    Given a task references a step name that is not in the card's workflow
    When the engine claims and begins processing the task
    Then it marks the task as failed
    And it logs an error about the unknown step

  # ── Security and Environment ─────────────────────────────────────────

  Scenario: Engine filters environment variables for child agent processes
    Given the engine is about to spawn an agent process
    When it builds the environment for the child process
    Then it passes only PATH, HOME, GIT_SSH_COMMAND, and ANTHROPIC_API_KEY
    And it does not leak other environment variables from the engine process

  Scenario: Blueprint names are validated against path traversal
    Given a workflow step references a blueprint name
    When the engine loads the blueprint
    Then it rejects names containing characters outside alphanumeric, hyphen, and underscore

  Scenario: Addendum paths are validated against directory traversal
    Given a project has an addenda subpath configured
    When the engine resolves the full addendum file path
    Then it rejects any path that resolves outside /workspace/

  Scenario: Passwords are excluded from log output
    Given the engine is logging operational events
    When a log message contains the word "password"
    Then the logging pipeline filters it out before it reaches any sink
