import { defaultClientConditions, defineConfig } from 'vite';

export default defineConfig({
  // The desktop serves the app under /apps/demo/; relative asset paths keep it movable.
  base: './',
  resolve: { conditions: ['vyxis-source', ...defaultClientConditions] },
  build: { outDir: 'dist', emptyOutDir: true },
});
