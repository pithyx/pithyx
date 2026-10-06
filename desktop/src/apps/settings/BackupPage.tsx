import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useId, useRef, useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { PasswordField as NativePasswordField } from '../../auth/controls';
import { NewPasswordField } from '../../auth/password/NewPasswordField';
import { useNewPasswordCheck } from '../../auth/password/PasswordStrengthMeter';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { Button, Checkbox, Dialog } from '@pithyx/ui';
import { backupCall, type BackupCategory, type BackupSummary } from '../sdk/backup';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

const CATEGORY_ORDER: readonly BackupCategory[] = [
  'users',
  'sharedFolders',
  'storage',
  'system',
  'certificates',
  'apps',
];

/**
 * Backup and restore of the configuration (D293): a backup is always everything and always encrypted with a password
 * the administrator chooses; it is downloaded, never kept on the box. A restore checks the file first and then
 * replaces the chosen parts.
 */
export function BackupPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.backup.title')}</h2>
          <p className={styles.muted}>{t('backup.intro')}</p>
        </div>
      </header>
      <CreateForm app={app} />
      <RestoreForm app={app} />
    </div>
  );
}

function CreateForm({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const checkPassword = useNewPasswordCheck();
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState<string | undefined>();
  const [repeatError, setRepeatError] = useState<string | undefined>();
  const create = useMutation({
    mutationFn: () => backupCall(app, 'internal.admin.backup.create', { password }),
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = checkPassword(password);
    const mismatch = password === repeat ? undefined : t('backup.create.mismatch');
    setError(problem);
    setRepeatError(mismatch);
    if (problem === undefined && mismatch === undefined) {
      create.mutate();
    }
  }

  return (
    <form className={styles.form} aria-labelledby="backup-create" onSubmit={submit}>
      <h3 id="backup-create" className={styles.sectionTitle}>
        {t('backup.create.title')}
      </h3>
      <p className={styles.muted}>{t('backup.create.intro')}</p>
      <NewPasswordField
        label={t('backup.create.password')}
        name="backup-password"
        error={error}
        onChange={(event) => {
          setPassword(event.target.value);
          setError(undefined);
          create.reset();
        }}
        required
      />
      <NativePasswordField
        label={t('backup.create.repeat')}
        name="backup-password-repeat"
        autoComplete="new-password"
        error={repeatError}
        onChange={(event) => {
          setRepeat(event.target.value);
          setRepeatError(undefined);
        }}
        required
      />
      <div className={styles.notice}>
        <p>{t('backup.create.keepPassword')}</p>
      </div>
      <div className={styles.buttons}>
        <Button type="submit" variant="primary" isDisabled={create.isPending}>
          {create.isPending ? t('backup.create.running') : t('backup.create.submit')}
        </Button>
      </div>
      {create.isSuccess && (
        <p role="status" className={styles.muted}>
          {t('backup.create.done', { fileName: create.data.fileName })}
        </p>
      )}
      {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
    </form>
  );
}

function RestoreForm({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const ids = useId();
  const fileInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [password, setPassword] = useState('');
  const inspect = useMutation({
    mutationFn: (chosen: File) =>
      backupCall(app, 'internal.admin.backup.inspect', { file: chosen, password }),
  });

  return (
    <section className={styles.form} aria-labelledby="backup-restore">
      <h3 id="backup-restore" className={styles.sectionTitle}>
        {t('backup.restore.title')}
      </h3>
      <p className={styles.muted}>{t('backup.restore.intro')}</p>
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          if (file !== null) {
            inspect.mutate(file);
          }
        }}
      >
        <div className={styles.selectField}>
          <span id={`${ids}-file`} className={styles.fieldLabel}>
            {t('backup.restore.file')}
          </span>
          <div className={styles.filePicker}>
            <Button
              aria-describedby={`${ids}-name`}
              onPress={() => {
                fileInput.current?.click();
              }}
            >
              {t('backup.restore.chooseFile')}
            </Button>
            <span id={`${ids}-name`} className={styles.muted}>
              {file?.name ?? t('backup.restore.noFile')}
            </span>
            {/* The native picker opens from the button; the input itself stays out of sight and out of the tab order. */}
            <input
              ref={fileInput}
              className="visually-hidden"
              type="file"
              accept=".pithyxbackup"
              tabIndex={-1}
              aria-labelledby={`${ids}-file`}
              onChange={(event) => {
                setFile(event.target.files?.[0] ?? null);
                inspect.reset();
              }}
            />
          </div>
        </div>
        <NativePasswordField
          label={t('backup.restore.password')}
          name="backup-restore-password"
          autoComplete="off"
          onChange={(event) => {
            setPassword(event.target.value);
            inspect.reset();
          }}
          required
        />
        <div className={styles.buttons}>
          <Button type="submit" isDisabled={file === null || password === '' || inspect.isPending}>
            {inspect.isPending ? t('backup.restore.checking') : t('backup.restore.check')}
          </Button>
        </div>
        {inspect.isError && <ErrorNotice error={describeProblem(inspect.error)} />}
      </form>
      {inspect.data !== undefined && (
        <RestoreChoice key={inspect.data.restoreId} app={app} summary={inspect.data} />
      )}
    </section>
  );
}

function RestoreChoice({ app, summary }: { app: PithyxApp; summary: BackupSummary }) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const available = CATEGORY_ORDER.filter((category) => summary.categories.includes(category));
  const [chosen, setChosen] = useState<BackupCategory[]>(available);
  const [confirming, setConfirming] = useState(false);
  const restore = useMutation({
    mutationFn: () =>
      backupCall(app, 'internal.admin.backup.restore', {
        restoreId: summary.restoreId,
        categories: chosen,
      }),
    onSettled: () => {
      setConfirming(false);
    },
  });

  if (restore.data !== undefined) {
    return (
      <div className={styles.notice} role="status">
        <p>{t('backup.restore.restarting')}</p>
        {restore.data.problems.length > 0 && (
          <>
            <p>{t('backup.restore.problems')}</p>
            <ul className={styles.plainList}>
              {restore.data.problems.map((problem) => (
                <li key={`${problem.category}-${problem.step}`}>
                  {t(`backup.steps.${problem.step}`, { defaultValue: problem.step })}
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    );
  }

  return (
    <div className={styles.card}>
      <h4 className={styles.subTitle}>{t('backup.restore.contents')}</h4>
      <dl className={styles.facts}>
        <div className={styles.fact}>
          <dt>{t('backup.restore.createdAt')}</dt>
          <dd>
            {new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
              new Date(summary.createdAt),
            )}
          </dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('backup.restore.hostName')}</dt>
          <dd>{summary.hostName}</dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('backup.restore.version')}</dt>
          <dd>{summary.version}</dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('backup.restore.holds')}</dt>
          <dd>
            {t('backup.restore.counts', {
              users: summary.users,
              groups: summary.groups,
              sharedFolders: summary.sharedFolders,
            })}
          </dd>
        </div>
      </dl>
      <fieldset className={styles.choices}>
        <legend className={styles.fieldLabel}>{t('backup.restore.choose')}</legend>
        {available.map((category) => (
          <div key={category}>
            <Checkbox
              isSelected={chosen.includes(category)}
              onChange={(selected) => {
                setChosen((current) =>
                  selected
                    ? CATEGORY_ORDER.filter(
                        (entry) => entry === category || current.includes(entry),
                      )
                    : current.filter((entry) => entry !== category),
                );
              }}
            >
              {t(`backup.categories.${category}.title`)}
            </Checkbox>
            <p className={styles.optionHint}>{t(`backup.categories.${category}.description`)}</p>
          </div>
        ))}
      </fieldset>
      <div className={styles.notice}>
        <p>{t('backup.restore.warning')}</p>
      </div>
      <div className={styles.buttons}>
        <Button
          variant="danger"
          isDisabled={chosen.length === 0}
          onPress={() => {
            setConfirming(true);
          }}
        >
          {t('backup.restore.submit')}
        </Button>
      </div>
      {restore.isError && <ErrorNotice error={describeProblem(restore.error)} />}
      {confirming && (
        <Dialog
          isOpen
          tone="danger"
          isDismissable={!restore.isPending}
          onOpenChange={(open) => {
            if (!open) {
              setConfirming(false);
            }
          }}
          title={t('backup.confirm.title')}
          actions={
            <>
              <Button
                isDisabled={restore.isPending}
                onPress={() => {
                  setConfirming(false);
                }}
              >
                {t('backup.confirm.cancel')}
              </Button>
              <Button
                variant="danger"
                isDisabled={restore.isPending}
                onPress={() => {
                  restore.mutate();
                }}
              >
                {restore.isPending ? t('backup.confirm.running') : t('backup.confirm.submit')}
              </Button>
            </>
          }
        >
          <p>{t('backup.confirm.body')}</p>
          <ul className={styles.explainList}>
            {chosen.map((category) => (
              <li key={category}>{t(`backup.categories.${category}.title`)}</li>
            ))}
          </ul>
          {chosen.includes('users') && <p>{t('backup.confirm.signOut')}</p>}
        </Dialog>
      )}
    </div>
  );
}
