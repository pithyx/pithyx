import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import i18next from 'i18next';
import { useEffect, useState, type ComponentType, type CSSProperties, type ReactNode } from 'react';
import coreStyles from '../apps/sdk/CoreSdkApp.module.css';
import { currentTheme } from '../apps/sandbox/theme';
import type { SdkAppProps } from '../apps/sdk/CoreSdkApp';
import { DesktopShell } from '../shell/DesktopShell';
import { Taskbar } from '../shell/Taskbar';
import { Window } from '../shell/Window';
import type { SceneContext } from './DesktopScreens';
import { now, useApps, type AppId } from './sampleData';
import { sampleApp, type ExtraAnswers, type SampleAppOptions } from './storageSample';

/**
 * One user action the gallery performs after the app rendered, so a screen can show a dialog in a later state. Texts
 * are i18n keys (resolved in the screen's language) or, for user data such as disk names, the text itself.
 */
export type ScriptStep = (
  | { press: string; inside?: string }
  | { check: string }
  | { fill: string; value: string; inside?: string }
  | { select: string; value: string }
  | { upload: string }
  | { show: string }
) & { params?: Record<string, string> };

// Keys come from the screen list, so they are not checked against the typed resources.
const translate = i18next.t as unknown as (key: string, options?: object) => string;
const text = (key: string, params?: Record<string, string>) =>
  key.includes(':') ? translate(key, params) : key;

// Where to look, first match wins: the topmost open dialog or menu (overlays are added at the end of the page), or the
// list items, rows, cards and sections that contain `inside` (a text or a translation key), innermost first, then the
// page.
function scopes(inside?: string): ParentNode[] {
  const overlay = [...document.querySelectorAll('[role="dialog"], [role="menu"]')].at(-1);
  if (inside === undefined) {
    return [overlay ?? document];
  }
  const holders = [...document.querySelectorAll('li, tr, article, section')].filter((element) =>
    element.textContent.includes(text(inside)),
  );
  return [...holders.reverse(), document];
}

// Buttons and tabs by the start of their text, icon buttons by their name; rows and options, which start with a date
// or a time, anywhere in it.
function byText(root: ParentNode, selector: string, wanted: string): HTMLElement | undefined {
  return [...root.querySelectorAll<HTMLElement>(selector)].find((element) => {
    const content = element.textContent.trim() || (element.getAttribute('aria-label') ?? '');
    return element.matches('[role="row"], [role="option"]')
      ? content.includes(wanted)
      : content.startsWith(wanted);
  });
}

// The control a visible label names, for React Aria fields (label for=) and native ones (label around the input), in
// the first of `roots` that has the label.
function control(
  label: string,
  roots: ParentNode[] = [document],
): HTMLInputElement | HTMLSelectElement | undefined {
  const element = roots
    .map((root) => byText(root, 'label', label) as HTMLLabelElement | undefined)
    .find((found) => found !== undefined);
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

// A row or option is pressed with the mouse, as a user would: React Aria takes a plain click() for a screen reader's,
// which runs the row's action (opening it) instead of selecting it.
function mousePress(element: HTMLElement) {
  const options = {
    bubbles: true,
    cancelable: true,
    button: 0,
    pointerId: 1,
    pointerType: 'mouse',
  };
  element.dispatchEvent(new PointerEvent('pointerdown', { ...options, buttons: 1 }));
  element.dispatchEvent(new MouseEvent('mousedown', { ...options, buttons: 1 }));
  element.dispatchEvent(new PointerEvent('pointerup', options));
  element.dispatchEvent(new MouseEvent('mouseup', options));
  element.dispatchEvent(new MouseEvent('click', { ...options, detail: 1 }));
}

function attempt(step: ScriptStep): boolean {
  if ('press' in step) {
    const wanted = text(step.press, step.params);
    // A button, radio, tab, table row or menu item by its text, else a native radio button or checkbox by its label.
    const button =
      scopes(step.inside)
        .map((root) =>
          byText(
            root,
            'button, [role="radio"], [role="tab"], [role="row"], [role="option"], [role="menuitem"]',
            wanted,
          ),
        )
        .find((found) => found !== undefined) ?? control(wanted);
    if (
      button === undefined ||
      button.getAttribute('aria-disabled') === 'true' ||
      (button as HTMLButtonElement).disabled
    ) {
      return false;
    }
    if (button.matches('[role="row"], [role="option"]')) {
      mousePress(button);
    } else {
      button.click();
    }
    return true;
  }
  if ('show' in step) {
    // A heading scrolled to the top of its scrolling area, for results further down a page.
    const heading = byText(document, 'h1, h2, h3, h4', text(step.show, step.params));
    heading?.scrollIntoView({ block: 'start' });
    return heading !== undefined;
  }
  if ('upload' in step) {
    // The first file input, with an empty file of that name, as if the user had picked it.
    const input = document.querySelector<HTMLInputElement>('input[type="file"]');
    if (input === null) {
      return false;
    }
    const files = new DataTransfer();
    files.items.add(new File([], step.upload));
    input.files = files.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
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
  const field =
    'fill' in step
      ? control(
          text(step.fill, step.params),
          step.inside === undefined ? undefined : scopes(step.inside),
        )
      : control(text(step.select, step.params));
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

/** Marks the page busy while `steps` run, so the checks and the screenshots wait for them. */
export function Scripted({
  steps,
  children,
}: {
  steps: readonly ScriptStep[];
  children: ReactNode;
}) {
  const busy = useScript(steps);
  return (
    <div data-design-busy={busy ? '' : undefined} style={{ display: 'contents' }}>
      {children}
    </div>
  );
}

/**
 * A real core app running on sample data, with its own query cache: `launch` is passed as its launch arguments,
 * `options` adds files, events and a saved session, and `seed` fills the cache for apps that read the daemon
 * directly instead of through the SDK.
 */
export function SampleAppHost({
  Component,
  launch = null,
  answers = {},
  options = {},
  seed,
}: {
  Component: ComponentType<SdkAppProps>;
  launch?: unknown;
  answers?: ExtraAnswers;
  options?: SampleAppOptions;
  seed?: (client: QueryClient) => void;
}) {
  const [client] = useState(() => {
    const created = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    });
    seed?.(created);
    return created;
  });
  const [app] = useState(() => sampleApp(launch, answers, options));
  // The SDK puts the public tokens (--pithyx-*) on the app's root; the gallery does the same without a connection.
  return (
    <div className={coreStyles.root} style={currentTheme().tokens as CSSProperties}>
      <QueryClientProvider client={client}>
        <Component app={app} />
      </QueryClientProvider>
    </div>
  );
}

/** A real core app, maximized on the desktop, on sample data; `script` then opens the dialog the screen shows. */
export function CoreAppScene({
  context,
  app: appId,
  title,
  Component,
  launch = null,
  script = [],
  answers = {},
  options = {},
  seed,
}: {
  context: SceneContext;
  app: AppId;
  title?: string;
  Component: ComponentType<SdkAppProps>;
  launch?: unknown;
  script?: readonly ScriptStep[];
  answers?: ExtraAnswers;
  options?: SampleAppOptions;
  seed?: (client: QueryClient) => void;
}) {
  const apps = useApps();
  const info = apps[appId];
  return (
    <Scripted steps={script}>
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
          <SampleAppHost
            Component={Component}
            launch={launch}
            answers={answers}
            options={options}
            {...(seed === undefined ? {} : { seed })}
          />
        </Window>
      </DesktopShell>
    </Scripted>
  );
}
