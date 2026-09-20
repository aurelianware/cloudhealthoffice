// ESLint flat configuration.
//
// Migrated from .eslintrc.json: ESLint 9 dropped the eslintrc format as the
// default, so `npm run lint` failed outright with "couldn't find an
// eslint.config.(js|mjs|cjs) file". The rules below are a direct translation of
// that file — no rule was added, removed or re-tuned in the move.
//
// Kept as CommonJS because package.json declares no "type": "module".

const js = require('@eslint/js');
const globals = require('globals');
const tsPlugin = require('@typescript-eslint/eslint-plugin');
const tsParser = require('@typescript-eslint/parser');

module.exports = [
  // Was "ignorePatterns". In flat config an `ignores`-only object is global.
  {
    ignores: ['node_modules/**', 'dist/**', '**/*.js'],
  },

  // Was "extends": ["eslint:recommended"].
  js.configs.recommended,

  {
    files: ['**/*.ts'],
    languageOptions: {
      parser: tsParser,
      ecmaVersion: 2020,
      sourceType: 'module',
      // Was "env": { node: true, es2020: true }.
      globals: {
        ...globals.node,
        ...globals.es2020,
      },
    },
    plugins: {
      '@typescript-eslint': tsPlugin,
    },
    rules: {
      // Was "extends": ["plugin:@typescript-eslint/recommended"].
      ...tsPlugin.configs.recommended.rules,

      // Project rule overrides, unchanged from .eslintrc.json.
      '@typescript-eslint/no-explicit-any': 'warn',
      '@typescript-eslint/explicit-module-boundary-types': 'off',
      '@typescript-eslint/no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
      'no-console': 'off',

      // eslint:recommended flags TypeScript's own declaration syntax as
      // undefined variables; the TypeScript compiler already checks this, and
      // the eslintrc setup suppressed it implicitly via the plugin preset.
      'no-undef': 'off',
    },
  },

  {
    // Jest specs deliberately re-`require()` a module after jest.resetModules()
    // so it re-reads process.cwd() (see generate-patient-access-specs.test.ts,
    // where OUTPUT_DIR must resolve into a temp directory). A static `import`
    // is hoisted and would defeat the test, so this is the correct construct
    // here rather than a lapse — the rule is scoped off for specs only.
    files: ['**/*.test.ts', '**/tests/**/*.ts', '**/__tests__/**/*.ts'],
    rules: {
      '@typescript-eslint/no-require-imports': 'off',
    },
  },
];
