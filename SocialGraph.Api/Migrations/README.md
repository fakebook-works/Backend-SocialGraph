# SocialGraph database migrations

Normal startup embeds and applies `schema.sql` followed by versioned `.sql` migrations in
filename/version order. Replicas serialize this work with a PostgreSQL advisory lock;
`social_graph.schema_migrations` stores each version and SHA-256 checksum. Failures abort
startup. `DatabaseMigrations:Enabled=false` is the opt-out for deployments that run the same
files in a separate migration job. `ConnectionStrings:PostgreSQLMigration` may name a
DDL-capable role and falls back to `ConnectionStrings:PostgreSQL` when omitted.

`20260808_add_group_join_requested_at.sql` adds a stored generated timestamp for association
types `GroupJoinRequest(17)` and `HaveGroupJoinRequest(18)`. It derives `requested_at` from the
existing authoritative Unix-millisecond `time`, so old requests retain their original creation
time and new writes cannot drift. Managed deployments must run this migration with the owner
before deploying the timestamp-aware SocialGraph query; the legacy `pendingGroupJoins` query
remains available during the rolling upgrade.

## Association contract data migration

The startup runner deliberately never runs this migration. Legacy and canonical numeric
association codes overlap, and this operation rewrites data, so it requires an operator to
verify and declare the source contract.

Preview against the configured database (transaction is always rolled back):

```powershell
dotnet run --project SocialGraph.Api -- --migrate-association-contract
```

After verifying that the source database uses the legacy v1 association codes, apply explicitly:

```powershell
dotnet run --project SocialGraph.Api -- --migrate-association-contract --source-version=1 --apply
```

Apply mode takes a full table backup named `social_graph.associations_backup_v1_<UTC timestamp>`, rebuilds canonical inverse rows, removes orphan/invalid rows, and normalizes conflicts with `block > friend > follow/request` plus `admin > member > join request`. It writes version `3` and the normalization counts to `social_graph.graph_contract_versions`. The backup is retained for manual rollback. Redis is not flushed: v2 code uses a versioned `socialgraph:v2:association:*` namespace, so legacy cache entries cannot be read accidentally.
