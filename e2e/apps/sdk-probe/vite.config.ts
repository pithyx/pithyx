import pithyx from '@pithyx/cli/vite';
import { defaultClientConditions, defineConfig } from 'vite';

// `vite` runs the probe in the mock desktop of pithyx dev; `vite build` validates the build as a package.
export default defineConfig({
  plugins: [pithyx()],
  resolve: { conditions: ['pithyx-source', ...defaultClientConditions] },
  build: { outDir: 'dist/frontend', emptyOutDir: true },
});
