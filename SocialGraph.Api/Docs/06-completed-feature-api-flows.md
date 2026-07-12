# 06 - Completed Feature API Flows

File nay gom cac chuc nang da hoan thien tam thoi de frontend/gateway co the goi dung API. Moi API deu di qua GraphQL endpoint cua SocialGraph:

```http
POST /graphql
Content-Type: application/json
```

Body frontend gui theo form:

```json
{
  "operationName": "OperationName",
  "query": "query or mutation string",
  "variables": {}
}
```

Field name trong GraphQL response la camelCase. ID trong schema la `Long`; frontend co the gui JSON number neu ID con trong safe integer, hoac de gateway chuan hoa neu can.

## Tong quan nhom chuc nang

File hien dang chia theo cac nhom lon:

- `Dang ki`: flow tao user sau khi Authentication xac thuc email/OTP xong.
- `Feed`: cac API phuc vu man hinh feed hien tai, gom story, loi tat group, feed post, them story va xoa story.

Trang thai hien tai: tam thoi phan feed da chot den muc story + group shortcut + feed post detail/candidate. Cac phan con lai nhu comment tree, like list, share list, saved list se bo sung sau khi chot flow.

## Ghi chu chung

### Type dung chung cho story

Story trong feed tra ve theo union `HomeStory`:

```graphql
union HomeStory = NormalStory | FeedPostShareStory | ReelShareStory
```

Concrete type:

- `NormalStory`: story thuong, co `media` la list.
- `FeedPostShareStory`: story share feed post public.
- `ReelShareStory`: story share reel.

Story share group post hien khong duoc ho tro. Neu source la group post hoac object khac, `createShareStory` se reject.

Fragment nen dung chung o frontend:

```graphql
fragment UserSummaryFields on UserSummaryResult {
  id
  name
  avatar
  isVerified
}

fragment MediaFields on MediaResult {
  id
  type
  url
}

fragment HomeStoryFields on HomeStory {
  __typename

  ... on NormalStory {
    id
    content
    create
    media {
      ...MediaFields
    }
  }

  ... on FeedPostShareStory {
    id
    content
    create
    sharedSource {
      id
      content
      media {
        ...MediaFields
      }
      author {
        ...UserSummaryFields
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
      media {
        ...MediaFields
      }
      author {
        ...UserSummaryFields
      }
    }
  }
}

fragment StoryBucketFields on HomeStoryBucketResult {
  author {
    ...UserSummaryFields
  }
  latestCreate
  stories {
    ...HomeStoryFields
  }
}
```

`FeedPostSharedSource.media` va `ReelSharedSource.media` la object nullable, khong phai list. Service chi lay media dau tien cua source goc de lam preview.

Media type convention:

- `0`: photo
- `1`: video
- `2`: audio
- `3`: file
- `4`: link

SocialGraph khong tao upload URL va khong nhan binary file. Frontend/media service upload file truoc, sau do truyen URL da co vao SocialGraph.

## Nhom chuc nang: Dang ki

### Chuc nang: Dang ki

Frontend flow:

1. Frontend goi Authentication service de tao ma xac thuc email.
2. User nhap ma, Authentication service xac nhan ma.
3. Sau khi xac nhan thanh cong, frontend goi SocialGraph `createUser`.
4. SocialGraph tao user graph object va goi Authentication internal create user bang `userId` vua sinh.

#### API: createUser(input)

GraphQL mutation:

```graphql
mutation RegisterUser($input: CreateUserInput!) {
  createUser(input: $input) {
    success
    userId
    message
  }
}
```

Input:

```json
{
  "name": "Nguyen Van A",
  "gender": true,
  "birthdate": "2000-01-01",
  "location": "Ha Noi",
  "email": "a@example.com",
  "password": "secret123"
}
```

Field:

- `name`: ten hien thi ban dau cua user.
- `gender`: `true` luu thanh `1`, `false` luu thanh `0`.
- `birthdate`: ngay sinh dang string, frontend/gateway nen thong nhat format `yyyy-MM-dd`.
- `location`: dia diem/hometown ban dau.
- `email`: email da xac thuc OTP ben Authentication.
- `password`: password de SocialGraph goi Authentication internal create user.

Logic ben trong:

1. Tao object type `0` user trong SocialGraph.
2. User data mac dinh:
   - `avatar = ""`
   - `background = ""`
   - `name = input.name`
   - `bio = "Xin chao, minh la {name} den tu {location}"`
   - `gender = 1/0`
   - `birthdate = input.birthdate`
   - `location = input.location`
   - `verify = ""`
   - `privacy = 0`
   - `create = now UTC`
3. Goi Authentication required endpoint `AuthenticationServiceCreateUser` voi payload internal:

```json
{
  "userId": 123,
  "email": "a@example.com",
  "password": "secret123",
  "displayName": "Nguyen Van A",
  "dob": "2000-01-01"
}
```

4. Neu Authentication fail hoac chua config endpoint required, SocialGraph xoa user object vua tao va tra `success = false`.
5. Neu Authentication thanh cong, SocialGraph goi best-effort:
   - Search `SearchServiceCreateIndex`: `{ objectId: userId, objectType: "user", text: name }`
   - Recommendation `RecommendServiceCreateUserEmbedding`: `{ userId }`
6. Messenger create user dang tam comment trong code, chua goi.

Output:

```json
{
  "success": true,
  "userId": 123,
  "message": "User created."
}
```

Neu Authentication fail:

```json
{
  "success": false,
  "userId": null,
  "message": "Authentication user creation failed."
}
```

Goi tin frontend gui:

```json
{
  "operationName": "RegisterUser",
  "query": "mutation RegisterUser($input: CreateUserInput!) { createUser(input: $input) { success userId message } }",
  "variables": {
    "input": {
      "name": "Nguyen Van A",
      "gender": true,
      "birthdate": "2000-01-01",
      "location": "Ha Noi",
      "email": "a@example.com",
      "password": "secret123"
    }
  }
}
```

Goi tin de thuc hien chuc nang dang ki:

```json
{
  "step": "after-auth-email-code-confirmed",
  "service": "SocialGraph",
  "http": {
    "method": "POST",
    "path": "/graphql",
    "body": {
      "operationName": "RegisterUser",
      "query": "mutation RegisterUser($input: CreateUserInput!) { createUser(input: $input) { success userId message } }",
      "variables": {
        "input": {
          "name": "Nguyen Van A",
          "gender": true,
          "birthdate": "2000-01-01",
          "location": "Ha Noi",
          "email": "a@example.com",
          "password": "secret123"
        }
      }
    }
  }
}
```

## Nhom chuc nang: Feed

Muc feed gom cac phan da hoan thien:

- Story cua chinh user va story cua friend/follow.
- Loi tat group / group vua truy cap.
- Feed post candidate pipe qua Recommendation.
- Feed post detail theo list id da duoc Recommendation rank.
- Them story va xoa story cua chinh user.

### Chuc nang: Lay feed story

Phan story tren home feed nen tach 2 nhom:

- Story cua chinh user: goi `myStories`, frontend ghim dau danh sach de user xem/xoa nhanh.
- Story cua friend/follow: goi `homeStories`, co paging theo author bucket.

#### API: myStories(userId)

GraphQL query:

```graphql
query GetMyStories($userId: Long!) {
  myStories(userId: $userId) {
    ...StoryBucketFields
  }
}
```

Input:

- `userId`: ID cua user dang dang nhap.

Logic ben trong:

1. Retrieve user summary cua `userId`.
2. Query cac object type `5` story do user nay tao qua association `user --authored(5)--> story`.
3. Voi tung story:
   - neu `expire <= now` hoac parse expire fail thi xoa story;
   - khi xoa, xoa ca media temporary gan truc tiep vao story qua `story --contained(20)--> media`;
   - neu con han thi giu lai.
4. Sort story con han theo `create asc`.
5. Build `HomeStoryBucketResult`.
6. Neu user khong ton tai hoac user khong con story hop le, tra `null`.

External calls: khong co.

Output:

```json
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
```

Goi tin frontend gui:

```json
{
  "operationName": "GetMyStories",
  "query": "query GetMyStories($userId: Long!) { myStories(userId: $userId) { author { id name avatar isVerified } latestCreate stories { __typename ... on NormalStory { id content create media { id type url } } ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } } } }",
  "variables": {
    "userId": 123
  }
}
```

#### API: homeStories(userId, limit, cursor)

GraphQL query:

```graphql
query GetHomeStories($userId: Long!, $limit: Int!, $cursor: String) {
  homeStories(userId: $userId, limit: $limit, cursor: $cursor) {
    items {
      ...StoryBucketFields
    }
    endCursor
    hasNextPage
  }
}
```

Input:

- `userId`: ID cua user dang dang nhap.
- `limit`: so author bucket can lay, service clamp ve `1..50`.
- `cursor`: `null` cho page dau; page sau dung `endCursor` cua response page truoc.

Logic ben trong:

1. Lay danh sach author co the xem story tu:
   - `user --friend(0)--> friend`
   - `user --followed(1)--> followedUser`
2. Loai chinh `userId` khoi danh sach author. Story cua chinh user lay bang `myStories`.
3. Query story type `5` cua cac author do qua `author --authored(5)--> story`.
4. Voi tung story:
   - neu het han hoac expire invalid thi xoa story va media temporary cua story;
   - neu con han thi dua vao bucket cua author.
5. Group story theo author.
6. Sort author bucket theo `latestCreate desc`, sau do `authorId desc`.
7. Apply cursor theo author bucket, khong cursor theo tung story.
8. Voi moi bucket duoc chon, build author summary va toan bo story con han cua author do.

External calls: khong co.

Output:

```json
{
  "items": [
    {
      "author": {
        "id": 456,
        "name": "Tran Van B",
        "avatar": "https://cdn.local/b.jpg",
        "isVerified": false
      },
      "latestCreate": "2026-07-12T10:00:00.0000000Z",
      "stories": [
        {
          "__typename": "FeedPostShareStory",
          "id": 902,
          "content": "Xem bai nay",
          "create": "2026-07-12T10:00:00.0000000Z",
          "sharedSource": {
            "id": 789,
            "content": "Feed post public",
            "media": {
              "id": 3101,
              "type": 0,
              "url": "https://cdn.local/feed-preview.jpg"
            },
            "author": {
              "id": 7890,
              "name": "Le Van C",
              "avatar": "https://cdn.local/c.jpg",
              "isVerified": true
            }
          }
        }
      ]
    }
  ],
  "endCursor": "base64-json",
  "hasNextPage": true
}
```

Goi tin frontend gui:

```json
{
  "operationName": "GetHomeStories",
  "query": "query GetHomeStories($userId: Long!, $limit: Int!, $cursor: String) { homeStories(userId: $userId, limit: $limit, cursor: $cursor) { items { author { id name avatar isVerified } latestCreate stories { __typename ... on NormalStory { id content create media { id type url } } ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } } } endCursor hasNextPage } }",
  "variables": {
    "userId": 123,
    "limit": 20,
    "cursor": null
  }
}
```

Goi tin de thuc hien chuc nang lay feed story lan dau:

```json
{
  "operationName": "LoadFeedStories",
  "query": "query LoadFeedStories($userId: Long!, $limit: Int!, $cursor: String) { myStories(userId: $userId) { author { id name avatar isVerified } latestCreate stories { __typename ... on NormalStory { id content create media { id type url } } ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } } } homeStories(userId: $userId, limit: $limit, cursor: $cursor) { items { author { id name avatar isVerified } latestCreate stories { __typename ... on NormalStory { id content create media { id type url } } ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } } } endCursor hasNextPage } }",
  "variables": {
    "userId": 123,
    "limit": 20,
    "cursor": null
  }
}
```

Frontend render:

1. Neu `myStories != null`, render bucket nay o dau danh sach.
2. Render tiep `homeStories.items`.
3. Khi can load more story nguoi khac, goi lai `homeStories` voi `cursor = endCursor`; khong bat buoc goi lai `myStories`.

### Chuc nang: Lay loi tat group / group vua truy cap

Chuc nang nay dung association mot chieu:

```text
user --visited(25)--> group
```

Association `visited(25)` khong co inverse. Moi lan user vao group, frontend/gateway goi `recordGroupVisit`; service se upsert association va cap nhat `time`. Khi lay loi tat, service doc association theo `time desc`, nen group vua truy cap gan nhat nam dau list.

#### API: visitedGroups(userId, limit, cursor)

GraphQL query:

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

- `userId`: ID cua user dang dang nhap.
- `limit`: so group can lay, service clamp theo `RetrieveAssociationAsync`, toi da `100`.
- `cursor`: `null` cho page dau; page sau dung `endCursor` cua response page truoc.

Logic ben trong:

1. Gateway phai forward trusted header; service check `userId` request khop `X-User-Id`.
2. Doc association `user --visited(25)--> group` bang `RetrieveAssociationAsync`.
3. Association service tra item theo `time desc`, nen group moi truy cap nhat dung truoc.
4. Voi tung `groupId`, retrieve object group type `1`.
5. Bo qua group da bi xoa hoac object khong phai group.
6. Neu group public `privacy = 0`, cho hien thi.
7. Neu group private, chi hien thi khi user la `member(13)` hoac `admin(15)` cua group.
8. Tra moi group voi 3 field toi thieu: `id`, `avatar`, `name`.
9. Tra `endCursor` va `hasNextPage` theo cursor cua association page.

External calls: khong co.

Output:

```json
{
  "items": [
    {
      "id": 456,
      "avatar": "https://cdn.local/group-avatar.jpg",
      "name": "Group name"
    }
  ],
  "endCursor": "20",
  "hasNextPage": true
}
```

Goi tin frontend gui:

```json
{
  "operationName": "VisitedGroups",
  "query": "query VisitedGroups($userId: Long!, $limit: Int!, $cursor: String) { visitedGroups(userId: $userId, limit: $limit, cursor: $cursor) { items { id avatar name } endCursor hasNextPage } }",
  "variables": {
    "userId": 123,
    "limit": 20,
    "cursor": null
  }
}
```

#### API: recordGroupVisit(userId, groupId)

GraphQL mutation:

```graphql
mutation RecordGroupVisit($userId: Long!, $groupId: Long!) {
  recordGroupVisit(userId: $userId, groupId: $groupId)
}
```

Input:

- `userId`: ID cua user dang dang nhap.
- `groupId`: ID group user vua truy cap.

Logic ben trong:

1. Gateway phai forward trusted header; service check `userId` request khop `X-User-Id`.
2. Retrieve `groupId`.
3. Neu group khong ton tai hoac object khong phai type `1`, return `false`.
4. Neu group public `privacy = 0`, cho ghi visited.
5. Neu group private, chi cho ghi khi user la `member(13)` hoac `admin(15)`.
6. Goi `AddAssociationAsync(userId, Visited(25), groupId)`.
7. `AddAssociationAsync` dang upsert DB bang `ON CONFLICT DO UPDATE SET time = EXCLUDED.time`, nen neu association da ton tai thi chi cap nhat `time` moi.
8. Vi `visited(25)` mot chieu va khong co inverse trong dictionary, service khong tao association nguoc.

External calls: khong co.

Output:

```json
true
```

Goi tin frontend gui:

```json
{
  "operationName": "RecordGroupVisit",
  "query": "mutation RecordGroupVisit($userId: Long!, $groupId: Long!) { recordGroupVisit(userId: $userId, groupId: $groupId) }",
  "variables": {
    "userId": 123,
    "groupId": 456
  }
}
```

Goi tin de thuc hien chuc nang group vua truy cap:

```json
{
  "whenOpenGroup": {
    "operationName": "RecordGroupVisit",
    "query": "mutation RecordGroupVisit($userId: Long!, $groupId: Long!) { recordGroupVisit(userId: $userId, groupId: $groupId) }",
    "variables": {
      "userId": 123,
      "groupId": 456
    }
  },
  "whenRenderShortcutList": {
    "operationName": "VisitedGroups",
    "query": "query VisitedGroups($userId: Long!, $limit: Int!, $cursor: String) { visitedGroups(userId: $userId, limit: $limit, cursor: $cursor) { items { id avatar name } endCursor hasNextPage } }",
    "variables": {
      "userId": 123,
      "limit": 20,
      "cursor": null
    }
  }
}
```

Frontend khong gui ca object wrapper `whenOpenGroup/whenRenderShortcutList`; day chi la mo ta 2 thoi diem goi API. Khi user scroll/load more loi tat group, goi lai `visitedGroups` voi `cursor = endCursor`.

### Chuc nang: Lay feed post

Feed post di theo pipeline 3 buoc:

1. Recommendation service goi SocialGraph REST de lay candidate post id.
2. Recommendation service tu rank/sort bang model rieng va tra list post id da sap xep ve Gateway/frontend.
3. Gateway/frontend goi SocialGraph GraphQL `postDetails` de lay data hien thi cua cac post do.

Ly do tach nhu vay: Recommendation chi can id de rank, con SocialGraph la noi resolve graph data nhu author, group, media va quan he cua viewer voi author/group.

#### API REST: GET /internal/recommendation/post-candidate-ids

Endpoint nay danh cho Recommendation service.

Input query:

- `userId: long`
- `limit: int`, service clamp ve `1..500`

Logic ben trong:

1. Lay block list cua user tu `blocked(23)` va `blocked_by(24)`.
2. Lay feed post cua friend authors:
   - `user --friend(0)--> author`
   - `author --authored(5)--> feedPost`
3. Lay feed post cua followed authors:
   - `user --followed(1)--> author`
   - `author --authored(5)--> feedPost`
4. Lay group post trong cac group user dang tham gia/quan tri:
   - `user --member(13)--> group`
   - `user --admin(15)--> group`
   - `group --published(9)--> groupPost`
5. Lay public feed post fallback:
   - object type `2`
   - `privacy = 0`
6. Lay public group post fallback:
   - object type `3`
   - group chua post co `privacy = 0`
7. Bo candidate co author nam trong block list.
8. Deduplicate theo post id.
9. Sort tam thoi theo `id desc`, vi Snowflake id lon hon gan voi bai moi hon.
10. Tra top `limit` post id.

External calls: khong co.

Output:

```json
[789, 788, 777]
```

Goi tin Recommendation service gui:

```http
GET /internal/recommendation/post-candidate-ids?userId=123&limit=500
```

#### API GraphQL: postDetails(userId, postIds)

API nay danh cho Gateway/frontend sau khi da co list id tu Recommendation.

GraphQL query:

```graphql
query PostDetails($userId: Long!, $postIds: [Long!]!) {
  postDetails(userId: $userId, postIds: $postIds) {
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
    }
    group {
      id
      name
      avatar
    }
    viewerRelation {
      isFriend
      isFollow
      isParticipant
    }
    media {
      id
      type
      url
    }
  }
}
```

Input:

- `userId`: ID cua viewer dang dang nhap.
- `postIds`: list id do Recommendation tra ve, da sap xep theo ranking.

Logic ben trong:

1. Gateway phai forward trusted header; service check `userId` request khop `X-User-Id`.
2. Duyet `postIds` theo dung thu tu input, bo id trung.
3. Retrieve object post.
4. Chi chap nhan:
   - feed post type `2`
   - group post type `3`
5. Lay author qua `post --authored_by(6)--> user`.
6. Lay media qua `post --contained(20)--> media`.
7. Neu la group post, lay group qua `post --published_in(10)--> group`.
8. Kiem tra quyen xem:
   - feed post `privacy = 0`: xem duoc;
   - feed post private: viewer phai la author hoac friend cua author;
   - group post public: group `privacy = 0` xem duoc;
   - group post private: viewer phai la member/admin cua group.
9. Post khong ton tai, sai type, thieu author, thieu group, hoac viewer khong co quyen xem se bi omit khoi output.
10. Build `viewerRelation`:
   - voi author co `privacy = 1`: set `isFollow` theo `viewer --followed(1)--> author`, `isFriend = null`;
   - voi author co `privacy = 0`: set `isFriend` theo `viewer --friend(0)--> author`, `isFollow = null`;
   - voi group post: set `isParticipant` theo viewer la `member(13)` hoac `admin(15)` cua group;
   - voi feed post: `isParticipant = null`.

External calls: khong co.

Output:

```json
[
  {
    "id": 789,
    "type": 2,
    "content": "Hello feed",
    "privacy": 0,
    "create": "2026-07-12T10:00:00.0000000Z",
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": true
    },
    "group": null,
    "viewerRelation": {
      "isFriend": true,
      "isFollow": null,
      "isParticipant": null
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
    "id": 790,
    "type": 3,
    "content": "Hello group",
    "privacy": 0,
    "create": "2026-07-12T10:05:00.0000000Z",
    "author": {
      "id": 457,
      "name": "Le Van C",
      "avatar": "https://cdn.local/c.jpg",
      "isVerified": false
    },
    "group": {
      "id": 88,
      "name": "Public Group",
      "avatar": "https://cdn.local/g.jpg"
    },
    "viewerRelation": {
      "isFriend": null,
      "isFollow": false,
      "isParticipant": false
    },
    "media": []
  }
]
```

Goi tin frontend/gateway gui:

```json
{
  "operationName": "PostDetails",
  "query": "query PostDetails($userId: Long!, $postIds: [Long!]!) { postDetails(userId: $userId, postIds: $postIds) { id type content privacy create author { id name avatar isVerified } group { id name avatar } viewerRelation { isFriend isFollow isParticipant } media { id type url } } }",
  "variables": {
    "userId": 123,
    "postIds": [789, 790, 777]
  }
}
```

#### API GraphQL: postDetail(userId, postId)

Ban single-post cua `postDetails`, dung khi frontend can refresh/moi mot bai.

GraphQL query:

```graphql
query PostDetail($userId: Long!, $postId: Long!) {
  postDetail(userId: $userId, postId: $postId) {
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
    }
    group {
      id
      name
      avatar
    }
    viewerRelation {
      isFriend
      isFollow
      isParticipant
    }
    media {
      id
      type
      url
    }
  }
}
```

Return: `PostDetailResult?`. Neu post khong ton tai hoac viewer khong co quyen xem thi tra `null`.

Goi tin de thuc hien chuc nang feed post:

```json
{
  "recommendationStep": {
    "service": "Recommendation",
    "internalCallToSocialGraph": "GET /internal/recommendation/post-candidate-ids?userId=123&limit=500",
    "returnToGateway": [789, 790, 777]
  },
  "socialGraphDetailStep": {
    "operationName": "PostDetails",
    "query": "query PostDetails($userId: Long!, $postIds: [Long!]!) { postDetails(userId: $userId, postIds: $postIds) { id type content privacy create author { id name avatar isVerified } group { id name avatar } viewerRelation { isFriend isFollow isParticipant } media { id type url } } }",
    "variables": {
      "userId": 123,
      "postIds": [789, 790, 777]
    }
  }
}
```

Frontend/gateway khong gui ca object wrapper `recommendationStep/socialGraphDetailStep`; day chi la mo ta pipe. Thuc te Recommendation service goi REST truoc, sau do Gateway/frontend goi GraphQL `postDetails` voi list id da duoc rank.

### Chuc nang: Them story

Them story co 2 mode rieng:

- Story thuong: goi `createNormalStory`.
- Story share source: goi `createShareStory`.

Frontend khong gui ca media va shared source trong cung mot request. Neu user dang share bai/reel thi goi `createShareStory`; neu user dang dang story anh/video/text thi goi `createNormalStory`.

#### API: createNormalStory(input)

GraphQL mutation:

```graphql
mutation CreateNormalStory($input: CreateNormalStoryInput!) {
  createNormalStory(input: $input) {
    id
    content
    create
    media {
      id
      type
      url
    }
  }
}
```

Input:

```json
{
  "authorId": 123,
  "content": "Story text",
  "media": {
    "type": 0,
    "url": "https://cdn.local/story.jpg"
  }
}
```

Field:

- `authorId`: user tao story.
- `content`: caption/text cua story.
- `media`: optional, toi da mot media. Neu story chi co text thi gui `media: null`.
- `media.type`: media type convention `0..4`.
- `media.url`: URL da upload xong.

Logic ben trong:

1. Tao story object type `5` voi data `{ content, create, expire }`.
2. `expire = create + 1 day`.
3. Neu co `media`, tao media object type `7` tu `{ type, url }`.
4. Tao `story --contained(20)--> media`.
5. Media cua story la temporary media, khong tao `author --owned(22)--> media`.
6. Tao `author --authored(5)--> story`.
7. Return `NormalStory`.

External calls: khong co.

Output:

```json
{
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

Goi tin frontend gui:

```json
{
  "operationName": "CreateNormalStory",
  "query": "mutation CreateNormalStory($input: CreateNormalStoryInput!) { createNormalStory(input: $input) { id content create media { id type url } } }",
  "variables": {
    "input": {
      "authorId": 123,
      "content": "Story text",
      "media": {
        "type": 0,
        "url": "https://cdn.local/story.jpg"
      }
    }
  }
}
```

Neu story text-only:

```json
{
  "operationName": "CreateNormalStory",
  "query": "mutation CreateNormalStory($input: CreateNormalStoryInput!) { createNormalStory(input: $input) { id content create media { id type url } } }",
  "variables": {
    "input": {
      "authorId": 123,
      "content": "Story text only",
      "media": null
    }
  }
}
```

#### API: createShareStory(input)

GraphQL mutation:

```graphql
mutation CreateShareStory($input: CreateShareStoryInput!) {
  createShareStory(input: $input) {
    ...HomeStoryFields
  }
}
```

Input:

```json
{
  "authorId": 123,
  "content": "Xem bai nay",
  "sharedSourceId": 789
}
```

Field:

- `authorId`: user tao story share.
- `content`: caption/text user viet them tren story.
- `sharedSourceId`: ID source duoc share. Source hop le hien tai:
  - feed post type `2` voi `privacy = 0`;
  - reel type `4`.

Logic ben trong:

1. Retrieve `sharedSourceId`.
2. Neu source la feed post thi yeu cau `privacy = 0`.
3. Neu source la reel thi cho phep.
4. Neu source la group post hoac object khac thi reject.
5. Tao story object type `5` voi `{ content, create, expire }`.
6. `expire = create + 1 day`.
7. Tao `author --authored(5)--> story`.
8. Tao `story --share(8)--> sharedSource`.
9. Khong tao media rieng cho story share.
10. Output la union:
    - `FeedPostShareStory` neu source la feed post public;
    - `ReelShareStory` neu source la reel.

External calls: khong co.

Output vi du share feed post:

```json
{
  "__typename": "FeedPostShareStory",
  "id": 902,
  "content": "Xem bai nay",
  "create": "2026-07-12T10:00:00.0000000Z",
  "sharedSource": {
    "id": 789,
    "content": "Feed post public",
    "media": {
      "id": 3101,
      "type": 0,
      "url": "https://cdn.local/feed-preview.jpg"
    },
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": false
    }
  }
}
```

Output vi du share reel:

```json
{
  "__typename": "ReelShareStory",
  "id": 903,
  "content": "Xem reel nay",
  "create": "2026-07-12T10:00:00.0000000Z",
  "sharedSource": {
    "id": 800,
    "content": "Reel caption",
    "media": {
      "id": 3201,
      "type": 1,
      "url": "https://cdn.local/reel.mp4"
    },
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": false
    }
  }
}
```

Goi tin frontend gui:

```json
{
  "operationName": "CreateShareStory",
  "query": "mutation CreateShareStory($input: CreateShareStoryInput!) { createShareStory(input: $input) { __typename ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on NormalStory { id content create media { id type url } } } }",
  "variables": {
    "input": {
      "authorId": 123,
      "content": "Xem bai nay",
      "sharedSourceId": 789
    }
  }
}
```

Goi tin de thuc hien chuc nang them story:

```json
{
  "normalStoryMode": {
    "operationName": "CreateNormalStory",
    "query": "mutation CreateNormalStory($input: CreateNormalStoryInput!) { createNormalStory(input: $input) { id content create media { id type url } } }",
    "variables": {
      "input": {
        "authorId": 123,
        "content": "Story text",
        "media": {
          "type": 0,
          "url": "https://cdn.local/story.jpg"
        }
      }
    }
  },
  "shareStoryMode": {
    "operationName": "CreateShareStory",
    "query": "mutation CreateShareStory($input: CreateShareStoryInput!) { createShareStory(input: $input) { __typename ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } } }",
    "variables": {
      "input": {
        "authorId": 123,
        "content": "Xem bai nay",
        "sharedSourceId": 789
      }
    }
  }
}
```

Frontend chi chon mot trong hai mode tren de gui len `/graphql`, khong gui ca object wrapper `normalStoryMode/shareStoryMode`.

### Chuc nang: Xoa story cua chinh user

Chuc nang nay dung cho story cua user dang dang nhap, thuong render trong bucket `myStories` duoc ghim dau feed.

#### API: deleteStory(input)

GraphQL mutation:

```graphql
mutation DeleteStory($input: DeleteStoryInput!) {
  deleteStory(input: $input) {
    success
    message
  }
}
```

Input:

```json
{
  "authorId": 123,
  "storyId": 901
}
```

Field:

- `authorId`: user dang yeu cau xoa story.
- `storyId`: story can xoa.

Logic ben trong:

1. Retrieve `storyId`.
2. Neu object khong ton tai, tra `{ success: false, message: "Story not found." }`.
3. Neu object khong phai story type `5`, tra `{ success: false, message: "Object is not a story." }`.
4. Lay author cua story qua `story --authored_by(7)--> author`.
5. Neu author khac `authorId`, tra `{ success: false, message: "Only the story author can delete this story." }`.
6. Lay media temporary qua `story --contained(20)--> media`.
7. Xoa moi association lien quan story.
8. Xoa story object.
9. Xoa media temporary gan truc tiep vao story va association lien quan media.
10. Neu story la story share, khong xoa feed post/reel source goc.

External calls: khong co.

Output:

```json
{
  "success": true,
  "message": "Story deleted."
}
```

Goi tin frontend gui:

```json
{
  "operationName": "DeleteStory",
  "query": "mutation DeleteStory($input: DeleteStoryInput!) { deleteStory(input: $input) { success message } }",
  "variables": {
    "input": {
      "authorId": 123,
      "storyId": 901
    }
  }
}
```

Sau khi `success = true`, frontend co the remove story khoi UI local. Neu bucket `myStories.stories` rong sau khi remove, an bucket story cua minh hoac hien nut tao story tuy UI.
