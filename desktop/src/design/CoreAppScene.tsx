import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import i18next from 'i18next';
import { useEffect, useState, type ComponentType } from 'react';
import type { SdkAppProps } from '../apps/sdk/CoreSdkApp';
import { DesktopShell } from '../shell/DesktopShell';
import { Taskbar } from '../shell/Taskbar';
import { Window } from '../shell/Window';
import type { SceneContext } from './DesktopScreens';
import { now, useApps, type AppId } from './sampleData';
import { sampleApp, type ExtraAnswers } from './storageSample';

/**
 * One user action the gallery performs after the app rendered, so a screen can show a dialog in a later state. Texts
 * are i18n keys (resolved in the screen's language) or, for user data such as disk names, the text itself.
 */
export type ScriptStep = (
  | { press: string; inside?: string }
  | { check: string }
  | { fill: string; value: string }
  | { select: string; value: string }
) & { params?: Record<string, string> };

// Keys come from the screen list, so they are not checked against the typed resources.
const translate = i18next.t as unknown as (key: string, options?: object) => string;
const text = (key: string, params?: Record<string, string>) =>
  key.includes(':') ? translate(key, params) : key;

// The open dialog, else the innermost list item, row or card that contains `inside`, else the page.
function scope(inside?: string): ParentNode {
  const dialog = document.querySelector('[role="dialog"]');
  if (inside === undefined) {
    return dialog ?? document;
  }
  const holders = [...document.querySelectorAll('li, tr, article, section')].filter((element) =>
    element.textContent.includes(inside),
  );
  return holders.at(-1) ?? document;
}

function byText(root: ParentNode, selector: string, wanted: string): HTMLElement | undefined {
  return [...root.querySelectorAll<HTMLElement>(selector)].find((element) =>
    element.textContent.trim().startsWith(wanted),
  );
}

// The control a visible label names, for React Aria fields (label for=) and native ones (label around the input).
function control(label: string): HTMLInputElement | HTMLSelectElement | undefined {
  const element = byText(document, 'label', label) as HTMLLabelElement | undefined;
  if (element === undefined) {
    return undefined;
  }
  const target = element.htmlFor === '' ? null : document.getElementById(element.htmlFor);
  return (target ?? element.querySelector('input, select') ?? undefined) as
    HTMLInputElement | HTMLSelectElement | undefined;
}

function setValue(element: HTMLInputElement | HTMLSelectElement, value: string) {
  const prototype =
    element instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(prototype, 'value')?.set?.call(element, value);
  element.dispatchEvent(
    new Event(element instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }),
  );
}

function attempt(step: ScriptStep): boolean {
  if ('press' in step) {
    const button = byText(
      scope(step.inside),
      'button, [role="radio"]',
      text(step.press, step.params),
    );
    if (
      button === undefined ||
      button.getAttribute('aria-disabled') === 'true' ||
      (button as HTMLButtonElement).disabled
    ) {
      return false;
    }
    button.click();
    return true;
  }
  if ('check' in step) {
    const input = control(text(step.check, step.params));
    if (!(input instanceof HTMLInputElement)) {
      return false;
    }
    if (!input.checked) {
      input.click();
    }
    return true;
  }
  const field = control(text('fill' in step ? step.fill : step.select, step.params));
  if (field === undefined) {
    return false;
  }
  setValue(field, step.value);
  return true;
}

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Runs the steps one after the other, each as soon as its control exists, and marks the page ready at the end. */
function useScript(steps: readonly ScriptStep[]) {
  const [busy, setBusy] = useState(steps.length > 0);
  useEffect(() => {
    const run = { stopped: false };
    void (async () => {
      for (const step of steps) {
        for (let tries = 0; tries < 60 && !run.stopped && !attempt(step); tries++) {
          await pause(50);
        }
        await pause(60);
      }
      if (!run.stopped) {
        setBusy(false);
      }
    })();
    return () => {
      run.stopped = true;
    };
  }, [steps]);
  return busy;
}

/**
 * A real core app, maximized on the desktop, running on sample data. `launch` is passed as its launch arguments,
 * `script` then opens the dialog the screen shows.
 */
export function CoreAppScene({
  context,
  app: appId,
  title,
  Component,
  launch = null,
  script = [],
  answers = {},
}: {
  context: SceneContext;
  app: AppId;
  title?: string;
  Component: ComponentType<SdkAppProps>;
  launch?: unknown;
  script?: readonly ScriptStep[];
  answers?: ExtraAnswers;
}) {
  const apps = useApps();
  const [client] = useState(
    () => new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } }),
  );
  const [app] = useState(() => sampleApp(launch, answers));
  const busy = useScript(script);
  const info = apps[appId];
  return (
    <div data-design-busy={busy ? '' : undefined} style={{ display: 'contents' }}>
      <DesktopShell
        taskbarPosition={context.taskbar}
        taskbar={
          <Taskbar
            position={context.taskbar}
            apps={[{ ...info, state: 'focused' }]}
            startOpen={false}
            jobs={0}
            unread={0}
            notificationsOpen={false}
            switcherOpen={false}
            now={now}
            locale={context.locale}
          />
        }
      >
        <Window
          app={info}
          title={title ?? info.name}
          focused
          maximized
          compactActive
          layer={1}
          geometry={{ x: 0, y: 0, width: 100, height: 100 }}
        >
          <QueryClientProvider client={client}>
            <Component app={app} />
          </QueryClientProvider>
        </Window>
      </DesktopShell>
    </div>
  );
}
