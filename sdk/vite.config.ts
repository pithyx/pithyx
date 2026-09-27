import { defineConfig } from 'vite';
import dts from 'vite-plugin-dts';

export default defineConfig({
  plugins: [dts({ include: ['src'], exclude: ['src/**/*.test.ts'] })],
  build: {
    lib: {
      // The host side is its own entry, so apps never bundle the desktop's half of the protocol.
      entry: { index: 'src/index.ts', host: 'src/host.ts' },
      formats: ['es'],
    },
  },
});
