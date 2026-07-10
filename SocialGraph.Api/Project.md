* Hệ thống Microservice giao tiếp bằng GrapQL Federation và RestAPI (giao tiếp giữa các service với nhau)
- Frontend (ReactJS, NextJS, React Native)
- API Gateway (GraphQL Federation)
- Service (SocialGraph (service hiện tại), Notification (để SocialGraph gọi để tạo thông báo đồng thời cũng đẩy thông báo cho front)
- , Authentication, Messenger, Search (tạo chỉ mục cho user ,group, post), Recommendation (tạo và lưu embeding cho post reel cùng với id, embeding user. tạo ra list bài đăng bằng cách nhân tích vô hướng))


* Viết chức năng tạo Snowflake ID tại ultils
  
* Các hàm trong service folder
Object: (id, otype, data) (lưu ở db)

Object: (id) → {"otype":, "data": {}} (lưu ở redis) 
key: id (only) 
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

Association: (id1, atype, id2, time) (lưu ở db)

Association list: (id1, atype) → ((id2_old, time_old),...(id2_new, time_new)) (lưu ở redis)
key: id:atype
value: sorted set (id2, time) (time là score)

Định nghĩa các atype và inverse atype của chúng (atype, inverse atype):
- (0, 0) (1, 2) (3, 4) (5, 6) (9, 10) (11, 12) (13, 14) (15, 16) (17, 18)
- chỉ các atype 0, 1, 3, 5, 9, 11, 13, 15, 17 sẽ tạo thêm inverse association với inverse atype tương ứng chứ không phải ngược lại

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

* countAssociation(id1, atype)
- kiểm tra redis key id1:atype có tồn tại ko, nếu có trả về số lượng element trong zset, nếu không truy vấn db lưu list id2 vào redis với score là time, 
sau đó trả về số lượng element trong zset

* retrieveAssociation (id1, atype, ?, limit)
- kiểm tra redis key id1:atype có tồn tại ko, nếu có trả về list id2 theo limit, nếu không truy vấn db lưu list id2 vào redis với score là time,
sau đó trả về list id2 theo limit (dùng cơ chế cursor pagination trên cache zset), nếu truy vấn db mà không có gì thì vẫn lưu vào redis với zset rỗng 

Dùng các hàm trên để viết API dưới đây, ngoài ra gọi thêm Rest API của các serivce khác 
GraphQL
Mutation
- CreateUser (string name, bool gender, DateOnly birthday, sting location, string email, string password) 
  bio mặc định "Xin chào, mình là [name] đến từ [location]",
  privacy mặc định là 0 (normal),
  create lấy thời gian hiện tại,
  đóng gói data thành dạng json {name: string, bio: string, gender: 0/1, birthdate: dateOnly, location: sting, privacy: 0/1, create: dateTime}
  gọi hàm object add (0, data)
  gọi RecommendServiceCreateUserEmbedding API (user id), 
  gọi AuthenticationServiceCreateUser API (user id, password, email),
  gọi MessengerServiceCreateUser API (user id), 
  gọi SearchServiceCreateIndex API (user id, name

  
- UpdateUser ( id, updateData)
  nếu đổi tên thì gọi API SearchServiceUpdateIndex (user id, name)
  
 (dùng hàm update Object như bthg, nếu sửa tên gọi "SearchServiceUpdateIndex" API (user id, name))
- xoá user (dùng hàm delete Object, gọi "SearchServiceDeleteIndex" API (user id), "MessengerServiceDeleteUser" API (user id), "AuthenticationServiceDeleteUser" API (user id),
"RecommendServiceDeleteUserEmbedding" API (user id))
- upload file do media/upload service hoặc frontend flow bên ngoài xử lý; SocialGraph chỉ nhận URL đã upload xong
- đổi avt bằng ảnh upload (truyền vào url ảnh gốc và url ảnh sau khi crop; url gốc tạo object media và asso owned, url crop lưu trực tiếp vào data user)
- đổi avt bằng ảnh đã có (truyền vào url ảnh sau khi crop; không tạo thêm media, chỉ patch avatar trong data user)
- tạo group (gọi "SearchServiceCreateIndex" API (group id, name), dùng hàm add Object và tao asso admin trỏ đến user tạo group)
- sửa group (dùng hàm update Object như bthg, nếu sửa tên gọi "SearchServiceUpdateIndex" API (group id, name))
- xoá group (dùng hàm delete Object, gọi "SearchServiceDeleteIndex" API (group id))
- đổi avt group hiện chỉ truyền vào url ảnh sau khi crop và patch trực tiếp vào data group
- nếu sau này cần lưu ảnh gốc group avatar thì mở rộng giống user avatar, tạo media và owned với owner là group id
- thêm thành viên group (thêm asso member trỏ đến user)
- xoá thành viên (xoá asso member trỏ đến user)
- thêm quản trị (xoá asso member, tạo asso admin))
- tạo post feed (truyền vào list URL media đã upload kèm type của chúng, lần lượt tạo object media cho từng URL, tạo object post và tạo asso contain đến các media vừa tạo, tạo asso authored từ user trỏ đến)
gọi SearchServiceCreateIndex API (post id, content), gọi RecommendServiceCreatePostEmbedding API (post id, content, danh sách URL)
- tạo post group (truyền vào list URL media đã upload kèm type của chúng, lần lượt tạo object media cho từng URL, tạo object post và tạo asso contain đến các media vừa tạo, tạo asso authored từ user trỏ đến, tạo asso published từ group trỏ vào)
gọi SearchServiceCreateIndex API (post id, content), gọi RecommendServiceCreatePostEmbedding API (post id, content, danh sách URL)
- sửa post (privacy) 
- xoá post ( xoá post kèm asso kèm theo gọi RecommendServiceDeletePostEmbedding API (post id), gọi SearchServiceDeleteIndex API (post id))
- tạo story
- xoá story
- tạo reel
- xoá reel
- xem story, reel
- lưu post, reel
- mention
- tag
- follow

- like post, story, reel
- comment post, story, reel
- share post, reel lên post


Query
- lấy Story
- lấy profile

- lấy list reel đã lưu
- lấy list reel đã react
- lấy list reel đã xem
- lấy list reel đã share

- lấy list friend gợi ý * có thể gợi ý bạn của bạn 
- lấy list friend
- lấy list block

- lấy list group gợi ý
- lấy list group
- lấy data object
- lấy list comment
- lấy list like
- lấy list share
- lấy list member
- lấy list admin
- lấy count association

RestAPI
- lấy candidate post id để recommendation service gọi (chỉ id)
- lấy candidate reel id để recommendation service gọi (chỉ id)

* cần thêm cơ chế trả 18.000 để có tích xanh
* cần thêm cơ chế trả 36.000 thì bài đăng của user đó sẽ có tỉ lệ xuất hiện nhiều hơn trên feed người khác 
