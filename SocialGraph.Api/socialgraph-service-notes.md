# SocialGraph Service Notes

## Public Boundary

- Gateway/frontend should call SocialGraph through GraphQL Federation only.
- REST endpoints in this service are internal service-to-service endpoints.
- PostgreSQL is the source of truth; Redis is only cache.

## Core Model

- Object table: `social_graph.objects(id, otype, data jsonb)`.
- Association table: `social_graph.associations(id1, atype, id2, time)`.
- Object types:
  - `0 user`, `1 group`, `2 post`, `3 reel`, `4 story`, `5 comment`, `6 media`.
- Important associations:
  - `0 friend`, `1/2 follow`, `3/4 like`, `5/6 authored`, `7 comment`, `8 share`.
  - `9/10 group published`, `13/14 member`, `15/16 admin`.
  - `17/18 watched`, `19 saved`, `20 contained`, `21 mentioned`, `22 owned`.
  - `23/24 blocked`.

## GraphQL Surface

Main mutation groups:

- User: `createUser`, `updateUser`, `deleteUser`, `changeUserAvatar`.
- Relation: `sendFriendRequest`, `acceptFriendRequest`, `followUser`, `unfollowUser`, `blockUser`, `unblockUser`.
- Group: `createGroup`, `updateGroup`, `deleteGroup`, member/admin mutations.
- Content: `createFeedPost`, `createGroupPost`, `updatePost`, `deleteContent`, `createComment`, `createStory`, `createReel`, `sharePost`, `like`, `save`, `watch`, `tag`, `mention`.

Main queries:

- `profile(userId)`, `group(groupId)`, `content(contentId)`.
- `relationIds(id1, atype, cursor, limit)`.
- `postCandidates(userId, limit)`, `reelCandidates(userId, limit)`.
- Core debug: `object`, `association`, `associationCount`.

## Internal REST

- `GET /internal/recommendation/post-candidates?userId=&limit=`
- `GET /internal/recommendation/reel-candidates?userId=&limit=`

Candidate responses include `id`, `authorId`, `source`, `createdAt`, `boostMultiplier`.

## External Services

External calls are best-effort in V1. SocialGraph logs failures but keeps local graph writes complete.

- Auth/Messenger/Search/Recommendation/Notification are called from `ExternalServiceClient`.
- Billing is read through `BillingClient`.
- Billing entitlement failure does not break profile or candidate APIs.
