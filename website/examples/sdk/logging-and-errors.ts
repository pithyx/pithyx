// Logging to the Log Center, telling retryable failures from final ones, and showing errors in the user's language.
import { isProblem, PithyxError, type PithyxApp } from '@pithyx/sdk';
import { createI18n, type I18n } from '@pithyx/sdk/i18n';

/** Failures that may pass by themselves: the desktop or the box was busy, restarting or starting the backend. */
const RETRYABLE = new Set([
  'sdk.request.timeout',
  'sdk.backend.unreachable',
  'sdk.box.unreachable',
  'app.backend.unavailable',
  'auth.rate.limited',
]);

function codeOf(error: unknown): string {
  return error instanceof PithyxError || isProblem(error) ? error.code : 'unknown';
}

function wait(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Runs `work` up to `attempts` times, waiting 1, 2, 4 ... seconds (or what the box asks for) between tries. */
export async function withRetry<T>(
  app: PithyxApp,
  work: () => Promise<T>,
  attempts = 4,
): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try {
      return await work();
    } catch (error) {
      const code = codeOf(error);
      if (!RETRYABLE.has(code) || attempt >= attempts) {
        throw error;
      }
      const after = error instanceof PithyxError ? Number(error.params.retryAfterSeconds) : NaN;
      const delay = Number.isFinite(after) ? after * 1000 : 1000 * 2 ** (attempt - 1);
      app.log.debug('Retrying', { code, attempt, delay });
      await wait(delay);
    }
  }
}

/** A backend answer as a value, or its problem as a PithyxError, so withRetry sees the 503 of a stopped backend. */
export async function backendJson<T>(app: PithyxApp, path: string): Promise<T> {
  const response = await app.backend.fetch(path);
  if (response.ok) {
    return (await response.json()) as T;
  }
  const body: unknown = await response.json().catch(() => null);
  throw new PithyxError(
    isProblem(body)
      ? body
      : { code: 'app.backend.unavailable', params: {}, detail: `HTTP ${String(response.status)}` },
  );
}

// The app's own texts for the codes it expects, nested by the dots of the code; a sandboxed app has no access to
// the desktop's texts.
const i18n: (app: PithyxApp) => I18n = (app) =>
  createI18n(app, {
    en: {
      errors: {
        app: {
          backend: {
            unavailable: 'The service is starting or not running. Try again in a moment.',
          },
          permission: { denied: 'An administrator has not allowed this for the app.' },
          storage: { too_large: 'Too much data to save (limit {maxKb} KB).' },
        },
        sdk: { backend: { unreachable: 'The box cannot be reached. Check the connection.' } },
        unknown: 'Something went wrong.',
      },
    },
  });

/** Logs a failure with its code and request id, and shows it: the desktop's dialog where it exists, else own text. */
export async function report(
  app: PithyxApp,
  error: unknown,
  show: (text: string) => void,
): Promise<void> {
  const code = codeOf(error);
  const requestId = error instanceof PithyxError ? (error.requestId ?? null) : null;
  app.log.error('Action failed', { code, requestId });
  if (app.supports('ui.showError')) {
    await app.ui.showError(error);
    return;
  }
  const t = i18n(app);
  const params = error instanceof PithyxError ? error.params : {};
  // t() returns the key itself for a code without a text.
  const text = t.t(`errors.${code}`, params);
  show(text === `errors.${code}` ? t.t('errors.unknown') : text);
}

export async function loadDashboard(app: PithyxApp, show: (text: string) => void) {
  try {
    const stats = await withRetry(app, () => backendJson<{ files: number }>(app, '/stats'));
    app.log.info('Dashboard loaded', { files: stats.files });
  } catch (error) {
    await report(app, error, show);
  }
}
