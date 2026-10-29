import type { ReactNode } from 'react';
import {
  Separator,
  Tab as AriaTab,
  TabList as AriaTabList,
  TabPanel as AriaTabPanel,
  Tabs as AriaTabs,
  Toolbar as AriaToolbar,
  type TabListProps as AriaTabListProps,
  type TabPanelProps as AriaTabPanelProps,
  type TabProps as AriaTabProps,
  type TabsProps as AriaTabsProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './Toolbar.module.css';

/** The props of {@link Toolbar}. */
export interface ToolbarProps {
  /** The accessible name, for example "Users". */
  'aria-label': string;
  /** The actions from the start: the primary action first, then the others, grouped by `ToolbarSeparator`. */
  children: ReactNode;
  /** The end section: view options, the details toggle, a search field. */
  end?: ReactNode;
  /** `plain` (the default) sits on the page; `bar` is a full-width strip with a background and borders. */
  variant?: 'plain' | 'bar';
  /** Extra class names, added after the toolbar's own. */
  className?: string;
}

/**
 * The toolbar of a view (D394): the primary action first as the one filled `Button`, then the other actions as buttons
 * with icon and label, groups split by {@link ToolbarSeparator}, and the end section for view options and search. The
 * arrow keys move between its controls, Tab leaves it. Buttons inside take the toolbar's control height.
 *
 * @param props - The name, the actions, the end section and the look.
 * @returns The toolbar.
 *
 * @example
 * ```tsx
 * <Toolbar aria-label="Users" end={<SearchField label="Search users" />}>
 *   <Button variant="primary" onPress={create}><IconUserPlus size={18} />Create user</Button>
 *   <ToolbarSeparator />
 *   <Button onPress={edit}><IconPencil size={18} />Edit</Button>
 * </Toolbar>
 * ```
 */
export function Toolbar({
  'aria-label': label,
  children,
  end,
  variant = 'plain',
  className,
}: ToolbarProps) {
  return (
    <AriaToolbar
      aria-label={label}
      className={cx(styles.toolbar, variant === 'bar' && styles.bar, className)}
    >
      {children}
      {end !== undefined && <div className={styles.end}>{end}</div>}
    </AriaToolbar>
  );
}

/**
 * A vertical line between groups of a {@link Toolbar}.
 *
 * @returns The separator.
 */
export function ToolbarSeparator() {
  return <Separator orientation="vertical" className={styles.separator} />;
}

/** The props of {@link Tabs}: React Aria's tabs props. */
export interface TabsProps extends Omit<AriaTabsProps, 'className'> {
  /** Extra class names, added after the tabs' own. */
  className?: string;
}

/**
 * Tabs below a page title: a {@link TabList} of {@link Tab}s and one {@link TabPanel} per tab, each with its own
 * toolbar and content.
 *
 * @param props - React Aria's tabs props (`selectedKey`, `onSelectionChange`, `defaultSelectedKey`).
 * @returns The tabs.
 *
 * @example
 * ```tsx
 * <Tabs>
 *   <TabList aria-label="Accounts">
 *     <Tab id="users" count={7}>Users</Tab>
 *     <Tab id="groups" count={4}>Groups</Tab>
 *   </TabList>
 *   <TabPanel id="users">...</TabPanel>
 *   <TabPanel id="groups">...</TabPanel>
 * </Tabs>
 * ```
 */
export function Tabs({ className, ...props }: TabsProps) {
  return <AriaTabs {...props} className={cx(styles.tabs, className)} />;
}

/** The props of {@link TabList}. */
export interface TabListProps<T> extends Omit<AriaTabListProps<T>, 'className'> {
  /** Extra class names, added after the list's own. */
  className?: string;
}

/**
 * The row of tabs, underlined across the page.
 *
 * @param props - React Aria's tab list props; give it an `aria-label`.
 * @returns The tab list.
 */
export function TabList<T extends object>({ className, ...props }: TabListProps<T>) {
  return <AriaTabList {...props} className={cx(styles.tabList, className)} />;
}

/** The props of {@link Tab}. */
export interface TabProps extends Omit<AriaTabProps, 'className' | 'children'> {
  /** The tab's name. */
  children: ReactNode;
  /** A count after the name, such as the number of users. */
  count?: number;
}

/**
 * A tab; the selected one is underlined in the accent.
 *
 * @param props - React Aria's tab props with the `id` of its panel, the name and an optional count.
 * @returns The tab.
 */
export function Tab({ children, count, ...props }: TabProps) {
  return (
    <AriaTab {...props} className={styles.tab}>
      {children}
      {count !== undefined && <span className={styles.count}>{count}</span>}
    </AriaTab>
  );
}

/** The props of {@link TabPanel}. */
export interface TabPanelProps extends Omit<AriaTabPanelProps, 'className'> {
  /** Extra class names, added after the panel's own. */
  className?: string;
}

/**
 * The content of a tab.
 *
 * @param props - React Aria's tab panel props with the `id` of its tab.
 * @returns The panel.
 */
export function TabPanel({ className, ...props }: TabPanelProps) {
  return <AriaTabPanel {...props} className={cx(styles.panel, className)} />;
}
