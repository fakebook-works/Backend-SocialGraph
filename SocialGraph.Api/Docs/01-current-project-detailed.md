# SocialGraph.Api - Tai Lieu Chi Tiet Project Hien Tai

Tai lieu nay mo ta code hien tai cua `SocialGraph.Api`: cau truc file, cong dung, API, service methods, input/output va logic chinh. Muc tieu la de ban hoac agent khac doc vao co the lam viec voi Gateway va cac microservice con lai ma khong phai doc toan bo source truoc.

## 1. Vai tro trong he thong microservice

`SocialGraph.Api` la service nam giu social graph cua ung dung clone Facebook:

- Luu toan bo thuc the xa hoi duoi dang generic object: user, group, post, reel, story, comment, media.
- Luu moi quan he xa hoi duoi dang association: friend, follow, like, authored, group member, comment, save, watched, blocked...
- Expose GraphQL Federation cho Gateway; hien Gateway chi public `createUser`, cac field SocialGraph khac dang `@internal`.
- Expose REST internal cho Recommendation service lay candidate post/reel.
- Goi REST sang service khac:
  - Authentication: create identity trong `createUser` la required; fail thi rollback user object. Cac call Auth khac hien best-effort.
  - Messenger: create/delete user dang tam disable.
  - Search: tao/cap nhat/xoa index theo best-effort.
  - Recommendation: tao/xoa embedding theo best-effort.
  - Notification: tao notification.
  - Payment/Billing: goi REST internal cua SocialGraph de cap nhat han `verify` sau khi thanh toan thanh cong.

Ranh gioi quan trong:

- Frontend/Gateway chi nen goi SocialGraph qua GraphQL Federation.
- REST trong SocialGraph chi danh cho service-to-service.
- PostgreSQL la source of truth.
- Redis chi la cache.
- `verify` cua user nam trong `user.data.verify`; chi REST internal moi duoc sua field nay.

## 2. Cau truc file hien tai

```text
SocialGraph.Api/
  Program.cs
  SocialGraph.Api.csproj
  appsettings.json
  Project.md
  databaseSchemaSocialGraph.md
  databaseSchemaNotification.md
  billing-service-contract.md
  socialgraph-service-notes.md
  Contracts/
    SocialGraphContracts.cs
  Database/
    Objects.cs
    Associations.cs
    MyDbContext.cs
  Infrastructure/
    CorrelationIdMiddleware.cs
    InternalApiAuthenticationMiddleware.cs
  RestAPI/
    RecommendationController.cs
    PaymentController.cs
  Service/
    GraphConstants.cs
    GraphJson.cs
    GraphResults.cs
    ObjectTypeRules.cs
    IObjectService.cs
    ObjectService.cs
    IAssociationService.cs
    AssociationService.cs
    IUserGraphService.cs
    UserGraphService.cs
    IGroupGraphService.cs
    GroupGraphService.cs
    IContentGraphService.cs
    ContentGraphService.cs
    ICandidateService.cs
    CandidateService.cs
    IExternalServiceClient.cs
    ExternalServiceClient.cs
  SubGraphQL/
    Query.cs
    Mutation.cs
  Utils/
    IdGeneratorUtil.cs
tests/
  SocialGraph.Api.Tests/
```

### Program.cs

Dang ky dependency injection va endpoint:

- `AddControllers()`: bat REST controller internal.
- `AddHttpClient("external-services")`: client goi service ngoai.
- `AddHttpContextAccessor()`: forward correlation ID tu request hien tai sang internal services.
- `AddDbContext<MyDbContext>()`: ket noi PostgreSQL bang connection string `PostgreSQL`.
- `IConnectionMultiplexer`: ket noi Redis.
- Dang ky service:
  - `IObjectService -> ObjectService`
  - `IAssociationService -> AssociationService`
  - `IExternalServiceClient -> ExternalServiceClient`
  - `IUserGraphService -> UserGraphService`
  - `IGroupGraphService -> GroupGraphService`
  - `IContentGraphService -> ContentGraphService`
  - `ICandidateService -> CandidateService`
- GraphQL endpoint: `/graphql`
- REST controller endpoint: map qua `MapControllers()`.
- `CorrelationIdMiddleware`: preserve/tao `X-Correlation-ID`.
- `InternalApiAuthenticationMiddleware`: bao ve moi route `/internal/*` bang fixed-time shared-secret comparison.

### SocialGraph.Api.csproj

Target framework va packages:

- `.NET 8`
- HotChocolate GraphQL/Federation `16.2.2`
- EF Core
- Npgsql PostgreSQL provider
- StackExchange Redis cache package
- NRedisStack cho RedisJSON
- Swashbuckle hien co nhung chua cau hinh Swagger UI.

### appsettings.json

Dang co:

- `ConnectionStrings:PostgreSQL`: PostgreSQL database `fakebook`, search path `social_graph`.
- `ConnectionStrings:Redis`: Redis local.
- `Gateway:InternalSharedSecret`: shared secret toi thieu 32 byte, dung cho ca inbound `/internal/*` va outbound internal calls.
- `ExternalServices:AuthenticationServiceCreateUser`: Auth internal endpoint `/internal/users`.
- `InternalServices:Search:BaseUrl`: Search base URL, mac dinh local `http://localhost:5191`.
- `InternalServices:Recommendation:BaseUrl`: Recommendation base URL, mac dinh local `http://localhost:8000`.
- `InternalServices:TimeoutSeconds`: timeout outbound call, mac dinh 10 giay va clamp trong khoang 1..60.
- `ExternalServices:*`: URL cac service legacy/khac nhu Auth delete, Notification va Messenger.

Search va Recommendation khong cau hinh tung operation URL. Client ghep canonical path tu base URL. Secret phai trung voi Auth, Search, Recommendation va caller cua REST internal SocialGraph.

## 3. Database model

### Objects.cs

```csharp
public class Objects
{
    public long id { get; set; }
    public short otype { get; set; }
    public string data { get; set; } = "{}";
}
```

Y nghia:

- `id`: Snowflake ID.
- `otype`: loai object.
- `data`: JSONB luu payload thuc te.

Khong con dung EF DTO typed cho `User`, `Post`, `Group`. DB chi map generic JSONB.

### Associations.cs

```csharp
public class Associations
{
    public long id1 { get; set; }
    public short atype { get; set; }
    public long id2 { get; set; }
    public long time { get; set; }
}
```

Y nghia:

- `id1`: object nguon.
- `atype`: loai quan he.
- `id2`: object dich.
- `time`: Unix milliseconds, dung lam score Redis sorted set va sort thoi gian.

### MyDbContext.cs

Map EF:

- Schema mac dinh: `social_graph`.
- Table `objects`:
  - primary key: `id`
  - `data` column type: `jsonb`
- Table `associations`:
  - composite primary key: `(id1, atype, id2)`
  - index `(id1, atype, id2)`
  - index `(id2, atype, id1)`

## 4. Object types va Association types

File: `Service/GraphConstants.cs`

### Object types

| otype | Name | data JSON |
|---:|---|---|
| 0 | User | `avatar`, `background`, `name`, `bio`, `gender`, `birthdate`, `location`, `verify`, `privacy`, `create` |
| 1 | Group | `avatar`, `background`, `name`, `bio`, `privacy`, `create` |
| 2 | FeedPost | `content`, `privacy`, `create` |
| 3 | GroupPost | `content`, `create` va co association `published_in(10)` den group |
| 4 | Reel | `content`, `create` |
| 5 | Story | `content`, `create`, `expire` |
| 6 | Comment | `content`, `create` |
| 7 | Media | `type`, `url` |

### Association types

| atype | Name | Huong |
|---:|---|---|
| 0 | Friend | user <-> user, inverse chinh no |
| 1 / 2 | Followed / FollowedBy | user -> user |
| 3 / 4 | Liked / LikedBy | user -> post/comment/reel/story |
| 5 / 6 | Authored / AuthoredBy | user -> post/comment/reel/story |
| 7 | Comment | target -> comment, khong inverse |
| 8 | Share | new feed post/story -> shared post/reel, khong inverse |
| 9 / 10 | Published / PublishedIn | group -> group post |
| 11 / 12 | Tagged / TaggedIn | feed post -> user |
| 13 / 14 | Member / HaveMember | user -> group |
| 15 / 16 | Admin / HaveAdmin | user -> group |
| 17 / 18 | Watched / WatchedBy | user -> reel/story |
| 19 | Saved | user -> post/reel, khong inverse |
| 20 | Contained | content -> media, khong inverse |
| 21 | Mentioned | content/comment -> user, khong inverse |
| 22 | Owned | user/group -> media, khong inverse |
| 23 / 24 | Blocked / BlockedBy | user -> user |

Association co inverse se duoc `AssociationService.AddAssociationAsync` tao ca 2 chieu neu atype la chieu goc.

## 5. Contracts

File: `Contracts/SocialGraphContracts.cs`

### Input contracts

#### CreateUserInput

Input:

- `Name: string`
- `Gender: bool`
- `Birthdate: string`
- `Location: string`
- `Email: string`
- `Password: string`

Dung cho `createUser`. API dang ky khong nhan `avatar` hoac `background`; 2 field nay chi doi sau bang mutation rieng. `Password` van phai thoa policy cua Authentication.

#### CreateUserPayload

- `Success: bool`
- `UserId: long?`
- `Message: string?`

#### UpdateUserInput

Input:

- `Id: long`
- `Avatar?: string`
- `Background?: string`
- `Name?: string`
- `Bio?: string`
- `Gender?: bool`
- `Birthdate?: string`
- `Location?: string`
- `Privacy?: int`

Field null se khong patch.

#### CreateGroupInput

- `CreatorId: long`
- `Name: string`
- `Bio?: string`
- `Privacy: int`
- `Avatar?: string`
- `Background?: string`

#### UpdateGroupInput

- `Id: long`
- `Avatar?: string`
- `Background?: string`
- `Name?: string`
- `Bio?: string`
- `Privacy?: int`

#### MediaInput

- `Type: int`
- `Url: string`

Media type theo convention: photo/video/audio/file/link. Frontend truyen URL da upload; SocialGraph tao media object va association tu URL nay, khong yeu cau frontend truyen media id.

#### CreateFeedPostInput

- `AuthorId: long`
- `Content: string`
- `Privacy: int`
- `Media?: IReadOnlyList<MediaInput>`

#### CreateGroupPostInput

- `AuthorId: long`
- `GroupId: long`
- `Content: string`
- `Media?: IReadOnlyList<MediaInput>`

Group post khong nhan `Privacy`. Khi doc/hien thi, privacy cua group post lay tu group chua post.

#### UpdatePostInput

- `Id: long`
- `Privacy: int`

Chi dung cho feed post. Group post khong co privacy rieng.

#### CreateCommentInput

- `AuthorId: long`
- `TargetId: long`
- `Content: string`

#### CreateStoryInput

- `AuthorId: long`
- `Content: string`
- `Media?: MediaInput`

Story expire tu tinh bang `create + 1 day`, frontend khong truyen expire. Story chi cho toi da 1 media.

#### CreateReelInput

- `AuthorId: long`
- `Content: string`
- `Media?: MediaInput`

Reel chi cho toi da 1 media.

#### SharePostInput

- `AuthorId: long`
- `SourceId: long`
- `Content: string`
- `Privacy: int`

### Output contracts

#### SocialGraphObjectResult

- `id: long`
- `otype: short`
- `data: string`

Core/debug output cua object.

#### AssociationPageResult

- `items: AssociationEdgeResult[]`
- `nextCursor?: string`

Dung cho association pagination.

#### UserProfileResult

- `Id`
- `Avatar`
- `Background`
- `Name`
- `Bio`
- `Gender`
- `Birthdate`
- `Location`
- `Privacy`
- `Create`
- `Verify`
- `IsVerified`
- `FriendCount`
- `FollowerCount`
- `FollowingCount`

`Verify` la thoi diem het han tich xanh dang luu trong `user.data.verify`. `IsVerified` duoc tinh bang cach parse `Verify` va so sanh voi thoi gian hien tai.

#### GroupResult

- `Id`
- `Avatar`
- `Background`
- `Name`
- `Bio`
- `Privacy`
- `Create`
- `MemberCount`
- `AdminCount`

#### ContentResult

- `Id`
- `Type`
- `Content`
- `Privacy`
- `Create`
- `AuthorId`
- `Media`

#### CandidateItemResult

- `Id`
- `AuthorId`
- `Source`: `friend`, `followed`, `group`, `recent_public`
- `CreatedAt`

## 6. Core services

### IObjectService / ObjectService

#### AddObjectAsync(short otype, string dataJson)

Input:

- `otype`: object type.
- `dataJson`: JSON object string phu hop voi otype.

Return:

- `SocialGraphObjectResult`.

Logic:

1. Goi `ObjectTypeRules.NormalizeObjectJson` de validate otype va JSON object.
2. Sinh `id` bang `IdGeneratorUtil.GenerateId`.
3. Insert vao PostgreSQL:
   - `social_graph.objects(id, otype, data)`
   - `data` la JSONB parameter.
4. Cache RedisJSON:
   - key: `{id}`
   - value: `{ "otype": ..., "data": ... }`
5. Tra object vua tao.

#### UpdateObjectAsync(long id, short otype, string patchJson)

Input:

- `id`
- `otype`
- `patchJson`: JSON object chi gom field muon update.

Return:

- `SocialGraphObjectResult?`

Logic:

1. Goi `ObjectTypeRules.FilterPatch` de chi giu field duoc phep sua theo otype.
2. Neu patch rong:
   - retrieve object hien tai.
   - neu otype khop thi tra object, neu khong tra null.
3. Update PostgreSQL bang JSONB merge:
   - `data = data || @patch`
   - dieu kien `id` va `otype`.
4. Neu Redis co key object thi patch tung field bang `JSON.SET $.data.{field}`.
5. Retrieve lai object va tra ve.

#### DeleteObjectAsync(long id)

Input:

- `id`

Return:

- `bool`

Logic:

1. Delete row trong PostgreSQL.
2. Neu xoa thanh cong thi delete Redis key `{id}`.
3. Tra `true` neu co row bi xoa.

#### RetrieveObjectAsync(long id)

Input:

- `id`

Return:

- `SocialGraphObjectResult?`

Logic:

1. Doc RedisJSON key `{id}` bang `JSON.GET`.
2. Neu cache hit va parse duoc thi tra object.
3. Neu miss, query PostgreSQL.
4. Neu DB co object thi nap RedisJSON roi tra.
5. Neu khong co thi tra null.

### IAssociationService / AssociationService

#### AddAssociationAsync(long id1, short atype, long id2)

Input:

- `id1`
- `atype`
- `id2`

Return:

- `bool`

Logic:

1. Lay `time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`.
2. Upsert association `(id1, atype, id2, time)`.
3. Neu `atype` co inverse thi upsert `(id2, inverseAtype, id1, time)`.
4. Commit transaction DB.
5. Cap nhat Redis sorted set neu cache da loaded; neu chua loaded thi hydrate tu DB.
6. Tra true.

Redis:

- key: `{id1}:{atype}`
- member: `id2`
- score: `time`
- marker key: `{id1}:{atype}:cached`

#### DeleteOneAssociationAsync(long id1, short atype, long id2)

Logic:

1. Delete association goc.
2. Neu atype co inverse thi delete inverse.
3. Xoa member khoi Redis sorted set neu cache da loaded.
4. Tra true neu co row bi delete.

#### DeleteAllAssociationAsync(long id1, short atype)

Logic:

1. Neu atype co inverse thi doc list id2 de biet nhung inverse cache can cap nhat.
2. Delete tat ca row co `(id1, atype)`.
3. Neu co inverse thi delete row inverse co `atype = inverse` va `id2 = id1`.
4. Delete cache key `{id1}:{atype}` va marker.
5. Remove `id1` khoi cache inverse neu cac cache do dang loaded.

#### DeleteObjectAssociationsAsync(long objectId)

Logic:

1. Tim cac association key bi anh huong khi object nam o `id1` hoac `id2`.
2. Delete moi association co `id1 = objectId` hoac `id2 = objectId`.
3. Delete cache cua cac key bi anh huong.
4. Tra so row bi delete.

Dung khi xoa user/group/content.

#### CountAssociationAsync(long id1, short atype)

Logic:

1. Neu cache marker chua co thi hydrate tu DB.
2. Tra `SortedSetLength`.

#### RetrieveAssociationAsync(long id1, short atype, string? cursor, int limit)

Logic:

1. Parse `cursor` thanh offset. Cursor hien tai la offset string.
2. Clamp `limit` trong khoang `1..100`.
3. Neu cache marker chua co thi hydrate tu DB.
4. Doc Redis sorted set bang rank descending.
5. Tra list `AssociationEdgeResult(id2, time)` va `nextCursor`.

## 7. Business services

### UserGraphService

#### CreateUserAsync(CreateUserInput input)

Return: `CreateUserPayload`.

Logic:

1. Tao user object type `0`.
2. Data mac dinh:
   - `avatar = ""`
   - `background = ""`
   - `name`
   - `bio = "Xin chao, minh la {name} den tu {location}"`
   - `gender = 1 neu input.Gender true, nguoc lai 0`
   - `birthdate`
   - `location`
   - `verify = ""`
   - `privacy = 0`
   - `create = UTC now`
3. Goi Authentication internal create user voi `userId`, `email`, `password`, `displayName`, `dob`.
4. Neu Authentication fail, xoa object user vua tao trong SocialGraph va tra `{ success: false, userId: null }`.
5. Neu Authentication thanh cong, chay dong thoi `PUT /internal/search/indexes/{userId}` va `PUT /internal/recommendation/users/{userId}/embedding`.
6. Hai projection call idempotent, best-effort; non-2xx/network timeout duoc log va khong rollback Auth/SocialGraph da tao.
7. Messenger create user tam thoi disable.
8. Tra `{ success: true, userId }`.

#### UpdateUserAsync(UpdateUserInput input)

Return: `UserProfileResult?`.

Logic:

1. Tao patch JSON tu field non-null, gom ca `avatar` va `background` neu co.
2. Update object type user.
3. Neu update name thi goi Search update index.
4. Tra profile moi.

#### DeleteUserAsync(long userId)

Return: `bool`.

Logic:

1. Xoa tat ca association lien quan user.
2. Xoa user object.
3. Neu xoa thanh cong thi goi external delete:
   - Auth
   - Messenger
   - Search
   - Recommendation user embedding

#### GetProfileAsync(long userId)

Return: `UserProfileResult?`.

Logic:

1. Retrieve object.
2. Neu khong co hoac khong phai user thi null.
3. Parse JSON data.
4. Dem friend/follower/following bang association counts.
5. Parse `verify` trong user data; neu la thoi gian tuong lai thi `IsVerified = true`.
6. Tra profile result.

#### SetUserVerifyAsync(long userId, DateTimeOffset? expiresAt)

Chi duoc goi qua REST internal de payment/billing service cap nhat tich xanh.

Logic:

1. Neu `expiresAt` co gia tri thi convert sang UTC ISO string.
2. Neu `expiresAt` null thi set `verify = ""` de clear tich xanh.
3. Goi `UpdateSystemObjectAsync` de patch field `verify` bo qua mutable field rules thong thuong.
4. Tra profile moi.

Field `verify` khong nam trong `ObjectTypeRules.MutableFields`, nen GraphQL `updateUser` va raw `updateObject` khong sua duoc field nay.

#### ChangeUserAvatarAsync(long userId, string avatarUrl, string? originalUrl)

Logic:

1. Kiem tra user ton tai va object type la user.
2. Neu `originalUrl` khong rong:
   - tao media object type `7` voi `{ type = photo, url = originalUrl }`.
   - tao association `userId --owned(22)--> mediaId`.
3. Patch `avatar = avatarUrl` trong user data.
4. Tra profile moi.

`avatarUrl` la URL anh da crop de hien thi truc tiep tren profile. `originalUrl` la URL anh goc user vua upload; neu user chon lai anh da owned va chi gui anh crop thi `originalUrl` co the null/rong.

#### ChangeUserBackgroundAsync(long userId, string backgroundUrl, string? originalUrl)

Logic:

1. Kiem tra user ton tai va object type la user.
2. Neu `originalUrl` khong rong:
   - tao media object type `7` voi `{ type = photo, url = originalUrl }`.
   - tao association `userId --owned(22)--> mediaId`.
3. Patch `background = backgroundUrl` trong user data.
4. Tra profile moi.

`backgroundUrl` la URL anh background da crop de hien thi tren profile. `originalUrl` la URL anh goc; neu user chon anh da owned thi co the null/rong de khong tao media moi.

#### SendFriendRequestAsync(long requesterId, long receiverId)

Return: `bool`.

Logic hien tai:

1. Goi Notification action `friend_request`.
2. Tra true.

Luu y: SocialGraph hien chua luu pending friend request. Notification service nen luu notification/action pending neu can.

#### AcceptFriendRequestAsync(long requesterId, long receiverId)

Logic:

1. Tao association friend type `0` giua requester va receiver.
2. Goi Notification action `friend_accept`.
3. Tra bool.

#### FollowUserAsync / UnfollowUserAsync

Logic:

- Follow: tao association `follower --followed(1)--> target`, inverse `followed_by(2)`.
- Unfollow: xoa association do va inverse.

#### BlockUserAsync / UnblockUserAsync

Logic block:

1. Xoa friend neu co.
2. Xoa follower/following giua 2 user neu co.
3. Tao association `blocked(23)` va inverse `blocked_by(24)`.

Unblock xoa association `blocked(23)`.

### GroupGraphService

#### CreateGroupAsync(CreateGroupInput input)

Logic:

1. Tao group object type `1`.
   - data gom `avatar`, `background`, `name`, `bio`, `privacy`, `create`.
2. Tao association `creator --admin(15)--> group`, inverse `have_admin(16)`.
3. Goi Search create index group.
4. Tra `GroupResult`.

#### UpdateGroupAsync(UpdateGroupInput input)

Logic:

1. Patch avatar/background/name/bio/privacy.
2. Neu name thay doi thi goi Search update index.
3. Tra group moi.

#### DeleteGroupAsync(long groupId)

Logic:

1. Xoa tat ca association lien quan group.
2. Xoa group object.
3. Goi Search delete index neu xoa thanh cong.

#### GetGroupAsync(long groupId)

Logic:

1. Retrieve group object.
2. Parse data.
3. Dem member/admin bang association count.
4. Tra `GroupResult`.

#### ChangeGroupAvatarAsync

Patch field `avatar` trong group data.

#### ChangeGroupBackgroundAsync(long groupId, string backgroundUrl, string? originalUrl)

Logic:

1. Kiem tra group ton tai va object type la group.
2. Neu `originalUrl` khong rong:
   - tao media object type `7` voi `{ type = photo, url = originalUrl }`.
   - tao association `groupId --owned(22)--> mediaId`.
3. Patch `background = backgroundUrl` trong group data.
4. Tra group moi.

`backgroundUrl` la anh background da crop. `originalUrl` la anh goc neu group vua upload anh moi.

#### AddMemberAsync / RemoveMemberAsync

- Add: `user --member(13)--> group`, inverse `have_member(14)`.
- Remove: xoa association tren.

#### AddAdminAsync / RemoveAdminAsync

- AddAdmin: remove member truoc, sau do tao `admin(15)`.
- RemoveAdmin: xoa `admin(15)`.

### ContentGraphService

#### CreateFeedPostAsync(CreateFeedPostInput input)

Logic:

1. Tao feed post object type `2`: `{content, privacy, create}`.
2. Voi moi media input:
   - tao media object type `7`.
   - tao `author --owned(22)--> media`.
   - tao `post --contained(20)--> media`.
3. Tao `author --authored(5)--> post`, inverse `authored_by(6)`.
4. Goi Search create index post.
5. Goi Recommendation create post embedding voi content + media URLs.
6. Tra `ContentResult`.

#### CreateGroupPostAsync(CreateGroupPostInput input)

Logic:

1. Tao group post object type `3`: `{content, create}`. Khong luu `privacy` rieng trong group post.
2. Voi moi media input:
   - tao media object type `7`.
   - tao `author --owned(22)--> media`.
   - tao `post --contained(20)--> media`.
3. Tao `author --authored(5)--> post`, inverse `authored_by(6)`.
4. Tao association `group --published(9)--> post`, inverse `published_in(10)`.
5. Goi Search create index post.
6. Goi Recommendation create post embedding voi content + media URLs.
7. Tra content result. `Privacy` trong result lay tu group hien tai.

#### UpdatePostAsync(UpdatePostInput input)

Logic:

1. Patch object type `FeedPost(2)` voi field `privacy`.
2. Neu id la group post thi update fail vi otype khong khop, tra null.
3. Khong can check `published_in(10)` de phan biet feed/group post.
4. Tra content result moi.

#### DeleteContentAsync(long contentId)

Logic:

1. Retrieve object.
2. Xoa tat ca association lien quan.
3. Xoa object.
4. Neu object la post thi goi:
   - Recommendation delete post embedding.
   - Search delete index.

#### GetContentAsync(long contentId)

Logic:

1. Retrieve object.
2. Lay author qua association `authored_by(6)`.
3. Lay media qua association `contained(20)`.
4. Build `ContentResult`.
   - Feed post: `Privacy` lay tu post data.
   - Group post: `Privacy` lay tu group data qua association `published_in(10)`.

#### CreateCommentAsync(CreateCommentInput input)

Logic:

1. Tao comment object type `6`.
2. Tao `author --authored(5)--> comment`.
3. Tao `target --comment(7)--> comment`.
4. Lay author cua target.
5. Neu target author ton tai va khac commenter thi goi Notification action `comment`.
6. Tra `ContentResult`.

#### CreateStoryAsync(CreateStoryInput input)

Logic:

1. Tao story object type `5`: `{content, create, expire}` voi `expire = create + 1 day`.
2. Attach toi da 1 media neu co.
3. Tao authored association.
4. Tra result.

#### CreateReelAsync(CreateReelInput input)

Logic tuong tu story nhung object type `4`, khong co expire va chi attach toi da 1 media.

#### SharePostAsync(SharePostInput input)

Logic:

1. Tao post moi bang `CreateFeedPostAsync`.
2. Tao association `newPost --share(8)--> source`.
3. Tra post moi.

#### LikeAsync / UnlikeAsync

Logic:

- Like: `user --liked(3)--> target`, inverse `liked_by(4)`.
- Neu target co author khac user, goi Notification action `like`.
- Unlike: xoa association liked.

#### SaveAsync / UnsaveAsync

- Save: `user --saved(19)--> target`.
- Unsave: xoa association saved.

#### WatchAsync

- Tao `user --watched(17)--> target`, inverse `watched_by(18)`.

#### TagAsync

- Tao `post --tagged(11)--> user`, inverse `tagged_in(12)`.
- Goi Notification action `tag`.

#### MentionAsync

- Tao `source --mentioned(21)--> user`.
- Goi Notification action `mention`.

### CandidateService

#### GetPostCandidatesAsync(long userId, int limit)

Return: `IReadOnlyList<CandidateItemResult>`.

Logic:

1. Clamp limit `1..500`.
2. Lay blocked ids:
   - `blocked(23)`
   - `blocked_by(24)`
3. Gom candidate tu:
   - friend authors: user `friend(0)` -> author `authored(5)` feed post object type `2`.
   - followed authors: user `followed(1)` -> author `authored(5)` feed post object type `2`.
   - groups: user `member/admin(13/15)` -> group `published(9)` group post object type `3`.
   - fallback recent public feed posts: object type `2`, privacy `0`.
4. Group posts chi lay qua groups cua user vi chung la object type `3`, khong phai loc bang `published_in`.
5. Loai candidate cua author bi block.
6. Sort tam thoi theo id desc, Snowflake id lon hon gan voi bai moi hon.
7. Tra top limit.

Recommendation service moi la noi rank feed cuoi cung.

#### GetReelCandidatesAsync(long userId, int limit)

Tuong tu post nhung nguon:

- friend authors -> authored reels.
- followed authors -> authored reels.
- recent public reels.

### ExternalServiceClient

Phan lon call external la best-effort, rieng Authentication create user la required. Moi operation tao mot correlation ID (hoac dung ID inbound), gui `X-Gateway-Secret` va `X-Correlation-ID`:

- Neu endpoint thieu config: log debug va return.
- Neu service tra non-success: log warning.
- Neu network/timeout: log warning.
- Required Auth create user fail se throw trong client, UserGraphService rollback object user local.

Methods:

- `NotifyAsync`: POST NotificationServiceCreateNotification payload `{ creatorId, receiverId, actionType, objectId, data }`.
- `CreateUserAsync`: goi Auth create bat buoc; chi khi Auth thanh cong moi chay Search va Recommendation dong thoi theo best-effort. Messenger create dang tam disable.
- `DeleteUserAsync`: goi Auth delete, Search delete index, Recommend delete user embedding. Messenger delete dang tam disable.
- `CreateSearchIndexAsync` / `UpdateSearchIndexAsync`: `PUT /internal/search/indexes/{objectId}`, body `{ objectType, text }`.
- `DeleteSearchIndexAsync`: `DELETE /internal/search/indexes/{objectId}`.
- `CreateUserEmbeddingAsync`: `PUT /internal/recommendation/users/{userId}/embedding`.
- `DeleteUserEmbeddingAsync`: `DELETE /internal/recommendation/users/{userId}/embedding`.
- `CreatePostEmbeddingAsync`: `PUT /internal/recommendation/posts/{postId}/embedding`, body `{ content, mediaUrls }`.
- `DeletePostEmbeddingAsync`: `DELETE /internal/recommendation/posts/{postId}/embedding`.
- `CreateMessengerUserAsync`: POST `{ userId }`.
- `DeleteMessengerUserAsync`: POST `{ userId }`.

## 8. GraphQL API

GraphQL endpoint: `/graphql`.

### Query

#### object(id)

Input:

- `id: Long`

Return:

- `SocialGraphObjectResult?`

Dung debug/core.

#### association(id1, atype, cursor, limit)

Return:

- `AssociationPageResult`.

#### associationCount(id1, atype)

Return:

- `Long`.

#### profile(userId)

Return:

- `UserProfileResult?`.

#### group(groupId)

Return:

- `GroupResult?`.

#### content(contentId)

Return:

- `ContentResult?`.

#### relationIds(id1, atype, cursor, limit)

Return:

- `long[]`.

Lay list id2 theo association.

#### postCandidates(userId, limit)

Return:

- `CandidateItemResult[]`.

#### reelCandidates(userId, limit)

Return:

- `CandidateItemResult[]`.

### Mutation

Core/debug:

- `addObject(otype, dataJson)`
- `updateObject(id, otype, patchJson)`
- `deleteObject(id)`
- `addAssociation(id1, atype, id2)`
- `deleteOneAssociation(id1, atype, id2)`
- `deleteAllAssociation(id1, atype)`

User:

- `createUser(input: CreateUserInput)`
- `updateUser(input: UpdateUserInput)`
- `deleteUser(userId)`
- `changeUserAvatar(userId, avatarUrl, originalUrl)`
- `changeUserBackground(userId, backgroundUrl, originalUrl)`

Relation:

- `sendFriendRequest(requesterId, receiverId)`
- `acceptFriendRequest(requesterId, receiverId)`
- `followUser(followerId, targetUserId)`
- `unfollowUser(followerId, targetUserId)`
- `blockUser(blockerId, blockedUserId)`
- `unblockUser(blockerId, blockedUserId)`

Group:

- `createGroup(input)`
- `updateGroup(input)`
- `deleteGroup(groupId)`
- `changeGroupAvatar(groupId, avatarUrl)`
- `changeGroupBackground(groupId, backgroundUrl, originalUrl)`
- `addGroupMember(groupId, userId)`
- `removeGroupMember(groupId, userId)`
- `addGroupAdmin(groupId, userId)`
- `removeGroupAdmin(groupId, userId)`

Content:

- `createFeedPost(input)`
- `createGroupPost(input)`
- `updatePost(input)`
- `deleteContent(contentId)`
- `createComment(input)`
- `createStory(input)`
- `createReel(input)`
- `sharePost(input)`
- `like(userId, targetId)`
- `unlike(userId, targetId)`
- `save(userId, targetId)`
- `unsave(userId, targetId)`
- `watch(userId, targetId)`
- `tag(postId, userId)`
- `mention(sourceId, userId)`

## 9. REST internal API

Moi endpoint `/internal/*` yeu cau `X-Gateway-Secret`. Secret server phai co it nhat 32 byte; missing/wrong secret tra `403`, chua cau hinh hop le tra `503`. Middleware preserve hoac tao `X-Correlation-ID` va tra ID trong response.

Files:

- `RestAPI/RecommendationController.cs`
- `RestAPI/PaymentController.cs`

Route prefix recommendation: `/internal/recommendation`.

### GET /internal/recommendation/post-candidates

Query:

- `userId: long`
- `limit: int = 200`

Return:

```json
[
  {
    "id": 123,
    "authorId": 456,
    "source": "friend",
    "createdAt": "2026-07-09T00:00:00.0000000Z"
  }
]
```

### GET /internal/recommendation/reel-candidates

Tuong tu post candidates nhung object type reel.

### PUT /internal/users/{userId}/verify

Endpoint internal de payment/billing service cap nhat tich xanh cho user sau khi thanh toan thanh cong.

Request body:

```json
{
  "expiresAt": "2026-08-10T00:00:00Z"
}
```

Logic:

- `expiresAt` co gia tri: patch `user.data.verify` bang UTC ISO string.
- `expiresAt = null`: clear `user.data.verify`, user het tich xanh.
- Tra `UserProfileResult`; neu user khong ton tai thi `404`.

Field `verify` khong sua duoc qua GraphQL `updateUser` hay raw `updateObject`.

## 10. Redis cache

### Object cache

- Key: `{id}`
- Type: RedisJSON
- Value:

```json
{
  "otype": 0,
  "data": {
    "name": "Nguyen Van A"
  }
}
```

### Association cache

- Key: `{id1}:{atype}`
- Type: sorted set
- Member: `id2`
- Score: `time`
- Marker key: `{id1}:{atype}:cached`

Marker can thiet vi Redis khong giu sorted set rong. Khong co marker thi coi nhu cache miss va hydrate tu DB.

## 11. Nhung diem can luu y khi service khac tich hop

- SocialGraph external calls khong dam bao transaction distributed. Neu Auth/Search/Recommend loi, graph local van co the da ghi thanh cong.
- Friend request hien chi tao notification, chua co pending table trong SocialGraph.
- Privacy rule hien moi luu field `privacy`, chua enforce day du tren query/candidate ngoai fallback recent public.
- Payment/Billing service khong ghi DB SocialGraph truc tiep; khi thanh toan tich xanh thanh cong thi goi REST internal `PUT /internal/users/{userId}/verify`.
- Recommendation service nen coi candidate cua SocialGraph la input pool, khong phai final feed.
- Notification service phai hieu action type giong `ExternalNotificationAction`.
