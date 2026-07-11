CREATE SCHEMA IF NOT EXISTS Social_Graph;
SET search_path TO Social_Graph;

-- ** Social Graph object ** --
-------------------------------
-- 0  user {avatar 1, background 1, name 1, bio 1, gender 1, birthdate 1, location 1, verify 0, privacy 1, create} 
--    verify la UTC ISO expiresAt cua tich xanh; 0 nghia la chi REST internal cua SocialGraph cho Payment/Billing duoc sua
mode: 0 normal (only friend relation), 1 advanced (friend and followed relation) gender: 0 male, 1 female
-- 1  group {avatar 1, background 1, name 1, bio 1, privacy 1, create}  privacy: 0 public, 1 private

-- 2  post feed {content, privacy 1, create};  privacy: 0 public, 1 friends
-- 3  post group {content, create}  
-- 4  reel {content, create}
-- 5  story {content, create, expire}
-- 6  comment {content, create}

-- 7  media {type, url} type: photo/video/audio/file/link
những trường đánh dấu 1 là có thể sửa đổi, còn lại thì ko, tất cả đều có chức năng xoá
-- ** Social Graph association ** --
------------------------------------
-- 0  friend (user<->user) -- 0 0

-- 1  followed (user->user) -- 1 2 
-- 2  followed_by (user<-user)

-- 3  liked (user->post/comment/reel/story) -- 3 4
-- 4  liked_by (post/comment/reel/story->user)

-- 5  authored (user->post/comment/reel/story) -- 5 6
-- 6  authored_by (post/comment/reel/story->user)

-- 7  comment (post/reel/story/comment->comment)
-- 8  share (new post feed/story-> shared post(privacy 0)/reel) 

-- 9  published (group->post group) -- 9 10
-- 10  published_in (post group->group)

-- 11  tagged (post feed->user) -- 11 12
-- 12  tagged_in (user->post feed)

-- 13  member (user->group) -- 13 14
-- 14  have_member (group->user)

-- 15 admin (user->group) -- 15 16
-- 16 have_admin (group->user)

-- 17 watched (user->reel/story) -- 17 18
-- 18 watched_by (reel/story->user)

-- 19 saved (user->post/reel)

-- 20 contained (post/reel/story->media)

-- 21 mentioned (post/reel/story/comment->user)

-- 22 owned (user/group->media)

-- 23 blocked (user->user) -- 23 24
-- 24 blocked_by (user<-user)

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

