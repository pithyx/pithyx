/**
 * `@pithyx/cli/vite`: the Vite plugin for Pithyx apps.
 *
 * ```ts
 * import { defineConfig } from 'vite';
 * import pithyx from '@pithyx/cli/vite';
 *
 * export default defineConfig({ plugins: [pithyx()] });
 * ```
 *
 * `vite` then serves the app under `/apps/<id>/` with the box's headers and the mock desktop of `pithyx dev` at `/`,
 * with the proxy to the backend; `vite build` writes a frontend the box can serve (relative asset paths) and runs
 * `pithyx validate` on it.
 *
 * @packageDocumentation
 */
import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import type { IncomingMessage } from 'node:http';
import { join, relative, resolve } from 'node:path';
import type { Duplex } from 'node:stream';
import type { AppManifest } from '@pithyx/sdk';
import type { Plugin, ResolvedConfig } from 'vite';
import { createDevServer } from './dev/server';
import { devBackend, type BackendOptions } from './dev/start';
import { describe } from './findings';
import { MANIFEST_FILE } from './layout';
import { validateManifest } from './manifest';
import { readProject } from './project';

export interface PithyxPluginOptions extends BackendOptions {
  /** The folder with `pithyx.json` and `icon.svg`; Vite's `root` by default. */
  project?: string;
  /** Runs `pithyx validate` after `vite build` and fails the build on an error; default true. */
  validate?: boolean;
  /**
   * Accepts an app id in `org.pithyx.*` (`--allow-reserved-id`), for the apps of the official catalog; a box refuses
   * it in a package from a file. Default false.
   */
  allowReservedId?: boolean;
}

function readManifest(project: string, allowReservedId: boolean): AppManifest {
  const validation = validateManifest(readFileSync(join(project, MANIFEST_FILE)), {
    allowReservedId,
  });
  if (validation.manifest === undefined) {
    throw new Error(
      `${MANIFEST_FILE} is not valid:\n${validation.issues.map((issue) => `  ${describe(issue)}`).join('\n')}`,
    );
  }
  return validation.manifest;
}

/**
 * Wires a Vite project to Pithyx. In `vite` (serve) the base becomes `/apps/<id>/`, every answer there gets the box's
 * `Content-Security-Policy` (scripts Vite injects carry a nonce, the only difference to the box), and the mock
 * desktop, its API and the backend proxy join the dev server. In `vite build` the base is `./` unless set, and the
 * output is validated as a package.
 */
export default function pithyx(options: PithyxPluginOptions = {}): Plugin {
  const scriptNonce = randomBytes(16).toString('base64url');
  let config: ResolvedConfig | undefined;
  let project = '';
  return {
    name: 'pithyx',
    config(user, env) {
      project = resolve(options.project ?? user.root ?? process.cwd());
      if (env.command === 'serve') {
        const manifest = readManifest(project, options.allowReservedId === true);
        return { base: `/apps/${manifest.id}/`, html: { cspNonce: scriptNonce } };
      }
      return user.base === undefined ? { base: './' } : {};
    },
    configResolved(resolved) {
      config = resolved;
    },
    async configureServer(server) {
      const manifest = readManifest(project, options.allowReservedId === true);
      const backend = await devBackend(project, manifest, options);
      const dev = createDevServer({
        manifest,
        frontend: { kind: 'vite', scriptNonce },
        endpoints: () => backend.endpoints,
        icon: join(project, 'icon.svg'),
      });
      server.middlewares.use((req, res, next) => {
        dev.handle(req, res, next);
      });
      server.httpServer?.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
        dev.upgrade(req, socket, head);
      });
      server.httpServer?.once('close', () => {
        void backend.stop();
      });
      server.httpServer?.once('listening', () => {
        const address = server.httpServer?.address();
        if (address !== null && typeof address === 'object') {
          server.config.logger.info(
            `  Pithyx mock desktop: http://localhost:${String(address.port)}/`,
          );
        }
      });
    },
    closeBundle() {
      if (config?.command !== 'build' || options.validate === false) {
        return;
      }
      const outDir = resolve(config.root, config.build.outDir);
      const result = readProject(project, {
        frontend: relative(project, outDir),
        allowUnbuiltBackend: true,
        allowReservedId: options.allowReservedId === true,
      });
      for (const finding of result.findings) {
        const line = `${finding.level} ${finding.code}: ${describe(finding)}`;
        if (finding.level === 'error') {
          config.logger.error(line);
        } else {
          config.logger.warn(line);
        }
      }
      if (result.findings.some((finding) => finding.level === 'error')) {
        throw new Error('pithyx validate found errors; the box would refuse this package');
      }
    },
  };
}
