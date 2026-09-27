import optimizeLocales from '@react-aria/optimize-locales-plugin';
import babel from '@rolldown/plugin-babel';
import react, { reactCompilerPreset } from '@vitejs/plugin-react';
import {
  cpSync,
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { brotliCompressSync, constants, gzipSync } from 'node:zlib';
import { defaultClientConditions, defineConfig, type Plugin } from 'vite';
import { generateAll, writeDeclaration } from './scripts/css-module-types.ts';

const daemonUrl = process.env.VYXIS_DAEMON_URL ?? 'http://localhost:8994';

// Keeps the CSS Module declarations current while the dev server runs.
function cssModuleTypes(): Plugin {
  return {
    name: 'vyxis-css-module-types',
    apply: 'serve',
    buildStart() {
      generateAll();
    },
    handleHotUpdate({ file }) {
      if (file.endsWith('.module.css')) {
        writeDeclaration(file);
      }
    },
  };
}

// The dev server sends modules uncompressed (react-dom alone is about 3 MB), which is slow over a VPN such as
// Tailscale. Text responses are gzipped here; the daemon's API and WebSocket pass through untouched.
function devCompression(): Plugin {
  return {
    name: 'vyxis-dev-compression',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const url = request.url ?? '';
        if (
          !/\bgzip\b/.test(request.headers['accept-encoding'] ?? '') ||
          url.startsWith('/api') ||
          url.startsWith('/ws')
        ) {
          next();
          return;
        }
        const chunks: Buffer[] = [];
        const end = response.end.bind(response);
        const collect = (chunk: unknown) => {
          if (typeof chunk === 'string' || chunk instanceof Uint8Array) {
            chunks.push(Buffer.from(chunk));
          }
        };
        response.write = ((chunk: unknown) => {
          collect(chunk);
          return true;
        }) as typeof response.write;
        response.end = ((chunk?: unknown) => {
          collect(chunk);
          const body = Buffer.concat(chunks);
          const type = String(response.getHeader('content-type') ?? '');
          if (
            body.length < 1024 ||
            response.getHeader('content-encoding') !== undefined ||
            !/javascript|css|json|html|svg/.test(type)
          ) {
            return end(body);
          }
          const compressed = gzipSync(body, { level: 1 });
          response.setHeader('content-encoding', 'gzip');
          response.setHeader('content-length', compressed.length);
          response.setHeader('vary', 'Accept-Encoding');
          return end(compressed);
        }) as typeof response.end;
        next();
      });
    },
  };
}

// React Aria ships its own strings for about 30 locales; keep only the languages in src/i18n.ts.
const ariaLocales = ['en-US', 'de-DE'];

// Text files the daemon serves precompressed. Fonts (woff2) and images are compressed already.
const COMPRESSIBLE = /\.(js|css|html|svg|json|txt|map)$/;

/** Writes Brotli and gzip copies of every text file in the build, served by the daemon by Accept-Encoding. */
function precompress(): Plugin {
  let outDir = 'dist';
  const walk = (directory: string): string[] =>
    readdirSync(directory).flatMap((name) => {
      const path = join(directory, name);
      return statSync(path).isDirectory() ? walk(path) : [path];
    });
  return {
    name: 'vyxis-precompress',
    apply: 'build',
    configResolved(config) {
      outDir = config.build.outDir;
    },
    closeBundle() {
      for (const file of walk(outDir).filter((path) => COMPRESSIBLE.test(path))) {
        const content = readFileSync(file);
        if (content.length < 1024) {
          continue;
        }
        writeFileSync(
          `${file}.br`,
          brotliCompressSync(content, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } }),
        );
        writeFileSync(`${file}.gz`, gzipSync(content, { level: 9 }));
      }
    },
  };
}

// Sample third-party apps (apps/*) are built on their own and served by the daemon under /apps/<id>/.
const sampleApps = ['demo'];

/** Copies the built sample apps into the desktop build. */
function copySampleApps(): Plugin {
  let outDir = 'dist';
  return {
    name: 'vyxis-sample-apps',
    apply: 'build',
    configResolved(config) {
      outDir = config.build.outDir;
    },
    closeBundle() {
      for (const app of sampleApps) {
        const built = join(import.meta.dirname, '..', 'apps', app, 'dist');
        if (!existsSync(join(built, 'index.html'))) {
          throw new Error(
            `apps/${app} is not built; build it first (pnpm --filter "@vyxis/desktop..." build).`,
          );
        }
        cpSync(built, join(outDir, 'apps', app), { recursive: true });
      }
    },
  };
}

// Where the bundle budgets (tools/checks/bundle-budget.ts) find the chunk graph; outside dist, which the daemon serves.
export const bundleGraphFile = join(
  import.meta.dirname,
  'node_modules',
  '.cache',
  'bundle-graph.json',
);

/** Records every chunk with its static imports and CSS, keyed by the module it was built from. */
function bundleGraph(): Plugin {
  return {
    name: 'vyxis-bundle-graph',
    apply: 'build',
    writeBundle(_, bundle) {
      const chunks = Object.values(bundle).flatMap((output) =>
        output.type === 'chunk'
          ? [
              {
                file: output.fileName,
                module:
                  output.facadeModuleId === null
                    ? null
                    : relative(import.meta.dirname, output.facadeModuleId),
                isEntry: output.isEntry,
                imports: output.imports,
                css: [...(output.viteMetadata?.importedCss ?? [])],
              },
            ]
          : [],
      );
      mkdirSync(dirname(bundleGraphFile), { recursive: true });
      writeFileSync(bundleGraphFile, `${JSON.stringify(chunks, null, 2)}\n`);
    },
  };
}

export default defineConfig({
  plugins: [
    { ...optimizeLocales.vite({ locales: ariaLocales }), enforce: 'pre' },
    react(),
    babel({ presets: [reactCompilerPreset()] }),
    cssModuleTypes(),
    copySampleApps(),
    precompress(),
    bundleGraph(),
    devCompression(),
  ],
  // Workspace packages (the SDK) are compiled from source.
  resolve: { conditions: ['vyxis-source', ...defaultClientConditions] },
  build: {
    rollupOptions: {
      // Only the desktop ships. design.html is the developer-only design gallery served by `pnpm design`.
      input: 'index.html',
      output: {
        // Over HTTP/1.1 (no TLS yet) every extra request on the sign-in path costs a round trip. Everything the entry
        // needs goes into one chunk; the desktop and its shared code into another, prefetched by the sign-in screen.
        codeSplitting: {
          groups: [{ name: 'index', tags: ['$initial'], priority: 2 }],
        },
      },
    },
  },
  server: {
    // Transforms the sign-in path and the desktop before the first request, not while the browser waits.
    warmup: { clientFiles: ['./src/main.tsx', './src/desktop/Desktop.tsx'] },
    proxy: {
      '/api': { target: daemonUrl, changeOrigin: true },
      '/ws': {
        target: daemonUrl,
        changeOrigin: true,
        ws: true,
        // The daemon only accepts WebSocket connections from its own origin. In development the page comes from
        // the Vite server, so the proxy presents the daemon's origin instead.
        configure: (proxy) => {
          proxy.on('proxyReqWs', (proxyRequest) => {
            proxyRequest.setHeader('origin', new URL(daemonUrl).origin);
          });
        },
      },
    },
  },
});
