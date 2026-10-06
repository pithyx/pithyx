import babel from '@rolldown/plugin-babel';
import react, { reactCompilerPreset } from '@vitejs/plugin-react';
import { dirname, relative } from 'node:path/posix';
import { defineConfig, type Plugin } from 'vite';
import dts from 'vite-plugin-dts';

/**
 * A library build leaves its CSS beside the JavaScript without importing it. Each chunk imports its own CSS here, so an
 * app's bundler picks up the styles of exactly the components it uses.
 */
function importChunkCss(): Plugin {
  return {
    name: 'pithyx-import-chunk-css',
    apply: 'build',
    enforce: 'post',
    generateBundle(_, bundle) {
      for (const output of Object.values(bundle)) {
        if (output.type !== 'chunk') {
          continue;
        }
        const css = [...(output.viteMetadata?.importedCss ?? [])];
        const imports = css.map(
          (file) => `import './${relative(dirname(output.fileName), file)}';\n`,
        );
        output.code = `${imports.join('')}${output.code}`;
      }
    },
  };
}

export default defineConfig({
  plugins: [
    react(),
    babel({ presets: [reactCompilerPreset()] }),
    importChunkCss(),
    dts({ include: ['src'], exclude: ['src/**/*.test.ts'], entryRoot: 'src' }),
  ],
  build: {
    lib: {
      // The native entry holds the class names without React Aria, for code that has to stay small.
      entry: { index: 'src/index.ts', native: 'src/native.ts' },
      formats: ['es'],
    },
    cssCodeSplit: true,
    rollupOptions: {
      // Dependencies and peers stay imports; the app's bundler resolves them once for the whole app.
      external: (id) =>
        /^(react|react-dom|react-aria-components|@tabler\/icons-react)(\/|$)/.test(id),
      output: {
        assetFileNames: '[name][extname]',
        chunkFileNames: '[name].js',
        // The code both entries share (class names, cx, the texts) in one chunk with its CSS.
        codeSplitting: { groups: [{ name: 'shared', minShareCount: 2 }] },
      },
    },
  },
});
