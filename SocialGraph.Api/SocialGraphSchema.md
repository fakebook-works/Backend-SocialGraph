CREATE SCHEMA IF NOT EXISTS Social_Graph;
SET search_path TO Social_Graph;

-- ** Social Graph object type & data ** --
-------------------------------------------
-- 0  user {avatar: Url 1, avatarSource: {contentId: SnowflakeString, mediaId: SnowflakeString},
background: Url 1, name: String 1, bio: String 1, gender: Short(0/1) 1, birthdate: DateOnly 1,
location: String 1, verify: DateTime 0, privacy: Short(0/1) 1, create: DateTime}

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
      
-- 4  reel {content: String 1, privacy: Short(0/1/2/3) 1, create: DateTime, aspectRatio: Float 0, focalPointX: Float 0, focalPointY: Float 0}

    privacy giống hệt post feed: 0 public, 1 friends and follow, 2 friends only, 3 private (chỉ mình tôi)
    aspectRatio là tỉ lệ khung trình bày tuỳ chọn trong khoảng 9/16..16/9. focalPointX/focalPointY là tâm vùng
    trình bày chuẩn hoá trong [0,1], mặc định 0.5/0.5. Ba giá trị này lưu cách người tạo căn/crop Reel để mọi
    lần render dùng lại đúng vùng đã chọn; video upload gốc không bị cắt hoặc mã hoá lại. Reel cũ không có metadata
    này vẫn dùng tỉ lệ media thích ứng và tâm giữa như trước.
-- 5  story {content: String, create: DateTime, expire: DateTime}
-- 6  comment {content: String, create: DateTime}; comment may contain at most one image through association 28.
Comment pages return direct children for lazy expansion, per-comment like/reply counts, and batched viewer-relative
`canFollowAuthor` / `isFollowingAuthor` state. The post/Reel engagement `commentCount` includes every descendant reply.

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
    * khi quản trị viên duy nhất rời nhóm, server tự chọn thành viên hiện tại vào sớm nhất theo
      `Member.time ASC`, rồi `userId ASC`; client không được chỉ định người kế nhiệm
    * promote người kế nhiệm và xoá Admin/Member/Visited của người rời nằm trong cùng transaction;
      pending request không bao giờ là ứng viên, không còn ứng viên thì toàn bộ thao tác bị từ chối
    * gỡ quyền quản trị dùng transaction Serializable và advisory lock theo group; chỉ được gỡ khi
      còn quản trị viên khác, giữ nguyên cạnh Member và không thể để nhóm có 0 quản trị viên
    * deleteGroup chỉ nhận groupId công khai nhưng actor được lấy từ trusted Gateway context; backend
      chỉ xoá khi actor vừa là Admin vừa là Member cuối cùng hiện tại của group; kiểm tra và xoá local
      chạy trong transaction Serializable với cùng advisory lock theo group, cleanup ngoài service chạy sau commit

-- 17  group_join_request (user->group) -- 17 18
-- 18  have_group_join_request (group->user)

    user1 -(17)-> group1: user1 đã gửi yêu cầu tham gia những group nào
    group1 -(18)-> user1: group1 nhận được yêu cầu tham gia từ những user nào
    * cả group công khai lẫn riêng tư đều tạo request và chỉ thành member sau khi admin duyệt;
      privacy của group chỉ quyết định quyền đọc nội dung, không tự động cấp membership
    * pendingGroupJoins đọc cạnh 17 từ phía user; groupJoinRequests đọc cạnh inverse 18 từ phía group,
      chỉ admin hiện tại được gọi, trả UserSummary đã lọc với page tối đa 50 thay vì raw association

-- 19  watched (user->reel/story) -- 19 20
-- 20  watched_by (reel/story->user)

    user1 -(19)-> reel1: user1 đã xem những reel/story nào
    reel1 -(20)-> user1: reel1 được những ai xem (dùng để đếm số lượt xem)

-- 21  have_comment (post/reel/comment->comment)
-- 22  comment (comment->post/reel/comment)

    post1 -(21)-> comment1: post1 có những comment nào
    comment1 -(22)-> post1: comment1 thuộc post/reel/comment nào

-- 23  share (post feed/post group->group/post feed/post group/reel; story->post feed/reel)
-- 24  shared_by (inverse của share)

    postfeed1 -(23)-> postgroup1: post feed chia sẻ bài trong group
    postgroup2 -(23)-> group1: bài đăng trong group chia sẻ thẻ giới thiệu một group
    * source của post share luôn được chuẩn hoá về đối tượng gốc: wrapper FeedPost/GroupPost đã có cạnh Share được bóc tới nguồn cuối, không tạo chuỗi wrapper lồng nhau
    * SharePostInput.destinationGroupId là optional. Khi có, resolver bắt buộc actor hiện tại là member/admin của group đích và tạo GroupPost wrapper; khi không có thì tạo FeedPost wrapper với privacy 0/1/2/3
    * cả create GroupPost thường và share vào group đều kiểm tra lại Member/Admin trong transaction, giữ row lock trên cạnh membership tới commit; thao tác rời/xoá thành viên đồng thời không thể chen giữa policy check và cạnh Published
    * projection SharedPostSource trả privacy/create và metadata group an toàn. Reel source khả dụng trả thêm aspectRatio/focalPointX/focalPointY đúng bản cắt trình bày của bài gốc; mutation share không ghi đè các giá trị này. Nguồn bị xoá, bị block, ngoài privacy hoặc ngoài group riêng tư luôn trả ba metadata trình bày là null. GroupPost riêng tư chỉ trả full content/author/media/mentions khi viewer hiện là member/admin; người ngoài chỉ nhận metadata group tối thiểu cùng requiresGroupMembership, tuyệt đối không nhận trường nội dung được bảo vệ
    * source loại Group chỉ trả thẻ group (bìa/avatar/tên/privacy/số thành viên/trạng thái viewer), không giả lập author bài viết
    * mutation lấy actor từ trusted Gateway context rồi kiểm tra privacy 0/1/2/3 và block hai chiều ở thời điểm ghi; input authorId không cấp quyền
    * mỗi lần đọc wrapper đều kiểm tra lại quyền của chính viewer với source; privacy của wrapper không mở rộng quyền source. Story vẫn chỉ nhận FeedPost/Reel và đi qua CanShareStoryTargetAsync riêng, nên Group/GroupPost không thể lách union Story

-- 25  tagged (post feed/post group->user)

    post1 -(25)-> user1: post feed hoặc post group tag những ai
    * không cần inverse vì không cần biết user1 được tag bởi những post nào
    * với post group, user được tag/mention phải đồng thời là bạn hiện tại của author và member/admin của đúng group; block hai chiều luôn thắng

-- 26  mentioned (post/reel/story/comment->user)

    post1 -(26)-> user1: post1 mention những ai
    * không cần inverse vì không cần biết user1 được mention bởi những post/reel/story

-- 27 saved (user->post/reel)

    user1 -(27)-> post1: user1 đã lưu những post/reel nào
    * không cần inverse vì không cần biết post1 được lưu bởi những user nào

-- 28 contained (post/reel/story/comment->media; comment accepts one image only)

    post1 -(28)-> media1: post1 có những media nào
    * không cần inverse vì không cần biết media1 thuộc về những post/reel/story nào


-- 29 visited (user->group)
 
    user1 -(29)-> group1: user1 đã ghé thăm những group nào
    * không cần inverse vì không cần biết group1 được những user nào ghé thăm
    * visitedGroups trả `visitedAt` từ `Associations.time` để frontend hiển thị lần truy cập gần nhất
    * leaveGroup thành công xoá Visited của chính caller cùng transaction với Member/Admin và thao tác
      chuyển quyền quản trị viên duy nhất (nếu cần); leave bị từ chối không thay đổi Visited
    * khi quản trị viên xoá một thành viên không phải admin, server kiểm tra lại quyền admin dưới
      transaction Serializable/advisory lock của group rồi xoá Member/HaveMember cùng Visited của
      đúng cặp user-group; Visited của user tới group khác và của user khác không bị ảnh hưởng


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
updatePost(input: { id, privacy?, content?, media? }) áp dụng cho feed post, group post và reel; feed post/reel dùng cùng privacy 0/1/2/3. Field bị omit được giữ nguyên; media=[] detach toàn bộ và garbage-collect media không còn parent.
Home post candidates gồm feed post, group post và reel. Reel được hydrate thành `ReelDetail` trong union `HomePost` và frontend dùng chung card hiển thị với feed post. `createReel` nhận `aspectRatio` trong khoảng 9/16..16/9 cùng `focalPointX`/`focalPointY` trong [0,1]; `ContentResult` và `ReelDetail` trả lại đủ metadata trình bày này. Client cũ có thể bỏ qua focal point và sẽ được căn giữa.
Fast-search hydration không nhận `viewerId` từ input. `UserSearchResult` trả `viewerIsSelf`,
`viewerIsFriend` và `viewerIsFollowing`; `GroupSearchResult` trả `viewerIsMember` (member hoặc admin), đều được tính từ
trusted Gateway caller và association hiện tại.
`groupSuggestions(limit)` lấy viewer từ trusted Gateway accessor, xếp hạng các group theo số bạn bè hiện tại
đang là member/admin và trả metadata của cả group công khai lẫn riêng tư. Query loại group viewer đã tham gia,
quản trị hoặc đang chờ duyệt và lọc nguồn bạn bè bị block hai chiều. Mỗi kết quả gồm `group`, tổng số bạn bè
thành viên distinct, tối đa ba preview chỉ có `id/name/avatar`, và tổng GroupPost còn tồn tại được `Published`
trong khoảng UTC `[00:00 hôm qua, 00:00 hôm nay)`. Đây là projection tổng hợp có giới hạn: nó không cấp quyền
đọc nội dung post riêng tư và không lộ phần còn lại của member list nhóm riêng tư.
`groupFriendMembers(groupId, limit)` là projection đích danh cho header profile nhóm. Viewer luôn lấy từ trusted
Gateway context; kết quả clamp tối đa 12 và chỉ là giao của Friend hiện tại với Member/Admin hiện tại của đúng
group, sau khi lọc block hai chiều và user đã xoá. Query vẫn dùng được khi group riêng tư có request chờ duyệt,
nhưng không trả người lạ hay phần còn lại của roster riêng tư.
`profilePosts(userId, cursor, limit)` là luồng authored hợp nhất của tab Tất cả trên profile: trả cả
`FeedPostDetail` và `ReelDetail` theo thứ tự association; `GroupPostDetail` luôn bị loại và chỉ được đọc
qua query có scope group (`groupPosts`/`groupUserPosts`). `profileReels` vẫn chỉ trả Reel cho tab riêng.
Viewer luôn lấy từ trusted Gateway accessor; target `userId` không phải caller identity. Resolver chặn block
hai chiều trước khi đọc và toàn bộ item tiếp tục đi qua `ContentGraphService.GetPostDetailsAsync`, nên privacy
0/1/2/3, trạng thái xoá và block hiện tại vẫn được áp dụng trước khi item xuất hiện.
Mỗi bucket của `homeStories` trả thêm `unseenCount`, được tính chính xác từ các story chưa có association `Watched` của viewer; `hasUnseen` tương đương `unseenCount > 0`.
userPhotos(userId, cursor, limit) lấy ảnh từ feed post của user mà viewer được xem.
groupPhotos(groupId, cursor, limit) chỉ lấy ảnh từ group post của group mà viewer được xem.
groupMedia(groupId, cursor, limit) lấy cả ảnh và video từ group post mà viewer được xem; file/audio/link không xuất hiện.
groupUserPhotos(groupId, userId, cursor, limit) lấy ảnh từ group post do user tạo trong group.
myFeedPhotoCandidates/groupPhotoCandidates là nguồn ảnh hợp lệ để chọn avatar/background.
Ảnh đại diện user được lưu từ bản cắt; nếu người dùng tải file mới thì ảnh gốc tạo một feed post công khai
(`privacy=0`) với nội dung `đã cập nhật ảnh đại diện`. Chọn lại ảnh đã có không tạo activity post mới.
`avatar` luôn là URL ảnh vuông sạch. `avatarSource` là object nullable trong `User.data`, gồm
`contentId` và `mediaId` được lưu dưới dạng chuỗi thập phân Snowflake để không bị JavaScript làm tròn.
Hai ID phải cùng tồn tại và chỉ là metadata truy nguồn, không phải quyền truy cập. Khi ghi ảnh có sẵn,
backend lấy actor từ trusted accessor rồi xác minh feed post thuộc actor, media thực sự được `Contained`
trong post và có type Photo. Khi upload mới, backend tạo activity post công khai, lấy chính ID post/media
vừa tạo rồi ghi cùng avatar trong một transaction local. Khi xóa/đổi avatar không có nguồn, `avatarSource`
được đặt null; user cũ không có field vẫn hợp lệ và xem avatar độc lập.

`profileAvatarSource(userId)` là read model viewer-aware. Resolver áp lại block hai chiều và gọi normal
post-detail visibility path để kiểm tra privacy/trạng thái xóa cùng quan hệ author/content/media trước khi
trả IDs. Nguồn không tồn tại, bị ẩn, đổi privacy, sai owner/media hoặc viewer không còn quyền đều trả null;
frontend không dò nguồn bằng URL hay nội dung `đã cập nhật ảnh đại diện` và không gọi thẳng service.
Ảnh bìa user được lưu từ bản cắt; nếu người dùng tải file mới thì ảnh gốc tạo một feed post công khai
(`privacy=0`) với nội dung `tôi đã cập nhật ảnh bìa của mình`. Chọn lại ảnh đã có không tạo activity post mới.
groupUserPosts(groupId, userId, cursor, limit) áp dụng group privacy và block/content visibility.
likedReels/sharedReels/watchedReels(cursor, limit) luôn lấy viewer từ trusted gateway header.
removeUserAvatar/removeUserBackground/removeGroupAvatar/removeGroupBackground đặt URL thành chuỗi rỗng với owner/admin authorization.
inviteGroupUser yêu cầu người mời là member/admin hiện tại và target là bạn hiện tại, áp dụng block hai chiều,
chỉ gửi notification action 6 và không tự thêm member; share feed/story gửi action 9 cho source author và bỏ qua self-notify.

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

Profile collections dùng `profileConnections(userId, associationType, limit)` để tải danh sách hiển thị ban đầu cho association `0/3/4`. Việc nhập từ khóa không được xử lý tại SocialGraph GraphQL: SearchService gọi REST nội bộ `GET /internal/users/{userId}/profile-connection-ids?associationType=0|3|4`, giới hạn search trong tập ID đó, rồi Gateway Fusion hydrate profile. Frontend chỉ gọi GraphQL qua Gateway.

Profile của người khác dùng hai read model target-scoped riêng:
- `profileFriends(targetUserId, limit)` lấy viewer từ trusted Gateway accessor; `targetUserId` chỉ là resource ID. Resolver kiểm tra block hai chiều giữa viewer-target, giới hạn kết quả tối đa 200, rồi tiếp tục lọc block hai chiều giữa viewer và từng người bạn. `mutualFriendCount` luôn được tính so với viewer thật, không phải target.
- `profileContact(userId)` chỉ trả `{ email }` khi viewer đã xác thực, canonical SocialGraph profile còn tồn tại, không có block theo cả hai chiều và tài khoản Auth đang active. Email được đọc bằng signed internal REST có timestamp/nonce Redis fail-closed; browser không gọi Auth trực tiếp và không có credential, hash, token hay session field nào đi qua contract này.

Hai query trên không nới `profileConnections`: API đó vẫn caller-owned và vẫn yêu cầu `userId` trùng trusted actor. Vì vậy input ID không thể được dùng để giả mạo caller.

Feed post detail trả thêm `taggedUsers { id name avatar isVerified }`. Danh sách này được hydrate cùng batch association/object của trang feed, không gọi query riêng cho từng post.
Group post detail cũng trả `taggedUsers { id name avatar isVerified }`. Khi tạo group post, cả
`taggedUserIds` lẫn user ID derive từ mention token đều phải vừa là bạn của author vừa là
member/admin hiện tại của group; lỗi dùng thông báo chung để không làm lộ friend/member/block state.

`deleteContent(contentId)` cho phép author xoá nội dung của mình. Ngoại lệ duy nhất là GroupPost:
admin hiện tại của đúng group lấy từ cạnh `PublishedIn` cũng có thể xoá. FeedPost/Reel/comment/story
không thể có thêm quyền xoá nhờ association Admin ở một group bất kỳ.

