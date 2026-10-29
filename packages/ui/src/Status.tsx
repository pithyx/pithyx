import { IconAlertCircle, IconAlertTriangle, IconChevronDown, IconInfoCircle } from './icons';
import { useId, type ReactNode } from 'react';
import {
  Button as AriaButton,
  Disclosure,
  DisclosurePanel,
  Meter as AriaMeter,
  type MeterProps as AriaMeterProps,
} from 'react-aria-components';
import { Button } from './Button';
import { cx } from './cx';
import styles from './Status.module.css';
import { useUiTexts } from './UiProvider';

/** The tone of a state: green success, orange warning, red danger, the accent for info, grey neutral. */
export type StatusTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

/**
 * A state in words on a tinted background (D394), such as **Healthy**, **85 % full** or **Disabled**. The words carry
 * the state; the color only repeats it.
 *
 * @param props - The words and the tone.
 * @returns The badge.
 *
 * @example
 * ```tsx
 * <StatusBadge tone="success">Healthy</StatusBadge>
 * ```
 */
export function StatusBadge({
  children,
  tone = 'neutral',
  className,
}: {
  /** The state in words. */
  children: ReactNode;
  /** The tone; `neutral` when left out. */
  tone?: StatusTone;
  /** Extra class names, added after the badge's own. */
  className?: string;
}) {
  return (
    <span className={cx(styles.badge, className)} data-tone={tone}>
      {children}
    </span>
  );
}

/** The props of {@link Meter}. */
export interface MeterProps extends Omit<AriaMeterProps, 'className' | 'children'> {
  /** The accessible name, for example "Space of tank". */
  'aria-label': string;
  /** The text above the start of the bar, for example "1.3 TB used". */
  start?: ReactNode;
  /** The text above the end of the bar, for example "2.3 TB free". */
  end?: ReactNode;
  /**
   * The value announced to screen readers, for example "1.3 TB used, 2.3 TB free"; the percentage when left out.
   * `start` and `end` are not announced, since a meter's content is not read.
   */
  valueLabel?: string;
  /** `accent` (the default), `warning` or `danger` for a quantity that needs attention. */
  tone?: 'accent' | 'warning' | 'danger';
  /** Extra class names, added after the meter's own. */
  className?: string;
}

/**
 * A quantity within a known range, such as the space of a pool, with optional texts above both ends of the bar.
 *
 * @param props - React Aria's meter props (`value`, `minValue`, `maxValue`) plus the name, texts and tone.
 * @returns The meter.
 *
 * @example
 * ```tsx
 * <Meter aria-label="Space of tank" value={41} start="1.3 TB used" end="2.3 TB free" valueLabel="1.3 TB used, 2.3 TB free" />
 * ```
 */
export function Meter({ start, end, tone = 'accent', className, ...props }: MeterProps) {
  return (
    <AriaMeter {...props} className={cx(styles.meter, className)}>
      {({ percentage }) => (
        <>
          {(start !== undefined || end !== undefined) && (
            <span className={styles.meterTexts}>
              <span>{start}</span>
              <span>{end}</span>
            </span>
          )}
          <span className={styles.meterTrack}>
            <span
              className={styles.meterFill}
              data-tone={tone}
              style={{ width: `${String(percentage)}%` }}
            />
          </span>
        </>
      )}
    </AriaMeter>
  );
}

/** The props of {@link Card}. */
export interface CardProps {
  /** The object's icon, about 24 px. */
  icon?: ReactNode;
  /** The object's name, the card's heading. */
  title: ReactNode;
  /** The heading level of the title; 3 when left out (below the page title and a section heading). */
  headingLevel?: 2 | 3 | 4;
  /** The state, usually a {@link StatusBadge}, after the name. */
  badge?: ReactNode;
  /** One line of facts below the name. */
  facts?: ReactNode;
  /** A {@link Meter} on the right, where a quantity fits. */
  meter?: ReactNode;
  /** The details shown when the card is expanded. A card without details and actions does not expand. */
  children?: ReactNode;
  /** The object's actions, shown below the details when the card is expanded. */
  actions?: ReactNode;
  /** Whether the card is expanded, for a controlled card. */
  isExpanded?: boolean;
  /** Whether the card starts expanded. */
  defaultExpanded?: boolean;
  /** Called when the user expands or collapses the card. */
  onExpandedChange?: (isExpanded: boolean) => void;
  /** Extra class names, added after the card's own. */
  className?: string;
}

/**
 * An object with a state, such as a pool, an interface or an app (D394): icon, name, a state badge, one line of facts,
 * an optional meter and a chevron that expands the details and the object's actions.
 *
 * @param props - The icon, name, badge, facts and meter, plus the details and actions behind the chevron.
 * @returns The card.
 *
 * @example
 * ```tsx
 * <Card
 *   icon={<IconDatabase />}
 *   title="tank"
 *   badge={<StatusBadge tone="success">Healthy</StatusBadge>}
 *   facts="Mirror, 2 x 4 TB, encrypted"
 *   meter={<Meter aria-label="Space of tank" value={41} start="1.3 TB used" end="2.3 TB free" />}
 *   actions={<Button size="sm" onPress={scrub}>Scrub</Button>}
 * >
 *   <DiskTable pool="tank" />
 * </Card>
 * ```
 */
export function Card({
  icon,
  title,
  headingLevel = 3,
  badge,
  facts,
  meter,
  children,
  actions,
  isExpanded,
  defaultExpanded,
  onExpandedChange,
  className,
}: CardProps) {
  const texts = useUiTexts();
  const titleId = useId();
  const detailsId = useId();
  const Heading = `h${String(headingLevel)}` as 'h2' | 'h3' | 'h4';
  const expandable = children !== undefined || actions !== undefined;

  const header = (
    <div className={styles.cardHeader}>
      {icon !== undefined && (
        <span className={styles.cardIcon} aria-hidden>
          {icon}
        </span>
      )}
      <div className={styles.cardMain}>
        <Heading id={titleId} className={styles.cardTitle}>
          <span>{title}</span>
          {badge}
        </Heading>
        {facts !== undefined && <p className={styles.cardFacts}>{facts}</p>}
      </div>
      {meter !== undefined && <div className={styles.cardMeter}>{meter}</div>}
      {expandable && (
        <AriaButton
          slot="trigger"
          className={styles.cardToggle}
          aria-labelledby={`${detailsId} ${titleId}`}
        >
          <span id={detailsId} hidden>
            {texts.card.details}
          </span>
          <IconChevronDown className={styles.cardChevron} size={18} stroke={1.75} aria-hidden />
        </AriaButton>
      )}
    </div>
  );

  if (!expandable) {
    return <div className={cx(styles.card, className)}>{header}</div>;
  }
  return (
    <Disclosure
      className={cx(styles.card, className)}
      {...(isExpanded === undefined ? {} : { isExpanded })}
      {...(defaultExpanded === undefined ? {} : { defaultExpanded })}
      {...(onExpandedChange === undefined ? {} : { onExpandedChange })}
    >
      {header}
      <DisclosurePanel className={styles.cardPanel}>
        {children}
        {actions !== undefined && <div className={styles.cardActions}>{actions}</div>}
      </DisclosurePanel>
    </Disclosure>
  );
}

/** A problem in an {@link AttentionList}. */
export interface AttentionItem {
  /** A stable id, used as the React key. */
  id: string;
  /** `danger` for a failure, `warning` for a risk, `info` for a step the user should take. */
  tone: 'danger' | 'warning' | 'info';
  /** The problem in one line. */
  title: ReactNode;
  /** Why it matters or what to do, in one line. */
  description?: ReactNode;
  /** The one action that leads to the fix. */
  action?: {
    /** The button's label, for example "Open Pools". */
    label: string;
    /** Called when the user presses the button. */
    onPress: () => void;
  };
}

const toneIcons = {
  danger: IconAlertCircle,
  warning: IconAlertTriangle,
  info: IconInfoCircle,
} as const;

/** The props of {@link AttentionList}. */
export interface AttentionListProps {
  /** The problems, the most severe first. */
  items: readonly AttentionItem[];
  /** The heading; the package's "Needs attention" when left out. */
  title?: ReactNode;
  /** The heading level; 2 when left out. */
  headingLevel?: 2 | 3;
  /** Extra class names, added after the list's own. */
  className?: string;
}

/**
 * **Needs attention** on an overview page (D394): one row per problem with the one action that leads to the fix,
 * instead of banners scattered over the pages. Renders nothing while there is no problem.
 *
 * @param props - The problems and an optional heading.
 * @returns The section, or nothing.
 *
 * @example
 * ```tsx
 * <AttentionList
 *   items={[{ id: 'full', tone: 'warning', title: 'The pool fast is 85 % full', action: { label: 'Open Pools', onPress: openPools } }]}
 * />
 * ```
 */
export function AttentionList({ items, title, headingLevel = 2, className }: AttentionListProps) {
  const texts = useUiTexts();
  const titleId = useId();
  if (items.length === 0) {
    return null;
  }
  const Heading = `h${String(headingLevel)}` as 'h2' | 'h3';
  return (
    <section className={cx(styles.attention, className)} aria-labelledby={titleId}>
      <Heading id={titleId} className={styles.attentionTitle}>
        {title ?? texts.attention.title}
      </Heading>
      <ul className={styles.attentionList}>
        {items.map((item) => {
          const Icon = toneIcons[item.tone];
          return (
            <li key={item.id} className={styles.attentionRow}>
              <span
                className={styles.attentionIcon}
                data-tone={item.tone}
                role="img"
                aria-label={texts.attention[item.tone]}
              >
                <Icon size={18} stroke={1.75} aria-hidden />
              </span>
              <div className={styles.attentionText}>
                <span>{item.title}</span>
                {item.description !== undefined && (
                  <span className={styles.attentionDescription}>{item.description}</span>
                )}
              </div>
              {item.action !== undefined && (
                <Button size="sm" onPress={item.action.onPress} className={styles.attentionAction}>
                  {item.action.label}
                </Button>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

/**
 * The summary tiles at the top of an overview page (D394), at most four; they wrap in a narrow container.
 *
 * @param props - The {@link SummaryTile}s.
 * @returns The tiles as a description list.
 */
export function SummaryTiles({
  children,
  className,
}: {
  /** At most four {@link SummaryTile}s. */
  children: ReactNode;
  /** Extra class names, added after the tiles' own. */
  className?: string;
}) {
  return (
    <div className={styles.tilesContainer}>
      <dl className={cx(styles.tiles, className)}>{children}</dl>
    </div>
  );
}

/**
 * A summary tile: a label, a large value and an optional line below.
 *
 * @param props - The label, the value and the detail line.
 * @returns The tile.
 *
 * @example
 * ```tsx
 * <SummaryTile label="Capacity" value="4.1 TB" detail="2 pools" />
 * ```
 */
export function SummaryTile({
  label,
  value,
  detail,
}: {
  /** What the value is, for example "Capacity". */
  label: ReactNode;
  /** The value, for example "4.1 TB". */
  value: ReactNode;
  /** A line below the value, for example "2 pools". */
  detail?: ReactNode;
}) {
  return (
    <div className={styles.tile}>
      <dt className={styles.tileLabel}>{label}</dt>
      <dd className={styles.tileValue}>{value}</dd>
      {detail !== undefined && <dd className={styles.tileDetail}>{detail}</dd>}
    </div>
  );
}
