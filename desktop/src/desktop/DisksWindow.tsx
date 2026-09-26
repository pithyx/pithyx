import { useTranslation } from 'react-i18next';
import { useTopic } from '../realtime/useTopic';
import { formatBytes } from './format';

export function DisksWindow() {
  const { t, i18n } = useTranslation('disks');
  const snapshot = useTopic('storage/disks');

  return (
    <section className="window" aria-labelledby="disks-window-title">
      <header className="window__title-bar">
        <h2 id="disks-window-title" className="window__title">
          {t('window.title')}
        </h2>
      </header>
      <div className="window__content">
        {snapshot === undefined ? (
          <p aria-busy="true">{t('loading')}</p>
        ) : snapshot.disks.length === 0 ? (
          <p>{t('empty')}</p>
        ) : (
          <table className="table">
            <caption className="visually-hidden">{t('table.caption')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('table.name')}</th>
                <th scope="col">{t('table.model')}</th>
                <th scope="col">{t('table.serial')}</th>
                <th scope="col" className="table__number">
                  {t('table.size')}
                </th>
                <th scope="col">{t('table.type')}</th>
                <th scope="col">{t('table.transport')}</th>
                <th scope="col" className="table__number">
                  {t('table.partitions')}
                </th>
              </tr>
            </thead>
            <tbody>
              {snapshot.disks.map((disk) => (
                <tr key={disk.id} data-disk={disk.name}>
                  <th scope="row">{disk.name}</th>
                  <td>{disk.model}</td>
                  <td className="mono">{disk.serial}</td>
                  <td className="table__number">{formatBytes(disk.sizeBytes, i18n.language)}</td>
                  <td>{disk.rotational ? t('type.hdd') : t('type.ssd')}</td>
                  <td>{disk.transport?.toUpperCase() ?? t('transport.unknown')}</td>
                  <td className="table__number">{disk.partitions.length}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </section>
  );
}
