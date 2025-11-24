module.exports = {
  preset: 'ts-jest',
  testEnvironment: 'node',
  roots: ['<rootDir>/scripts', '<rootDir>/src/security', '<rootDir>/src/ai', '<rootDir>/src/fhir'],
  testMatch: ['**/__tests__/**/*.ts', '**/?(*.)+(spec|test).ts'],
  transform: {
    '^.+\\.ts$': 'ts-jest',
  },
  collectCoverageFrom: [
    'scripts/**/*.ts',
    'core/**/*.ts',
    'src/security/**/*.ts',
    'src/ai/**/*.ts',
    'src/fhir/**/*.ts',
    '!**/*.test.ts',
    '!**/node_modules/**',
    '!**/dist/**',
    '!**/examples.ts',
    '!**/secureExamples.ts',
    '!**/provider-access-examples.ts',
    '!**/cli/**',
    '!**/utils/template-helpers.ts',
  ],
  coverageThreshold: {
    global: {
      branches: 77,
      functions: 80,
      lines: 80,
      statements: 80,
    },
  },
  moduleFileExtensions: ['ts', 'tsx', 'js', 'jsx', 'json', 'node'],
};
