import { IconExternalLink } from '@tabler/icons-react';
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

const SOURCE = 'https://github.com/vyxis/vyxis';

async function fetchNotices(signal: AbortSignal): Promise<Notice[]> {
  const response = await fetch('/third-party-notices.json', { signal });
  if (!response.ok) {
    throw new Error(`third-party notices: ${String(response.status)}`);
  }
  return (await response.json()) as Notice[];
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
        <Wordmark name={t('brand')} size="lg" />
        <p className={styles.tagline}>{t('about.tagline')}</p>
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
              <li key={notice.name} className={styles.notice}>
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
