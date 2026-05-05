/** @type {import('next').NextConfig} */
const nextConfig = {
  output: 'standalone',   // Required for Electron packaging — produces a self-contained server.js

  // Disable Turbopack for production: Next.js 16.2.4 Turbopack emits a TDZ
  // ('Cannot access X before initialization') in minified chunks which crashes
  // the React runtime in Electron.  Webpack produces correct output.
  bundlePagesRouterDependencies: true,
  experimental: {
    turbo: {
      rules: {},   // empty — keep dev HMR fast but let `next build` use webpack
    },
  },
};

export default nextConfig;

