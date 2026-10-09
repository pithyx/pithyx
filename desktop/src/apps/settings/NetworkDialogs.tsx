import type { PithyxApp } from '@pithyx/sdk';
import { Button, Checkbox, Dialog, TextField } from '@pithyx/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { IconAlertTriangle } from '../../ui/icons';
import { IpFields } from '../../network/IpFields';
import {
  bondCandidates,
  bondName,
  defaultInterface,
  gatewayCandidates,
  ipFormOf,
  splitList,
  urlFor,
  vlanName,
  vlanParents,
  withInterface,
  withIp,
  type BondMode,
  type InterfaceConfig,
  type NetworkConfig,
} from '../../network/model';
import { networkCall, networkKey, type NetworkCheck, type NetworkState } from '../sdk/network';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

export type Editing =
  | { kind: 'interface'; name: string }
  | { kind: 'newBond' }
  | { kind: 'newVlan' }
  | { kind: 'global' };

const BOND_MODES: readonly BondMode[] = ['activeBackup', 'lacp', 'balanceAlb'];

/** The interface that a dialog starts from: its settings in the draft, or a new bond or VLAN. */
function startingPoint(
  editing: Editing,
  state: NetworkState,
  config: NetworkConfig,
): InterfaceConfig {
  if (editing.kind === 'interface') {
    const link = state.links.find((candidate) => candidate.name === editing.name);
    return (
      config.interfaces.find((item) => item.name === editing.name) ?? {
        ...defaultInterface(editing.name),
        kind: link?.kind ?? 'ethernet',
      }
    );
  }
  if (editing.kind === 'newBond') {
    return {
      ...defaultInterface(bondName(config, state.links)),
      kind: 'bond',
      bond: { members: [], mode: 'activeBackup', primary: null, lacpRate: 'slow' },
    };
  }
  const parent = vlanParents(state.links, config)[0] ?? '';
  return { ...defaultInterface(vlanName(parent, 10)), kind: 'vlan', vlan: { parent, id: 10 } };
}

/**
 * Edits one interface of the draft: on or off, its addresses and MTU, a bond's cards and mode, a VLAN's parent and
 * id. Nothing applies here; the page applies the whole draft as one change.
 */
export function InterfaceDialog({
  editing,
  state,
  config,
  onSave,
  onClose,
}: {
  editing: Exclude<Editing, { kind: 'global' }>;
  state: NetworkState;
  config: NetworkConfig;
  onSave: (next: NetworkConfig) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const ids = useId();
  const [item, setItem] = useState(() => startingPoint(editing, state, config));
  const [mtu, setMtu] = useState(
    item.mtu === null || item.mtu === undefined ? '' : String(item.mtu),
  );
  const [vlanId, setVlanId] = useState(String(item.vlan?.id ?? ''));
  const [ip, setIp] = useState(() => ipFormOf(item));
  const isNew = editing.kind !== 'interface';
  const bond = item.bond ?? null;
  const vlan = item.vlan ?? null;
  const candidates =
    item.bond === null || item.bond === undefined
      ? []
      : [
          ...new Set([...bondCandidates(state.links, config, item.name), ...item.bond.members]),
        ].sort();
  const parents = vlanParents(state.links, config);
  const connection = state.connectionInterface;
  const touchesConnection =
    connection !== null &&
    (item.name === connection ||
      (item.bond?.members.includes(connection) ?? false) ||
      item.vlan?.parent === connection);

  const save = () => {
    const id = Number.parseInt(vlanId, 10);
    const next = withIp(
      {
        ...item,
        mtu: mtu.trim() === '' ? null : Number.parseInt(mtu, 10),
        vlan:
          item.vlan === null || item.vlan === undefined
            ? null
            : { ...item.vlan, id: Number.isNaN(id) ? 0 : id },
      },
      ip,
    );
    const renamed = isNew ? next : { ...next, name: item.name };
    onSave(withInterface(config, renamed));
    onClose();
  };

  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={
        editing.kind === 'newBond'
          ? t('network.dialog.newBond')
          : editing.kind === 'newVlan'
            ? t('network.dialog.newVlan')
            : t('network.dialog.edit', { name: item.name })
      }
      actions={
        <>
          <Button onPress={onClose}>{t('network.dialog.cancel')}</Button>
          <Button variant="primary" type="submit" form={`${ids}-form`}>
            {t('network.dialog.done')}
          </Button>
        </>
      }
    >
      <form
        id={`${ids}-form`}
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          save();
        }}
      >
        {touchesConnection && (
          <p className={styles.notice} role="note">
            {t('network.dialog.connectionWarning')}
          </p>
        )}
        {isNew && (
          <TextField
            label={t('network.dialog.name')}
            description={t('network.dialog.nameHint')}
            value={item.name}
            maxLength={15}
            autoComplete="off"
            spellCheck="false"
            onChange={(name) => {
              setItem({ ...item, name });
            }}
          />
        )}
        {item.bond !== null && item.bond !== undefined && (
          <fieldset className={styles.choices}>
            <legend>{t('network.dialog.members')}</legend>
            {candidates.length === 0 && (
              <p className={styles.muted}>{t('network.dialog.noCards')}</p>
            )}
            {candidates.map((name) => (
              <Checkbox
                key={name}
                isSelected={item.bond?.members.includes(name) ?? false}
                onChange={(selected) => {
                  const members = item.bond?.members ?? [];
                  setItem({
                    ...item,
                    bond: {
                      mode: item.bond?.mode ?? 'activeBackup',
                      lacpRate: item.bond?.lacpRate ?? 'slow',
                      primary: item.bond?.primary ?? null,
                      members: selected
                        ? [...members, name].sort()
                        : members.filter((member) => member !== name),
                    },
                  });
                }}
              >
                <span className={styles.mono}>{name}</span>
                {name === connection && ` ${t('network.dialog.thisConnection')}`}
              </Checkbox>
            ))}
          </fieldset>
        )}
        {bond !== null && (
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('network.dialog.mode')}</span>
            <select
              className={styles.select}
              value={bond.mode}
              onChange={(event) => {
                setItem({ ...item, bond: { ...bond, mode: event.target.value as BondMode } });
              }}
            >
              {BOND_MODES.map((mode) => (
                <option key={mode} value={mode}>
                  {t(`network.bondModes.${mode}`)}
                </option>
              ))}
            </select>
            <span className={styles.optionHint}>{t(`network.bondModeHints.${bond.mode}`)}</span>
          </label>
        )}
        {bond?.mode === 'activeBackup' && (
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('network.dialog.primary')}</span>
            <select
              className={styles.select}
              value={bond.primary ?? ''}
              onChange={(event) => {
                setItem({
                  ...item,
                  bond: { ...bond, primary: event.target.value === '' ? null : event.target.value },
                });
              }}
            >
              <option value="">{t('network.dialog.noPrimary')}</option>
              {bond.members.map((member) => (
                <option key={member} value={member}>
                  {member}
                </option>
              ))}
            </select>
          </label>
        )}
        {bond?.mode === 'lacp' && (
          <label className={styles.selectField}>
            <span className={styles.fieldLabel}>{t('network.dialog.lacpRate')}</span>
            <select
              className={styles.select}
              value={bond.lacpRate ?? 'slow'}
              onChange={(event) => {
                setItem({
                  ...item,
                  bond: { ...bond, lacpRate: event.target.value as 'slow' | 'fast' },
                });
              }}
            >
              <option value="slow">{t('network.dialog.lacpSlow')}</option>
              <option value="fast">{t('network.dialog.lacpFast')}</option>
            </select>
          </label>
        )}
        {vlan !== null && isNew && (
          <>
            <label className={styles.selectField}>
              <span className={styles.fieldLabel}>{t('network.dialog.parent')}</span>
              <select
                className={styles.select}
                value={vlan.parent}
                onChange={(event) => {
                  const parent = event.target.value;
                  setItem({
                    ...item,
                    name: vlanName(parent, Number.parseInt(vlanId, 10) || 0),
                    vlan: { ...vlan, parent },
                  });
                }}
              >
                {parents.map((parent) => (
                  <option key={parent} value={parent}>
                    {parent}
                  </option>
                ))}
              </select>
            </label>
            <TextField
              label={t('network.dialog.vlanId')}
              description={t('network.dialog.vlanIdHint')}
              value={vlanId}
              inputMode="numeric"
              autoComplete="off"
              onChange={(value) => {
                setVlanId(value);
                const id = Number.parseInt(value, 10);
                if (!Number.isNaN(id)) {
                  setItem({ ...item, name: vlanName(vlan.parent, id) });
                }
              }}
            />
          </>
        )}
        <Checkbox
          isSelected={item.enabled}
          onChange={(enabled) => {
            setItem({ ...item, enabled });
          }}
        >
          {t('network.dialog.enabled')}
        </Checkbox>
        <IpFields form={ip} onChange={setIp} disabled={!item.enabled} />
        <TextField
          label={t('network.dialog.mtu')}
          description={t('network.dialog.mtuHint')}
          value={mtu}
          inputMode="numeric"
          autoComplete="off"
          onChange={setMtu}
        />
      </form>
    </Dialog>
  );
}

/** Host name, name servers, search domains and the interface of the default route. */
export function GlobalDialog({
  config,
  onSave,
  onClose,
}: {
  config: NetworkConfig;
  onSave: (next: NetworkConfig) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const ids = useId();
  const [hostName, setHostName] = useState(config.global.hostName);
  const [dns, setDns] = useState(config.global.dnsServers.join(' '));
  const [domains, setDomains] = useState(config.global.searchDomains.join(' '));
  const [preferred, setPreferred] = useState(config.global.defaultInterface ?? '');
  const candidates = gatewayCandidates(config);
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('network.global.title')}
      actions={
        <>
          <Button onPress={onClose}>{t('network.dialog.cancel')}</Button>
          <Button variant="primary" type="submit" form={`${ids}-form`}>
            {t('network.dialog.done')}
          </Button>
        </>
      }
    >
      <form
        id={`${ids}-form`}
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          onSave({
            ...config,
            global: {
              hostName: hostName.trim(),
              dnsServers: splitList(dns),
              searchDomains: splitList(domains),
              defaultInterface: preferred === '' ? null : preferred,
            },
          });
          onClose();
        }}
      >
        <TextField
          label={t('network.global.hostName')}
          description={t('network.global.hostNameHint')}
          value={hostName}
          maxLength={63}
          autoComplete="off"
          spellCheck="false"
          onChange={setHostName}
        />
        <TextField
          label={t('network.global.dns')}
          description={t('network.global.dnsHint')}
          value={dns}
          autoComplete="off"
          spellCheck="false"
          onChange={setDns}
        />
        <TextField
          label={t('network.global.searchDomains')}
          description={t('network.global.searchDomainsHint')}
          value={domains}
          autoComplete="off"
          spellCheck="false"
          onChange={setDomains}
        />
        <label className={styles.selectField}>
          <span className={styles.fieldLabel}>{t('network.global.defaultRoute')}</span>
          <select
            className={styles.select}
            value={preferred}
            onChange={(event) => {
              setPreferred(event.target.value);
            }}
          >
            <option value="">{t('network.global.automatic')}</option>
            {candidates.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
          <span className={styles.optionHint}>{t('network.global.defaultRouteHint')}</span>
        </label>
      </form>
    </Dialog>
  );
}

/** What the change does, the warning for this connection, and the button that applies it. */
export function ApplyDialog({
  app,
  check,
  connection,
  onApplied,
  onClose,
}: {
  app: PithyxApp;
  check: NetworkCheck;
  connection: string | null;
  onApplied: () => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const apply = useMutation({
    mutationFn: () => networkCall(app, 'internal.admin.network.apply', check.config),
    onSuccess: async () => {
      onApplied();
      await queryClient.invalidateQueries({ queryKey: networkKey });
    },
  });
  return (
    <Dialog
      isOpen
      role="alertdialog"
      tone={check.connectionAffected ? 'danger' : 'default'}
      {...(check.connectionAffected ? { icon: <IconAlertTriangle aria-hidden /> } : {})}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('network.apply.title')}
      actions={
        <>
          <Button onPress={onClose}>{t('network.dialog.cancel')}</Button>
          <Button
            variant={check.connectionAffected ? 'danger' : 'primary'}
            isDisabled={apply.isPending}
            onPress={() => {
              apply.mutate();
            }}
          >
            {t('network.apply.confirm')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <p>{t('network.apply.text', { seconds: 60 })}</p>
        {check.interfaces.length > 0 && (
          <p>{t('network.apply.interfaces', { names: check.interfaces.join(', ') })}</p>
        )}
        {check.hostNameChanged && (
          <p>{t('network.apply.hostName', { name: check.config.global.hostName })}</p>
        )}
        {check.connectionAffected && (
          <p className={styles.notice} role="note">
            <strong>{t('network.apply.connectionWarning', { name: connection ?? '' })}</strong>
          </p>
        )}
        {check.addresses.length > 0 && (
          <>
            <p>{t('network.apply.addresses')}</p>
            <ul className={styles.plainList}>
              {check.addresses.map((address) => (
                <li key={address} className={styles.mono}>
                  {urlFor(address, window.location)}
                </li>
              ))}
            </ul>
          </>
        )}
        {apply.isError && <ErrorNotice error={describeProblem(apply.error)} />}
      </div>
    </Dialog>
  );
}
