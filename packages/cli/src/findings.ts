import { app, store } from '../../../i18n/en/errors.json' with { type: 'json' };
import { IntlMessageFormat } from 'intl-messageformat';
import type { Issue } from './issues';

/** Problems only the CLI reports: the box sees a package, not the project it was built from. */
export type CliCode =
  | 'cli.manifest.missing'
  | 'cli.frontend.missing'
  | 'cli.frontend.skipped'
  | 'cli.icon.template'
  | 'cli.lock.missing'
  | 'cli.lock.invalid'
  | 'cli.lock.stale'
  | 'cli.lock.platform_missing'
  | 'cli.compose.healthcheck_redundant'
  | 'cli.backend.unbuilt';

/** What `pithyx validate` reports: an error stops `pithyx pack`, a warning does not. */
export interface Finding {
  level: 'error' | 'warning';
  code: Issue['code'] | CliCode;
  params: Record<string, string | number>;
}

const cliTexts: Record<CliCode, string> = {
  'cli.manifest.missing':
    'pithyx.json does not exist in {folder}. Run the command in the project folder of an app, or name that folder.',
  'cli.frontend.missing':
    'The frontend build {folder} does not exist. Build the app first, or name the folder with --frontend.',
  'cli.frontend.skipped':
    '{path} is left out of the package: hidden files and folders cannot be in an app package.',
  'cli.icon.template':
    'icon.svg is still the template icon (the Pithyx box). Replace it with the icon of your app.',
  'cli.lock.missing': '{path} is missing. Run pithyx lock to resolve the images of the backend.',
  'cli.lock.invalid': '{path} is not a valid images lock. Run pithyx lock to write it again.',
  'cli.lock.stale':
    '{path} does not match the Compose file ({reason, select, missing {{image} is not locked} unused {{image} is not used} other {{image} differs}}). Run pithyx lock.',
  'cli.lock.platform_missing':
    '{image} has no image for {arch}; the app will not install on {arch} boxes.',
  'cli.compose.healthcheck_redundant':
    'Service {service} has a Compose healthcheck next to the health of the manifest. Docker starts a process in the container for every check, which delays every start and keeps the idle backend busy. Remove it and let health decide.',
  'cli.backend.unbuilt':
    '{path} is not built yet: run pithyx build, or let the catalog build it from the source form.',
};

function lookup(texts: unknown, path: readonly string[]): string | undefined {
  let node = texts;
  for (const part of path) {
    if (typeof node !== 'object' || node === null) {
      return undefined;
    }
    node = (node as Record<string, unknown>)[part];
  }
  return typeof node === 'string' ? node : undefined;
}

/** The English text of a finding: the box's own message for its codes, so the CLI says what the box would say. */
export function describe(finding: Pick<Finding, 'code' | 'params'>): string {
  const text =
    finding.code in cliTexts
      ? cliTexts[finding.code as CliCode]
      : lookup({ app, store }, finding.code.split('.'));
  if (text === undefined) {
    return `${finding.code} ${JSON.stringify(finding.params)}`;
  }
  try {
    return String(new IntlMessageFormat(text, 'en').format(finding.params));
  } catch {
    return `${finding.code} ${JSON.stringify(finding.params)}`;
  }
}

/** An issue of the box's validators as an error finding. */
export function error(issue: Issue): Finding {
  return { level: 'error', code: issue.code, params: issue.params };
}
