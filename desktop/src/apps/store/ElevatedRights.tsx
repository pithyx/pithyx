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
import { manifestText } from '../installed';
import {
  type DeviceNoteKey,
  deviceNote,
  elevatedKind,
  manifestFacts,
  type RiskTier,
  tierOf,
} from './model';
import { ReasonText } from './PermissionText';
import styles from './Store.module.css';

const TIER_CLASS: Record<RiskTier, string | undefined> = {
  elevated: styles.tierElevated,
  high: styles.tierHigh,
  critical: styles.tierCritical,
};

/**
 * An elevated right in the colour of its risk tier (D388) with what it lets the app's background service do, why that
 * is a risk, the author's reason (D311, D387) and for a device whether it is plugged in.
 */
export function ElevatedRightText({
  right,
  id,
  note,
  reason,
}: {
  right: string;
  id?: string;
  note?: DeviceNoteKey | null;
  reason: Readonly<Record<string, string>> | undefined;
}) {
  const { t } = useTranslation('store');
  const parsed = elevatedKind(right);
  const tier = tierOf(right);
  return (
    <span className={`${styles.permissionText} ${TIER_CLASS[tier] ?? ''}`} id={id}>
      <span className={styles.tierLabel}>{t(`elevated.tier.${tier}`)}</span>
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
      <ReasonText reason={reason} />
      {note != null && <span className={styles.notice}>{t(note)}</span>}
    </span>
  );
}

/** Whether the typed text is the app's name, ignoring case and spaces at the ends. */
export function nameConfirmed(typed: string, name: string): boolean {
  return typed.trim().toLocaleLowerCase() === name.trim().toLocaleLowerCase();
}

/**
 * The step of its own for a right that is root on the box (D388): its concrete consequences in red and a field where
 * the administrator types the app's name. Pasting stays possible (accessibility), but a pasted name gets a friendly
 * reminder; dictation and autofill are no pastes and get none.
 */
export function CriticalConfirm({
  name,
  rights,
  typed,
  onType,
}: {
  name: string;
  rights: readonly string[];
  typed: string;
  onType: (typed: string) => void;
}) {
  const { t } = useTranslation('store');
  const [pasted, setPasted] = useState(false);
  return (
    <section className={`${styles.critical} ${styles.form}`} aria-labelledby="store-critical">
      <h4 id="store-critical" className={styles.subTitle}>
        {t('elevated.critical.title', { name })}
      </h4>
      <p>{t('elevated.critical.consequences', { rights: rights.join(', ') })}</p>
      <p>{t('elevated.critical.trust')}</p>
      <TextField
        label={t('elevated.critical.typeName', { name })}
        value={typed}
        onChange={onType}
        autoComplete="off"
        onPaste={() => {
          setPasted(true);
        }}
        onBeforeInput={(event) => {
          if ((event.nativeEvent as InputEvent).inputType === 'insertFromPaste') {
            setPasted(true);
          }
        }}
      />
      {pasted && (
        <p className={styles.notice} role="status">
          {t('elevated.critical.pasted')}
        </p>
      )}
    </section>
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
  const { t, i18n } = useTranslation('store');
  const queryClient = useQueryClient();
  const [granting, setGranting] = useState<string | null>(null);
  const change = useAppChange(
    details.id,
    (rights: {
      right: string;
      granted: boolean;
      password?: string;
      totpCode?: string;
      confirmCritical?: string;
    }) =>
      storeCall(app, 'internal.admin.apps.setElevated', {
        appId: details.id,
        rights: [{ right: rights.right, granted: rights.granted }],
        ...(rights.password === undefined ? {} : { password: rights.password }),
        ...(rights.totpCode === undefined ? {} : { totpCode: rights.totpCode }),
        ...(rights.confirmCritical === undefined
          ? {}
          : { confirmCritical: rights.confirmCritical }),
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
  const facts = manifestFacts(details.manifest);
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
              reason={facts.reasons[entry.right]}
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
          appId={details.id}
          name={manifestText(facts.name, i18n.language)}
          right={granting}
          reason={facts.reasons[granting]}
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

/**
 * Grants one right again with the consent of its tier (D388): a device of the tier elevated with the click, a right of
 * high risk with the step-up, root access also with the app's typed name.
 */
function GrantDialog({
  app,
  appId,
  name,
  right,
  reason,
  onGrant,
  onClose,
}: {
  app: PithyxApp;
  appId: string;
  name: string;
  right: string;
  reason: Readonly<Record<string, string>> | undefined;
  onGrant: (params: {
    password?: string;
    totpCode?: string;
    confirmCritical?: string;
  }) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = useTranslation('store');
  const tier = tierOf(right);
  const needsStepUp = tier !== 'elevated';
  const stepUp = useStepUp(app);
  const until = stepUp.data ?? null;
  const [input, setInput] = useState<StepUpInput>({ password: '', code: '' });
  const [typed, setTyped] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [pending, setPending] = useState(false);
  const ready =
    (!needsStepUp || (stepUp.data !== undefined && stepUpReady(app, until, input))) &&
    (tier !== 'critical' || nameConfirmed(typed, name));
  const grant = () => {
    setPending(true);
    setError(null);
    onGrant({
      ...(needsStepUp ? stepUpParams(app, until, input) : {}),
      ...(tier === 'critical' ? { confirmCritical: appId } : {}),
    }).catch((reason: unknown) => {
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
            {tier === 'critical' ? t('elevated.critical.grant') : t('elevated.confirm')}
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
        <ElevatedRightText right={right} reason={reason} />
        {tier === 'critical' ? (
          <CriticalConfirm name={name} rights={[right]} typed={typed} onType={setTyped} />
        ) : (
          <p className={styles.warning}>{t('elevated.warning')}</p>
        )}
        {needsStepUp && <StepUpFields app={app} until={until} value={input} onChange={setInput} />}
        {error !== null && <ErrorNotice error={describeProblem(error)} />}
      </form>
    </Dialog>
  );
}

/**
 * The permanent mark of an app that holds a special right: "Root access" while it holds a critical one (D388),
 * "Special rights" for any other.
 */
export function ElevatedBadge({ details }: { details: AppDetails }) {
  const { t } = useTranslation('store');
  const granted = details.elevated.filter((entry) => entry.granted);
  if (granted.some((entry) => entry.tier === 'critical')) {
    return <Badge tone="danger">{t('elevated.rootBadge')}</Badge>;
  }
  return granted.length > 0 ? <Badge tone="danger">{t('elevated.badge')}</Badge> : null;
}
