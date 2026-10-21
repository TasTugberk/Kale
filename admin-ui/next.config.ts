import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const nextConfig: NextConfig = {
  // A self-contained server for the container image (node server.js), without node_modules.
  output: "standalone",
  poweredByHeader: false,
  // No image optimization service: the UI has no images worth it, and isolated networks have no CDN.
  images: { unoptimized: true },
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          // The admin UI must never be framed by another site (clickjacking).
          { key: "X-Frame-Options", value: "DENY" },
          { key: "Content-Security-Policy", value: "frame-ancestors 'none'" },
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "same-origin" },
        ],
      },
    ];
  },
};

// Messages come from src/i18n/request.ts (Turkish by default, English).
export default createNextIntlPlugin()(nextConfig);
