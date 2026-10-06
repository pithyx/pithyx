import type { components } from '@pithyx/api-client';
import { useTranslation } from 'react-i18next';
import { Checkbox } from '@pithyx/ui';
import { useAppName } from '../names';
import type { ShareDetails } from '../sdk/internal';
import type { GrantEntry, PermissionCatalog } from '../sdk/users';
import {
  allows,
  givenBy,
  shareAccess,
  shareResource,
  withAllowed,
  withShareAccess,
  type Access,
} from './grants';
import styles from './Users.module.css';

type InheritedGrant = components['schemas']['InheritedGrant'];

const ACCESS: readonly Access[] = ['none', 'read', 'readWrite'];

type RightName = 'admin' | 'desktopLogin' | 'sshLogin' | 'power';
export type ProtocolName = 'smb' | 'sftp' | 'webdav' | 'ftp';

/** The part of a resource after the colon. */
function nameOf(resource: string): string {
  return resource.slice(resource.indexOf(':') + 1);
}

/** The key of a right's name, such as `admin` for `right:admin`. */
function rightKey(resource: string): RightName {
  return nameOf(resource) as RightName;
}

/** The key of a protocol's name, such as `smb` for `protocol:smb`. */
export function protocolKey(resource: string): ProtocolName {
  return nameOf(resource) as ProtocolName;
}

/**
 * The grants of one user or group: special rights, file protocols, apps and shared folders. What a user's groups give
 * is shown checked and locked with the groups that give it; it is changed on the group. Administrators have every
 * right, so the rest is locked while the administrator right is on.
 */
export function GrantEditor({
  catalog,
  shares,
  value,
  inherited = [],
  onChange,
}: {
  catalog: PermissionCatalog;
  shares: readonly ShareDetails[];
  value: readonly GrantEntry[];
  inherited?: readonly InheritedGrant[];
  onChange: (value: GrantEntry[]) => void;
}) {
  const { t } = useTranslation('users');
  const appName = useAppName();
  const admin = allows(value, 'right:admin') || givenBy(inherited, 'right:admin').length > 0;
  const toggle = (resource: string, label: string) => {
    const groups = givenBy(inherited, resource);
    const locked = groups.length > 0 || (admin && resource !== 'right:admin');
    return (
      <div key={resource}>
        <Checkbox
          isSelected={allows(value, resource) || locked}
          isDisabled={locked}
          onChange={(on) => {
            onChange(withAllowed(value, resource, on));
          }}
        >
          {label}
        </Checkbox>
        {groups.length > 0 && (
          <span className={styles.hint}>{t('grants.through', { groups: groups.join(', ') })}</span>
        )}
      </div>
    );
  };
  return (
    <div className={styles.form}>
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>{t('grants.rights')}</legend>
        {catalog.rights.map((resource) =>
          toggle(resource, t(`grants.right.${rightKey(resource)}`)),
        )}
        {admin && <p className={styles.muted}>{t('grants.adminHasAll')}</p>}
      </fieldset>
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>{t('grants.protocols')}</legend>
        {catalog.protocols.map((resource) =>
          toggle(resource, t(`grants.protocol.${protocolKey(resource)}`)),
        )}
      </fieldset>
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>{t('grants.apps')}</legend>
        {catalog.apps.map((app) => toggle(`app:${app}`, appName(app)))}
      </fieldset>
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>{t('grants.shares')}</legend>
        {shares.length === 0 && <p className={styles.muted}>{t('grants.noShares')}</p>}
        {shares.map((share) => {
          const groups = givenBy(inherited, shareResource(share.id));
          return (
            <div key={share.id} className={styles.shareRow}>
              <span>{share.name}</span>
              <select
                className={styles.select}
                aria-label={t('grants.shareAccess', { share: share.name })}
                value={admin ? 'readWrite' : shareAccess(value, share.id)}
                disabled={admin}
                onChange={(event) => {
                  onChange(withShareAccess(value, share.id, event.target.value as Access));
                }}
              >
                {ACCESS.map((access) => (
                  <option key={access} value={access}>
                    {t(`grants.access.${access}`)}
                  </option>
                ))}
              </select>
              {groups.length > 0 && (
                <span className={styles.hint}>
                  {t('grants.throughShare', {
                    groups: groups.join(', '),
                  })}
                </span>
              )}
            </div>
          );
        })}
      </fieldset>
    </div>
  );
}
