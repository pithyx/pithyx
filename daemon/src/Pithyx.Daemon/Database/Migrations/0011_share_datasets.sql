-- Shared folders as datasets (pulled forward from step 12, D101): a share created in Settings is its own dataset
-- <pool>/pithyx/shares/<name>, recorded here with a description. The folder shares of step 6 keep dataset NULL; they
-- exist only in development and are not migrated.

ALTER TABLE shares ADD COLUMN description TEXT NOT NULL DEFAULT '' CHECK (length(description) <= 200);

-- Full name of the share's dataset; it follows a rename of the dataset.
ALTER TABLE shares ADD COLUMN dataset TEXT;

CREATE UNIQUE INDEX shares_dataset ON shares (dataset) WHERE dataset IS NOT NULL;
