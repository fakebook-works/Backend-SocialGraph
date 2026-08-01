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
          profileFriends, profileContact, profileAvatarSource,
          group, groups, groupSuggestions, groupFriendMembers, groupViewerState, memberGroups, adminGroups,
          pendingGroupJoins, groupMembers, groupAdmins, groupPosts, groupUserPosts,
          visitedGroups, userPhotos, groupPhotos, groupMedia, groupUserPhotos,
          myFeedPhotoCandidates, groupPhotoCandidates, likedReels, sharedReels, watchedReels,
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

`groupSuggestions(limit)` is a separate, bounded metadata-only discovery path. It derives the
viewer from trusted Gateway context, ranks groups by the number of the viewer's current friends
who participate, and includes both public and private groups. It excludes groups the viewer
already joins/administers or has requested to join and filters blocked friend sources through
`BlockVisibilityService`. Each result contains the group projection, the distinct friend-member
count, at most three minimal friend previews (`id`, `name`, `avatar`), and the number of existing
group posts published during the previous UTC calendar day. It never hydrates private group post
content or exposes the remaining member roster.

`groupFriendMembers(groupId, limit)` is the exact group-profile companion projection. It
derives the viewer from the trusted Gateway context, clamps the result to 12 and returns
only that viewer's current, unblocked friends who are current Member/Admin participants of
the named group. It remains available for a discoverable private group and while the viewer
has a pending join request, but never exposes strangers or the rest of the private roster.

Viewer-specific feed, shortcut, post, and Story operations require trusted Gateway headers:

```http
X-Internal-SocialGraphService-Secret: <dedicated SocialGraph secret>
X-User-Id: <authenticated user id>
```

Viewer-specific IDs are derived from `X-User-Id`; legacy `authorId` values in create inputs are overwritten by the trusted actor. Gateway must remove client-supplied trusted headers and generate them from the validated session. Calls with a missing/invalid secret or missing user identity fail before business logic runs.

Gateway strips client-supplied trusted headers, validates the session, then creates these headers itself. `X-Gateway-Secret` remains accepted as a compatibility alias. `postDetails` preserves ranked input order, removes duplicate IDs, enforces a 100-ID maximum, batches graph reads, and omits deleted, blocked, malformed, or unauthorized posts. `visitedGroups` uses an opaque keyset cursor over `Visited(29)`, returns each edge's `visitedAt` timestamp for relative-time shortcuts, and hides inaccessible private groups. `leaveGroup` derives its actor from the trusted Gateway caller and does not accept a successor ID. A successful leave removes the actor's Member, optional Admin, inverse edges and Visited in one transaction. When the actor is the sole administrator, the service selects only from current `HaveMember` edges (never pending requests), ordered by membership time ascending and then user ID ascending, promotes that member first, and removes the actor in the same transaction. Concurrent leave operations for one group are serialized by a PostgreSQL transaction-scoped advisory lock. If no current member can succeed the sole administrator, the operation fails closed and preserves all associations. Administrative member removal uses the same serialized group boundary: the trusted administrator is re-authorized from both canonical Admin edges after the lock is held, then the non-admin target's Member/HaveMember pair and only that group's one-way Visited edge are removed in the same transaction. Visits to other groups are preserved, and administrator targets must use the dedicated demotion/leave flows.

`profilePosts` is the chronological authored stream used by the profile's All tab and
returns only visible `FeedPostDetail` and `ReelDetail` items. `GroupPostDetail` is explicitly
excluded even when the target authored it; group-authored content remains available through
`groupPosts`/`groupUserPosts` under group privacy and membership checks. It derives the viewer from
the trusted Gateway context, applies the existing two-way block check, and hydrates the
page through `ContentGraphService`, so all four feed/Reel privacy values are evaluated
from current state. `profileReels` remains the Reel-only collection for the dedicated tab.

Post sharing is viewer-aware and supports four canonical source types: Group, FeedPost,
GroupPost and Reel. The resolver derives the actor from trusted Gateway context, unwraps an
existing FeedPost/GroupPost wrapper to its final source, checks current source visibility, and
optionally accepts `destinationGroupId`. A destination group requires current Member/Admin
participation and creates a GroupPost wrapper; without it, the wrapper is a FeedPost with privacy
0/1/2/3. Both normal GroupPost creation and destination-group sharing recheck participation inside
the content transaction and hold a PostgreSQL row lock on that membership until commit, closing the
leave/remove race between authorization and the `Published` edge. Story sharing deliberately remains restricted to FeedPost/Reel through the separate
`CanShareStoryTargetAsync` policy.

Every wrapper read re-evaluates the original source for that viewer. A private GroupPost returns
full author/content/media/mentions only to a current member or administrator. Other viewers receive
only safe group-card metadata plus `requiresGroupMembership`; a deleted source or a two-way block
returns the generic unavailable projection. Group metadata itself is discoverable to authenticated
users so a private group can be found and joined. Wrapper privacy never widens original-source
access, and browser traffic still reaches these operations only through Gateway GraphQL.

Raw object/association CRUD is not part of the public schema. Search hydration is provided through five internal Fusion lookups (`userSearchResult`, `groupSearchResult`, `feedPostSearchResult`, `groupPostSearchResult`, and `reelSearchResult`). Messenger hydrates participants through the federated `User @key(id)` entity. All hydration applies block and content/group privacy rules.

Fast-search hydration also returns viewer-relative metadata without accepting a viewer ID
from GraphQL input: `UserSearchResult.viewerIsSelf/viewerIsFriend/viewerIsFollowing` and
`GroupSearchResult.viewerIsMember` are derived from the trusted Gateway caller and current
friend/member/admin associations. Group administrators count as members for this projection.

`profileFriends(targetUserId, limit)` and `profileContact(userId)` are authenticated,
target-scoped profile reads. The viewer always comes from the trusted Gateway context;
the supplied ID is only the resource being viewed. Both operations stop on a block in
either direction. Friend hydration additionally removes every friend who has a block
relationship with the viewer and caps the public result at 200. Contact email remains
owned by Authentication: SocialGraph reads only `{ userId, email }` for an active account
through the existing signed internal REST client with timestamp/nonce replay protection.
There is no browser-to-Authentication shortcut and no credential/session field is exposed.

Story reads are side-effect free: expired/invalid stories are filtered, not deleted. Cleanup runs in a hosted background service and can also be triggered through the authenticated `DELETE /internal/stories/expired` endpoint. A feed post or reel may be shared only while the trusted actor can read it. Every Story read independently rechecks the Story viewer against the source's current privacy and two-way block state, so changing privacy or relationships hides the source only from viewers who no longer have access. `createStory` is not part of the schema; use `createNormalStory` or `createShareStory`.

`updatePost` accepts optional `content`, `privacy`, and `media`. Omitted values are preserved; `media: []` detaches every current media item and deletes media whose final `Contained` reference disappears. The mutation remains author-only. There is no independent Owned-media library. `userPhotos`, `groupPhotos`, and `groupUserPhotos` derive galleries from visible posts; the two candidate queries provide authorized avatar/background pickers. Viewer reel collections derive identity only from the trusted `X-User-Id` header.

`groupMedia(groupId, cursor, limit)` is the viewer-aware group profile gallery and returns
only photo/video attachments from currently visible GroupPosts. `groupPhotos` remains the
photo-only compatibility query. Both reuse current group privacy and post visibility checks.
GroupPost creation accepts `taggedUserIds`; tags and mention tokens are accepted only when each
referenced account is both the actor's current friend and a current member/administrator of the
same group, with two-way blocks taking precedence. GroupPost detail returns `taggedUsers`.

`deleteContent` remains author-only for feed posts, Reels, comments and Stories. A GroupPost may
also be deleted by a current administrator of the exact group reached through its `PublishedIn`
edge. Supplying another group ID cannot grant deletion authority because the mutation accepts
only the content ID and derives both caller and owning group from trusted/current state.

`createReel` uses feed privacy `0..3` and persists non-destructive presentation metadata:
`aspectRatio` is constrained to `9/16..16/9`, while `focalPointX` and `focalPointY` are
normalized to `0..1` (center defaults to `0.5/0.5`). `ContentResult` and `ReelDetail`
return these fields so every client can reproduce the creator's crop without rewriting
the uploaded video; older Reels without the fields remain centered and compatible.

User avatar and cover changes store the cropped asset. When the source is a newly uploaded
file, the original asset is attached to a public (`privacy=0`) feed activity: avatar uses
`đã cập nhật ảnh đại diện`, while cover uses `tôi đã cập nhật ảnh bìa của mình`. Selecting
an existing authorized photo does not create another activity post.

Avatar provenance is stored separately in the nullable `User.data.avatarSource` object as
decimal Snowflake strings `{ contentId, mediaId }`; `avatar` remains a clean square-image URL.
The source pair is provenance only, never authorization. SocialGraph validates trusted actor,
upload ownership, source ownership, FeedPost type, Contained membership and Photo type on write.
`profileAvatarSource` reapplies current post privacy, two-way block, deletion and content-media
checks on every read. Missing, legacy or no-longer-visible provenance falls back to standalone
avatar viewing. No database-table migration or browser-to-service shortcut is introduced.

Comments are paged as direct children of either a post/Reel or another comment, so clients can expand reply levels lazily without loading an unbounded tree. `createComment` accepts text, one optional image, or both; non-image comment media is rejected. Comment projections include that image, direct reply count, and batched viewer-relative author follow state. `ContentEngagementResult.commentCount` counts the complete descendant comment tree while each comment's `replyCount` remains direct-only. `ContentEngagementResult.viewCount` reports the number of unique `WatchedBy` users for a Reel.

`requestJoinGroup` always creates a pending request for both public and private groups;
group privacy controls post visibility, not admission. Only a current administrator can
approve that request and create membership. `inviteGroupUser` is available to a current
member/administrator for one of their current friends, remains block-aware, and only queues
the invitation notification. It never silently creates membership; the invited user still
uses the same request/approval flow. Feed/story shares enqueue canonical Share notifications
for the original author and suppress self-notifications.

`groupJoinRequests(groupId, cursor, limit)` is a typed, administrator-only projection over
the group-side inverse edge `HaveGroupJoinRequest(18)`. The trusted caller is checked both at
the resolver and read-model boundary, the page is capped at 50, blocked/deleted profiles are
not hydrated, and the API returns `UserSummaryPageResult` rather than exposing raw association
rows. `pendingGroupJoins` continues to read the caller-side `GroupJoinRequest(17)` edge.

Group role termination is fail-closed. Removing an administrator owns a serializable
transaction and takes the same PostgreSQL advisory group lock as `leaveGroup`; it preserves
membership and refuses to remove the last administrator. `deleteGroup` keeps its public
GraphQL shape but passes the trusted actor into the service and succeeds only when that actor
is both a current administrator and the final current participant. Pending requests do not
count as participants and are removed with the group. The final-participant check and local
group deletion run in a service-owned Serializable transaction under the same PostgreSQL
advisory group lock; external cleanup is dispatched only after that transaction commits.

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
InternalServices__Upload__BaseUrl=http://localhost:4001
InternalServices__Upload__SharedSecret=<upload-target-secret>
InternalServices__TimeoutSeconds=10
IntegrationOutbox__PayloadEncryptionKey=<at-least-32-bytes>
IntegrationOutbox__PollMilliseconds=500
IntegrationOutbox__MaxIdlePollMilliseconds=2000
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
GET /internal/users/{userId}/friend-ids
GET /internal/users/{userId}/profile-connection-ids?associationType=<code>
PUT /internal/users/{userId}/verify
DELETE /internal/stories/expired?limit=100
GET /internal/outbox/dead-letters?limit=50
POST /internal/outbox/{eventId}/retry
```

Operational probes are public to the container/orchestrator: `GET /health/live` always reports process liveness; `GET /health/ready` requires PostgreSQL and reports Redis as either `available` or `postgres-fallback` without failing readiness.

## Run

Prerequisites: .NET SDK 10 and PostgreSQL. SocialGraph's application cache may fall back
to PostgreSQL, but the separate shared security Redis connection is required and
fail-closed when internal signature enforcement is enabled.

```powershell
dotnet restore .\SocialGraphService.sln
dotnet run --project .\SocialGraph.Api\SocialGraph.Api.csproj
```

The default HTTP launch URL is `http://localhost:1002`; GraphQL is at `/graphql`.

## Tests

```powershell
dotnet test .\SocialGraphService.sln
```

The suite verifies the 0..29 association contract and migration mapping, precedence rules, relationship/group flows, exact downstream projection contracts, dedicated internal authentication, trusted viewer enforcement, Redis fallback, candidate/privacy filtering, typed group/comment/engagement read models, Story behavior, Fusion hydration, and GraphQL schema compatibility.

## Durable integration outbox

SocialGraph creates `social_graph.integration_outbox` additively with `CREATE TABLE IF NOT EXISTS`; it does not alter the object or association tables. Workers claim rows with `FOR UPDATE SKIP LOCKED`, recover stale processing locks, send a stable `Idempotency-Key` to each target, and retain completed rows for the configured retention period. Idle polling backs off from `PollMilliseconds` to `MaxIdlePollMilliseconds` and resets immediately after work is found. HTTP 408/425/429/5xx and transport errors retry; invalid payloads/configuration and other 4xx responses dead-letter immediately.

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
