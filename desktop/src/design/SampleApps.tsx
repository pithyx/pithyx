import {
  IconArrowLeft,
  IconArrowRight,
  IconBaselineDensityMedium,
  IconBaselineDensitySmall,
  IconCertificate,
  IconChevronRight,
  IconCloudDownload,
  IconFolderFilled,
  IconFolders,
  IconHome,
  IconInfoCircle,
  IconMovie,
  IconNetwork,
  IconPhoto,
  IconServer2,
  IconUsers,
  IconUsersGroup,
} from '../ui/icons';
import { useState } from 'react';
import { ToggleButton, ToggleButtonGroup } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../desktop/format';
import { cx, IconButton, ProgressBar, SearchField } from '@pithyx/ui';
import { filesApp, logLines, storageApp } from './sampleApps';
import settingsStyles from '../apps/settings/Settings.module.css';
import styles from './SampleApps.module.css';
import { currentFolder } from './sampleData';

const kindIcon = { folder: IconFolderFilled, image: IconPhoto, video: IconMovie } as const;

export type Density = 'comfortable' | 'compact';

// Well-known folders show a translated name; every other place is a user folder and keeps its own name.
const wellKnownPlaces = ['home', 'desktop', 'documents'] as const;
type WellKnownPlace = (typeof wellKnownPlaces)[number];

function isWellKnownPlace(place: string): place is WellKnownPlace {
  return (wellKnownPlaces as readonly string[]).includes(place);
}

export function FilesContent({ density: initialDensity = 'comfortable' }: { density?: Density }) {
  const { t, i18n } = useTranslation('files');
  const [density, setDensity] = useState<Density>(initialDensity);
  const date = new Intl.DateTimeFormat(i18n.language, { month: 'short', day: 'numeric' });
  return (
    <div className={styles.files}>
      <div className={styles.toolbar}>
        <IconButton label={t('back')} size="sm">
          <IconArrowLeft size={18} stroke={1.75} />
        </IconButton>
        <IconButton label={t('forward')} size="sm" isDisabled>
          <IconArrowRight size={18} stroke={1.75} />
        </IconButton>
        <nav className={styles.breadcrumb} aria-label={t('path')}>
          <IconHome size={16} stroke={1.75} aria-hidden />
          {filesApp.path.map((part) => (
            <span key={part} className={styles.crumb}>
              <IconChevronRight size={14} stroke={1.75} aria-hidden />
              {part}
            </span>
          ))}
        </nav>
        <SearchField
          label={t('search', { folder: currentFolder })}
          placeholder={t('searchPlaceholder')}
          className={styles.search}
        />
        <ToggleButtonGroup
          aria-label={t('density.label')}
          selectionMode="single"
          disallowEmptySelection
          selectedKeys={[density]}
          onSelectionChange={(keys) => {
            const [key] = [...keys];
            if (key === 'comfortable' || key === 'compact') {
              setDensity(key);
            }
          }}
          className={styles.density}
        >
          <ToggleButton
            id="comfortable"
            aria-label={t('density.comfortable')}
            className={styles.densityButton}
          >
            <IconBaselineDensityMedium size={18} stroke={1.75} />
          </ToggleButton>
          <ToggleButton
            id="compact"
            aria-label={t('density.compact')}
            className={styles.densityButton}
          >
            <IconBaselineDensitySmall size={18} stroke={1.75} />
          </ToggleButton>
        </ToggleButtonGroup>
      </div>
      <div className={styles.split}>
        <aside className={styles.sidebar}>
          <h3 className={styles.sideTitle}>{t('places.title')}</h3>
          {filesApp.places.map((place) => (
            <a
              key={place}
              href="#files"
              className={cx(styles.sideItem, place === currentFolder && styles.sideActive)}
            >
              {isWellKnownPlace(place) ? t(`places.${place}`) : place}
            </a>
          ))}
          <h3 className={styles.sideTitle}>{t('shares.title')}</h3>
          {filesApp.shares.map((share) => (
            <a key={share} href="#files" className={styles.sideItem}>
              <IconServer2 size={16} stroke={1.75} aria-hidden />
              {share}
            </a>
          ))}
        </aside>
        <div className={styles.listWrap}>
          <div className={styles.listScroll} tabIndex={0} role="region" aria-label={t('list')}>
            <table className={styles.list} data-density={density}>
              <thead>
                <tr>
                  <th>{t('columns.name')}</th>
                  <th className={styles.optional}>{t('columns.size')}</th>
                  <th className={styles.optional}>{t('columns.modified')}</th>
                </tr>
              </thead>
              <tbody>
                {filesApp.entries.map((entry, index) => {
                  const Glyph = kindIcon[entry.kind];
                  return (
                    <tr key={entry.name} className={cx(index === 2 && styles.selectedRow)}>
                      <td>
                        <span className={styles.name}>
                          <Glyph
                            size={18}
                            stroke={1.75}
                            className={styles[entry.kind]}
                            aria-hidden
                          />
                          {entry.name}
                        </span>
                      </td>
                      <td className={styles.optional}>
                        {'items' in entry
                          ? t('items', { count: entry.items })
                          : formatBytes(entry.bytes, i18n.language)}
                      </td>
                      <td className={styles.optional}>{date.format(new Date(entry.modified))}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <p className={styles.statusBar}>
            {t('summary', {
              count: filesApp.entries.length,
              size: formatBytes(filesApp.totalBytes, i18n.language),
            })}
          </p>
        </div>
      </div>
    </div>
  );
}

export function StorageContent() {
  const { t, i18n } = useTranslation('storage');
  const celsius = new Intl.NumberFormat(i18n.language, { style: 'unit', unit: 'celsius' });
  return (
    <div className={styles.storage}>
      <section className={styles.pool}>
        <div className={styles.poolHead}>
          <h3 className={styles.poolName}>{storageApp.pool}</h3>
          <span className={styles.healthy}>{t('sample.health.healthy')}</span>
        </div>
        <p className={styles.muted}>
          {t('sample.layout', { type: storageApp.layout, count: storageApp.disks.length })}
        </p>
        <ProgressBar
          label={t('sample.used', {
            used: formatBytes(storageApp.usedBytes, i18n.language),
            total: formatBytes(storageApp.totalBytes, i18n.language),
          })}
          value={(storageApp.usedBytes / storageApp.totalBytes) * 100}
        />
      </section>
      <h3 className={styles.sideTitle}>{t('sample.disks')}</h3>
      <ul className={styles.disks}>
        {storageApp.disks.map((disk) => (
          <li key={disk.number} className={styles.disk} data-state={disk.state}>
            <span className={styles.diskName}>{t('sample.disk', { number: disk.number })}</span>
            <span className={cx(styles.muted, styles.mono, styles.optional)}>{disk.model}</span>
            <span className={cx(styles.mono, styles.temperature)}>
              {disk.temperature === undefined ? '' : celsius.format(disk.temperature)}
            </span>
            <span className={styles.diskState}>{t(`sample.state.${disk.state}`)}</span>
          </li>
        ))}
      </ul>
      <p className={styles.muted}>{t('sample.standbyHint')}</p>
    </div>
  );
}

export function LogsContent() {
  const { t } = useTranslation('logs');
  return (
    <div className={styles.logs}>
      <div className={styles.toolbar}>
        <SearchField label={t('filter')} placeholder={t('filter')} className={styles.search} />
      </div>
      <ol className={styles.logLines} tabIndex={0} aria-label={t('list')}>
        {logLines.map(([time, level, area, message]) => (
          <li key={`${time}-${message}`} className={styles.logLine} data-level={level}>
            <span className={styles.logTime}>{time}</span>
            <span className={styles.logLevel}>{level}</span>
            <span className={styles.logArea}>{area}</span>
            <span className={styles.logMessage}>{message}</span>
          </li>
        ))}
      </ol>
    </div>
  );
}

// The tiles of the Settings overview (OverviewPage.tsx) with its own styles, as a static sample.
const settingsTiles = [
  { id: 'info', icon: IconInfoCircle },
  { id: 'network', icon: IconNetwork },
  { id: 'certificates', icon: IconCertificate },
  { id: 'updates', icon: IconCloudDownload },
  { id: 'shares', icon: IconFolders },
  { id: 'fileServices', icon: IconServer2 },
  { id: 'users', icon: IconUsers },
  { id: 'groups', icon: IconUsersGroup },
] as const;

export function SettingsContent() {
  const { t } = useTranslation('settings');
  return (
    <div className={settingsStyles.overviewPage}>
      <h2 className={settingsStyles.overviewHeading}>{t('overview.allSettings')}</h2>
      <ul className={settingsStyles.tiles}>
        {settingsTiles.map((entry) => {
          const Icon = entry.icon;
          return (
            <li key={entry.id}>
              <button type="button" className={settingsStyles.tile}>
                <span className={settingsStyles.tileIcon} aria-hidden>
                  <Icon size={22} stroke={1.5} />
                </span>
                <span className={settingsStyles.tileText}>
                  <span className={settingsStyles.tileTitle}>{t(`pages.${entry.id}.title`)}</span>
                  <span className={settingsStyles.tileDescription}>
                    {t(`pages.${entry.id}.description`)}
                  </span>
                </span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
