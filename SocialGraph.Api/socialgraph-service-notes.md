# SocialGraph Service Notes

## Public Boundary

- Gateway/frontend should call SocialGraph through GraphQL Federation only.
- REST endpoints in this service are internal service-to-service endpoints.
- PostgreSQL is the source of truth; Redis is only cache.

## Core Model

- Object table: `social_graph.objects(id, otype, data jsonb)`.
- Association table: `social_graph.associations(id1, atype, id2, time)`.
- Object types:
  - `0 user`, `1 group`, `2 feed post`, `3 group post`, `4 reel`, `5 story`, `6 comment`, `7 media`.
- Important associations:
  - `0 friend`, `1/2 follow`, `3/4 like`, `5/6 authored`, `7 comment`, `8 share`.
  - `9/10 group published`, `13/14 member`, `15/16 admin`.
  - `17/18 watched`, `19 saved`, `20 contained`, `21 mentioned`, `22 owned`.
  - `23/24 blocked`.

## GraphQL Surface

Main mutation groups:

- User: `createUser`, `updateUser`, `deleteUser`, `changeUserAvatar`, `changeUserBackground`.
- Relation: `sendFriendRequest`, `acceptFriendRequest`, `followUser`, `unfollowUser`, `blockUser`, `unblockUser`.
- Group: `createGroup`, `updateGroup`, `deleteGroup`, `changeGroupBackground`, member/admin mutations.
- Content: `createFeedPost`, `createGroupPost`, `updatePost`, `deleteContent`, `createComment`, `createStory`, `createReel`, `sharePost`, `like`, `save`, `watch`, `tag`, `mention`.
  - Feed post is object type `2` and has its own privacy. Group post is object type `3`; its effective privacy comes from the group.
  - Story and reel accept at most one media URL.

Main queries:

- `profile(userId)`, `group(groupId)`, `content(contentId)`.
- `relationIds(id1, atype, cursor, limit)`.
- `postCandidates(userId, limit)`, `reelCandidates(userId, limit)`.
- Core debug: `object`, `association`, `associationCount`.

## Internal REST

- `GET /internal/recommendation/post-candidates?userId=&limit=`
- `GET /internal/recommendation/reel-candidates?userId=&limit=`
- `PUT /internal/users/{userId}/verify`

Candidate responses include `id`, `authorId`, `source`, `createdAt`.
Payment/Billing should use the verify endpoint to set or clear `user.data.verify`; GraphQL user updates cannot change this field.

## External Services

External calls are best-effort in V1. SocialGraph logs failures but keeps local graph writes complete.

- Auth/Messenger/Search/Recommendation/Notification are called from `ExternalServiceClient`.
- SocialGraph no longer reads paid-state APIs from Billing. Payment/Billing calls SocialGraph only after successful payment.
