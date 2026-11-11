import { defineConfig } from 'vite';

// Relative base: the bundle is served from a virtual host folder inside the WebView2 host.
export default defineConfig({ base: './', build: { target: 'es2022' } });
