import { useTranslation } from 'react-i18next';
import { IconButton, ProgressBar } from '@pithyx/ui';
import { IconX } from '../ui/icons';
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
      {jobs.map((entry) => {
        const title = jobText(entry.message, 'title');
        return (
          <div key={entry.id} className={styles.job}>
            <div className={styles.jobRow}>
              <ProgressBar
                label={title}
                className={styles.jobBar}
                {...(entry.progress === null
                  ? { isIndeterminate: true, showValue: false }
                  : { value: entry.progress })}
              />
              {entry.onCancel !== undefined && (
                <IconButton
                  label={t('notifications.cancelJob', { job: title ?? '' })}
                  size="sm"
                  onPress={entry.onCancel}
                >
                  <IconX size={15} stroke={2} />
                </IconButton>
              )}
            </div>
            <p className={styles.jobDetail}>{jobText(entry.message, 'detail')}</p>
          </div>
        );
      })}
    </section>
  );
}
