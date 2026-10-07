import { defaultClientConditions, defineConfig } from 'vite';

// The mock desktop of `pithyx dev`, a plain browser page with the SDK's host side and the tokens of @pithyx/ui
// bundled in, served by the dev server from dist/shell.
export default defineConfig({
  root: 'shell',
  base: '/__pithyx/',
  resolve: { conditions: ['pithyx-source', ...defaultClientConditions] },
  build: { outDir: '../dist/shell', emptyOutDir: true, target: 'es2023' },
});
