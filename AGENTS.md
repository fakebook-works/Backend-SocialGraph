# SocialGraph service agent rules

When embedded in the Fakebook workspace, also read the root API security contract.

- Derive the actor from TrustedCallerAccessor; an input userId is only a target identifier.
- Use BlockVisibilityService and the existing read-model/content visibility methods.
- Block is two-way and always wins. Tag/mention never grants access.
- Feed/reel privacy is 0 public, 1 friends/followers, 2 friends, 3 author only.
- Re-check current group membership/admin, ownership and privacy at read/write time.
- Authorize media ownership before storing URLs and preserve final-parent deletion rules.
- Cross-service mutation uses the encrypted integration outbox, bounded retry/dead-letter
  and idempotent clients. Registration keeps Auth-first saga/compensation semantics.
- Internal REST uses signed requests with Redis replay protection; never raw secrets.
- Bound pagination, hydration, deletion batches and cache entries; index new auth paths.
- Runtime DB access uses the social_graph role; schema changes require a migration.

Run dotnet test SocialGraphService.sln. New content APIs require privacy, both block
directions, wrong-owner and untrusted-caller tests.
