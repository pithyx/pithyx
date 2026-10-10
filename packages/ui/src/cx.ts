/**
 * Joins class names, skipping empty values.
 *
 * @param names - Class names; `false`, `null`, `undefined` and empty strings are left out.
 * @returns The names joined by spaces.
 *
 * @example
 * ```ts
 * cx(styles.item, isActive && styles.active); // "item active" or "item"
 * ```
 */
export function cx(...names: (string | false | null | undefined)[]): string {
  return names.filter(Boolean).join(' ');
}
