import js from '@eslint/js';
import i18next from 'eslint-plugin-i18next';
import reactHooks from 'eslint-plugin-react-hooks';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// The pre-commit hook sets PITHYX_ESLINT_FAST=1 to skip type-aware rules and stay under 5 s.
// CI and `pnpm lint` always run the full type-checked rule set.
const fast = process.env.PITHYX_ESLINT_FAST === '1';

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
  globalIgnores([
    '**/dist/',
    '**/generated/',
    '**/.css-types/',
    'daemon/',
    '**/node_modules/',
    '.worktrees/',
  ]),
  js.configs.recommended,
  ...typescriptConfigs,
  {
    files: [
      '*.js',
      'tools/**/*.ts',
      'desktop/scripts/**/*.ts',
      'sdk/scripts/**/*.ts',
      'packages/cli/src/**/*.ts',
      '**/vite.config.ts',
    ],
    languageOptions: { globals: globals.node },
  },
  {
    files: ['desktop/src/**/*.{ts,tsx}', 'packages/ui/src/**/*.{ts,tsx}'],
    extends: [reactHooks.configs.flat['recommended-latest']],
    languageOptions: { globals: globals.browser },
  },
  {
    // Icons come from icons.ts (desktop/src/ui/ and packages/ui/src/), one module per icon; the package index pulls in
    // all of them.
    files: ['desktop/src/**/*.{ts,tsx}', 'packages/ui/src/**/*.{ts,tsx}'],
    ignores: [
      'desktop/src/ui/icons.ts',
      'desktop/src/tabler-icons.d.ts',
      'packages/ui/src/icons.ts',
      'packages/ui/src/tabler-icons.d.ts',
    ],
    rules: {
      '@typescript-eslint/no-restricted-imports': [
        'error',
        {
          paths: [
            {
              name: '@tabler/icons-react',
              message: 'Import icons from icons.ts (add new ones there as single-icon imports).',
              allowTypeImports: true,
            },
          ],
          patterns: [
            {
              group: ['@tabler/icons-react/*'],
              message: 'Single-icon imports belong in icons.ts.',
            },
          ],
        },
      ],
    },
  },
  {
    // Pithyx works without a secure context (plain HTTP by IP, an untrusted certificate). APIs that need one are
    // reached only through desktop/src/platform/browser.ts, which falls back or reports that they are missing.
    files: [
      'desktop/src/**/*.{ts,tsx}',
      'sdk/src/**/*.ts',
      'packages/ui/src/**/*.{ts,tsx}',
      'e2e/apps/*/src/**/*.ts',
    ],
    ignores: ['desktop/src/platform/browser.ts', '**/*.test.{ts,tsx}'],
    rules: {
      'no-restricted-properties': [
        'error',
        ...[
          ['crypto', 'randomUUID'],
          ['crypto', 'subtle'],
          ['navigator', 'clipboard'],
          ['navigator', 'serviceWorker'],
          ['navigator', 'credentials'],
          ['navigator', 'storage'],
          ['navigator', 'locks'],
          ['navigator', 'share'],
          ['navigator', 'mediaDevices'],
          ['document', 'execCommand'],
          ['window', 'isSecureContext'],
          ['window', 'crypto'],
          ['window', 'navigator'],
          ['globalThis', 'crypto'],
          ['globalThis', 'navigator'],
        ].map(([object, property]) => ({
          object,
          property,
          message: 'Needs a secure context; use desktop/src/platform/browser.ts.',
        })),
        {
          object: 'window',
          property: 'open',
          message:
            'A new tab must not reach the desktop; use openInNewTab in desktop/src/platform/browser.ts.',
        },
      ],
      'no-restricted-globals': [
        'error',
        ...[
          'Notification',
          'PublicKeyCredential',
          'isSecureContext',
          'showSaveFilePicker',
          'showOpenFilePicker',
        ].map((name) => ({
          name,
          message: 'Needs a secure context; use desktop/src/platform/browser.ts.',
        })),
        {
          name: 'open',
          message:
            'A new tab must not reach the desktop; use openInNewTab in desktop/src/platform/browser.ts.',
        },
      ],
    },
  },
  {
    files: ['desktop/src/**/*.tsx', 'packages/ui/src/**/*.tsx'],
    ignores: ['**/*.test.tsx'],
    plugins: { i18next },
    rules: {
      'i18next/no-literal-string': ['error', { mode: 'jsx-text-only' }],
    },
  },
);
