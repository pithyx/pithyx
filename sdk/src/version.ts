/**
 * The version of this SDK package. It follows SemVer for the whole Vyxis monorepo: a new method, event or optional
 * field is a minor release, a removed or changed one a major release.
 */
export const SDK_VERSION = '0.1.0';

/**
 * The version of the wire protocol between an app and the desktop. It changes only when a message changes in a way an
 * older side cannot ignore. Additions (new methods, events, optional fields) keep the protocol version; apps find
 * them through {@link AppContext.capabilities}.
 *
 * The desktop supports every protocol version from {@link MIN_PROTOCOL_VERSION} up to this one, so an app built
 * against an older SDK keeps working after a desktop update.
 */
export const PROTOCOL_VERSION = 1;

/** The oldest protocol version this SDK's host side still accepts. */
export const MIN_PROTOCOL_VERSION = 1;

/** Whether a host that speaks `min` to `max` can talk to an app that speaks `version`. */
export function isProtocolSupported(
  version: number,
  min = MIN_PROTOCOL_VERSION,
  max = PROTOCOL_VERSION,
): boolean {
  return Number.isInteger(version) && version >= min && version <= max;
}
