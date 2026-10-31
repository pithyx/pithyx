import { unwrap, type components } from '@pithyx/api-client';
import { Switch } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { manifestText } from '../apps/installed';
import styles from './Channels.module.css';

type AppNotificationCategory = components['schemas']['AppNotificationCategory'];

const queryKey = ['notifications', 'categories'];

/**
 * The kinds of notifications the user's apps declare (D390), each with a switch: off mutes it, so its notifications
 * arrive as read, without a toast, email or webhook.
 */
export function AppCategories() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const list = useQuery({
    queryKey,
    queryFn: async () => unwrap(await api.GET('/api/notifications/categories')).categories,
  });
  const mute = useMutation({
    mutationFn: async ({ app, id, muted }: { app: string; id: string; muted: boolean }) => {
      unwrap(
        await api.PUT('/api/notifications/categories/{app}/{category}', {
          params: { path: { app, category: id } },
          body: { muted },
        }),
      );
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey }),
  });
  const byApp = new Map<string, AppNotificationCategory[]>();
  for (const category of list.data ?? []) {
    byApp.set(category.app, [...(byApp.get(category.app) ?? []), category]);
  }
  return (
    <section aria-labelledby="notification-categories">
      <h3 id="notification-categories" className={styles.name}>
        {t('channels.apps.title')}
      </h3>
      <p className={styles.muted}>{t('channels.apps.intro')}</p>
      {list.data !== undefined && byApp.size === 0 && (
        <p className={styles.muted}>{t('channels.apps.none')}</p>
      )}
      <ul className={styles.list}>
        {[...byApp.values()].map((categories) => (
          <li key={categories[0]?.app} className={styles.card}>
            <span className={styles.name}>
              {manifestText(categories[0]?.appName ?? {}, i18n.language)}
            </span>
            {categories.map((category) => (
              <Switch
                key={category.id}
                isSelected={!category.muted}
                isDisabled={mute.isPending}
                onChange={(shown) => {
                  mute.mutate({ app: category.app, id: category.id, muted: !shown });
                }}
              >
                {manifestText(category.name, i18n.language)}
                {category.description != null && (
                  <span className={styles.detail}>
                    {manifestText(category.description, i18n.language)}
                  </span>
                )}
              </Switch>
            ))}
          </li>
        ))}
      </ul>
    </section>
  );
}
