-- Nightshift schema
-- Run as superuser or a role with CREATE SCHEMA privileges

CREATE SCHEMA IF NOT EXISTS nightshift;

-- Projects
CREATE TABLE nightshift.project (
    id              serial          PRIMARY KEY,
    name            text            NOT NULL UNIQUE,
    repo_path       text            NOT NULL UNIQUE,
    workflow_id     integer,        -- nullable FK, null = default workflow
    addenda_subpath text            NOT NULL DEFAULT 'DSWF',
    created_at      timestamptz     NOT NULL DEFAULT now()
);

-- Workflows
CREATE TABLE nightshift.workflow (
    id              serial          PRIMARY KEY,
    name            text            NOT NULL UNIQUE,
    is_default      boolean         NOT NULL DEFAULT false
);

-- Only one default workflow allowed
CREATE UNIQUE INDEX ix_workflow_one_default
    ON nightshift.workflow (is_default)
    WHERE is_default = true;

-- Workflow steps
CREATE TABLE nightshift.workflow_step (
    id              serial          PRIMARY KEY,
    workflow_id     integer         NOT NULL REFERENCES nightshift.workflow(id),
    step_name       text            NOT NULL,
    sequence        integer         NOT NULL CHECK (sequence > 0),
    blueprint_name  text            NOT NULL,
    model_tier      text            NOT NULL DEFAULT 'sonnet'
                    CHECK (model_tier IN ('opus', 'sonnet', 'haiku')),
    timeout_seconds integer         NOT NULL DEFAULT 1800 CHECK (timeout_seconds > 0),
    allowed_outcomes text[]         NOT NULL,
    transition_map  jsonb           NOT NULL,
    is_review_gate  boolean         NOT NULL DEFAULT false,
    response_step   text,
    rewind_target   text,
    UNIQUE (workflow_id, step_name)
);

CREATE INDEX ix_workflow_step_workflow ON nightshift.workflow_step (workflow_id);

-- Cards
CREATE TABLE nightshift.card (
    id              serial          PRIMARY KEY,
    project_id      integer         NOT NULL REFERENCES nightshift.project(id),
    title           text            NOT NULL,
    description     text            NOT NULL,
    priority        integer         NOT NULL DEFAULT 3 CHECK (priority BETWEEN 1 AND 5),
    status          text            NOT NULL DEFAULT 'queued'
                    CHECK (status IN ('queued', 'in_progress', 'blocked', 'complete')),
    current_step    text,
    conditional_counts jsonb        NOT NULL DEFAULT '{}',
    created_at      timestamptz     NOT NULL DEFAULT now(),
    updated_at      timestamptz     NOT NULL DEFAULT now(),
    completed_at    timestamptz
);

CREATE INDEX ix_card_project ON nightshift.card (project_id);
CREATE INDEX ix_card_status ON nightshift.card (status, priority, created_at);
CREATE INDEX ix_card_completed ON nightshift.card (completed_at)
    WHERE completed_at IS NOT NULL;

-- Task queue (OGRE pattern: separate from card lifecycle)
CREATE TABLE nightshift.task_queue (
    id              bigserial       PRIMARY KEY,
    card_id         integer         NOT NULL REFERENCES nightshift.card(id),
    step_name       text            NOT NULL,
    status          text            NOT NULL DEFAULT 'pending'
                    CHECK (status IN ('pending', 'claimed', 'complete', 'failed')),
    created_at      timestamptz     NOT NULL DEFAULT now(),
    claimed_at      timestamptz,
    completed_at    timestamptz
);

-- FIFO index for pending tasks
CREATE INDEX ix_task_queue_fifo
    ON nightshift.task_queue (created_at)
    WHERE status = 'pending';

-- One active task per card
CREATE UNIQUE INDEX ix_task_queue_one_active
    ON nightshift.task_queue (card_id)
    WHERE status IN ('pending', 'claimed');

CREATE INDEX ix_task_queue_card ON nightshift.task_queue (card_id);

-- Tune autovacuum for high-churn queue table
ALTER TABLE nightshift.task_queue SET (
    autovacuum_vacuum_scale_factor = 0.01,
    autovacuum_analyze_scale_factor = 0.005
);

-- Blockers
CREATE TABLE nightshift.blocker (
    id              serial          PRIMARY KEY,
    card_id         integer         NOT NULL REFERENCES nightshift.card(id),
    step_name       text            NOT NULL,
    agent_response  jsonb,
    foreman_assessment jsonb,
    context         text,
    created_at      timestamptz     NOT NULL DEFAULT now(),
    resolved_at     timestamptz,
    resolution      text,
    CHECK ((resolved_at IS NULL) = (resolution IS NULL))
);

CREATE INDEX ix_blocker_card ON nightshift.blocker (card_id);
CREATE INDEX ix_blocker_unresolved
    ON nightshift.blocker (card_id)
    WHERE resolved_at IS NULL;

-- Run history
CREATE TABLE nightshift.run_history (
    id              bigserial       PRIMARY KEY,
    card_id         integer         NOT NULL REFERENCES nightshift.card(id),
    step_name       text            NOT NULL,
    model           text            NOT NULL,
    started_at      timestamptz     NOT NULL DEFAULT now(),
    completed_at    timestamptz,
    outcome         text,
    notes           text
);

CREATE INDEX ix_run_history_card ON nightshift.run_history (card_id);
CREATE INDEX ix_run_history_card_step ON nightshift.run_history (card_id, step_name);

-- Engine config (singleton)
CREATE TABLE nightshift.engine_config (
    id              integer         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    engine_enabled  boolean         NOT NULL DEFAULT true,
    updated_at      timestamptz     NOT NULL DEFAULT now()
);

INSERT INTO nightshift.engine_config (id) VALUES (1) ON CONFLICT DO NOTHING;

-- Foreign key for project -> workflow
ALTER TABLE nightshift.project
    ADD CONSTRAINT fk_project_workflow
    FOREIGN KEY (workflow_id) REFERENCES nightshift.workflow(id);
