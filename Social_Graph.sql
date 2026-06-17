CREATE SCHEMA IF NOT EXISTS fb;
SET search_path TO fb;

-- ** Social Graph object ** --
-------------------------------
-- 0  user {first_name, last_name, nickname, bio, gender, birthdate, location}
-- 1  group {name, privacy}

-- 2  post {content}
-- 3 reel {content}
-- 4 story {expire}
-- 5  comment {content}

-- 6  media {type, media_id}
-- 7 album {title, description}

-- ** Social Graph association ** --
------------------------------------
-- 0  friend (user<->user)

-- 1  followed (user->user)
-- 2  followed_by (user<-user)

-- 3  reacted (user->post/comment/reel/story)
-- 4  reacted_by (post/comment/reel/story->user)

-- 5  authored (user->post/comment/reel/story)
-- 6  authored_by (post/comment/reel/story->user)

-- 7  comment (post/reel/story/comment->comment)
-- 8  share (post/story->post/reel)

-- 9  published (group->post)
-- 10  published_in (post->group)

-- 11  tagged (post->user)
-- 12  tagged_in (user->post)

-- 13  member (user->group)
-- 14  have_member (group->user)

-- 15 admin (user->group)
-- 16 have_admin (group->user)

-- 17 watched (user->reel/story)
-- 18 watched_by (reel/story->user)

-- 19 saved (user->post/reel)

-- 20 owned (user/group->album)
-- 21 contained (post/reel/story/abum->media)

-- 22 mentioned (post/reel/story/comment->user)

-- ** Social Graph table ** --
------------------------------
CREATE TABLE Objects (
    id BIGINT PRIMARY KEY,
    otype SMALLINT NOT NULL,
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
CREATE INDEX idx_associations ON Associations (id1, atype, id2);
CREATE INDEX idx_associations_inverse ON Associations (id2, atype, id1);

CREATE TABLE Association_Counts (
    id1 BIGINT NOT NULL,
    atype SMALLINT NOT NULL,
    count INT NOT NULL DEFAULT 1,
    PRIMARY KEY (id1, atype)
);

