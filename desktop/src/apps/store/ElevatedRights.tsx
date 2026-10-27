import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button, Dialog, PasswordField, Switch, TextField } from '@pithyx/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import type { AppDetails } from '../sdk/store';
import { storeCall } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { useAppChange } from './AppPage';
import { type DeviceNoteKey, deviceNote, elevatedKind } from './model';
import styles from './Store.module.css';

/**
 * An elevated right with what it lets the app's background service do and why that is a risk (D311, D387), and for a
 * device whether it is plugged in.
 */
export function ElevatedRightText({
  right,
  id,
  note,
}: {
  right: string;
  id?: string;
  note?: DeviceNoteKey | null;
}) {
  const { t } = useTranslation('store');
  const parsed = elevatedKind(right);
  return (
    <span className={styles.permissionText} id={id}>
      <span className={styles.permissionTitle}>
        {parsed.kind === 'device'
          ? parsed.deviceClass === undefined
            ? t('elevated.device.title', { path: parsed.value })
            : t(`elevated.deviceClass.${parsed.deviceClass}`)
          : parsed.kind === 'capability'
            ? t('elevated.capability.title', { name: parsed.value })
            : parsed.kind === 'unknown'
              ? parsed.value
              : t(`elevated.${parsed.kind}.title`)}
      </span>
      <span className={parsed.kind === 'dockerSocketRaw' ? styles.warning : styles.muted}>
        {parsed.kind === 'capability'
          ? t('elevated.capability.risk', { name: parsed.value })
          : parsed.kind === 'unknown'
            ? t('elevated.unknown')
            : t(`elevated.${parsed.kind}.risk`)}
      </span>
      {note != null && <span className={styles.notice}>{t(note)}</span>}
    </span>
  );
}

export const stepUpKey = ['store', 'stepUp'] as const;

/** Until when this session holds a step-up, or null; asked again whenever a dialog needs it. */
export function useStepUp(app: PithyxApp) {
  return useQuery({
    queryKey: stepUpKey,
    queryFn: () => storeCall(app, 'internal.admin.auth.stepUp', {}),
    staleTime: 0,
  });
}

export interface StepUpInput {
  password: string;
  code: string;
}

/** The password and, with two-factor sign-in on, the code that confirm granting special rights; or that it is confirmed. */
export function StepUpFields({
  app,
  until,
  value,
  onChange,
}: {
  app: PithyxApp;
  until: string | null;
  value: StepUpInput;
  onChange: (value: StepUpInput) => void;
}) {
  const { t } = useTranslation('store');
  const locale = useFormatLocale();
  const twoFactor = app.context.user.twoFactor;
  if (until !== null) {
    return (
      <p className={styles.muted}>
        {t('elevated.stepUpHeld', {
          time: new Date(until).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' }),
        })}
      </p>
    );
  }
  return (
    <div className={styles.form}>
      <p className={styles.muted}>{t('elevated.stepUp', { twoFactor: String(twoFactor) })}</p>
      <PasswordField
        label={t('elevated.password')}
        value={value.password}
        onChange={(password) => {
          onChange({ ...value, password });
        }}
        autoComplete="current-password"
      />
      {twoFactor && (
        <TextField
          label={t('elevated.code')}
          value={value.code}
          onChange={(code) => {
            onChange({ ...value, code });
          }}
          inputMode="numeric"
          autoComplete="one-time-code"
        />
      )}
    </div>
  );
}

/** Whether the fields are filled enough to send, or a step-up is held already. */
export function stepUpReady(app: PithyxApp, until: string | null, value: StepUpInput): boolean {
  return (
    until !== null ||
    (value.password !== '' && (!app.context.user.twoFactor || value.code.trim() !== ''))
  );
}

/** The step-up part of a request: nothing while one is held. */
export function stepUpParams(
  app: PithyxApp,
  until: string | null,
  value: StepUpInput,
): { password?: string; totpCode?: string } {
  if (until !== null) {
    return {};
  }
  return {
    password: value.password,
    ...(app.context.user.twoFactor ? { totpCode: value.code.trim() } : {}),
  };
}

/**
 * The special rights of an installed app (D311): each with its risk and whether it holds it. Revoking one restarts the
 * background service without it; granting one again needs a step-up.
 */
export function ElevatedSection({ app, details }: { app: PithyxApp; details: AppDetails }) {
  const { t } = useTranslation('store');
  const queryClient = useQueryClient();
  const [granting, setGranting] = useState<string | null>(null);
  const change = useAppChange(
    details.id,
    (rights: { right: string; granted: boolean; password?: string; totpCode?: string }) =>
      storeCall(app, 'internal.admin.apps.setElevated', {
        appId: details.id,
        rights: [{ right: rights.right, granted: rights.granted }],
        ...(rights.password === undefined ? {} : { password: rights.password }),
        ...(rights.totpCode === undefined ? {} : { totpCode: rights.totpCode }),
      }),
    (current, { right, granted }) => ({
      ...current,
      elevated: current.elevated.map((entry) =>
        entry.right === right ? { ...entry, granted } : entry,
      ),
    }),
  );
  if (details.elevated.length === 0) {
    return null;
  }
  return (
    <section className={styles.group} aria-labelledby="store-elevated">
      <h3 id="store-elevated" className={styles.sectionTitle}>
        {t('elevated.section')}
      </h3>
      <p className={styles.muted}>{t('elevated.sectionIntro')}</p>
      <ul className={styles.permissions}>
        {details.elevated.map((entry) => (
          <li key={entry.right} className={styles.permission}>
            <ElevatedRightText
              right={entry.right}
              id={`store-elevated-${entry.right}`}
              note={deviceNote(entry.right, details.devices, 'installed')}
            />
            <Switch
              aria-labelledby={`store-elevated-${entry.right}`}
              isSelected={entry.granted}
              isDisabled={details.busy}
              onChange={(granted) => {
                if (granted) {
                  setGranting(entry.right);
                } else {
                  change.mutate({ right: entry.right, granted });
                }
              }}
            >
              <span className="visually-hidden">{entry.right}</span>
            </Switch>
          </li>
        ))}
      </ul>
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      {granting !== null && (
        <GrantDialog
          app={app}
          right={granting}
          onGrant={async (params) => {
            await change.mutateAsync({ right: granting, granted: true, ...params });
            await queryClient.invalidateQueries({ queryKey: stepUpKey });
            setGranting(null);
          }}
          onClose={() => {
            setGranting(null);
          }}
        />
      )}
    </section>
  );
}

function GrantDialog({
  app,
  right,
  onGrant,
  onClose,
}: {
  app: PithyxApp;
  right: string;
  onGrant: (params: { password?: string; totpCode?: string }) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = useTranslation('store');
  const stepUp = useStepUp(app);
  const until = stepUp.data ?? null;
  const [input, setInput] = useState<StepUpInput>({ password: '', code: '' });
  const [error, setError] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const ready = stepUp.data !== undefined && stepUpReady(app, until, input);
  const grant = () => {
    setPending(true);
    setError(null);
    onGrant(stepUpParams(app, until, input)).catch((reason: unknown) => {
      setError(reason);
      setPending(false);
    });
  };
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('elevated.grantTitle')}
      actions={
        <>
          <Button onPress={onClose}>{t('elevated.cancel')}</Button>
          <Button variant="danger" isDisabled={!ready || pending} onPress={grant}>
            {t('elevated.confirm')}
          </Button>
        </>
      }
    >
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          if (ready && !pending) {
            grant();
          }
        }}
      >
        <ElevatedRightText right={right} />
        <p className={styles.warning}>{t('elevated.warning')}</p>
        <StepUpFields app={app} until={until} value={input} onChange={setInput} />
        {error !== null && <ErrorNotice error={describeProblem(error)} />}
      </form>
    </Dialog>
  );
}

/** The permanent mark of an app that holds a special right. */
export function ElevatedBadge({ details }: { details: AppDetails }) {
  const { t } = useTranslation('store');
  return details.elevated.some((entry) => entry.granted) ? (
    <Badge tone="danger">{t('elevated.badge')}</Badge>
  ) : null;
}
