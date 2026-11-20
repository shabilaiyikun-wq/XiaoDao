/* =============================================================================
   showcase 截图
   -----------------------------------------------------------------------------
   产出到 shots/pro/。当前模型没有图像输入，这些图是给人看的，
   脚本只保证尺寸正确、文件非空。
   ============================================================================= */
import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";
import { mkdir, stat } from "node:fs/promises";

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(__dirname, "..");
const OUT = resolve(ROOT, "shots/pro");
const CHROME = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";

await mkdir(OUT, { recursive: true });

const browser = await chromium.launch({ executablePath: CHROME });
const ctx = await browser.newContext({
  viewport: { width: 1440, height: 1000 },
  deviceScaleFactor: 2
});
const page = await ctx.newPage();
await page.goto("file:///" + ROOT.replace(/\\/g, "/") + "/showcase.html", {
  waitUntil: "load"
});
await page.waitForTimeout(1800);

const made = [];
const shot = async (name, target, opts = {}) => {
  const file = resolve(OUT, name + ".png");
  if (target) await target.screenshot({ path: file, ...opts });
  else await page.screenshot({ path: file, ...opts });
  const s = await stat(file);
  made.push({ name: name + ".png", kb: Math.round(s.size / 1024) });
};

/* ------------------------------------------------ 整页（长图，给设计审阅） */
await shot("showcase-full", null, { fullPage: true });

/* -------------------------------------------------------------- 主视觉区 */
await shot("hero-studio", page.locator(".studio"));

/* ----------------------------------------------------- 三形态（主视觉岛） */
const hero = page.locator("#heroIsland");
for (const [key, state] of [
  ["1", "idle"],
  ["2", "compact"],
  ["3", "expanded"]
]) {
  await page.keyboard.press(key);
  await page.waitForTimeout(720);
  await shot("hero-" + state, hero);
}

/* -------------------------------------------------- 六场景展开态（近距离） */
const SCENES = ["media", "timer", "volume", "transfer", "meeting"];
for (const sc of SCENES) {
  await page.evaluate((s) => {
    const el = document.getElementById("heroIsland");
    el.setAttribute("data-scene", s);
    el.setAttribute("data-state", "expanded");
    window.WinIslandPro.render(el, { art: "aurora" });
    window.WinIslandPro.applySize(el, "expanded");
  }, sc);
  await page.waitForTimeout(700);
  await shot("scene-" + sc, hero);
}

/* -------------------------------------------------------------- 四种外观 */
for (const lk of ["obsidian", "graphite", "frost", "glass"]) {
  await page.evaluate((l) => {
    const el = document.getElementById("heroIsland");
    el.setAttribute("data-look", l);
  }, lk);
  await page.waitForTimeout(280);
  await shot("look-" + lk, hero);
}

/* ------------------------------------------------------------ 细部标样 */
await page.evaluate(() => {
  const el = document.getElementById("heroIsland");
  el.setAttribute("data-look", "glass");
});
await page.waitForTimeout(220);
await shot("corner-zoom", page.locator(".corner-zoom"));
await shot("anatom", page.locator(".anatom"));
await shot("typescale", page.locator("#s-type .grid"));

/* ------------------------------------------------------------ 场景画廊 */
await shot("grid-scenes", page.locator("#sceneGrid"));
await shot("grid-looks", page.locator("#lookGrid"));

/* ------------------------------------------------------------------ 报告 */
console.log("");
for (const m of made) console.log(`  ${m.name.padEnd(24)} ${m.kb} KB`);
console.log("");
console.log(`共 ${made.length} 张 → ${OUT}`);
const empty = made.filter((m) => m.kb < 3);
if (empty.length) {
  console.log("  警告：以下文件过小，可能是空白：" + empty.map((e) => e.name).join(" "));
  await browser.close();
  process.exit(1);
}

await browser.close();