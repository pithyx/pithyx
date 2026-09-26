import { IconX } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '../ui/Button';
import { AppIcon } from './AppIcon';
import styles from './AppSwitcher.module.css';
import type { AppInfo, TaskbarPosition } from './types';

export interface SwitcherCard {
  id: string;
  app: Pick<AppInfo, 'icon' | 'tone' | 'name'>;
  title: string;
  preview: ReactNode;
}

/** Compact size class: open apps as cards; tap to switch, close with the button on each card. */
export function AppSwitcher({
  cards,
  position,
}: {
  cards: SwitcherCard[];
  position: TaskbarPosition;
}) {
  const { t } = useTranslation();
  return (
    <section
      className={styles.switcher}
      data-position={position}
      role="dialog"
      aria-label={t('switcher.title')}
    >
      <header className={styles.header}>
        <h2 className={styles.title}>{t('switcher.title')}</h2>
        <Button variant="quiet" size="sm">
          {t('switcher.closeAll')}
        </Button>
      </header>
      <ul className={styles.cards}>
        {cards.map((card) => (
          <li key={card.id} className={styles.card}>
            <div className={styles.cardHeader}>
              <AppIcon app={card.app} size={22} />
              <span className={styles.cardTitle}>{card.title}</span>
              <IconButton
                label={t('switcher.close', { name: card.app.name })}
                size="sm"
                tooltip={false}
              >
                <IconX size={16} stroke={2} />
              </IconButton>
            </div>
            <div className={styles.preview}>
              <div className={styles.previewInner} aria-hidden inert>
                {card.preview}
              </div>
              <button
                type="button"
                className={styles.previewButton}
                aria-label={t('switcher.open', { name: card.title })}
              />
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
