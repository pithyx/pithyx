import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import styles from './PasswordStrength.module.css';
import { assessPassword, MIN_PASSWORD_LENGTH, type PasswordLists } from './strength';

/** The lists of common passwords and words, loaded once when the first form that sets a password opens. */
export function usePasswordLists(): PasswordLists | undefined {
  return useQuery({
    queryKey: ['auth', 'password-lists'],
    queryFn: async () => (await import('./lists')).passwordLists,
    staleTime: Infinity,
    gcTime: Infinity,
  }).data;
}

/**
 * Live feedback below a new password: three bars, the strength with its most important reason, and a tip. It always
 * takes the same space, so nothing moves while typing. The status is announced when the strength or its reason
 * changes, not on every key.
 */
export function PasswordStrengthMeter({ password }: { password: string }) {
  const { t } = useTranslation('auth');
  const lists = usePasswordLists();
  const result = assessPassword(password, lists);
  const empty = password === '';
  const level = {
    weak: t('password.weak'),
    medium: t('password.medium'),
    strong: t('password.strong'),
  }[result.strength];
  const reasons = {
    tooShort: t('password.tooShort', { minLength: MIN_PASSWORD_LENGTH }),
    common: t('password.common'),
    dictionary: t('password.dictionary'),
    repetition: t('password.repetition'),
    sequence: t('password.sequence'),
    length: t('password.length'),
  };
  const issue = result.issues[0];
  return (
    <span className={styles.meter} data-strength={empty ? undefined : result.strength}>
      <span className={styles.bars} aria-hidden>
        <span className={styles.bar} />
        <span className={styles.bar} />
        <span className={styles.bar} />
      </span>
      <span className={styles.status} aria-live="polite">
        {empty ? (
          t('password.hint', { minLength: MIN_PASSWORD_LENGTH })
        ) : (
          <>
            <span className={styles.level}>{t('password.level', { level })}</span>{' '}
            {issue === undefined ? t('password.good') : reasons[issue]}
          </>
        )}
      </span>
      <span className={styles.tip}>{t('password.tip')}</span>
    </span>
  );
}

/**
 * The hard rules before submitting, with the same texts as the meter: a password that is too short or too well
 * known is refused here instead of after a round trip. The daemon checks the same rules and its longer list.
 */
export function useNewPasswordCheck(): (password: string) => string | undefined {
  const { t } = useTranslation('auth');
  const lists = usePasswordLists();
  return (password) => {
    const { issues } = assessPassword(password, lists);
    if (issues.includes('tooShort')) {
      return t('password.tooShort', { minLength: MIN_PASSWORD_LENGTH });
    }
    return issues.includes('common') ? t('password.common') : undefined;
  };
}
