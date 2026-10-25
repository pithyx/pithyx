import { useEffect } from 'react';
import { api } from '../api/client';
import { languages } from '../i18n';

const reportedKey = (userId: number) => `pithyx.notificationLanguage.${String(userId)}`;

/**
 * Tells the box which language this user's desktop uses, for the emails and webhooks it writes to them (D379). Only
 * when it changed since this device last said so, and never in the way of the desktop.
 */
export function useReportLanguage(userId: number, language: string): void {
  useEffect(() => {
    if (!(languages as readonly string[]).includes(language)) {
      return;
    }
    let reported: string | null = null;
    try {
      reported = localStorage.getItem(reportedKey(userId));
    } catch {
      // Without storage the language is told on every start.
    }
    if (reported === language) {
      return;
    }
    void api
      .PUT('/api/notifications/language', { body: { language } })
      .then(({ response }) => {
        if (response.ok) {
          localStorage.setItem(reportedKey(userId), language);
        }
      })
      .catch(() => undefined);
  }, [userId, language]);
}
