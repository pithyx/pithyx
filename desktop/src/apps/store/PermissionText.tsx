import { useTranslation } from 'react-i18next';
import { isKnownPermission } from './model';
import styles from './Store.module.css';

/** A permission with what it lets the app do; one this version does not know shows its name. */
export function PermissionText({ permission, id }: { permission: string; id?: string }) {
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
    </span>
  );
}
