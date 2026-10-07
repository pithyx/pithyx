import type { AppManifest, DirectoryGroup, DirectoryUser, GroupRef } from '@pithyx/sdk';
import type { MockUser } from './users';

/** What the mock desktop learns from the dev server: the app, the users to switch between and the entry URL. */
export interface DevConfig {
  manifest: AppManifest;
  /** The URL of the app's entry, `/apps/<id>/index.html`. */
  entry: string;
  users: MockUser[];
  groups: (GroupRef & { description: string })[];
  directory: { users: DirectoryUser[]; groups: DirectoryGroup[] };
}
