// 样式验收：不靠肉眼，全部用可断言的量校验
//   1) 布局：每个场景 × 每个形态，所有元素必须落在岛的裁切区内（溢出会被 overflow:hidden 吃掉）
//   2) 度量：内边距、字号、行高、圆角按文档 §8.1 校验
//   3) 对比度：文本令牌对底色算 WCAG 比值（正文 ≥4.5，装饰面 ≥3）
//   4) 像素：拿 PNG 回灌浏览器解码，实测胶囊不透明度、圆角留白、阴影存在
// 用法：node scripts/verify.mjs
import { chromium } from "playwright";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..");
const demo = pathToFileURL(path.join(root, "style-demo.html")).href;
const CHROME = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";

const browser = await chromium.launch({ executablePath: CHROME });
const fails = [];
let passes = 0;

function check(name, cond, detail) {
  if (cond) {
    passes++;
    console.log(`  ok   ${name}`);
  } else {
    fails.push(name);
    console.log(`  FAIL ${name}  ${detail === undefined ? "" : JSON.stringify(detail)}`);
  }
}
function near(a, b, tol = 1) {
  return Math.abs(a - b) <= tol;
}

/* =========================================================== 1. 布局与度量 */
const ctx = await browser.newContext({ viewport: { width: 1680, height: 1000 }, deviceScaleFactor: 2 });
const page = await ctx.newPage();
await page.goto(demo);
await page.waitForFunction(() => window.__winisland);

const SCENES = ["media", "timer", "volume", "transfer", "meeting"];
const STATES = { media: ["idle", "compact", "expanded"] };
for (const s of SCENES) STATES[s] = s === "media" ? STATES[s] : ["compact", "expanded"];

console.log("\n【1】布局溢出检查（只在活动视图上判，未激活视图被裁切属正常）");
for (const scene of SCENES) {
  for (const state of STATES[scene]) {
    await page.evaluate(
      (o) => window.__winisland.set(o),
      { scene, state, theme: "obsidian", measure: false }
    );
    await page.evaluate(() => window.__winisland.settle());
    await page.waitForTimeout(180);

    const res = await page.evaluate((st) => {
      const island = document.getElementById("island");
      const ir = island.getBoundingClientRect();
      const view = island.querySelector(".island__view--" + st);
      const out = [];
      view.querySelectorAll("*").forEach((el) => {
        const r = el.getBoundingClientRect();
        if (r.width < 0.5 || r.height < 0.5) return;
        const worst = Math.max(
          ir.left - r.left,
          r.right - ir.right,
          ir.top - r.top,
          r.bottom - ir.bottom
        );
        if (worst > 0.6) {
          out.push({
            tag: el.tagName.toLowerCase(),
            cls: (el.className && String(el.className).slice(0, 34)) || "",
            worst: +worst.toFixed(2)
          });
        }
      });
      return {
        overflow: out,
        // 活动视图自身是否比岛还大（内容被挤压/溢出）
        viewOverW: +(view.scrollWidth - Math.round(ir.width)).toFixed(2),
        viewOverH: +(view.scrollHeight - Math.round(ir.height)).toFixed(2)
      };
    }, state);

    check(
      `${scene}/${state} 无溢出`,
      res.overflow.length === 0 && res.viewOverW <= 1 && res.viewOverH <= 1,
      res
    );
  }
}

console.log("\n【2】度量（§8.1 内边距 16px / 字号 / 圆角）");
await page.evaluate(() => window.__winisland.set({ scene: "media", state: "expanded", theme: "obsidian" }));
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(200);
const pad = await page.evaluate(() => {
  const island = document.getElementById("island");
  const view = island.querySelector(".island__view--expanded");
  const ir = island.getBoundingClientRect();
  // 子元素的并集：展开态是 flex 居中，单个子元素不一定贴边，并集才代表「内容区」
  let L = Infinity, T = Infinity, R = -Infinity, B = -Infinity;
  view.querySelectorAll("*").forEach((el) => {
    const r = el.getBoundingClientRect();
    if (r.width < 0.5 || r.height < 0.5) return;
    L = Math.min(L, r.left); T = Math.min(T, r.top);
    R = Math.max(R, r.right); B = Math.max(B, r.bottom);
  });
  return {
    left: +(L - ir.left).toFixed(2),
    top: +(T - ir.top).toFixed(2),
    right: +(ir.right - R).toFixed(2),
    bottom: +(ir.bottom - B).toFixed(2),
    cssPad: getComputedStyle(view).padding
  };
});
check(
  "展开态内容区内边距 = 16px（左/右贴合，上/下允许居中误差）",
  near(pad.left, 16, 0.6) &&
    near(pad.right, 16, 0.6) &&
    pad.top >= 15.8 &&
    pad.top <= 18.5 &&
    pad.bottom >= 15.8 &&
    pad.bottom <= 18.5 &&
    pad.cssPad === "16px",
  pad
);

const type = await page.evaluate(() => {
  const island = document.getElementById("island");
  const t = island.querySelector(".island__view--expanded .t-title");
  const cs = getComputedStyle(t);
  return { fs: cs.fontSize, fw: cs.fontWeight, ff: cs.fontFamily.split(",")[0].replace(/"/g, "") };
});
check("展开态标题字号 15px", type.fs === "15px", type);
check("字体族取到 Segoe UI Variable Display", type.ff === "Segoe UI Variable Display", type);

const fontOk = await page.evaluate(() =>
  document.fonts.check('13px "Segoe UI Variable Display"')
);
check("Segoe UI Variable Display 实际可渲染", fontOk === true, fontOk);

const coverR = await page.evaluate(() => {
  const c = document.getElementById("island").querySelector(".cover--lg");
  return parseFloat(getComputedStyle(c).borderRadius);
});
check("展开态封面圆角 14px", near(coverR, 14, 0.5), coverR);

const compactPad = await page.evaluate(() => {
  const island = document.getElementById("island");
  island.setAttribute("data-state", "compact");
  const view = island.querySelector(".island__view--compact");
  return getComputedStyle(view).padding;
});
check("紧凑态左右内边距 = 6 / 10", /^0px 10px 0px 6px$/.test(compactPad), compactPad);

/* ============================================================== 3. 对比度 */
console.log("\n【3】对比度（WCAG AA，直接量令牌而不是量元素）");

function parseColor(s) {
  if (!s) return null;
  const m = s.match(/rgba?\(([^)]+)\)/);
  if (!m) return null;
  const p = m[1].split(/[,\s/]+/).filter(Boolean).map((v) => parseFloat(v));
  return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
}
function over(fg, bg) {
  const a = fg.a;
  return {
    r: fg.r * a + bg.r * (1 - a),
    g: fg.g * a + bg.g * (1 - a),
    b: fg.b * a + bg.b * (1 - a),
    a: 1
  };
}
function lin(c) {
  const v = c / 255;
  return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
}
function lum(c) {
  return 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);
}
function ratio(a, b) {
  const l1 = lum(a), l2 = lum(b);
  const hi = Math.max(l1, l2), lo = Math.min(l1, l2);
  return (hi + 0.05) / (lo + 0.05);
}

/* 令牌读出来后在页面里统一归一化成 rgb()/rgba()，避免手写 hex 解析 */
const tokens = await page.evaluate(() => {
  const probe = document.createElement("span");
  probe.style.display = "none";
  document.body.appendChild(probe);
  const norm = (v) => {
    probe.style.color = "";
    probe.style.color = v;
    return getComputedStyle(probe).color;
  };
  const island = document.getElementById("island");
  const out = {};
  for (const look of ["obsidian", "light", "acrylic"]) {
    island.setAttribute("data-look", look);
    const cs = getComputedStyle(island);
    out[look] = {
      fill: norm(cs.getPropertyValue("--is-fill").trim()),
      text: norm(cs.getPropertyValue("--is-text").trim()),
      text2: norm(cs.getPropertyValue("--is-text-2").trim()),
      text3: norm(cs.getPropertyValue("--is-text-3").trim()),
      // 毛玻璃压暗系数：和 backdrop-filter 里的 brightness() 同源
      dim: parseFloat(cs.getPropertyValue("--is-acrylic-dim").trim()) || 1
    };
  }
  probe.remove();
  return out;
});

for (const look of ["obsidian", "light"]) {
  const t = tokens[look];
  const base = { r: 255, g: 255, b: 255, a: 1 }; // 亮色主题用白底兜底
  const bg = over(parseColor(t.fill), look === "light" ? base : { r: 0, g: 0, b: 0, a: 1 });
  const rows = [
    ["主文本", t.text],
    ["次级文本", t.text2],
    ["三级/计量文本", t.text3]
  ];
  for (const [label, col] of rows) {
    const fg = parseColor(col);
    if (!fg) { check(`${look}/${label} 令牌可解析`, false, col); continue; }
    const r = ratio(over(fg, bg), bg);
    check(`${look}/${label} ${r.toFixed(2)}:1 ≥ 4.5`, r >= 4.5, +r.toFixed(2));
  }
}

// 毛玻璃：色调是半透明的，先按 backdrop 压暗系数（brightness）过一遍，再叠色调
{
  const t = tokens.acrylic;
  const tint = parseColor(t.fill);
  const dim = t.dim;
  for (const backdrop of [
    { name: "深壁纸", c: { r: 18, g: 22, b: 34, a: 1 } },
    { name: "亮壁纸最差", c: { r: 205, g: 210, b: 220, a: 1 } }
  ]) {
    // brightness() 近似为逐通道乘系数
    const dimmed = {
      r: backdrop.c.r * dim,
      g: backdrop.c.g * dim,
      b: backdrop.c.b * dim,
      a: 1
    };
    const bg = over(tint, dimmed);
    for (const [label, col] of [
      ["次级文本", t.text2],
      ["三级文本", t.text3]
    ]) {
      const r = ratio(over(parseColor(col), bg), bg);
      check(`acrylic/${backdrop.name}/${label} ${r.toFixed(2)}:1 ≥ 4.5`, r >= 4.5, {
        r: +r.toFixed(2),
        dim,
        bg: [Math.round(bg.r), Math.round(bg.g), Math.round(bg.b)]
      });
    }
  }
}

// 实心强调面上的白字（磁贴 / 主按钮）
const onCases = await page.evaluate(() => {
  const out = [];
  const island = document.getElementById("island");
  for (const scene of ["media", "timer", "volume", "transfer", "meeting"]) {
    island.setAttribute("data-scene", scene);
    const cs = getComputedStyle(island);
    out.push({ scene, on: cs.getPropertyValue("--is-accent-on").trim() });
  }
  return out;
});
for (const c of onCases) {
  const m = c.on.match(/#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})/i);
  if (!m) { check(`${c.scene} accent-on 可解析`, false, c.on); continue; }
  const bg = { r: parseInt(m[1], 16), g: parseInt(m[2], 16), b: parseInt(m[3], 16), a: 1 };
  const r = ratio({ r: 255, g: 255, b: 255, a: 1 }, bg);
  check(`${c.scene} 白字/强调面 ${r.toFixed(2)}:1 ≥ 4.5`, r >= 4.5, +r.toFixed(2));
}

/* =========================================================== 5. 动画时长（§8.2） */
console.log("\n【5】动画（§8.2 展开 350–450ms / 收起 250–300ms / 可打断）");

await page.evaluate(() => window.__winisland.set({ scene: "media", state: "compact", theme: "obsidian", measure: false }));
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(120);

const tExpand = await page.evaluate(() => window.__winisland.measure("expanded"));
check(`展开耗时 ${tExpand}ms 落在 350–450`, tExpand >= 350 && tExpand <= 450, tExpand);

const tCollapse = await page.evaluate(() => window.__winisland.measure("compact"));
check(`收起耗时 ${tCollapse}ms 落在 250–300`, tCollapse >= 250 && tCollapse <= 300, tCollapse);
check(`收起快于展开（${tCollapse} < ${tExpand}）`, tCollapse < tExpand, { tExpand, tCollapse });

// 可打断：展开中途反向，宽度必须从当前插值位置继续回落，而不是跳到端点
await page.evaluate(() => window.__winisland.settle());
await page.waitForTimeout(80);
const interrupt = await page.evaluate(async () => {
  const W = window.__winisland;
  W.measure("expanded"); // 不等待
  await new Promise((r) => setTimeout(r, 110));
  const mid = W.width();
  W.measure("compact");
  await new Promise((r) => setTimeout(r, 70));
  const after = W.width();
  await new Promise((r) => setTimeout(r, 700));
  return { start: 226, mid: +mid.toFixed(1), after: +after.toFixed(1), end: W.width() };
});
check(
  `打断：中途宽度介于两端（${interrupt.mid}），反向时已回落（${interrupt.after}）`,
  interrupt.mid > 226.5 &&
    interrupt.mid < 399.5 &&
    interrupt.after < interrupt.mid &&
    interrupt.after > 226.5 &&
    Math.abs(interrupt.end - 226) < 1,
  interrupt
);

/* ================================================================ 6. 像素 */
console.log("\n【4】像素实测（PNG 回灌解码）");
const ctx1 = await browser.newContext({ viewport: { width: 1200, height: 800 }, deviceScaleFactor: 1 });
const p1 = await ctx1.newPage();
await p1.goto(demo);
await p1.waitForFunction(() => window.__winisland);
await p1.evaluate(() => window.__winisland.set({ scene: "media", state: "compact", theme: "obsidian", wall: "light", measure: false }));
await p1.evaluate(() => window.__winisland.settle());
await p1.waitForTimeout(250);

const box = await p1.locator("#island").boundingBox();
const shot = await p1.screenshot();
const px = await p1.evaluate(async ({ b64, box }) => {
  const bin = atob(b64);
  const arr = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
  const bmp = await createImageBitmap(new Blob([arr], { type: "image/png" }));
  const cv = new OffscreenCanvas(bmp.width, bmp.height);
  const g = cv.getContext("2d");
  g.drawImage(bmp, 0, 0);
  const at = (x, y) => {
    const d = g.getImageData(Math.round(x), Math.round(y), 1, 1).data;
    return [d[0], d[1], d[2], d[3]];
  };
  const cx = box.x + box.width / 2;
  const cy = box.y + box.height / 2;
  return {
    // 采样点要落在「纯胶囊面」上：左边 3px 处于内边距里（内容从 6px 才开始），
    // 采样正中有大概率打在文字的抗锯齿像素上
    fill: at(box.x + 3, cy),
    cornerTL: at(box.x + 2, box.y + 2),        // 圆角外 → 应是壁纸
    edgeTop: at(box.x + 30, box.y + 3),        // 上边缘中部 → 应是胶囊（带高光）
    outsideLeft: at(box.x - 14, cy),           // 胶囊左侧 → 壁纸
    belowShadow: at(cx, box.y + box.height + 6), // 下方 → 应被阴影压暗
    farBelow: at(cx, box.y + box.height + 60),
    w: bmp.width, h: bmp.height
  };
}, { b64: shot.toString("base64"), box });

const dark = (p) => (p[0] + p[1] + p[2]) / 3;
check("胶囊面接近纯黑（luma<12）", dark(px.fill) < 12, px.fill);
check("圆角外露出壁纸（luma>120）", dark(px.cornerTL) > 120, px.cornerTL);
check("上边缘中部为胶囊（luma<40）", dark(px.edgeTop) < 40, px.edgeTop);
check("胶囊左侧是壁纸（luma>120）", dark(px.outsideLeft) > 120, px.outsideLeft);
check("下边缘存在轻微外阴影（比远处更暗）", dark(px.belowShadow) < dark(px.farBelow) - 3, {
  below: px.belowShadow, far: px.farBelow
});

// 亮色主题的胶囊必须是浅色
await p1.evaluate(() => window.__winisland.set({ theme: "light", wall: "dark", scene: "media", state: "compact" }));
await p1.evaluate(() => window.__winisland.settle());
await p1.waitForTimeout(200);
const box2 = await p1.locator("#island").boundingBox();
const shot2 = await p1.screenshot();
const px2 = await p1.evaluate(async ({ b64, box }) => {
  const bin = atob(b64);
  const arr = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
  const bmp = await createImageBitmap(new Blob([arr], { type: "image/png" }));
  const cv = new OffscreenCanvas(bmp.width, bmp.height);
  const g = cv.getContext("2d");
  g.drawImage(bmp, 0, 0);
  const d = g.getImageData(Math.round(box.x + box.width / 2), Math.round(box.y + box.height / 2), 1, 1).data;
  return [d[0], d[1], d[2], d[3]];
}, { b64: shot2.toString("base64"), box: box2 });
check("亮色主题胶囊为浅色（luma>200）", dark(px2) > 200, px2);

/* 内容真的画上去了吗：胶囊内部必须有足够的亮像素（文字/封面/图标）和足够的层次 */
async function inkOf(page, box) {
  const shot = await page.screenshot();
  return page.evaluate(
    async ({ b64, box }) => {
      const bin = atob(b64);
      const arr = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
      const bmp = await createImageBitmap(new Blob([arr], { type: "image/png" }));
      const cv = new OffscreenCanvas(bmp.width, bmp.height);
      const g = cv.getContext("2d");
      g.drawImage(bmp, 0, 0);
      // 只取内缩 2px 的区域，避开描边与抗锯齿边缘
      const x = Math.round(box.x) + 2;
      const y = Math.round(box.y) + 2;
      const w = Math.round(box.width) - 4;
      const h = Math.round(box.height) - 4;
      const d = g.getImageData(x, y, w, h).data;
      let bright = 0;
      const levels = new Set();
      for (let i = 0; i < d.length; i += 4) {
        const l = Math.round((d[i] + d[i + 1] + d[i + 2]) / 3);
        levels.add(l);
        if (l > 140) bright++;
      }
      return { bright, levels: levels.size, total: (w * h) | 0 };
    },
    { b64: shot.toString("base64"), box }
  );
}

for (const st of ["compact", "expanded"]) {
  await p1.evaluate((s) => window.__winisland.set({ theme: "obsidian", wall: "dark", scene: "media", state: s }), st);
  await p1.evaluate(() => window.__winisland.settle());
  await p1.waitForTimeout(220);
  const b = await p1.locator("#island").boundingBox();
  const ink = await inkOf(p1, b);
  check(
    `${st} 内部有实际绘制内容（亮像素 ${ink.bright}，色阶 ${ink.levels}）`,
    ink.bright > 40 && ink.levels > 12,
    ink
  );
}

await browser.close();

console.log(`\n通过 ${passes} 项，失败 ${fails.length} 项`);
if (fails.length) {
  console.log("失败清单：\n  - " + fails.join("\n  - "));
  process.exit(1);
}