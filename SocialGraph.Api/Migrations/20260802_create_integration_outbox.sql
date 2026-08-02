-- Move integration-outbox DDL under the same versioned startup migration history as the
-- SocialGraph domain tables. All statements are idempotent so an existing deployment that
-- was initialized by the legacy OutboxSchemaHostedService can be adopted safely.

CREATE TABLE IF NOT EXISTS social_graph.integration_outbox (
    id uuid PRIMARY KEY,
    event_type varchar(100) NOT NULL,
    aggregate_id bigint NULL,
    idempotency_key varchar(200) NOT NULL,
    payload jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    available_at timestamptz NOT NULL,
    attempts integer NOT NULL DEFAULT 0,
    max_attempts integer NOT NULL,
    status smallint NOT NULL DEFAULT 0,
    locked_at timestamptz NULL,
    locked_by varchar(200) NULL,
    last_error varchar(2000) NULL,
    completed_at timestamptz NULL,
    CONSTRAINT ux_integration_outbox_idempotency_key UNIQUE (idempotency_key),
    CONSTRAINT ck_integration_outbox_status CHECK (status BETWEEN 0 AND 3),
    CONSTRAINT ck_integration_outbox_attempts CHECK (attempts >= 0 AND max_attempts > 0)
);

CREATE INDEX IF NOT EXISTS ix_integration_outbox_dispatch
    ON social_graph.integration_outbox (status, available_at, created_at);
