// 样式截图：出「三形态 × 主题 × 场景」的证据图，并顺手做一遍几何断言
// 用法：node scripts/shots.mjs
import { chromium } from "playwright";
import { fileURLToPath, pathToFileURL } from "node:url";
import { mkdirSync } from "node:fs";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const out = path.join(root, "shots");
mkdirSync(out, { recursive: true });

const demo = pathToFileURL(path.join(root, "style-demo.html")).href;
const board = pathToFileURL(path.join(root, "style-board.html")).href;

// 本机没有 playwright 自带浏览器，直接用系统 Chrome
const CHROME = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";
const browser = await chromium.launch({ executablePath: CHROME });
const ctx = await browser.newContext({
  viewport: { width: 1680, height: 1000 },
  deviceScaleFactor: 2,
  reducedMotion: "no-preference"
});
const page = await ctx.newPage();

const fails = [];
function check(name, actual, expected) {
  const ok = JSON.stringify(actual) === JSON.stringify(expected);
  console.log(`${ok ? "  ok " : "FAIL "} ${name} → ${JSON.stringify(actual)}${ok ? "" : ` (期望 ${JSON.stringify(expected)})`}`);
  if (!ok) fails.push(name);
}

async function islandBox() {
  return page.locator("#island").boundingBox();
}

// 让岛周围留出一点壁纸，方便判断阴影和描边
async function shotIsland(file, pad = 26) {
  const b = await islandBox();
  await page.screenshot({
    path: path.join(out, file),
    clip: { x: b.x - pad, y: Math.max(0, b.y - pad), width: b.width + pad * 2, height: b.height + pad * 2 }
  });
}

await page.goto(demo);
await page.waitForFunction(() => window.__winisland);

// ---------------------------------------------------------------- 三形态几何
await page.evaluate(() => window.__winisland.set({ state: "compact", scene: "media", theme: "obsidian", measure: false }));
for (const [state, expect] of [
  ["idle", [120, 28]],
  ["compact", [226, 36]],
  ["expanded", [400, 140]]
]) {
  await page.evaluate((s) => window.__winisland.set({ state: s }), state);
  await page.evaluate(() => window.__winisland.settle());
  await page.waitForTimeout(260);
  const box = await islandBox();
  check(`media/${state} 尺寸`, [Math.round(box.width), Math.round(box.height)], expect);

  const r = await page.evaluate(() => document.getElementById("island").style.borderRadius);
  const expectR = state === "expanded" ? 28 : Math.round(expect[1] / 2);
  check(`media/${state} 圆角`, Math.round(parseFloat(r)), expectR);

  await shotIsland(`state-${state}-obsidian.png`);
}

// 各场景展开尺寸
for (const [scene, expect] of [
  ["timer", [400, 160]],
  ["volume", [380, 124]],
  ["transfer", [420, 176]],
  ["meeting", [400, 148]]
]) {
  await page.evaluate((s) => window.__winisland.set({ scene: s, state: "expanded", theme: "obsidian", measure: false }), scene);
  await page.evaluate(() => window.__winisland.settle());
  await page.waitForTimeout(220);
  const box = await islandBox();
  check(`${scene}/expanded 尺寸`, [Math.round(box.width), Math.round(box.height)], expect);
  await shotIsland(`scene-${scene}-expanded.png`);
  await page.evaluate((s) => window.__winisland.set({ scene: s, state: "compact" }), scene);
  await page.evaluate(() => window.__winisland.settle());
  await page.waitForTimeout(220);
  await shotIsland(`scene-${scene}-compact.png`);
}

// ------------------------------------------------------------------ 四主题
for (const [theme, look] of [["obsidian", "obsidian"], ["light", "light"], ["acrylic", "acrylic"]]) {
  await page.evaluate((t) => window.__winisland.set({ theme: t, wall: "dark", scene: "media", state: "expanded", measure: false }), theme);
  await page.evaluate(() => window.__winisland.settle());
  await page.waitForTimeout(240);
  check(`主题 ${theme} → data-look`, await page.getAttribute("#island", "data-look"), look);
  await shotIsland(`theme-${theme}-expanded.png`);
}
// 跟随系统
await page.evaluate(() => window.__winisland.set({ theme: "auto", sys: "light", wall: "light", scene: "media", state: "compact" }));
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(200);
check("跟随系统(浅) → data-look", await page.getAttribute("#island", "data-look"), "light");
await shotIsland(`theme-auto-light.png`);

// 毛玻璃在花哨壁纸下
await page.evaluate(() => window.__winisland.set({ theme: "acrylic", wall: "busy", scene: "media", state: "expanded" }));
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(240);
await shotIsland(`theme-acrylic-busy.png`);

// 黑胶囊在亮壁纸上的轮廓
await page.evaluate(() => window.__winisland.set({ theme: "obsidian", wall: "light", scene: "media", state: "compact" }));
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(200);
await shotIsland(`contrast-obsidian-on-light.png`);

// --------------------------------------------------------------- 整屏台面
await page.evaluate(() => window.__winisland.set({ theme: "obsidian", wall: "dark", scene: "media", state: "compact", measure: true }));
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(300);
await page.screenshot({ path: path.join(out, "stage-demo.png") });

// ------------------------------------------------------------------ 对照板
await page.goto(board);
await page.waitForFunction(() => window.__boardReady);
await page.waitForTimeout(500);
await page.screenshot({ path: path.join(out, "style-board.png"), fullPage: true });

await browser.close();

console.log(fails.length ? `\n失败 ${fails.length} 项：${fails.join(", ")}` : "\n全部通过");
if (fails.length) process.exit(1);