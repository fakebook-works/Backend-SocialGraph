# Billing Service Contract

SocialGraph does not own billing data. It only calls Billing to read active entitlements.

## Products

- `verified_18k`: price `18000`, grants entitlement `verified` for 30 days.
- `feed_boost_36k`: price `36000`, grants entitlement `feed_boost_author` for 30 days.

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
    metadata JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_billing_orders_user ON billing_orders (user_id, created_at DESC);

CREATE TABLE user_entitlements (
    id BIGINT PRIMARY KEY,
    user_id BIGINT NOT NULL,
    type TEXT NOT NULL,
    source_order_id BIGINT REFERENCES billing_orders(id),
    starts_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at TIMESTAMPTZ,
    metadata JSONB NOT NULL DEFAULT '{}'
);

CREATE INDEX idx_user_entitlements_active
    ON user_entitlements (user_id, type, expires_at);
```

## APIs Needed By SocialGraph

### Get Active Entitlements

`GET /internal/billing/entitlements?userId={userId}`

Response:

```json
{
  "entitlements": [
    {
      "type": "verified",
      "expiresAt": "2026-08-09T00:00:00Z",
      "metadata": {}
    },
    {
      "type": "feed_boost_author",
      "expiresAt": "2026-08-09T00:00:00Z",
      "metadata": {
        "boostMultiplier": "1.3"
      }
    }
  ]
}
```

SocialGraph fallback behavior: if this API is missing or fails, `isVerified=false` and `boostMultiplier=1.0`.

## APIs For Frontend/Gateway Later

- `POST /billing/orders`: create an order for `verified_18k` or `feed_boost_36k`.
- `POST /billing/orders/{id}/mock-paid`: development-only endpoint to mark an order paid.
- `POST /billing/webhook`: real payment provider callback later.

When an order becomes paid, Billing creates or extends the matching entitlement by 30 days.
