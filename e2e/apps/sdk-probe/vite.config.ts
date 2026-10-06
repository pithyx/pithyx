import { defaultClientConditions, defineConfig } from 'vite';

export default defineConfig({
  // The box serves the frontend under /apps/<id>/; relative asset paths keep it movable.
  base: './',
  resolve: { conditions: ['pithyx-source', ...defaultClientConditions] },
  build: { outDir: 'dist/frontend', emptyOutDir: true },
});
