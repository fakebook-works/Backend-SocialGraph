# SocialGraph API Theo Chuc Nang Front

File nay liet ke API SocialGraph theo flow frontend hien tai. Dang nhap khong nam trong file nay vi thuoc Authentication service.

## Ghi Chu Chung

GraphQL endpoint:

```http
POST /graphql
```

REST internal:

```http
GET /internal/recommendation/post-candidate-ids
GET /internal/recommendation/reel-candidates
PUT /internal/users/{userId}/verify
DELETE /internal/stories/expired
```

Trusted headers cho resolver/action can viewer identity:

```http
X-Gateway-Secret: <shared secret at least 32 bytes>
X-User-Id: <authenticated viewer id>
X-Correlation-ID: <trace id>
```

Frontend khong tu gui trusted headers den SocialGraph. Frontend gui token/session den Gateway; Gateway verify voi Auth roi tu gan trusted headers khi goi SocialGraph.

## Chuc Nang: Dang Ki

Dang nhap khong can SocialGraph API. Sau khi user nhap ma email/verify ben Auth thanh cong, frontend goi Gateway de dang ki profile tren SocialGraph.

### GraphQL: createUser(input)

Input:

```json
{
  "input": {
    "name": "Tran Van A",
    "gender": true,
    "birthdate": "2001-01-01",
    "location": "Ha Noi",
    "email": "a@example.com",
    "password": "secret"
  }
}
```

Logic:

1. SocialGraph tao Snowflake user id.
2. Tao object user type `0` voi avatar/background rong, bio mac dinh, privacy `0`, verify rong.
3. Goi Auth service tao identity voi dung user id vua sinh.
4. Neu Auth fail thi rollback object user va tra `success = false`.
5. Neu Auth thanh cong, goi best-effort:
   - Search upsert user index.
   - Recommendation upsert user embedding.
6. Tra payload dang ki.

Output:

```json
{
  "success": true,
  "userId": 123,
  "message": "User created."
}
```

Can API service khac:

- Auth: `POST /internal/users` body `{ userId, email, password, displayName, dob }`.
- Search: `PUT /internal/search/indexes/{userId}`.
- Recommendation: `PUT /internal/recommendation/users/{userId}/embedding`.

## Chuc Nang: Feed

Feed hien chia thanh cac phan:

- Story cua chinh user.
- Story home tu friend/follow.
- Group visited/shortcut.
- Feed post candidate va post detail.
- Mutation tao story, xoa story, tao feed post, record visited group.

### Query: myStories(userId)

Lay story cua chinh user de ghim dau UI.

Input:

```json
{
  "userId": 123
}
```

Logic:

1. Gateway gan trusted headers va SocialGraph check `userId` khop `X-User-Id`.
2. Lay story do user authored trong ngay/chua expire.
3. Filter story het han/invalid; khong xoa data trong read path.
4. Load author, media, shared source neu story share.
5. Tra mot bucket cua user hoac `null` neu khong co story hop le.

Output type: `HomeStoryBucketResult`.

Story union:

- `NormalStory`: `{ id, content, create, media }`.
- `FeedPostShareStory`: `{ id, content, create, sharedSource }`.
- `ReelShareStory`: `{ id, content, create, sharedSource }`.

### Query: homeStories(userId, limit, cursor)

Lay story home tu friend/follow theo author bucket.

Input:

```json
{
  "userId": 123,
  "limit": 10,
  "cursor": null
}
```

Logic:

1. Gateway gan trusted headers va SocialGraph check `userId` khop `X-User-Id`.
2. Lay visible author tu friend/follow, khong lay chinh user.
3. Moi author bucket gom tat ca story hop le trong ngay.
4. Cursor tinh theo bucket author, khong theo tung story.
5. Load source/author/media theo batch de giam N+1.
6. Omit story share neu source feed post da private/deleted.

Output:

```json
{
  "items": [
    {
      "author": {
        "id": 456,
        "name": "Tran Van B",
        "avatar": "https://cdn.local/b.jpg",
        "isVerified": true
      },
      "latestCreate": "2026-07-12T10:00:00.0000000Z",
      "stories": []
    }
  ],
  "endCursor": "base64-cursor",
  "hasNextPage": true
}
```

### Query: visitedGroups(userId, limit, cursor)

Lay loi tat group/group vua truy cap.

Input:

```json
{
  "userId": 123,
  "limit": 20,
  "cursor": null
}
```

Logic:

1. Gateway gan trusted headers va SocialGraph check `userId` khop `X-User-Id`.
2. Lay association `user --visited(19)--> group`.
3. Sort theo association time moi nhat truoc.
4. Paging bang cursor.
5. Tra id/avatar/name cua group.

Output:

```json
{
  "items": [
    {
      "id": 88,
      "avatar": "https://cdn.local/group.jpg",
      "name": "Public Group"
    }
  ],
  "endCursor": "base64-cursor",
  "hasNextPage": true
}
```

### REST Internal: GET /internal/recommendation/post-candidate-ids

API nay khong do frontend goi. Recommendation service goi SocialGraph de lay pool id, sau do rank rieng.

Input query:

```http
GET /internal/recommendation/post-candidate-ids?userId=123&limit=500
X-Gateway-Secret: <shared secret>
```

Logic:

1. Lay block list cua user.
2. Lay feed post cua friend authors.
3. Lay feed post cua followed authors.
4. Lay group post trong group user la member/admin.
5. Lay fallback public feed posts.
6. Lay fallback public group posts.
7. Loai author bi block.
8. Deduplicate, sort tam theo id desc, tra top limit id.

Output:

```json
[789, 788, 777]
```

### Query Resolver: postDetails(postIds)

Sau khi Recommendation rank xong, Gateway/frontend goi SocialGraph de dap data hien thi vao list id.

Day la HotChocolate top-level resolver keyed by `postIds`, khong phai Federation `_entities` key resolver. Viewer lay tu trusted `X-User-Id`.

Input:

```json
{
  "postIds": [789, 790, 777]
}
```

GraphQL:

```graphql
query PostDetails($postIds: [Long!]!) {
  postDetails(postIds: $postIds) {
    __typename
    ... on FeedPostDetail {
      id
      type
      content
      privacy
      create
      author {
        id
        name
        avatar
        isVerified
        canFollow
      }
      media {
        id
        type
        url
      }
    }
    ... on GroupPostDetail {
      id
      type
      content
      privacy
      create
      author {
        id
        name
        avatar
        isVerified
        canFollow
      }
      group {
        id
        name
        avatar
        canJoin
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

Logic:

1. Resolver validate trusted Gateway secret va doc viewer id tu `X-User-Id`.
2. Duyet `postIds` theo thu tu input, bo id trung.
3. Chi chap nhan feed post type `2` va group post type `3`.
4. Lay author, media, va group neu la group post.
5. Check quyen xem theo privacy cua feed post hoac group.
6. Omit post khong ton tai/khong co quyen xem/thieu author/group.
7. Feed post tra `FeedPostDetail`.
8. Group post tra `GroupPostDetail`.
9. `author.canFollow` tinh theo viewer voi author.
10. `group.canJoin` tinh theo viewer voi group.

Output type:

```graphql
union HomePost = FeedPostDetail | GroupPostDetail
```

### Query Resolver: postDetail(postId)

Ban single-post cua `postDetails`.

Input:

```json
{
  "postId": 789
}
```

Logic/output giong `postDetails`, nhung chi resolve mot post. Neu khong co quyen xem thi tra `null`.

### Mutation: createNormalStory(input)

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

1. Check `authorId` khop trusted `X-User-Id`.
2. Tao story type `5`, expire = create + 1 day.
3. Neu co media thi tao media type `7` va association `story --contained(20)--> media`.
4. Media story la temporary, khong tao `owned(22)`.

Output: `NormalStory`.

### Mutation: createShareStory(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "content": "Share this",
    "sharedSourceId": 789
  }
}
```

Logic:

1. Check `authorId` khop trusted `X-User-Id`.
2. Chi cho share feed post public hoac reel.
3. Tao story type `5` co data share source.
4. Tao association den shared source.
5. Output la `FeedPostShareStory` hoac `ReelShareStory`.

Output: `HomeStory` union.

### Mutation: deleteStory(input)

Input:

```json
{
  "input": {
    "authorId": 123,
    "storyId": 999
  }
}
```

Logic:

1. Check `authorId` khop trusted `X-User-Id`.
2. Story phai ton tai, type `5`, va dung author.
3. Xoa association cua story va object story trong transaction.
4. Xoa media temporary neu khong owned va khong duoc content khac reference.

Output:

```json
{
  "success": true,
  "message": null
}
```

### Mutation: createFeedPost(input)

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
        "url": "https://cdn.local/post.jpg"
      }
    ]
  }
}
```

Logic:

1. Tao feed post type `2`.
2. Tao media object cho tung URL frontend da upload xong.
3. Tao association `author --authored(5)--> post`.
4. Tao association `post --contained(20)--> media`.
5. Upsert Search index va Recommendation embedding theo best-effort.

Output: `ContentResult`.

Can API service khac:

- Search: `PUT /internal/search/indexes/{postId}`.
- Recommendation: `PUT /internal/recommendation/posts/{postId}/embedding`.

### Mutation: recordGroupVisit(userId, groupId)

Dung khi user mo group, de cap nhat shortcut group vua truy cap.

Input:

```json
{
  "userId": 123,
  "groupId": 88
}
```

Logic:

1. Check `userId` khop trusted `X-User-Id`.
2. Kiem tra group ton tai.
3. Upsert association mot chieu `user --visited(19)--> group`.
4. Neu association da ton tai thi cap nhat `time` de group len dau list visited.

Output:

```json
true
```

## API Hien Co Nhung Chua Dua Vao Flow Front Tam Thoi

GraphQL query raw/core:

- `object(id)`
- `association(id1, atype, cursor, limit)`
- `associationCount(id1, atype)`
- `profile(userId)`
- `group(groupId)`
- `content(contentId)`
- `relationIds(id1, atype, cursor, limit)`
- `reelCandidates(userId, limit)`

GraphQL mutation raw/core va cac domain khac:

- `addObject`, `updateObject`, `deleteObject`
- `addAssociation`, `deleteOneAssociation`, `deleteAllAssociation`
- `updateUser`, `deleteUser`, `changeUserAvatar`, `changeUserBackground`
- `sendFriendRequest`, `acceptFriendRequest`, `followUser`, `unfollowUser`, `blockUser`, `unblockUser`
- `createGroup`, `updateGroup`, `deleteGroup`, `changeGroupAvatar`, `changeGroupBackground`
- `addGroupMember`, `removeGroupMember`, `addGroupAdmin`, `removeGroupAdmin`
- `createGroupPost`, `updatePost`, `deleteContent`
- `createComment`, `createReel`, `sharePost`
- `like`, `unlike`, `save`, `unsave`, `watch`, `tag`, `mention`

REST internal khac:

- `GET /internal/recommendation/reel-candidates`
- `PUT /internal/users/{userId}/verify`
- `DELETE /internal/stories/expired`
