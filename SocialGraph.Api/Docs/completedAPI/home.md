# Completed API - Home

File nay ghi lai cac API da hoan thien cho man hinh Home/Feed.

## Tong Quan Home

Home hien chia thanh cac phan:

1. Story cua chinh user: `myStories(userId)`.
2. Story home cua friend/follow: `homeStories(userId, limit, cursor)`.
3. Loi tat group/group vua truy cap: `visitedGroups(userId, limit, cursor)` va `recordGroupVisit(userId, groupId)`.
4. Feed post:
   - Recommendation service goi REST `post-candidate-ids`.
   - Recommendation rank list id.
   - Fusion dung internal lookup SocialGraph de batch-hydrate `RecommendationItem.post`.
   - Frontend nhan ranked ID va post detail trong cung `recommendFeed` response.

## Trusted Viewer

Story, visited group va post detail can viewer identity.

Gateway phai goi SocialGraph kem:

```http
X-Gateway-Secret: <shared secret at least 32 bytes>
X-User-Id: 123
X-Correlation-ID: <trace id>
```

Frontend khong tu gui trusted headers den SocialGraph. Gateway lay viewer tu token/session Auth.

## Type Dung Chung Cho Story

```graphql
union HomeStory = NormalStory | FeedPostShareStory | ReelShareStory
```

Normal story:

```graphql
type NormalStory {
  id: Long!
  content: String!
  create: String!
  media: [MediaResult!]!
}
```

Feed post share story:

```graphql
type FeedPostShareStory {
  id: Long!
  content: String!
  create: String!
  sharedSource: FeedPostSharedSource!
}
```

Reel share story:

```graphql
type ReelShareStory {
  id: Long!
  content: String!
  create: String!
  sharedSource: ReelSharedSource!
}
```

## API: myStories(userId)

Lay story cua chinh viewer de ghim dau UI.

GraphQL:

```graphql
query MyStories($userId: Long!) {
  myStories(userId: $userId) {
    author {
      id
      name
      avatar
      isVerified
    }
    latestCreate
    stories {
      __typename
      ... on NormalStory {
        id
        content
        create
        media { id type url }
      }
      ... on FeedPostShareStory {
        id
        content
        create
        sharedSource {
          id
          content
          media { id type url }
          author { id name avatar isVerified }
        }
      }
      ... on ReelShareStory {
        id
        content
        create
        sharedSource {
          id
          content
          media { id type url }
          author { id name avatar isVerified }
        }
      }
    }
  }
}
```

Input:

```json
{
  "userId": 123
}
```

Logic:

1. Validate trusted header va check `userId` khop `X-User-Id`.
2. Lay story do user authored.
3. Chi giu story chua expire.
4. Neu story share feed post, source phai con public.
5. Load author/media/shared source.
6. Tra bucket cua user hoac `null`.

Output: `HomeStoryBucketResult?`.

## API: homeStories(userId, limit, cursor)

Lay story cua friend/follow theo bucket author.

GraphQL:

```graphql
query HomeStories($userId: Long!, $limit: Int!, $cursor: String) {
  homeStories(userId: $userId, limit: $limit, cursor: $cursor) {
    items {
      author { id name avatar isVerified }
      latestCreate
      stories {
        __typename
        ... on NormalStory {
          id
          content
          create
          media { id type url }
        }
        ... on FeedPostShareStory {
          id
          content
          create
          sharedSource {
            id
            content
            media { id type url }
            author { id name avatar isVerified }
          }
        }
        ... on ReelShareStory {
          id
          content
          create
          sharedSource {
            id
            content
            media { id type url }
            author { id name avatar isVerified }
          }
        }
      }
    }
    endCursor
    hasNextPage
  }
}
```

Input:

```json
{
  "userId": 123,
  "limit": 10,
  "cursor": null
}
```

Logic:

1. Validate trusted header va check `userId` khop `X-User-Id`.
2. Lay author id tu `friend(0)` va `followed(1)`.
3. Bo chinh user ra khoi home story.
4. Lay story chua expire theo author bucket.
5. Cursor tinh theo bucket author, khong theo tung story.
6. Moi bucket tra tat ca story hop le cua author do.
7. Story share feed post bi omit neu source khong con public.
8. Query nay chi filter, khong xoa story het han.

Output:

```json
{
  "items": [],
  "endCursor": null,
  "hasNextPage": false
}
```

## API: visitedGroups(userId, limit, cursor)

Lay group vua truy cap/shortcut.

GraphQL:

```graphql
query VisitedGroups($userId: Long!, $limit: Int!, $cursor: String) {
  visitedGroups(userId: $userId, limit: $limit, cursor: $cursor) {
    items {
      id
      avatar
      name
    }
    endCursor
    hasNextPage
  }
}
```

Input:

```json
{
  "userId": 123,
  "limit": 20,
  "cursor": null
}
```

Logic:

1. Validate trusted header va check `userId` khop `X-User-Id`.
2. Lay association mot chieu `user --visited(25)--> group`.
3. Sort `time DESC, groupId DESC` de tie-break on dinh.
4. Paging keyset bang opaque Base64 cursor; frontend truyen lai `endCursor` nguyen ven.
5. An private group neu viewer khong con la member/admin.
6. Tra id/avatar/name cua group.

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
  "endCursor": null,
  "hasNextPage": false
}
```

## API: recordGroupVisit(userId, groupId)

Cap nhat group vua truy cap moi khi viewer mo group.

GraphQL:

```graphql
mutation RecordGroupVisit($userId: Long!, $groupId: Long!) {
  recordGroupVisit(userId: $userId, groupId: $groupId)
}
```

Input:

```json
{
  "userId": 123,
  "groupId": 88
}
```

Logic:

1. Validate trusted header va check `userId` khop `X-User-Id`.
2. Check group ton tai.
3. Upsert association `visited(25)`.
4. Neu da visited truoc do thi update `time`.

Output:

```json
true
```

## API REST: GET /internal/recommendation/post-candidate-ids

Endpoint nay danh cho Recommendation service, khong phai frontend.

Request:

```http
GET /internal/recommendation/post-candidate-ids?userId=123&limit=500
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <trace id>
```

Logic:

1. Clamp limit `1..500`.
2. Lay block list cua user.
3. Lay feed post cua friend authors.
4. Lay feed post cua followed authors.
5. Lay group post trong group user member/admin.
6. Lay public feed post fallback.
7. Lay public group post fallback.
8. Loai author bi block.
9. Deduplicate theo post id.
10. Sort tam thoi theo id desc va tra top limit id.

Output:

```json
[789, 788, 777]
```

## Cong Nghe Resolver: postDetails/postDetail

`postDetails` va `postDetail` la HotChocolate top-level resolver keyed by post id, khong phai Federation `_entities` key resolver.

Input key:

- `postDetails`: list `postIds`.
- `postDetail`: mot `postId`.

Viewer:

- Lay tu trusted `X-User-Id`.
- Khong nam trong GraphQL variables.

## API GraphQL: postDetails(postIds)

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
      media { id type url }
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
      media { id type url }
    }
  }
}
```

Input:

```json
{
  "postIds": [789, 790, 777]
}
```

Logic:

1. Validate trusted Gateway secret va lay viewer id.
2. Reject hon 100 IDs voi GraphQL code `BAD_USER_INPUT`.
3. Giu thu tu ranked input va bo ID trung/khong hop le.
4. Batch-load post, association, author, media, group va viewer relations.
5. Chi chap nhan feed post type `2` hoac group post type `3`.
6. Feed post check privacy cua post; group post check privacy cua group.
7. Loai author bi viewer block hoac da block viewer.
8. Omit post invalid, deleted, thieu author/group, hoac khong co quyen xem.
9. Feed post tra `FeedPostDetail`; group post tra `GroupPostDetail` kem group.

Output type:

```graphql
union HomePost = FeedPostDetail | GroupPostDetail
```

Example output:

```json
[
  {
    "__typename": "FeedPostDetail",
    "id": 789,
    "type": 2,
    "content": "Hello feed",
    "privacy": 0,
    "create": "2026-07-12T10:00:00.0000000Z",
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": true,
      "canFollow": false
    },
    "media": [
      {
        "id": 3001,
        "type": 0,
        "url": "https://cdn.local/post.jpg"
      }
    ]
  },
  {
    "__typename": "GroupPostDetail",
    "id": 790,
    "type": 3,
    "content": "Hello group",
    "privacy": 0,
    "create": "2026-07-12T10:05:00.0000000Z",
    "author": {
      "id": 457,
      "name": "Le Van C",
      "avatar": "https://cdn.local/c.jpg",
      "isVerified": false,
      "canFollow": false
    },
    "group": {
      "id": 88,
      "name": "Public Group",
      "avatar": "https://cdn.local/g.jpg",
      "canJoin": true
    },
    "media": []
  }
]
```

## API GraphQL: postDetail(postId)

Ban single-post cua `postDetails`.

GraphQL:

```graphql
query PostDetail($postId: Long!) {
  postDetail(postId: $postId) {
    __typename
    ... on FeedPostDetail {
      id
      type
      content
      privacy
      create
      author { id name avatar isVerified canFollow }
      media { id type url }
    }
    ... on GroupPostDetail {
      id
      type
      content
      privacy
      create
      author { id name avatar isVerified canFollow }
      group { id name avatar canJoin }
      media { id type url }
    }
  }
}
```

Input:

```json
{
  "postId": 789
}
```

Return: `HomePost` hoac `null`.

## Flow Tong Hop Home Feed Post

```json
{
  "candidateStep": {
    "caller": "Recommendation service",
    "requestToSocialGraph": "GET /internal/recommendation/post-candidate-ids?userId=123&limit=500",
    "responseFromSocialGraph": [789, 788, 777]
  },
  "rankingStep": {
    "caller": "Recommendation service",
    "logic": "rank/sort candidate ids bang model rieng",
    "responseToGateway": [789, 790, 777]
  },
  "detailStep": {
    "caller": "Fusion Gateway",
    "headers": {
      "X-Gateway-Secret": "<shared secret>",
      "X-User-Id": "123"
    },
    "internalLookup": "recommendationItem(postId)",
    "socialGraphBatch": "postDetails([789, 790, 777])"
  }
}
```

## API Gateway: recommendFeed Hydrated

Frontend nen dung operation nay de Recommendation rank va SocialGraph hydrate trong mot Gateway request:

```graphql
query RecommendedFeed($userId: ID!, $skip: Int! = 0, $take: Int! = 20) {
  recommendFeed(userId: $userId, skip: $skip, take: $take) {
    postId
    post {
      __typename
      ... on FeedPostDetail {
        id type content privacy create
        author { id name avatar isVerified canFollow }
        media { id type url }
      }
      ... on GroupPostDetail {
        id type content privacy create
        author { id name avatar isVerified canFollow }
        group { id name avatar canJoin }
        media { id type url }
      }
    }
  }
}
```

Variables:

```json
{
  "userId": "123",
  "skip": 0,
  "take": 20
}
```

Flow Fusion:

1. Gateway goi Recommendation `recommendFeed`, nhan `RecommendationItem.postId` theo thu tu rank.
2. Gateway dung internal lookup `recommendationItem(postId)` cua SocialGraph.
3. Fusion gui variable-batched lookup; SocialGraph endpoint cho phep toi da 100 batch entries va DataLoader gom database reads qua `postDetails`.
4. Gateway merge `post` vao tung item va giu thu tu Recommendation.
5. `post = null` la hop le neu post vua bi xoa, bi block, hoac privacy vua thay doi; frontend bo item do.
