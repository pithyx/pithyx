import { Button, Dialog } from '@pithyx/ui';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { LogoMark } from '../../ui/Logo';
import { IconArrowRight, IconCloudUpload, IconServer2 } from '../../ui/icons';
import { DirectionTag, type Direction } from './Direction';
import { closeOn } from './JobDialogs';
import styles from './Backup.module.css';

/**
 * "What do you want to back up?" (D416): folders of this box to a target, or a Linux server onto this box. Each choice
 * shows its way with the box's mark and its direction, then Next opens its own dialog.
 */
export function ChooserDialog({
  onChoose,
  onClose,
}: {
  onChoose: (direction: Direction) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const name = useId();
  const [direction, setDirection] = useState<Direction>('out');
  const box = (
    <span className={styles.chooserBox}>
      <LogoMark size={28} />
    </span>
  );
  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={t('chooser.title')}
      size="large"
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="primary"
            onPress={() => {
              onChoose(direction);
            }}
          >
            {t('form.next')}
          </Button>
        </>
      }
    >
      <fieldset className={styles.chooser}>
        <legend className={styles.visuallyHidden}>{t('chooser.title')}</legend>
        {(['out', 'in'] as const).map((choice) => (
          <label key={choice} className={styles.chooserCard}>
            <input
              type="radio"
              name={name}
              className={styles.visuallyHidden}
              checked={direction === choice}
              onChange={() => {
                setDirection(choice);
              }}
            />
            <span className={styles.chooserWay} aria-hidden>
              {choice === 'out' ? (
                box
              ) : (
                <span className={styles.chooserPlace}>
                  <IconServer2 size={28} stroke={1.5} />
                </span>
              )}
              <IconArrowRight size={20} stroke={1.75} />
              {choice === 'out' ? (
                <span className={styles.chooserPlace}>
                  <IconCloudUpload size={28} stroke={1.5} />
                </span>
              ) : (
                box
              )}
            </span>
            <DirectionTag direction={choice} />
            <span className={styles.chooserTitle}>{t(`chooser.${choice}.title`)}</span>
            <span className={styles.chooserText}>{t(`chooser.${choice}.text`)}</span>
            <ul className={styles.chooserPoints}>
              <li>{t(`chooser.${choice}.point1`)}</li>
              <li>{t(`chooser.${choice}.point2`)}</li>
            </ul>
          </label>
        ))}
      </fieldset>
    </Dialog>
  );
}
