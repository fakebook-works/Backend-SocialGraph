# SocialGraph - Technology And Project Structure

File nay gom thong tin cong nghe dang dung, vai tro cua SocialGraph service, va y nghia cac folder/file chinh trong project.

## Vai Tro Service

SocialGraph la service quan ly graph xa hoi cua he thong Fakebook. Service nay la source of truth cho:

- User profile va verify/tich xanh.
- Group va thanh vien/admin group.
- Feed post, group post, reel, story, comment, media.
- Quan he friend, follow, block, authored, published, member/admin, contained, owned, watched, saved, tagged, mentioned.
- Candidate post/reel dau vao cho Recommendation service.

Frontend khong noi truc tiep vao REST internal cua SocialGraph. Frontend goi Gateway, Gateway compose GraphQL Federation va goi SocialGraph. REST `/internal/*` chi danh cho service-to-service.

## Cong Nghe Su Dung

- .NET 8 / ASP.NET Core: runtime va web framework chinh.
- HotChocolate GraphQL 16.2.2: expose GraphQL subgraph cho Gateway.
- HotChocolate Apollo Federation 16.2.2: service tham gia GraphQL Federation.
- ASP.NET Core Controllers: expose REST internal cho service-to-service.
- Entity Framework Core 8 + Npgsql: truy cap PostgreSQL.
- PostgreSQL: source of truth cho `objects` va `associations`.
- Redis + NRedisStack + StackExchange.Redis: cache object JSON va association sorted set.
- `HttpClientFactory`: goi Auth, Search, Recommendation, Notification.
- Hosted background service: cleanup story het han theo batch.
- Snowflake ID: sinh ID cho user/group/post/story/media/association.

## Boundary Trong He Thong

Gateway/Frontend:

- Public surface chinh la GraphQL.
- Gateway validate session/token voi Auth service.
- Gateway strip trusted headers client tu gui.
- Gateway tu gan `X-Gateway-Secret`, `X-User-Id`, `X-Correlation-ID` khi goi SocialGraph cho cac resolver can trusted viewer.

Service-to-service:

- Auth: SocialGraph goi khi dang ki/xoa user.
- Search: SocialGraph upsert/delete index cho user/group/post.
- Recommendation: SocialGraph upsert/delete embedding cho user/post va cung cap REST candidate post/reel.
- Payment/Billing: goi SocialGraph REST de set/clear user verify.
- Notification: SocialGraph se goi khi co action can tao notification. File huong dan notification cu da duoc xoa; se viet lai sau.

## Cau Truc Folder/File

### Program.cs

Bootstrap ung dung:

- Dang ki controller va GraphQL server.
- Dang ki `HttpClient` cho external service calls.
- Dang ki `IHttpContextAccessor` va `ITrustedCallerAccessor`.
- Cau hinh EF Core voi PostgreSQL.
- Cau hinh Redis connection.
- Dang ki service layer.
- Dang ki GraphQL union/object types va `HomePostByIdDataLoader`.
- Dang ki `StoryCleanupBackgroundService`.
- Map `/graphql` voi request/variable batching toi da 100 entries va map REST controllers.

### Contracts/

Chua DTO/input/output contract dung cho GraphQL va REST.

File chinh:

- `SocialGraphContracts.cs`: input nhu `CreateUserInput`, `CreateFeedPostInput`, `CreateNormalStoryInput`; output nhu `UserProfileResult`, `GroupResult`, `ContentResult`, `HomePost`, `HomeStory`, `VisitedGroupPageResult`, `CandidateItemResult`.

Luu y:

- Home feed post hien dung union `HomePost = FeedPostDetail | GroupPostDetail`.
- Story home dung union `HomeStory = NormalStory | FeedPostShareStory | ReelShareStory`.

### Database/

Mapping EF Core cho database SocialGraph.

- `Objects.cs`: entity `objects(id, otype, data)`.
- `Associations.cs`: entity `associations(id1, atype, id2, time)`.
- `MyDbContext.cs`: cau hinh schema, key, DbSet.

Database schema chi tiet nam o:

- `Docs/databaseSchemaSocialGraph.md`
- `Docs/databaseSchemaNotification.md`

### Infrastructure/

Thanh phan cross-cutting.

- `CorrelationIdMiddleware.cs`: preserve hoac tao `X-Correlation-ID`.
- `InternalApiAuthenticationMiddleware.cs`: bao ve REST `/internal/*` bang shared secret.
- `TrustedCallerAccessor.cs`: validate Gateway secret va doc trusted `X-User-Id` cho GraphQL resolver can viewer identity.

### RestAPI/

REST internal cho service-to-service.

- `RecommendationController.cs`: candidate post/reel cho Recommendation service.
- `PaymentController.cs`: endpoint Payment/Billing cap nhat verify/tich xanh.
- `StoryMaintenanceController.cs`: endpoint operational cleanup story het han.

### Service/

Business logic va data access layer.

- `ObjectService.cs`: CRUD object, cache object vao Redis JSON.
- `AssociationService.cs`: CRUD association, inverse association, cursor pagination bang Redis sorted set.
- `UserGraphService.cs`: user profile, dang ki, avatar/background, verify, friend/follow/block.
- `GroupGraphService.cs`: group, member/admin, visited group shortcut.
- `ContentGraphService.cs`: feed/group post, story, reel, comment, media, post detail resolver data.
- `CandidateService.cs`: lay pool candidate post/reel cho Recommendation.
- `ExternalServiceClient.cs`: goi Auth/Search/Recommendation/Notification.
- `StoryCleanupBackgroundService.cs`: cleanup story het han theo schedule.
- `GraphConstants.cs`: object type va association type.
- `GraphJson.cs`: helper tao/patch/parse JSON data.
- `GraphResults.cs`: result noi bo cho object/association.
- `ObjectTypeRules.cs`: whitelist field duoc patch theo object type.
- Interface files `I*.cs`: contract noi bo giua resolver/controller va service implementation.

### SubGraphQL/

GraphQL resolver layer.

- `Query.cs`: resolver query nhu `profile`, `group`, `homeStories`, `myStories`, `visitedGroups`, `postDetails`, `postDetail` va internal `recommendationItem` lookup.
- `Mutation.cs`: resolver mutation nhu `createUser`, `createFeedPost`, `createNormalStory`, `deleteStory`, `recordGroupVisit`.
- `RecommendationItemResolvers.cs`: them `RecommendationItem.post` cho Fusion entity hydration.
- `HomePostByIdDataLoader.cs`: gom lookup IDs va goi batch post detail voi trusted viewer.

### Utils/

- `IdGeneratorUtil.cs`: sinh Snowflake ID.

### Properties/

- `launchSettings.json`: cau hinh launch local/dev.

### appsettings.json / appsettings.Development.json

Cau hinh:

- PostgreSQL connection.
- Redis connection.
- Internal shared secret.
- External service endpoints.
- Story cleanup schedule.
- Outbound timeout.

### SocialGraphService.http

File request mau de test GraphQL/REST bang HTTP client.

### Docs/

Chua tai lieu project sau khi sap xep lai.

- `coreFunction.md`: file y tuong/core function goc.
- `project-technology-and-structure.md`: file hien tai.
- `api-by-front-feature.md`: API theo nhom chuc nang front.
- `databaseSchemaSocialGraph.md`: schema SocialGraph.
- `databaseSchemaNotification.md`: schema Notification hien co.
- `completedAPI/`: tai lieu cac chuc nang da hoan thien, tach tu file 06 cu.

### bin/ va obj/

Thu muc build output/generated cua .NET. Khong sua tay.

## Model Du Lieu Chinh

Object table:

```text
objects(id, otype, data jsonb)
```

Association table:

```text
associations(id1, atype, id2, time)
```

Object types:

- `0`: user
- `1`: group
- `2`: feed post
- `3`: group post
- `4`: reel
- `5`: story
- `6`: comment
- `7`: media

Association types quan trong:

- `friend(0)`
- `followed(1)` / `followed_by(2)`
- `authored(5)` / `authored_by(6)`
- `published(9)` / `published_in(10)`
- `member(13)` / `have_member(14)`
- `admin(15)` / `have_admin(16)`
- `saved(19)`
- `contained(20)`
- `mentioned(21)`
- `owned(22)`
- `blocked(23)` / `blocked_by(24)`
- `visited(25)`

## Luu Y Tich Hop

- ID do SocialGraph sinh la canonical ID cho user/group/content.
- Upload file khong xu ly trong SocialGraph; SocialGraph nhan URL da upload xong.
- Verify/tich xanh chi duoc set qua REST internal Payment/Billing.
- Boost post tra phi da bo.
- Candidate post chi la pool dau vao; Recommendation service moi la noi rank feed cuoi cung.
- Story read khong xoa du lieu het han; cleanup do background worker hoac REST maintenance phu trach.
