import {
  IconArrowLeft,
  IconArrowRight,
  IconChevronRight,
  IconFolderFilled,
  IconHome,
  IconMovie,
  IconPhoto,
  IconServer2,
} from '@tabler/icons-react';
import { Label, RadioButton, RadioField, RadioGroup } from 'react-aria-components';
import { IconButton } from '../ui/Button';
import { ProgressBar } from '../ui/Controls';
import { cx } from '../ui/cx';
import { SearchField } from '../ui/TextField';
import { filesApp, logsApp, settingsApp, storageApp } from './sampleApps';
import styles from './SampleApps.module.css';

const kindIcon = { folder: IconFolderFilled, image: IconPhoto, video: IconMovie } as const;

export function FilesContent() {
  return (
    <div className={styles.files}>
      <div className={styles.toolbar}>
        <IconButton label={filesApp.back} size="sm">
          <IconArrowLeft size={18} stroke={1.75} />
        </IconButton>
        <IconButton label={filesApp.forward} size="sm" isDisabled>
          <IconArrowRight size={18} stroke={1.75} />
        </IconButton>
        <nav className={styles.breadcrumb} aria-label={filesApp.pathLabel}>
          <IconHome size={16} stroke={1.75} aria-hidden />
          {filesApp.path.map((part) => (
            <span key={part} className={styles.crumb}>
              <IconChevronRight size={14} stroke={1.75} aria-hidden />
              {part}
            </span>
          ))}
        </nav>
        <SearchField
          label={filesApp.search}
          placeholder={filesApp.search}
          className={styles.search}
        />
      </div>
      <div className={styles.split}>
        <aside className={styles.sidebar}>
          <h3 className={styles.sideTitle}>{filesApp.placesTitle}</h3>
          {filesApp.places.map((place, index) => (
            <a
              key={place}
              href="#files"
              className={cx(styles.sideItem, index === 3 && styles.sideActive)}
            >
              {place}
            </a>
          ))}
          <h3 className={styles.sideTitle}>{filesApp.sharesTitle}</h3>
          {filesApp.shares.map((share) => (
            <a key={share} href="#files" className={styles.sideItem}>
              <IconServer2 size={16} stroke={1.75} aria-hidden />
              {share}
            </a>
          ))}
        </aside>
        <div className={styles.listWrap}>
          <div
            className={styles.listScroll}
            tabIndex={0}
            role="region"
            aria-label={filesApp.listLabel}
          >
            <table className={styles.list}>
              <thead>
                <tr>
                  <th>{filesApp.columns.name}</th>
                  <th className={styles.optional}>{filesApp.columns.size}</th>
                  <th className={styles.optional}>{filesApp.columns.modified}</th>
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
                      <td className={styles.optional}>{entry.size}</td>
                      <td className={styles.optional}>{entry.modified}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <p className={styles.statusBar}>{filesApp.status}</p>
        </div>
      </div>
    </div>
  );
}

export function StorageContent() {
  return (
    <div className={styles.storage}>
      <section className={styles.pool}>
        <div className={styles.poolHead}>
          <h3 className={styles.poolName}>{storageApp.pool}</h3>
          <span className={styles.healthy}>{storageApp.health}</span>
        </div>
        <p className={styles.muted}>{storageApp.layout}</p>
        <ProgressBar label={storageApp.used} value={storageApp.usedPercent} />
      </section>
      <h3 className={styles.sideTitle}>{storageApp.disksTitle}</h3>
      <ul className={styles.disks}>
        {storageApp.disks.map((disk) => (
          <li key={disk.name} className={styles.disk} data-state={disk.state}>
            <span className={styles.diskName}>{disk.name}</span>
            <span className={cx(styles.muted, styles.mono, styles.optional)}>{disk.model}</span>
            <span className={styles.mono}>{disk.temp}</span>
            <span className={styles.diskState}>{disk.state}</span>
          </li>
        ))}
      </ul>
      <p className={styles.muted}>{storageApp.sleepingHint}</p>
    </div>
  );
}

export function LogsContent() {
  return (
    <div className={styles.logs}>
      <div className={styles.toolbar}>
        <SearchField
          label={logsApp.filter}
          placeholder={logsApp.filter}
          className={styles.search}
        />
      </div>
      <ol className={styles.logLines} tabIndex={0} aria-label={logsApp.listLabel}>
        {logsApp.lines.map(([time, level, area, message]) => (
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

export function SettingsContent({ accent }: { accent: string }) {
  return (
    <div className={styles.settings}>
      <nav className={styles.settingsNav} aria-label={settingsApp.navLabel}>
        {settingsApp.nav.map((item, index) => (
          <a
            key={item}
            href="#settings"
            className={cx(styles.sideItem, index === 0 && styles.sideActive)}
          >
            {item}
          </a>
        ))}
      </nav>
      <div className={styles.settingsBody}>
        <h3 className={styles.settingsTitle}>{settingsApp.title}</h3>
        <RadioGroup className={styles.setting} defaultValue="System" orientation="horizontal">
          <Label className={styles.settingLabel}>{settingsApp.theme}</Label>
          <div className={styles.segmented}>
            {settingsApp.themes.map((theme) => (
              <RadioField key={theme} value={theme}>
                <RadioButton className={styles.segment}>{theme}</RadioButton>
              </RadioField>
            ))}
          </div>
        </RadioGroup>
        <RadioGroup className={styles.setting} defaultValue={accent} orientation="horizontal">
          <Label className={styles.settingLabel}>{settingsApp.accent}</Label>
          <div className={styles.swatches}>
            {settingsApp.accents.map((name) => (
              <RadioField key={name} value={name} aria-label={settingsApp.accentNames[name]}>
                <RadioButton className={styles.swatch} data-accent={name} />
              </RadioField>
            ))}
          </div>
        </RadioGroup>
        <RadioGroup className={styles.setting} defaultValue="Bottom" orientation="horizontal">
          <Label className={styles.settingLabel}>{settingsApp.taskbar}</Label>
          <div className={styles.segmented}>
            {settingsApp.positions.map((position) => (
              <RadioField key={position} value={position}>
                <RadioButton className={styles.segment}>{position}</RadioButton>
              </RadioField>
            ))}
          </div>
        </RadioGroup>
        <div className={styles.setting}>
          <span className={styles.settingLabel}>{settingsApp.wallpaper}</span>
          <p className={styles.muted}>{settingsApp.wallpaperHint}</p>
          <div className={styles.wallpapers}>
            <span className={cx(styles.wallpaperThumb, styles.wallpaperActive)} />
            <span className={cx(styles.wallpaperThumb, styles.wallpaperAlt)} />
            <span className={cx(styles.wallpaperThumb, styles.wallpaperPlain)} />
          </div>
        </div>
      </div>
    </div>
  );
}
