import { TextField } from '@pithyx/ui';
import { useTranslation } from 'react-i18next';
import { parseSize, SIZE_UNITS, type SizeUnit } from '../storage/datasets';
import styles from './Settings.module.css';

/** A size in a text field with its unit; empty means no quota. In a `FormRow` the row labels the field. */
export function SizeField({
  label,
  description,
  inRow = false,
  value,
  onChange,
}: {
  label: string;
  description?: string;
  inRow?: boolean;
  value: { text: string; unit: SizeUnit };
  onChange: (value: { text: string; unit: SizeUnit }) => void;
}) {
  const { t } = useTranslation('settings');
  return (
    <TextField
      {...(inRow ? {} : { label, description })}
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
