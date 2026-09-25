import { defineConfig } from 'vite';

export default defineConfig({
  // relative Pfade, damit die App unter https://<user>.github.io/<repo>/ läuft
  base: './',
  build: {
    target: 'es2020',
    assetsInlineLimit: 0,
  },
  test: {
    include: ['tests/unit/**/*.test.js'],
  },
});
