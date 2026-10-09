import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Field } from '../auth/controls';
import type { IpForm, Ipv4Mode, Ipv6Mode } from './model';
import styles from './Network.module.css';

const IPV4_MODES: readonly Ipv4Mode[] = ['dhcp', 'static', 'off'];
const IPV6_MODES: readonly Ipv6Mode[] = ['auto', 'static', 'off'];

/**
 * How an interface gets its IPv4 and IPv6 addresses: the same fields in Settings > Network and in step 2 of the
 * installer (D294, no second UI). Native controls, so the installer's first chunk stays small.
 */
export function IpFields({
  form,
  onChange,
  disabled = false,
}: {
  form: IpForm;
  onChange: (next: IpForm) => void;
  disabled?: boolean;
}) {
  const { t } = useTranslation('network');
  const ids = useId();
  const set = (patch: Partial<IpForm>) => {
    onChange({ ...form, ...patch });
  };
  return (
    <div className={styles.ipFields}>
      <fieldset className={styles.family} disabled={disabled}>
        <legend className={styles.legend}>{t('ip.ipv4')}</legend>
        <label className={styles.selectField} htmlFor={`${ids}-ipv4`}>
          <span className={styles.label}>{t('ip.mode')}</span>
          <select
            id={`${ids}-ipv4`}
            className={styles.select}
            value={form.ipv4}
            onChange={(event) => {
              set({ ipv4: event.target.value as Ipv4Mode });
            }}
          >
            {IPV4_MODES.map((mode) => (
              <option key={mode} value={mode}>
                {t(`ip.ipv4Modes.${mode}`)}
              </option>
            ))}
          </select>
        </label>
        {form.ipv4 === 'static' && (
          <>
            <Field
              label={t('ip.address')}
              description={t('ip.ipv4AddressHint')}
              value={form.ipv4Address}
              required
              autoComplete="off"
              spellCheck={false}
              inputMode="decimal"
              onChange={(event) => {
                set({ ipv4Address: event.target.value });
              }}
            />
            <Field
              label={t('ip.gateway')}
              value={form.ipv4Gateway}
              autoComplete="off"
              spellCheck={false}
              inputMode="decimal"
              onChange={(event) => {
                set({ ipv4Gateway: event.target.value });
              }}
            />
          </>
        )}
      </fieldset>
      <fieldset className={styles.family} disabled={disabled}>
        <legend className={styles.legend}>{t('ip.ipv6')}</legend>
        <label className={styles.selectField} htmlFor={`${ids}-ipv6`}>
          <span className={styles.label}>{t('ip.mode')}</span>
          <select
            id={`${ids}-ipv6`}
            className={styles.select}
            value={form.ipv6}
            onChange={(event) => {
              set({ ipv6: event.target.value as Ipv6Mode });
            }}
          >
            {IPV6_MODES.map((mode) => (
              <option key={mode} value={mode}>
                {t(`ip.ipv6Modes.${mode}`)}
              </option>
            ))}
          </select>
        </label>
        {form.ipv6 === 'static' && (
          <>
            <Field
              label={t('ip.address')}
              description={t('ip.ipv6AddressHint')}
              value={form.ipv6Address}
              required
              autoComplete="off"
              spellCheck={false}
              onChange={(event) => {
                set({ ipv6Address: event.target.value });
              }}
            />
            <Field
              label={t('ip.gateway')}
              value={form.ipv6Gateway}
              autoComplete="off"
              spellCheck={false}
              onChange={(event) => {
                set({ ipv6Gateway: event.target.value });
              }}
            />
          </>
        )}
      </fieldset>
    </div>
  );
}
