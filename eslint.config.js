import js from '@eslint/js';
import i18next from 'eslint-plugin-i18next';
import reactHooks from 'eslint-plugin-react-hooks';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// The pre-commit hook sets VYXIS_ESLINT_FAST=1 to skip type-aware rules and stay under 5 s.
// CI and `pnpm lint` always run the full type-checked rule set.
const fast = process.env.VYXIS_ESLINT_FAST === '1';

const typescriptConfigs = fast
  ? [
      tseslint.configs.strict,
      tseslint.configs.stylistic,
      // Disable comments for type-aware rules look unused when those rules are off.
      { linterOptions: { reportUnusedDisableDirectives: 'off' } },
    ]
  : [
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      {
        languageOptions: {
          parserOptions: {
            projectService: {
              allowDefaultProject: ['*.js'],
            },
            tsconfigRootDir: import.meta.dirname,
          },
        },
      },
    ];

export default defineConfig(
  globalIgnores(['**/dist/', '**/generated/', 'daemon/', '**/node_modules/']),
  js.configs.recommended,
  ...typescriptConfigs,
  {
    files: ['*.js', 'tools/**/*.ts', '**/vite.config.ts'],
    languageOptions: { globals: globals.node },
  },
  {
    files: ['desktop/src/**/*.{ts,tsx}'],
    extends: [reactHooks.configs.flat['recommended-latest']],
    languageOptions: { globals: globals.browser },
  },
  {
    files: ['desktop/src/**/*.tsx'],
    ignores: ['**/*.test.tsx'],
    plugins: { i18next },
    rules: {
      'i18next/no-literal-string': ['error', { mode: 'jsx-text-only' }],
    },
  },
);
