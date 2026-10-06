/**
 * `@pithyx/cli`: the `pithyx` command for building Pithyx apps, and the checks it shares with the box.
 *
 * @packageDocumentation
 */
export { COMPOSE_RULES, validateCompose, type ComposeValidation } from './compose';
export { isValidCron } from './cron';
export { MAX_ICON_BYTES, validateIcon } from './icon';
export type { Issue } from './issues';
export { checkLayout, ICON_FILE, isSafePath, MANIFEST_FILE } from './layout';
export {
  isSupportedElevatedRight,
  RESERVED_ID_PREFIX,
  SUPPORTED_MANIFEST_VERSION,
  validateManifest,
  type ManifestValidation,
} from './manifest';
