import { IconExternalLink } from '../../ui/icons';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Wordmark } from '../../ui/Logo';
import styles from './AboutApp.module.css';

interface Notice {
  name: string;
  version: string;
  license: string;
  source: string;
  licenseFile?: string;
}

const SOURCE = 'https://github.com/pithyx/pithyx';

async function fetchList(file: string, signal: AbortSignal): Promise<Notice[]> {
  const response = await fetch(`/${file}`, { signal });
  if (!response.ok) {
    throw new Error(`${file}: ${String(response.status)}`);
  }
  const json = (await response.json()) as Notice[] | { entries: Notice[] };
  return Array.isArray(json) ? json : json.entries;
}

// The hand-written list (fonts, icons) comes first, then the generated npm and NuGet packages.
async function fetchNotices(signal: AbortSignal): Promise<Notice[]> {
  const [listed, packages] = await Promise.all([
    fetchList('third-party-notices.json', signal),
    fetchList('third-party-packages.json', signal),
  ]);
  return [...listed, ...packages];
}

/** Product, license, the attribution required by NOTICE (AGPL section 7(b)) and the third-party notices. */
export function AboutApp() {
  const { t } = useTranslation();
  const notices = useQuery({
    queryKey: ['third-party-notices'],
    queryFn: ({ signal }) => fetchNotices(signal),
    staleTime: Infinity,
  });

  return (
    <div className={styles.about}>
      <header className={styles.header}>
        <Wordmark name={t('brand')} mark={t('brandMark')} size="lg" motion="welcome" />
        <p className={styles.tagline}>{t('about.tagline')}</p>
        <p className={styles.story}>
          <span className={styles.greek} lang="el">
            {t('about.greek')}
          </span>{' '}
          {t('about.story')}
        </p>
      </header>
      <section className={styles.section} aria-labelledby="about-license">
        <h2 id="about-license" className={styles.title}>
          {t('about.licenseTitle')}
        </h2>
        <p>{t('about.license')}</p>
        <blockquote className={styles.attribution}>{t('about.attribution')}</blockquote>
        <a className={styles.link} href={SOURCE} target="_blank" rel="noreferrer">
          {t('about.source')}
          <IconExternalLink size={14} stroke={1.75} aria-hidden />
        </a>
      </section>
      <section className={styles.section} aria-labelledby="about-notices">
        <h2 id="about-notices" className={styles.title}>
          {t('about.noticesTitle')}
        </h2>
        {notices.data !== undefined && (
          <ul className={styles.notices}>
            {notices.data.map((notice) => (
              <li key={`${notice.name}@${notice.version}`} className={styles.notice}>
                <span className={styles.noticeName}>{notice.name}</span>
                <span className={styles.noticeMeta}>
                  {notice.version} · {notice.license}
                </span>
                {notice.licenseFile !== undefined && (
                  <a
                    className={styles.link}
                    href={`/${notice.licenseFile}`}
                    target="_blank"
                    rel="noreferrer"
                  >
                    {t('about.licenseText')}
                  </a>
                )}
              </li>
            ))}
          </ul>
        )}
        {notices.isError && <p className={styles.muted}>{t('about.noticesMissing')}</p>}
      </section>
    </div>
  );
}
