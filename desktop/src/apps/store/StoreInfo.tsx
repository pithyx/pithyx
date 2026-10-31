import { StatusBadge } from '@pithyx/ui';
import { useTranslation } from 'react-i18next';
import { useFormatLocale } from '../../i18n';
import { manifestText } from '../installed';
import {
  isCategory,
  screenshotUrl,
  speaksLanguage,
  type RequirementHint,
  type StoreFacts,
} from './model';
import styles from './Store.module.css';

/** The badge of an app that is not stable yet (D389); nothing for a stable one or an unknown value. */
export function MaturityBadge({ maturity }: { maturity: string }) {
  const { t } = useTranslation('store');
  if (maturity !== 'beta' && maturity !== 'experimental') {
    return null;
  }
  return <StatusBadge tone="warning">{t(`info.maturity.${maturity}`)}</StatusBadge>;
}

/**
 * The rows the Store shows of a manifest's store fields (D389), for a `<dl>` of facts: what the app sends, whether its
 * source is public, where it runs, its languages, its security contact and its categories.
 */
export function StoreInfoRows({ facts }: { facts: StoreFacts & { license: string | null } }) {
  const { t, i18n } = useTranslation('store');
  const locale = useFormatLocale();
  const languageName = (tag: string) => {
    try {
      return new Intl.DisplayNames([locale], { type: 'language' }).of(tag) ?? tag;
    } catch {
      return tag;
    }
  };
  const speaks = speaksLanguage(facts.locales, i18n.language);
  const source = facts.source;
  return (
    <>
      <dt>{t('info.privacy.title')}</dt>
      <dd>
        {facts.privacy === null
          ? t('info.privacy.unknown')
          : t(`info.privacy.${facts.privacy.sends}`)}
        {facts.privacy?.note != null && (
          <span className={styles.muted}> {manifestText(facts.privacy.note, i18n.language)}</span>
        )}
      </dd>
      {source !== null && (
        <>
          <dt>{t('info.source.title')}</dt>
          <dd className={styles.breakable}>
            {source === 'private' ? (
              t('info.source.private')
            ) : (
              <a href={source} target="_blank" rel="noreferrer">
                {facts.license === null
                  ? t('info.source.public')
                  : t('info.source.open', { license: facts.license })}
              </a>
            )}
          </dd>
        </>
      )}
      {facts.arch.length > 0 && (
        <>
          <dt>{t('info.arch.title')}</dt>
          <dd>{facts.arch.join(', ')}</dd>
        </>
      )}
      {speaks !== null && (
        <>
          <dt>{t('info.languages.title')}</dt>
          <dd>
            {speaks
              ? t('info.languages.yours', { language: languageName(i18n.language) })
              : t('info.languages.others', {
                  languages: facts.locales.map(languageName).join(', '),
                })}
          </dd>
        </>
      )}
      {facts.security !== null && (
        <>
          <dt>{t('info.security.title')}</dt>
          <dd className={styles.breakable}>
            <a href={facts.security} target="_blank" rel="noreferrer">
              {facts.security.replace(/^mailto:/, '')}
            </a>
          </dd>
        </>
      )}
      {facts.categories.length > 0 && (
        <>
          <dt>{t('info.categories.title')}</dt>
          <dd>
            {facts.categories
              .filter(isCategory)
              .map((category) => t(`category.${category}`))
              .join(', ')}
          </dd>
        </>
      )}
    </>
  );
}

/** What is new in a version, from its manifest's `releaseNotes` (D389). */
export function ReleaseNotes({
  notes,
  version,
}: {
  notes: Record<string, string> | null | undefined;
  version: string;
}) {
  const { t, i18n } = useTranslation('store');
  if (notes == null) {
    return null;
  }
  return (
    <div className={styles.releaseNotes}>
      <strong>{t('info.releaseNotes', { version })}</strong>
      <p>{manifestText(notes, i18n.language)}</p>
    </div>
  );
}

/** The install dialog's hints that the box may be too small for the app; the install goes on regardless. */
export function RequirementHints({ hints }: { hints: RequirementHint[] }) {
  const { t } = useTranslation('store');
  return hints.map((hint) => (
    <p key={hint.kind} className={styles.notice}>
      {t(`info.requirements.${hint.kind}`, { needed: hint.needed, available: hint.available })}
    </p>
  ));
}

/** The screenshots an installed app's manifest names, served with its frontend (D389). */
export function ManifestScreenshots({
  appId,
  name,
  screenshots,
}: {
  appId: string;
  name: string;
  screenshots: StoreFacts['screenshots'];
}) {
  const { t, i18n } = useTranslation('store');
  if (screenshots.length === 0) {
    return null;
  }
  return (
    <section className={styles.group} aria-labelledby={`store-screenshots-${appId}`}>
      <h2 id={`store-screenshots-${appId}`} className={styles.sectionTitle}>
        {t('catalog.screenshots')}
      </h2>
      <ul className={styles.screenshots}>
        {screenshots.map((shot, index) => (
          <li key={shot.file}>
            <img
              className={styles.screenshot}
              src={screenshotUrl(appId, shot.file)}
              alt={
                shot.caption === null
                  ? t('catalog.screenshot', { name, number: index + 1 })
                  : manifestText(shot.caption, i18n.language)
              }
              loading="lazy"
            />
          </li>
        ))}
      </ul>
    </section>
  );
}
