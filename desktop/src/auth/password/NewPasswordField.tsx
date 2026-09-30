import { useState } from 'react';
import { PasswordField, type FieldProps } from '../controls';
import { PasswordStrengthMeter } from './PasswordStrengthMeter';

/** A password field for a new password, with the live strength meter as its description. */
export function NewPasswordField({
  onChange,
  ...props
}: Omit<FieldProps, 'type' | 'trailing' | 'description' | 'autoComplete'>) {
  const [password, setPassword] = useState('');
  return (
    <PasswordField
      {...props}
      autoComplete="new-password"
      description={<PasswordStrengthMeter password={password} />}
      onChange={(event) => {
        setPassword(event.target.value);
        onChange?.(event);
      }}
    />
  );
}
