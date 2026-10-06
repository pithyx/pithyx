import { IconX } from '../ui/icons';
import type { ReactNode, Ref } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, IconButton } from '@pithyx/ui';
import { badgeText } from '../apps/host/rules';
import { useFormatLocale } from '../i18n';
import { AppIcon } from './AppIcon';
import styles from './AppSwitcher.module.css';
import type { AppInfo, TaskbarPosition } from './types';

export interface SwitcherCard {
  id: string;
  app: Pick<AppInfo, 'icon' | 'tone' | 'name'>;
  title: string;
  /** Rendered scaled down. Without it the card shows the app icon on the app's color. */
  preview?: ReactNode;
  /** The app's badge count (`setBadge`); 0 for none. */
  badge?: number;
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
  const locale = useFormatLocale();
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
              {badgeText(card.badge, locale) !== null && (
                <span aria-hidden>
                  <Badge>{badgeText(card.badge, locale)}</Badge>
                </span>
              )}
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
                aria-label={t('switcher.open', { name: card.title, count: card.badge ?? 0 })}
                onClick={() => onOpen?.(card.id)}
              />
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}
