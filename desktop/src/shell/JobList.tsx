import { useTranslation } from 'react-i18next';
import { ProgressBar } from '../ui/Controls';
import type { JobItem } from './Notifications';
import styles from './Notifications.module.css';
import { useMessages } from './messages';

/** Running jobs with their progress, the first group of the notification panel. */
export function JobList({ jobs }: { jobs: JobItem[] }) {
  const { t } = useTranslation();
  const jobText = useMessages('jobs');
  if (jobs.length === 0) {
    return null;
  }
  return (
    <section className={styles.group} aria-labelledby="panel-jobs">
      <h3 id="panel-jobs" className={styles.groupTitle}>
        {t('notifications.jobs')}
      </h3>
      {jobs.map((entry) => (
        <div key={entry.id} className={styles.job}>
          <ProgressBar label={jobText(entry.message, 'title')} value={entry.progress} />
          <p className={styles.jobDetail}>{jobText(entry.message, 'detail')}</p>
        </div>
      ))}
    </section>
  );
}
