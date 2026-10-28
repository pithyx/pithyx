import {
  IconApps,
  IconBell,
  IconCertificate,
  IconChevronRight,
  IconColorSwatch,
  IconDevices,
  IconFingerprint,
  IconLanguage,
  IconLayoutBottombar,
  IconLogin,
  IconPalette,
  IconShieldLock,
  IconWallpaper,
  type TablerIcon,
} from '../../ui/icons';
import { useQuery } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { lazy, Suspense, useId, useState, useSyncExternalStore, type ReactNode } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { Menu, MenuItem, MenuPopover, MenuTrigger } from '@pithyx/ui';
import { fetchSession, queryKeys } from '../../api/queries';
import { AccentOptions, WallpaperOptions } from '../../desktop/AppearanceOptions';
import { DevicesMenu } from '../../desktop/DevicesMenu';
import { LanguageOptions } from '../../desktop/LanguageOptions';
import { ThemeOptions } from '../../desktop/ThemeOptions';
import { languageName, loadTranslations, storedLanguage } from '../../i18n';
import { trustQuery } from '../../security/trust';
import type { TaskbarPosition } from '../../shell/types';
import { onThemeChoice, storedTheme } from '../../theme/preference';
import { useAppearance } from '../../theme/appearance';
import { useSizeClass } from '../../wm/sizeClass';
import { dispatch, useDesktop } from '../../wm/store';
import styles from './PreferencesApp.module.css';

type User = components['schemas']['UserInfo'];

const TASKBAR_POSITIONS: readonly TaskbarPosition[] = ['bottom', 'top', 'left', 'right'];

// The dialogs load when they are opened, as they did from the account menu.
const TotpSetup = lazy(() =>
  import('../../auth/TotpSetup').then(({ TotpSetup }) => ({ default: TotpSetup })),
);
const StartupAppsDialog = lazy(() =>
  import('../../desktop/StartupAppsDialog').then(({ StartupAppsDialog }) => ({
    default: StartupAppsDialog,
  })),
);
const ChannelsDialog = lazy(() =>
  import('../../notifications/ChannelsDialog').then(({ ChannelsDialog }) => ({
    default: ChannelsDialog,
  })),
);
const FileDefaultsDialog = lazy(() =>
  import('../../desktop/FileDefaultsDialog').then(({ FileDefaultsDialog }) => ({
    default: FileDefaultsDialog,
  })),
);
const PasskeysDialog = lazy(() =>
  Promise.all([import('../../security/PasskeysDialog'), loadTranslations('security')]).then(
    ([{ PasskeysDialog }]) => ({ default: PasskeysDialog }),
  ),
);
const TrustDialog = lazy(() =>
  Promise.all([import('../../security/TrustDialog'), loadTranslations('security')]).then(
    ([{ TrustDialog }]) => ({ default: TrustDialog }),
  ),
);

type DialogId = 'twoFactor' | 'passkeys' | 'trust' | 'fileDefaults' | 'startupApps' | 'channels';

/** One setting: its icon, name, a line that explains it and, at the end, its current value. */
function RowContent({
  icon: Icon,
  label,
  description,
  value,
  ids,
}: {
  icon: TablerIcon;
  label: string;
  description: string;
  value: string | undefined;
  ids: { label: string; details: string };
}) {
  return (
    <>
      <span className={styles.icon} aria-hidden>
        <Icon size={20} stroke={1.75} />
      </span>
      <span className={styles.text}>
        <span id={ids.label} className={styles.label}>
          {label}
        </span>
        <span id={ids.details} className={styles.description}>
          {value !== undefined && <span className={styles.value}>{value}</span>}
          {description}
        </span>
      </span>
      <IconChevronRight className={styles.chevron} size={18} stroke={1.75} aria-hidden />
    </>
  );
}

interface RowProps {
  icon: TablerIcon;
  label: string;
  description: string;
  value?: string;
}

/** A setting that opens a dialog. */
function DialogRow({ onOpen, ...content }: RowProps & { onOpen: () => void }) {
  const id = useId();
  const ids = { label: `${id}-label`, details: `${id}-details` };
  return (
    <li>
      <AriaButton
        className={styles.row}
        aria-labelledby={ids.label}
        aria-describedby={ids.details}
        onPress={onOpen}
      >
        <RowContent {...content} value={content.value} ids={ids} />
      </AriaButton>
    </li>
  );
}

/** A setting with a few choices, offered in a menu next to it. */
function ChoiceRow({ menu, ...content }: RowProps & { menu: ReactNode }) {
  const id = useId();
  const ids = { label: `${id}-label`, details: `${id}-details` };
  return (
    <li>
      <MenuTrigger>
        <AriaButton
          className={styles.row}
          aria-labelledby={ids.label}
          aria-describedby={ids.details}
        >
          <RowContent {...content} value={content.value} ids={ids} />
        </AriaButton>
        <MenuPopover placement="bottom end">{menu}</MenuPopover>
      </MenuTrigger>
    </li>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <section className={styles.section} aria-labelledby={id}>
      <h2 id={id} className={styles.sectionTitle}>
        {title}
      </h2>
      <ul className={styles.rows}>{children}</ul>
    </section>
  );
}

/**
 * Personal settings (D392): everything a user sets for themselves, in groups, for every user. The account menu only
 * opens it; a new personal setting gets a row here.
 */
export function PreferencesApp() {
  const { t, i18n } = useTranslation();
  const [dialog, setDialog] = useState<DialogId | null>(null);
  const session = useQuery({
    queryKey: queryKeys.session,
    queryFn: ({ signal }) => fetchSession(signal),
  });
  const user: User | undefined = session.data;
  const trust = useQuery(trustQuery);
  const theme = useSyncExternalStore(onThemeChoice, storedTheme);
  const { accent, wallpaper } = useAppearance();
  const taskbar = useDesktop((state) => state.taskbar);
  const compact = useSizeClass() === 'compact';
  const close = () => {
    setDialog(null);
  };
  const language = storedLanguage();

  return (
    <div className={styles.app}>
      <div className={styles.content}>
        <Section title={t('preferences.account')}>
          <DialogRow
            icon={IconShieldLock}
            label={t('menu.twoFactor')}
            description={t('preferences.twoFactorHint')}
            {...(user === undefined
              ? {}
              : { value: user.totpEnabled ? t('preferences.on') : t('preferences.off') })}
            onOpen={() => {
              setDialog('twoFactor');
            }}
          />
          <DialogRow
            icon={IconFingerprint}
            label={t('menu.passkeys')}
            description={t('preferences.passkeysHint')}
            onOpen={() => {
              setDialog('passkeys');
            }}
          />
          {trust.data === 'untrusted' && (
            <DialogRow
              icon={IconCertificate}
              label={t('menu.trust')}
              description={t('preferences.trustHint')}
              onOpen={() => {
                setDialog('trust');
              }}
            />
          )}
        </Section>

        <Section title={t('preferences.desktop')}>
          <ChoiceRow
            icon={IconLanguage}
            label={t('menu.language')}
            description={t('preferences.thisDevice')}
            value={
              language === null
                ? t('preferences.languageAuto', { language: languageName(i18n.language) })
                : languageName(language)
            }
            menu={<LanguageOptions />}
          />
          <ChoiceRow
            icon={IconPalette}
            label={t('menu.theme')}
            description={t('preferences.thisDevice')}
            value={t(`menu.themeChoice.${theme}`)}
            menu={<ThemeOptions />}
          />
          <ChoiceRow
            icon={IconColorSwatch}
            label={t('menu.accent')}
            description={t('preferences.everyDevice')}
            value={t(`menu.accents.${accent}`)}
            menu={<AccentOptions />}
          />
          <ChoiceRow
            icon={IconWallpaper}
            label={t('menu.wallpaper')}
            description={t('preferences.everyDevice')}
            value={t(`menu.wallpapers.${wallpaper}`)}
            menu={<WallpaperOptions />}
          />
          {!compact && (
            <ChoiceRow
              icon={IconLayoutBottombar}
              label={t('menu.taskbar')}
              description={t('preferences.thisDevice')}
              value={t(`taskbar.position.${taskbar}`)}
              menu={
                <Menu
                  aria-label={t('menu.taskbar')}
                  selectionMode="single"
                  selectedKeys={[taskbar]}
                  onAction={(key) => {
                    const next = TASKBAR_POSITIONS.find((entry) => entry === key);
                    if (next !== undefined) {
                      dispatch({ type: 'setTaskbar', position: next });
                    }
                  }}
                >
                  {TASKBAR_POSITIONS.map((entry) => (
                    <MenuItem key={entry} id={entry}>
                      {t(`taskbar.position.${entry}`)}
                    </MenuItem>
                  ))}
                </Menu>
              }
            />
          )}
          <ChoiceRow
            icon={IconDevices}
            label={t('menu.getDesktop')}
            description={t('preferences.getDesktopHint')}
            menu={<DevicesMenu />}
          />
        </Section>

        <Section title={t('preferences.apps')}>
          <DialogRow
            icon={IconApps}
            label={t('menu.fileDefaults')}
            description={t('preferences.fileDefaultsHint')}
            onOpen={() => {
              setDialog('fileDefaults');
            }}
          />
          <DialogRow
            icon={IconLogin}
            label={t('menu.startupApps')}
            description={t('preferences.startupAppsHint')}
            onOpen={() => {
              setDialog('startupApps');
            }}
          />
        </Section>

        <Section title={t('preferences.notifications')}>
          <DialogRow
            icon={IconBell}
            label={t('menu.channels')}
            description={t('preferences.channelsHint')}
            onOpen={() => {
              setDialog('channels');
            }}
          />
        </Section>
      </div>

      <Suspense>
        {dialog === 'twoFactor' && user !== undefined && (
          <TotpSetup user={user} isOpen onClose={close} />
        )}
        {dialog === 'passkeys' && (
          <PasskeysDialog
            onClose={close}
            onTrust={() => {
              setDialog('trust');
            }}
          />
        )}
        {dialog === 'trust' && <TrustDialog onClose={close} />}
        {dialog === 'fileDefaults' && <FileDefaultsDialog onClose={close} />}
        {dialog === 'startupApps' && <StartupAppsDialog onClose={close} />}
        {dialog === 'channels' && <ChannelsDialog onClose={close} />}
      </Suspense>
    </div>
  );
}
