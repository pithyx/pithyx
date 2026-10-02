-- The SFTP views of 0017 set the mode of their mount points on every run after the first, which changed the root of
-- every shared folder offered over SFTP to 0755: the ACL mask lost its write bit and others could read. Forgetting
-- the ACL last set makes the daemon set every share's ACL again at its next start, which repairs them.
UPDATE shares SET acl = NULL;
