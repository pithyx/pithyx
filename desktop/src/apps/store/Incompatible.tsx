import { Badge } from '@pithyx/ui';
import { useTranslation } from 'react-i18next';
import type { StoreAppSummary } from '../sdk/store';
import styles from './Store.module.css';

export type Incompatible = NonNullable<StoreAppSummary['incompatible']>;

/** Whether the version's manifest is one this box reads, so its rights and Store information can be shown. */
export function readsManifest(incompatible: Incompatible | null): boolean {
  return (
    incompatible === null || incompatible.reason === 'box' || incompatible.reason === 'architecture'
  );
}

/** The badge of an app this box cannot install (D393). */
export function IncompatibleBadge() {
  const { t } = useTranslation('store');
  return <Badge tone="neutral">{t('catalog.incompatible')}</Badge>;
}

/** Why this box cannot install the app, in a sentence that says what may help. */
export function IncompatibleNotice({ incompatible }: { incompatible: Incompatible }) {
  const { t } = useTranslation('store');
  return (
    <p className={styles.notice}>
      {t(`catalog.incompatibleReason.${incompatible.reason}`, {
        range: incompatible.pithyx,
        version: incompatible.boxVersion,
        box: incompatible.boxArchitecture,
        architectures: incompatible.architectures.join(', '),
        manifest: incompatible.manifestVersion ?? '-',
      })}
    </p>
  );
}

/** The short reason next to a version in the list of versions. */
export function useVersionReason(): (incompatible: Incompatible) => string {
  const { t } = useTranslation('store');
  return (incompatible) =>
    incompatible.reason === 'box'
      ? t('catalog.needs', { range: incompatible.pithyx })
      : t(`catalog.versionReason.${incompatible.reason}`, { box: incompatible.boxArchitecture });
}
