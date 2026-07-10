# Notification Service - Huong Dan Implement Day Du

File nay danh cho agent code Notification service. Muc tieu: tao service co DB, API, realtime/polling contract de SocialGraph goi duoc va frontend/Gateway lay notification duoc.

## 1. Boi canh toan he thong microservice

He thong gom:

- Gateway: GraphQL Federation, frontend noi vao Gateway.
- SocialGraph: tao user/group/post/relation va goi Notification khi co event xa hoi.
- Notification: luu notification, danh dau read, day realtime/polling cho frontend.
- Authentication: xac thuc user.
- Messenger/Search/Recommendation/Billing: service rieng.

Notification service khong nen tu suy dien graph qua DB SocialGraph. No nhan event tu SocialGraph va luu thanh notification.

## 2. Vai tro Notification service

Notification service can lam:

- Nhan event notification tu service khac, truoc mat la SocialGraph.
- Luu notification vao PostgreSQL.
- Cho Gateway/Frontend query notification cua user.
- Ho tro mark read/read all.
- Co the day realtime bang WebSocket/SSE sau nay.
- Luu data JSONB de frontend render linh hoat.

## 3. Action types can support

Can khop voi `ExternalNotificationAction` cua SocialGraph:

| action_type | Name | Creator | Receiver | Object |
|---:|---|---|---|---|
| 0 | friend_request | requester | receiver | requester id hoac null |
| 1 | friend_accept | accepter | requester | accepter id hoac null |
| 2 | group_invite | inviter | invited user | group id |
| 3 | group_join | user | group admin | group id |
| 4 | group_accept | admin | user | group id |
| 5 | comment | commenter | target author | target post/reel/story/comment id |
| 6 | like | liker | target author | target id |
| 7 | mention | author | mentioned user | source id |
| 8 | tag | author | tagged user | post id |

SocialGraph hien goi cac action:

- `friend_request`
- `friend_accept`
- `comment`
- `like`
- `mention`
- `tag`

Group invite/join/accept co trong schema de service khac dung sau.

## 4. Database schema de xuat

File cu `databaseSchemaNotification.md` da co schema nen co the dung:

```sql
CREATE SCHEMA IF NOT EXISTS notification_service;
SET search_path TO notification_service;

CREATE TABLE notifications (
    id BIGINT PRIMARY KEY,
    creator_id BIGINT NOT NULL,
    receiver_id BIGINT NOT NULL,
    action_type SMALLINT NOT NULL,
    object_id BIGINT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    is_read BOOLEAN NOT NULL DEFAULT false,
    data JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_notifications_receiver_created
    ON notifications (receiver_id, created_at DESC);

CREATE INDEX idx_notifications_receiver_read_created
    ON notifications (receiver_id, is_read, created_at DESC);
```

Neu muon giu ten table cu la `notification` cung duoc, nhung khuyen nghi dung plural `notifications`.

## 5. Snowflake ID

Notification nen co ID generator rieng giong SocialGraph:

- `long id`
- sortable theo thoi gian.
- worker id rieng qua env `NOTIFICATION_WORKER_ID`.

Khong nen lay ID tu SocialGraph.

## 6. API internal SocialGraph se goi

Trong SocialGraph config:

```json
"NotificationServiceCreateNotification": "http://localhost:5001"
```

SocialGraph `ExternalServiceClient.NotifyAsync` dang POST JSON:

```json
{
  "creatorId": 111,
  "receiverId": 222,
  "actionType": 6,
  "objectId": 333,
  "data": {}
}
```

Notification service can expose endpoint nhan payload nay. De ro rang, nen dat:

### POST /internal/notifications

Request:

```json
{
  "creatorId": 111,
  "receiverId": 222,
  "actionType": 6,
  "objectId": 333,
  "data": {
    "optional": "metadata"
  }
}
```

Response:

```json
{
  "id": 999,
  "creatorId": 111,
  "receiverId": 222,
  "actionType": 6,
  "objectId": 333,
  "createdAt": "2026-07-09T00:00:00Z",
  "isRead": false,
  "data": {
    "optional": "metadata"
  }
}
```

Important: De khop appsettings hien tai, co 2 cach:

1. Doi appsettings SocialGraph thanh URL day du `http://notification-service/internal/notifications`.
2. Hoac Notification service chap nhan POST ngay tai root `/`.

Khuyen nghi cach 1.

Validation:

- `receiverId` bat buoc.
- `actionType` phai nam trong `0..8`.
- Neu `creatorId == receiverId` co the bo qua notification va tra success noop.
- `data` neu null thi luu `{}`.

## 7. API cho Gateway/Frontend

Neu Gateway la GraphQL Federation, Notification service nen expose GraphQL subgraph. Neu can REST noi bo thi co the co REST song song.

### Query notifications

GraphQL field de xuat:

```graphql
notifications(userId: Long!, cursor: String, limit: Int = 20, unreadOnly: Boolean = false): NotificationPage!
```

REST tuong duong:

`GET /notifications?userId=&cursor=&limit=&unreadOnly=`

Response:

```json
{
  "items": [
    {
      "id": 999,
      "creatorId": 111,
      "receiverId": 222,
      "actionType": 6,
      "objectId": 333,
      "createdAt": "2026-07-09T00:00:00Z",
      "isRead": false,
      "data": {}
    }
  ],
  "nextCursor": "2026-07-09T00:00:00Z|999"
}
```

Pagination:

- Sort `created_at DESC, id DESC`.
- Cursor nen encode `(createdAt, id)`.
- Neu cursor null thi lay trang dau.

### Query unread count

GraphQL:

```graphql
unreadNotificationCount(userId: Long!): Long!
```

REST:

`GET /notifications/unread-count?userId=`

Response:

```json
{
  "count": 12
}
```

### Mark one read

GraphQL mutation:

```graphql
markNotificationRead(userId: Long!, notificationId: Long!): Boolean!
```

REST:

`POST /notifications/{notificationId}/read`

Request:

```json
{
  "userId": 222
}
```

Logic:

- Chi update neu `receiver_id = userId`.
- Set `is_read = true`.

### Mark all read

GraphQL:

```graphql
markAllNotificationsRead(userId: Long!): Int!
```

REST:

`POST /notifications/read-all`

Request:

```json
{
  "userId": 222
}
```

Return so notification da update.

## 8. DTO de agent tao

```csharp
public sealed record CreateNotificationRequest(
    long CreatorId,
    long ReceiverId,
    short ActionType,
    long? ObjectId,
    JsonElement? Data);

public sealed record NotificationResult(
    long Id,
    long CreatorId,
    long ReceiverId,
    short ActionType,
    long? ObjectId,
    DateTimeOffset CreatedAt,
    bool IsRead,
    JsonElement Data);

public sealed record NotificationPageResult(
    IReadOnlyList<NotificationResult> Items,
    string? NextCursor);
```

## 9. Render notification tren frontend

Notification service khong can build message tieng Viet cuoi cung, nhung nen tra du data de frontend/Gateway render.

Mapping goi y:

- `friend_request`: creator sent you a friend request.
- `friend_accept`: creator accepted your friend request.
- `comment`: creator commented on your post/reel/story/comment.
- `like`: creator liked your content.
- `mention`: creator mentioned you.
- `tag`: creator tagged you.

Frontend co the can lay profile creator tu SocialGraph theo `creatorId`.

## 10. Realtime delivery

V1 co the chua can realtime. Neu lam:

- Option 1: WebSocket endpoint `/notifications/ws?userId=`.
- Option 2: Server-Sent Events `/notifications/stream?userId=`.
- Option 3: Redis pub/sub internal.

Flow:

1. Create notification luu DB.
2. Publish event in-memory/Redis channel `notifications:{receiverId}`.
3. Connection cua user nhan event.

Neu chua lam realtime, Gateway/Frontend poll query notifications + unread count.

## 11. Idempotency va duplicate

Nen co co che tranh duplicate neu service caller retry:

- Them optional `idempotencyKey` vao request sau nay.
- V1 co the chua can.

Neu them:

```sql
ALTER TABLE notifications ADD COLUMN idempotency_key TEXT;
CREATE UNIQUE INDEX uq_notifications_idempotency
    ON notifications (receiver_id, idempotency_key)
    WHERE idempotency_key IS NOT NULL;
```

## 12. Test cases bat buoc

- Create notification thanh cong.
- Create notification voi `data = null` luu `{}`.
- Create notification invalid action type bi reject.
- Neu creator == receiver thi noop hoac van luu tuy quyet dinh; khuyen nghi noop.
- Query notification sort moi nhat truoc.
- Cursor pagination khong duplicate/skip.
- Unread count dung.
- Mark one read chi update notification cua receiver.
- Mark all read tra so row update.

## 13. Nhung dieu Notification service khong nen lam

- Khong validate objectId co ton tai bang cach doc DB SocialGraph.
- Khong tu tao friend association.
- Khong tu rank feed.
- Khong luu business state cua Billing.
- Khong can biet password/email.

