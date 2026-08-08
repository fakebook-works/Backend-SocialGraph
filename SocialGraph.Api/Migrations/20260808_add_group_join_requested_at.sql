-- Expose the canonical creation time of group join-request associations as a
-- database timestamp. The original association "time" remains authoritative
-- Unix milliseconds; a generated column prevents dual-write drift and
-- automatically backfills every existing forward/inverse request edge.

ALTER TABLE social_graph.associations
    ADD COLUMN IF NOT EXISTS requested_at timestamptz
    GENERATED ALWAYS AS (
        CASE
            WHEN atype IN (17, 18)
            THEN to_timestamp("time"::double precision / 1000.0)
            ELSE NULL
        END
    ) STORED;

COMMENT ON COLUMN social_graph.associations.requested_at IS
    'Canonical group join-request time for association types 17/18, generated from time Unix milliseconds; NULL for other edge types.';
