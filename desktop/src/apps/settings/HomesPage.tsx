import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { Button, TextField } from '@pithyx/ui';
import { IconFolderShare, IconPencil, IconTrash } from '../../ui/icons';
import { parseSize, sizeText, SIZE_UNITS, type SizeUnit } from '../storage/datasets';
import { describeProblem } from '../storage/shared';
import { usersCall, type HomeInfo, type HomeOverview } from '../sdk/users';
import { FormDialog } from '../users/FormDialog';
import styles from './Settings.module.css';
import { sharesKey, useShares } from './shares';

/** Changes of the shares and the pools refresh it with the other answers of the sharing pages. */
const homesKey = [...sharesKey, 'homes'] as const;

/** Why a pool cannot take the homes, as the daemon names it. */
type HomeReason = 'missing' | 'offline' | 'read_only' | 'not_prepared' | 'locked' | 'not_mounted';

type Open =
  | { kind: 'move'; pool: string }
  | { kind: 'quota'; home: HomeInfo; userId: number }
  | { kind: 'release'; home: HomeInfo }
  | { kind: 'delete'; home: HomeInfo }
  | null;

/**
 * Home folders (D348): the pool the homes are on, moving them to another pool, the default quota and every home with
 * its user, space and quota; the homes of deleted users can be moved into a shared folder or deleted.
 */
export function HomesPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const overview = useQuery({
    queryKey: homesKey,
    queryFn: () => usersCall(app, 'internal.admin.homes.overview', {}),
  });
  const [open, setOpen] = useState<Open>(null);
  const close = () => {
    setOpen(null);
  };
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <p className={styles.muted}>{t('homes.intro')}</p>
        </div>
      </header>
      {overview.isPending && (
        <p className={styles.loading} role="status">
          {t('homes.loading')}
        </p>
      )}
      {overview.isError && <ErrorNotice error={describeProblem(overview.error)} />}
      {overview.data !== undefined && (
        <>
          <PoolCard
            overview={overview.data}
            onMove={(pool) => {
              setOpen({ kind: 'move', pool });
            }}
          />
          <DefaultQuotaCard app={app} overview={overview.data} />
          <HomeTable
            homes={overview.data.homes}
            defaultQuota={overview.data.defaultQuotaBytes ?? null}
            onOpen={setOpen}
          />
        </>
      )}
      {open?.kind === 'move' && <MoveDialog app={app} pool={open.pool} onClose={close} />}
      {open?.kind === 'quota' && (
        <QuotaDialog app={app} home={open.home} userId={open.userId} onClose={close} />
      )}
      {open?.kind === 'release' && <ReleaseDialog app={app} home={open.home} onClose={close} />}
      {open?.kind === 'delete' && <DeleteHomeDialog app={app} home={open.home} onClose={close} />}
    </div>
  );
}

function PoolCard({
  overview,
  onMove,
}: {
  overview: HomeOverview;
  onMove: (pool: string) => void;
}) {
  const { t } = useTranslation('settings');
  const others = overview.pools.filter((pool) => pool.name !== overview.pool);
  const [chosen, setChosen] = useState<string | null>(null);
  const target =
    chosen ?? others.find((pool) => pool.unavailable == null)?.name ?? others[0]?.name ?? '';
  const usable = others.find((pool) => pool.name === target)?.unavailable == null;
  return (
    <section className={styles.card} aria-labelledby="homes-pool">
      <h3 id="homes-pool" className={styles.sectionTitle}>
        {t('homes.pool.title')}
      </h3>
      <p>
        {overview.pool == null
          ? t('homes.pool.none')
          : t('homes.pool.current', { pool: overview.pool })}
      </p>
      <p className={styles.muted}>{t('homes.pool.explain')}</p>
      {overview.moving != null && (
        <p className={styles.notice} role="status">
          {t('homes.pool.moving')}
        </p>
      )}
      {overview.pool != null && others.length > 0 && overview.moving == null && (
        <div className={styles.group}>
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('homes.pool.target')}</span>
            <select
              className={styles.select}
              value={target}
              onChange={(event) => {
                setChosen(event.currentTarget.value);
              }}
            >
              {others.map((pool) => (
                <option key={pool.name} value={pool.name} disabled={pool.unavailable != null}>
                  {pool.unavailable == null
                    ? pool.name
                    : t('homes.pool.unavailable', {
                        pool: pool.name,
                        reason: t(`homes.reasons.${pool.unavailable as HomeReason}`),
                      })}
                </option>
              ))}
            </select>
          </label>
          <div className={styles.buttons}>
            <Button
              isDisabled={!usable}
              onPress={() => {
                onMove(target);
              }}
            >
              {t('homes.pool.move')}
            </Button>
          </div>
        </div>
      )}
    </section>
  );
}

/** A size in a text field with its unit; empty means no quota. */
function SizeField({
  label,
  description,
  value,
  onChange,
}: {
  label: string;
  description?: string;
  value: { text: string; unit: SizeUnit };
  onChange: (value: { text: string; unit: SizeUnit }) => void;
}) {
  const { t } = useTranslation('settings');
  return (
    <TextField
      label={label}
      description={description}
      value={value.text}
      inputMode="decimal"
      autoComplete="off"
      isInvalid={parseSize(value.text, value.unit) === undefined}
      errorMessage={t('homes.quota.invalid')}
      onChange={(text) => {
        onChange({ ...value, text });
      }}
      trailing={
        <select
          className={styles.select}
          aria-label={t('homes.quota.unit', { field: label })}
          value={value.unit}
          onChange={(event) => {
            onChange({ ...value, unit: event.currentTarget.value as SizeUnit });
          }}
        >
          {Object.keys(SIZE_UNITS).map((unit) => (
            <option key={unit} value={unit}>
              {t(`homes.units.${unit as SizeUnit}`)}
            </option>
          ))}
        </select>
      }
    />
  );
}

function DefaultQuotaCard({ app, overview }: { app: PithyxApp; overview: HomeOverview }) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const [size, setSize] = useState(() => sizeText(overview.defaultQuotaBytes ?? null, locale));
  const bytes = parseSize(size.text, size.unit);
  const save = useMutation({
    mutationFn: (defaultQuotaBytes: number | null) =>
      usersCall(app, 'internal.admin.homes.setDefaultQuota', { defaultQuotaBytes }),
    onSuccess: (next) => {
      queryClient.setQueryData(homesKey, next);
    },
  });
  return (
    <section className={styles.card} aria-labelledby="homes-quota">
      <h3 id="homes-quota" className={styles.sectionTitle}>
        {t('homes.quota.title')}
      </h3>
      <form
        className={styles.group}
        onSubmit={(event) => {
          event.preventDefault();
          if (bytes !== undefined) {
            save.mutate(bytes);
          }
        }}
      >
        <SizeField
          label={t('homes.quota.default')}
          description={t('homes.quota.defaultHint')}
          value={size}
          onChange={setSize}
        />
        <div className={styles.buttons}>
          <Button
            type="submit"
            variant="primary"
            isDisabled={
              bytes === undefined ||
              bytes === (overview.defaultQuotaBytes ?? null) ||
              save.isPending
            }
          >
            {t('homes.save')}
          </Button>
        </div>
      </form>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </section>
  );
}

function HomeTable({
  homes,
  defaultQuota,
  onOpen,
}: {
  homes: HomeInfo[];
  defaultQuota: number | null;
  onOpen: (open: Open) => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const bytes = (value: number) => formatBytes(value, locale);
  if (homes.length === 0) {
    return <p className={styles.muted}>{t('homes.empty')}</p>;
  }
  return (
    <table className={styles.table}>
      <caption className="visually-hidden">{t('homes.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('homes.columns.home')}</th>
          <th scope="col">{t('homes.columns.pool')}</th>
          <th scope="col">{t('homes.columns.used')}</th>
          <th scope="col">{t('homes.columns.quota')}</th>
          <th scope="col">
            <span className="visually-hidden">{t('homes.columns.actions')}</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {homes.map((home) => {
          const userId = home.userId;
          return (
            <tr key={home.shareId}>
              <th scope="row" className={styles.shareName}>
                {home.displayName ?? home.username ?? home.folder}
                <span className={styles.path}>
                  {home.userId == null
                    ? t('homes.deletedUser', { folder: home.folder })
                    : home.folder}
                </span>
              </th>
              <td data-label={t('homes.columns.pool')}>
                {home.pool}
                {home.unavailable != null && (
                  <span className={styles.detail}>{t('homes.unavailable')}</span>
                )}
              </td>
              <td data-label={t('homes.columns.used')}>
                {home.usedBytes == null ? t('homes.unknown') : bytes(home.usedBytes)}
              </td>
              <td data-label={t('homes.columns.quota')}>
                {home.quotaBytes == null ? t('homes.quota.none') : bytes(home.quotaBytes)}
                {home.userId != null && (
                  <span className={styles.detail}>
                    {home.ownQuotaBytes == null
                      ? defaultQuota == null
                        ? t('homes.quota.fromDefaultNone')
                        : t('homes.quota.fromDefault')
                      : t('homes.quota.own')}
                  </span>
                )}
              </td>
              <td className={styles.rowActions}>
                {userId != null && (
                  <Button
                    size="sm"
                    aria-label={t('homes.quota.editLabel', { name: home.username ?? home.folder })}
                    onPress={() => {
                      onOpen({ kind: 'quota', home, userId });
                    }}
                  >
                    <IconPencil size={16} aria-hidden />
                    {t('homes.quota.edit')}
                  </Button>
                )}
                {userId == null && (
                  <Button
                    size="sm"
                    aria-label={t('homes.release.label', { folder: home.folder })}
                    onPress={() => {
                      onOpen({ kind: 'release', home });
                    }}
                  >
                    <IconFolderShare size={16} aria-hidden />
                    {t('homes.release.button')}
                  </Button>
                )}
                {/* A user's home on a pool that is gone can be deleted, so the user gets a new one. */}
                {(userId == null || home.unavailable === 'pool_missing') && (
                  <Button
                    size="sm"
                    variant="quiet"
                    aria-label={t('homes.delete.label', { folder: home.folder })}
                    onPress={() => {
                      onOpen({ kind: 'delete', home });
                    }}
                  >
                    <IconTrash size={16} aria-hidden />
                    {t('homes.delete.button')}
                  </Button>
                )}
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function useHomesChange<T, V = void>(run: (value: V) => Promise<T>, onClose: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: sharesKey });
      onClose();
    },
  });
}

function MoveDialog({ app, pool, onClose }: { app: PithyxApp; pool: string; onClose: () => void }) {
  const { t } = useTranslation('settings');
  const move = useHomesChange(() => usersCall(app, 'internal.admin.homes.move', { pool }), onClose);
  return (
    <FormDialog
      title={t('homes.move.title', { pool })}
      submit={t('homes.move.submit')}
      disabled={move.isPending}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        move.mutate();
      }}
    >
      <ul className={styles.explainList}>
        <li>{t('homes.move.oneByOne')}</li>
        <li>{t('homes.move.readOnly')}</li>
        <li>{t('homes.move.kept')}</li>
        <li>{t('homes.move.failure')}</li>
      </ul>
      {move.isError && <ErrorNotice error={describeProblem(move.error)} />}
    </FormDialog>
  );
}

type QuotaChoice = 'default' | 'none' | 'own';

function QuotaDialog({
  app,
  home,
  userId,
  onClose,
}: {
  app: PithyxApp;
  home: HomeInfo;
  userId: number;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const own = home.ownQuotaBytes ?? null;
  const [choice, setChoice] = useState<QuotaChoice>(
    own === null ? 'default' : own === 0 ? 'none' : 'own',
  );
  const [size, setSize] = useState(() => sizeText(own === null || own === 0 ? null : own, locale));
  const bytes = parseSize(size.text, size.unit);
  const quotaBytes = choice === 'default' ? null : choice === 'none' ? 0 : bytes;
  const save = useHomesChange(
    () =>
      usersCall(app, 'internal.admin.homes.setQuota', {
        userId,
        quotaBytes: quotaBytes ?? null,
      }),
    onClose,
  );
  const name = home.username ?? home.folder;
  return (
    <FormDialog
      title={t('homes.quota.dialogTitle', { name })}
      submit={t('homes.save')}
      disabled={quotaBytes === undefined || (choice === 'own' && bytes === null) || save.isPending}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <fieldset className={styles.choices}>
        <legend>{t('homes.quota.title')}</legend>
        {(['default', 'none', 'own'] as const).map((option) => (
          <label key={option} className={styles.option}>
            <input
              type="radio"
              name="quota"
              checked={choice === option}
              onChange={() => {
                setChoice(option);
              }}
            />
            {t(`homes.quota.choice.${option}`)}
          </label>
        ))}
      </fieldset>
      {choice === 'own' && (
        <SizeField label={t('homes.quota.size')} value={size} onChange={setSize} />
      )}
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </FormDialog>
  );
}

function ReleaseDialog({
  app,
  home,
  onClose,
}: {
  app: PithyxApp;
  home: HomeInfo;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const shares = useShares(app);
  const [shareId, setShareId] = useState<number | null>(null);
  const target = shareId ?? shares.data?.[0]?.id ?? null;
  const release = useHomesChange(
    (targetShareId: number) =>
      usersCall(app, 'internal.admin.homes.release', { shareId: home.shareId, targetShareId }),
    onClose,
  );
  return (
    <FormDialog
      title={t('homes.release.title', { folder: home.folder })}
      submit={t('homes.release.submit')}
      disabled={target === null || release.isPending}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        if (target !== null) {
          release.mutate(target);
        }
      }}
    >
      <p>{t('homes.release.explain', { folder: home.folder })}</p>
      {shares.data?.length === 0 ? (
        <p className={styles.muted}>{t('homes.release.noShares')}</p>
      ) : (
        <label className={styles.selectField}>
          <span className={styles.fieldLabel}>{t('homes.release.share')}</span>
          <select
            className={styles.select}
            value={target ?? ''}
            onChange={(event) => {
              setShareId(Number(event.currentTarget.value));
            }}
          >
            {(shares.data ?? []).map((share) => (
              <option key={share.id} value={share.id}>
                {share.name}
              </option>
            ))}
          </select>
        </label>
      )}
      {release.isError && <ErrorNotice error={describeProblem(release.error)} />}
    </FormDialog>
  );
}

function DeleteHomeDialog({
  app,
  home,
  onClose,
}: {
  app: PithyxApp;
  home: HomeInfo;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const [typed, setTyped] = useState('');
  const remove = useHomesChange(
    () => usersCall(app, 'internal.admin.homes.delete', { shareId: home.shareId, confirm: typed }),
    onClose,
  );
  return (
    <FormDialog
      title={t('homes.delete.title', { folder: home.folder })}
      submit={t('homes.delete.submit')}
      danger
      disabled={typed !== home.folder || remove.isPending}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        remove.mutate();
      }}
    >
      <p>{t('homes.delete.explain')}</p>
      <TextField
        label={t('homes.delete.confirm', { folder: home.folder })}
        value={typed}
        onChange={setTyped}
        autoComplete="off"
        autoCapitalize="none"
      />
      {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
    </FormDialog>
  );
}
