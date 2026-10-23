import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { lazy, Suspense, useEffect, useState, useSyncExternalStore } from 'react';
import { fetchSession, fetchSetupStatus, queryKeys } from './api/queries';
import { LoginScreen } from './auth/LoginScreen';
import { continueHost, dropContinue, ProxyContinue, proxyContinue } from './auth/ProxyContinue';
import { initialsOf, WelcomeCard } from './auth/parts';
import { clearSessionHint, readSessionHint, writeSessionHint } from './auth/sessionHint';
import { SetupScreen } from './auth/SetupScreen';
import { Unreachable } from './auth/Unreachable';
import { isSessionExpired } from './errors/describe';
import { useErrorStore } from './errors/errorStore';
import { loadDesktopTranslations, loadTranslations } from './i18n';
import { realtime, useRealtime } from './realtime/connection';
import { AuthLayout } from './shell/AuthLayout';
import { markSignedIn } from './wm/signIn';

type User = components['schemas']['UserInfo'];

// The desktop (with React Aria, the window manager and the texts of its apps) is its own chunk. The sign-in screen prefetches it, so after
// signing in only the WebSocket is left to wait for. It is rendered only once loaded instead of through lazy() and
// Suspense: React holds back content that resolves after suspending for up to 300 ms, which the welcome card would
// have to cover for nothing.
type DesktopModule = typeof import('./desktop/Desktop');
let desktopModule: DesktopModule | undefined;
const desktopListeners = new Set<() => void>();
let desktopLoading: Promise<void> | undefined;

function loadDesktop(): Promise<void> {
  desktopLoading ??= Promise.all([import('./desktop/Desktop'), loadDesktopTranslations()]).then(
    ([module]) => {
      desktopModule = module;
      for (const listener of desktopListeners) {
        listener();
      }
    },
  );
  return desktopLoading;
}

// A browser that was signed in before most likely still is: load the desktop right away, next to the session check.
const sessionHint = readSessionHint();
if (sessionHint !== undefined) {
  void loadDesktop();
}

/** The desktop module once loaded; asking for it starts loading. */
function useDesktopModule(): DesktopModule | undefined {
  return useSyncExternalStore(
    (listener) => {
      desktopListeners.add(listener);
      void loadDesktop();
      return () => {
        desktopListeners.delete(listener);
      };
    },
    () => desktopModule,
  );
}

// Only the live system of the ISO shows the installer; its code and texts stay out of the sign-in chunk.
const InstallerScreen = lazy(() =>
  Promise.all([
    import('./installer/InstallerScreen'),
    loadTranslations('installer', 'network'),
  ]).then(([{ InstallerScreen }]) => ({ default: InstallerScreen })),
);

const ErrorDialog = lazy(() =>
  import('./errors/ErrorDialog').then(({ ErrorDialog }) => ({ default: ErrorDialog })),
);

/** Starts loading the desktop chunk once the sign-in screen is on screen and the browser is idle. */
function PrefetchDesktop() {
  useEffect(() => {
    const start = () => void loadDesktop();
    if ('requestIdleCallback' in window) {
      const handle = requestIdleCallback(start, { timeout: 2000 });
      return () => {
        cancelIdleCallback(handle);
      };
    }
    const timer = setTimeout(start, 200);
    return () => {
      clearTimeout(timer);
    };
  }, []);
  return null;
}

/** The desktop as soon as its module is there; until then the welcome card (after sign-in) or nothing (reload). */
function SignedIn({ user, entering }: { user: User; entering: boolean }) {
  const queryClient = useQueryClient();
  const module = useDesktopModule();
  // The WebSocket lives as long as the user is signed in and opens while the desktop chunk still loads. Opening it
  // here instead of in the desktop also spares the desktop a second render for the status change.
  useEffect(() => {
    realtime.connect(() => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.session });
    });
    return () => {
      realtime.disconnect();
    };
  }, [queryClient]);
  return module === undefined ? null : <module.Desktop user={user} entering={entering} />;
}

/** 'welcome' from sign-in until the desktop is ready, 'done' once the card has left. */
type Intro = 'none' | 'welcome' | 'done';

/** First run shows the setup, then the login, then the desktop. There is no router yet. */
export function App() {
  const queryClient = useQueryClient();
  // Both requests start together (index.html preloads them while the scripts load): a returning user with a
  // session needs neither the setup state nor a second round trip before the desktop.
  const setup = useQuery({
    queryKey: queryKeys.setup,
    queryFn: ({ signal }) => fetchSetupStatus(signal),
  });
  const session = useQuery<User>({
    queryKey: queryKeys.session,
    queryFn: ({ signal }) => fetchSession(signal),
    retry: false,
    // Read on every (re)start of the query: signing out clears the hint before the session query resets.
    placeholderData: () => readSessionHint(),
  });
  const realtime = useRealtime((state) => state.status);
  const hasError = useErrorStore((state) => state.current !== null);
  const [intro, setIntro] = useState<Intro>('none');
  // A proxy host behind the Pithyx sign-in sent the browser here; after signing in it goes back there.
  const [continuing, setContinuing] = useState(proxyContinue);

  function signedIn(user: User) {
    if (setup.data?.required === true) {
      queryClient.setQueryData(queryKeys.setup, { ...setup.data, required: false });
    }
    queryClient.setQueryData(queryKeys.session, user);
    markSignedIn();
    setIntro('welcome');
  }

  // The desktop is ready when its live connection is up, or has failed and the desktop shows why. The welcome card
  // bridges exactly that wait and never adds its own delay.
  const leaving = intro === 'welcome' && realtime !== 'idle' && realtime !== 'connecting';
  // A failed refetch keeps the previous data; an ended session must still lead to the login screen.
  const sessionEnded =
    isSessionExpired(session.error) || (session.isError && session.data === undefined);
  const confirmedUser = session.isSuccess && !session.isPlaceholderData ? session.data : undefined;

  // Keeps the hint for the next reload in step with what the daemon said.
  useEffect(() => {
    if (confirmedUser !== undefined) {
      writeSessionHint(confirmedUser);
    } else if (sessionEnded) {
      clearSessionHint();
    }
  }, [confirmedUser, sessionEnded]);

  let screen;
  if (setup.data?.installer === true) {
    screen = (
      <Suspense fallback={null}>
        <InstallerScreen />
      </Suspense>
    );
  } else if (continuing !== null && session.data !== undefined && !sessionEnded) {
    screen =
      confirmedUser === undefined ? null : (
        <ProxyContinue
          url={continuing}
          onDesktop={() => {
            dropContinue();
            setContinuing(null);
          }}
        />
      );
  } else if (session.data !== undefined && !sessionEnded) {
    const name = session.data.displayName ?? session.data.username;
    screen = (
      <>
        <SignedIn user={session.data} entering={leaving || intro === 'done'} />
        {intro === 'welcome' && (
          <AuthLayout
            entrance={false}
            leaving={leaving}
            onLeft={() => {
              setIntro('done');
            }}
          >
            <WelcomeCard name={name} initials={initialsOf(name)} />
          </AuthLayout>
        )}
      </>
    );
  } else if (setup.isError) {
    screen = (
      <Unreachable
        retrying={setup.isFetching}
        onRetry={() => {
          void setup.refetch();
          void session.refetch();
        }}
      />
    );
  } else if (setup.data === undefined || (!setup.data.required && !sessionEnded)) {
    // Nothing is shown while loading: no spinner before 300 ms, and these requests are local and fast.
    screen = null;
  } else {
    screen = (
      <>
        {setup.data.required ? (
          <SetupScreen hostname={setup.data.hostname} onSignedIn={signedIn} />
        ) : (
          <LoginScreen
            hostname={setup.data.hostname}
            continueTo={continuing === null ? undefined : continueHost(continuing)}
            onSignedIn={signedIn}
          />
        )}
        <PrefetchDesktop />
      </>
    );
  }

  return (
    <>
      {screen}
      {hasError && (
        <Suspense fallback={null}>
          <ErrorDialog />
        </Suspense>
      )}
    </>
  );
}
