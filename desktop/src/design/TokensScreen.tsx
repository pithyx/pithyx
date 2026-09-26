import {
  IconBell,
  IconDatabase,
  IconDots,
  IconFolders,
  IconPlus,
  IconSearch,
  IconSettings,
  IconTrash,
} from '@tabler/icons-react';
import { useState } from 'react';
import { Button, IconButton } from '../ui/Button';
import { Badge, Checkbox, ProgressBar, Switch } from '../ui/Controls';
import { Menu, MenuItem, MenuPopover, MenuTrigger } from '../ui/Menu';
import { PasswordField, SearchField, TextField } from '../ui/TextField';
import { labels } from './labels';
import styles from './TokensScreen.module.css';

const swatches: [string, string][] = [
  ['--color-bg', '--color-text'],
  ['--color-surface-1', '--color-text'],
  ['--color-surface-2', '--color-text'],
  ['--color-surface-3', '--color-text-muted'],
  ['--color-surface-hover', '--color-text'],
  ['--color-accent', '--color-accent-contrast'],
  ['--color-accent-subtle', '--color-accent'],
  ['--color-taskbar-solid', '--color-text'],
  ['--color-highlight', '--color-highlight-contrast'],
  ['--color-note', '--color-note-text'],
  ['--color-desktop', '--color-text'],
];

const statusSwatches: [string, string][] = [
  ['--color-success-surface', '--color-success'],
  ['--color-warning-surface', '--color-warning'],
  ['--color-danger-surface', '--color-danger'],
  ['--color-danger', '--color-danger-contrast'],
];

const accents = ['petrol', 'ocean', 'indigo', 'plum', 'rose', 'moss', 'graphite'];

const typeScale = [
  ['--font-size-display-2', 'display'],
  ['--font-size-display-1', 'display'],
  ['--font-size-2xl', 'sans'],
  ['--font-size-xl', 'sans'],
  ['--font-size-lg', 'sans'],
  ['--font-size-md', 'sans'],
  ['--font-size-sm', 'sans'],
  ['--font-size-xs', 'sans'],
  ['--font-size-sm', 'mono'],
] as const;

const spacing = [
  '--space-0',
  '--space-1',
  '--space-2',
  '--space-3',
  '--space-4',
  '--space-5',
  '--space-6',
  '--space-7',
];
const radii = ['--radius-xs', '--radius-sm', '--radius-md', '--radius-lg', '--radius-full'];
const shadows = ['--shadow-1', '--shadow-2', '--shadow-3', '--shadow-4'];
const motion = [
  ['--duration-instant', '--ease-standard'],
  ['--duration-fast', '--ease-standard'],
  ['--duration-normal', '--ease-enter'],
  ['--duration-slow', '--ease-enter'],
  ['--duration-normal', '--ease-exit'],
] as const;

function parseColor(value: string): [number, number, number] | undefined {
  const match = /rgba?\((\d+(?:\.\d+)?)[ ,]+(\d+(?:\.\d+)?)[ ,]+(\d+(?:\.\d+)?)/.exec(value);
  return match === null ? undefined : [Number(match[1]), Number(match[2]), Number(match[3])];
}

function luminance([r, g, b]: [number, number, number]): number {
  const channel = (value: number) => {
    const c = value / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

/** Writes the measured contrast ratio into the swatch once it is rendered. */
function measure(element: HTMLElement | null) {
  if (element === null) {
    return;
  }
  const style = getComputedStyle(element);
  const back = parseColor(style.backgroundColor);
  const front = parseColor(style.color);
  const output = element.querySelector('[data-ratio]');
  if (back === undefined || front === undefined || output === null) {
    return;
  }
  const [high, low] = [luminance(back), luminance(front)].sort((a, b) => b - a) as [number, number];
  output.textContent = `${((high + 0.05) / (low + 0.05)).toFixed(2)}:1`;
}

function Swatch({ background, foreground }: { background: string; foreground: string }) {
  return (
    <div
      className={styles.swatch}
      style={{ background: `var(${background})`, color: `var(${foreground})` }}
      ref={measure}
    >
      <span className={styles.swatchSample}>{labels.tokens.aa}</span>
      <code className={styles.swatchName}>{background}</code>
      <code className={styles.swatchName}>{foreground}</code>
      <span className={styles.ratio} data-ratio />
    </div>
  );
}

function MotionRow({ duration, easing }: { duration: string; easing: string }) {
  const [moved, setMoved] = useState(false);
  return (
    <button
      type="button"
      className={styles.motionRow}
      onClick={() => {
        setMoved(!moved);
      }}
    >
      <code>
        {duration} {easing}
      </code>
      <span className={styles.track}>
        <span
          className={styles.dot}
          data-moved={moved || undefined}
          style={{
            transitionDuration: `var(${duration})`,
            transitionTimingFunction: `var(${easing})`,
          }}
        />
      </span>
    </button>
  );
}

export function TokensScreen() {
  const t = labels.tokens;
  return (
    <main className={styles.page}>
      <h1 className={styles.title}>{labels.screensTitle.tokens}</h1>
      <section className={styles.section}>
        <h2 className={styles.heading}>{t.colors}</h2>
        <div className={styles.swatches}>
          {swatches.map(([background, foreground]) => (
            <Swatch key={background} background={background} foreground={foreground} />
          ))}
        </div>
        <h3 className={styles.subheading}>{t.status}</h3>
        <div className={styles.swatches}>
          {statusSwatches.map(([background, foreground]) => (
            <Swatch key={background} background={background} foreground={foreground} />
          ))}
        </div>
      </section>

      <section className={styles.section}>
        <h2 className={styles.heading}>{t.accents}</h2>
        <div className={styles.accents}>
          {accents.map((accent) => (
            <div key={accent} data-accent={accent} className={styles.accent}>
              <span className={styles.accentChip} />
              <code>{accent}</code>
              <Button variant="primary" size="sm">
                {t.buttons.primary}
              </Button>
            </div>
          ))}
        </div>
      </section>

      <section className={styles.section}>
        <h2 className={styles.heading}>{t.type}</h2>
        <div className={styles.typeScale}>
          {typeScale.map(([size, family]) => (
            <div key={`${size}-${family}`} className={styles.typeRow}>
              <code className={styles.typeToken}>
                {size} {family}
              </code>
              <span className={styles[family]} style={{ fontSize: `var(${size})` }}>
                {t.sample}
              </span>
            </div>
          ))}
        </div>
      </section>

      <div className={styles.columns}>
        <section className={styles.section}>
          <h2 className={styles.heading}>{t.spacing}</h2>
          {spacing.map((token) => (
            <div key={token} className={styles.spaceRow}>
              <code>{token}</code>
              <span className={styles.spaceBar} style={{ width: `var(${token})` }} />
            </div>
          ))}
        </section>
        <section className={styles.section}>
          <h2 className={styles.heading}>{t.radii}</h2>
          <div className={styles.row}>
            {radii.map((token) => (
              <div key={token} className={styles.radius} style={{ borderRadius: `var(${token})` }}>
                <code>{token.replace('--radius-', '')}</code>
              </div>
            ))}
          </div>
        </section>
      </div>

      <section className={styles.section}>
        <h2 className={styles.heading}>{t.shadows}</h2>
        <div className={styles.row}>
          {shadows.map((token) => (
            <div key={token} className={styles.shadow} style={{ boxShadow: `var(${token})` }}>
              <code>{token}</code>
            </div>
          ))}
        </div>
      </section>

      <section className={styles.section}>
        <h2 className={styles.heading}>{t.motion}</h2>
        <p className={styles.hint}>{t.motionHint}</p>
        {motion.map(([duration, easing]) => (
          <MotionRow key={`${duration}-${easing}`} duration={duration} easing={easing} />
        ))}
      </section>

      <section className={styles.section}>
        <h2 className={styles.heading}>{t.components}</h2>
        <div className={styles.row}>
          <Button variant="primary">
            <IconPlus size={18} stroke={1.75} aria-hidden />
            {t.buttons.primary}
          </Button>
          <Button>{t.buttons.secondary}</Button>
          <Button variant="quiet">{t.buttons.quiet}</Button>
          <Button variant="danger">
            <IconTrash size={18} stroke={1.75} aria-hidden />
            {t.buttons.danger}
          </Button>
          <Button isDisabled>{t.buttons.disabled}</Button>
          <Button variant="primary" size="sm">
            {t.buttons.primary}
          </Button>
          <IconButton label={t.tooltip}>
            <IconSettings size={20} stroke={1.75} />
          </IconButton>
          <MenuTrigger>
            <IconButton label={t.menu} variant="secondary">
              <IconDots size={20} stroke={1.75} />
            </IconButton>
            <MenuPopover>
              <Menu>
                {t.menuItems.map((item, index) => (
                  <MenuItem key={item} tone={index === 2 ? 'danger' : 'default'}>
                    {item}
                  </MenuItem>
                ))}
              </Menu>
            </MenuPopover>
          </MenuTrigger>
        </div>
        <div className={styles.fields}>
          <TextField label={t.field} description={t.fieldHint} defaultValue={t.fieldValue} />
          <TextField
            label={t.invalidField}
            defaultValue={t.invalidValue}
            isInvalid
            errorMessage={t.invalidMessage}
          />
          <PasswordField label={t.password} autoComplete="new-password" />
          <SearchField label={t.search} placeholder={t.search} />
        </div>
        <div className={styles.row}>
          <Checkbox defaultSelected>{t.checkbox}</Checkbox>
          <Switch defaultSelected>{t.switch}</Switch>
          <Badge>3</Badge>
          <Badge tone="accent">12</Badge>
          <Badge tone="neutral">99+</Badge>
          <Badge tone="danger">1</Badge>
        </div>
        <ProgressBar label={t.progress} value={42} className={styles.progress} />
        <div className={styles.row}>
          {[IconFolders, IconDatabase, IconSearch, IconBell, IconSettings, IconTrash].map(
            (Glyph, index) => (
              <Glyph key={index} size={20} stroke={1.75} aria-hidden />
            ),
          )}
        </div>
      </section>
    </main>
  );
}
