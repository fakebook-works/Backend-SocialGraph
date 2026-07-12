# Completed API - Register

File nay ghi lai phan chuc nang dang ki da hoan thien.

## Chuc Nang: Dang Ki

Flow frontend:

1. Frontend goi Auth service de tao ma xac thuc email.
2. User nhap ma.
3. Auth verify ma thanh cong.
4. Frontend/Gateway goi SocialGraph `createUser`.
5. SocialGraph tao canonical user id va goi Auth tao identity bang dung id do.

Dang nhap khong nam trong SocialGraph; sau khi dang nhap Auth tra token/session va user id cho frontend/Gateway.

## API: createUser(input)

GraphQL mutation:

```graphql
mutation CreateUser($input: CreateUserInput!) {
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
  "input": {
    "name": "Tran Van A",
    "gender": true,
    "birthdate": "2001-01-01",
    "location": "Ha Noi",
    "email": "a@example.com",
    "password": "secret"
  }
}
```

Input khong nhan:

- avatar
- background
- user id

User id do SocialGraph tu sinh bang Snowflake ID. Avatar/background mac dinh rong va co API rieng de cap nhat sau.

## Logic Ben Trong

1. Validate input theo contract `CreateUserInput`.
2. Sinh Snowflake user id.
3. Tao data user:
   - `avatar = ""`
   - `background = ""`
   - `name = input.name`
   - `bio = "Xin chao, minh la {name} den tu {location}"`
   - `gender = input.gender`
   - `birthdate = input.birthdate`
   - `location = input.location`
   - `privacy = 0`
   - `create = now UTC`
   - `verify = ""`
4. Tao object user type `0` trong `objects`.
5. Goi Auth service tao identity bat buoc.
6. Neu Auth fail:
   - xoa object user vua tao;
   - tra `success = false`, `userId = null`, `message = error`.
7. Neu Auth thanh cong:
   - goi Search upsert user index theo best-effort;
   - goi Recommendation upsert user embedding theo best-effort;
   - Messenger create user dang tam disable.
8. Tra `CreateUserPayload`.

## External Calls

Auth service bat buoc:

```http
POST /internal/users
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <correlation id>
```

Body:

```json
{
  "userId": 123,
  "email": "a@example.com",
  "password": "secret",
  "displayName": "Tran Van A",
  "dob": "2001-01-01"
}
```

Search service best-effort:

```http
PUT /internal/search/indexes/123
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <correlation id>
```

Body:

```json
{
  "objectType": "user",
  "text": "Tran Van A"
}
```

Recommendation service best-effort:

```http
PUT /internal/recommendation/users/123/embedding
X-Gateway-Secret: <shared secret>
X-Correlation-ID: <correlation id>
```

## Output

Thanh cong:

```json
{
  "success": true,
  "userId": 123,
  "message": "User created."
}
```

Auth fail:

```json
{
  "success": false,
  "userId": null,
  "message": "Authentication user creation failed."
}
```

## Goi Tin Frontend/Gateway Gui

```json
{
  "operationName": "CreateUser",
  "query": "mutation CreateUser($input: CreateUserInput!) { createUser(input: $input) { success userId message } }",
  "variables": {
    "input": {
      "name": "Tran Van A",
      "gender": true,
      "birthdate": "2001-01-01",
      "location": "Ha Noi",
      "email": "a@example.com",
      "password": "secret"
    }
  }
}
```

## Ket Qua Cho Service Khac

- Auth coi `userId` SocialGraph gui sang la canonical user id.
- Search dung cung id de index user.
- Recommendation dung cung id de tao user embedding.
- Gateway/frontend luu user id Auth tra sau dang nhap va dung cho cac query can viewer.
