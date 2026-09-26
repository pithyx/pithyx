import { useTranslation } from 'react-i18next';
import { useTopic } from '../realtime/useTopic';
import { cx } from '../ui/cx';
import styles from './DisksContent.module.css';
import { formatBytes } from './format';

/** The live disk list from the topic storage/disks. */
export function DisksContent() {
  const { t, i18n } = useTranslation('disks');
  const snapshot = useTopic('storage/disks');

  if (snapshot === undefined) {
    return (
      <p className={styles.message} aria-busy="true">
        {t('loading')}
      </p>
    );
  }
  if (snapshot.disks.length === 0) {
    return <p className={styles.message}>{t('empty')}</p>;
  }
  return (
    <table className={styles.table}>
      <caption className="visually-hidden">{t('table.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('table.name')}</th>
          <th scope="col">{t('table.model')}</th>
          <th scope="col" className={styles.optional}>
            {t('table.serial')}
          </th>
          <th scope="col" className={styles.number}>
            {t('table.size')}
          </th>
          <th scope="col" className={styles.optional}>
            {t('table.type')}
          </th>
          <th scope="col" className={styles.optional}>
            {t('table.transport')}
          </th>
          <th scope="col" className={styles.number}>
            {t('table.partitions')}
          </th>
        </tr>
      </thead>
      <tbody>
        {snapshot.disks.map((disk) => (
          <tr key={disk.id} data-disk={disk.name}>
            <th scope="row" className={styles.mono}>
              {disk.name}
            </th>
            <td>{disk.model}</td>
            <td className={cx(styles.mono, styles.optional)}>{disk.serial}</td>
            <td className={styles.number}>{formatBytes(disk.sizeBytes, i18n.language)}</td>
            <td className={styles.optional}>{disk.rotational ? t('type.hdd') : t('type.ssd')}</td>
            <td className={styles.optional}>
              {disk.transport?.toUpperCase() ?? t('transport.unknown')}
            </td>
            <td className={styles.number}>{disk.partitions.length}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
