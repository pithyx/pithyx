import { FilesApp } from '../apps/files/FilesApp';
import { LogsApp } from '../apps/logs/LogsApp';
import { SettingsApp } from '../apps/settings/SettingsApp';
import { filesSample, logsAnswers, sampleFolder } from './appSamples';
import { SampleAppHost } from './CoreAppScene';

// The real core apps on sample data, as window contents of the desktop scenes.

/** Files in the folder of summer photos. */
export function LiveFiles() {
  return (
    <SampleAppHost
      Component={FilesApp}
      options={{ files: filesSample, session: { share: '2', path: sampleFolder } }}
    />
  );
}

/** Settings on a page, the pools by default. */
export function LiveSettings({ page = 'pools' }: { page?: string }) {
  return <SampleAppHost Component={SettingsApp} launch={{ page }} />;
}

export function LiveLogs() {
  return <SampleAppHost Component={LogsApp} answers={logsAnswers} />;
}
