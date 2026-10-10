---
title: 'Logging und Fehler'
description: 'Mit app.log ins Protokoll-Center schreiben, PithyxError-Codes behandeln, wiederholen, was vorübergehen kann, und Fehler in der Sprache des Benutzers zeigen.'
status: ready
audience: developer
sidebar:
  order: 6
---

`app.log` schreibt das Log der App ins Protokoll-Center, wo Administratoren es neben den Logs des Systems sehen, und in die Browser-Konsole. Jeder gescheiterte SDK-Aufruf weist mit einem `PithyxError` ab, der einen stabilen Code (`bereich.objekt.grund`), seine `params`, ein englisches `detail` und, wenn der Daemon beteiligt war, eine `requestId` trägt. Entscheide nach dem Code, nie nach dem Text: Wiederhole, was von selbst vorübergehen kann, und zeige den Rest in der Sprache des Benutzers.

## Beispiel

<!-- example: sdk/logging-and-errors.ts -->

```ts
// Logging to the Log Center, telling retryable failures from final ones, and showing errors in the user's language.
import { isProblem, PithyxError, type PithyxApp } from '@pithyx/sdk';
import { createI18n, type I18n } from '@pithyx/sdk/i18n';

/** Failures that may pass by themselves: the desktop or the box was busy, restarting or starting the backend. */
const RETRYABLE = new Set([
  'sdk.request.timeout',
  'sdk.backend.unreachable',
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
```

## Logging

- `app.log.debug`, `info`, `warn`, `error` und `log` (dasselbe wie `info`) nehmen eine Nachricht und optional `data`, ein flaches Objekt aus Texten, Zahlen, Wahrheitswerten und `null`. Sie kehren sofort zurück und werfen nie.
- Jeder Eintrag erscheint in der Browser-Konsole auf seiner Stufe. `info`, `warn` und `error` gehen auch an den Daemon; `debug` nur, solange ein Administrator das Debug-Logging der App eingeschaltet hat (`context.logLevel` ist `debug`, `logLevelChange` folgt ihm).
- Im Protokoll-Center erscheinen die Einträge unter der Quelle `app/<id>` mit dem angemeldeten Benutzer. Die Ausgabe der Container des Backends landet in derselben Quelle.
- Das SDK sammelt Einträge 250 ms lang und sendet bis zu 100 auf einmal.

**Grenzen:** Eine Nachricht wird bei 4000 Zeichen abgeschnitten; `data` behält 20 Felder mit Schlüsseln bis 100 und Werten bis 1000 Zeichen, alle als Text. Das SDK puffert höchstens 500 Einträge und verwirft neuere, solange der Puffer voll ist; der Daemon nimmt Schübe von 200 und dann 20 pro Sekunde pro App an, für alle ihre Benutzer zusammen, verwirft den Rest und meldet die verworfene Anzahl einmal pro Minute als eine Warnung. Logge nie Passwörter, Tokens oder Dateiinhalte.

## Fehler

`PithyxError` hat `code`, `params`, `detail` und `requestId` sowie `toProblem()` für das einfache Objekt. `isProblem(value)` prüft einen beliebigen Wert auf diese Form. Codes kommen aus zwei Quellen:

- **Vom Daemon**, unverändert: zum Beispiel `app.permission.denied`, `app.storage.too_large`, `fs.entry.exists`, `app.token.invalid`. Die vollständige Liste mit Texten ist die [Fehlerreferenz](/de/docs/reference/errors/).
- **Vom SDK** für Fehler zwischen App und Desktop: [`sdk.method.unknown`](/de/docs/reference/errors/#sdk.method.unknown), [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid), [`sdk.request.timeout`](/de/docs/reference/errors/#sdk.request.timeout), [`sdk.transport.closed`](/de/docs/reference/errors/#sdk.transport.closed), [`sdk.protocol.unsupported`](/de/docs/reference/errors/#sdk.protocol.unsupported), [`sdk.transfer.cancelled`](/de/docs/reference/errors/#sdk.transfer.cancelled), [`sdk.backend.endpoint_unknown`](/de/docs/reference/errors/#sdk.backend.endpoint_unknown) und [`sdk.backend.unreachable`](/de/docs/reference/errors/#sdk.backend.unreachable). Die Liste wird als `sdkErrorCodes` exportiert.

`app.backend.fetch` erfüllt sich bei jedem HTTP-Status. Die eigenen Fehler des Proxys (401, 503) tragen einen Problem-Body mit Code; mach daraus einen `PithyxError` wie im Beispiel. Was dein Backend zurückgibt, entscheidest du.

## Wiederholen oder nicht

| Code                                                                            | Bedeutung                                                                                          | Wiederholen                                        |
| ------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- | -------------------------------------------------- |
| [`sdk.request.timeout`](/de/docs/reference/errors/#sdk.request.timeout)         | der Desktop hat nicht innerhalb von 15 Sekunden geantwortet (nicht bei Übertragungen und Dialogen) | ein paar Mal, mit wachsender Pause                 |
| [`sdk.backend.unreachable`](/de/docs/reference/errors/#sdk.backend.unreachable) | die Anfrage hat die Box nicht erreicht                                                             | nach `connectionChange` mit `online: true`         |
| [`app.backend.unavailable`](/de/docs/reference/errors/#app.backend.unavailable) | das Backend wird aktualisiert, ist gescheitert oder kam nicht in 30 s hoch                         | wenn `backendStatus` `running` meldet, oder später |
| [`auth.rate.limited`](/de/docs/reference/errors/#auth.rate.limited)             | zu viele Tokens auf einmal                                                                         | nach `params.retryAfterSeconds`                    |
| [`app.token.invalid`](/de/docs/reference/errors/#app.token.invalid)             | Sitzung beendet oder Zugriff auf die App weg (das SDK hat schon einmal ein neues Token versucht)   | nein; der Benutzer öffnet das Fenster neu          |
| [`app.permission.denied`](/de/docs/reference/errors/#app.permission.denied)     | der App fehlt `params.permission`                                                                  | nein; warte auf `permissionsChange`                |
| [`sdk.transport.closed`](/de/docs/reference/errors/#sdk.transport.closed)       | kein Desktop verbunden, oder die Verbindung zu ihm ist weg                                         | nein                                               |
| [`sdk.method.unknown`](/de/docs/reference/errors/#sdk.method.unknown)           | der Desktop bedient diesen Aufruf nicht                                                            | nein; prüfe vorher `app.supports`                  |
| [`sdk.request.invalid`](/de/docs/reference/errors/#sdk.request.invalid)         | die Parameter sind falsch, etwa ein Schlüssel über 128 Zeichen                                     | nein; ein Fehler in der App                        |
| `fs.*`, `app.storage.*`                                                         | die Anfrage selbst wird abgelehnt                                                                  | nein; sag es dem Benutzer                          |

Ein Aufruf mit Zeitüberschreitung kann auf der Box trotzdem passiert sein. Wiederhole nur Aufrufe, die sich gefahrlos wiederholen lassen, etwa Lesen und `upload` mit `overwrite`.

## Fehler zeigen

Die App übersetzt die Codes, die sie erwartet, mit eigenen Texten, nutzt `params` für die Platzhalter und fällt auf eine allgemeine Meldung zurück. `detail` ist Englisch und für Logs gedacht. Logge die `requestId`, statt sie zu zeigen: Mit ihr findet ein Administrator die Anfrage im Protokoll-Center.

`app.ui.showError(error)` zeigt den eigenen Fehlerdialog des Desktops mit dem übersetzten Text. Prüfe `app.supports('ui.showError')` wie im Beispiel: Ein älterer Desktop bedient ihn vielleicht nicht und antwortet mit [`sdk.method.unknown`](/de/docs/reference/errors/#sdk.method.unknown). Der Mock-Desktop von `pithyx dev` bedient ihn und schreibt den Code in sein Log.

## Sonderfälle

- `app.log` geht nicht, bevor `connect()` sich erfüllt hat: Es gibt noch kein `app`. Nutze bis dahin `console`.
- Einträge erreichen den Daemon nur, wenn der Desktop `log.write` bedient; sonst bleiben sie in der Konsole.
- `connect()` selbst weist mit `sdk.transport.closed` ab, wenn sich innerhalb von 10 Sekunden kein Desktop verbindet, etwa wenn die Seite direkt in einem Tab geöffnet wird, und mit `sdk.protocol.unsupported`, wenn der Desktop das Protokoll des SDK nicht spricht.
