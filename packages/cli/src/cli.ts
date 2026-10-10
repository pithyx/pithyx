#!/usr/bin/env node
// The pithyx command: validate, lock, build, pack and dev for an app project, and the CI of catalog repositories.
import { mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { version } from '../package.json' with { type: 'json' };
import { build } from './build';
import type { CatalogChannel, TrustedKey } from './catalog';
import {
  checkPullRequest,
  IssuesError,
  pendingVersions,
  readCatalog,
  signIndex,
  signingKey,
  verifyBranch,
  writeIndex,
} from './catalog-tools';
import { runDev } from './dev/start';
import { dockerCredentials, saveImages } from './docker';
import { describe, error, type Finding } from './findings';
import {
  composeImages,
  formatLock,
  LOCK_FILE,
  parseLock,
  RegistryClient,
  resolveLock,
} from './lock';
import { pack, packageName } from './pack';
import { lockTarget, readProject, type Project } from './project';

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
  build      Build the services of backend/compose.dev.yaml that have build: with the local
             Docker for this machine, tag them pithyx.local/<id>/<service>:<version> and pin
             backend/compose.yaml and backend/images.lock.json to them (for pack --images).
             --oci-out builds reproducibly for amd64 and arm64 with buildx into OCI archives
             and pushes nothing; --pin then pins to those archives as pushed to a registry.
  pack       Validate, then write <id>-<version>.pithyxapp: a deterministic tar.gz with
             pithyx.json, icon.svg, frontend/, the backend's compose.yaml and lock, README
             and LICENSE when they exist, and CHECKSUMS. The same files give the same bytes.
  dev        Run the mock desktop with the app in the box's sandbox and a proxy to the
             backend, which runs with docker compose (backend/compose.dev.yaml instead of
             the pinned file when it exists). With Vite, use the plugin @pithyx/cli/vite and run vite instead.
  catalog    The CI of a catalog repository (run in its root):
               catalog check --base <commit> --author <login> [--index <file>]
                 the pull request rules on the changes since the base commit
               catalog pending [--index <file>]
                 the app versions of apps/ that catalog-next does not list yet, as JSON
               catalog index <branch folder> --channel stable|beta (--add <package>...
                 --source <checkout> --repository <url> --commit <sha> | --promote <folder>)
                 adds built packages or the versions of another branch, writes index.json
               catalog sign <branch folder> --key-id <id> [--append]
                 signs index.json with the Ed25519 key (PEM) in $PITHYX_SIGNING_KEY
               catalog verify <branch folder> --channel stable|beta [--key <id>=<base64>]...
                 checks the signature (with keys), the index and every file as a box does

Options:
  --frontend <folder>   The built frontend (default: dist)
  --oci-out <folder>    build: write OCI archives <service>.tar for amd64 and arm64 there
  --pin <prefix>        build: pin to the archives of --oci-out as <prefix>/<id>/<service>:<version>
  --catalog <file>      catalog: the catalog.json (default: catalog.json)
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
      'oci-out': { type: 'string' },
      pin: { type: 'string' },
      catalog: { type: 'string' },
      base: { type: 'string' },
      author: { type: 'string' },
      index: { type: 'string' },
      'allow-reserved-id': { type: 'boolean' },
      channel: { type: 'string' },
      add: { type: 'string', multiple: true },
      source: { type: 'string' },
      repository: { type: 'string' },
      commit: { type: 'string' },
      promote: { type: 'string' },
      'key-id': { type: 'string' },
      append: { type: 'boolean' },
      key: { type: 'string', multiple: true },
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
  if (command === 'catalog') {
    return catalog(folder, rest, values);
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
      const target = lockTarget(project);
      if (target.kind === 'invalid') {
        print(target.findings);
        console.error('The project is not valid; there is nothing to lock.');
        return 1;
      }
      if (target.kind === 'none') {
        console.error('The app has no backend; there is nothing to lock.');
        return 0;
      }
      const compose = target.path;
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
    case 'build': {
      for (const line of await build(folder, { ociOut: values['oci-out'], pin: values.pin })) {
        console.log(line);
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

function channelOf(value: string | undefined): CatalogChannel {
  if (value !== 'stable' && value !== 'beta') {
    throw new Error('--channel must be stable or beta');
  }
  return value;
}

function keyOf(value: string, channel: CatalogChannel): TrustedKey {
  const equals = value.indexOf('=');
  if (equals <= 0) {
    throw new Error(`--key ${value}: write it as id=<base64 public key>`);
  }
  return { keyId: value.slice(0, equals), publicKey: value.slice(equals + 1), channels: [channel] };
}

/** The `catalog` subcommands, run in the root of a catalog repository. */
function catalog(
  subcommand: string,
  rest: readonly string[],
  values: Record<string, string | boolean | string[] | undefined>,
): number {
  const text = (name: string) => {
    const value = values[name];
    return typeof value === 'string' ? value : undefined;
  };
  const list = (name: string) => {
    const value = values[name];
    return Array.isArray(value) ? value : [];
  };
  const [branch, ...extra] = rest;
  const catalogFile = text('catalog') ?? 'catalog.json';
  try {
    switch (subcommand) {
      case 'check': {
        const base = text('base');
        const author = text('author');
        if (base === undefined || author === undefined) {
          console.error('pithyx catalog check needs --base <commit> and --author <login>.');
          return 2;
        }
        const issues = checkPullRequest({
          root: '.',
          base,
          author,
          index: text('index'),
          allowReservedId: values['allow-reserved-id'] === true,
        });
        print(issues.map(error));
        if (issues.length > 0) {
          return 1;
        }
        console.log('The pull request passes the catalog rules.');
        return 0;
      }
      case 'pending':
        console.log(JSON.stringify(pendingVersions('.', text('index'))));
        return 0;
      case 'index': {
        if (branch === undefined || extra.length > 0) {
          console.error('pithyx catalog index takes one branch folder.');
          return 2;
        }
        const added = writeIndex({
          branch,
          catalog: readCatalog(catalogFile, { allowReservedId: true }),
          channel: channelOf(text('channel')),
          add: list('add'),
          source: text('source'),
          repository: text('repository'),
          commit: text('commit'),
          promote: text('promote'),
        });
        console.log(
          added.length === 0
            ? 'Nothing new; the index has a new sequence.'
            : `Added ${added.join(', ')}.`,
        );
        return 0;
      }
      case 'sign': {
        const keyId = text('key-id');
        const pem = process.env.PITHYX_SIGNING_KEY;
        if (branch === undefined || keyId === undefined || pem === undefined || pem === '') {
          console.error(
            'pithyx catalog sign <branch folder> --key-id <id> needs the key in PITHYX_SIGNING_KEY.',
          );
          return 2;
        }
        signIndex(branch, keyId, signingKey(pem), values.append === true);
        console.log(`Signed ${branch}/index.json with ${keyId}.`);
        return 0;
      }
      case 'verify': {
        if (branch === undefined) {
          console.error('pithyx catalog verify takes one branch folder.');
          return 2;
        }
        const channel = channelOf(text('channel'));
        const keys = list('key').map((key) => keyOf(key, channel));
        const issues = verifyBranch(branch, {
          catalog: readCatalog(catalogFile, { allowReservedId: true }).id,
          channel,
          keys,
        });
        print(issues.map(error));
        if (issues.length > 0) {
          return 1;
        }
        console.log(
          keys.length === 0
            ? `${branch} is valid (signature not checked).`
            : `${branch} is valid and signed.`,
        );
        return 0;
      }
      default:
        console.error(`Unknown catalog command ${subcommand}. Run pithyx --help.`);
        return 2;
    }
  } catch (failure) {
    if (failure instanceof IssuesError) {
      console.error(failure.message);
      print(failure.issues.map(error));
      return 1;
    }
    throw failure;
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
