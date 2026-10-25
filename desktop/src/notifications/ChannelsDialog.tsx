import { unwrap } from '@pithyx/api-client';
import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@pithyx/ui';
import { api } from '../api/client';
import { ChannelEditor, type ChannelClient } from './ChannelEditor';
import styles from './Channels.module.css';

const ownChannels: ChannelClient = {
  queryKey: ['notifications', 'channels'],
  list: async () => unwrap(await api.GET('/api/notifications/channels')),
  create: async (body) => unwrap(await api.POST('/api/notifications/channels', { body })),
  update: async (id, body) =>
    unwrap(await api.PUT('/api/notifications/channels/{id}', { params: { path: { id } }, body })),
  remove: async (id) => {
    unwrap(await api.DELETE('/api/notifications/channels/{id}', { params: { path: { id } } }));
  },
  test: async (id) =>
    unwrap(await api.POST('/api/notifications/channels/{id}/test', { params: { path: { id } } })),
};

/** The user's own notification channels (D379), from the user menu. */
export function ChannelsDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      size="medium"
      title={t('channels.title')}
      actions={<Button onPress={onClose}>{t('channels.close')}</Button>}
    >
      <p className={styles.muted}>{t('channels.intro')}</p>
      <ChannelEditor client={ownChannels} />
    </Dialog>
  );
}
