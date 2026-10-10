-- D342: the firewall may limit the file services to private networks; off by default.
ALTER TABLE file_services ADD COLUMN private_networks_only INTEGER NOT NULL DEFAULT 0 CHECK (private_networks_only IN (0, 1));
