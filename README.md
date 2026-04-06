# Nightshift

Autonomous SDLC engine. Pulls cards off a Postgres kanban board, walks them through
an agent pipeline via Claude CLI subprocesses, delivers merged PRs.

## Prerequisites

- .NET 10
- PostgreSQL (with `nightshift` database and role)
- Claude CLI (`claude` on PATH)
- `ANTHROPIC_API_KEY` environment variable

## Setup

```bash
# Create database (see sql/ for DDL)
psql -U nightshift -d nightshift -f sql/001_create_schema.sql
psql -U nightshift -d nightshift -f sql/002_seed_default_workflow.sql

# Set connection string
export NIGHTSHIFT_CONNECTION_STRING="Host=172.18.0.1;Port=5432;Database=nightshift;Username=nightshift;Password=<password>"
```

## Run

```bash
cd src/Nightshift.Engine
dotnet run
```

The engine:
1. Recovers orphaned tasks from prior crashes
2. Cleans up old run history (>30 days)
3. Validates all workflows and project paths
4. Polls for highest-priority pending card
5. Walks the card through its pipeline (agent by agent)
6. Exits when queue is drained or engine is disabled

## Engine Enable/Disable

```sql
-- Disable (engine finishes current step then exits)
UPDATE nightshift.engine_config SET engine_enabled = false, updated_at = now();

-- Re-enable before next run
UPDATE nightshift.engine_config SET engine_enabled = true, updated_at = now();
```

## Queue a Card

```sql
INSERT INTO nightshift.card (project_id, title, description, priority)
VALUES (1, 'Add user login', 'Implement basic auth with bcrypt...', 2);

INSERT INTO nightshift.task_queue (card_id, step_name)
VALUES (currval('nightshift.card_id_seq'), 'rte_setup');
```

## Security

- All registered projects and addenda files are fully trusted
- Never point Nightshift at untrusted repositories
- DB credentials are NOT passed to agent subprocesses
- Agent CLI arguments use list format (no shell injection)
- repo_path validated: must be absolute, under /workspace/, no traversal
