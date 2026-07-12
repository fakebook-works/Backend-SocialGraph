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
2. Authentication POST /internal/users receives that ID (required).
3. Auth failure causes SocialGraph profile rollback and a failed payload.
4. After Auth succeeds, Search and Recommendation provisioning start concurrently:
   PUT /internal/search/indexes/{userId}
   PUT /internal/recommendation/users/{userId}/embedding
5. The derived writes are idempotent and best-effort.
6. SocialGraph returns the canonical userId.
```

## Gateway Feed Contract

The completed SocialGraph fields exposed by the composed Gateway are:

```text
Query:    visitedGroups, postDetail, postDetails, homeStories, myStories
Mutation: createUser, recordGroupVisit, createFeedPost,
          createNormalStory, createShareStory, deleteStory
```

`recommendFeed` is owned by Recommendation. Each returned `RecommendationItem` is hydrated through SocialGraph's internal Fusion lookup, so frontend can request `post` in the same operation. The `post` field is the `HomePost` union: `FeedPostDetail` for user posts or `GroupPostDetail` for group posts. Group posts include `group { id name avatar canJoin }`.

Viewer-specific feed, shortcut, post, and Story operations require trusted Gateway headers:

```http
X-Gateway-Secret: <shared secret>
X-User-Id: <authenticated user id>
```

The `userId` or `authorId` GraphQL argument must match `X-User-Id`. Gateway must remove client-supplied trusted headers and generate them from the validated session. Calls with a missing/invalid secret, missing user identity, or mismatched identity fail before Story business logic runs.

Gateway strips client-supplied trusted headers, validates the session, then creates these headers itself. `postDetails` preserves ranked input order, removes duplicate IDs, enforces a 100-ID maximum, batches graph reads, and omits deleted, blocked, malformed, or unauthorized posts. `visitedGroups` uses an opaque keyset cursor over `Visited(25)` and hides inaccessible private groups.

Story reads are side-effect free: expired/invalid stories are filtered, not deleted. Cleanup runs in a hosted background service and can also be triggered through the authenticated `DELETE /internal/stories/expired` endpoint. Shared feed-post privacy is checked both when a Story is created and each time it is read, so a source made private later is no longer returned. `createStory` is not part of the schema; use `createNormalStory` or `createShareStory`.

## Configuration

Use environment variables for machine-specific values and secrets:

```text
ConnectionStrings__PostgreSQL=<social-graph-postgres-connection>
ConnectionStrings__Redis=localhost:6379
Gateway__InternalSharedSecret=<at-least-32-bytes>
ExternalServices__AuthenticationServiceCreateUser=http://localhost:5001/internal/users
InternalServices__Search__BaseUrl=http://localhost:5191
InternalServices__Recommendation__BaseUrl=http://localhost:8000
InternalServices__TimeoutSeconds=10
StoryCleanup__IntervalMinutes=15
StoryCleanup__BatchSize=100
```

The shared secret must match Authentication, Search, Recommendation, and callers of SocialGraph internal REST. Do not commit real credentials.

## Internal Security

All routes below `/internal` require:

```http
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <optional trace id>
```

Missing/invalid credentials return `403`; invalid server configuration returns `503`. Correlation IDs are preserved or generated and returned in the response.

Current internal endpoints:

```text
GET /internal/recommendation/post-candidate-ids
GET /internal/recommendation/reel-candidates
PUT /internal/users/{userId}/verify
DELETE /internal/stories/expired?limit=100
```

## Run

Prerequisites: .NET SDK 8, PostgreSQL, and Redis.

```powershell
dotnet restore .\SocialGraphService.sln
dotnet run --project .\SocialGraph.Api\SocialGraph.Api.csproj
```

The default HTTP launch URL is `http://localhost:5223`; GraphQL is at `/graphql`.

## Tests

```powershell
dotnet test .\SocialGraphService.sln
```

The suite verifies registration ID propagation and rollback, exact projection contracts, internal authentication, trusted viewer enforcement, candidate privacy/block filtering, keyset shortcut paging, batch post hydration, user/group post discrimination, Story privacy and cleanup, Fusion recommendation hydration, and GraphQL schema compatibility.

## Detailed Documentation

- `SocialGraph.Api/Docs/README.md`
- `SocialGraph.Api/Docs/api-by-front-feature.md`
- `SocialGraph.Api/Docs/completedAPI/home.md`
- `SocialGraph.Api/Docs/completedAPI/register.md`
- `SocialGraph.Api/Docs/completedAPI/story.md`
