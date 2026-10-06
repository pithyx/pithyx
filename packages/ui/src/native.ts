// The components' class names and texts for native HTML elements that look the same, without React Aria: for code
// that has to stay small, such as the desktop's sign-in screen. Unused class names are left out of a bundle.
import button from './Button.module.css';
import controls from './Controls.module.css';
import textField from './TextField.module.css';

export { cx } from './cx';
export { uiTexts, type UiTexts } from './texts';

/** The class names of `Button`: `button`, one per variant, `small` and `spinner` for a pending state. */
export const buttonStyles = button;
/** The class names of `Checkbox`, `Switch`, `ProgressBar` and `Badge`; a checkbox is `checkbox` with a `box`. */
export const controlStyles = controls;
/** The class names of `TextField`, `PasswordField`, `OtpField` and `SearchField`. */
export const textFieldStyles = textField;
