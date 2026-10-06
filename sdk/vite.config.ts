import { defineConfig } from 'vite';
import dts from 'vite-plugin-dts';

export default defineConfig({
  plugins: [dts({ include: ['src'], exclude: ['src/**/*.test.ts'], entryRoot: 'src' })],
  build: {
    lib: {
      // The host side is its own entry, so apps never bundle the desktop's half of the protocol; the i18n helper is
      // its own entry, so apps without it never bundle a message formatter.
      entry: { index: 'src/index.ts', host: 'src/host.ts', i18n: 'src/i18n.ts' },
      formats: ['es'],
    },
    rollupOptions: { external: ['intl-messageformat'] },
  },
});
