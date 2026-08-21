import { defineConfig } from "vite";
import tailwindcss from "@tailwindcss/vite";

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
      "/health": "http://127.0.0.1:5081",
      "/campaigns": "http://127.0.0.1:5081",
    },
  },
});
