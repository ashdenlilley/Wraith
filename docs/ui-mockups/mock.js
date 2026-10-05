// Shared fake data so every design shows the same app state.
const Mock = {
  gpu: "AMD Radeon RX 6900 XT", vram: 16, backend: "Vulkan", endpoint: "http://127.0.0.1:8080/v1",
  model: { name: "Qwen2.5 Coder 14B", quant: "Q5_K_M", id: "qwen2.5-coder-14b-q5km" },
  models: [
    { name: "Qwen2.5 Coder 14B", quant: "Q5_K_M", size: 10.5, vram: 11.3, verdict: "ok", def: true },
    { name: "DeepSeek R1 Distill 14B", quant: "Q4_K_M", size: 9.0, vram: 10.1, verdict: "ok" },
    { name: "Gemma 3 12B", quant: "Q5_K_M", size: 8.4, vram: 9.6, verdict: "ok" },
    { name: "Llama 3.3 70B", quant: "Q4_K_M", size: 42.5, vram: 46.0, verdict: "no" },
  ],
  history: [
    { when: "Today 14:02", gen: 48.1, pp: 1842, w: 286, t: 63, label: "UV -75mV" },
    { when: "Today 13:20", gen: 45.2, pp: 1790, w: 310, t: 69, label: "stock" },
    { when: "Yesterday", gen: 44.8, pp: 1775, w: 312, t: 70, label: "stock" },
  ],
  t: 0, running: false, series: [],
  tick() {
    this.t++;
    const on = this.running;
    const j = (b, a) => b + Math.sin(this.t / 3) * a + (Math.random() - .5) * a;
    const s = on ? {
      util: Math.min(100, j(96, 3)), vramUsed: j(11.3, .15), tps: j(48, 2.5), power: j(285, 8), temp: j(63, 1.2),
      hot: j(73, 1.5), clk: j(2250, 30), mclk: 2150, ttft: j(312, 30), pp: j(1850, 80)
    } : { util: 2, vramUsed: 1.1, tps: 0, power: 22, temp: 38, hot: 41, clk: 400, mclk: 200, ttft: 0, pp: 0 };
    this.series.push(s); if (this.series.length > 60) this.series.shift();
    return s;
  },
  spark(key, w = 160, h = 36, color = "currentColor") {
    const v = this.series.map(s => s[key]); if (v.length < 2) return "";
    const lo = Math.min(...v), hi = Math.max(...v, lo + 1e-6);
    const pts = v.map((y, i) => `${(i / 59 * w).toFixed(1)},${(h - 2 - (y - lo) / (hi - lo) * (h - 4)).toFixed(1)}`).join(" ");
    return `<svg width="${w}" height="${h}" viewBox="0 0 ${w} ${h}"><polyline fill="none" stroke="${color}" stroke-width="1.5" points="${pts}"/></svg>`;
  },
  start(cb) { setInterval(() => cb(this.tick()), 1000); cb(this.tick()); },
  chat: [["user", "Write a C# function that parses this JSON into a record."],
         ["assistant", "Here is a minimal approach using System.Text.Json:\n\npublic record Item(string Name, int Qty);\nvar item = JsonSerializer.Deserialize<Item>(json);"]],
};
