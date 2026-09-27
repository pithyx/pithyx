import {
  IconArrowLeft,
  IconArrowRight,
  IconBaselineDensityMedium,
  IconBaselineDensitySmall,
  IconChevronRight,
  IconFolderFilled,
  IconHome,
  IconMovie,
  IconPhoto,
  IconPhotoPlus,
  IconServer2,
} from '../ui/icons';
import { useState } from 'react';
import {
  Label,
  RadioButton,
  RadioField,
  RadioGroup,
  ToggleButton,
  ToggleButtonGroup,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../desktop/format';
import { Wallpaper } from '../shell/Wallpaper';
import { IconButton } from '../ui/Button';
import { ProgressBar } from '../ui/Controls';
import { cx } from '../ui/cx';
import { SearchField } from '../ui/TextField';
import { accentPresets, filesApp, logLines, storageApp } from './sampleApps';
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
          <span className={styles.healthy}>{t('health.healthy')}</span>
        </div>
        <p className={styles.muted}>
          {t('layout', { type: storageApp.layout, count: storageApp.disks.length })}
        </p>
        <ProgressBar
          label={t('used', {
            used: formatBytes(storageApp.usedBytes, i18n.language),
            total: formatBytes(storageApp.totalBytes, i18n.language),
          })}
          value={(storageApp.usedBytes / storageApp.totalBytes) * 100}
        />
      </section>
      <h3 className={styles.sideTitle}>{t('disks')}</h3>
      <ul className={styles.disks}>
        {storageApp.disks.map((disk) => (
          <li key={disk.number} className={styles.disk} data-state={disk.state}>
            <span className={styles.diskName}>{t('disk', { number: disk.number })}</span>
            <span className={cx(styles.muted, styles.mono, styles.optional)}>{disk.model}</span>
            <span className={cx(styles.mono, styles.temperature)}>
              {disk.temperature === undefined ? '' : celsius.format(disk.temperature)}
            </span>
            <span className={styles.diskState}>{t(`state.${disk.state}`)}</span>
          </li>
        ))}
      </ul>
      <p className={styles.muted}>{t('standbyHint')}</p>
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

const settingsSections = [
  'personalization',
  'users',
  'network',
  'storage',
  'updates',
  'about',
] as const;

export function SettingsContent({ accent }: { accent: string }) {
  const { t } = useTranslation('settings');
  return (
    <div className={styles.settings}>
      <nav className={styles.settingsNav} aria-label={t('sections')}>
        {settingsSections.map((section, index) => (
          <a
            key={section}
            href="#settings"
            className={cx(styles.sideItem, index === 0 && styles.sideActive)}
          >
            {t(`nav.${section}`)}
          </a>
        ))}
      </nav>
      <div className={styles.settingsBody}>
        <h3 className={styles.settingsTitle}>{t('nav.personalization')}</h3>
        <RadioGroup className={styles.setting} defaultValue="system" orientation="horizontal">
          <Label className={styles.settingLabel}>{t('personalization.theme')}</Label>
          <div className={styles.segmented}>
            {(['system', 'light', 'dark'] as const).map((theme) => (
              <RadioField key={theme} value={theme}>
                <RadioButton className={styles.segment}>
                  {t(`personalization.themes.${theme}`)}
                </RadioButton>
              </RadioField>
            ))}
          </div>
        </RadioGroup>
        <RadioGroup className={styles.setting} defaultValue={accent} orientation="horizontal">
          <Label className={styles.settingLabel}>{t('personalization.accent')}</Label>
          <div className={styles.swatches}>
            {accentPresets.map((name) => (
              <RadioField key={name} value={name} aria-label={t(`personalization.accents.${name}`)}>
                <RadioButton className={styles.swatch} data-accent={name} />
              </RadioField>
            ))}
          </div>
        </RadioGroup>
        <RadioGroup className={styles.setting} defaultValue="bottom" orientation="horizontal">
          <Label className={styles.settingLabel}>{t('personalization.taskbar')}</Label>
          <div className={styles.segmented}>
            {(['bottom', 'top'] as const).map((position) => (
              <RadioField key={position} value={position}>
                <RadioButton className={styles.segment}>
                  {t(`personalization.positions.${position}`)}
                </RadioButton>
              </RadioField>
            ))}
          </div>
        </RadioGroup>
        <RadioGroup className={styles.setting} defaultValue="default" orientation="horizontal">
          <Label className={styles.settingLabel}>{t('personalization.wallpaper')}</Label>
          <p className={styles.muted}>{t('personalization.wallpaperHint')}</p>
          <div className={styles.wallpapers}>
            <RadioField value="default">
              <RadioButton className={styles.wallpaperOption}>
                <span className={styles.wallpaperThumb}>
                  <Wallpaper />
                </span>
                {t('personalization.wallpapers.default')}
              </RadioButton>
            </RadioField>
            <RadioField value="solid">
              <RadioButton className={styles.wallpaperOption}>
                <span className={cx(styles.wallpaperThumb, styles.wallpaperSolid)} />
                {t('personalization.wallpapers.solid')}
              </RadioButton>
            </RadioField>
            <RadioField value="picture">
              <RadioButton className={styles.wallpaperOption}>
                <span className={cx(styles.wallpaperThumb, styles.wallpaperPicture)}>
                  <IconPhotoPlus size={24} stroke={1.5} aria-hidden />
                </span>
                {t('personalization.wallpapers.picture')}
              </RadioButton>
            </RadioField>
          </div>
        </RadioGroup>
      </div>
    </div>
  );
}
