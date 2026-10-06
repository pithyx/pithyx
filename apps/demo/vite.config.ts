import { defaultClientConditions, defineConfig } from 'vite';

export default defineConfig({
  // The desktop serves the app under /apps/org.pithyx.demo/; relative asset paths keep it movable.
  base: './',
  resolve: { conditions: ['pithyx-source', ...defaultClientConditions] },
  build: { outDir: 'dist', emptyOutDir: true },
});
