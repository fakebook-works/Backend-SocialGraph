# SocialGraph API Reference - GraphQL va REST

File nay liet ke tat ca API hien co cua SocialGraph, gom GraphQL va REST internal. Moi API co input, output, logic chinh va external service call neu co.

Luu y ve ten GraphQL: HotChocolate thuong bo tien to `Get` va hau to `Async`, sau do camelCase ten field. Vi du `GetProfileAsync` thanh `profile`, `CreateUserAsync` thanh `createUser`. Khi schema thay doi, export SDL tu runtime HotChocolate va compose lai `gateway.far`; khong viet schema Gateway bang tay.

Gateway hien chi public SocialGraph `createUser`. Cac query va mutation SocialGraph con lai dang `@internal` cho den khi authorization duoc implement.

## 1. Endpoint Tong Quan

- GraphQL: `POST /graphql`
- REST Recommendation: `GET /internal/recommendation/post-candidates`, `GET /internal/recommendation/reel-candidates`
- REST Payment: `PUT /internal/users/{userId}/verify`

## 2. Common Types

### Object Types

| otype | Name | Data |
|---:|---|---|
| 0 | User | `avatar`, `background`, `name`, `bio`, `gender`, `birthdate`, `location`, `verify`, `privacy`, `create` |
| 1 | Group | `avatar`, `background`, `name`, `bio`, `privacy`, `create` |
| 2 | FeedPost | `content`, `privacy`, `create` |
| 3 | GroupPost | `content`, `create` |
| 4 | Reel | `content`, `create` |
| 5 | Story | `content`, `create`, `expire` |
| 6 | Comment | `content`, `create` |
| 7 | Media | `type`, `url` |

### Association Types

| atype | Name | Huong |
|---:|---|---|
| 0 | Friend | user <-> user |
| 1 / 2 | Followed / FollowedBy | user -> user |
| 3 / 4 | Liked / LikedBy | user -> post/comment/reel/story |
| 5 / 6 | Authored / AuthoredBy | user -> content |
| 7 | Comment | content/comment -> comment |
| 8 | Share | new feed post/story -> shared post/reel |
| 9 / 10 | Published / PublishedIn | group -> group post |
| 11 / 12 | Tagged / TaggedIn | feed post -> user |
| 13 / 14 | Member / HaveMember | user -> group |
| 15 / 16 | Admin / HaveAdmin | user -> group |
| 17 / 18 | Watched / WatchedBy | user -> reel/story |
| 19 | Saved | user -> post/reel |
| 20 | Contained | content -> media |
| 21 | Mentioned | content/comment -> user |
| 22 | Owned | user/group -> media |
| 23 / 24 | Blocked / BlockedBy | user -> user |

### MediaInput

Input:

```json
{
  "type": 0,
  "url": "https://cdn.local/photo.jpg"
}
```

Media type:

- `0` photo
- `1` video
- `2` audio
- `3` file
- `4` link

SocialGraph chi nhan URL da upload xong. Frontend/Gateway khong truyen media id khi tao post/story/reel.

### UserProfileResult

Output:

```json
{
  "id": 123,
  "avatar": "https://cdn.local/avatar-crop.jpg",
  "background": "https://cdn.local/bg-crop.jpg",
  "name": "Nguyen Van A",
  "bio": "Xin chao...",
  "gender": 1,
  "birthdate": "2000-01-01",
  "location": "Ha Noi",
  "privacy": 0,
  "create": "2026-07-11T00:00:00.0000000Z",
  "verify": "2026-08-10T00:00:00.0000000Z",
  "isVerified": true, 
  "friendCount": 10,
  "followerCount": 20,
  "followingCount": 5
}
```

`verify` la thoi diem het han tich xanh. `isVerified` duoc tinh tu `verify > now`. Field `verify` khong sua qua GraphQL update user.

### GroupResult

Output:

```json
{
  "id": 456,
  "avatar": "",
  "background": "",
  "name": "Group name",
  "bio": "",
  "privacy": 0,
  "create": "2026-07-11T00:00:00.0000000Z",
  "memberCount": 12,
  "adminCount": 2
}
```

### ContentResult

Output:

```json
{
  "id": 789,
  "type": 2,
  "content": "hello",
  "privacy": 0,
  "create": "2026-07-11T00:00:00.0000000Z",
  "authorId": 123, 
  "media": [
    {
      "id": 900,
      "type": 0,
      "url": "https://cdn.local/photo.jpg"
    }
  ]
}
```

Feed post co type `2`; group post co type `3`. Voi group post, `privacy` trong result lay tu group chua post qua association `published_in(10)`.

### HomeStoryPageResult

Output cua `homeStories` va `myStories`:

```json
{
  "items": [
    {
      "author": {
        "id": 123,
        "name": "Nguyen Van A",
        "avatar": "https://cdn.local/avatar.jpg",
        "isVerified": true
      },
      "latestCreate": "2026-07-12T10:00:00.0000000Z",
      "stories": [
        {
          "__typename": "NormalStory",
          "id": 901,
          "content": "Story text",
          "create": "2026-07-12T09:00:00.0000000Z",
          "media": [
            {
              "id": 3001,
              "type": 0,
              "url": "https://cdn.local/story.jpg"
            }
          ]
        }
      ]
    }
  ],
  "endCursor": "base64-json",
  "hasNextPage": true
}
```

`limit` trong `homeStories` la so author bucket, khong phai so story. Moi bucket tra toan bo story con han cua author do. Field `stories` la GraphQL union `HomeStory`. `myStories` tra mot `HomeStoryBucketResult?` cho chinh user, khong co paging.

`HomeStory` co 3 concrete type:

```graphql
union HomeStory =
    NormalStory
  | FeedPostShareStory
  | ReelShareStory
```

- `NormalStory`: story thuong, co list `media`.
- `FeedPostShareStory`: story share feed post public.
- `ReelShareStory`: story share reel.

Story share group post da bi chan, ke ca group public. Neu can share group post sau nay thi phai them type moi va can than voi privacy group.

Frontend phai query bang `__typename` va inline fragment:

```graphql
stories {
  __typename

  ... on NormalStory {
    id
    content
    create
    media {
      id
      type
      url
    }
  }

  ... on FeedPostShareStory {
    id
    content
    create
    sharedSource {
      id
      content
      author {
        id
        name
        avatar
        isVerified
      }
      media {
        id
        type
        url
      }
    }
  }

  ... on ReelShareStory {
    id
    content
    create
    sharedSource {
      id
      content
      author {
        id
        name
        avatar
        isVerified
      }
      media {
        id
        type
        url
      }
    }
  }
}
```

Trong `FeedPostSharedSource` va `ReelSharedSource`, field `media` la mot object nullable, khong phai list. Service lay media dau tien cua bai/reel goc de lam preview. `author` cung nullable neu source bi thieu association author.

Vi du normal story:

```json
{
  "__typename": "NormalStory",
  "id": 901,
  "content": "Story text",
  "create": "2026-07-12T09:00:00.0000000Z",
  "media": [
    {
      "id": 3001,
      "type": 0,
      "url": "https://cdn.local/story.jpg"
    }
  ]
}
```

Vi du story share feed post:

```json
{
  "__typename": "FeedPostShareStory",
  "id": 902,
  "content": "Xem bai nay",
  "create": "2026-07-12T10:00:00.0000000Z",
  "sharedSource": {
    "id": 789,
    "content": "Feed post public",
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": false
    },
    "media": {
      "id": 3101,
      "type": 0,
      "url": "https://cdn.local/feed-preview.jpg"
    }
  }
}
```

Vi du story share reel:

```json
{
  "__typename": "ReelShareStory",
  "id": 904,
  "content": "Xem reel nay",
  "create": "2026-07-12T10:00:00.0000000Z",
  "sharedSource": {
    "id": 800,
    "content": "Reel caption",
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": false
    },
    "media": {
      "id": 3201,
      "type": 1,
      "url": "https://cdn.local/reel.mp4"
    }
  }
}
```

### CandidateItemResult

Output:

```json
{
  "id": 789,
  "authorId": 123,
  "source": "friend",
  "createdAt": "2026-07-11T00:00:00.0000000Z"
}
```

`source` co the la `friend`, `followed`, `group`, `recent_public`.

## 3. External Service Calls

Phan lon external call trong SocialGraph la best-effort. Rieng Authentication create user la required; neu Auth fail, SocialGraph rollback object user vua tao, khong goi derived services va tra payload fail.

| Config/contract | Method va path | Body |
|---|---|---|
| `ExternalServices:AuthenticationServiceCreateUser` | `POST /internal/users` | `{ userId, email, password, displayName, dob }` |
| `ExternalServices:AuthenticationServiceDeleteUser` | configured legacy `POST` | `{ userId }` |
| `ExternalServices:MessengerServiceCreateUser` | configured legacy `POST`, tam disable | `{ userId }` |
| `ExternalServices:MessengerServiceDeleteUser` | configured legacy `POST`, tam disable | `{ userId }` |
| `InternalServices:Search:BaseUrl` | `PUT /internal/search/indexes/{objectId}` | `{ objectType, text }` |
| `InternalServices:Search:BaseUrl` | `DELETE /internal/search/indexes/{objectId}` | none |
| `InternalServices:Recommendation:BaseUrl` | `PUT /internal/recommendation/users/{userId}/embedding` | none |
| `InternalServices:Recommendation:BaseUrl` | `DELETE /internal/recommendation/users/{userId}/embedding` | none |
| `InternalServices:Recommendation:BaseUrl` | `PUT /internal/recommendation/posts/{postId}/embedding` | `{ content, mediaUrls }` |
| `InternalServices:Recommendation:BaseUrl` | `DELETE /internal/recommendation/posts/{postId}/embedding` | none |
| `ExternalServices:NotificationServiceCreateNotification` | configured `POST` | `{ creatorId, receiverId, actionType, objectId, data }` |

Moi call gui `X-Gateway-Secret` va `X-Correlation-ID`. Trong registration, Auth chay truoc; Search va Recommendation chi duoc start sau Auth va chay dong thoi bang cung canonical `userId`.

Notification action types:

- `0` friend_request
- `1` friend_accept
- `5` comment
- `6` like
- `7` mention
- `8` tag

## 4. GraphQL Query APIs

### object(id)

Input:

- `id: long`

Logic:

1. Doc RedisJSON key `{id}`.
2. Neu cache hit va JSON hop le thi tra object.
3. Neu miss thi query PostgreSQL `objects`.
4. Neu co object thi nap RedisJSON va tra ve.

External calls: khong co.

Return: `SocialGraphObjectResult?`

```json
{
  "id": 123,
  "otype": 0,
  "data": "{\"name\":\"Nguyen Van A\"}"
}
```

### association(id1, atype, cursor, limit)

Input:

- `id1: long`
- `atype: short`
- `cursor: string?`
- `limit: int`, clamp ve `1..100`

Logic:

1. Cursor la offset string; null/invalid thi offset `0`.
2. Neu Redis marker `{id1}:{atype}:cached` chua co thi hydrate association tu DB vao sorted set.
3. Doc sorted set theo rank descending.
4. Tra `items` va `nextCursor` neu con du lieu.

External calls: khong co.

Return: `AssociationPageResult`

```json
{
  "items": [
    {
      "id2": 456,
      "time": 1783770000000
    }
  ],
  "nextCursor": "20"
}
```

### associationCount(id1, atype)

Input:

- `id1: long`
- `atype: short`

Logic:

1. Dam bao cache association da hydrate.
2. Tra length cua sorted set.

External calls: khong co.

Return: `long`

### profile(userId)

Input:

- `userId: long`

Logic:

1. Retrieve object user type `0`.
2. Parse user data.
3. Dem `friend(0)`, `followed_by(2)`, `followed(1)`.
4. Parse `verify`; neu la DateTime tuong lai thi `isVerified = true`.

External calls: khong co.

Return: `UserProfileResult?`

### group(groupId)

Input:

- `groupId: long`

Logic:

1. Retrieve object group type `1`.
2. Parse group data.
3. Dem `have_member(14)` va `have_admin(16)`.

External calls: khong co.

Return: `GroupResult?`

### content(contentId)

Input:

- `contentId: long`

Logic:

1. Retrieve content object.
2. Lay author qua `authored_by(6)`.
3. Lay media qua `contained(20)`, retrieve tung media object.
4. Neu object la feed post type `2` thi privacy lay tu post data.
5. Neu object la group post type `3` thi privacy lay tu group data qua `published_in(10)`.
6. Story/reel/comment privacy mac dinh `0`.

External calls: khong co.

Return: `ContentResult?`

### homeStories(userId, limit, cursor)

Input:

- `userId: long`
- `limit: int`, clamp ve `1..50`, la so author bucket can lay
- `cursor: string?`, base64 JSON cua `(latestCreate, authorId)` bucket cuoi page truoc

Logic:

1. Lay author co the xem story tu `friend(0)` va `followed(1)` cua user.
2. Loai chinh user khoi danh sach author.
3. Query story type `5` cua cac author do qua `authored(5)`.
4. Voi tung story:
   - neu `expire <= now` hoac expire invalid thi xoa story;
   - neu con han thi dua vao bucket cua author.
5. Khi xoa story het han:
   - lay media qua `story --contained(20)--> media`;
   - xoa moi association lien quan story;
   - xoa story object;
   - xoa moi media object gan truc tiep vao story va association lien quan media.
6. Group story con han theo author.
7. Sort author bucket theo `latestCreate desc, authorId desc`.
8. Apply cursor theo author bucket, khong cursor theo tung story.
9. Voi moi author bucket duoc chon, tra author summary va toan bo story con han. Moi story tra theo union `NormalStory`, `FeedPostShareStory`, hoac `ReelShareStory`.

External calls: khong co.

Return: `HomeStoryPageResult`

### myStories(userId)

Input:

- `userId: long`

Logic:

1. Lay user summary cua chinh `userId`.
2. Query story type `5` do user nay tao qua `user --authored(5)--> story`.
3. Voi tung story:
   - neu `expire <= now` hoac expire invalid thi xoa story va media temporary gan truc tiep vao story;
   - neu con han thi dua vao bucket cua user.
4. Sort story con han theo `create asc`.
5. `latestCreate` la `create` lon nhat trong bucket.
6. Neu user khong ton tai hoac khong con story hop le thi tra `null`.

External calls: khong co.

Return: `HomeStoryBucketResult?`

### relationIds(id1, atype, cursor, limit)

Input:

- `id1: long`
- `atype: short`
- `cursor: string?`
- `limit: int`

Logic:

1. Goi logic `association(...)`.
2. Chi map `items[].id2` thanh list id.

External calls: khong co.

Return: `long[]`

### postCandidates(userId, limit)

Input:

- `userId: long`
- `limit: int`, clamp ve `1..500`

Logic:

1. Lay block list tu `blocked(23)` va `blocked_by(24)`.
2. Lay feed post type `2` cua friend authors: `user --friend(0)--> author`, sau do author `authored(5)` feed post moi nhat.
3. Lay feed post type `2` cua followed authors: `user --followed(1)--> author`, sau do author `authored(5)` feed post moi nhat.
4. Lay group post type `3` tu cac group user la member/admin: `member(13)`, `admin(15)`, group `published(9)` group post.
5. Lay fallback recent public feed posts: object type `2`, `privacy = 0`.
6. Khong can check `published_in(10)` de phan biet feed/group post.
7. Bo candidate cua author bi block/blocked_by.
8. Deduplicate theo post id.
9. Sort theo `id` desc va tra top limit.

External calls: khong co.

Return: `CandidateItemResult[]`

### reelCandidates(userId, limit)

Input:

- `userId: long`
- `limit: int`, clamp ve `1..500`

Logic:

1. Lay block list.
2. Lay reel cua friend authors.
3. Lay reel cua followed authors.
4. Lay fallback recent public reels.
5. Bo author bi block/blocked_by.
6. Deduplicate theo reel id.
7. Sort theo `id` desc va tra top limit.

External calls: khong co.

Return: `CandidateItemResult[]`

## 5. GraphQL Mutation APIs

### addObject(otype, dataJson)

Input:

- `otype: short`
- `dataJson: string`, JSON object hop le

Logic:

1. Validate `otype` da biet va `dataJson` la JSON object.
2. Sinh Snowflake id.
3. Insert DB `objects(id, otype, data)`.
4. Cache RedisJSON key `{id}`.

External calls: khong co.

Return: `SocialGraphObjectResult`

### updateObject(id, otype, patchJson)

Input:

- `id: long`
- `otype: short`
- `patchJson: string`, JSON object patch

Logic:

1. Loc patch theo field duoc sua trong `ObjectTypeRules`.
2. User khong sua duoc `verify`; post chi sua duoc `privacy`; media/reel/story/comment hien khong co mutable field.
3. Neu patch rong thi retrieve object neu otype khop.
4. Update DB bang JSONB merge `data = data || patch`.
5. Neu Redis co object thi patch RedisJSON tung field.
6. Retrieve lai object.

External calls: khong co.

Return: `SocialGraphObjectResult?`

### deleteObject(id)

Input:

- `id: long`

Logic:

1. Delete row trong DB `objects`.
2. Neu delete thanh cong thi delete Redis object key.

External calls: khong co.

Return: `bool`

### addAssociation(id1, atype, id2)

Input:

- `id1: long`
- `atype: short`
- `id2: long`

Logic:

1. Lay `time = UTC unix milliseconds`.
2. Upsert association goc.
3. Neu `atype` co inverse thi upsert inverse.
4. Commit transaction.
5. Update cache neu loaded; neu chua loaded thi hydrate.

External calls: khong co.

Return: `bool`

### deleteOneAssociation(id1, atype, id2)

Input:

- `id1: long`
- `atype: short`
- `id2: long`

Logic:

1. Delete association goc.
2. Neu co inverse thi delete inverse.
3. Remove member khoi Redis sorted set neu cache loaded.

External calls: khong co.

Return: `bool`

### deleteAllAssociation(id1, atype)

Input:

- `id1: long`
- `atype: short`

Logic:

1. Lay danh sach id inverse neu atype co inverse.
2. Delete tat ca association `(id1, atype, *)`.
3. Delete inverse rows neu co.
4. Delete cache key goc.
5. Remove id1 khoi cache inverse neu cache loaded.

External calls: khong co.

Return: `bool`

### createUser(input)

Input:

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

Logic:

1. Tao user object type `0`.
2. Data mac dinh: `avatar`, `background`, `name`, `bio`, `gender`, `birthdate`, `location`, `verify = ""`, `privacy = 0`, `create = now`.
3. Goi Auth internal create user voi `userId`, `email`, `password`, `displayName`, `dob`.
4. Neu Auth fail, xoa object user local va return fail payload.
5. Neu Auth thanh cong, start dong thoi Search index `PUT` va Recommendation user embedding `PUT`.
6. Hai call derived la idempotent/best-effort; failure duoc log va khong rollback user da co Auth.
7. Return payload co canonical `userId`.

External calls:

- Authentication: `POST /internal/users`, body `{ userId, email, password, displayName, dob }`
- Messenger `MessengerServiceCreateUser`: tam disable
- Search: `PUT /internal/search/indexes/{userId}`, body `{ objectType: "user", text: name }`
- Recommendation: `PUT /internal/recommendation/users/{userId}/embedding`, no body

Return: `CreateUserPayload { success, userId, message }`

### updateUser(input)

Input:

```json
{
  "input": {
    "id": 123,
    "avatar": null,
    "background": null,
    "name": "New name",
    "bio": "new bio",
    "gender": true,
    "birthdate": "2000-01-01",
    "location": "Ha Noi",
    "privacy": 0
  }
}
```

Logic:

1. Tao patch tu field non-null.
2. Patch user object type `0`.
3. `verify` khong nam trong input va khong sua qua API nay.
4. Neu co update `name` thi update search index.
5. Return profile moi.

External calls:

- Neu `name` khong rong: `PUT /internal/search/indexes/{userId}`, body `{ objectType: "user", text: name }`

Return: `UserProfileResult?`

### deleteUser(userId)

Input:

- `userId: long`

Logic:

1. Xoa tat ca association lien quan user.
2. Xoa user object.
3. Neu delete thanh cong thi goi external delete pipeline.

External calls:

- Authentication `AuthenticationServiceDeleteUser`: `{ userId }`
- Messenger `MessengerServiceDeleteUser`: tam disable
- Search: `DELETE /internal/search/indexes/{userId}`
- Recommendation: `DELETE /internal/recommendation/users/{userId}/embedding`

Return: `bool`

### changeUserAvatar(userId, avatarUrl, originalUrl)

Input:

- `userId: long`
- `avatarUrl: string`, URL anh crop de hien thi
- `originalUrl: string?`, URL anh goc neu upload anh moi

Logic:

1. Kiem tra user ton tai va dung type.
2. Neu `originalUrl` khong rong thi tao media photo object va association `user --owned(22)--> media`.
3. Patch `avatar = avatarUrl` vao user data.
4. Return profile moi.

External calls: khong co.

Return: `UserProfileResult?`

### changeUserBackground(userId, backgroundUrl, originalUrl)

Input:

- `userId: long`
- `backgroundUrl: string`, URL anh crop de hien thi
- `originalUrl: string?`, URL anh goc neu upload anh moi

Logic:

1. Kiem tra user ton tai va dung type.
2. Neu `originalUrl` khong rong thi tao media photo object va association `user --owned(22)--> media`.
3. Patch `background = backgroundUrl`.
4. Return profile moi.

External calls: khong co.

Return: `UserProfileResult?`

### sendFriendRequest(requesterId, receiverId)

Input:

- `requesterId: long`
- `receiverId: long`

Logic:

1. Khong tao pending association trong SocialGraph.
2. Goi Notification tao notification friend request.
3. Return true neu local flow khong throw.

External calls:

- Notification `NotificationServiceCreateNotification`: `{ creatorId: requesterId, receiverId, actionType: 0, objectId: requesterId, data: null }`

Return: `bool`

### acceptFriendRequest(requesterId, receiverId)

Input:

- `requesterId: long`
- `receiverId: long`

Logic:

1. Tao association `requester --friend(0)--> receiver`, inverse friend tu dong tao.
2. Goi Notification friend accept cho requester.

External calls:

- Notification `NotificationServiceCreateNotification`: `{ creatorId: receiverId, receiverId: requesterId, actionType: 1, objectId: receiverId, data: null }`

Return: `bool`

### followUser(followerId, targetUserId)

Input:

- `followerId: long`
- `targetUserId: long`

Logic:

1. Tao association `follower --followed(1)--> target`.
2. Inverse `target --followed_by(2)--> follower` duoc tao tu dong.

External calls: khong co.

Return: `bool`

### unfollowUser(followerId, targetUserId)

Input:

- `followerId: long`
- `targetUserId: long`

Logic:

1. Xoa association `followed(1)` va inverse `followed_by(2)`.

External calls: khong co.

Return: `bool`

### blockUser(blockerId, blockedUserId)

Input:

- `blockerId: long`
- `blockedUserId: long`

Logic:

1. Xoa friend giua 2 user neu co.
2. Xoa follow 2 chieu neu co.
3. Tao association `blocker --blocked(23)--> blocked`, inverse `blocked_by(24)`.

External calls: khong co.

Return: `bool`

### unblockUser(blockerId, blockedUserId)

Input:

- `blockerId: long`
- `blockedUserId: long`

Logic:

1. Xoa association `blocked(23)` va inverse `blocked_by(24)`.

External calls: khong co.

Return: `bool`

### createGroup(input)

Input:

```json
{
  "input": {
    "creatorId": 123,
    "name": "Group name",
    "bio": "Group bio",
    "privacy": 0,
    "avatar": null,
    "background": null
  }
}
```

Logic:

1. Tao group object type `1`.
2. Tao association `creator --admin(15)--> group`, inverse `have_admin(16)`.
3. Tao Search index group.
4. Return group result.

External calls:

- Search: `PUT /internal/search/indexes/{groupId}`, body `{ objectType: "group", text: name }`

Return: `GroupResult`

### updateGroup(input)

Input:

```json
{
  "input": {
    "id": 456,
    "avatar": null,
    "background": null,
    "name": "New group name",
    "bio": "new bio",
    "privacy": 0
  }
}
```

Logic:

1. Patch field group non-null.
2. Neu co `name` thi update Search index.
3. Return group moi.

External calls:

- Neu `name` khong rong: `PUT /internal/search/indexes/{groupId}`, body `{ objectType: "group", text: name }`

Return: `GroupResult?`

### deleteGroup(groupId)

Input:

- `groupId: long`

Logic:

1. Xoa tat ca association lien quan group.
2. Xoa group object.
3. Neu delete thanh cong thi xoa Search index.

External calls:

- Search: `DELETE /internal/search/indexes/{groupId}`

Return: `bool`

### changeGroupAvatar(groupId, avatarUrl)

Input:

- `groupId: long`
- `avatarUrl: string`

Logic:

1. Patch `avatar = avatarUrl` vao group data.
2. Return group moi.

External calls: khong co.

Return: `GroupResult?`

### changeGroupBackground(groupId, backgroundUrl, originalUrl)

Input:

- `groupId: long`
- `backgroundUrl: string`, URL crop hien thi
- `originalUrl: string?`, URL goc neu upload moi

Logic:

1. Kiem tra group ton tai va dung type.
2. Neu `originalUrl` khong rong thi tao media photo object va association `group --owned(22)--> media`.
3. Patch `background = backgroundUrl`.
4. Return group moi.

External calls: khong co.

Return: `GroupResult?`

### addGroupMember(groupId, userId)

Input:

- `groupId: long`
- `userId: long`

Logic:

1. Tao association `user --member(13)--> group`, inverse `have_member(14)`.

External calls: khong co.

Return: `bool`

### removeGroupMember(groupId, userId)

Input:

- `groupId: long`
- `userId: long`

Logic:

1. Xoa association `member(13)` va inverse `have_member(14)`.

External calls: khong co.

Return: `bool`

### addGroupAdmin(groupId, userId)

Input:

- `groupId: long`
- `userId: long`

Logic:

1. Xoa member association neu co.
2. Tao association `user --admin(15)--> group`, inverse `have_admin(16)`.

External calls: khong co.

Return: `bool`

### removeGroupAdmin(groupId, userId)

Input:

- `groupId: long`
- `userId: long`

Logic:

1. Xoa association `admin(15)` va inverse `have_admin(16)`.

External calls: khong co.

Return: `bool`

### createFeedPost(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "content": "Hello feed",
    "privacy": 0,
    "media": [
      {
        "type": 0,
        "url": "https://cdn.local/a.jpg"
      }
    ]
  }
}
```

Logic:

1. Tao feed post object type `2` voi `{ content, privacy, create }`.
2. Voi moi media URL:
   - tao media object type `7`;
   - tao `author --owned(22)--> media`;
   - tao `post --contained(20)--> media`.
3. Tao `author --authored(5)--> post`, inverse `authored_by(6)`.
4. Tao Search index post.
5. Tao Recommendation post embedding.
6. Return content result.

External calls:

- Search: `PUT /internal/search/indexes/{postId}`, body `{ objectType: "post", text: content }`
- Recommendation: `PUT /internal/recommendation/posts/{postId}/embedding`, body `{ content, mediaUrls }`

Return: `ContentResult`

### createGroupPost(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "groupId": 456,
    "content": "Hello group",
    "media": [
      {
        "type": 0,
        "url": "https://cdn.local/a.jpg"
      }
    ]
  }
}
```

Logic:

1. Tao group post object type `3` voi `{ content, create }`, khong co privacy rieng.
2. Attach media giong feed post.
3. Tao `author --authored(5)--> post`.
4. Tao `group --published(9)--> post`, inverse `published_in(10)`.
5. Tao Search index post.
6. Tao Recommendation post embedding.
7. Return content result, privacy effective lay tu group.

External calls:

- Search: `PUT /internal/search/indexes/{postId}`, body `{ objectType: "post", text: content }`
- Recommendation: `PUT /internal/recommendation/posts/{postId}/embedding`, body `{ content, mediaUrls }`

Return: `ContentResult`

### updatePost(input)

Input:

```json
{
  "input": {
    "id": 789,
    "privacy": 0
  }
}
```

Logic:

1. Patch object type `FeedPost(2)` voi field `privacy`.
2. Neu id la group post type `3` thi update fail do otype khong khop, return null.
3. Khong can check `published_in(10)` de phan biet loai post.
4. Return content moi.

External calls: khong co.

Return: `ContentResult?`

### deleteContent(contentId)

Input:

- `contentId: long`

Logic:

1. Retrieve object.
2. Neu khong ton tai return false.
3. Xoa tat ca association lien quan.
4. Xoa object.
5. Neu object type la post thi xoa Recommendation embedding va Search index.

External calls:

- Neu content la post: `DELETE /internal/recommendation/posts/{contentId}/embedding`
- Neu content la post: `DELETE /internal/search/indexes/{contentId}`

Return: `bool`

### createComment(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "targetId": 789,
    "content": "Comment text"
  }
}
```

Logic:

1. Tao comment object type `6`.
2. Tao `author --authored(5)--> comment`.
3. Tao `target --comment(7)--> comment`.
4. Tim author cua target qua `authored_by(6)`.
5. Neu target author ton tai va khac commenter thi tao notification comment.
6. Return content result cua comment.

External calls:

- Neu co target author khac commenter: Notification `NotificationServiceCreateNotification`: `{ creatorId: authorId, receiverId: targetAuthorId, actionType: 5, objectId: targetId, data: null }`

Return: `ContentResult`

### createNormalStory(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "content": "Story text",
    "media": {
      "type": 0,
      "url": "https://cdn.local/story.jpg"
    }
  }
}
```

Logic:

1. Tao story object type `5` voi `{ content, create, expire }`.
2. `expire = create + 1 day`.
3. Neu input co `media`, tao media object tu `{ type, url }`.
4. Attach toi da mot media bang `story --contained(20)--> media`.
5. Media tao rieng cho story la temporary media, khong tao `owned(22)`.
6. Tao `author --authored(5)--> story`.
7. Return story thuong theo type `NormalStory`.

External calls: khong co.

Return: `NormalStory`

### createShareStory(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "content": "Xem bai nay",
    "sharedSourceId": 789
  }
}
```

Logic:

1. Validate `sharedSourceId`:
   - feed post type `2` phai co `privacy = 0`;
   - reel type `4` duoc phep share;
   - group post type `3` va cac object khac bi reject.
2. Tao story object type `5` voi `{ content, create, expire }`.
3. `expire = create + 1 day`.
4. Tao `author --authored(5)--> story`.
5. Tao `story --share(8)--> sharedSource`.
6. Return story share theo union `HomeStory`:
   - `FeedPostShareStory` neu source la feed post public;
   - `ReelShareStory` neu source la reel.
7. Story share khong tao media rieng. Preview media lay tu source goc va chi lay media dau tien.

External calls: khong co.

Return: `HomeStory`

### deleteStory(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "storyId": 901
  }
}
```

Logic:

1. Retrieve `storyId`.
2. Neu object khong ton tai hoac khong phai type `5` thi tra payload `success = false`.
3. Lay author cua story qua `story --authored_by(7)--> author`.
4. Neu author khac `authorId` thi tra payload `success = false`.
5. Lay media temporary qua `story --contained(20)--> media`.
6. Xoa moi association lien quan story.
7. Xoa story object.
8. Xoa moi media temporary gan truc tiep vao story va association lien quan media.
9. Khong xoa source goc neu story la story share.

External calls: khong co.

Return:

```json
{
  "success": true,
  "message": "Story deleted."
}
```

Type: `DeleteStoryPayload`

### createReel(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "content": "Reel text",
    "media": {
      "type": 1,
      "url": "https://cdn.local/reel.mp4"
    }
  }
}
```

Logic:

1. Tao reel object type `4` voi `{ content, create }`.
2. Attach toi da mot media neu co.
3. Tao `author --authored(5)--> reel`.
4. Return content result.

External calls: khong co.

Return: `ContentResult`

### sharePost(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "sourceId": 789,
    "content": "Share text",
    "privacy": 0
  }
}
```

Logic:

1. Tao feed post moi bang logic `createFeedPost`, khong attach media.
2. Tao association `newPost --share(8)--> source`.
3. Return post moi.

External calls:

- Giong `createFeedPost`: Search create index va Recommendation create post embedding cho post moi.

Return: `ContentResult`

### like(userId, targetId)

Input:

- `userId: long`
- `targetId: long`

Logic:

1. Tao association `user --liked(3)--> target`, inverse `liked_by(4)`.
2. Tim author cua target.
3. Neu target author ton tai va khac user thi tao notification like.

External calls:

- Neu target author khac user: Notification `NotificationServiceCreateNotification`: `{ creatorId: userId, receiverId: targetAuthorId, actionType: 6, objectId: targetId, data: null }`

Return: `bool`

### unlike(userId, targetId)

Input:

- `userId: long`
- `targetId: long`

Logic:

1. Xoa association `liked(3)` va inverse `liked_by(4)`.

External calls: khong co.

Return: `bool`

### save(userId, targetId)

Input:

- `userId: long`
- `targetId: long`

Logic:

1. Tao association `user --saved(19)--> target`.

External calls: khong co.

Return: `bool`

### unsave(userId, targetId)

Input:

- `userId: long`
- `targetId: long`

Logic:

1. Xoa association `saved(19)`.

External calls: khong co.

Return: `bool`

### watch(userId, targetId)

Input:

- `userId: long`
- `targetId: long`

Logic:

1. Tao association `user --watched(17)--> target`, inverse `watched_by(18)`.

External calls: khong co.

Return: `bool`

### tag(postId, userId)

Input:

- `postId: long`
- `userId: long`

Logic:

1. Tao association `post --tagged(11)--> user`, inverse `tagged_in(12)`.
2. Tim author cua post.
3. Tao notification tag cho user duoc tag.

External calls:

- Notification `NotificationServiceCreateNotification`: `{ creatorId: postAuthorId, receiverId: userId, actionType: 8, objectId: postId, data: null }`

Return: `bool`

### mention(sourceId, userId)

Input:

- `sourceId: long`
- `userId: long`

Logic:

1. Tao association `source --mentioned(21)--> user`.
2. Tim author cua source.
3. Tao notification mention cho user duoc mention.

External calls:

- Notification `NotificationServiceCreateNotification`: `{ creatorId: sourceAuthorId, receiverId: userId, actionType: 7, objectId: sourceId, data: null }`

Return: `bool`

## 6. REST Internal APIs

Tat ca endpoint trong section nay yeu cau:

```http
X-Gateway-Secret: <shared secret at least 32 bytes>
X-Correlation-ID: <optional trace id>
```

Thieu/sai secret tra `403`. Neu server secret chua cau hinh hop le thi tra `503`. Correlation ID inbound duoc preserve; neu thieu, SocialGraph tao ID va tra lai trong response header.

### GET /internal/recommendation/post-candidates

Caller: Recommendation service.

Query:

- `userId: long`
- `limit: int = 200`, clamp ve `1..500`

Logic: giong GraphQL query `postCandidates(userId, limit)`.

External calls: khong co.

Return: `200 OK` voi `CandidateItemResult[]`

```json
[
  {
    "id": 789,
    "authorId": 123,
    "source": "friend",
    "createdAt": "2026-07-11T00:00:00.0000000Z"
  }
]
```

### GET /internal/recommendation/reel-candidates

Caller: Recommendation service.

Query:

- `userId: long`
- `limit: int = 200`, clamp ve `1..500`

Logic: giong GraphQL query `reelCandidates(userId, limit)`.

External calls: khong co.

Return: `200 OK` voi `CandidateItemResult[]`

### PUT /internal/users/{userId}/verify

Caller: Payment/Billing service.

Route:

- `userId: long`

Body cap/gia han:

```json
{
  "expiresAt": "2026-08-10T00:00:00Z"
}
```

Body clear:

```json
{
  "expiresAt": null
}
```

Logic:

1. Convert `expiresAt` sang UTC ISO string neu co.
2. Neu `expiresAt = null` thi set `verify = ""`.
3. Goi system update object de patch `user.data.verify`, bo qua mutable-field rule thong thuong.
4. Retrieve va return profile moi.
5. Neu user khong ton tai hoac khong update duoc thi return 404.

External calls: khong co.

Return:

- `200 OK` voi `UserProfileResult`
- `404 Not Found` neu user khong ton tai

## 7. API Khong Nen Dung Truc Tiep Tu Frontend

Nhung API sau nen coi la debug/internal cho service owner:

- `addObject`
- `updateObject`
- `deleteObject`
- `addAssociation`
- `deleteOneAssociation`
- `deleteAllAssociation`
- REST `/internal/recommendation/*`
- REST `/internal/users/{userId}/verify`

Frontend phai di qua Gateway GraphQL. Hien frontend chi dung duoc `createUser` cua SocialGraph; cac API business user/group/content/relation khac chua duoc public qua Gateway va khong duoc goi truc tiep tu Internet. Payment/Billing chi goi REST verify sau khi thanh toan thanh cong.
