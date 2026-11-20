// 经验标定：60fps 定步长模型和真机 rAF 有差距，直接拿浏览器实测值反解参数。
// 用法：node scripts/calib.mjs
import { chromium } from "playwright";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const demo = pathToFileURL(path.join(root, "style-demo.html")).href;
const CHROME = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";

const browser = await chromium.launch({ executablePath: CHROME });
const page = await browser.newPage({ viewport: { width: 1680, height: 1000 } });
await page.goto(demo);
await page.waitForFunction(() => window.__winisland);

async function measurePair(params) {
  const res = await page.evaluate(async (p) => {
    const W = window.__winisland;
    W.spring(p);
    W.set({ scene: "media", theme: "obsidian", measure: false });
    W.set({ state: "compact" });
    W.settle();
    await new Promise((r) => setTimeout(r, 120));
    const ex = [];
    const co = [];
    for (let i = 0; i < 3; i++) {
      ex.push(await W.measure("expanded"));
      co.push(await W.measure("compact"));
    }
    const med = (a) => a.slice().sort((x, y) => x - y)[1];
    return { expand: med(ex), collapse: med(co), ex, co };
  }, params);
  return res;
}

const candidates = [];
for (const k of [226, 240, 250]) {
  for (const kCollapse of [1.75, 1.85, 1.95, 2.05]) {
    candidates.push({
      k,
      c: +(2 * 0.77 * Math.sqrt(k)).toFixed(1),
      kCollapse,
      cCollapse: +(1.3 * Math.sqrt(kCollapse / 1.55)).toFixed(2)
    });
  }
}

console.log("k\tc\tkColl\tcColl\t展开\t收起\t三态样本");
const rows = [];
for (const c of candidates) {
  const r = await measurePair(c);
  rows.push({ ...c, ...r });
  console.log(
    `${c.k}\t${c.c}\t${c.kCollapse}\t${c.cCollapse}\t${r.expand}\t${r.collapse}\t${r.ex.join("/")} | ${r.co.join("/")}`
  );
}

const ok = rows.filter((r) => r.expand >= 350 && r.expand <= 450 && r.collapse >= 250 && r.collapse <= 300);
ok.sort(
  (a, b) =>
    Math.abs(a.expand - 400) + Math.abs(a.collapse - 277) - (Math.abs(b.expand - 400) + Math.abs(b.collapse - 277))
);

console.log("\n落在规格内的组合（按离理想值排序）：");
for (const r of ok) {
  console.log(
    `  k=${r.k} c=${r.c} kCollapse=${r.kCollapse} cCollapse=${r.cCollapse} → 展开 ${r.expand}ms / 收起 ${r.collapse}ms`
  );
}
if (!ok.length) console.log("  无：需要继续扩大搜索范围");

await browser.close();