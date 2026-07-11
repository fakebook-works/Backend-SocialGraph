-- ** Notification action type ** --
------------------------
-- 0 friend_request -> mutation
-- 1 friend_accept <- gọi API thông báo friend

-- 2 group_invite -> mutation
-- 3 group_join-> mutation
-- 4 group_accept <- gọi API thông báo member

-- 5 comment <- gọi API thông báo comment
-- 6 like <- gọi API thông báo like
-- 7 mention <- gọi API thông báo mention
-- 8 tag <- gọi API thông báo tag

-- ** Notification table ** --
-----------------------
CREATE TABLE notification (
id bigint PRIMARY KEY,
creator_id bigint NOT NULL,
reciver_id bigint NOT NULL,
action_type smallint NOT NULL,
object_id bigint,
created_at timestamptz NOT NULL DEFAULT now(),
is_read boolean NOT NULL DEFAULT false,
data jsonb
);