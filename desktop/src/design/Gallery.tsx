import { useState } from 'react';
import { Checkbox } from '../ui/Controls';
import { cx } from '../ui/cx';
import { Wordmark } from '../ui/Logo';
import type { TaskbarPosition } from '../shell/types';
import styles from './Gallery.module.css';
import { labels } from './labels';
import { screens, type ScreenDefinition } from './screens';

const devices = [
  { id: 'phone', width: 390, height: 844, column: 280 },
  { id: 'tablet', width: 820, height: 1180, column: 340 },
  { id: 'laptop', width: 1440, height: 900, column: 520 },
  { id: 'ultrawide', width: 3440, height: 1440, column: 900 },
] as const;

const accents = ['petrol', 'ocean', 'indigo', 'plum', 'moss', 'graphite'] as const;
const TASKBAR_POSITIONS: readonly TaskbarPosition[] = ['bottom', 'top', 'left', 'right'];

/** The taskbar position from a URL parameter; bottom when missing or unknown. */
export function taskbarFrom(value: string | null): TaskbarPosition {
  return TASKBAR_POSITIONS.find((position) => position === value) ?? 'bottom';
}

interface Options {
  screen: string;
  theme: 'both' | 'light' | 'dark';
  accent: string;
  taskbar: TaskbarPosition;
  lang: 'en' | 'de';
  ultrawide: boolean;
}

function readOptions(): Options {
  const params = new URLSearchParams(location.search);
  const theme = params.get('theme');
  return {
    screen: params.get('view') ?? 'login',
    theme: theme === 'light' || theme === 'dark' ? theme : 'both',
    accent: params.get('accent') ?? 'petrol',
    taskbar: taskbarFrom(params.get('taskbar')),
    lang: params.get('lang') === 'de' ? 'de' : 'en',
    ultrawide: params.get('ultrawide') === '1',
  };
}

function writeOptions(options: Options) {
  const params = new URLSearchParams({
    view: options.screen,
    theme: options.theme,
    accent: options.accent,
    taskbar: options.taskbar,
    lang: options.lang,
  });
  if (options.ultrawide) {
    params.set('ultrawide', '1');
  }
  history.replaceState(null, '', `?${params.toString()}`);
}

function screenUrl(screen: string, theme: 'light' | 'dark', options: Options): string {
  const params = new URLSearchParams({
    screen,
    theme,
    accent: options.accent,
    taskbar: options.taskbar,
    lang: options.lang,
  });
  return `?${params.toString()}`;
}

/** Scales a real-size iframe down to the width of its column. */
function scaleToFit(width: number) {
  return (element: HTMLDivElement | null) => {
    if (element === null) {
      return;
    }
    const observer = new ResizeObserver(([entry]) => {
      if (entry !== undefined) {
        element.style.setProperty('--scale', String(Math.min(1, entry.contentRect.width / width)));
      }
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  };
}

function Frames({ screen, options }: { screen: ScreenDefinition; options: Options }) {
  const themes = options.theme === 'both' ? (['light', 'dark'] as const) : [options.theme];
  const shown = devices.filter((device) => device.id !== 'ultrawide' || options.ultrawide);
  return (
    <div className={styles.devices}>
      {shown.map((device) => (
        <section key={device.id} className={styles.device}>
          <h3 className={styles.deviceTitle}>
            {labels.devices[device.id]}
            <span className={styles.deviceSize}>
              {device.width} × {device.height}
            </span>
          </h3>
          <div
            className={styles.frames}
            style={{ '--column': `${String(device.column)}px` } as React.CSSProperties}
          >
            {themes.map((theme) => {
              const url = screenUrl(screen.id, theme, options);
              return (
                <figure key={theme} className={styles.frame}>
                  <figcaption className={styles.frameCaption}>
                    <span>{labels.themes[theme]}</span>
                    <a href={url} target="_blank" rel="noreferrer">
                      {labels.openFull}
                    </a>
                  </figcaption>
                  <div
                    className={styles.viewport}
                    ref={scaleToFit(device.width)}
                    style={
                      {
                        '--device-width': `${String(device.width)}px`,
                        '--device-height': `${String(device.height)}px`,
                      } as React.CSSProperties
                    }
                  >
                    <iframe
                      key={url}
                      src={url}
                      title={`${screen.title}, ${labels.devices[device.id]}, ${labels.themes[theme]}`}
                      width={device.width}
                      height={device.height}
                      loading="lazy"
                    />
                  </div>
                </figure>
              );
            })}
          </div>
        </section>
      ))}
    </div>
  );
}

export function Gallery() {
  const [options, setOptions] = useState(readOptions);
  const screen = screens.find((entry) => entry.id === options.screen) ?? screens[0];
  const update = (patch: Partial<Options>) => {
    const next = { ...options, ...patch };
    setOptions(next);
    writeOptions(next);
  };
  const groups = Object.entries(labels.groups) as [ScreenDefinition['group'], string][];

  if (screen === undefined) {
    return null;
  }
  return (
    <div className={styles.gallery}>
      <nav className={styles.sidebar}>
        <Wordmark name={labels.title} />
        <p className={styles.intro}>{labels.intro}</p>
        {groups.map(([group, title]) => (
          <div key={group} className={styles.group}>
            <h2 className={styles.groupTitle}>{title}</h2>
            {screens
              .filter((entry) => entry.group === group)
              .map((entry) => (
                <button
                  key={entry.id}
                  type="button"
                  className={cx(styles.link, entry.id === screen.id && styles.active)}
                  aria-current={entry.id === screen.id ? 'page' : undefined}
                  onClick={() => {
                    update({ screen: entry.id });
                  }}
                >
                  {entry.title}
                </button>
              ))}
          </div>
        ))}
      </nav>
      <main className={styles.main}>
        <header className={styles.header}>
          <h1 className={styles.title}>{screen.title}</h1>
          <p className={styles.description}>{screen.description}</p>
          <div className={styles.toolbar}>
            <label className={styles.control}>
              {labels.theme}
              <select
                value={options.theme}
                onChange={(event) => {
                  update({ theme: event.target.value as Options['theme'] });
                }}
              >
                {(['both', 'light', 'dark'] as const).map((theme) => (
                  <option key={theme} value={theme}>
                    {labels.themes[theme]}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.control}>
              {labels.accent}
              <select
                value={options.accent}
                onChange={(event) => {
                  update({ accent: event.target.value });
                }}
              >
                {accents.map((accent) => (
                  <option key={accent} value={accent}>
                    {accent}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.control}>
              {labels.taskbar}
              <select
                value={options.taskbar}
                onChange={(event) => {
                  update({ taskbar: event.target.value as Options['taskbar'] });
                }}
              >
                {TASKBAR_POSITIONS.map((position) => (
                  <option key={position} value={position}>
                    {labels.taskbarPositions[position]}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.control}>
              {labels.language}
              <select
                value={options.lang}
                onChange={(event) => {
                  update({ lang: event.target.value as Options['lang'] });
                }}
              >
                {(['en', 'de'] as const).map((lang) => (
                  <option key={lang} value={lang}>
                    {labels.languages[lang]}
                  </option>
                ))}
              </select>
            </label>
            <Checkbox
              isSelected={options.ultrawide}
              onChange={(ultrawide) => {
                update({ ultrawide });
              }}
            >
              {labels.ultrawide}
            </Checkbox>
          </div>
        </header>
        <Frames screen={screen} options={options} />
      </main>
    </div>
  );
}
