import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// In development, /api is forwarded to the .NET API (run it on http://localhost:8080).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      "/api": { target: process.env.API_URL ?? "http://localhost:8080", changeOrigin: true },
      "/ws-python": { target: process.env.PYTHON_URL ?? "http://localhost:8000", ws: true, changeOrigin: true, rewrite: (path) => path.replace(/^\/ws-python/, "/ws") },
    },
  },
});
