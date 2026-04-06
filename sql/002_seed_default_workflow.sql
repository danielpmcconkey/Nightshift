-- Default BDD workflow seed
-- Run after 001_create_schema.sql

INSERT INTO nightshift.workflow (name, is_default) VALUES ('bdd_pipeline', true);

DO $$
DECLARE
    wf_id integer;
BEGIN
    SELECT id INTO wf_id FROM nightshift.workflow WHERE name = 'bdd_pipeline';

    INSERT INTO nightshift.workflow_step (workflow_id, step_name, sequence, blueprint_name, model_tier, allowed_outcomes, transition_map, is_review_gate, response_step, rewind_target) VALUES
    (wf_id, 'rte_setup',         1,  'rte',             'sonnet', ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "po_kickoff"}'::jsonb,      false, NULL, NULL),
    (wf_id, 'po_kickoff',        2,  'po',              'opus',   ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "planner"}'::jsonb,         false, NULL, NULL),
    (wf_id, 'planner',           3,  'planner',         'opus',   ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "gherkin_writer"}'::jsonb,  false, NULL, NULL),
    (wf_id, 'gherkin_writer',    4,  'gherkin-writer',  'sonnet', ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "builder"}'::jsonb,         false, NULL, NULL),
    (wf_id, 'builder',           5,  'builder',         'sonnet', ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "qe"}'::jsonb,              false, NULL, NULL),
    (wf_id, 'qe',               6,  'qe',              'sonnet', ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "reviewer"}'::jsonb,        false, NULL, NULL),
    (wf_id, 'reviewer',          7,  'reviewer',        'opus',   ARRAY['APPROVED', 'CONDITIONAL', 'FAIL'], '{"APPROVED": "governor"}'::jsonb,       true,  'builder_response', 'builder'),
    (wf_id, 'builder_response',  8,  'builder',         'sonnet', ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "reviewer"}'::jsonb,        false, NULL, NULL),
    (wf_id, 'governor',          9,  'governor',        'opus',   ARRAY['APPROVED', 'CONDITIONAL', 'FAIL'], '{"APPROVED": "po_signoff"}'::jsonb,     true,  'builder_response', 'builder'),
    (wf_id, 'po_signoff',        10, 'po',              'opus',   ARRAY['APPROVED', 'FAIL'],                '{"APPROVED": "rte_merge"}'::jsonb,      false, NULL, NULL),
    (wf_id, 'rte_merge',         11, 'rte',             'sonnet', ARRAY['SUCCESS', 'FAIL'],                 '{"SUCCESS": "COMPLETE"}'::jsonb,        false, NULL, NULL);
END $$;
