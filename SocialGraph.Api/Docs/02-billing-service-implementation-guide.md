# Billing Service - Huong Dan Implement Day Du

File nay danh cho agent code Billing service. Muc tieu: doc file nay va co the tao mot Billing microservice du chay voi SocialGraph hien tai, dong thoi de sau nay mo rong thanh thanh toan that.

## 1. Boi canh toan he thong microservice

He thong gom cac service chinh:

- Gateway: GraphQL Federation, frontend chi noi voi Gateway.
- SocialGraph: nam user/group/post/relation, goi Billing de doc entitlement.
- Authentication: quan ly email/password/session/token.
- Messenger: chat user.
- Search: index user/group/post.
- Recommendation: embedding/ranking feed.
- Notification: luu/thong bao notification.
- Billing: quan ly don hang, goi thanh toan, entitlement tra phi.

Billing khong can biet chi tiet graph. Billing chi can biet `user_id` va product ma user mua.

## 2. Vai tro Billing service

Billing service can lam:

- Tao order cho cac goi tra phi.
- Xac nhan order da thanh toan.
- Tao hoac gia han entitlement cho user.
- Cung cap API de service khac doc entitlement active.
- Sau nay tich hop payment provider/webhook that.

Billing service khong nen:

- Sua truc tiep database SocialGraph.
- Tu quyet feed ranking.
- Luu password/user profile.
- Tao notification truc tiep neu khong can. Neu muon thong bao thanh toan thanh cong, co the goi Notification service.

## 3. Products bat buoc

### verified_18k

- Gia: `18000` VND.
- Entitlement: `verified`.
- Thoi han: 30 ngay.
- SocialGraph dung de tra `UserProfileResult.IsVerified = true`.

### feed_boost_36k

- Gia: `36000` VND.
- Entitlement: `feed_boost_author`.
- Thoi han: 30 ngay.
- Metadata default:

```json
{
  "boostMultiplier": "1.3"
}
```

- SocialGraph dung multiplier nay trong candidate response.

## 4. Database schema de xuat

Dung PostgreSQL. Schema rieng: `billing`.

```sql
CREATE SCHEMA IF NOT EXISTS billing;
SET search_path TO billing;

CREATE TABLE billing_orders (
    id BIGINT PRIMARY KEY,
    user_id BIGINT NOT NULL,
    product_code TEXT NOT NULL,
    amount INTEGER NOT NULL,
    currency TEXT NOT NULL DEFAULT 'VND',
    status TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    paid_at TIMESTAMPTZ,
    provider TEXT,
    provider_ref TEXT,
    metadata JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_billing_orders_user_created
    ON billing_orders (user_id, created_at DESC);

CREATE INDEX idx_billing_orders_status_created
    ON billing_orders (status, created_at DESC);

CREATE TABLE user_entitlements (
    id BIGINT PRIMARY KEY,
    user_id BIGINT NOT NULL,
    type TEXT NOT NULL,
    source_order_id BIGINT REFERENCES billing_orders(id),
    starts_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ,
    metadata JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_user_entitlements_lookup
    ON user_entitlements (user_id, type, expires_at);

CREATE INDEX idx_user_entitlements_order
    ON user_entitlements (source_order_id);
```

Neu dung EF Core, tao entity:

- `BillingOrder`
- `UserEntitlement`

## 5. Status va lifecycle order

Order status nen co:

- `pending`: moi tao, chua tra tien.
- `paid`: da tra tien, da tao entitlement.
- `failed`: thanh toan loi.
- `cancelled`: user huy.
- `refunded`: da hoan tien, entitlement nen bi thu hoi hoac expire ngay.

Lifecycle V1 mock:

1. Gateway/Frontend goi Billing tao order.
2. Billing tao row `billing_orders` status `pending`.
3. Dev/test goi endpoint mock-paid.
4. Billing update order status `paid`, set `paid_at`.
5. Billing tao/gia han entitlement 30 ngay.
6. SocialGraph doc entitlement qua internal API.

## 6. API public cho Gateway/Frontend

### POST /billing/orders

Tao order.

Request:

```json
{
  "userId": 123,
  "productCode": "verified_18k"
}
```

Validation:

- `userId` bat buoc.
- `productCode` phai la `verified_18k` hoac `feed_boost_36k`.

Response:

```json
{
  "id": 987,
  "userId": 123,
  "productCode": "verified_18k",
  "amount": 18000,
  "currency": "VND",
  "status": "pending",
  "createdAt": "2026-07-09T00:00:00Z",
  "paymentUrl": "http://localhost:xxxx/billing/orders/987/mock-pay"
}
```

V1 co the tra `paymentUrl` mock. Sau nay thay bang provider URL.

### GET /billing/orders/{orderId}

Response:

```json
{
  "id": 987,
  "userId": 123,
  "productCode": "verified_18k",
  "amount": 18000,
  "currency": "VND",
  "status": "paid",
  "createdAt": "2026-07-09T00:00:00Z",
  "paidAt": "2026-07-09T00:01:00Z"
}
```

### POST /billing/orders/{orderId}/mock-paid

Endpoint development-only de danh dau order da tra tien.

Request co the rong.

Response:

```json
{
  "success": true,
  "orderId": 987,
  "status": "paid",
  "entitlement": {
    "type": "verified",
    "startsAt": "2026-07-09T00:01:00Z",
    "expiresAt": "2026-08-08T00:01:00Z",
    "metadata": {}
  }
}
```

Logic:

1. Load order.
2. Neu order khong ton tai -> 404.
3. Neu da paid roi -> tra current entitlement, khong tao duplicate.
4. Update status `paid`.
5. Tao/gia han entitlement.

### GET /billing/users/{userId}/entitlements

API public cho Gateway neu can hien thi trang subscription.

Response:

```json
{
  "userId": 123,
  "entitlements": [
    {
      "type": "verified",
      "startsAt": "2026-07-09T00:00:00Z",
      "expiresAt": "2026-08-08T00:00:00Z",
      "metadata": {}
    }
  ]
}
```

## 7. API internal bat buoc cho SocialGraph

SocialGraph hien cau hinh:

```json
"BillingServiceGetActiveEntitlements": "http://localhost:5001/internal/billing/entitlements"
```

### GET /internal/billing/entitlements?userId={userId}

Request:

- query `userId`.

Response chap nhan 1 trong 2 format sau.

Format khuyen nghi:

```json
{
  "entitlements": [
    {
      "type": "verified",
      "expiresAt": "2026-08-08T00:00:00Z",
      "metadata": {}
    },
    {
      "type": "feed_boost_author",
      "expiresAt": "2026-08-08T00:00:00Z",
      "metadata": {
        "boostMultiplier": "1.3"
      }
    }
  ]
}
```

Format SocialGraph cung chap nhan:

```json
[
  {
    "type": "verified",
    "expiresAt": "2026-08-08T00:00:00Z",
    "metadata": {}
  }
]
```

Chi tra entitlement active:

- `starts_at <= now`
- `expires_at IS NULL OR expires_at > now`

Neu user khong co entitlement, tra:

```json
{
  "entitlements": []
}
```

## 8. Logic tao/gia han entitlement

Khi order paid:

1. Map product sang entitlement:
   - `verified_18k` -> `verified`
   - `feed_boost_36k` -> `feed_boost_author`
2. Tim entitlement active cung user va type.
3. Neu dang active:
   - gia han tu `max(existing.expires_at, now) + 30 days`.
4. Neu khong active:
   - tao entitlement moi `starts_at = now`, `expires_at = now + 30 days`.
5. Metadata:
   - verified: `{}`
   - boost: `{ "boostMultiplier": "1.3" }`

## 9. DTO de agent tao

```csharp
public sealed record CreateBillingOrderRequest(long UserId, string ProductCode);

public sealed record BillingOrderResponse(
    long Id,
    long UserId,
    string ProductCode,
    int Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    string? PaymentUrl);

public sealed record EntitlementResponse(
    string Type,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ActiveEntitlementsResponse(
    long UserId,
    IReadOnlyList<EntitlementResponse> Entitlements);
```

## 10. Cach SocialGraph se dung Billing

### Profile

SocialGraph `GetProfileAsync` goi:

- `IBillingClient.IsVerifiedAsync(userId)`

Neu Billing tra entitlement `verified` active thi profile co:

```json
{
  "isVerified": true
}
```

Neu Billing down/loi/empty thi:

```json
{
  "isVerified": false
}
```

### Feed candidate

SocialGraph `CandidateService` goi:

- `IBillingClient.GetFeedBoostMultiplierAsync(authorId)`

Neu author co `feed_boost_author` active:

```json
{
  "boostMultiplier": 1.3
}
```

Neu khong:

```json
{
  "boostMultiplier": 1.0
}
```

Recommendation service moi dung multiplier de rank feed cuoi.

## 11. Yeu cau test

Agent Billing can viet test cho:

- Tao order `verified_18k` dung amount 18000.
- Tao order `feed_boost_36k` dung amount 36000.
- Product khong hop le bi reject.
- Mark paid tao entitlement 30 ngay.
- Mark paid 2 lan khong tao duplicate order side effect.
- Gia han entitlement neu user mua lai khi con han.
- Internal entitlements chi tra active entitlement.
- Expired entitlement khong tra ve.
- Response JSON khop format SocialGraph doc o tren.

## 12. Dieu chua can lam V1

- Khong can payment provider that.
- Khong can refund phuc tap.
- Khong can invoice PDF.
- Khong can subscription auto-renew.
- Khong can doc/sua SocialGraph DB.

