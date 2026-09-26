import babel from '@rolldown/plugin-babel';
import react, { reactCompilerPreset } from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vite';
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

export default defineConfig({
  plugins: [react(), babel({ presets: [reactCompilerPreset()] }), cssModuleTypes()],
  build: {
    rollupOptions: {
      // Only the desktop ships. design.html is the developer-only design gallery served by `pnpm design`.
      input: 'index.html',
    },
  },
  server: {
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
