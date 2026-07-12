# Billing Service - Huong Dan Implement V1

File nay danh cho agent code Billing/Payment service. Muc tieu V1: xu ly goi tich xanh `verified_18k`, sau khi thanh toan thanh cong thi goi SocialGraph de cap nhat han verify trong `user.data.verify`.

## 1. Boi canh microservice

He thong gom:

- Gateway: GraphQL Federation, frontend chi noi voi Gateway.
- SocialGraph: nam user/group/post/relation/content, expose GraphQL va REST internal.
- Authentication: email/password/session/token.
- Messenger: chat.
- Search: index user/group/post.
- Recommendation: tao/rank feed tu candidate pool cua SocialGraph.
- Notification: luu va day notification.
- Billing/Payment: order, thanh toan, lich su giao dich.

Billing khong sua truc tiep DB cua SocialGraph. Billing chi can biet `userId` va product user mua.

## 2. Boundary moi voi SocialGraph

SocialGraph khong goi Billing de doc entitlement nua.

Billing/Payment se goi nguoc ve SocialGraph khi thanh toan thanh cong:

```http
PUT /internal/users/{userId}/verify
X-Gateway-Secret: <shared secret at least 32 bytes>
X-Correlation-ID: <trace id>
```

Body:

```json
{
  "expiresAt": "2026-08-10T00:00:00Z"
}
```

Clear verify:

```json
{
  "expiresAt": null
}
```

SocialGraph luu chuoi ISO nay vao `user.data.verify`. Query profile cua SocialGraph tra:

- `verify`: thoi diem het han dang luu.
- `isVerified`: true neu parse duoc `verify` va `verify > now`.

## 3. Product bat buoc

### verified_18k

- Gia: `18000` VND.
- Thoi han de xuat: 30 ngay.
- Tac dung: cap tich xanh cho user den `expiresAt`.
- SocialGraph field lien quan: `user.data.verify`.

Goi tra tien de day bai len feed da bi loai khoi V1. Khong tao product rieng cho post promotion, khong tra multiplier cho Recommendation.

## 4. Database schema de xuat

Dung PostgreSQL, schema rieng `billing`.

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
    verify_expires_at TIMESTAMPTZ,
    metadata JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_billing_orders_user_created
    ON billing_orders (user_id, created_at DESC);

CREATE INDEX idx_billing_orders_status_created
    ON billing_orders (status, created_at DESC);

CREATE TABLE billing_webhook_events (
    id BIGINT PRIMARY KEY,
    provider TEXT NOT NULL,
    provider_event_id TEXT NOT NULL,
    received_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    processed_at TIMESTAMPTZ,
    payload JSONB NOT NULL,
    UNIQUE (provider, provider_event_id)
);
```

Entity toi thieu:

- `BillingOrder`
- `BillingWebhookEvent`

Khong bat buoc tao `user_entitlements` vi SocialGraph khong doc entitlement tu Billing nua. Neu can lich su noi bo, co the tao bang grant rieng, nhung API contract van la goi SocialGraph update verify.

## 5. Order status

Status de xuat:

- `pending`: moi tao, chua tra tien.
- `paid`: da tra tien va da sync verify sang SocialGraph.
- `paid_sync_failed`: da tra tien nhung goi SocialGraph loi, can retry.
- `failed`: thanh toan loi.
- `cancelled`: user huy.
- `refunded`: da hoan tien; Billing nen clear verify neu policy yeu cau.

## 6. Lifecycle V1

1. Gateway/Frontend goi Billing tao order.
2. Billing tao `billing_orders` status `pending`.
3. User thanh toan qua mock endpoint hoac provider.
4. Billing verify payment.
5. Billing tinh `expiresAt`.
6. Billing goi SocialGraph `PUT /internal/users/{userId}/verify`.
7. Neu SocialGraph 2xx: set order `paid`, luu `paid_at`, `verify_expires_at`.
8. Neu SocialGraph fail: set `paid_sync_failed` va retry bang background job.

Khi user mua tiep trong luc con han, Billing nen gia han tu:

```text
base = max(currentVerifyExpiresAt, now)
newExpiresAt = base + 30 days
```

Billing co the lay `currentVerifyExpiresAt` tu order paid moi nhat cua chinh Billing, hoac goi Gateway/SocialGraph profile neu can doi chieu. Khong doc truc tiep DB SocialGraph.

## 7. API public cho Gateway/Frontend

### POST /billing/orders

Request:

```json
{
  "userId": 123,
  "productCode": "verified_18k"
}
```

Validation:

- `userId > 0`.
- `productCode == "verified_18k"`.

Response:

```json
{
  "id": 987,
  "userId": 123,
  "productCode": "verified_18k",
  "amount": 18000,
  "currency": "VND",
  "status": "pending",
  "createdAt": "2026-07-11T00:00:00Z",
  "paidAt": null,
  "verifyExpiresAt": null,
  "paymentUrl": "http://localhost:5002/billing/orders/987/mock-pay"
}
```

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
  "createdAt": "2026-07-11T00:00:00Z",
  "paidAt": "2026-07-11T00:01:00Z",
  "verifyExpiresAt": "2026-08-10T00:01:00Z",
  "paymentUrl": null
}
```

### POST /billing/orders/{orderId}/mock-paid

Endpoint development-only.

Response:

```json
{
  "success": true,
  "orderId": 987,
  "status": "paid",
  "verifyExpiresAt": "2026-08-10T00:01:00Z"
}
```

Logic:

1. Load order.
2. Neu khong ton tai -> 404.
3. Neu da `paid` -> tra state hien tai, khong sync duplicate neu khong can.
4. Tinh `verifyExpiresAt`.
5. Goi SocialGraph update verify.
6. Update order.

### POST /billing/webhook

Dung cho provider that sau nay.

Yeu cau:

- Idempotent theo `(provider, provider_event_id)`.
- Luu raw payload vao `billing_webhook_events`.
- Chi mark paid khi verify duoc chu ky/provider status.
- Sau khi paid thi di cung flow sync SocialGraph.

## 8. API internal can goi SocialGraph

Config de xuat trong Billing:

```json
{
  "ExternalServices": {
    "SocialGraphSetUserVerify": "http://localhost:5000/internal/users/{userId}/verify"
  }
}
```

Call:

```http
PUT /internal/users/123/verify
Content-Type: application/json
X-Gateway-Secret: <shared secret at least 32 bytes>
X-Correlation-ID: billing-order-456
```

```json
{
  "expiresAt": "2026-08-10T00:01:00Z"
}
```

Response 200 la `UserProfileResult`. Response 404 nghia la user khong ton tai trong SocialGraph. Response 403 nghia la shared secret sai/thieu; 503 nghia la SocialGraph chua cau hinh internal auth. Billing nen de order o `paid_sync_failed` va retry theo policy cho loi co the phuc hoi.

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
    DateTimeOffset? VerifyExpiresAt,
    string? PaymentUrl);

public sealed record MockPaidResponse(
    bool Success,
    long OrderId,
    string Status,
    DateTimeOffset? VerifyExpiresAt);

public sealed record SetUserVerifyRequest(DateTimeOffset? ExpiresAt);
```

## 10. SocialGraph response can parse

Billing chi can biet call update thanh cong hay khong. Neu muon log, parse cac field nay:

```json
{
  "id": 123,
  "verify": "2026-08-10T00:01:00Z",
  "isVerified": true
}
```

Khong can endpoint `GET /internal/billing/entitlements`; SocialGraph khong dung endpoint do nua.

## 11. Test bat buoc

- Tao order `verified_18k` dung amount `18000`.
- Product khong hop le bi reject.
- Mark paid tinh `verifyExpiresAt = now + 30 days` khi chua con han.
- Mua lai khi con han thi gia han tu han cu.
- Mark paid 2 lan idempotent.
- SocialGraph 404/500 thi order vao `paid_sync_failed` va co the retry.
- Webhook duplicate khong tao side effect duplicate.
- Clear verify khi refund neu policy yeu cau.

## 12. Viec chua can lam V1

- Chua can payment provider that.
- Chua can subscription auto-renew.
- Chua can invoice PDF.
- Chua can doc/sua database SocialGraph.
- Chua can feed/post promotion.
