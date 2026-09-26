import { useTranslation } from 'react-i18next';
import { formatBytes } from '../desktop/format';

/** A parameter value. Byte counts are formatted for the user's locale before they reach the message. */
export type MessageParam = string | number | { bytes: number };

/** A translatable message as the daemon sends it: a stable code and its parameters, like an error. */
export interface Message {
  code: string;
  params: Record<string, MessageParam>;
}

export function formatParams(
  params: Record<string, MessageParam>,
  locale: string,
): Record<string, string | number> {
  return Object.fromEntries(
    Object.entries(params).map(([name, value]) => [
      name,
      typeof value === 'object' ? formatBytes(value.bytes, locale) : value,
    ]),
  );
}

/**
 * Translates one part of a message (title, body, action) from a namespace keyed by message codes.
 * Returns undefined for a part the message does not have.
 */
export function useMessages(ns: 'notifications' | 'jobs') {
  const { t, i18n } = useTranslation(ns);
  // Codes are dynamic; i18n.test.ts checks that en and de define the same keys.
  const translate = t as unknown as (key: string, options: object) => string;
  return (message: Message, part: string): string | undefined => {
    const key = `${message.code}.${part}`;
    return i18n.exists(key, { ns })
      ? translate(key, formatParams(message.params, i18n.language))
      : undefined;
  };
}
