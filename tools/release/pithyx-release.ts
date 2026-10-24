// The release tool (D364): one command over the release scripts of this folder, so their order, defaults and
// preconditions live in one place. Every command first becomes a plan of plain commands, which --dry-run prints and
// which otherwise run one after the other; nothing happens that the plan does not show.
//
//   tools/release/pithyx-release [<command> [options]] [--dry-run]
//
// Without a command in a terminal it asks which command to run and for its options, shows the plan and asks before it
// runs. Commands: status, checklist, iso, update, sign, channel, publish-beta, publish-sdk, sources, baseline,
// templates, key, sign-remote. `pithyx-release help` lists them with their options.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { createInterface } from 'node:readline/promises';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

/** One command of a plan: what runs, in which folder, and why. */
export interface Step {
  command: string;
  args: string[];
  why: string;
}

/** What the tool knows about the repository and this machine before it plans anything. */
export interface Facts {
  root: string;
  /** The product version of daemon/Directory.Build.props. */
  productVersion: string;
  /** The shared version of the npm packages. */
  sdkVersion: string;
  /** The version of the packages image/build.sh left in image/out/debs, if any. */
  builtVersion: string | null;
  /** File names in image/out. */
  out: string[];
  /** File names in image/baseline. */
  baselines: string[];
  /** The beta key on this machine, if any. */
  betaKey: string | null;
  branch: string;
  clean: boolean;
  tags: string[];
}

/** A refusal with a hint what to do instead; the tool prints it and exits with 1. */
export class PlanError extends Error {}

const BETA_KEY_ID = 'pithyx-beta-1';
const DEFAULT_MINIMUM = '1.0.0';
const SDK_PACKAGES = ['@pithyx/sdk', '@pithyx/ui', '@pithyx/cli'];
const VERSION = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;

/** The options every command understands, as parseArgs wants them. */
const OPTIONS = {
  'dry-run': { type: 'boolean' },
  version: { type: 'string' },
  channel: { type: 'string' },
  since: { type: 'string' },
  full: { type: 'boolean' },
  minimum: { type: 'string' },
  sign: { type: 'boolean' },
  'build-image': { type: 'boolean' },
  key: { type: 'string' },
  'key-id': { type: 'string' },
  sequence: { type: 'string' },
  url: { type: 'string' },
  mirror: { type: 'string' },
  'reuse-rootfs': { type: 'boolean' },
  'no-app-build': { type: 'boolean' },
  otp: { type: 'string' },
  latest: { type: 'boolean' },
  'npm-dry-run': { type: 'boolean' },
  install: { type: 'boolean' },
  yes: { type: 'boolean' },
  from: { type: 'string' },
  repo: { type: 'string' },
  work: { type: 'string' },
  publish: { type: 'boolean' },
} as const;

export type Options = Partial<Record<keyof typeof OPTIONS, string | boolean>>;

interface CommandInfo {
  summary: string;
  usage: string;
  /** Options the menu asks for, with their question. */
  asks: { name: keyof typeof OPTIONS | 'file' | 'folder' | 'id'; question: string }[];
}

export const COMMANDS: Record<string, CommandInfo> = {
  status: {
    summary: 'What is built, signed and tagged on this machine',
    usage: 'status',
    asks: [],
  },
  checklist: {
    summary: 'The release steps for a version and which of them are done',
    usage: 'checklist [--version <v>]',
    asks: [{ name: 'version', question: 'Version' }],
  },
  iso: {
    summary: 'Build the ISO and its package list (image/build.sh)',
    usage: 'iso [--version <v>] [--mirror <url>] [--reuse-rootfs] [--no-app-build]',
    asks: [{ name: 'version', question: 'Version' }],
  },
  update: {
    summary: 'Build an update file against a baseline, optionally signed (build-update.sh)',
    usage:
      'update [--version <v>] [--channel beta|stable] [--since <packages> | --full | --minimum <v>] [--build-image] [--sign [--key <pem> --key-id <id>]]',
    asks: [
      { name: 'version', question: 'Version' },
      { name: 'minimum', question: 'Minimum version the file updates from' },
    ],
  },
  sign: {
    summary: 'Sign an update file (sign-update.sh); the beta key by default',
    usage: 'sign <file> [--key <pem> --key-id <id>]',
    asks: [{ name: 'file', question: 'Update file' }],
  },
  channel: {
    summary: 'Write and sign channel.json for an update file (build-channel.sh)',
    usage: 'channel <file> --key <pem> --key-id <id> --sequence <n> [--url <url>]',
    asks: [
      { name: 'file', question: 'Update file' },
      { name: 'sequence', question: 'Sequence (higher than the current one)' },
    ],
  },
  'sign-remote': {
    summary:
      'Fetch an update file from the build machine, sign it here with channel.json (sign-remote.sh)',
    usage:
      'sign-remote --from <[user@]host:dir> --key <pem> --key-id <id> [--version <v>] [--sequence <n>] [--repo <owner/name>] [--work <dir>] [--publish [--yes]]',
    asks: [
      { name: 'from', question: 'Build machine and folder ([user@]host:dir)' },
      { name: 'version', question: 'Version' },
      { name: 'key', question: 'Private key file' },
      { name: 'key-id', question: 'Key id' },
    ],
  },
  'publish-beta': {
    summary: 'Publish a signed beta file and move channel-beta (publish-beta.sh)',
    usage: 'publish-beta <file> [--yes]',
    asks: [{ name: 'file', question: 'Signed update file' }],
  },
  'publish-sdk': {
    summary: 'Publish @pithyx/sdk, ui and cli (publish-sdk.sh), optionally as latest',
    usage: 'publish-sdk [--otp <code>] [--latest] [--npm-dry-run]',
    asks: [],
  },
  sources: {
    summary: 'Collect and pack the GPL sources of an image (collect-sources.ts)',
    usage: 'sources [--version <v>]',
    asks: [{ name: 'version', question: 'Version' }],
  },
  baseline: {
    summary: "Keep a release image's package list in image/baseline",
    usage: 'baseline [--version <v>]',
    asks: [{ name: 'version', question: 'Version' }],
  },
  templates: {
    summary: 'Raise the SDK version in local clones of the app templates (bump-templates.ts)',
    usage: 'templates <folder> [--install]',
    asks: [{ name: 'folder', question: 'Folder with the template clones' }],
  },
  key: {
    summary: 'Create an Ed25519 signing key (create-key.sh)',
    usage: 'key <id> <file>',
    asks: [
      { name: 'id', question: 'Key id' },
      { name: 'file', question: 'Private key file to write' },
    ],
  },
};

function git(root: string, args: string[]): string {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  return result.status === 0 ? result.stdout.trim() : '';
}

function list(folder: string): string[] {
  return existsSync(folder) ? readdirSync(folder).sort() : [];
}

/** Reads the facts of the checkout at root and of this machine. */
export function readFacts(root: string): Facts {
  const props = readFileSync(join(root, 'daemon', 'Directory.Build.props'), 'utf8');
  const sdk = JSON.parse(readFileSync(join(root, 'sdk', 'package.json'), 'utf8')) as {
    version: string;
  };
  const debs = join(root, 'image', 'out', 'debs', 'debs.env');
  const betaKey = join(homedir(), '.config', 'pithyx-dev', `${BETA_KEY_ID}.pem`);
  return {
    root,
    productVersion: /<Version>([^<]+)<\/Version>/.exec(props)?.[1] ?? '',
    sdkVersion: sdk.version,
    builtVersion: existsSync(debs)
      ? (/^VERSION=(.*)$/m.exec(readFileSync(debs, 'utf8'))?.[1] ?? null)
      : null,
    out: list(join(root, 'image', 'out')),
    baselines: list(join(root, 'image', 'baseline')),
    betaKey: existsSync(betaKey) ? betaKey : null,
    branch: git(root, ['branch', '--show-current']),
    clean: git(root, ['status', '--porcelain', '--untracked-files=no']) === '',
    tags: git(root, ['tag', '--list']).split('\n').filter(Boolean),
  };
}

const packagesFile = (version: string) => `pithyx-${version}-amd64.packages`;
const updateFile = (version: string) => `pithyx-${version}-amd64.pithyxupdate`;

function versionOf(options: Options, facts: Facts): string {
  const version = typeof options.version === 'string' ? options.version : facts.productVersion;
  if (!VERSION.test(version)) {
    throw new PlanError(`"${version}" is not a version like 1.0.0 or 1.0.0-beta.2`);
  }
  return version;
}

function text(options: Options, name: keyof typeof OPTIONS): string | undefined {
  const value = options[name];
  return typeof value === 'string' ? value : undefined;
}

/** The key that signs a file of this channel: the one named, else the beta key for beta. */
function signingKey(options: Options, facts: Facts, channel: string): [string, string] {
  const key = text(options, 'key');
  const keyId = text(options, 'key-id');
  if (key !== undefined || keyId !== undefined) {
    if (key === undefined || keyId === undefined) {
      throw new PlanError('--key and --key-id go together');
    }
    return [key, keyId];
  }
  if (channel !== 'beta') {
    throw new PlanError(
      'a stable file needs the release key: pass --key <pem> --key-id pithyx-release-1',
    );
  }
  if (facts.betaKey === null) {
    throw new PlanError(
      `no beta key in ~/.config/pithyx-dev/${BETA_KEY_ID}.pem; pass --key and --key-id`,
    );
  }
  return [facts.betaKey, BETA_KEY_ID];
}

/** The package list an update is built against: --since, else the minimum's list in image/baseline or image/out. */
export function baselineFor(options: Options, facts: Facts): string {
  const since = text(options, 'since');
  if (since !== undefined) {
    return resolve(since);
  }
  const minimum = text(options, 'minimum') ?? DEFAULT_MINIMUM;
  const name = packagesFile(minimum);
  if (facts.baselines.includes(name)) {
    return join(facts.root, 'image', 'baseline', name);
  }
  if (facts.out.includes(name)) {
    return join(facts.root, 'image', 'out', name);
  }
  throw new PlanError(
    `no package list of ${minimum}: keep it with \`pithyx-release baseline --version ${minimum}\` after its image, or pass --since <file> or --full`,
  );
}

/** The steps of a command, or a PlanError that says what is missing. */
export function plan(
  command: string,
  positionals: string[],
  options: Options,
  facts: Facts,
): Step[] {
  const script = (name: string) => join(facts.root, 'tools', 'release', name);
  switch (command) {
    case 'iso': {
      const version = versionOf(options, facts);
      const args = ['--version', version];
      const mirror = text(options, 'mirror');
      if (mirror !== undefined) {
        args.push('--mirror', mirror);
      }
      if (options['reuse-rootfs'] === true) {
        args.push('--reuse-rootfs');
      }
      if (options['no-app-build'] === true) {
        args.push('--no-app-build');
      }
      return [
        {
          command: join(facts.root, 'image', 'build.sh'),
          args,
          why: `the ISO and package list of ${version}`,
        },
      ];
    }
    case 'update': {
      const version = versionOf(options, facts);
      const channel = text(options, 'channel') ?? (version.includes('-') ? 'beta' : 'stable');
      if (channel !== 'beta' && channel !== 'stable') {
        throw new PlanError('--channel is beta or stable');
      }
      const steps: Step[] = [];
      if (options['build-image'] === true) {
        steps.push(...plan('iso', [], options, facts));
      } else if (facts.builtVersion !== version) {
        throw new PlanError(
          `image/out holds the packages of ${facts.builtVersion ?? 'no build'}, not ${version}: add --build-image or run \`pithyx-release iso --version ${version}\` first`,
        );
      }
      const args = ['--version', version, '--channel', channel];
      if (options.full === true) {
        args.push('--full');
      } else {
        args.push('--since', baselineFor(options, facts));
      }
      const minimum = text(options, 'minimum');
      if (minimum !== undefined) {
        args.push('--minimum', minimum);
      }
      steps.push({
        command: script('build-update.sh'),
        args,
        why: `the ${channel} update file of ${version}`,
      });
      if (options.sign === true) {
        const [key, keyId] = signingKey(options, facts, channel);
        steps.push({
          command: script('sign-update.sh'),
          args: [
            join(facts.root, 'image', 'out', updateFile(version)),
            '--key',
            key,
            '--key-id',
            keyId,
          ],
          why: `its signature with ${keyId}`,
        });
      }
      return steps;
    }
    case 'sign': {
      const file = positionals[0];
      if (file === undefined) {
        throw new PlanError('which file? pithyx-release sign <file>');
      }
      const [key, keyId] = signingKey(options, facts, text(options, 'channel') ?? 'beta');
      return [
        {
          command: script('sign-update.sh'),
          args: [resolve(file), '--key', key, '--key-id', keyId],
          why: `the signature with ${keyId}`,
        },
      ];
    }
    case 'channel': {
      const file = positionals[0];
      const key = text(options, 'key');
      const keyId = text(options, 'key-id');
      const sequence = text(options, 'sequence');
      if (
        file === undefined ||
        key === undefined ||
        keyId === undefined ||
        sequence === undefined
      ) {
        throw new PlanError(
          'pithyx-release channel <file> --key <pem> --key-id <id> --sequence <n>',
        );
      }
      const args = [resolve(file), '--key', key, '--key-id', keyId, '--sequence', sequence];
      const url = text(options, 'url');
      if (url !== undefined) {
        args.push('--url', url);
      }
      return [{ command: script('build-channel.sh'), args, why: 'channel.json and its signature' }];
    }
    case 'sign-remote': {
      const from = text(options, 'from');
      const key = text(options, 'key');
      const keyId = text(options, 'key-id');
      if (from === undefined || key === undefined || keyId === undefined) {
        throw new PlanError(
          'pithyx-release sign-remote --from <[user@]host:dir> --key <pem> --key-id <id>',
        );
      }
      const version = versionOf(options, facts);
      const args = ['--from', from, '--version', version, '--key', key, '--key-id', keyId];
      for (const name of ['sequence', 'repo', 'work'] as const) {
        const value = text(options, name);
        if (value !== undefined) {
          args.push(`--${name}`, value);
        }
      }
      if (options.publish === true) {
        args.push('--publish', ...(options.yes === true ? ['--yes'] : []));
      }
      return [
        {
          command: script('sign-remote.sh'),
          args,
          why: `the ${version} update file of ${from} signed with ${keyId}, with channel.json${options.publish === true ? ', published' : ''}`,
        },
      ];
    }
    case 'publish-beta': {
      const file = positionals[0];
      if (file === undefined) {
        throw new PlanError('which file? pithyx-release publish-beta <file>');
      }
      return [
        {
          command: script('publish-beta.sh'),
          args: [resolve(file), ...(options.yes === true ? ['--yes'] : [])],
          why: 'the pre-release beta-<version> and a new channel-beta',
        },
      ];
    }
    case 'publish-sdk': {
      const args: string[] = [];
      const otp = text(options, 'otp');
      if (otp !== undefined) {
        args.push('--otp', otp);
      }
      if (options['npm-dry-run'] === true) {
        args.push('--dry-run');
      }
      // A stable version goes straight to latest; publish-sdk.sh refuses it without --latest.
      const prerelease = facts.sdkVersion.includes('-');
      if (options.latest === true && !prerelease) {
        args.push('--latest');
      }
      const steps: Step[] = [
        { command: script('publish-sdk.sh'), args, why: `the npm packages at ${facts.sdkVersion}` },
      ];
      if (options.latest === true && prerelease && options['npm-dry-run'] !== true) {
        // publish-sdk.sh never moves latest to a prerelease; the owner's --latest does it afterwards.
        for (const name of SDK_PACKAGES) {
          steps.push({
            command: 'npm',
            args: [
              'dist-tag',
              'add',
              `${name}@${facts.sdkVersion}`,
              'latest',
              ...(otp === undefined ? [] : ['--otp', otp]),
            ],
            why: `latest of ${name}`,
          });
        }
      }
      return steps;
    }
    case 'sources': {
      const version = versionOf(options, facts);
      return [
        {
          command: 'node',
          args: [script('collect-sources.ts'), '--version', version, '--pack'],
          why: `the GPL sources of the ${version} image as release parts`,
        },
      ];
    }
    case 'baseline': {
      const version = versionOf(options, facts);
      if (!facts.out.includes(packagesFile(version))) {
        throw new PlanError(
          `image/out has no ${packagesFile(version)}: build the image of ${version} first`,
        );
      }
      return [
        {
          command: 'cp',
          args: [
            join(facts.root, 'image', 'out', packagesFile(version)),
            join(facts.root, 'image', 'baseline', packagesFile(version)),
          ],
          why: `the base later update files of ${version} on are built against (commit it)`,
        },
      ];
    }
    case 'templates': {
      const folder = positionals[0];
      if (folder === undefined) {
        throw new PlanError('which folder? pithyx-release templates <folder with the clones>');
      }
      return [
        {
          command: 'node',
          args: [
            script('bump-templates.ts'),
            resolve(folder),
            facts.sdkVersion,
            ...(options.install === true ? ['--install'] : []),
          ],
          why: `the templates on @pithyx/* ${facts.sdkVersion}`,
        },
      ];
    }
    case 'key': {
      const [id, file] = positionals;
      if (id === undefined || file === undefined) {
        throw new PlanError('pithyx-release key <id> <file>');
      }
      return [
        { command: script('create-key.sh'), args: [id, resolve(file)], why: `a new key ${id}` },
      ];
    }
    default:
      throw new PlanError(`unknown command ${command}; see pithyx-release help`);
  }
}

/** Lines of `status`: versions, the checkout, builds and keys, with a hint where something is off. */
export function describe(facts: Facts): string[] {
  const isos = facts.out.filter((name) => name.endsWith('.iso'));
  const updates = facts.out.filter((name) => name.endsWith('.pithyxupdate'));
  return [
    `product version   ${facts.productVersion} (daemon/Directory.Build.props)`,
    `npm packages      ${facts.sdkVersion}${facts.tags.includes(`sdk-v${facts.sdkVersion}`) ? ' (tagged)' : ' (not tagged yet)'}`,
    `checkout          ${facts.branch || 'detached'}${facts.clean ? ', clean' : ', with changes'}`,
    `built packages    ${facts.builtVersion ?? 'none'} (image/out/debs, what update builds from)`,
    `ISOs              ${isos.length === 0 ? 'none' : isos.join(', ')}`,
    `update files      ${updates.length === 0 ? 'none' : updates.join(', ')}`,
    `baselines         ${facts.baselines.length === 0 ? 'none (image/baseline)' : facts.baselines.join(', ')}`,
    `beta key          ${facts.betaKey === null ? 'missing' : relative(homedir(), facts.betaKey).replace(/^/, '~/')}`,
  ];
}

/** The release of a version step by step, each with whether this machine shows it done. */
export function checklist(version: string, facts: Facts): string[] {
  const mark = (done: boolean) => (done ? '[x]' : '[ ]');
  const sources = facts.out.some((name) => name.startsWith(`pithyx-${version}-sources`));
  return [
    `${mark(facts.productVersion === version)} daemon/Directory.Build.props says ${version}`,
    `${mark(facts.out.includes(`pithyx-${version}-amd64.iso`))} ISO: pithyx-release iso --version ${version}`,
    `${mark(sources)} GPL sources: pithyx-release sources --version ${version}`,
    `${mark(facts.baselines.includes(packagesFile(version)))} baseline: pithyx-release baseline --version ${version}, then commit image/baseline`,
    `${mark(facts.tags.includes(`v${version}`))} tag v${version} on stable (the owner)`,
    `${mark(facts.tags.includes(`sdk-v${facts.sdkVersion}`))} npm packages ${facts.sdkVersion}: pithyx-release publish-sdk`,
    '[ ] later updates: pithyx-release update --version <next> --minimum ' + version + ' --sign',
  ];
}

export function quote(value: string): string {
  return /^[\w./:@=+-]+$/.test(value) ? value : `'${value.replaceAll("'", "'\\''")}'`;
}

function show(steps: Step[], root: string): void {
  for (const step of steps) {
    const command = step.command.startsWith(root) ? relative(root, step.command) : step.command;
    console.log(
      `  ${[command, ...step.args.map((arg) => (arg.startsWith(`${root}/`) ? relative(root, arg) : arg))].map(quote).join(' ')}`,
    );
    console.log(`      ${step.why}`);
  }
}

function run(steps: Step[], root: string): number {
  for (const step of steps) {
    const result = spawnSync(step.command, step.args, { cwd: root, stdio: 'inherit' });
    if (result.status !== 0) {
      console.error(
        `pithyx-release: ${step.command} ended with ${String(result.status ?? result.signal)}`,
      );
      return 1;
    }
  }
  return 0;
}

function help(): void {
  console.log('pithyx-release [<command> [options]] [--dry-run]\n');
  for (const info of Object.values(COMMANDS)) {
    console.log(`  ${info.usage}\n      ${info.summary}`);
  }
  console.log(
    '\n--dry-run prints the plan without running it. Without a command, a terminal gets a menu.',
  );
}

/** The menu: pick a command, answer its questions, see the plan, confirm. */
async function menu(facts: Facts): Promise<number> {
  const prompt = createInterface({ input: process.stdin, output: process.stdout });
  try {
    for (const line of describe(facts)) {
      console.log(line);
    }
    const names = Object.keys(COMMANDS);
    console.log('');
    names.forEach((name, index) => {
      console.log(
        `  ${String(index + 1).padStart(2)}  ${name.padEnd(13)} ${COMMANDS[name]?.summary ?? ''}`,
      );
    });
    const choice = names[Number(await prompt.question('\nWhich one? ')) - 1];
    if (choice === undefined) {
      return 0;
    }
    const options: Options = {};
    const positionals: string[] = [];
    for (const ask of COMMANDS[choice]?.asks ?? []) {
      const fallback =
        ask.name === 'version'
          ? facts.productVersion
          : ask.name === 'minimum'
            ? DEFAULT_MINIMUM
            : '';
      const answer =
        (
          await prompt.question(`${ask.question}${fallback === '' ? '' : ` [${fallback}]`}: `)
        ).trim() || fallback;
      if (ask.name === 'file' || ask.name === 'folder' || ask.name === 'id') {
        positionals.push(answer);
      } else if (answer !== '') {
        options[ask.name] = answer;
      }
    }
    if (choice === 'update') {
      options.sign = /^y/i.test(await prompt.question('Sign it with the beta key? [y/N] '));
    }
    if (choice === 'status') {
      return 0;
    }
    if (choice === 'checklist') {
      for (const line of checklist(versionOf(options, facts), facts)) {
        console.log(line);
      }
      return 0;
    }
    const steps = plan(choice, positionals, options, facts);
    console.log(`\npithyx-release ${choice} would run:`);
    show(steps, facts.root);
    if (!/^y/i.test(await prompt.question('\nRun it? [y/N] '))) {
      return 0;
    }
    prompt.close();
    return run(steps, facts.root);
  } finally {
    prompt.close();
  }
}

export async function main(argv: string[], root: string): Promise<number> {
  const { values, positionals } = parseArgs({
    args: argv,
    options: OPTIONS,
    allowPositionals: true,
    strict: true,
  });
  const options = values as Options;
  const [command, ...rest] = positionals;
  const facts = readFacts(root);
  try {
    if (command === undefined) {
      if (process.stdin.isTTY) {
        return await menu(facts);
      }
      help();
      return 2;
    }
    if (command === 'help') {
      help();
      return 0;
    }
    if (command === 'status') {
      for (const line of describe(facts)) {
        console.log(line);
      }
      return 0;
    }
    if (command === 'checklist') {
      for (const line of checklist(versionOf(options, facts), facts)) {
        console.log(line);
      }
      return 0;
    }
    const steps = plan(command, rest, options, facts);
    if (options['dry-run'] === true) {
      show(steps, root);
      return 0;
    }
    return run(steps, root);
  } catch (error) {
    if (error instanceof PlanError) {
      console.error(`pithyx-release: ${error.message}`);
      return 1;
    }
    throw error;
  }
}

if (import.meta.url === `file://${process.argv[1] ?? ''}`) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
  process.exitCode = await main(process.argv.slice(2), root);
}
