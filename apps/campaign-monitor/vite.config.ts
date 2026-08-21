import { defineConfig } from "vite";
import tailwindcss from "@tailwindcss/vite";
import { readFileSync } from "node:fs";

const serviceToken = () => readFileSync(
  new URL("../../.run/campaign-service/service.token", import.meta.url),
  "utf8",
).trim();

const serviceProxy = () => ({
  target: "http://127.0.0.1:5081",
  configure(proxy: { on: (event: string, handler: (request: { setHeader: (name: string, value: string) => void }) => void) => void }) {
    proxy.on("proxyReq", request => request.setHeader("Authorization", `Bearer ${serviceToken()}`));
  },
});

export default defineConfig({
  base: "/monitor/",
  plugins: [tailwindcss()],
  build: {
    outDir: "../../src/CrimeanTom.Campaign.ServiceHost/wwwroot/monitor",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      "/health": serviceProxy(),
      "/campaigns": serviceProxy(),
      "/control": serviceProxy(),
      "/content": serviceProxy(),
      "/durability-policy": serviceProxy(),
    },
  },
});
