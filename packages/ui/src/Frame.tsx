import { IconChevronLeft, IconChevronRight } from './icons';
import {
  createContext,
  useContext,
  useEffect,
  useId,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { cx } from './cx';
import styles from './Frame.module.css';
import { SearchField } from './TextField';
import { useUiTexts } from './UiProvider';

/** Which half of an {@link AppFrame} the compact size class shows: the navigation list or the page. */
export type AppFrameView = 'navigation' | 'page';

const FrameContext = createContext<{ showPage: () => void } | null>(null);

/** The props of {@link AppFrame}. */
export interface AppFrameProps {
  /** The navigation column, usually a {@link NavList}. Leave it out for an app with one view. */
  navigation?: ReactNode;
  /** The page title, shown as the page's heading. */
  title?: ReactNode;
  /** Content at the end of the title row, for example a wide search field over the whole app. */
  titleEnd?: ReactNode;
  /** The page's toolbar, usually a `Toolbar`, between the title and the content. */
  toolbar?: ReactNode;
  /** The page's content. */
  children: ReactNode;
  /** A bar fixed below the scrolling content, usually an `ActionBar`. */
  footer?: ReactNode;
  /** A status line at the bottom, for counts, the selection or **Read only**. */
  statusBar?: ReactNode;
  /**
   * `page` (the default) scrolls the title, toolbar and content together, as on a settings page; `content` keeps them
   * fixed and leaves the scrolling to the content, for a long table that scrolls on its own.
   */
  scroll?: 'page' | 'content';
  /** The half shown in the compact size class, for a controlled frame. */
  view?: AppFrameView;
  /** The half shown first in the compact size class; `navigation` when left out. */
  defaultView?: AppFrameView;
  /** Called when the user drills into a page or goes back to the navigation. */
  onViewChange?: (view: AppFrameView) => void;
  /** Names the page shown; when it changes, the page scrolls back to its top. */
  pageKey?: string;
  /** Extra class names, added after the frame's own. */
  className?: string;
}

/**
 * The frame of an app with more than one view (D394): a navigation column on the left and a content area with the
 * page title, the toolbar, the content, an optional bar fixed at the bottom and an optional status bar. It adapts to its
 * own width, not the screen's: below 600 px the navigation becomes a list page, a choice in a {@link NavList} opens
 * the page, and a back button leads back.
 *
 * @param props - The navigation, the page's title, toolbar and content, and the optional footer and status bar.
 * @returns The frame.
 *
 * @example
 * ```tsx
 * <AppFrame
 *   navigation={<NavList aria-label="Settings" groups={groups} selectedId={page} onSelect={setPage} />}
 *   title="Network"
 *   footer={<ActionBar message="Applies to every device." onApply={apply} onDiscard={reset} />}
 * >
 *   <FormSection title="IPv4">...</FormSection>
 * </AppFrame>
 * ```
 */
export function AppFrame({
  navigation,
  title,
  titleEnd,
  toolbar,
  children,
  footer,
  statusBar,
  scroll = 'page',
  view: controlledView,
  defaultView = 'navigation',
  onViewChange,
  pageKey,
  className,
}: AppFrameProps) {
  const texts = useUiTexts();
  const [ownView, setOwnView] = useState<AppFrameView>(defaultView);
  const view = controlledView ?? ownView;
  const titleRef = useRef<HTMLHeadingElement>(null);
  const navigationRef = useRef<HTMLDivElement>(null);
  const scrollerRef = useRef<HTMLDivElement>(null);
  const moved = useRef(false);

  const change = (next: AppFrameView) => {
    moved.current = next !== view;
    setOwnView(next);
    onViewChange?.(next);
  };

  // In the compact size class one half replaces the other, so focus follows the user into the half that appeared.
  useEffect(() => {
    if (!moved.current) {
      return;
    }
    moved.current = false;
    const column = navigationRef.current;
    if (column === null || column.checkVisibility()) {
      if (view === 'navigation') {
        column?.querySelector<HTMLElement>('[aria-current="page"], button')?.focus();
      }
      return;
    }
    titleRef.current?.focus();
  }, [view]);

  // Another page starts at its top, not where the previous one was scrolled to.
  useEffect(() => {
    if (scrollerRef.current !== null) {
      scrollerRef.current.scrollTop = 0;
    }
  }, [pageKey]);

  const hasNavigation = navigation !== undefined;
  return (
    <div className={cx(styles.frame, className)}>
      <div className={styles.layout} data-view={hasNavigation ? view : 'page'} data-scroll={scroll}>
        {hasNavigation && (
          <div ref={navigationRef} className={styles.navigation}>
            <FrameContext
              value={{
                showPage: () => {
                  change('page');
                },
              }}
            >
              {navigation}
            </FrameContext>
          </div>
        )}
        <div className={styles.main}>
          {/* Focusable, so a page without controls (facts only) still scrolls with the keyboard. */}
          <div ref={scrollerRef} className={styles.scroller} tabIndex={0}>
            {(title !== undefined || hasNavigation) && (
              <div className={styles.header}>
                {hasNavigation && (
                  <AriaButton
                    className={styles.back}
                    onPress={() => {
                      change('navigation');
                    }}
                  >
                    <IconChevronLeft size={18} stroke={1.75} aria-hidden />
                    {texts.frame.back}
                  </AriaButton>
                )}
                {(title !== undefined || titleEnd !== undefined) && (
                  <div className={styles.titleRow}>
                    {title !== undefined && (
                      <h1 ref={titleRef} className={styles.title} tabIndex={-1}>
                        {title}
                      </h1>
                    )}
                    {titleEnd !== undefined && <div className={styles.titleEnd}>{titleEnd}</div>}
                  </div>
                )}
              </div>
            )}
            {toolbar !== undefined && <div className={styles.toolbar}>{toolbar}</div>}
            <div className={styles.content}>{children}</div>
          </div>
          {footer}
          {statusBar !== undefined && <div className={styles.statusBar}>{statusBar}</div>}
        </div>
      </div>
    </div>
  );
}

/** An item of a {@link NavList}. */
export interface NavItem {
  /** The id passed to `onSelect`. */
  id: string;
  /** The visible name, also matched by the search. */
  label: string;
  /** An icon before the name, about 18 px. */
  icon?: ReactNode;
  /** A small count after the name, for example unread or open problems. */
  count?: number;
  /** What the count means, announced with it ("3 problems"); without it only the number is announced. */
  countLabel?: string;
  /** A marker after the name, such as a lock for a read-only place; give it an accessible name. */
  marker?: ReactNode;
  /** More words the search matches, for example the settings on the page. */
  keywords?: readonly string[];
  /** Shown but not choosable. */
  isDisabled?: boolean;
}

/** A group of a {@link NavList}, with an optional small heading. */
export interface NavGroup {
  /** A stable id, used as the React key. */
  id: string;
  /** The small uppercase heading; a group without one has none (for example the first group with Overview). */
  label?: string;
  /** The items in order. */
  items: readonly NavItem[];
}

/** The props of {@link NavList}. */
export interface NavListProps {
  /** The name of the navigation landmark, for example the app's name. */
  'aria-label': string;
  /** The groups in order. */
  groups: readonly NavGroup[];
  /** The id of the current item, marked with the accent bar and announced as the current page. */
  selectedId: string | null;
  /** Called when the user chooses an item. */
  onSelect: (id: string) => void;
  /** Whether a search field above the groups filters the items; true when left out. */
  searchable?: boolean;
  /** The placeholder of the search field; the package's "Search" when left out. */
  searchPlaceholder?: string;
  /** Extra class names, added after the list's own. */
  className?: string;
}

function matches(item: NavItem, query: string): boolean {
  const needle = query.trim().toLocaleLowerCase();
  return (
    needle === '' ||
    [item.label, ...(item.keywords ?? [])].some((text) => text.toLocaleLowerCase().includes(needle))
  );
}

/**
 * The navigation column of an {@link AppFrame} (D394): a search field, groups under small headings and items with an
 * icon, a name and an optional count. The current item has a background and an accent bar and is announced as the
 * current page. Inside an `AppFrame` in the compact size class, choosing an item opens its page.
 *
 * @param props - The groups, the current item and the handler, plus the search options.
 * @returns The navigation landmark.
 *
 * @example
 * ```tsx
 * <NavList
 *   aria-label="Settings"
 *   groups={[{ id: 'system', label: 'System', items: [{ id: 'network', label: 'Network', icon: <IconNetwork /> }] }]}
 *   selectedId={page}
 *   onSelect={setPage}
 * />
 * ```
 */
export function NavList({
  'aria-label': label,
  groups,
  selectedId,
  onSelect,
  searchable = true,
  searchPlaceholder,
  className,
}: NavListProps) {
  const texts = useUiTexts();
  const frame = useContext(FrameContext);
  const [query, setQuery] = useState('');
  const baseId = useId();
  const shown = groups
    .map((group) => ({ ...group, items: group.items.filter((item) => matches(item, query)) }))
    .filter((group) => group.items.length > 0);

  return (
    <nav aria-label={label} className={cx(styles.nav, className)}>
      {searchable && (
        <SearchField
          label={searchPlaceholder ?? texts.nav.search}
          placeholder={searchPlaceholder ?? texts.nav.search}
          value={query}
          onChange={setQuery}
          className={styles.navSearch}
        />
      )}
      {shown.length === 0 && <p className={styles.navEmpty}>{texts.nav.noResults}</p>}
      {shown.map((group) => {
        const headingId = `${baseId}-${group.id}`;
        return (
          <div key={group.id} className={styles.navGroup}>
            {group.label !== undefined && (
              <h2 id={headingId} className={styles.navHeading}>
                {group.label}
              </h2>
            )}
            <ul
              className={styles.navItems}
              {...(group.label === undefined ? {} : { 'aria-labelledby': headingId })}
            >
              {group.items.map((item) => (
                <li key={item.id}>
                  <AriaButton
                    className={styles.navItem}
                    {...(item.id === selectedId ? { 'aria-current': 'page' as const } : {})}
                    isDisabled={item.isDisabled ?? false}
                    onPress={() => {
                      onSelect(item.id);
                      frame?.showPage();
                    }}
                  >
                    {item.icon !== undefined && (
                      <span className={styles.navIcon} aria-hidden>
                        {item.icon}
                      </span>
                    )}
                    <span className={styles.navLabel}>{item.label}</span>
                    {item.marker}
                    {item.count !== undefined && item.count > 0 && (
                      <span className={styles.navCount}>
                        <span aria-hidden>{item.count}</span>
                        <span className={styles.visuallyHidden}>
                          {item.countLabel ?? String(item.count)}
                        </span>
                      </span>
                    )}
                    <IconChevronRight
                      className={styles.navChevron}
                      size={16}
                      stroke={1.75}
                      aria-hidden
                    />
                  </AriaButton>
                </li>
              ))}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}
