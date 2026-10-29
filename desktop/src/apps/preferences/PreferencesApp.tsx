import {
  IconApps,
  IconBell,
  IconCertificate,
  IconChevronRight,
  IconDevices,
  IconDeviceDesktop,
  IconEyeCheck,
  IconFingerprint,
  IconLanguage,
  IconLogin,
  IconMoon,
  IconPalette,
  IconShieldLock,
  IconSun,
  IconUser,
  type TablerIcon,
} from '../../ui/icons';
import { useQuery } from '@tanstack/react-query';
import { unwrap, type components } from '@pithyx/api-client';
import { lazy, Suspense, useId, useState, useSyncExternalStore, type ReactNode } from 'react';
import {
  Button as AriaButton,
  RadioButton,
  RadioField,
  RadioGroup as AriaRadioGroup,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import {
  AppFrame,
  FormRow,
  FormSection,
  MenuPopover,
  MenuTrigger,
  NavList,
  Radio,
  RadioGroup,
  type NavGroup,
} from '@pithyx/ui';
import { api } from '../../api/client';
import { fetchSession, queryKeys } from '../../api/queries';
import { initialsOf } from '../../auth/parts';
import { saveAppearance } from '../../desktop/AppearanceOptions';
import { DevicesMenu } from '../../desktop/DevicesMenu';
import {
  languageName,
  languages,
  loadTranslations,
  setLanguage,
  storedLanguage,
  type Language,
} from '../../i18n';
import { trustQuery } from '../../security/trust';
import { Wallpaper } from '../../shell/Wallpaper';
import type { TaskbarPosition } from '../../shell/types';
import {
  onThemeChoice,
  setTheme,
  storedTheme,
  themeChoices,
  type ThemeChoice,
} from '../../theme/preference';
import { ACCENTS, useAppearance, WALLPAPERS } from '../../theme/appearance';
import { useSizeClass } from '../../wm/sizeClass';
import { dispatch, useDesktop } from '../../wm/store';
import { useAppName } from '../names';
import styles from './PreferencesApp.module.css';

type User = components['schemas']['UserInfo'];

const TASKBAR_POSITIONS: readonly TaskbarPosition[] = ['bottom', 'top', 'left', 'right'];

// Light first, as in the mockups of D394; the system choice last.
const THEMES: readonly { id: ThemeChoice; icon: TablerIcon }[] = [
  { id: 'light', icon: IconSun },
  { id: 'dark', icon: IconMoon },
  { id: 'system', icon: IconDeviceDesktop },
];

const PROTOCOLS = ['smb', 'webdav', 'sftp', 'ftp'] as const;
const RIGHTS = ['desktopLogin', 'sshLogin', 'power'] as const;

type PageId =
  'account' | 'security' | 'appearance' | 'language' | 'notifications' | 'apps' | 'access';

const PAGES: readonly { id: PageId; icon: TablerIcon }[] = [
  { id: 'account', icon: IconUser },
  { id: 'security', icon: IconShieldLock },
  { id: 'appearance', icon: IconPalette },
  { id: 'language', icon: IconLanguage },
  { id: 'notifications', icon: IconBell },
  { id: 'apps', icon: IconApps },
  { id: 'access', icon: IconEyeCheck },
];

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

/** A titled list of rows that open a dialog or a menu. */
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

/** A titled choice drawn as pictures, such as the theme or the wallpaper; it applies at once. */
function PictureChoice({
  title,
  value,
  onChange,
  className,
  children,
}: {
  title: string;
  value: string;
  onChange: (value: string) => void;
  className: string;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <section className={styles.section} aria-labelledby={id}>
      <h2 id={id} className={styles.sectionTitle}>
        {title}
      </h2>
      <AriaRadioGroup
        aria-labelledby={id}
        orientation="horizontal"
        value={value}
        onChange={onChange}
        className={className}
      >
        {children}
      </AriaRadioGroup>
    </section>
  );
}

function AccountPage({ user }: { user: User | undefined }) {
  const { t } = useTranslation();
  if (user === undefined) {
    return null;
  }
  const name = user.displayName ?? user.username;
  const role = user.isAdmin ? t('roles.administrator') : t('roles.user');
  return (
    <div className={styles.page}>
      <div className={styles.profile}>
        <span className={styles.avatar} aria-hidden>
          {initialsOf(name)}
        </span>
        <span className={styles.profileText}>
          <span className={styles.profileName}>{name}</span>
          <span className={styles.profileFacts}>
            {user.username} · {role}
          </span>
        </span>
      </div>
      <FormSection title={t('preferences.pages.account')} note={t('preferences.accountNote')}>
        <FormRow label={t('preferences.name')}>{name}</FormRow>
        <FormRow label={t('preferences.username')}>{user.username}</FormRow>
        <FormRow label={t('preferences.role')}>{role}</FormRow>
        <FormRow label={t('preferences.groups')}>
          {user.groups.length === 0
            ? t('preferences.noGroups')
            : user.groups.map((group) => group.name).join(', ')}
        </FormRow>
      </FormSection>
    </div>
  );
}

function SecurityPage({
  user,
  untrusted,
  onOpen,
}: {
  user: User | undefined;
  untrusted: boolean;
  onOpen: (dialog: DialogId) => void;
}) {
  const { t } = useTranslation();
  return (
    <div className={styles.page}>
      <Section title={t('preferences.signIn')}>
        <DialogRow
          icon={IconShieldLock}
          label={t('menu.twoFactor')}
          description={t('preferences.twoFactorHint')}
          {...(user === undefined
            ? {}
            : { value: user.totpEnabled ? t('preferences.on') : t('preferences.off') })}
          onOpen={() => {
            onOpen('twoFactor');
          }}
        />
        <DialogRow
          icon={IconFingerprint}
          label={t('menu.passkeys')}
          description={t('preferences.passkeysHint')}
          onOpen={() => {
            onOpen('passkeys');
          }}
        />
        {untrusted && (
          <DialogRow
            icon={IconCertificate}
            label={t('menu.trust')}
            description={t('preferences.trustHint')}
            onOpen={() => {
              onOpen('trust');
            }}
          />
        )}
      </Section>
    </div>
  );
}

function AppearancePage() {
  const { t } = useTranslation();
  const theme = useSyncExternalStore(onThemeChoice, storedTheme);
  const { accent, wallpaper } = useAppearance();
  const taskbar = useDesktop((state) => state.taskbar);
  const compact = useSizeClass() === 'compact';
  return (
    <div className={styles.page}>
      <PictureChoice
        title={t('preferences.theme')}
        value={theme}
        onChange={(next) => {
          const choice = themeChoices.find((entry) => entry === next);
          if (choice !== undefined) {
            setTheme(choice);
          }
        }}
        className={styles.pictures}
      >
        {THEMES.map(({ id, icon: Icon }) => (
          <RadioField key={id} value={id}>
            <RadioButton className={styles.pictureCard}>
              <span className={styles.themePreview} data-preview={id} aria-hidden>
                <span className={styles.themeWindow} />
              </span>
              <span className={styles.pictureLabel}>
                <Icon size={16} stroke={1.75} aria-hidden />
                {t(`menu.themeChoice.${id}`)}
              </span>
            </RadioButton>
          </RadioField>
        ))}
      </PictureChoice>

      <PictureChoice
        title={t('menu.accent')}
        value={accent}
        onChange={(next) => {
          const choice = ACCENTS.find((entry) => entry === next);
          if (choice !== undefined) {
            void saveAppearance({ accent: choice });
          }
        }}
        className={styles.accents}
      >
        {ACCENTS.map((entry) => (
          <RadioField key={entry} value={entry}>
            <RadioButton className={styles.accent}>
              <span className={styles.accentSwatch} data-accent={entry} aria-hidden />
              <span className={styles.visuallyHidden}>{t(`menu.accents.${entry}`)}</span>
            </RadioButton>
          </RadioField>
        ))}
      </PictureChoice>

      <PictureChoice
        title={t('menu.wallpaper')}
        value={wallpaper}
        onChange={(next) => {
          const choice = WALLPAPERS.find((entry) => entry === next);
          if (choice !== undefined) {
            void saveAppearance({ wallpaper: choice });
          }
        }}
        className={styles.pictures}
      >
        {WALLPAPERS.map((entry) => (
          <RadioField key={entry} value={entry}>
            <RadioButton className={styles.pictureCard}>
              <span className={styles.wallpaperPreview} aria-hidden>
                <Wallpaper plain={entry === 'plain'} />
              </span>
              <span className={styles.pictureLabel}>{t(`menu.wallpapers.${entry}`)}</span>
            </RadioButton>
          </RadioField>
        ))}
      </PictureChoice>
      <p className={styles.note}>{t('preferences.appearanceNote')}</p>

      {!compact && (
        <FormSection title={t('preferences.desktopAndTaskbar')}>
          <FormRow label={t('menu.taskbar')} hint={t('preferences.thisDevice')}>
            <RadioGroup
              value={taskbar}
              onChange={(next) => {
                const position = TASKBAR_POSITIONS.find((entry) => entry === next);
                if (position !== undefined) {
                  dispatch({ type: 'setTaskbar', position });
                }
              }}
            >
              {TASKBAR_POSITIONS.map((entry) => (
                <Radio key={entry} value={entry}>
                  {t(`taskbar.position.${entry}`)}
                </Radio>
              ))}
            </RadioGroup>
          </FormRow>
        </FormSection>
      )}

      <Section title={t('preferences.otherDevices')}>
        <ChoiceRow
          icon={IconDevices}
          label={t('menu.getDesktop')}
          description={t('preferences.getDesktopHint')}
          menu={<DevicesMenu />}
        />
      </Section>
    </div>
  );
}

function LanguagePage() {
  const { t, i18n } = useTranslation();
  const [choice, setChoice] = useState<Language | 'auto'>(() => storedLanguage() ?? 'auto');
  return (
    <div className={styles.page}>
      <FormSection title={t('menu.language')}>
        <FormRow label={t('menu.language')} hint={t('preferences.thisDevice')}>
          <RadioGroup
            orientation="vertical"
            value={choice}
            onChange={(next) => {
              const language = languages.find((code) => code === next) ?? null;
              setChoice(language ?? 'auto');
              void setLanguage(language);
            }}
          >
            <Radio value="auto">
              {t('preferences.languageAuto', { language: languageName(i18n.language) })}
            </Radio>
            {languages.map((code) => (
              <Radio key={code} value={code} lang={code}>
                {languageName(code)}
              </Radio>
            ))}
          </RadioGroup>
        </FormRow>
      </FormSection>
      <p className={styles.note}>{t('preferences.regionNote')}</p>
    </div>
  );
}

function NotificationsPage({ onOpen }: { onOpen: (dialog: DialogId) => void }) {
  const { t } = useTranslation();
  return (
    <div className={styles.page}>
      <Section title={t('preferences.notifications')}>
        <DialogRow
          icon={IconBell}
          label={t('menu.channels')}
          description={t('preferences.channelsHint')}
          onOpen={() => {
            onOpen('channels');
          }}
        />
      </Section>
    </div>
  );
}

function AppsPage({ onOpen }: { onOpen: (dialog: DialogId) => void }) {
  const { t } = useTranslation();
  return (
    <div className={styles.page}>
      <Section title={t('preferences.apps')}>
        <DialogRow
          icon={IconApps}
          label={t('menu.fileDefaults')}
          description={t('preferences.fileDefaultsHint')}
          onOpen={() => {
            onOpen('fileDefaults');
          }}
        />
        <DialogRow
          icon={IconLogin}
          label={t('menu.startupApps')}
          description={t('preferences.startupAppsHint')}
          onOpen={() => {
            onOpen('startupApps');
          }}
        />
      </Section>
    </div>
  );
}

/** What the user may use, as the box decides it: shared folders, file services, apps and special rights. */
function AccessPage({ user }: { user: User | undefined }) {
  const { t } = useTranslation();
  const appName = useAppName();
  const shares = useQuery({
    queryKey: ['preferences', 'shares'],
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/shares', { signal })),
  });
  if (user === undefined) {
    return null;
  }
  const { permissions } = user;
  const folders = (shares.data ?? []).filter((share) => share.kind === 'share');
  const yes = (has: boolean) => (has ? t('preferences.yes') : t('preferences.no'));
  return (
    <div className={styles.page}>
      {permissions.admin && <p className={styles.note}>{t('preferences.adminHasAll')}</p>}
      <FormSection title={t('preferences.shares')}>
        {folders.length === 0 ? (
          <p className={styles.empty}>
            {shares.isPending ? t('preferences.loading') : t('preferences.noShares')}
          </p>
        ) : (
          folders.map((share) => (
            <FormRow key={share.id} label={share.name}>
              {share.access === 'readWrite' ? t('preferences.readWrite') : t('preferences.read')}
            </FormRow>
          ))
        )}
      </FormSection>
      <FormSection title={t('preferences.services')}>
        {PROTOCOLS.map((protocol) => (
          <FormRow key={protocol} label={t(`preferences.protocol.${protocol}`)}>
            {yes(permissions.admin || permissions.protocols.includes(`protocol:${protocol}`))}
          </FormRow>
        ))}
      </FormSection>
      <FormSection title={t('preferences.apps')}>
        <FormRow label={t('preferences.appsYouOpen')}>
          {permissions.apps.length === 0
            ? t('preferences.noApps')
            : permissions.apps.map((id) => appName(id)).join(', ')}
        </FormRow>
      </FormSection>
      <FormSection title={t('preferences.rights')}>
        {RIGHTS.map((right) => (
          <FormRow key={right} label={t(`preferences.right.${right}`)}>
            {yes(permissions.admin || permissions.rights.includes(`right:${right}`))}
          </FormRow>
        ))}
      </FormSection>
    </div>
  );
}

/**
 * Personal settings (D392): everything a user sets for themselves, for every user, in the frame of D394 with a page
 * per area. The account menu only opens it; a new personal setting gets a row on its page here.
 */
export function PreferencesApp() {
  const { t } = useTranslation();
  const [dialog, setDialog] = useState<DialogId | null>(null);
  const [current, setCurrent] = useState<PageId>('account');
  const [view, setView] = useState<'navigation' | 'page'>('navigation');
  const session = useQuery({
    queryKey: queryKeys.session,
    queryFn: ({ signal }) => fetchSession(signal),
  });
  const user: User | undefined = session.data;
  const trust = useQuery(trustQuery);
  const close = () => {
    setDialog(null);
  };

  const groups: NavGroup[] = [
    {
      id: 'pages',
      items: PAGES.map(({ id, icon: Icon }) => ({
        id,
        label: t(`preferences.pages.${id}`),
        icon: <Icon size={18} stroke={1.75} aria-hidden />,
      })),
    },
  ];

  return (
    <>
      <AppFrame
        className={styles.frame}
        view={view}
        onViewChange={setView}
        navigation={
          <NavList
            aria-label={t('preferences.navigation')}
            searchPlaceholder={t('preferences.search')}
            groups={groups}
            selectedId={current}
            onSelect={(id) => {
              const next = PAGES.find((page) => page.id === id);
              if (next !== undefined) {
                setCurrent(next.id);
                setView('page');
              }
            }}
          />
        }
        title={t(`preferences.pages.${current}`)}
      >
        {current === 'account' && <AccountPage user={user} />}
        {current === 'security' && (
          <SecurityPage user={user} untrusted={trust.data === 'untrusted'} onOpen={setDialog} />
        )}
        {current === 'appearance' && <AppearancePage />}
        {current === 'language' && <LanguagePage />}
        {current === 'notifications' && <NotificationsPage onOpen={setDialog} />}
        {current === 'apps' && <AppsPage onOpen={setDialog} />}
        {current === 'access' && <AccessPage user={user} />}
      </AppFrame>

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
    </>
  );
}
