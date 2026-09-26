import babel from '@rolldown/plugin-babel';
import react, { reactCompilerPreset } from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const daemonUrl = process.env.VYXIS_DAEMON_URL ?? 'http://localhost:8994';

export default defineConfig({
  plugins: [react(), babel({ presets: [reactCompilerPreset()] })],
  server: {
    proxy: {
      '/api': { target: daemonUrl, changeOrigin: true },
      '/ws': { target: daemonUrl, changeOrigin: true, ws: true },
    },
  },
});
