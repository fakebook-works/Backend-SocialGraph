-- Indexes for the two hottest tables in the system, plus removal of one that was
-- redundant from the start.
--
-- WHY
--
-- social_graph.associations carried only its primary key (id1, atype, id2) and
-- idx_associations_inverse (id2, atype, id1). Neither contains the "time" column, yet
-- almost every read orders by it: the Redis-miss fallback in
-- AssociationService.RetrieveAssociationAsync, CandidateService feed and reel candidate
-- selection, GroupGraphService.GetVisitedGroupsAsync (which paginates on (time, id2)),
-- and SocialReadModelService's relationship listings. Without a matching index
-- PostgreSQL reads and sorts an entire bucket to return one page — fetching twenty rows
-- for an account with a hundred thousand followers reads all hundred thousand.
--
-- idx_associations is defined on exactly the primary key columns, in the same order.
-- Verified against the live database, where the two definitions are identical:
--   associations_pkey  UNIQUE btree (id1, atype, id2)
--   idx_associations          btree (id1, atype, id2)
-- It can never be preferred over the primary key, so on the largest table in the system
-- it is a second B-tree maintained on every write for no benefit.
--
-- social_graph.objects had no index on otype at all, while several hot paths filter by
-- it: reel candidate selection on every feed request, and the story cleanup pass that
-- runs every fifteen minutes scanning WHERE otype = Story ORDER BY id. Because
-- Snowflake ids are time-ordered and stories are always the newest rows, that scan
-- walked almost the whole table from the oldest id each time.
--
-- HOW TO APPLY
--
--   .\scripts\stop-local.ps1
--   .\scripts\apply-migrations.ps1 -WritersStopped -File `
--       .\SocialGraphService\SocialGraph.Api\migrations\20260727_add_hot_path_indexes.sql
--
-- psql is not installed on every workstation, and when it is missing the command fails in
-- a way that looks like it applied. The script above goes through Fakebook.Maintenance,
-- which already carries the database client this repository depends on.
--
-- The startup runner wraps this script and its history insert in one transaction. Every DDL
-- statement is idempotent, so manual recovery/re-running is safe.
-- CREATE INDEX takes a lock that blocks writes to the table for the duration of the
-- build; on the current data this is milliseconds. If this is ever applied to a database
-- large enough for that to matter, run the same statements with CONCURRENTLY instead and
-- without the transaction — CONCURRENTLY cannot run inside one — then check for indexes
-- left invalid by an interrupted build:
--
--   SELECT i.indexrelid::regclass FROM pg_index i WHERE NOT i.indisvalid;

-- Paged reads of an association bucket, newest first. id2 completes the key so the
-- (time, id2) keyset cursor is satisfied by the index alone.
CREATE INDEX IF NOT EXISTS idx_associations_time
    ON social_graph.associations (id1, atype, "time" DESC, id2 DESC);

-- Filtering objects by type: reel candidates, and the story cleanup sweep.
CREATE INDEX IF NOT EXISTS idx_objects_type_id
    ON social_graph.objects (otype, id);

-- Orphan-media cleanup asks whether another media object points at the same uploaded file
-- before deleting it from disk. otype 7 is GraphObjectType.Media; the literal is required
-- because a partial index predicate cannot reference application constants.
CREATE INDEX IF NOT EXISTS idx_objects_media_url
    ON social_graph.objects ((lower(data ->> 'url')))
    WHERE otype = 7;

-- Identical to the primary key. Dropped after the replacements above exist, so the table
-- is never left without an index the read paths were relying on.
DROP INDEX IF EXISTS social_graph.idx_associations;
