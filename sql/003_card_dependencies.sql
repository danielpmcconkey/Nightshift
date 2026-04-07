-- Card dependencies: card cannot start until all its dependencies are complete.
-- This is a many-to-many blocking relationship, not a parent-child hierarchy.

CREATE TABLE nightshift.card_dependency (
    card_id         integer     NOT NULL REFERENCES nightshift.card(id),
    depends_on_id   integer     NOT NULL REFERENCES nightshift.card(id),
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (card_id, depends_on_id),
    CHECK (card_id != depends_on_id)
);

CREATE INDEX ix_card_dependency_depends_on ON nightshift.card_dependency (depends_on_id);
