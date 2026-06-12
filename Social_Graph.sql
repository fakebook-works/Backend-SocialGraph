CREATE SCHEMA IF NOT EXISTS fb;
SET search_path TO fb;

-- Object type codes (int)
-- 1  user
-- 2  page
-- 3  group

-- 3  post
-- 22 poll
-- 8  event
-- 4  comment
-- 24 reel
-- 12 story

-- 5  media
-- 13 album


-- 18 ad
-- 19 ad_account

-- 20 notification

-- Association type codes (int)
-- 1  friend
-- 2  follow
-- 3  like
-- 4  react
-- 5  comment_on
-- 6  post_to
-- 7  share
-- 8  tag
-- 9  member_of_group
-- 10 admin_of_group
-- 11 invited_to_event
-- 12 going_to_event
-- 13 interested_in_event
-- 14 message_to
-- 15 media_in_album
-- 16 page_follower
-- 17 blocks
-- 18 mute
-- 19 report
-- 20 save
-- 21 reply_to_comment
-- 22 mentioned_in
-- 23 attached_photo
-- 24 attached_video
-- 25 marketplace_saved
-- 26 follows_page
-- 27 joined_community
-- 28 react_to_comment
-- 29 review_of
-- 30 purchased




CREATE TABLE Objects (
    id BIGINT PRIMARY KEY,
    type SMALLINT NOT NULL,
    version SMALLINT NOT NULL DEFAULT 1,
    data JSONB
);

CREATE TABLE Associations (
    id1 BIGINT NOT NULL,
    atype SMALLINT NOT NULL,
    id2 BIGINT NOT NULL,
    time BIGINT NOT NULL,
    data JSONB,
    PRIMARY KEY (id1, atype, id2)
);
CREATE INDEX idx_associations_inverse ON Associations (id2, atype, id1);

CREATE TABLE Association_Counts (
    id1 BIGINT NOT NULL,
    atype SMALLINT NOT NULL,
    count INT NOT NULL DEFAULT 1,
    PRIMARY KEY (id1, atype)
);

