# SocialGraph Service Notes

## Public Boundary

- Gateway/frontend calls SocialGraph through GraphQL Federation.
- The current Gateway composition exposes only `createUser`. Story authorization is implemented in this subgraph, but the Gateway schema/archive still needs to be refreshed before Story fields become public.
- REST endpoints under `/internal` are service-to-service only.
- PostgreSQL is the source of truth; Redis is an association cache.

## Core Model

- Object table: `social_graph.objects(id, otype, data jsonb)`.
- Association table: `social_graph.associations(id1, atype, id2, time)`.
- Object types: `0 user`, `1 group`, `2 feed post`, `3 group post`, `4 reel`, `5 story`, `6 comment`, `7 media`.
- Important associations: friend/follow, authored, group published/member/admin, watched/saved, contained/mentioned/owned, and blocked.

## Registration

`createUser` is the canonical public registration mutation.

```text
1. SocialGraph creates the user profile and canonical Snowflake userId.
2. SocialGraph calls Authentication POST /internal/users with that exact ID (required).
3. If Authentication fails, SocialGraph deletes the profile object and returns failure.
4. If Authentication succeeds, SocialGraph concurrently calls:
   PUT /internal/search/indexes/{userId}
   PUT /internal/recommendation/users/{userId}/embedding
5. Search and Recommendation provisioning are idempotent and best-effort.
6. SocialGraph returns the canonical userId.
```

Search and Recommendation are deliberately called only after Auth succeeds, preventing derived records for an identity that Auth rejected.

## Story Boundary

- Subgraph operations: `homeStories`, `myStories`, `createNormalStory`, `createShareStory`, and `deleteStory`.
- Every Story resolver validates `X-Gateway-Secret` and `X-User-Id`; the GraphQL `userId`/`authorId` must equal the trusted user ID.
- Gateway must strip client-provided trusted headers, validate the session, and generate these headers itself.
- Shared feed posts must be public at creation and remain public at read time. A deleted/private source causes the share Story to be omitted.
- `homeStories` batches database reads by selected author page instead of retrieving each Story/source/media individually.
- Story reads only filter expired or malformed entries. They never delete data.
- `StoryCleanupBackgroundService` performs bounded cleanup; `DELETE /internal/stories/expired?limit=` provides an authenticated operational trigger.
- Story deletion and cleanup preserve media owned by a user/group or referenced by other content. Database deletion runs in one relational transaction.

## Internal REST

- `GET /internal/recommendation/post-candidate-ids?userId=&limit=`
- `GET /internal/recommendation/reel-candidates?userId=&limit=`
- `PUT /internal/users/{userId}/verify`
- `DELETE /internal/stories/expired?limit=100`

Every `/internal/*` request requires `X-Gateway-Secret`. The configured secret must be at least 32 bytes. Missing or invalid credentials return `403`; an unconfigured server returns `503`. `X-Correlation-ID` is preserved or generated and returned in the response.

Post candidate responses are `long[]` ids. Reel candidate responses include `id`, `authorId`, `source`, and `createdAt`. Payment/Billing uses the verify endpoint to set or clear `user.data.verify`; GraphQL user updates cannot change this field.

Story cleanup uses `StoryCleanup:IntervalMinutes` (default `15`, clamp `1..1440`) and `StoryCleanup:BatchSize` (default `100`, clamp `1..500`). The internal endpoint also clamps `limit` to `1..500`.

## External Contracts

- Authentication create: configured by `ExternalServices:AuthenticationServiceCreateUser`, `POST`, JSON `{ userId, email, password, displayName, dob }`.
- Search base URL: `InternalServices:Search:BaseUrl`; index upsert/delete uses `/internal/search/indexes/{objectId}`.
- Recommendation base URL: `InternalServices:Recommendation:BaseUrl`; user/post embedding upsert/delete uses `/internal/recommendation/...`.
- Outbound timeout: `InternalServices:TimeoutSeconds`, default 10 and clamped to 1..60 seconds.
- All outbound internal calls carry `X-Gateway-Secret` and the same `X-Correlation-ID` for one registration operation.
- Messenger user create/delete is temporarily disabled.

Most external writes are best-effort. Authentication identity creation during `createUser` is the required exception. Failed derived projections need a future retry/outbox or repair workflow.
