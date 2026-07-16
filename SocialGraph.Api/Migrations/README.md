# SocialGraph association contract migration

The application never runs this migration during normal startup.

Preview against the configured database (transaction is always rolled back):

```powershell
dotnet run --project SocialGraph.Api -- --migrate-association-contract
```

After verifying that the source database uses the legacy v1 association codes, apply explicitly:

```powershell
dotnet run --project SocialGraph.Api -- --migrate-association-contract --source-version=1 --apply
```

Apply mode takes a full table backup named `social_graph.associations_backup_v1_<UTC timestamp>`, rebuilds canonical inverse rows, removes orphan/invalid rows, and normalizes conflicts with `block > friend > follow/request` plus `admin > member > join request`. It writes version `2` and the normalization counts to `social_graph.graph_contract_versions`. The backup is retained for manual rollback. Redis is not flushed: v2 code uses a versioned `socialgraph:v2:association:*` namespace, so legacy cache entries cannot be read accidentally.
