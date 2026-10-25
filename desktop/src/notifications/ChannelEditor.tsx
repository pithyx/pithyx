import type { components } from '@pithyx/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Badge, Button, Checkbox, Dialog, TextField } from '@pithyx/ui';
import { ErrorNotice } from '../errors/ErrorNotice';
import { useFormatLocale } from '../i18n';
import { describeProblem } from '../apps/storage/shared';
import styles from './Channels.module.css';

export type NotificationChannel = components['schemas']['NotificationChannel'];
export type NotificationChannelList = components['schemas']['NotificationChannelList'];
export type NotificationChannelRequest = components['schemas']['NotificationChannelRequest'];
type Category = components['schemas']['NotificationCategory'];
type Level = components['schemas']['NotificationLevel'];
type Kind = components['schemas']['ChannelKind'];
type Format = NonNullable<components['schemas']['WebhookFormat']>;

const CATEGORIES: readonly Category[] = [
  'storage',
  'updates',
  'backups',
  'security',
  'apps',
  'system',
];
const LEVELS: readonly Level[] = ['info', 'warning', 'error'];

/** Where a list of channels lives: the user's own (desktop) or the box's (Settings, through the SDK). */
export interface ChannelClient {
  queryKey: readonly unknown[];
  list: () => Promise<NotificationChannelList>;
  create: (request: NotificationChannelRequest) => Promise<NotificationChannel>;
  update: (id: number, request: NotificationChannelRequest) => Promise<NotificationChannel>;
  remove: (id: number) => Promise<unknown>;
  test: (id: number) => Promise<NotificationChannel>;
}

/** The address of a webhook without its path and query, which often carry a secret topic or token. */
function hostOf(url: string | null): string {
  if (url === null) {
    return '';
  }
  try {
    const parsed = new URL(url);
    return `${parsed.protocol}//${parsed.host}/…`;
  } catch {
    return '';
  }
}

/**
 * Email and webhook channels (D379): the list with each channel's events and its last delivery, and a dialog to add or
 * change one. Used for a user's own channels and for the box's in Settings > Notifications.
 */
export function ChannelEditor({ client }: { client: ChannelClient }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const locale = useFormatLocale();
  const list = useQuery({ queryKey: client.queryKey, queryFn: client.list });
  const [editing, setEditing] = useState<NotificationChannel | 'new' | null>(null);
  const [removing, setRemoving] = useState<NotificationChannel | null>(null);
  const [tested, setTested] = useState<number | null>(null);
  const refresh = () => queryClient.invalidateQueries({ queryKey: client.queryKey });
  const test = useMutation({
    mutationFn: client.test,
    onSuccess: (_, id) => {
      setTested(id);
    },
    onSettled: refresh,
  });
  const date = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' });
  const data = list.data;
  return (
    <div className={styles.form}>
      {list.isError && <ErrorNotice error={describeProblem(list.error)} />}
      {data !== undefined && (
        <>
          {data.items.length === 0 ? (
            <p className={styles.muted}>{t('channels.none')}</p>
          ) : (
            <ul className={styles.list}>
              {data.items.map((channel) => (
                <li key={channel.id} className={styles.card}>
                  <span>
                    <span className={styles.name}>{channel.name}</span>{' '}
                    <Badge tone="neutral">{t(`channels.kinds.${channel.kind}`)}</Badge>{' '}
                    {!channel.enabled && <Badge tone="neutral">{t('channels.off')}</Badge>}
                  </span>
                  <span className={styles.detail}>
                    {channel.kind === 'email' ? channel.recipients.join(', ') : hostOf(channel.url)}
                  </span>
                  <span className={styles.detail}>
                    {channel.categories
                      .map((category) => t(`channels.categoryNames.${category}`))
                      .join(', ')}
                    {' · '}
                    {t(`channels.levels.${channel.minLevel}`)}
                  </span>
                  <span className={styles.detail}>
                    {channel.lastSentAt == null
                      ? t('channels.never')
                      : t('channels.lastSent', { date: date.format(new Date(channel.lastSentAt)) })}
                  </span>
                  {channel.errorCode != null && (
                    <ErrorNotice
                      error={describeProblem({
                        code: channel.errorCode,
                        params: channel.errorParams ?? {},
                      })}
                    />
                  )}
                  {tested === channel.id && channel.errorCode == null && (
                    <p className={styles.muted} role="status">
                      {t('channels.tested')}
                    </p>
                  )}
                  <div className={styles.buttons}>
                    <Button
                      onPress={() => {
                        setEditing(channel);
                      }}
                    >
                      {t('channels.edit')}
                    </Button>
                    <Button
                      isDisabled={test.isPending}
                      onPress={() => {
                        setTested(null);
                        test.mutate(channel.id);
                      }}
                    >
                      {t('channels.test')}
                    </Button>
                    <Button
                      variant="danger"
                      onPress={() => {
                        setRemoving(channel);
                      }}
                    >
                      {t('channels.remove')}
                    </Button>
                  </div>
                </li>
              ))}
            </ul>
          )}
          <div className={styles.buttons}>
            <Button
              isDisabled={data.items.length >= data.max}
              onPress={() => {
                setEditing('new');
              }}
            >
              {t('channels.add')}
            </Button>
          </div>
        </>
      )}
      {editing !== null && data !== undefined && (
        <ChannelDialog
          client={client}
          channel={editing === 'new' ? null : editing}
          emailAvailable={data.emailAvailable}
          onClose={() => {
            setEditing(null);
          }}
        />
      )}
      {removing !== null && (
        <RemoveDialog
          client={client}
          channel={removing}
          onClose={() => {
            setRemoving(null);
          }}
        />
      )}
    </div>
  );
}

interface HeaderRow {
  name: string;
  value: string;
  saved: boolean;
}

function ChannelDialog({
  client,
  channel,
  emailAvailable,
  onClose,
}: {
  client: ChannelClient;
  channel: NotificationChannel | null;
  emailAvailable: boolean;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [name, setName] = useState(channel?.name ?? '');
  const [kind, setKind] = useState<Kind>(channel?.kind ?? (emailAvailable ? 'email' : 'webhook'));
  const [recipients, setRecipients] = useState(channel?.recipients.join(', ') ?? '');
  const [url, setUrl] = useState(channel?.url ?? '');
  const [format, setFormat] = useState<Format>(channel?.format ?? 'json');
  const [headers, setHeaders] = useState<HeaderRow[]>(
    channel?.headers.map((header) => ({ name: header, value: '', saved: true })) ?? [],
  );
  const [categories, setCategories] = useState<Category[]>(
    channel?.categories ?? ['storage', 'updates', 'security', 'system'],
  );
  const [minLevel, setMinLevel] = useState<Level>(channel?.minLevel ?? 'warning');
  const [enabled, setEnabled] = useState(channel?.enabled ?? true);
  const save = useMutation({
    mutationFn: () => {
      const request: NotificationChannelRequest = {
        name: name.trim(),
        kind,
        enabled,
        categories,
        minLevel,
        ...(kind === 'email'
          ? {
              recipients: recipients
                .split(',')
                .map((address) => address.trim())
                .filter((address) => address !== ''),
            }
          : {
              url: url.trim(),
              format,
              headers: headers
                .filter((header) => header.name.trim() !== '')
                .map((header) => ({
                  name: header.name.trim(),
                  // An empty value of a saved header keeps the value on the box.
                  value: header.saved && header.value === '' ? null : header.value,
                })),
            }),
      };
      return channel === null ? client.create(request) : client.update(channel.id, request);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: client.queryKey });
      onClose();
    },
  });
  const changeHeader = (index: number, change: Partial<HeaderRow>) => {
    setHeaders((current) =>
      current.map((header, at) => (at === index ? { ...header, ...change } : header)),
    );
  };
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      size="medium"
      title={
        channel === null ? t('channels.newTitle') : t('channels.editTitle', { name: channel.name })
      }
      actions={
        <>
          <Button onPress={onClose}>{t('channels.cancel')}</Button>
          <Button
            variant="primary"
            isDisabled={save.isPending || name.trim() === '' || categories.length === 0}
            onPress={() => {
              save.mutate();
            }}
          >
            {t('channels.save')}
          </Button>
        </>
      }
    >
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          save.mutate();
        }}
      >
        <TextField label={t('channels.name')} value={name} onChange={setName} autoComplete="off" />
        <label className={styles.field}>
          <span className={styles.label}>{t('channels.kind')}</span>
          <select
            className={styles.select}
            value={kind}
            disabled={channel !== null}
            onChange={(event) => {
              setKind(event.target.value as Kind);
            }}
          >
            <option value="email" disabled={!emailAvailable}>
              {t('channels.kinds.email')}
            </option>
            <option value="webhook">{t('channels.kinds.webhook')}</option>
          </select>
          {!emailAvailable && <span className={styles.hint}>{t('channels.emailMissing')}</span>}
        </label>
        {kind === 'email' ? (
          <TextField
            label={t('channels.recipients')}
            description={t('channels.recipientsHint')}
            value={recipients}
            onChange={setRecipients}
            type="email"
            autoComplete="email"
            autoCapitalize="none"
          />
        ) : (
          <>
            <TextField
              label={t('channels.url')}
              description={t('channels.urlHint')}
              value={url}
              onChange={setUrl}
              type="url"
              autoComplete="off"
              autoCapitalize="none"
            />
            <label className={styles.field}>
              <span className={styles.label}>{t('channels.format')}</span>
              <select
                className={styles.select}
                value={format}
                onChange={(event) => {
                  setFormat(event.target.value as Format);
                }}
              >
                <option value="json">{t('channels.formats.json')}</option>
                <option value="ntfy">{t('channels.formats.ntfy')}</option>
              </select>
            </label>
            <fieldset className={styles.form}>
              <legend className={styles.label}>{t('channels.headers')}</legend>
              <span className={styles.hint}>{t('channels.headersHint')}</span>
              {headers.map((header, index) => (
                <div key={index} className={styles.header}>
                  <TextField
                    label={t('channels.headerName')}
                    value={header.name}
                    onChange={(value) => {
                      changeHeader(index, { name: value });
                    }}
                    autoComplete="off"
                    autoCapitalize="none"
                  />
                  <TextField
                    label={t('channels.headerValue')}
                    value={header.value}
                    {...(header.saved ? { placeholder: t('channels.headerSaved') } : {})}
                    onChange={(value) => {
                      changeHeader(index, { value });
                    }}
                    type="password"
                    autoComplete="off"
                  />
                  <Button
                    onPress={() => {
                      setHeaders((current) => current.filter((_, at) => at !== index));
                    }}
                  >
                    {t('channels.removeHeader', { name: header.name })}
                  </Button>
                </div>
              ))}
              {headers.length < 10 && (
                <div className={styles.buttons}>
                  <Button
                    onPress={() => {
                      setHeaders((current) => [...current, { name: '', value: '', saved: false }]);
                    }}
                  >
                    {t('channels.addHeader')}
                  </Button>
                </div>
              )}
            </fieldset>
          </>
        )}
        <fieldset className={styles.choices}>
          <legend className={styles.label}>{t('channels.categories')}</legend>
          {CATEGORIES.map((category) => (
            <Checkbox
              key={category}
              isSelected={categories.includes(category)}
              onChange={(selected) => {
                setCategories((current) =>
                  selected
                    ? CATEGORIES.filter((entry) => entry === category || current.includes(entry))
                    : current.filter((entry) => entry !== category),
                );
              }}
            >
              {t(`channels.categoryNames.${category}`)}
            </Checkbox>
          ))}
        </fieldset>
        <label className={styles.field}>
          <span className={styles.label}>{t('channels.minLevel')}</span>
          <select
            className={styles.select}
            value={minLevel}
            onChange={(event) => {
              setMinLevel(event.target.value as Level);
            }}
          >
            {LEVELS.map((level) => (
              <option key={level} value={level}>
                {t(`channels.levels.${level}`)}
              </option>
            ))}
          </select>
        </label>
        <Checkbox isSelected={enabled} onChange={setEnabled}>
          {t('channels.enabled')}
        </Checkbox>
        {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      </form>
    </Dialog>
  );
}

function RemoveDialog({
  client,
  channel,
  onClose,
}: {
  client: ChannelClient;
  channel: NotificationChannel;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const remove = useMutation({
    mutationFn: () => client.remove(channel.id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: client.queryKey });
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      tone="danger"
      role="alertdialog"
      title={t('channels.removeTitle', { name: channel.name })}
      actions={
        <>
          <Button onPress={onClose}>{t('channels.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={remove.isPending}
            onPress={() => {
              remove.mutate();
            }}
          >
            {t('channels.remove')}
          </Button>
        </>
      }
    >
      <p>{t('channels.removeExplain')}</p>
      {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
    </Dialog>
  );
}
