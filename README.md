# Fakebook Backend SocialGraph

SocialGraph owns canonical user/object Snowflake IDs, social profiles, content graph objects, and relationships. It exposes a HotChocolate Federation subgraph and authenticated internal REST endpoints.

## Registration

The public registration entry point is the Gateway-composed SocialGraph mutation:

```graphql
mutation CreateUser($input: CreateUserInput!) {
  createUser(input: $input) {
    success
    userId
    message
  }
}
```

```json
{
  "input": {
    "name": "Nguyen Van A",
    "gender": true,
    "birthdate": "2000-01-01",
    "location": "Ha Noi",
    "email": "a@example.com",
    "password": "secret"
  }
}
```

Execution order:

```text
1. SocialGraph creates the profile and canonical userId.
2. In the same PostgreSQL transaction, SocialGraph writes four independent outbox events for:
   Authentication, Search, Recommendation, and Messenger.
3. The mutation returns after the profile and all four events commit atomically.
4. A background worker delivers the events independently:
   POST /internal/users (Authentication)
   PUT /internal/search/indexes/{userId}
   PUT /internal/recommendation/users/{userId}/embedding
   POST /internal/users (Messenger)
5. Failed delivery is retried with exponential backoff and deterministic jitter.
6. Permanent failures or exhausted retries move to dead-letter for an authenticated replay.
```

## Gateway Feed Contract

The business-safe SocialGraph fields exposed by the composed Gateway are:

```text
Query:    profile, profiles, relationshipState, friends, incomingFriendRequests,
          outgoingFriendRequests, following, followers, blockedUsers,
          group, groups, groupViewerState, memberGroups, adminGroups,
          pendingGroupJoins, groupMembers, groupAdmins, groupPosts, groupUserPosts,
          visitedGroups, ownedMedia, likedReels, sharedReels, watchedReels,
          postDetail, postDetails, profilePosts, profileReels, comments,
          contentEngagement, savedContent, likedUsers, taggedUsers, mentionedUsers,
          homeStories, myStories, storyViewers
Mutation: createUser, updateUser, change/remove user avatar/background,
          send/cancel/accept/reject friend request, unfriend, follow/unfollow,
          block/unblock, create/update/delete group, change/remove group media,
          join/leave/invite/manage group,
          create/update/delete content, like/unlike, save/unsave, watch,
          tag, mention, create/share/delete Story
```

`recommendFeed` is owned by Recommendation. Each returned `RecommendationItem` is hydrated through SocialGraph's internal Fusion lookup, so frontend can request `post` in the same operation. The `post` field is the `HomePost` union: `FeedPostDetail` for user posts or `GroupPostDetail` for group posts. Group posts include `group { id name avatar canJoin }`.

Viewer-specific feed, shortcut, post, and Story operations require trusted Gateway headers:

```http
X-Internal-SocialGraphService-Secret: <dedicated SocialGraph secret>
X-User-Id: <authenticated user id>
```

The `userId` or `authorId` GraphQL argument must match `X-User-Id`. Gateway must remove client-supplied trusted headers and generate them from the validated session. Calls with a missing/invalid secret, missing user identity, or mismatched identity fail before Story business logic runs.

Gateway strips client-supplied trusted headers, validates the session, then creates these headers itself. `X-Gateway-Secret` remains accepted as a compatibility alias. `postDetails` preserves ranked input order, removes duplicate IDs, enforces a 100-ID maximum, batches graph reads, and omits deleted, blocked, malformed, or unauthorized posts. `visitedGroups` uses an opaque keyset cursor over `Visited(30)` and hides inaccessible private groups.

Raw object/association CRUD is not part of the public schema. Search hydration is provided through five internal Fusion lookups (`userSearchResult`, `groupSearchResult`, `feedPostSearchResult`, `groupPostSearchResult`, and `reelSearchResult`). Messenger hydrates participants through the federated `User @key(id)` entity. All hydration applies block and content/group privacy rules.

Story reads are side-effect free: expired/invalid stories are filtered, not deleted. Cleanup runs in a hosted background service and can also be triggered through the authenticated `DELETE /internal/stories/expired` endpoint. Shared feed-post privacy is checked both when a Story is created and each time it is read, so a source made private later is no longer returned. `createStory` is not part of the schema; use `createNormalStory` or `createShareStory`.

`updatePost` accepts optional `content`, `privacy`, and `media`. Omitted values are preserved; `media: []` detaches every current media item from that post while keeping the media in the owner's library. The mutation remains author-only. `ownedMedia` returns all owned media to its owner/group admin, while other viewers receive only media attached to content they can currently view. Viewer reel collections derive identity only from the trusted `X-User-Id` header.

`inviteGroupUser` is an admin-only notification flow and does not silently add membership; the invited user still uses the normal join/request flow. Feed/story shares enqueue canonical Share notifications for the original author and suppress self-notifications.

## Configuration

Use environment variables for machine-specific values and secrets:

```text
ConnectionStrings__PostgreSQL=<social-graph-postgres-connection>
ConnectionStrings__Redis=localhost:6379
InternalServices__SocialGraph__SharedSecret=<inbound-at-least-32-bytes>
InternalServices__Authentication__BaseUrl=http://localhost:1001
InternalServices__Authentication__SharedSecret=<auth-target-secret>
InternalServices__Search__BaseUrl=http://localhost:1004
InternalServices__Search__SharedSecret=<search-target-secret>
InternalServices__Recommendation__BaseUrl=http://localhost:1003
InternalServices__Recommendation__SharedSecret=<recommendation-target-secret>
InternalServices__Notification__BaseUrl=http://localhost:1005
InternalServices__Notification__SharedSecret=<notification-target-secret>
InternalServices__Messaging__BaseUrl=http://localhost:1006
InternalServices__Messaging__SharedSecret=<messaging-target-secret>
InternalServices__TimeoutSeconds=10
IntegrationOutbox__PayloadEncryptionKey=<at-least-32-bytes>
IntegrationOutbox__MaxAttempts=10
IntegrationOutbox__BaseDelaySeconds=2
IntegrationOutbox__MaxDelayMinutes=15
StoryCleanup__IntervalMinutes=15
StoryCleanup__BatchSize=100
```

Each target service has a separate secret/header. Do not commit real credentials. `appsettings.json` contains localhost placeholders only.

## Internal Security

All routes below `/internal` require:

```http
X-Internal-SocialGraphService-Secret: <dedicated secret>
X-Correlation-ID: <optional trace id>
```

Missing/invalid credentials return `403`; invalid server configuration returns `503`. Correlation IDs are preserved or generated and returned in the response.

Current internal endpoints:

```text
GET /internal/recommendation/post-candidate-ids
GET /internal/recommendation/reel-candidates
POST /internal/messaging/permissions/check
PUT /internal/users/{userId}/verify
DELETE /internal/stories/expired?limit=100
GET /internal/outbox/dead-letters?limit=50
POST /internal/outbox/{eventId}/retry
```

Operational probes are public to the container/orchestrator: `GET /health/live` always reports process liveness; `GET /health/ready` requires PostgreSQL and reports Redis as either `available` or `postgres-fallback` without failing readiness.

## Run

Prerequisites: .NET SDK 8 and PostgreSQL. Redis is optional: startup uses `AbortOnConnectFail=false`, reads fall back to PostgreSQL, and cache writes are best-effort.

```powershell
dotnet restore .\SocialGraphService.sln
dotnet run --project .\SocialGraph.Api\SocialGraph.Api.csproj
```

The default HTTP launch URL is `http://localhost:1002`; GraphQL is at `/graphql`.

## Tests

```powershell
dotnet test .\SocialGraphService.sln
```

The suite verifies the 0..30 association contract and migration mapping, precedence rules, relationship/group flows, exact downstream projection contracts, dedicated internal authentication, trusted viewer enforcement, Redis fallback, candidate/privacy filtering, typed group/comment/engagement read models, Story behavior, Fusion hydration, and GraphQL schema compatibility.

## Durable integration outbox

SocialGraph creates `social_graph.integration_outbox` additively with `CREATE TABLE IF NOT EXISTS`; it does not alter the object or association tables. Workers claim rows with `FOR UPDATE SKIP LOCKED`, recover stale processing locks, send a stable `Idempotency-Key` to each target, and retain completed rows for the configured retention period. HTTP 408/425/429/5xx and transport errors retry; invalid payloads/configuration and other 4xx responses dead-letter immediately.

User create, user update-name projection, and user delete write domain state plus outbox rows in the same PostgreSQL transaction because they share the scoped `MyDbContext`. Other content, group, relationship, and notification flows enqueue immediately after their domain write, but their existing service-level transaction boundaries still leave a small crash window between the domain commit and outbox insert. The downstream endpoints must honor `Idempotency-Key`; replaying a partially delivered operation is otherwise only at-least-once.

User-create credentials are AES-GCM encrypted in the outbox. Keep `IntegrationOutbox__PayloadEncryptionKey` stable until all pending/dead-letter user-create events have completed; rotating it early makes those encrypted rows undecryptable and therefore dead-lettered. The inbound SocialGraph or legacy Gateway secret is accepted as a fallback when the dedicated key is absent, but a dedicated key is recommended.

## Association migration

Normal startup never changes association codes. Preview the legacy v1 to canonical v2 migration with an always-rollback transaction:

```powershell
dotnet run --project .\SocialGraph.Api -- --migrate-association-contract
```

Apply requires an explicit source declaration and flag; it creates a full backup and version marker first:

```powershell
dotnet run --project .\SocialGraph.Api -- --migrate-association-contract --source-version=1 --apply
```

See `SocialGraph.Api/Migrations/README.md` before applying to any shared database.

## Detailed Documentation

- `SocialGraph.Api/SocialGraphSchema.md`
- `SocialGraph.Api/CoreService.md`
- `SocialGraph.Api/Migrations/README.md`
