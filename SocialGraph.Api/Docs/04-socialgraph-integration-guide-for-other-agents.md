# SocialGraph Integration Guide Cho Agent O Service Khac

File nay noi ro SocialGraph service hien co gi, service khac can implement endpoint nao de khop, va Gateway/Frontend nen goi nhu the nao.

## 1. Tom tat SocialGraph hien tai

SocialGraph la service quan ly graph xa hoi:

- User profile.
- Group.
- Post/reel/story/comment/media.
- Friend/follow/block.
- Group member/admin.
- Like/comment/share/save/watch/tag/mention.
- Candidate post/reel cho Recommendation.
- User verify/tich xanh luu trong `user.data.verify`; payment service cap nhat qua REST internal.

GraphQL endpoint:

- `/graphql`

REST internal:

- `/internal/recommendation/post-candidate-ids`
- `/internal/recommendation/reel-candidates`
- `/internal/users/{userId}/verify`
- `DELETE /internal/stories/expired`

## 2. Service boundary

Frontend:

- Khong goi REST cua SocialGraph.
- Goi Gateway GraphQL.
- Gateway federate den SocialGraph GraphQL.

Service-to-service:

- SocialGraph goi REST den Auth/Search/Recommend/Notification. Messenger create/delete dang tam disable.
- Recommendation goi REST candidate cua SocialGraph.
- Payment/Billing goi REST internal cua SocialGraph de cap nhat verify sau khi thanh toan.

## 3. Data model ma service khac can hieu

### User ID

User ID duoc tao tai SocialGraph bang Snowflake ID. Khi tao user:

1. Gateway goi `createUser` tren SocialGraph.
2. SocialGraph tao user id.
3. SocialGraph goi Auth service voi user id + email + password + display name + dob.
4. Neu Auth fail, SocialGraph rollback object user vua tao.
5. Cac service khac nen coi user id tu SocialGraph la canonical ID.

### Object IDs

Post/group/reel/story/comment/media ID cung do SocialGraph tao.

Search/Recommendation/Messenger/Notification khong tu tao object ID cho cac object graph nay.

### Privacy

Feed post la object type `2` va co `privacy` rieng trong post data. Group post la object type `3`, khong co `privacy` rieng; khi doc/hien thi thi privacy lay tu group chua post qua `published_in(10)`. Service khac nen khong gop 2 loai post thanh mot type.

Story chi share feed post public hoac reel. SocialGraph kiem tra privacy feed post luc tao va moi lan doc; neu source bi chuyen sang private hoac bi xoa thi story share khong duoc return.

## 4. GraphQL operation chinh cho Gateway

Gateway hien compose SocialGraph nhung chi expose public mutation `createUser`. Story authorization da co trong subgraph, nhung source schema/extensions va `gateway.far` hien tai chua duoc refresh de public Story. Cac field business khac van `@internal` cho den khi co authorization rieng.

Ten GraphQL field co the duoc HotChocolate camelCase tu method C#:

- `GetProfileAsync` -> `profile`
- `CreateUserAsync` -> `createUser`
- `CreateFeedPostAsync` -> `createFeedPost`

Export schema that cua SocialGraph vao mot thu muc tam, copy rieng `schema.graphqls` sang Gateway, sau do compose lai `gateway.far` cung schema Authentication:

```powershell
dotnet run --project .\SocialGraph.Api\SocialGraph.Api.csproj -- schema export --output <temporary-absolute-path-to-schema.graphqls>
```

Lenh export cung sinh `schema-settings.json` mac dinh. Khong ghi de file settings va extensions do Gateway so huu.

### User

Mutation `createUser(input)`:

```json
{
  "input": {
    "name": "Nguyen Van A",
    "gender": true,
    "birthdate": "2000-01-01",
    "location": "Ha Noi",
    "email": "a@example.com",
    "password": "secret123"
  }
}
```

Return `CreateUserPayload`: `{ success, userId, message }`. `success: true` nghia la SocialGraph da tao user local va Auth da tao identity voi dung `userId`.

Dang ky khong nhan `avatar` hoac `background`. Hai field nay cap nhat sau bang mutation rieng.

User mutations lien quan avatar/background:

- `changeUserAvatar(userId, avatarUrl, originalUrl)`
- `changeUserBackground(userId, backgroundUrl, originalUrl)`

Query `profile(userId)` return:

- profile data.
- `verify` la thoi diem het han tich xanh, `isVerified` tinh tu `verify > now`.
- friend/follower/following count.
- `avatar` va `background` la URL crop hien thi truc tiep.

### Group

- `createGroup(input)`
- `updateGroup(input)`
- `changeGroupBackground(groupId, backgroundUrl, originalUrl)`
- `deleteGroup(groupId)`
- `addGroupMember(groupId, userId)`
- `addGroupAdmin(groupId, userId)`
- `group(groupId)`

### Content

- `createFeedPost(input)`
- `createGroupPost(input)` khong nhan `privacy`; group post dung privacy cua group.
- `createComment(input)`
- `homeStories(userId, limit, cursor)`
- `myStories(userId)`
- `createNormalStory(input)`
- `createShareStory(input)`
- `deleteStory(input)`
- `createReel(input)`
- `sharePost(input)`
- `like(userId, targetId)`
- `save(userId, targetId)`
- `watch(userId, targetId)`
- `tag(postId, userId)`
- `mention(sourceId, userId)`
- `content(contentId)`

Story query/mutation bat buoc co hai header do Gateway tao sau khi validate session:

```http
X-Gateway-Secret: <shared secret at least 32 bytes>
X-User-Id: <authenticated user id>
```

Gateway phai xoa header cung ten do client gui len. `userId`/`authorId` trong GraphQL phai trung `X-User-Id`; SocialGraph fail closed neu secret/user header thieu, sai hoac mismatch.

`homeStories` page theo author bucket (`limit` clamp `1..50`), con `myStories` tra mot bucket. Ca hai chi filter story het han, khong xoa data. Cleanup chay qua background worker hoac `DELETE /internal/stories/expired?limit=100` voi `X-Gateway-Secret`.

### Relations

- `sendFriendRequest(requesterId, receiverId)`
- `acceptFriendRequest(requesterId, receiverId)`
- `followUser(followerId, targetUserId)`
- `blockUser(blockerId, blockedUserId)`
- `relationIds(id1, atype, cursor, limit)`

## 5. Endpoints ma Auth service can co

SocialGraph config:

- `Gateway:InternalSharedSecret`
- `ExternalServices:AuthenticationServiceCreateUser`
- `ExternalServices:AuthenticationServiceDeleteUser`

Create identity la call required:

```http
POST /internal/users
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <trace id>
Content-Type: application/json
```

```json
{
  "userId": 123,
  "email": "a@example.com",
  "password": "secret123",
  "displayName": "Nguyen Van A",
  "dob": "2000-01-01"
}
```

Auth luu credential bang canonical `userId`, hash password, enforce email unique va tra 2xx khi thanh cong. Neu Auth fail/non-2xx, SocialGraph rollback object user local va khong goi Search/Recommendation.

Sau khi Auth thanh cong, Search va Recommendation duoc provision dong thoi, idempotent va best-effort.

Auth delete hien van la legacy configured POST payload `{ "userId": 123 }` va duoc goi best-effort.

## 6. Endpoints ma Messenger service can co

Messenger create/delete user dang tam disable trong SocialGraph, nhung contract de day cho luc bat lai.

Config:

- `ExternalServices:MessengerServiceCreateUser`
- `ExternalServices:MessengerServiceDeleteUser`

Create payload:

```json
{
  "userId": 123
}
```

Delete payload:

```json
{
  "userId": 123
}
```

Messenger nen dung chung canonical user id.

## 7. Contract voi Search service

Config base URL:

- `InternalServices:Search:BaseUrl`

Create va update cung dung idempotent upsert:

```http
PUT /internal/search/indexes/{objectId}
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <trace id>
Content-Type: application/json
```

```json
{
  "objectType": "post",
  "text": "noi dung index"
}
```

`objectType` hop le: `user`, `group`, `post`, `reel`. `objectId` nam tren path va la positive signed 64-bit Snowflake ID.

Delete:

```http
DELETE /internal/search/indexes/{objectId}
```

Upsert va delete deu phai idempotent. Delete tra `204` ke ca khi index khong ton tai.

## 8. Contract voi Recommendation service

Config base URL:

- `InternalServices:Recommendation:BaseUrl`

User embedding:

```http
PUT /internal/recommendation/users/{userId}/embedding
DELETE /internal/recommendation/users/{userId}/embedding
```

`PUT` khong co body, chi tao vector neu user chua co. `DELETE` idempotent va tra `204`.

Post embedding:

```http
PUT /internal/recommendation/posts/{postId}/embedding
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <trace id>
Content-Type: application/json
```

```json
{
  "content": "post content",
  "mediaUrls": ["https://media.local/a.jpg"]
}
```

Delete post embedding:

```http
DELETE /internal/recommendation/posts/{postId}/embedding
```

Recommendation service nen lay candidates tu SocialGraph:

```http
GET /internal/recommendation/post-candidate-ids?userId=123&limit=200
GET /internal/recommendation/reel-candidates?userId=123&limit=200
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <trace id>
```

Response:

```json
[456, 455, 454]
```

Recommendation nen:

- Dung `post-candidate-ids` lam pool id bai viet, sau do rank o Recommendation service.
- Dung `reel-candidates` neu can pool reel kem `authorId`, `source`, `createdAt`.
- Tu tinh ranking score rieng; SocialGraph chi tra candidate pool va khong tra multiplier tra phi.

## 9. Endpoints ma Notification service can co

Config:

- `ExternalServices:NotificationServiceCreateNotification`

SocialGraph POST payload:

```json
{
  "creatorId": 111,
  "receiverId": 222,
  "actionType": 6,
  "objectId": 333,
  "data": null
}
```

Action types:

- `0 friend_request`
- `1 friend_accept`
- `5 comment`
- `6 like`
- `7 mention`
- `8 tag`

Notification service nen luu DB va cho Gateway/Frontend query notification.

## 10. Endpoint payment/billing can goi SocialGraph

Payment/Billing khong can tao entitlement API cho SocialGraph doc. Khi thanh toan tich xanh thanh cong, payment goi REST internal cua SocialGraph.

Request:

```http
PUT /internal/users/123/verify
```

Body:

```json
{
  "expiresAt": "2026-08-10T00:00:00Z"
}
```

Behavior:

- SocialGraph patch `user.data.verify`.
- `expiresAt = null` de clear tich xanh.
- `profile(userId)` tra `verify` va `isVerified`.
- Payment/Billing khong ghi truc tiep database SocialGraph.

## 11. Upload/media integration

SocialGraph khong sinh upload URL va khong upload binary file. Media/upload service hoac frontend flow ben ngoai phai upload file truoc, sau do dua URL da co vao SocialGraph.

Post/story/reel flow:

1. Client upload file sang media storage/service ben ngoai.
2. Gateway goi `createFeedPost`/`createGroupPost` voi list `MediaInput { type, url }`. Caller khong truyen media id.
3. Gateway goi `createNormalStory`/`createReel` voi toi da mot `MediaInput { type, url }`; story share dung `createShareStory` va khong tao media rieng.
4. SocialGraph tao media object type `7` cho tung URL.
5. SocialGraph tao association:
   - `authorId --owned(22)--> mediaId`
   - `contentId --contained(20)--> mediaId`

Ngoai le Story: media tao rieng cho normal story chi co `storyId --contained(20)--> mediaId` va duoc coi la temporary; khong tao `owned(22)`. Khi story bi xoa/cleanup, media temporary cung bi xoa, nhung media owned hoac dang duoc content khac reference se duoc giu lai.

Avatar flow:

1. Neu user upload anh moi lam avatar, Gateway goi `changeUserAvatar(userId, avatarUrl, originalUrl)`.
2. `originalUrl` la URL anh goc vua upload. Neu co, SocialGraph tao media object photo va association `userId --owned(22)--> mediaId`.
3. `avatarUrl` la URL anh da crop, duoc luu truc tiep vao `user.data.avatar`.
4. Neu user chon lai anh da owned, Gateway co the chi gui `avatarUrl`; `originalUrl` null/rong thi SocialGraph khong tao media moi.

Background flow:

1. User background va group background dung chung pattern 2 URL.
2. `backgroundUrl` la URL anh da crop, luu truc tiep vao `user.data.background` hoac `group.data.background`.
3. `originalUrl` la URL anh goc vua upload. Neu co, SocialGraph tao media object photo va association `ownerId --owned(22)--> mediaId`.
4. Neu chon lai anh da owned, Gateway chi gui `backgroundUrl`; `originalUrl` null/rong thi khong tao media moi.

## 12. Important caveats cho agent khac

- External projection calls tu SocialGraph la best-effort, nen service khac phai idempotent.
- User Search/Recommendation projection chi bat dau sau khi Auth create thanh cong. Neu projection fail, Auth va SocialGraph user van da duoc tao.
- Search va Recommendation user provisioning duoc start dong thoi va dung cung canonical user ID/correlation ID.
- Service khac nen co endpoint repair/reindex neu can sync lai.
- Friend request pending hien nam o Notification domain, khong co table pending trong SocialGraph.
- Candidate endpoint la candidate pool, khong phai final feed.
- Verify/tich xanh chi duoc cap nhat qua REST internal cua SocialGraph; service khac khong doc hay ghi truc tiep DB SocialGraph.
- Story reads side-effect free; operational cleanup phai dung background worker hoac authenticated maintenance endpoint.
- Khi compose Story vao Gateway, public `createNormalStory`, `createShareStory`, `deleteStory`, `homeStories`, `myStories` va forward trusted identity headers.

## 13. Checklist khi code service khac

- Dung `long userId/objectId` tu SocialGraph.
- Implement dung HTTP method, path va body canonical trong file nay.
- Endpoint nen idempotent.
- Tra 2xx neu request duplicate nhung state da dung.
- Validate `X-Gateway-Secret` va propagate/log `X-Correlation-ID`.
- Khong doc truc tiep DB SocialGraph.
- Neu can them contract, cap nhat file nay va appsettings cua SocialGraph.
