# SocialGraph Documentation Index

Tai lieu trong thu muc nay phan anh contract hien tai cua code va schema export.

## Bat Dau Theo Nhu Cau

- Frontend/Gateway: [`api-by-front-feature.md`](api-by-front-feature.md)
- Home, feed, shortcut group, post hydration: [`completedAPI/home.md`](completedAPI/home.md)
- Dang ky canonical user ID: [`completedAPI/register.md`](completedAPI/register.md)
- Story create/read/delete: [`completedAPI/story.md`](completedAPI/story.md)
- Cong nghe va cau truc project: [`project-technology-and-structure.md`](project-technology-and-structure.md)
- Object/association core: [`coreFunction.md`](coreFunction.md)
- SocialGraph database schema: [`databaseSchemaSocialGraph.md`](databaseSchemaSocialGraph.md)
- Notification database schema: [`databaseSchemaNotification.md`](databaseSchemaNotification.md)

## Public Qua Gateway

Frontend chi goi `POST /graphql` cua API Gateway. SocialGraph fields da public:

```text
Query:    visitedGroups, postDetail, postDetails, homeStories, myStories
Mutation: createUser, recordGroupVisit, createFeedPost,
          createNormalStory, createShareStory, deleteStory
```

Gateway cung compose `recommendFeed` cua Recommendation. `RecommendationItem.post` duoc SocialGraph batch-hydrate qua internal Fusion lookup.

Raw object/association fields va domain mutations chua co ownership authorization van la `@internal`; frontend khong duoc phu thuoc vao chung.

## Internal REST

```text
GET    /internal/recommendation/post-candidate-ids
GET    /internal/recommendation/reel-candidates
PUT    /internal/users/{userId}/verify
DELETE /internal/stories/expired?limit=100
```

Moi internal REST request can `X-Gateway-Secret`; frontend khong goi truc tiep.
