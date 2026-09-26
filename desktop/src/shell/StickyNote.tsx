import { IconX } from '@tabler/icons-react';
import type { CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../ui/Button';
import styles from './StickyNote.module.css';

/** A sticky note widget on the desktop. Position in percent of the workspace. */
export function StickyNote({ text, x, y }: { text: string; x: number; y: number }) {
  const { t } = useTranslation();
  return (
    <section
      className={styles.note}
      style={{ '--x': `${String(x)}%`, '--y': `${String(y)}%` } as CSSProperties}
      aria-label={t('items.note')}
    >
      <header className={styles.bar}>
        <IconButton label={t('items.removeNote')} size="sm" className={styles.close}>
          <IconX size={15} stroke={2} />
        </IconButton>
      </header>
      <textarea className={styles.text} defaultValue={text} aria-label={t('items.note')} />
    </section>
  );
}
