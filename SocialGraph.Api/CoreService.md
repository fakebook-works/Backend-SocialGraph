Định nghĩa cách các Object và Association được lưu trữ trong cơ sở dữ liệu và Redis
Đồng thời định nghĩa logic các service cơ bản để thao tác với Object và Association
Những service này được dùng để xây dựng những chức năng cụ thể hơn 

Object: (id, otype, data) database

Object: (id) → {"otype":, "data": {}} redis
key: socialgraph:v2:object:{id}
value: ReJSON-RL {"otype":, "data": {}}
vd:
"1234567890123456789":
{
"otype": 0,
"data": {
"avatar": "https://example.com/avatar.jpg",
"name": "John Doe",
"bio": "Hello, I'm John Doe from New York.",
"gender": 1,
"birthdate": "1990-01-01",
"location": "New York",
"privacy": 0,
"create": "2024-06-01T12:00:00Z"
}
}

* addObject (short otype, string data (dạng json chuẩn cấu trúc của otype))
- id = IdGenerator.GenerateId()
- tạo object trong db (id, otype, data)
- ghép otype và data thành json {"otype":, "data": {}} (dạng như ví dụ trên)
- dùng jsonDb.Set lưu object vào redis với key: id và value: {"otype":, "data": {}}

* updateObject (long id, short otype, sting patchUpdate (dạng json chứa các trường muốn thay của loại otype))
- lấy những trường cần thay đổi ở patchUpdate
- dùng id, short otype map dữ liệu để dùng chức năng tự viết câu sql cập nhật một phần chuỗi json trong db (chỉ cập nhật những trường được chỉ định trong patchUpdate)
- kiểm tra redis có tồn tại key id ko, nếu có dùng jsonDb cập nhật những trường được chỉ định trong patchUpdate, nếu không có thì bỏ qua

* deleteObject (long id)
- xóa object trong db
- kiểm tra redis có tồn tại object với id đó ko, nếu có xóa nó đi


* retrieveObject (long id)
- check redis nếu có trả về object id otype data, nếu không có check db
- nếu có trong db nạp vào redis và trả về object,
  nếu không có trả về null

Association: (id1, atype, id2, time) database

Association list: (id1, atype) → ((id2_old, time_old),...(id2_new, time_new)) redis
key: socialgraph:v2:association:{id1}:{atype}
value: sorted set (id2, time) (time là score)

Định nghĩa các atype và inverse atype của chúng (atype, inverse atype):
- (0, 0) (1, 2) (3, 4) (5, 6) (7, 8) (9, 10) (11, 12) (13, 14) (15, 16) (17, 18) (19, 20) (21, 22) (23, 24)
- inverse map hoạt động hai chiều: gọi service bằng forward hay inverse atype đều duy trì đúng cạnh đối ứng. Các atype 25..30 là một chiều và không tự tạo inverse.
- association được kiểm tra loại object ở cả hai đầu trước khi ghi; id không tồn tại, cùng id hoặc sai loại sẽ bị từ chối.
- các chuyển đổi nhiều cạnh (accept friend, block, approve member, promote admin) dùng một transaction.

* addAssociation (long id1, short atype, long id2)
- tạo association trong db (id1, atype, id2, time)
- tạo inverse association trong db (id2, inverse atype, id1, time) nếu có dạng inverse
- kiểm tra redis có tồn tại association list với key là id1:atype ko, nếu có thêm id2 vào list với score là time,
  nếu không có truy vấn table association dùng id1 và atype lấy list id2 cho vào redis với score là time
- nếu có dạng inverse kiểm tra redis có tồn tại association list với key là id2:inverse atype ko, nếu có thêm id1 vào list với score là time,
  nếu không có truy vấn table association dùng id2 và inverse atype lấy list id1 cho vào redis với score là time

* deleteOneAssociation (id1, atype, id2)
- xoá association trong db (id1, atype, id2)
- xoá inverse association trong db (id2, inverse atype, id1) nếu có dạng inverse
- kiểm tra redis có tồn tại association list với key là id1:atype ko, nếu có xóa id2 khỏi list
- nếu có dạng inverse kiểm tra redis có tồn tại association list với key là id2:inverse atype ko, nếu có xóa id1 khỏi list

* deleteAllAssociation (id1, atype)
- xoá tất cả association trong db có id1 = id1, atype = atype, nếu có inverse lưu list id2 lại
- xoá tất cả association trong db có atype = inverse atype, id2 = id1 nếu có dạng inverse
- kiểm tra redis có tồn tại association list với key là id1:atype ko, nếu có xóa toàn bộ list
- nếu có dạng inverse lần lượt kiểm tra tất cả association list với key id2:inverse atype (id2 thuộc list id2 ở trên) có tồn tại trong redis ko, nếu có xoá id1 khỏi list

Redis là cache tùy chọn. Kết nối dùng `AbortOnConnectFail=false`; lỗi kết nối, timeout hoặc thiếu module RedisJSON sẽ fallback sang PostgreSQL cho read và bỏ qua cache write. Mutation database đã thành công không bị báo lỗi chỉ vì cache đang down. `/health/ready` chỉ fail khi PostgreSQL không sẵn sàng; Redis ở chế độ fallback vẫn được coi là ready.

Các projection/call sang Authentication, Search, Recommendation, Notification và Messaging được ghi vào `social_graph.integration_outbox`. Worker dùng `FOR UPDATE SKIP LOCKED`, idempotency key, exponential backoff, stale-lock recovery và dead-letter. User create/update/delete dùng chung transaction PostgreSQL cho domain + outbox; các flow content/group/relationship hiện enqueue ngay sau domain write và vẫn có crash window nhỏ do transaction boundary cũ. Dead-letter được xem tại `GET /internal/outbox/dead-letters` và replay bằng `POST /internal/outbox/{id}/retry`; cả hai bắt buộc internal secret. Payload tạo Auth user chứa password được AES-GCM encrypt, do đó không rotate key trước khi drain toàn bộ pending/dead-letter user-create event.

Các hàm Object/Association thô là primitive nội bộ để xây dựng business service và không được expose trong public GraphQL schema. Bảng mã legacy phải được chuyển bằng command trong `Migrations/README.md`; application startup không tự chạy migration.

* countAssociation(id1, atype)
- kiểm tra redis key id1:atype có tồn tại ko, nếu có trả về số lượng element trong zset, nếu không truy vấn db lưu list id2 vào redis với score là time,
  sau đó trả về số lượng element trong zset

* retrieveAssociation (id1, atype, ?, limit)
- kiểm tra redis key id1:atype có tồn tại ko, nếu có trả về list id2 theo limit, nếu không truy vấn db lưu list id2 vào redis với score là time,
  sau đó trả về list id2 theo limit (dùng cơ chế cursor pagination trên cache zset), nếu truy vấn db mà không có gì thì vẫn lưu vào redis với zset rỗn
