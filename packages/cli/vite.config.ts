import { builtinModules } from 'node:module';
import { defineConfig } from 'vite';
import dts from 'vite-plugin-dts';

export default defineConfig({
  plugins: [dts({ include: ['src'], exclude: ['src/**/*.test.ts'] })],
  build: {
    target: 'node24',
    lib: {
      entry: { index: 'src/index.ts', cli: 'src/cli.ts' },
      formats: ['es'],
    },
    rollupOptions: {
      external: [/^node:/, ...builtinModules, 'ajv', /^ajv\//, 'saxes'],
    },
  },
});
