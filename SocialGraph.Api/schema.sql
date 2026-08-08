-- Social graph domain (PostgreSQL) for Fakebook.
--
-- This file is the authoritative baseline definition. The startup migration runner embeds
-- and applies it before the versioned scripts under Migrations; SocialGraphSchema.md remains
-- documentation of the model, not its source of truth.
--
-- Existing deployments already have these tables. Applying this baseline is harmless there
-- because every statement is guarded, after which the runner records its checksum in
-- social_graph.schema_migrations.
--
-- social_graph.integration_outbox is deliberately absent from the baseline. It is owned by
-- the versioned 20260802_create_integration_outbox.sql migration.

CREATE SCHEMA IF NOT EXISTS social_graph;

-- Every node in the graph. otype selects the shape of the jsonb payload; the model keeps
-- ids Snowflake-generated so they sort by creation time.
CREATE TABLE IF NOT EXISTS social_graph.objects (
    id      bigint PRIMARY KEY,
    otype   smallint NOT NULL,
    data    jsonb
);

-- Every edge. time is the association timestamp in Unix milliseconds and is what read
-- paths order by, which is why it appears in idx_associations_time below.
CREATE TABLE IF NOT EXISTS social_graph.associations (
    id1     bigint NOT NULL,
    atype   smallint NOT NULL,
    id2     bigint NOT NULL,
    "time"  bigint NOT NULL,
    PRIMARY KEY (id1, atype, id2)
);

-- Reverse traversal: given a target, find the edges pointing at it.
CREATE INDEX IF NOT EXISTS idx_associations_inverse
    ON social_graph.associations (id2, atype, id1);

-- Paged reads of an association bucket, newest first. See
-- migrations/20260727_add_hot_path_indexes.sql for why this exists and why there is no
-- separate (id1, atype, id2) index — that would duplicate the primary key exactly.
CREATE INDEX IF NOT EXISTS idx_associations_time
    ON social_graph.associations (id1, atype, "time" DESC, id2 DESC);

-- Filtering objects by type: reel candidate selection, and the story cleanup sweep.
CREATE INDEX IF NOT EXISTS idx_objects_type_id
    ON social_graph.objects (otype, id);
