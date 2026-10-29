import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  sameConfig,
  secondsLeft,
  urlFor,
  withoutInterface,
  type NetworkConfig,
  type NetworkLink,
} from '../../network/model';
import {
  networkCall,
  networkKey,
  onNetwork,
  type NetworkCheck,
  type NetworkState,
  type PendingNetworkChange,
} from '../sdk/network';
import { describeProblem } from '../storage/shared';
import { ApplyDialog, GlobalDialog, InterfaceDialog, type Editing } from './NetworkDialogs';
import styles from './Settings.module.css';

const KIND_ORDER = { ethernet: 0, bond: 1, vlan: 2 } as const;

/**
 * Settings > Network (step 14.2, D110, D292, D337): every card, bond and VLAN with its link and addresses, live from
 * the topic `system/network`. Changes collect in a draft, so several fields are one change; Apply checks them, warns
 * when this connection's interface changes, applies them and counts down 60 seconds: without Keep, the box rolls back.
 */
export function NetworkPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const network = useQuery({
    queryKey: networkKey,
    queryFn: () => networkCall(app, 'internal.admin.network.get', {}),
  });
  // The topic leaves out this browser's interface; it comes from the REST answer.
  useEffect(() => {
    const stop = onNetwork(app, (state) => {
      queryClient.setQueryData<NetworkState>(networkKey, (previous) => ({
        ...state,
        connectionInterface: previous?.connectionInterface ?? null,
      }));
    });
    void networkCall(app, 'internal.admin.network.watch', { watch: true });
    return () => {
      stop();
      void networkCall(app, 'internal.admin.network.watch', { watch: false });
    };
  }, [app, queryClient]);
  const [draft, setDraft] = useState<NetworkConfig | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [check, setCheck] = useState<NetworkCheck | null>(null);
  const checking = useMutation({
    mutationFn: (config: NetworkConfig) => networkCall(app, 'internal.admin.network.check', config),
    onSuccess: setCheck,
  });

  const state = network.data;
  const config = draft ?? state?.config;
  const dirty = state !== undefined && draft !== null && !sameConfig(draft, state.config);
  const pending = state?.pending ?? null;
  const edit = (next: NetworkConfig) => {
    setDraft(next);
    checking.reset();
  };

  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <p className={styles.muted}>{t('network.intro')}</p>
        </div>
      </header>
      {network.isError && <ErrorNotice error={describeProblem(network.error)} />}
      {pending !== null && <PendingBanner app={app} pending={pending} />}
      {state !== undefined && config !== undefined && (
        <>
          {dirty && (
            <section className={styles.notice} aria-label={t('network.draft.label')}>
              <p>{t('network.draft.text')}</p>
              <div className={styles.buttons}>
                <Button
                  variant="primary"
                  isDisabled={pending !== null || checking.isPending}
                  onPress={() => {
                    checking.mutate(config);
                  }}
                >
                  {t('network.draft.apply')}
                </Button>
                <Button
                  onPress={() => {
                    setDraft(null);
                    checking.reset();
                  }}
                >
                  {t('network.draft.discard')}
                </Button>
              </div>
              {pending !== null && <p className={styles.muted}>{t('network.draft.waiting')}</p>}
              {checking.isError && <ErrorNotice error={describeProblem(checking.error)} />}
            </section>
          )}
          <GlobalCard
            state={state}
            config={config}
            onEdit={() => {
              setEditing({ kind: 'global' });
            }}
          />
          <div className={styles.buttons}>
            <Button
              isDisabled={!state.links.some((link) => link.changeable)}
              onPress={() => {
                setEditing({ kind: 'newBond' });
              }}
            >
              {t('network.addBond')}
            </Button>
            <Button
              isDisabled={!state.links.some((link) => link.changeable)}
              onPress={() => {
                setEditing({ kind: 'newVlan' });
              }}
            >
              {t('network.addVlan')}
            </Button>
          </div>
          <InterfaceList
            state={state}
            config={config}
            onEdit={(name) => {
              setEditing({ kind: 'interface', name });
            }}
            onRemove={(name) => {
              edit(withoutInterface(config, name));
            }}
          />
        </>
      )}
      {editing !== null &&
        state !== undefined &&
        config !== undefined &&
        (editing.kind === 'global' ? (
          <GlobalDialog
            config={config}
            onSave={edit}
            onClose={() => {
              setEditing(null);
            }}
          />
        ) : (
          <InterfaceDialog
            editing={editing}
            state={state}
            config={config}
            onSave={edit}
            onClose={() => {
              setEditing(null);
            }}
          />
        ))}
      {check !== null && (
        <ApplyDialog
          app={app}
          check={check}
          connection={state?.connectionInterface ?? null}
          onApplied={() => {
            setDraft(null);
            setCheck(null);
          }}
          onClose={() => {
            setCheck(null);
          }}
        />
      )}
    </div>
  );
}

/** The waiting change with its countdown: keep it, or undo it before the box does. */
function PendingBanner({ app, pending }: { app: PithyxApp; pending: PendingNetworkChange }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [now, setNow] = useState(() => Date.now());
  // The countdown follows the clock, which lives outside React.
  useEffect(() => {
    const timer = setInterval(() => {
      setNow(Date.now());
    }, 1000);
    return () => {
      clearInterval(timer);
    };
  }, []);
  const left = secondsLeft(pending.deadline, now);
  const settle = useMutation({
    mutationFn: (keep: boolean) =>
      networkCall(app, keep ? 'internal.admin.network.confirm' : 'internal.admin.network.revert', {
        id: pending.id,
      }),
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: networkKey });
    },
  });
  return (
    <section className={styles.notice} role="alert" aria-label={t('network.pending.label')}>
      <p>
        <strong>{t('network.pending.title', { seconds: left })}</strong>
      </p>
      <p>
        {pending.origin === 'console'
          ? t('network.pending.fromConsole')
          : t('network.pending.text')}
      </p>
      {pending.addresses.length > 0 && (
        <>
          <p>{t('network.pending.openNew')}</p>
          <ul className={styles.plainList}>
            {pending.addresses.map((address) => (
              <li key={address}>
                <a className={styles.mono} href={urlFor(address, window.location)}>
                  {urlFor(address, window.location)}
                </a>
              </li>
            ))}
          </ul>
        </>
      )}
      <div className={styles.buttons}>
        <Button
          variant="primary"
          isDisabled={settle.isPending}
          onPress={() => {
            settle.mutate(true);
          }}
        >
          {t('network.pending.keep')}
        </Button>
        <Button
          isDisabled={settle.isPending}
          onPress={() => {
            settle.mutate(false);
          }}
        >
          {t('network.pending.undo')}
        </Button>
      </div>
      {settle.isError && <ErrorNotice error={describeProblem(settle.error)} />}
    </section>
  );
}

function GlobalCard({
  state,
  config,
  onEdit,
}: {
  state: NetworkState;
  config: NetworkConfig;
  onEdit: () => void;
}) {
  const { t } = useTranslation('settings');
  const global = config.global;
  return (
    <section className={styles.card} aria-labelledby="network-global">
      <h3 id="network-global" className={styles.sectionTitle}>
        {t('network.global.title')}
      </h3>
      <dl className={styles.facts}>
        <div className={styles.fact}>
          <dt>{t('network.global.hostName')}</dt>
          <dd className={styles.mono}>{global.hostName}</dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('network.global.dns')}</dt>
          <dd className={styles.mono}>
            {global.dnsServers.length === 0
              ? t('network.global.fromDhcp')
              : global.dnsServers.join(', ')}
          </dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('network.global.searchDomains')}</dt>
          <dd className={styles.mono}>{global.searchDomains.join(', ') || t('network.none')}</dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('network.global.defaultRoute')}</dt>
          <dd className={styles.mono}>
            {global.defaultInterface ?? t('network.global.automatic')}
          </dd>
        </div>
      </dl>
      {state.globalChangeable ? (
        <div className={styles.buttons}>
          <Button onPress={onEdit}>{t('network.global.edit')}</Button>
        </div>
      ) : (
        <p className={styles.muted}>{t('network.global.notChangeable')}</p>
      )}
    </section>
  );
}

function InterfaceList({
  state,
  config,
  onEdit,
  onRemove,
}: {
  state: NetworkState;
  config: NetworkConfig;
  onEdit: (name: string) => void;
  onRemove: (name: string) => void;
}) {
  const { t } = useTranslation('settings');
  const names = new Set(state.links.map((link) => link.name));
  // Bonds and VLANs of the draft that do not exist yet.
  const planned = config.interfaces.filter((item) => !names.has(item.name));
  const links = [...state.links].sort(
    (a, b) => KIND_ORDER[a.kind] - KIND_ORDER[b.kind] || a.name.localeCompare(b.name),
  );
  return (
    <>
      {links.map((link) => (
        <LinkCard
          key={link.name}
          link={link}
          config={config}
          saved={state.config}
          connection={state.connectionInterface === link.name}
          onEdit={onEdit}
          onRemove={onRemove}
        />
      ))}
      {planned.map((item) => (
        <section key={item.name} className={styles.card} aria-labelledby={`nic-${item.name}`}>
          <h3 id={`nic-${item.name}`} className={styles.sectionTitle}>
            <span className={styles.mono}>{item.name}</span>{' '}
            <Badge>{t(`network.kinds.${item.kind}`)}</Badge> <Badge>{t('network.planned')}</Badge>
          </h3>
          <div className={styles.buttons}>
            <Button
              onPress={() => {
                onEdit(item.name);
              }}
            >
              {t('network.edit')}
            </Button>
            <Button
              onPress={() => {
                onRemove(item.name);
              }}
            >
              {t('network.remove')}
            </Button>
          </div>
        </section>
      ))}
    </>
  );
}

function LinkCard({
  link,
  config,
  saved,
  connection,
  onEdit,
  onRemove,
}: {
  link: NetworkLink;
  config: NetworkConfig;
  saved: NetworkConfig;
  connection: boolean;
  onEdit: (name: string) => void;
  onRemove: (name: string) => void;
}) {
  const { t } = useTranslation('settings');
  const text = t as unknown as (key: string) => string;
  const item = config.interfaces.find((candidate) => candidate.name === link.name);
  const before = saved.interfaces.find((candidate) => candidate.name === link.name);
  const changed = JSON.stringify(item ?? null) !== JSON.stringify(before ?? null);
  const member = config.interfaces.find((candidate) => candidate.bond?.members.includes(link.name));
  const removable = item !== undefined && link.changeable;
  return (
    <section className={styles.card} aria-labelledby={`nic-${link.name}`}>
      <h3 id={`nic-${link.name}`} className={styles.sectionTitle}>
        <span className={styles.mono}>{link.name}</span>{' '}
        <Badge>{t(`network.kinds.${link.kind}`)}</Badge>{' '}
        <Badge>
          {link.up ? t('network.up') : link.carrier ? t('network.off') : t('network.down')}
        </Badge>
        {connection && (
          <>
            {' '}
            <Badge>{t('network.thisConnection')}</Badge>
          </>
        )}
        {link.defaultRoute && (
          <>
            {' '}
            <Badge>{t('network.defaultRoute')}</Badge>
          </>
        )}
        {changed && (
          <>
            {' '}
            <Badge>{t('network.changed')}</Badge>
          </>
        )}
      </h3>
      <dl className={styles.facts}>
        {member !== undefined ? (
          <div className={styles.fact}>
            <dt>{t('network.bond')}</dt>
            <dd className={styles.mono}>{member.name}</dd>
          </div>
        ) : (
          <>
            <div className={styles.fact}>
              <dt>{t('network.method')}</dt>
              <dd>{text(`network.methods.${link.method}`)}</dd>
            </div>
            <div className={styles.fact}>
              <dt>{t('network.addresses')}</dt>
              <dd className={styles.mono}>{link.addresses.join(', ') || t('network.none')}</dd>
            </div>
            <div className={styles.fact}>
              <dt>{t('network.gateway')}</dt>
              <dd className={styles.mono}>{link.gateways.join(', ') || t('network.none')}</dd>
            </div>
            <div className={styles.fact}>
              <dt>{t('network.dns')}</dt>
              <dd className={styles.mono}>{link.dnsServers.join(', ') || t('network.none')}</dd>
            </div>
          </>
        )}
        {link.vlanId !== null && (
          <div className={styles.fact}>
            <dt>{t('network.vlan')}</dt>
            <dd>{t('network.vlanOn', { id: link.vlanId, parent: link.vlanParent ?? '' })}</dd>
          </div>
        )}
        {link.speedMbps !== null && (
          <div className={styles.fact}>
            <dt>{t('network.speed')}</dt>
            <dd>
              {t('network.speedValue', { mbps: link.speedMbps })}
              {link.duplex !== null && ` (${text(`network.duplex.${link.duplex}`)})`}
            </dd>
          </div>
        )}
        <div className={styles.fact}>
          <dt>{t('network.mtu')}</dt>
          <dd>{link.mtu}</dd>
        </div>
        {link.macAddress !== null && (
          <div className={styles.fact}>
            <dt>{t('network.mac')}</dt>
            <dd className={styles.mono}>{link.macAddress}</dd>
          </div>
        )}
      </dl>
      {!link.changeable ? (
        <p className={styles.muted}>{t('network.notChangeable')}</p>
      ) : (
        member === undefined && (
          <div className={styles.buttons}>
            <Button
              aria-label={t('network.editLabel', { name: link.name })}
              onPress={() => {
                onEdit(link.name);
              }}
            >
              {t('network.edit')}
            </Button>
            {removable && (
              <Button
                aria-label={t('network.removeLabel', { name: link.name })}
                onPress={() => {
                  onRemove(link.name);
                }}
              >
                {link.kind === 'ethernet' ? t('network.useDhcp') : t('network.remove')}
              </Button>
            )}
          </div>
        )
      )}
    </section>
  );
}
