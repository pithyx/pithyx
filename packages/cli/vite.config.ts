import { builtinModules } from 'node:module';
import { defineConfig } from 'vite';
import dts from 'vite-plugin-dts';

// The Node side: the library, the command and the Vite plugin. The mock desktop is a browser build of its own
// (vite.shell.config.ts) into dist/shell.
export default defineConfig({
  plugins: [dts({ include: ['src'], exclude: ['src/**/*.test.ts'], entryRoot: 'src' })],
  build: {
    target: 'node22',
    lib: {
      entry: { index: 'src/index.ts', cli: 'src/cli.ts', vite: 'src/vite.ts' },
      formats: ['es'],
    },
    rollupOptions: {
      external: [
        /^node:/,
        ...builtinModules,
        'ajv',
        /^ajv\//,
        'intl-messageformat',
        'saxes',
        'yaml',
        'vite',
      ],
    },
  },
});
