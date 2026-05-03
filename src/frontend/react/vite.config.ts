import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";
import basicSsl from "@vitejs/plugin-basic-ssl";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const gatewayTarget =
    env.VITE_GATEWAY_TARGET || "http://localhost:5152";

  return {
    plugins: [react(), basicSsl()],
    test: {
      environment: "jsdom",
      setupFiles: "./src/test/setup.ts",
      globals: true,
    },
    server: {
      port: 5173,
      proxy: {
        "/api": {
          target: gatewayTarget,
          changeOrigin: true,
        },
        "/health": {
          target: gatewayTarget,
          changeOrigin: true,
        },
      },
    },
  };
});
