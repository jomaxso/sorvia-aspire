import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const serverBaseUrl = process.env.SERVER_HTTPS || process.env.SERVER_HTTP;
const scalarBaseUrl = process.env.SCALAR_HTTPS || process.env.SCALAR_HTTP;

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: serverBaseUrl,
        changeOrigin: true
      },
      // Must be before '/docs' so it doesn't get caught by the broader pattern.
      // Must be before '/docs' so it doesn't get caught by the broader pattern.
      // Scalar uses /scalar-proxy internally to fetch the OpenAPI spec.
      '/scalar-proxy': {
        target: scalarBaseUrl,
        changeOrigin: true
      },
      // Strip /docs prefix so Scalar container receives requests at its root.
      // Browser stays on same origin, so all relative sub-resource URLs
      // (scalar.js, favicon, etc.) also hit this proxy entry.
      '/docs': {
        target: scalarBaseUrl,
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/docs/, '') || '/'
      }
    }
  }
});
