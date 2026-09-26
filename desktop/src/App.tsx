import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { components } from '@vyxis/api-client';
import { lazy, Suspense, useState } from 'react';
import { fetchSession, fetchSetupStatus, queryKeys } from './api/queries';
import { LoginScreen } from './auth/LoginScreen';
import { initialsOf, WelcomeCard } from './auth/parts';
import { SetupScreen } from './auth/SetupScreen';
import { Unreachable } from './auth/Unreachable';
import { isSessionExpired } from './errors/describe';
import { ErrorDialog } from './errors/ErrorDialog';
import { useRealtime } from './realtime/connection';
import { AuthLayout } from './shell/AuthLayout';

type User = components['schemas']['UserInfo'];

// The desktop is loaded only after sign-in; the welcome card covers the time it takes.
const Desktop = lazy(() =>
  import('./desktop/Desktop').then(({ Desktop }) => ({ default: Desktop })),
);

/** 'welcome' from sign-in until the desktop is ready, 'done' once the card has left. */
type Intro = 'none' | 'welcome' | 'done';

/** First run shows the setup, then the login, then the desktop. There is no router yet. */
export function App() {
  const queryClient = useQueryClient();
  const setup = useQuery({
    queryKey: queryKeys.setup,
    queryFn: ({ signal }) => fetchSetupStatus(signal),
  });
  const session = useQuery({
    queryKey: queryKeys.session,
    queryFn: ({ signal }) => fetchSession(signal),
    enabled: setup.data?.required === false,
    retry: false,
  });
  const realtime = useRealtime((state) => state.status);
  const [intro, setIntro] = useState<Intro>('none');

  function signedIn(user: User) {
    if (setup.data?.required === true) {
      queryClient.setQueryData(queryKeys.setup, { ...setup.data, required: false });
    }
    queryClient.setQueryData(queryKeys.session, user);
    setIntro('welcome');
  }

  // Loading ends when the live connection is up, or has failed and the desktop shows why. No artificial delay.
  const leaving = intro === 'welcome' && realtime !== 'idle' && realtime !== 'connecting';

  let screen;
  if (setup.isError) {
    screen = (
      <Unreachable
        retrying={setup.isFetching}
        onRetry={() => {
          void setup.refetch();
        }}
      />
    );
  } else if (setup.data === undefined) {
    // Nothing is shown while loading: no spinner before 300 ms, and these requests are local and fast.
    screen = null;
  } else if (setup.data.required) {
    screen = <SetupScreen hostname={setup.data.hostname} onSignedIn={signedIn} />;
  } else if (isSessionExpired(session.error) || (session.isError && session.data === undefined)) {
    // A failed refetch keeps the previous data; an ended session must still lead to the login screen.
    screen = <LoginScreen hostname={setup.data.hostname} onSignedIn={signedIn} />;
  } else if (session.data !== undefined) {
    const name = session.data.displayName ?? session.data.username;
    screen = (
      <>
        <Suspense fallback={null}>
          <Desktop user={session.data} entering={leaving || intro === 'done'} />
        </Suspense>
        {intro === 'welcome' && (
          <AuthLayout
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
  } else {
    screen = null;
  }

  return (
    <>
      {screen}
      <ErrorDialog />
    </>
  );
}
