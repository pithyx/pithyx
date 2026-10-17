---
title: 'Berechtigungen und permissionsChange'
description: 'Berechtigungen deklarieren, context.permissions prüfen, app.permission.denied behandeln und permissionsChange folgen, wenn ein Administrator sie ändert.'
status: ready
audience: developer
sidebar:
  order: 4
---

Eine App bekommt genau die Berechtigungen, die sie in `pithyx.json` deklariert und ein Administrator bestätigt. Der Administrator kann jede später entziehen und wieder erteilen; das Fenster bekommt dann `permissionsChange` mit der vollständigen neuen Liste. Eine API zu benutzen erteilt nichts: Ein Aufruf ohne die Berechtigung scheitert mit `app.permission.denied`. Baue die Oberfläche aus `app.context.permissions` und halte sie mit dem Ereignis aktuell.

## Die Berechtigungen

| Berechtigung       | Erlaubt der App                                                                                                                 |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------- |
| `files.read`       | Dateien in freigegebenen Ordnern (mit `files.shares`) und Dateien, die der Benutzer ihr gibt, lesen                             |
| `files.write`      | diese Dateien schreiben                                                                                                         |
| `files.shares`     | die freigegebenen Ordner des Benutzers listen und mit seinen Rechten durchsuchen                                                |
| `notifications`    | Benachrichtigungen mit `app.notify` zeigen                                                                                      |
| `directory.read`   | alle Benutzer und Gruppen mit `app.directory.users()` und `groups()` listen                                                     |
| `network.internet` | Verbindungen ins Internet aus dem Fenster (`connect-src https:`) und aus dem Backend öffnen                                     |
| `network.lan`      | Verbindungen aus dem Backend zu anderen Geräten im lokalen Netzwerk öffnen; das Fenster und die Box selbst bleiben unerreichbar |

Alles andere braucht keine Berechtigung: der Kontext, Fensteraufrufe, `app.storage`, `app.session`, Logs, das Backend und Speicheranfragen, die der Administrator zugeordnet hat. Welche Benutzer die App überhaupt öffnen dürfen, ist eine eigene Freigabe pro Benutzer und Gruppe, keine Berechtigung der App.

Wer `directory.read` verlangt, bekommt im Installationsdialog den Vorschlag "nur Administratoren", wie bei root-Containern und Sonderrechten; verlange es nur, wenn die App wirklich die Namen aller braucht.

## Beispiel

Das Manifest deklariert `"permissions": ["notifications", "directory.read"]`. Das Board funktioniert ohne beide und bietet mit ihnen mehr:

<!-- example: sdk/permissions.ts -->

```ts
// A team board that offers what its permissions allow and follows when an administrator changes them.
// pithyx.json declares: "permissions": ["notifications", "directory.read"]
import { PithyxError, type DirectoryUser, type PithyxApp } from '@pithyx/sdk';

interface Features {
  notify: boolean;
  pickPeople: boolean;
}

/** What the app may do now; context.permissions is always the current, complete list. */
export function features(app: PithyxApp): Features {
  const has = (permission: string) => app.context.permissions.includes(permission);
  return { notify: has('notifications'), pickPeople: has('directory.read') };
}

/** Everyone to assign a card to, or only the signed-in user without `directory.read`. */
export async function people(
  app: PithyxApp,
): Promise<Pick<DirectoryUser, 'id' | 'name' | 'displayName'>[]> {
  const { user } = app.context;
  if (!features(app).pickPeople) {
    return [{ id: user.id, name: user.name, displayName: user.displayName }];
  }
  try {
    return await app.directory.users();
  } catch (error) {
    // Revoked between the check and the call: the SDK or the daemon refuses with the missing permission.
    if (error instanceof PithyxError && error.code === 'app.permission.denied') {
      return [{ id: user.id, name: user.name, displayName: user.displayName }];
    }
    throw error;
  }
}

export async function remind(app: PithyxApp, title: string): Promise<boolean> {
  if (!features(app).notify) {
    return false;
  }
  try {
    await app.notify({ title, level: 'info', tag: 'board-reminder' });
    return true;
  } catch (error) {
    if (error instanceof PithyxError && error.code === 'app.permission.denied') {
      return false;
    }
    throw error;
  }
}

/** Renders once and again after every change; returns the function that stops following. */
export function follow(app: PithyxApp, render: (features: Features) => void): () => void {
  render(features(app));
  return app.on('permissionsChange', () => {
    // The SDK has replaced context.permissions and dropped its backend token before handlers run.
    render(features(app));
  });
}
```

## Wie Änderungen ankommen

1. Bei der Installation listet der Dialog jede deklarierte Berechtigung, und der Administrator bestätigt sie. Ein Update, das eine neue Berechtigung deklariert, wartet, bis ein Administrator sie bestätigt; eine entzogene Berechtigung bleibt über Updates hinweg entzogen.
2. Erteilt oder entzieht ein Administrator eine, bekommt jedes offene Fenster der App `permissionsChange` mit der ganzen Liste. Bevor deine Handler laufen, ersetzt das SDK `context.permissions` und verwirft sein Backend-Token, sodass der nächste Backend-Aufruf ein frisches bekommt.
3. Ein Aufruf, der eine entzogene Berechtigung braucht, scheitert mit [`app.permission.denied`](/de/docs/reference/errors/#app.permission.denied); `params.permission` nennt sie. Das SDK lehnt `directory.*` sofort ab, der Daemon prüft jeden Aufruf erneut.
4. Verliert der Benutzer den Zugriff auf die App selbst (die Freigabe wird entzogen oder die App deaktiviert oder entfernt), schließen sich ihre Fenster, und die Sockets dieses Benutzers zum Backend werden geschlossen.

## Sonderfälle

- **Wettlauf.** Eine Berechtigung kann zwischen deiner Prüfung und deinem Aufruf verschwinden. Behandle `app.permission.denied` bei jedem Aufruf, der eine braucht, wie das Beispiel.
- **`network.internet`** steckt in der Content-Security-Policy des Fensters, die der Browser beim Laden der Seite festlegt. Eine Änderung gilt für das Fenster nach einem Neuladen. Ein Backend bekommt oder verliert sein Internet-Netzwerk mit einem Neustart. **`network.lan`** wirkt für das Backend genauso.
- **Administratoren** haben jede App, aber eine App hält trotzdem nur die Berechtigungen, die für sie bestätigt sind.
- **Freigegebene Ordner** brauchen `files.shares` für die Liste und `files.read` oder `files.write` für den Inhalt; der eigene Zugriff des Benutzers auf jeden freigegebenen Ordner gilt zusätzlich. Siehe [Dateien und App-Speicher](/de/docs/sdk/guides/files-and-storage/).
- **Mock-Desktop.** Das Panel von `pithyx dev` listet die Berechtigungen des Manifests als Schalter; jeder Schalter sendet `permissionsChange`. Der Mock lehnt `notifications`, `directory.*` und seine Dateiaufrufe ohne die Berechtigung ab, wie die Box.

## Fehlercodes

[`app.permission.denied`](/de/docs/reference/errors/#app.permission.denied), [`app.token.invalid`](/de/docs/reference/errors/#app.token.invalid), [`app.app.not_found`](/de/docs/reference/errors/#app.app.not_found). Ein Administrator, der eine Berechtigung erteilt, die das Manifest nicht deklariert, bekommt [`app.permission.not_requested`](/de/docs/reference/errors/#app.permission.not_requested); die App sieht das nie.
