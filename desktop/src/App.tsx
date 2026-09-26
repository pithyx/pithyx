import { useQuery } from '@tanstack/react-query';
import { fetchSession, fetchSetupStatus, queryKeys } from './api/queries';
import { LoginScreen } from './auth/LoginScreen';
import { SetupScreen } from './auth/SetupScreen';
import { DaemonStatus } from './DaemonStatus';
import { Desktop } from './desktop/Desktop';
import { isSessionExpired } from './errors/describe';
import { ErrorDialog } from './errors/ErrorDialog';

/** First run shows the setup, then the login, then the desktop. There is no router yet. */
export function App() {
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

  let screen;
  if (setup.isError) {
    screen = (
      <main className="screen">
        <DaemonStatus />
      </main>
    );
  } else if (setup.data === undefined) {
    // Nothing is shown while loading: no spinner before 300 ms, and these requests are local and fast.
    screen = null;
  } else if (setup.data.required) {
    screen = <SetupScreen />;
  } else if (isSessionExpired(session.error) || (session.isError && session.data === undefined)) {
    // A failed refetch keeps the previous data; an ended session must still lead to the login screen.
    screen = <LoginScreen />;
  } else if (session.data !== undefined) {
    screen = <Desktop user={session.data} />;
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
