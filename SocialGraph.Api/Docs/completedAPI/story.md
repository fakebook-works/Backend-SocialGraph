# Completed API - Story Mutations

File nay ghi lai cac API story mutation da hoan thien.

## Trusted Caller

Tat ca story mutation can trusted viewer:

```http
X-Gateway-Secret: <shared secret at least 32 bytes>
X-User-Id: <authenticated user id>
```

`authorId` trong input phai khop `X-User-Id`.

## Chuc Nang: Them Story

Them story co hai mode rieng:

- Story thuong: `createNormalStory`.
- Story share source: `createShareStory`.

Frontend khong gui ca media va shared source trong cung mot request. Neu user dang dang story anh/video/text thi goi `createNormalStory`; neu user dang share feed post public hoac reel thi goi `createShareStory`.

## API: createNormalStory(input)

GraphQL:

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

Rules:

- `media` optional.
- Neu co media thi toi da mot media.
- Media URL phai la URL da upload xong.
- Story media la temporary, khong tao association `owned(22)`.

Logic:

1. Validate trusted caller va check `input.authorId == X-User-Id`.
2. Tao story object type `5`.
3. Data story co `content`, `create`, `expire`.
4. `expire = create + 1 day`.
5. Neu co media:
   - tao media object type `7`;
   - tao association `story --contained(20)--> media`;
   - khong tao `owned(22)`.
6. Tao association `author --authored(5)--> story`.
7. Tra `NormalStory`.

Output:

```json
{
  "id": 999,
  "content": "Story text",
  "create": "2026-07-12T10:00:00.0000000Z",
  "media": [
    {
      "id": 3001,
      "type": 0,
      "url": "https://cdn.local/story.jpg"
    }
  ]
}
```

Goi tin frontend/gateway:

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

## API: createShareStory(input)

GraphQL:

```graphql
mutation CreateShareStory($input: CreateShareStoryInput!) {
  createShareStory(input: $input) {
    __typename
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
```

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

Rules:

- Chi share feed post public type `2`, privacy `0`.
- Chi share reel type `4`.
- Khong share group post.
- Khong gui media rieng trong share story.

Logic:

1. Validate trusted caller va check `input.authorId == X-User-Id`.
2. Retrieve shared source.
3. Neu source la feed post thi privacy phai la `0`.
4. Neu source la reel thi chap nhan.
5. Source type khac bi reject.
6. Tao story object type `5`, expire = create + 1 day.
7. Luu shared source id trong data story.
8. Tao association `author --authored(5)--> story`.
9. Tao association den shared source.
10. Tra union `HomeStory` dung type source.

Output feed post share:

```json
{
  "__typename": "FeedPostShareStory",
  "id": 1000,
  "content": "Share this",
  "create": "2026-07-12T10:05:00.0000000Z",
  "sharedSource": {
    "id": 789,
    "content": "Original post",
    "media": {
      "id": 3002,
      "type": 0,
      "url": "https://cdn.local/post.jpg"
    },
    "author": {
      "id": 456,
      "name": "Tran Van B",
      "avatar": "https://cdn.local/b.jpg",
      "isVerified": true
    }
  }
}
```

Goi tin frontend/gateway:

```json
{
  "operationName": "CreateShareStory",
  "query": "mutation CreateShareStory($input: CreateShareStoryInput!) { createShareStory(input: $input) { __typename ... on FeedPostShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } ... on ReelShareStory { id content create sharedSource { id content media { id type url } author { id name avatar isVerified } } } } }",
  "variables": {
    "input": {
      "authorId": 123,
      "content": "Share this",
      "sharedSourceId": 789
    }
  }
}
```

## Chuc Nang: Xoa Story Cua Chinh User

## API: deleteStory(input)

GraphQL:

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
  "input": {
    "authorId": 123,
    "storyId": 999
  }
}
```

Logic:

1. Validate trusted caller va check `input.authorId == X-User-Id`.
2. Retrieve story.
3. Story phai ton tai va object type phai la `5`.
4. Story phai do author hien tai authored.
5. Lay media contained trong story.
6. Media nao co `owned(22)` hoac dang duoc content khac contained thi giu lai.
7. Media temporary chi thuoc story nay thi xoa.
8. Xoa association cua story.
9. Xoa story object.
10. Chay trong relational transaction neu database support transaction.

Output thanh cong:

```json
{
  "success": true,
  "message": null
}
```

Output khi khong co quyen hoac story khong hop le:

```json
{
  "success": false,
  "message": "Story not found or not owned by user."
}
```

Goi tin frontend/gateway:

```json
{
  "operationName": "DeleteStory",
  "query": "mutation DeleteStory($input: DeleteStoryInput!) { deleteStory(input: $input) { success message } }",
  "variables": {
    "input": {
      "authorId": 123,
      "storyId": 999
    }
  }
}
```

## Cleanup Story Het Han

Read path `homeStories` va `myStories` chi filter story het han, khong xoa data.

Cleanup do background service hoac REST internal phu trach:

```http
DELETE /internal/stories/expired?limit=100
X-Gateway-Secret: <shared secret>
```

Output:

```json
{
  "deleted": 12
}
```
