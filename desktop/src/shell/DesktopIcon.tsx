import { IconArrowUpRight, IconLinkOff } from '../ui/icons';
import type { ReactNode } from 'react';
import type { TablerIcon } from '../ui/icons';
import { useTranslation } from 'react-i18next';
import { cx } from '@pithyx/ui';
import { AppIcon } from './AppIcon';
import styles from './DesktopIcon.module.css';
import type { AppInfo } from './types';

export type DesktopItemKind =
  | { kind: 'folder'; link?: boolean }
  | { kind: 'file'; icon: TablerIcon; link?: boolean }
  | { kind: 'missing' }
  | { kind: 'shortcut'; app: Pick<AppInfo, 'icon' | 'tone'> };

/** The arrow that marks a shortcut: removing it never touches what it points at. */
function LinkBadge({ children }: { children: ReactNode }) {
  return (
    <span className={styles.shortcut}>
      {children}
      <span className={styles.arrow} aria-hidden>
        <IconArrowUpRight size={12} stroke={2.5} />
      </span>
    </span>
  );
}

/** The glyph and label of a desktop item, inside whatever element makes it interactive. */
export function DesktopItemFace({ name, item }: { name: string; item: DesktopItemKind }) {
  let visual;
  if (item.kind === 'shortcut') {
    visual = (
      <LinkBadge>
        <AppIcon app={item.app} size={48} />
      </LinkBadge>
    );
  } else if (item.kind === 'missing') {
    visual = (
      <span className={cx(styles.file, styles.missing)} aria-hidden>
        <IconLinkOff size={30} stroke={1.5} />
      </span>
    );
  } else {
    let glyph;
    if (item.kind === 'folder') {
      glyph = <FolderGlyph />;
    } else {
      const Glyph = item.icon;
      glyph = (
        <span className={styles.file} aria-hidden>
          <Glyph size={30} stroke={1.5} />
        </span>
      );
    }
    visual = item.link === true ? <LinkBadge>{glyph}</LinkBadge> : glyph;
  }
  return (
    <>
      {visual}
      <span className={styles.label} aria-hidden>
        {name}
      </span>
    </>
  );
}

/** A file, folder or app shortcut on the desktop, as the design gallery shows it. */
export function DesktopIcon({
  name,
  item,
  selected = false,
  onOpen,
}: {
  name: string;
  item: DesktopItemKind;
  selected?: boolean;
  /** Double click with a mouse, a single tap on touch, Enter or Space with the keyboard. */
  onOpen?: () => void;
}) {
  const { t } = useTranslation();
  return (
    <li>
      <button
        type="button"
        className={cx(styles.icon, selected && styles.selected)}
        aria-pressed={selected}
        aria-label={item.kind === 'shortcut' ? t('items.shortcut', { name }) : name}
        onClick={(event) => {
          // detail is 0 for keyboard activation; touch opens on the first tap like on a phone home screen.
          const pointerType =
            'pointerType' in event.nativeEvent ? event.nativeEvent.pointerType : '';
          if (event.detail === 0 || pointerType === 'touch') {
            onOpen?.();
          }
        }}
        onDoubleClick={onOpen}
      >
        <DesktopItemFace name={name} item={item} />
      </button>
    </li>
  );
}

/** Folders are drawn in the brand amber, the only place amber fills a large shape. */
function FolderGlyph() {
  return (
    <svg className={styles.folder} viewBox="0 0 48 48" width="52" height="52" aria-hidden>
      <path
        className={styles.folderBack}
        d="M4 12a4 4 0 0 1 4-4h11l4 4h17a4 4 0 0 1 4 4v22a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4z"
      />
      <path
        className={styles.folderFront}
        d="M4 19a3 3 0 0 1 3-3h34a3 3 0 0 1 3 3v19a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4z"
      />
    </svg>
  );
}
