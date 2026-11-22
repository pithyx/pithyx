import { coreAppIds } from '../ids';
import type { AppProps } from '../registry';
import { CoreSdkApp } from '../sdk/CoreSdkApp';
import { backupsHost } from '../sdk/backups';
import { BackupApp } from './BackupApp';

/** The Backup app in a window: a core app on the SDK with the internal administrator calls (D414). Administrators only. */
export function BackupWindow({ windowId }: AppProps) {
  return (
    <CoreSdkApp
      windowId={windowId}
      app={coreAppIds.backup}
      permissions={[]}
      internal={() => ({ extensions: backupsHost(), close: () => undefined })}
      Component={BackupApp}
    />
  );
}
