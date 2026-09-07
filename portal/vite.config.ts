import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";

// Portal +351 Monitor — dev server na porta 5173 com proxy /api -> API ASP.NET Core (http://localhost:5080).
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  build: {
    // O chunk do ECharts (core + zrender, já modular: Bar/Line/Pie e 6
    // components) fica em ~589 kB min / ~202 kB gzip - acima do aviso default
    // de 500 kB, mas é um vendor chunk único e cacheável. Limite documentado.
    // O Line e o Legend entraram em 07/09/2026: sem eles o ECharts descartava
    // em silêncio a série do cartão de atividade por hora e a legenda da
    // composição por dia da pessoa (ver o comentário em charts/EChart.tsx).
    chunkSizeWarningLimit: 600,
    rollupOptions: {
      output: {
        // ECharts (e o zrender que ele embute) em chunk próprio: cacheia
        // separado do código do app e mantém o chunk principal pequeno.
        manualChunks: {
          echarts: ["echarts/core", "echarts/charts", "echarts/components", "echarts/renderers"],
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: "http://localhost:5080",
        changeOrigin: false,
      },
    },
  },
});
