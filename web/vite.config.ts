import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The SPA calls the API with root-relative paths only (/work-orders, /system/*, …). In dev the
// browser makes same-origin requests to Vite, and Vite proxies the API's own routes to the API's
// origin — so there is no CORS policy to add, loosen, or forget to tighten. In production Epic 15
// puts the built bundle and the API behind one reverse proxy on a single hostname, where the same
// relative paths are same-origin for real; the bundle ships untouched.
//
// The API's origin is configurable so this file need not be edited per machine.
// The API's HTTP launch profile (see src/ArtificeWorks.Api/Properties/launchSettings.json). Run
// the API with that profile in dev — under HTTP-only it can't determine an HTTPS port, so
// UseHttpsRedirection stays quiet and the proxy reaches it without a 307 detour.
const apiTarget = process.env.VITE_API_TARGET ?? "http://localhost:5181";

// Every API route prefix the SPA touches. Anything not listed here is served by Vite as the SPA
// itself (index.html), which is what makes client-side routing work.
const apiPaths = ["/work-orders", "/products", "/system", "/hubs"];

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: Object.fromEntries(
      apiPaths.map((path) => [
        path,
        { target: apiTarget, changeOrigin: true, ws: true },
      ]),
    ),
  },
});
