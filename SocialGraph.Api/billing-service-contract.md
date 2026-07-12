# Payment/Billing Service Contract

SocialGraph khong doc entitlement tu Billing nua. Billing/Payment chi xu ly order va thanh toan; khi goi tich xanh thanh cong thi goi REST internal cua SocialGraph de cap nhat `user.data.verify`.

## Product V1

- `verified_18k`: price `18000`, cap tich xanh cho user den thoi diem `expiresAt`.

Goi tra tien de tang ti le xuat hien tren feed da bi loai khoi scope hien tai. Recommendation khong nhan multiplier tu SocialGraph.

## Suggested Database

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

CREATE INDEX idx_billing_orders_user ON billing_orders (user_id, created_at DESC);

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

Khong bat buoc tao bang `user_entitlements` cho SocialGraph. Neu Billing van muon luu lich su quyen loi noi bo, bang do chi la state rieng cua Billing.

## SocialGraph API Billing Can Goi

### Set User Verify

```http
PUT /internal/users/{userId}/verify
X-Gateway-Secret: <shared secret at least 32 bytes>
X-Correlation-ID: <trace id>
Content-Type: application/json
```

Body cap/gia han:

```json
{
  "expiresAt": "2026-08-10T00:00:00Z"
}
```

Body thu hoi/clear:

```json
{
  "expiresAt": null
}
```

Rules:

- Billing khong ghi truc tiep database SocialGraph.
- Billing phai dung shared secret trung voi `Gateway:InternalSharedSecret` cua SocialGraph.
- Billing chi goi endpoint nay sau khi order da paid hoac khi can thu hoi verify.
- `expiresAt` nen gui dang UTC ISO-8601.
- SocialGraph se tu tinh `isVerified = verify > now`.
- Missing/wrong secret tra `403`; SocialGraph chua cau hinh secret hop le tra `503`.

## APIs For Frontend/Gateway Later

- `POST /billing/orders`: create an order for `verified_18k`.
- `POST /billing/orders/{id}/mock-paid`: development-only endpoint to mark an order paid.
- `POST /billing/webhook`: real payment provider callback later.

When an order becomes paid, Billing computes `verify_expires_at` and calls `PUT /internal/users/{userId}/verify`.
