#!/usr/bin/env node
// The pithyx command: validate, lock, pack and dev for an app project.
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { version } from '../package.json' with { type: 'json' };
import { runDev } from './dev/start';
import { dockerCredentials, saveImages } from './docker';
import { describe, type Finding } from './findings';
import {
  composeImages,
  formatLock,
  LOCK_FILE,
  parseLock,
  RegistryClient,
  resolveLock,
} from './lock';
import { pack, packageName } from './pack';
import { readProject, type Project } from './project';

const HELP = `pithyx ${version}: build Pithyx apps

Usage: pithyx <command> [project folder] [options]

Commands:
  validate   Check the project as the box checks its package: pithyx.json, icon.svg, the
             entry in the built frontend, the package layout, backend/compose.yaml and
             backend/images.lock.json. Warns while icon.svg is still the template icon.
  lock       Resolve the images of backend/compose.yaml from their registries and write
             backend/images.lock.json (the platform manifest and image id for amd64 and
             arm64). Needs the network; registries are asked anonymously or with the
             credentials stored in ~/.docker/config.json (not with credential helpers).
  pack       Validate, then write <id>-<version>.pithyxapp: a deterministic tar.gz with
             pithyx.json, icon.svg, frontend/, the backend's compose.yaml and lock, README
             and LICENSE when they exist, and CHECKSUMS. The same files give the same bytes.
  dev        Run the mock desktop with the app in the box's sandbox and a proxy to the
             backend, which runs with docker compose (plus backend/compose.dev.yaml when it
             exists). With Vite, use the plugin @pithyx/cli/vite and run vite instead.

Options:
  --frontend <folder>   The built frontend (default: dist)
  --out <path>          pack: the package file, or a folder for it (default: the project)
  --images              pack: add backend/images/<arch>/<id>.tar for offline installs, saved
                        with the local docker (pull and save per architecture of the lock).
                        Needs Docker with access to the registries; the images make the
                        package large, and their bytes depend on Docker's save format.
  --port <port>         dev: the port (default: 7400)
  --host <host>         dev: the address to listen on (default: 127.0.0.1)
  --endpoint <n=url>    dev: reach the endpoint n at url instead of through Docker,
                        for example --endpoint api=http://localhost:8080 (repeatable)
  --no-docker           dev: do not start the backend; endpoints without --endpoint answer 503
  -h, --help            Show this help
  -v, --version         Show the version

There is no development mode on the box: install the package in the Store to try it there.
`;

function print(findings: readonly Finding[]): void {
  for (const finding of findings) {
    const line = `${finding.level} ${finding.code}: ${describe(finding)}`;
    if (finding.level === 'error') {
      console.error(line);
    } else {
      console.warn(line);
    }
  }
}

function hasErrors(project: Project): boolean {
  return project.findings.some((finding) => finding.level === 'error');
}

function endpoints(values: readonly string[]): Record<string, string> {
  return Object.fromEntries(
    values.map((value) => {
      const equals = value.indexOf('=');
      if (equals <= 0) {
        throw new Error(`--endpoint ${value}: write it as name=url`);
      }
      return [value.slice(0, equals), value.slice(equals + 1)];
    }),
  );
}

async function main(): Promise<number> {
  const { values, positionals } = parseArgs({
    allowPositionals: true,
    options: {
      frontend: { type: 'string' },
      out: { type: 'string' },
      images: { type: 'boolean' },
      port: { type: 'string' },
      host: { type: 'string' },
      endpoint: { type: 'string', multiple: true },
      'no-docker': { type: 'boolean' },
      help: { type: 'boolean', short: 'h' },
      version: { type: 'boolean', short: 'v' },
    },
  });
  if (values.version === true) {
    console.log(version);
    return 0;
  }
  const [command, folder = '.', ...rest] = positionals;
  if (values.help === true || command === undefined || command === 'help') {
    console.log(HELP);
    return command === undefined && values.help !== true ? 2 : 0;
  }
  if (rest.length > 0) {
    console.error(`pithyx ${command} takes one project folder, not: ${rest.join(' ')}`);
    return 2;
  }
  const projectOptions = { frontend: values.frontend };
  switch (command) {
    case 'validate': {
      const project = readProject(folder, projectOptions);
      print(project.findings);
      if (hasErrors(project)) {
        return 1;
      }
      console.log(`${project.manifest?.id ?? ''} ${project.manifest?.version ?? ''} is valid.`);
      return 0;
    }
    case 'lock': {
      const project = readProject(folder, projectOptions);
      const compose = project.manifest?.backend?.compose;
      if (project.manifest === undefined || compose === undefined) {
        console.error('The app has no backend; there is nothing to lock.');
        return project.manifest === undefined ? 1 : 0;
      }
      const images = composeImages(readFileSync(join(project.root, compose)));
      const lock = await resolveLock(images, new RegistryClient(fetch, dockerCredentials));
      const file = join(project.root, LOCK_FILE);
      const text = formatLock(lock);
      let current: string | undefined;
      try {
        current = readFileSync(file, 'utf8');
      } catch {
        current = undefined;
      }
      if (current === text) {
        console.log(`${LOCK_FILE} is up to date.`);
      } else {
        writeFileSync(file, text);
        console.log(`Wrote ${LOCK_FILE} for ${String(images.length)} image(s).`);
      }
      return 0;
    }
    case 'pack': {
      const project = readProject(folder, projectOptions);
      print(project.findings);
      if (hasErrors(project) || project.manifest === undefined) {
        console.error('Not packed: fix the errors above first.');
        return 1;
      }
      const name = packageName(project.manifest);
      let target = resolve(project.root, name);
      if (values.out !== undefined) {
        const out = resolve(values.out);
        target = out.endsWith('.pithyxapp') ? out : join(out, name);
        if (
          !out.endsWith('.pithyxapp') &&
          !statSync(out, { throwIfNoEntry: false })?.isDirectory()
        ) {
          console.error(`--out ${values.out} is neither a .pithyxapp file nor a folder.`);
          return 2;
        }
      }
      let scratch: string | undefined;
      try {
        let extra: Awaited<ReturnType<typeof saveImages>> = [];
        if (values.images === true) {
          const lockEntry = project.entries.find((entry) => entry.path === LOCK_FILE);
          const lock =
            typeof lockEntry?.source === 'string'
              ? parseLock(readFileSync(lockEntry.source))
              : null;
          if (lock === null) {
            console.error('--images needs an app with a backend and its images lock.');
            return 2;
          }
          scratch = mkdtempSync(join(tmpdir(), 'pithyx-images-'));
          extra = await saveImages(lock, scratch);
        }
        await pack(project, target, extra);
      } finally {
        if (scratch !== undefined) {
          rmSync(scratch, { recursive: true, force: true });
        }
      }
      console.log(`Packed ${target} (${String(statSync(target).size)} bytes).`);
      return 0;
    }
    case 'dev': {
      const port = values.port === undefined ? undefined : Number(values.port);
      if (port !== undefined && !(Number.isInteger(port) && port > 0 && port < 65536)) {
        console.error(`--port ${String(values.port)} is not a port.`);
        return 2;
      }
      await runDev(folder, {
        ...projectOptions,
        port,
        host: values.host,
        endpoints: endpoints(values.endpoint ?? []),
        docker: values['no-docker'] !== true,
      });
      return -1;
    }
    default:
      console.error(`Unknown command ${command}. Run pithyx --help.`);
      return 2;
  }
}

main().then(
  (code) => {
    if (code >= 0) {
      process.exitCode = code;
    }
  },
  (failure: unknown) => {
    console.error(`pithyx: ${failure instanceof Error ? failure.message : String(failure)}`);
    process.exitCode = 1;
  },
);
