import { Button } from '@pithyx/ui';
import type { ReactNode } from 'react';
import { IconPlus } from '../../ui/icons';
import styles from './Backup.module.css';

/**
 * What an empty list of the Backup app shows in its place: what is missing in a heading and one sentence, and the
 * page's primary action, which the toolbar leaves out while the list is empty (D394).
 */
export function EmptyCard({
  icon,
  title,
  text,
  action,
  onAction,
}: {
  icon: ReactNode;
  title: string;
  text: string;
  action: string;
  onAction: () => void;
}) {
  return (
    <div className={styles.empty}>
      <span className={styles.emptyIcon} aria-hidden>
        {icon}
      </span>
      <h2 className={styles.emptyTitle}>{title}</h2>
      <p>{text}</p>
      <Button variant="primary" onPress={onAction}>
        <IconPlus size={18} stroke={1.75} aria-hidden />
        {action}
      </Button>
    </div>
  );
}
