// Writes desktop/public/third-party-packages.json and the license texts it links, for the About page.
// Lists the npm packages the desktop bundles and the NuGet packages the daemon ships. Needs node_modules and a
// restored daemon (dotnet restore); the repo check only compares the recorded input hash with the lock files.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const outputPath = join(root, 'desktop/public/third-party-packages.json');
const textFolder = join(root, 'desktop/public/licenses/generated');
const daemonProject = 'daemon/src/Pithyx.Daemon/Pithyx.Daemon.csproj';

// Listed by hand in desktop/public/third-party-notices.json with their own license files.
const listedByHand = new Set([
  '@tabler/icons-react',
  'react-aria-components',
  'pdfjs-dist',
  'SkiaSharp',
]);
// Build-time only, or excluded from the daemon's assets.
const notShipped = new Set([
  'Microsoft.Extensions.ApiDescription.Server',
  'SkiaSharp.NativeAssets.macOS',
  'SkiaSharp.NativeAssets.Win32',
]);

// Packages that ship no license file get the standard text of their license; MIT also names the copyright holder.
const standardLicenses = ['Apache-2.0', 'MIT'];

function standardText(license: string, holder: string): string | undefined {
  if (!standardLicenses.includes(license)) {
    return undefined;
  }
  const copyright = /^(copyright|\(c\)|©)/i.test(holder) ? holder : `Copyright ${holder}`;
  const text = readFileSync(join(root, `tools/licenses/texts/${license}.txt`), 'utf8');
  return text.includes('{copyright}')
    ? text.replace('{copyright}', copyright)
    : `${copyright}\n\n${text}`;
}

export interface Entry {
  name: string;
  version: string;
  license: string;
  source: string;
  licenseFile?: string;
}

export function inputsHash(): string {
  const files = [
    'pnpm-lock.yaml',
    'daemon/Directory.Packages.props',
    daemonProject,
    'tools/licenses/generate-notices.ts',
    ...standardLicenses.map((id) => `tools/licenses/texts/${id}.txt`),
  ];
  const hash = createHash('sha256');
  for (const file of files) {
    hash.update(`${file}\n`).update(readFileSync(join(root, file)));
  }
  return hash.digest('hex');
}

function run(command: string, args: string[]): string {
  return execFileSync(command, args, { cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });
}

function findLicenseText(folder: string): string | undefined {
  if (!existsSync(folder)) {
    return undefined;
  }
  const name = readdirSync(folder).find((file) =>
    /^(licen[sc]e|copying)(\.(txt|md))?$/i.test(file),
  );
  return name === undefined ? undefined : readFileSync(join(folder, name), 'utf8');
}

interface NpmLicense {
  name: string;
  versions: string[];
  paths: string[];
  license: string;
  author?: string;
  homepage?: string;
}

function npmEntries(): { entry: Entry; text?: string | undefined }[] {
  const found = new Map<string, { entry: Entry; text?: string | undefined }>();
  const json = run('pnpm', ['--filter', '@pithyx/desktop', 'licenses', 'list', '--prod', '--json']);
  const groups = JSON.parse(json) as Record<string, NpmLicense[]>;
  for (const packages of Object.values(groups)) {
    for (const pkg of packages) {
      if (pkg.name.startsWith('@pithyx/') || listedByHand.has(pkg.name)) {
        continue;
      }
      const version = pkg.versions[0] ?? '';
      found.set(`${pkg.name}@${version}`, {
        entry: {
          name: pkg.name,
          version,
          license: pkg.license,
          source: pkg.homepage ?? `https://www.npmjs.com/package/${pkg.name}`,
        },
        text:
          findLicenseText(pkg.paths[0] ?? '') ??
          standardText(pkg.license, pkg.author ?? `the ${pkg.name} authors`),
      });
    }
  }
  return [...found.values()];
}

function nugetEntries(): { entry: Entry; text?: string | undefined }[] {
  const json = JSON.parse(
    run('dotnet', ['list', daemonProject, 'package', '--include-transitive', '--format', 'json']),
  ) as {
    projects: { frameworks: { topLevelPackages?: Package[]; transitivePackages?: Package[] }[] }[];
  };
  interface Package {
    id: string;
    resolvedVersion: string;
  }
  const packages = new Map<string, Package>();
  for (const framework of json.projects.flatMap((project) => project.frameworks)) {
    for (const pkg of [
      ...(framework.topLevelPackages ?? []),
      ...(framework.transitivePackages ?? []),
    ]) {
      packages.set(pkg.id, pkg);
    }
  }
  const cache = process.env.NUGET_PACKAGES ?? join(homedir(), '.nuget/packages');
  const result: { entry: Entry; text?: string | undefined }[] = [];
  for (const pkg of [...packages.values()].sort((a, b) => a.id.localeCompare(b.id))) {
    if (notShipped.has(pkg.id) || listedByHand.has(pkg.id)) {
      continue;
    }
    const folder = join(cache, pkg.id.toLowerCase(), pkg.resolvedVersion);
    const nuspec = readFileSync(join(folder, `${pkg.id.toLowerCase()}.nuspec`), 'utf8');
    const expression = /<license type="expression">([^<]+)<\/license>/.exec(nuspec)?.[1];
    const licenseFile = /<license type="file">([^<]+)<\/license>/.exec(nuspec)?.[1];
    const project = /<projectUrl>([^<]+)<\/projectUrl>/.exec(nuspec)?.[1];
    const holder =
      /<copyright>([^<]+)<\/copyright>/.exec(nuspec)?.[1] ??
      /<authors>([^<]+)<\/authors>/.exec(nuspec)?.[1] ??
      `the ${pkg.id} authors`;
    const shipped =
      licenseFile !== undefined && existsSync(join(folder, licenseFile))
        ? readFileSync(join(folder, licenseFile), 'utf8')
        : findLicenseText(folder);
    const fromText = shipped?.trimStart().startsWith('MIT License') === true ? 'MIT' : undefined;
    const license = expression ?? fromText ?? 'unknown';
    result.push({
      entry: {
        name: pkg.id,
        version: pkg.resolvedVersion,
        license,
        source: project ?? `https://www.nuget.org/packages/${pkg.id}/${pkg.resolvedVersion}`,
      },
      text: shipped ?? standardText(license, holder.trim()),
    });
  }
  return result;
}

export function generate(): { json: string; texts: Map<string, string> } {
  const texts = new Map<string, string>();
  const entries: Entry[] = [];
  for (const { entry, text } of [
    ...npmEntries().sort((a, b) => a.entry.name.localeCompare(b.entry.name)),
    ...nugetEntries(),
  ]) {
    if (text !== undefined) {
      const name = `${createHash('sha256').update(text).digest('hex').slice(0, 12)}.txt`;
      texts.set(name, text);
      entry.licenseFile = `licenses/generated/${name}`;
    }
    entries.push(entry);
  }
  return { json: `${JSON.stringify({ inputsHash: inputsHash(), entries }, null, 2)}\n`, texts };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { json, texts } = generate();
  rmSync(textFolder, { recursive: true, force: true });
  mkdirSync(textFolder, { recursive: true });
  for (const [name, text] of texts) {
    writeFileSync(join(textFolder, name), text);
  }
  writeFileSync(outputPath, json);
}
