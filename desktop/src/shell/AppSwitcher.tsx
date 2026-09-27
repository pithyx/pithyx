import { IconX } from '@tabler/icons-react';
import type { ReactNode, Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, IconButton } from '../ui/Button';
import { AppIcon } from './AppIcon';
import styles from './AppSwitcher.module.css';
import type { AppInfo, TaskbarPosition } from './types';

export interface SwitcherCard {
  id: string;
  app: Pick<AppInfo, 'icon' | 'tone' | 'name'>;
  title: string;
  /** Rendered scaled down. Without it the card shows the app icon on the app's color. */
  preview?: ReactNode;
}

/** Compact size class: open apps as cards; tap to switch, close with the button on each card. */
export function AppSwitcher({
  cards,
  position,
  switcherRef,
  onOpen,
  onClose,
  onCloseAll,
}: {
  cards: SwitcherCard[];
  position: TaskbarPosition;
  switcherRef?: Ref<HTMLElement>;
  onOpen?: (id: string) => void;
  onClose?: (id: string) => void;
  onCloseAll?: () => void;
}) {
  const { t } = useTranslation();
  return (
    <section
      ref={switcherRef}
      className={styles.switcher}
      data-position={position}
      role="dialog"
      aria-label={t('switcher.title')}
    >
      <header className={styles.header}>
        <h2 className={styles.title}>{t('switcher.title')}</h2>
        {cards.length > 0 && (
          <Button variant="quiet" size="sm" onPress={() => onCloseAll?.()}>
            {t('switcher.closeAll')}
          </Button>
        )}
      </header>
      {cards.length === 0 && <p className={styles.empty}>{t('switcher.empty')}</p>}
      <ul className={styles.cards}>
        {cards.map((card) => (
          <li key={card.id} className={styles.card}>
            <div className={styles.cardHeader}>
              <AppIcon app={card.app} size={22} />
              <span className={styles.cardTitle}>{card.title}</span>
              <IconButton
                label={t('switcher.close', { name: card.title })}
                size="sm"
                tooltip={false}
                onPress={() => onClose?.(card.id)}
              >
                <IconX size={16} stroke={2} />
              </IconButton>
            </div>
            <div className={styles.preview}>
              {card.preview === undefined ? (
                <div className={styles.placeholder} data-tone={card.app.tone} aria-hidden>
                  <AppIcon app={card.app} size={56} />
                </div>
              ) : (
                <div className={styles.previewInner} aria-hidden inert>
                  {card.preview}
                </div>
              )}
              <button
                type="button"
                className={styles.previewButton}
                aria-label={t('switcher.open', { name: card.title })}
                onClick={() => onOpen?.(card.id)}
              />
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
