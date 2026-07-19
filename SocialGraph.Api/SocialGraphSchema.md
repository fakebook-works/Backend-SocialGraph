CREATE SCHEMA IF NOT EXISTS Social_Graph;
SET search_path TO Social_Graph;

-- ** Social Graph object type & data ** --
-------------------------------------------
-- 0  user {avatar: Url 1, background: Url 1, name: String 1, bio: String 1, gender: Short(0/1) 1,
birthdate: DateOnly 1, location: String 1, verify: DateTime 0, privacy: Short(0/1) 1, create: DateTime} 

    gender: 0 female, 1 male
    verify: thời gian hết hạn tích xanh, bình thường luôn null
    privacy: 0 normal (only friend relation), 1 advanced (friend and followed relation) 
    
-- 1  group {avatar: Url 1, background: Url 1, name: String 1, bio: String 1, privacy: Short(0/1) 1, create: DateTime}  
    
    privacy: 0 public (có thể xem post của group mà không tham gia), 1 private (phai tham gia group mới xem được post của group)

-- 2  post feed {content: String 1, privacy: Short(0/1/2/3) 1, create: DateTime};

    privacy: 0 public (ai cũng xem được), 1 friends and follow (bạn bè và người theo dõi mới xem được), 
    2 friends only (chỉ bạn bè mới xem được), 3 private (chỉ mình tôi mới xem được)

-- 3  post group {content: String 1, create: DateTime}

    privacy phụ thuộc vào group
      
-- 4  reel {content: String, create: DateTime}
-- 5  story {content: String, create: DateTime, expire: DateTime}
-- 6  comment {content: String, create: DateTime}

-- 7  media {type: Short(0/1/2/3/4), url: Url} 

    type: 0 photo, 1 video, 2 audio, 3 file, 4 link

những trường đánh dấu 1 là có thể sửa đổi qua GraphQL mutation
những trường đánh dấu 0 là có thể sửa đổi qua REST internal
những trường không đánh dấu là không được sửa đổi khi đã tạo

-- ** Social Graph association type ** --
-----------------------------------------
-- 0  friend (user<->user) -- 0 0 

    user1 -(0)-> user2: user1 có những ai là bạn
    user2 -(0)-> user1: user2 có những ai là bạn

-- 1 friend_request (user->user) -- 1 2
-- 2 have_friend_request (user<-user)

    user1 -(1)-> user2: user1 đã gửi lời mời kết bạn đến những ai
    user2 -(2)-> user1: user2 nhận được lời mời kết bạn từ những ai

-- 3  followed (user->user) -- 3 4
-- 4  followed_by (user<-user)   

    user1 -(3)-> user2: user1 đang theo dõi những ai
    user2 -(4)-> user1: user2 đang được những ai theo dõi mình

-- 5  blocked (user->user) -- 5 6
-- 6  blocked_by (user->user)

    user1 -(5)-> user2: user1 đã chặn những ai
    user2 -(6)-> user1: user2 bị những ai chặn

-- 7  liked (user->post/comment/reel/story) -- 7 8
-- 8  liked_by (post/comment/reel/story->user)

    user1 -(7)-> post1: user1 đã thích những post/comment/reel/story nào
    post1 -(8)-> user1: post1 được những ai thích

-- 9  authored (user->post/comment/reel/story) -- 9 10
-- 10  authored_by (post/comment/reel/story->user)

    user1 -(9)-> post1: user1 đã tạo những post/comment/reel/story nào
    post1 -(10)-> user1: post1 được tạo bởi ai

-- 11  published (group->post group) -- 11 12
-- 12  published_in (post group->group)

    group1 -(11)-> post1: group1 có những post nào
    post1 -(12)-> group1: post1 thuộc group nào

-- 13  member (user->group) -- 13 14
-- 14  have_member (group->user)

    user1 -(13)-> group1: user1 là thành viên của những group nào
    group1 -(14)-> user1: group1 có những thành viên nào

-- 15  admin (user->group) -- 15 16
-- 16  have_admin (group->user)

    user1 -(15)-> group1: user1 là quản trị của những group nào
    group1 -(16)-> user1: group1 có những quản trị nào

-- 17  group_join_request (user->group) -- 17 18
-- 18  have_group_join_request (group->user)

    user1 -(17)-> group1: user1 đã gửi yêu cầu tham gia những group nào
    group1 -(18)-> user1: group1 nhận được yêu cầu tham gia từ những user nào

-- 19  watched (user->reel/story) -- 19 20
-- 20  watched_by (reel/story->user)

    user1 -(19)-> reel1: user1 đã xem những reel/story nào
    reel1 -(20)-> user1: reel1 được những ai xem (dùng để đếm số lượt xem)

-- 21  have_comment (post/reel/comment->comment)
-- 22  comment (comment->post/reel/comment)

    post1 -(21)-> comment1: post1 có những comment nào
    comment1 -(22)-> post1: comment1 thuộc post/reel/comment nào

-- 23  share (post feed/story->post feed(privacy 0)/reel) 
-- 24  shared_by (post feed(privacy 0)/reel->post feed/story)

    postfeed1 -(23)-> postfeed2: post1 chia sẻ postfeed/reel nào
    postfeed2 -(24)-> postfeed1: postfeed2 được chia sẻ bởi những postfeed/story nào

-- 25  tagged (post feed->user)

    postfeed1 -(25)-> user1: postfeed1 tag những ai
    * không cần inverse vì không cần biết user1 được tag bởi những postfeed nào

-- 26  mentioned (post/reel/story/comment->user)

    post1 -(26)-> user1: post1 mention những ai
    * không cần inverse vì không cần biết user1 được mention bởi những post/reel/story

-- 27 saved (user->post/reel)

    user1 -(27)-> post1: user1 đã lưu những post/reel nào
    * không cần inverse vì không cần biết post1 được lưu bởi những user nào

-- 28 contained (post/reel/story->media)

    post1 -(28)-> media1: post1 có những media nào
    * không cần inverse vì không cần biết media1 thuộc về những post/reel/story nào


-- 29 visited (user->group)
 
    user1 -(29)-> group1: user1 đã ghé thăm những group nào
    * không cần inverse vì không cần biết group1 được những user nào ghé thăm


Association thể hiện mối quan hệ giữa 2 object
bên cạnh mỗi association chú thích mối quan hệ đó xuất hiện được giữa các loại object nào 
có 2 dạng post là feed và group post, những chỗ chú thích chỉ ghi post nghĩa là áp dụng cho cả 2 loại post
một số association có dạng inverse để phục vụ cho việc query ngược

block > friend > follow chỉ 1 cái được tồn tại 1 thời điểm
block là cao nhất chỉ có thể xoá bằng bỏ block
friend, follow sẽ bị xoá nếu bị block
không thể follow khi đã là friend hoặc block
đang follow vẫn có thể tạo friend request, nếu request đó bị từ chối thì giữ nguyên follow, mặt khác nếu được chấp nhận thì xoá follow tạo friend

-- ** Social Graph table ** --
------------------------------
CREATE TABLE Objects (
id BIGINT PRIMARY KEY,
otype SMALLINT NOT NULL,
data JSONB
);

CREATE TABLE Associations (
id1 BIGINT NOT NULL,
atype SMALLINT NOT NULL,
id2 BIGINT NOT NULL,
time BIGINT NOT NULL,
PRIMARY KEY (id1, atype, id2)
);
CREATE INDEX idx_associations ON Associations (id1, atype, id2);
CREATE INDEX idx_associations_inverse ON Associations (id2, atype, id1);

-- ** Typed GraphQL additions ** --
-----------------------------------
Không còn association Owned. Media graph chỉ tồn tại khi còn ít nhất một association Contained từ post/reel/story; detach parent cuối cùng sẽ xóa Media và asset tương ứng.
updatePost(input: { id, privacy?, content?, media? }) giữ nguyên field bị omit; media=[] detach toàn bộ và garbage-collect media không còn parent.
userPhotos(userId, cursor, limit) lấy ảnh từ feed post của user mà viewer được xem.
groupPhotos(groupId, cursor, limit) lấy ảnh từ group post của group mà viewer được xem.
groupUserPhotos(groupId, userId, cursor, limit) lấy ảnh từ group post do user tạo trong group.
myFeedPhotoCandidates/groupPhotoCandidates là nguồn ảnh hợp lệ để chọn avatar/background.
groupUserPosts(groupId, userId, cursor, limit) áp dụng group privacy và block/content visibility.
likedReels/sharedReels/watchedReels(cursor, limit) luôn lấy viewer từ trusted gateway header.
removeUserAvatar/removeUserBackground/removeGroupAvatar/removeGroupBackground đặt URL thành chuỗi rỗng với owner/admin authorization.
inviteGroupUser chỉ gửi notification action 6, không tự thêm member; share feed/story gửi action 9 cho source author và bỏ qua self-notify.

-- ** Mention trong content ** --
--------------------------------
Mention được lưu ngay tại đúng vị trí trong `content` bằng token `[[mention:<userId>]]`, ví dụ: `Chào [[mention:123]], bạn khỏe không?`.
`userId` phải là số nguyên dương hợp lệ trong miền BIGINT/Int64. Tên user không được lưu trong token hoặc content.
Frontend chỉ giữ `@từ-khóa` trong lúc đang tìm người dùng. Ngay khi chọn, editor bỏ ký tự `@`, hiển thị tên đậm tại đúng vị trí nhưng vẫn giữ `userId` trong draft; trước khi gửi, vùng tên đó được thay bằng token ID. Client mới không cần gửi `mentionedUserIds`; field cũ chỉ còn trong input để tương thích schema và backend không dùng nó làm nguồn tạo mention.

Association type 26 (`mentioned`) được derive từ token trong content:
- Khi tạo post/comment, backend parse token và tạo association cho các user ID hợp lệ.
- Khi sửa content post, backend đồng bộ association: thêm mention mới và xóa mention không còn trong content.
- Token trùng user ID chỉ tạo một association; token sai định dạng, bằng 0, số âm hoặc vượt miền Int64 bị bỏ qua.
- Token trong content là tham chiếu ổn định; association không phải nơi lưu snapshot tên hiển thị.

Read model của feed post, group post, shared source và comment trả thêm `mentions { userId name available }`.
`name` luôn là tên hiện tại tại thời điểm đọc, vì vậy user đổi tên thì lần đọc tiếp theo tự cập nhật mà không cần sửa content.
Nếu user đã bị xóa hoặc không còn khả dụng, token vẫn được giữ, read model trả `available: false`; client hiển thị nhãn fallback `Người dùng Fakebook` và không tạo liên kết profile.
Khi render, frontend bỏ ký tự `@`, hiển thị tên đậm và cho phép nhấn để mở `/profile/<userId>` nếu `available: true`.
Không cần migration hay bảng mới; cơ chế này tái sử dụng association type 26 hiện có.

Feed post detail trả thêm `taggedUsers { id name avatar isVerified }`. Danh sách này được hydrate cùng batch association/object của trang feed, không gọi query riêng cho từng post.

