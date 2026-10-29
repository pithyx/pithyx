import {
  IconArrowsUpDown,
  IconBell,
  IconBolt,
  IconCamera,
  IconCertificate,
  IconCircleCheck,
  IconDatabase,
  IconFolder,
  IconHome,
  IconInfoCircle,
  IconKey,
  IconLock,
  IconNetwork,
  IconPencil,
  IconPlugConnected,
  IconPlus,
  IconRefresh,
  IconShield,
  IconTrash,
  IconUserPlus,
  IconUsers,
  type TablerIcon,
} from '../ui/icons';
import { useState, type ReactNode } from 'react';
import {
  ActionBar,
  AppFrame,
  AttentionList,
  Button,
  Card,
  DataTable,
  FormRow,
  FormSection,
  Meter,
  NavList,
  Radio,
  RadioGroup,
  SearchField,
  Select,
  StatusBadge,
  SummaryTile,
  SummaryTiles,
  Switch,
  Tab,
  TabList,
  TabPanel,
  Tabs,
  TextField,
  Toolbar,
  ToolbarSeparator,
  UiProvider,
  type DataTableColumn,
  type NavGroup,
} from '@pithyx/ui';
import { LogoMark } from '../ui/Logo';
import { DesktopShell } from '../shell/DesktopShell';
import { Taskbar } from '../shell/Taskbar';
import { Window } from '../shell/Window';
import type { SceneContext } from './DesktopScreens';
import styles from './PatternScreens.module.css';
import { sampleTexts as t } from './patternSample';
import { now, useApps } from './sampleData';

type PageId = keyof typeof t.pages;

// The sample's buttons do nothing.
const ignore = () => undefined;

const pageIcons: Record<PageId, TablerIcon> = {
  overview: IconHome,
  general: IconInfoCircle,
  network: IconNetwork,
  security: IconShield,
  certificates: IconCertificate,
  updates: IconRefresh,
  notifications: IconBell,
  ups: IconBolt,
  users: IconUsers,
  shares: IconFolder,
  services: IconPlugConnected,
  pools: IconDatabase,
  keys: IconKey,
};

function item(id: PageId) {
  const Icon = pageIcons[id];
  return { id, label: t.pages[id], icon: <Icon stroke={1.75} /> };
}

const groups: NavGroup[] = [
  { id: 'home', items: [item('overview')] },
  {
    id: 'system',
    label: t.groups.system,
    items: [
      item('general'),
      item('network'),
      item('security'),
      item('certificates'),
      item('updates'),
      item('notifications'),
      item('ups'),
    ],
  },
  {
    id: 'access',
    label: t.groups.access,
    items: [item('users'), item('shares'), item('services')],
  },
  { id: 'storage', label: t.groups.storage, items: [item('pools'), item('keys')] },
];

function Overview({ open }: { open: (page: PageId) => void }) {
  return (
    <>
      <div className={styles.box}>
        <LogoMark size={56} />
        <div className={styles.boxName}>
          <h2>{t.box.name}</h2>
          <p>{t.box.facts}</p>
        </div>
        <dl className={styles.facts}>
          <dt>{t.box.runningFor}</dt>
          <dd>{t.box.runningForValue}</dd>
          <dt>{t.box.address}</dt>
          <dd className={styles.mono}>{t.box.addressValue}</dd>
          <dt>{t.box.timeZone}</dt>
          <dd>{t.box.timeZoneValue}</dd>
          <dt>{t.box.updates}</dt>
          <dd>{t.box.updatesValue}</dd>
        </dl>
      </div>
      <AttentionList
        items={t.attention.map((entry) => ({
          id: entry.id,
          tone: entry.tone,
          title: entry.title,
          description: entry.description,
          action: {
            label: entry.action,
            onPress: () => {
              open(entry.id === 'full' ? 'pools' : 'updates');
            },
          },
        }))}
      />
      <h2 className={styles.heading}>{t.allSettings}</h2>
      <div className={styles.categories}>
        {t.categories.map((category) => {
          const Icon = pageIcons[category.id];
          return (
            <button
              key={category.id}
              type="button"
              className={styles.category}
              onClick={() => {
                open(category.id);
              }}
            >
              <Icon className={styles.categoryIcon} size={24} stroke={1.5} aria-hidden />
              <span>
                <b>{t.pages[category.id]}</b>
                <small>{category.description}</small>
              </span>
            </button>
          );
        })}
      </div>
    </>
  );
}

function Network() {
  const [mode, setMode] = useState('manual');
  const [subnet, setSubnet] = useState<string | number | null>('24');
  const [ipv6, setIpv6] = useState(true);
  return (
    <Tabs>
      <TabList aria-label={t.pages.network}>
        <Tab id="interfaces">{t.network.tabs.interfaces}</Tab>
        <Tab id="dns">{t.network.tabs.dns}</Tab>
        <Tab id="firewall">{t.network.tabs.firewall}</Tab>
        <Tab id="proxy">{t.network.tabs.proxy}</Tab>
      </TabList>
      <TabPanel id="interfaces">
        <div className={styles.pair}>
          {t.network.interfaces.map((entry) => (
            <Card
              key={entry.id}
              icon={<IconNetwork stroke={1.5} />}
              title={entry.id}
              badge={<StatusBadge tone={entry.tone}>{entry.state}</StatusBadge>}
              facts={entry.facts}
            />
          ))}
        </div>
        <FormSection title={t.network.ipv4} note="enp1s0">
          <FormRow label={t.network.address}>
            <RadioGroup value={mode} onChange={setMode}>
              <Radio value="dhcp">{t.network.dhcp}</Radio>
              <Radio value="manual">{t.network.manual}</Radio>
            </RadioGroup>
          </FormRow>
          <FormRow label={t.network.ip}>
            <TextField defaultValue={t.network.ipValue} inputClassName={styles.mono} />
          </FormRow>
          <FormRow label={t.network.subnet} hint={t.network.subnetHint}>
            <Select options={t.network.subnets} value={subnet} onChange={setSubnet} monospace />
          </FormRow>
          <FormRow label={t.network.gateway}>
            <TextField defaultValue={t.network.gatewayValue} inputClassName={styles.mono} />
          </FormRow>
          <FormRow label={t.network.dns} hint={t.network.dnsHint}>
            <TextField defaultValue={t.network.dnsValue} inputClassName={styles.mono} />
          </FormRow>
        </FormSection>
        <FormSection
          title={t.network.ipv6}
          note="enp1s0"
          end={
            <Switch isSelected={ipv6} onChange={setIpv6}>
              {t.network.ipv6On}
            </Switch>
          }
        >
          <FormRow label={t.network.address}>
            <RadioGroup defaultValue="slaac" isDisabled={!ipv6}>
              <Radio value="slaac">{t.network.slaac}</Radio>
              <Radio value="dhcpv6">{t.network.dhcpv6}</Radio>
              <Radio value="manual">{t.network.manual}</Radio>
            </RadioGroup>
          </FormRow>
        </FormSection>
      </TabPanel>
      {(['dns', 'firewall', 'proxy'] as const).map((id) => (
        <TabPanel key={id} id={id}>
          <p className={styles.placeholder}>{t.placeholder}</p>
        </TabPanel>
      ))}
    </Tabs>
  );
}

type SampleUser = (typeof t.users.list)[number];

function Chips({ values }: { values: readonly string[] }) {
  return (
    <span className={styles.chips}>
      {values.map((value) => (
        <span key={value} className={styles.chip}>
          {value}
        </span>
      ))}
    </span>
  );
}

const userColumns: DataTableColumn<SampleUser>[] = [
  {
    id: 'name',
    label: t.users.columns.name,
    sortValue: (user) => user.name,
    cell: (user) => (
      <span className={styles.person}>
        <span className={styles.avatar} aria-hidden>
          {user.name.slice(0, 1)}
        </span>
        <span className={styles.personName}>
          {user.name}
          <small>{user.id}</small>
        </span>
      </span>
    ),
  },
  { id: 'groups', label: t.users.columns.groups, cell: (user) => <Chips values={user.groups} /> },
  {
    id: 'twoFactor',
    label: t.users.columns.twoFactor,
    width: 70,
    cell: (user) =>
      user.twoFactor ? (
        <IconCircleCheck
          className={styles.ok}
          size={16}
          stroke={1.75}
          aria-label={t.users.twoFactorOn}
        />
      ) : (
        <span aria-label={t.users.twoFactorOff}>-</span>
      ),
  },
  {
    id: 'lastSignIn',
    label: t.users.columns.lastSignIn,
    width: 130,
    muted: true,
    cell: (user) => user.lastSignIn,
  },
  {
    id: 'state',
    label: t.users.columns.state,
    width: 100,
    sortValue: (user) => (user.active ? 0 : 1),
    cell: (user) =>
      user.active ? (
        <StatusBadge tone="success">{t.users.active}</StatusBadge>
      ) : (
        <StatusBadge>{t.users.disabled}</StatusBadge>
      ),
  },
];

function UserDetails({ user }: { user: SampleUser }) {
  return (
    <>
      <div className={styles.person}>
        <span className={styles.avatarLarge} aria-hidden>
          {user.name.slice(0, 1)}
        </span>
        <span className={styles.personName}>
          <b>{user.name}</b>
          <small>
            {user.id}, {user.groups.join(', ')}
          </small>
        </span>
      </div>
      <h3 className={styles.detailsHeading}>{t.users.sharedFolders}</h3>
      <dl className={styles.pairs}>
        {t.users.access.map(([folder, access]) => (
          <div key={folder}>
            <dt>{folder}</dt>
            <dd>{access}</dd>
          </div>
        ))}
      </dl>
      <h3 className={styles.detailsHeading}>{t.users.services}</h3>
      <Chips values={t.users.serviceList} />
      <h3 className={styles.detailsHeading}>{t.users.apps}</h3>
      <Chips values={t.users.appList} />
      <h3 className={styles.detailsHeading}>{t.users.specialRights}</h3>
      <dl className={styles.pairs}>
        {t.users.rights.map(([right, value]) => (
          <div key={right}>
            <dt>{right}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      <div>
        <Button size="sm">
          <IconPencil size={16} stroke={1.75} aria-hidden />
          {t.users.editPermissions}
        </Button>
      </div>
    </>
  );
}

function Users() {
  const [selected, setSelected] = useState<string>('anna');
  const user = t.users.list.find((entry) => entry.id === selected);
  return (
    <Tabs>
      <TabList aria-label={t.pages.users}>
        <Tab id="users" count={t.users.list.length}>
          {t.users.tabs.users}
        </Tab>
        <Tab id="groups" count={0}>
          {t.users.tabs.groups}
        </Tab>
      </TabList>
      <TabPanel id="users">
        <Toolbar
          aria-label={t.users.toolbar}
          end={<SearchField label={t.users.search} placeholder={t.users.search} />}
        >
          <Button variant="primary">
            <IconUserPlus size={18} stroke={1.75} aria-hidden />
            {t.users.create}
          </Button>
          <ToolbarSeparator />
          <Button>
            <IconPencil size={18} stroke={1.75} aria-hidden />
            {t.users.edit}
          </Button>
          <Button>
            <IconLock size={18} stroke={1.75} aria-hidden />
            {t.users.disable}
          </Button>
          <Button>
            <IconTrash size={18} stroke={1.75} aria-hidden />
            {t.users.delete}
          </Button>
        </Toolbar>
        <DataTable
          aria-label={t.users.tabs.users}
          columns={userColumns}
          items={t.users.list}
          getKey={(entry) => entry.id}
          density="comfortable"
          selectionMode="single"
          selectedKeys={new Set([selected])}
          onSelectionChange={(keys) => {
            const [first] = keys === 'all' ? [] : [...keys];
            if (first !== undefined) {
              setSelected(String(first));
            }
          }}
          details={user === undefined ? undefined : <UserDetails user={user} />}
          detailsLabel={t.users.details}
        />
      </TabPanel>
      <TabPanel id="groups">
        <DataTable
          aria-label={t.users.tabs.groups}
          columns={[{ id: 'name', label: t.users.columns.name, cell: (name: string) => name }]}
          items={[]}
          getKey={(name) => name}
          emptyState={
            <>
              {t.users.groupsEmpty}
              <Button variant="primary">
                <IconPlus size={18} stroke={1.75} aria-hidden />
                {t.users.createGroup}
              </Button>
            </>
          }
        />
      </TabPanel>
    </Tabs>
  );
}

type SampleDisk = (typeof t.pools.diskList)[number];

const diskColumns: DataTableColumn<SampleDisk>[] = [
  { id: 'disk', label: t.pools.columns.disk, cell: (disk) => disk.name },
  { id: 'serial', label: t.pools.columns.serial, width: 140, muted: true, cell: (d) => d.serial },
  {
    id: 'temperature',
    label: t.pools.columns.temperature,
    width: 120,
    align: 'end',
    cell: (disk) => `${String(disk.temperature)} °C`,
  },
  {
    id: 'state',
    label: t.pools.columns.state,
    width: 90,
    cell: () => <StatusBadge tone="success">{t.pools.ok}</StatusBadge>,
  },
];

const poolActionIcons: TablerIcon[] = [
  IconRefresh,
  IconCamera,
  IconPlus,
  IconArrowsUpDown,
  IconLock,
];

function Pools() {
  return (
    <>
      <SummaryTiles>
        {t.pools.tiles.map((tile) => (
          <SummaryTile key={tile.label} {...tile} />
        ))}
      </SummaryTiles>
      <Toolbar aria-label={t.pools.toolbar}>
        <Button variant="primary">
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t.pools.create}
        </Button>
        <Button>
          <IconRefresh size={18} stroke={1.75} aria-hidden />
          {t.pools.scrubAll}
        </Button>
        <Button>
          <IconCamera size={18} stroke={1.75} aria-hidden />
          {t.pools.snapshotAll}
        </Button>
      </Toolbar>
      {t.pools.list.map((pool) => (
        <Card
          key={pool.id}
          icon={<IconDatabase stroke={1.5} />}
          title={pool.id}
          badge={<StatusBadge tone={pool.tone}>{pool.state}</StatusBadge>}
          facts={pool.facts}
          defaultExpanded={pool.expanded}
          meter={
            <Meter
              aria-label={`${t.pools.space} ${pool.id}`}
              value={pool.percent}
              start={pool.used}
              end={pool.free}
              valueLabel={`${pool.used}, ${pool.free}`}
              tone={pool.tone === 'warning' ? 'warning' : 'accent'}
            />
          }
          actions={t.pools.actions.map((action, index) => {
            const Icon = poolActionIcons[index] ?? IconRefresh;
            return (
              <Button key={action} size="sm">
                <Icon size={16} stroke={1.75} aria-hidden />
                {action}
              </Button>
            );
          })}
        >
          <div className={styles.flush}>
            <DataTable
              aria-label={`${t.pools.disks} ${pool.id}`}
              columns={diskColumns}
              items={pool.id === 'tank' ? t.pools.diskList : []}
              getKey={(disk) => disk.id}
            />
          </div>
        </Card>
      ))}
    </>
  );
}

/** The sample Settings app of the design pass: one AppFrame with the grouped navigation and four sample pages. */
export function SampleSettings({ initial }: { initial: PageId }) {
  const [page, setPage] = useState<PageId>(initial);
  const [view, setView] = useState<'navigation' | 'page'>('page');
  const footer =
    page === 'network' ? (
      <ActionBar message={t.network.applyNote} onApply={ignore} onDiscard={ignore} />
    ) : undefined;
  const content: Partial<Record<PageId, ReactNode>> = {
    overview: <Overview open={setPage} />,
    network: <Network />,
    users: <Users />,
    pools: <Pools />,
  };
  return (
    <AppFrame
      view={view}
      onViewChange={setView}
      navigation={
        <NavList
          aria-label={t.app}
          groups={groups}
          selectedId={page}
          onSelect={(id) => {
            setPage(id as PageId);
          }}
        />
      }
      title={t.pages[page]}
      footer={footer}
    >
      {content[page] ?? <p className={styles.placeholder}>{t.placeholder}</p>}
    </AppFrame>
  );
}

/** The sample Settings app maximized on the desktop, like a core app scene. */
export function PatternAppScene({ context, page }: { context: SceneContext; page: PageId }) {
  const apps = useApps();
  const info = apps.settings;
  return (
    <UiProvider locale={context.locale}>
      <DesktopShell
        taskbarPosition={context.taskbar}
        taskbar={
          <Taskbar
            position={context.taskbar}
            apps={[{ ...info, state: 'focused' }]}
            startOpen={false}
            jobs={0}
            unread={0}
            notificationsOpen={false}
            switcherOpen={false}
            now={now}
            locale={context.locale}
          />
        }
      >
        <Window
          app={info}
          title={info.name}
          focused
          maximized
          compactActive
          layer={1}
          geometry={{ x: 0, y: 0, width: 100, height: 100 }}
        >
          <SampleSettings initial={page} />
        </Window>
      </DesktopShell>
    </UiProvider>
  );
}

function Specimen({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className={styles.specimen}>
      <h2 className={styles.specimenTitle}>{title}</h2>
      {children}
    </section>
  );
}

/** Every pattern of the design pass on its own, for review in light and dark and every accent. */
export function PatternsScreen({ context }: { context: SceneContext }) {
  const [page, setPage] = useState<string>('network');
  return (
    <UiProvider locale={context.locale}>
      <main className={styles.specimens}>
        <h1 className={styles.specimensTitle}>{t.specimens.title}</h1>
        <Specimen title={t.specimens.badges}>
          <div className={styles.row}>
            {t.specimens.badgeTones.map(([tone, label]) => (
              <StatusBadge key={tone} tone={tone}>
                {label}
              </StatusBadge>
            ))}
          </div>
        </Specimen>
        <Specimen title={t.specimens.tiles}>
          <SummaryTiles>
            {t.pools.tiles.map((tile) => (
              <SummaryTile key={tile.label} {...tile} />
            ))}
          </SummaryTiles>
        </Specimen>
        <Specimen title={t.specimens.navList}>
          <div className={styles.navSample}>
            <NavList
              aria-label={t.app}
              groups={groups.map((group) =>
                group.id === 'storage'
                  ? {
                      ...group,
                      items: group.items.map((entry) =>
                        entry.id === 'pools'
                          ? { ...entry, count: 1, countLabel: '1 problem' }
                          : entry,
                      ),
                    }
                  : group,
              )}
              selectedId={page}
              onSelect={setPage}
            />
          </div>
        </Specimen>
        <Specimen title={t.specimens.attention}>
          <AttentionList
            headingLevel={3}
            items={t.attention.map((entry) => ({
              id: entry.id,
              tone: entry.id === 'full' ? 'danger' : entry.tone,
              title: entry.title,
              description: entry.description,
              action: { label: entry.action, onPress: ignore },
            }))}
          />
        </Specimen>
        <Specimen title={t.specimens.table}>
          <Users />
        </Specimen>
        <Specimen title={t.specimens.card}>
          <Pools />
        </Specimen>
        <Specimen title={t.specimens.form}>
          <div className={styles.formSample}>
            <Network />
            <ActionBar message={t.network.applyNote} onApply={ignore} onDiscard={ignore} />
          </div>
        </Specimen>
      </main>
    </UiProvider>
  );
}
