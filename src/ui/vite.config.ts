import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

// Relative base: the bundle is served from a virtual host folder inside the WebView2 host.
export default defineConfig({
  base: './',
  build: { target: 'es2022' },
  resolve: {
    alias: {
      // Monaco's package "exports" map hides its CSS and internals; we import the pieces we want directly.
      // See src/monaco.ts. The version is pinned in package.json because these are internal paths.
      '@monaco': fileURLToPath(new URL('./node_modules/monaco-editor/esm/vs', import.meta.url)),
    },
  },
});
