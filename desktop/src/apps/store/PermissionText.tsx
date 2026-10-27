import { useTranslation } from 'react-i18next';
import { manifestText } from '../installed';
import { isKnownPermission } from './model';
import styles from './Store.module.css';

/**
 * The author's reason for a permission or right (D388); an app installed before reasons were required has none, and
 * the line says so.
 */
export function ReasonText({ reason }: { reason: Readonly<Record<string, string>> | undefined }) {
  const { t, i18n } = useTranslation('store');
  return (
    <span className={styles.reason}>
      {reason === undefined
        ? t('elevated.noReason')
        : t('elevated.reason', { reason: manifestText(reason, i18n.language) })}
    </span>
  );
}

/**
 * A permission with what it lets the app do and the author's reason; one this version does not know shows its name.
 * `network.internet` also names the hosts the app says it talks to.
 */
export function PermissionText({
  permission,
  id,
  reason,
  hosts = [],
}: {
  permission: string;
  id?: string;
  reason: Readonly<Record<string, string>> | undefined;
  hosts?: readonly string[];
}) {
  const { t } = useTranslation('store');
  // The permission names are data; their texts are keyed by them (permission.files.read.title).
  const text = t as unknown as (key: string) => string;
  return (
    <span className={styles.permissionText} id={id}>
      <span className={styles.permissionTitle}>
        {isKnownPermission(permission) ? text(`permission.${permission}.title`) : permission}
      </span>
      <span className={styles.muted}>
        {isKnownPermission(permission)
          ? text(`permission.${permission}.description`)
          : t('permission.unknown')}
      </span>
      <ReasonText reason={reason} />
      {permission === 'network.internet' && hosts.length > 0 && (
        <span className={styles.muted}>{t('elevated.hosts', { hosts: hosts.join(', ') })}</span>
      )}
    </span>
  );
}
