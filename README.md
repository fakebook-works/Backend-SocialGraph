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
GET /internal/recommendation/post-candidates
GET /internal/recommendation/reel-candidates
PUT /internal/users/{userId}/verify
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

The suite verifies registration ID propagation, Auth rollback, exact HTTP methods/paths/bodies, shared-secret and correlation headers, concurrent Search/Recommendation provisioning, best-effort failures, canonical post projection contracts, and internal middleware fail-closed behavior.

## Detailed Documentation

- `SocialGraph.Api/Docs/01-current-project-detailed.md`
- `SocialGraph.Api/Docs/04-socialgraph-integration-guide-for-other-agents.md`
- `SocialGraph.Api/Docs/05-api-reference-graphql-rest.md`
- `SocialGraph.Api/billing-service-contract.md`
