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

- `/internal/recommendation/post-candidates`
- `/internal/recommendation/reel-candidates`
- `/internal/users/{userId}/verify`

## 2. Service boundary

Frontend:

- Khong goi REST cua SocialGraph.
- Goi Gateway GraphQL.
- Gateway federate den SocialGraph GraphQL.

Service-to-service:

- SocialGraph goi REST den Auth/Search/Recommend/Messenger/Notification.
- Recommendation goi REST candidate cua SocialGraph.
- Payment/Billing goi REST internal cua SocialGraph de cap nhat verify sau khi thanh toan.

## 3. Data model ma service khac can hieu

### User ID

User ID duoc tao tai SocialGraph bang Snowflake ID. Khi tao user:

1. Gateway goi `createUser` tren SocialGraph.
2. SocialGraph tao user id.
3. SocialGraph goi Auth service voi user id + email + password.
4. Cac service khac nen coi user id tu SocialGraph la canonical ID.

### Object IDs

Post/group/reel/story/comment/media ID cung do SocialGraph tao.

Search/Recommendation/Messenger/Notification khong tu tao object ID cho cac object graph nay.

### Privacy

Feed post la object type `2` va co `privacy` rieng trong post data. Group post la object type `3`, khong co `privacy` rieng; khi doc/hien thi thi privacy lay tu group chua post qua `published_in(10)`. Service khac nen khong gop 2 loai post thanh mot type.

## 4. GraphQL operation chinh cho Gateway

Ten GraphQL field co the duoc HotChocolate camelCase tu method C#:

- `GetProfileAsync` -> `profile`
- `CreateUserAsync` -> `createUser`
- `CreateFeedPostAsync` -> `createFeedPost`

Gateway agent nen introspect schema sau khi chay app de lay ten chinh xac.

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
    "password": "secret"
  }
}
```

Return `Boolean`. `true` nghia la SocialGraph da tao user local va da goi pipeline Auth/Messenger/Search/Recommendation.

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
- `createStory(input)`
- `createReel(input)`
- `sharePost(input)`
- `like(userId, targetId)`
- `save(userId, targetId)`
- `watch(userId, targetId)`
- `tag(postId, userId)`
- `mention(sourceId, userId)`
- `content(contentId)`

### Relations

- `sendFriendRequest(requesterId, receiverId)`
- `acceptFriendRequest(requesterId, receiverId)`
- `followUser(followerId, targetUserId)`
- `blockUser(blockerId, blockedUserId)`
- `relationIds(id1, atype, cursor, limit)`

## 5. Endpoints ma Auth service can co

SocialGraph config keys:

- `AuthenticationServiceCreateUser`
- `AuthenticationServiceDeleteUser`

### Create user

SocialGraph POST payload:

```json
{
  "userId": 123,
  "email": "a@example.com",
  "password": "secret"
}
```

Auth nen:

- Luu user credential voi `userId` canonical.
- Hash password.
- Email unique.
- Tra 2xx neu ok.

SocialGraph hien khong rollback neu Auth fail, nen Auth service nen idempotent theo `userId/email`.

### Delete user

Payload:

```json
{
  "userId": 123
}
```

Nen disable/xoa credential.

## 6. Endpoints ma Messenger service can co

Config:

- `MessengerServiceCreateUser`
- `MessengerServiceDeleteUser`

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

## 7. Endpoints ma Search service can co

Config:

- `SearchServiceCreateIndex`
- `SearchServiceUpdateIndex`
- `SearchServiceDeleteIndex`

### Create/update index

Payload:

```json
{
  "objectId": 123,
  "objectType": "post",
  "text": "noi dung index"
}
```

`objectType` co the la:

- `user`
- `group`
- `post`

Search nen upsert theo `(objectType, objectId)`.

### Delete index

Payload:

```json
{
  "objectId": 123
}
```

Neu Search can objectType de xoa chinh xac, service Search nen chap nhan thieu objectType hoac SocialGraph can duoc update sau.

## 8. Endpoints ma Recommendation service can co

Config SocialGraph goi:

- `RecommendServiceCreateUserEmbedding`
- `RecommendServiceDeleteUserEmbedding`
- `RecommendServiceCreatePostEmbedding`
- `RecommendServiceDeletePostEmbedding`

### Create user embedding

Payload:

```json
{
  "userId": 123
}
```

### Delete user embedding

```json
{
  "userId": 123
}
```

### Create post embedding

```json
{
  "postId": 456,
  "content": "post content",
  "mediaUrls": ["https://media.local/a.jpg"]
}
```

### Delete post embedding

```json
{
  "postId": 456
}
```

Recommendation service nen lay candidates tu SocialGraph:

```http
GET /internal/recommendation/post-candidates?userId=123&limit=200
GET /internal/recommendation/reel-candidates?userId=123&limit=200
```

Response:

```json
[
  {
    "id": 456,
    "authorId": 123,
    "source": "friend",
    "createdAt": "2026-07-09T00:00:00Z"
  }
]
```

Recommendation nen:

- Dung `id` lam post/reel id.
- Dung `authorId` neu can author embedding.
- Dung `source` lam feature.
- Tu tinh ranking score rieng; SocialGraph chi tra candidate pool va khong tra multiplier tra phi.

## 9. Endpoints ma Notification service can co

Config:

- `NotificationServiceCreateNotification`

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
3. Gateway goi `createStory`/`createReel` voi toi da mot `MediaInput { type, url }`.
4. SocialGraph tao media object type `7` cho tung URL.
5. SocialGraph tao association:
   - `authorId --owned(22)--> mediaId`
   - `contentId --contained(20)--> mediaId`

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

- External calls tu SocialGraph la best-effort, nen service khac nen idempotent.
- Neu Search/Recommend fail, SocialGraph van co the da tao post/user.
- Service khac nen co endpoint repair/reindex neu can sync lai.
- Friend request pending hien nam o Notification domain, khong co table pending trong SocialGraph.
- Candidate endpoint la candidate pool, khong phai final feed.
- Verify/tich xanh chi duoc cap nhat qua REST internal cua SocialGraph; service khac khong doc hay ghi truc tiep DB SocialGraph.

## 13. Checklist khi code service khac

- Dung `long userId/objectId` tu SocialGraph.
- Implement endpoint dung payload SocialGraph dang POST/GET.
- Endpoint nen idempotent.
- Tra 2xx neu request duplicate nhung state da dung.
- Log ro request den tu SocialGraph.
- Khong doc truc tiep DB SocialGraph.
- Neu can them contract, cap nhat file nay va appsettings cua SocialGraph.
