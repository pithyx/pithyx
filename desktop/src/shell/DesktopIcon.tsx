import { IconArrowUpRight } from '@tabler/icons-react';
import type { TablerIcon } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { cx } from '../ui/cx';
import { AppIcon } from './AppIcon';
import styles from './DesktopIcon.module.css';
import type { AppInfo } from './types';

export type DesktopItemKind =
  | { kind: 'folder' }
  | { kind: 'file'; icon: TablerIcon }
  | { kind: 'shortcut'; app: Pick<AppInfo, 'icon' | 'tone'> };

/** A file, folder or app shortcut on the desktop. */
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
  let visual;
  if (item.kind === 'shortcut') {
    visual = (
      <span className={styles.shortcut}>
        <AppIcon app={item.app} size={48} />
        <span className={styles.arrow} aria-hidden>
          <IconArrowUpRight size={12} stroke={2.5} />
        </span>
      </span>
    );
  } else if (item.kind === 'folder') {
    visual = <FolderGlyph />;
  } else {
    const Glyph = item.icon;
    visual = (
      <span className={styles.file} aria-hidden>
        <Glyph size={30} stroke={1.5} />
      </span>
    );
  }

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
        {visual}
        <span className={styles.label} aria-hidden>
          {name}
        </span>
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
