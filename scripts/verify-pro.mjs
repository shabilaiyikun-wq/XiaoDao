/* =============================================================================
   showcase 验收（无约束版）
   -----------------------------------------------------------------------------
   当前模型没有图像输入，所以不看图，全部用可测量的证据：
     A 无脚本错误        B 弹簧形变时长       C 内容不溢出
     D 连续圆角确实生效   E 玻璃确实在折射      F 文本对比度（截图实测）
     G 强调色压白字对比度
   ============================================================================= */
import { chromium } from "playwright";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(__dirname, "..");
const CHROME = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";

const pass = [];
const fail = [];
const ok = (cond, label, detail) =>
  (cond ? pass : fail).push(label + (detail ? `（${detail}）` : ""));

/* 颜色 / 对比度 */
const srgb = (c) => {
  const v = c / 255;
  return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
};
const lum = (r, g, b) => 0.2126 * srgb(r) + 0.7152 * srgb(g) + 0.0722 * srgb(b);
const contrast = (l1, l2) => {
  const [a, b] = l1 > l2 ? [l1, l2] : [l2, l1];
  return (a + 0.05) / (b + 0.05);
};
const hex2rgb = (h) => {
  const s = h.replace("#", "");
  return [0, 2, 4].map((i) => parseInt(s.slice(i, i + 2), 16));
};

const browser = await chromium.launch({ executablePath: CHROME });
const ctx = await browser.newContext({
  viewport: { width: 1440, height: 1000 },
  deviceScaleFactor: 2
});
const page = await ctx.newPage();

const errors = [];
page.on("console", (m) => {
  if (m.type() === "error") errors.push("console: " + m.text());
});
page.on("pageerror", (e) => errors.push("pageerror: " + e.message));

await page.goto("file:///" + ROOT.replace(/\\/g, "/") + "/showcase.html", {
  waitUntil: "load"
});
await page.waitForTimeout(1400);

/* ------------------------------------------------------------------ A 报错 */
ok(errors.length === 0, "无脚本错误", errors.slice(0, 3).join(" | ") || "0 条");

/* ------------------------------------------------------------ B 形变时长 */
/* 首屏那一发在 700ms 才开始，且此时画廊十几颗岛正同时跑入场动画，
   帧间隔被拉长会让弹簧的 dt 被钳到 50ms，测出来偏慢。
   所以先落到紧凑、等静止，再触发一次干净的展开来计时。 */
await page.waitForTimeout(1500);
await page.keyboard.press("2");
await page.waitForTimeout(850);
await page.keyboard.press("3");
await page.waitForTimeout(950);

const hero = await page.evaluate(() => {
  const el = document.getElementById("heroIsland");
  const cs = getComputedStyle(el);
  return {
    w: parseFloat(el.style.getPropertyValue("--w")),
    h: parseFloat(el.style.getPropertyValue("--h")),
    r: parseFloat(el.style.getPropertyValue("--r")),
    dur: document.getElementById("roDur").textContent.trim(),
    cs: cs.cornerShape || cs.getPropertyValue("corner-shape"),
    state: el.getAttribute("data-state")
  };
});
const durMs = parseInt(hero.dur, 10);
ok(
  Math.abs(hero.w - 416) < 1.2 && Math.abs(hero.h - 156) < 1.2,
  "展开态落位 416×156",
  `${hero.w.toFixed(1)}×${hero.h.toFixed(1)}`
);
ok(durMs >= 350 && durMs <= 460, "形变时长落在 350–460ms", hero.dur);
ok(hero.r === 30, "展开态圆角 30", String(hero.r));
ok(
  /superellipse|squircle/.test(hero.cs || ""),
  "外框使用连续圆角",
  hero.cs || "(空)"
);

/* -------------------------------------------------------------- C 不溢出 */
const overflow = await page.evaluate(() => {
  const out = [];
  document.querySelectorAll(".isl").forEach((isl) => {
    const box = isl.getBoundingClientRect();
    const view = isl.querySelector(".isl__view--" + isl.getAttribute("data-state"));
    if (!view) {
      out.push({
        scene: isl.getAttribute("data-scene"),
        state: isl.getAttribute("data-state") || "(无 data-state)",
        why: "无对应视图"
      });
      return;
    }
    const sc = { w: view.scrollWidth, h: view.scrollHeight };
    const cw = view.clientWidth;
    const ch = view.clientHeight;
    let minL = Infinity,
      minT = Infinity,
      maxR = -Infinity,
      maxB = -Infinity;
    view.querySelectorAll("*").forEach((n) => {
      const r = n.getBoundingClientRect();
      if (!r.width && !r.height) return;
      minL = Math.min(minL, r.left);
      minT = Math.min(minT, r.top);
      maxR = Math.max(maxR, r.right);
      maxB = Math.max(maxB, r.bottom);
    });
    const overL = box.left - minL;
    const overT = box.top - minT;
    const overR = maxR - box.right;
    const overB = maxB - box.bottom;
    const spill = Math.max(overL, overT, overR, overB);
    if (sc.w > cw + 1 || sc.h > ch + 1 || spill > 1) {
      out.push({
        scene: isl.getAttribute("data-scene"),
        state: isl.getAttribute("data-state"),
        scroll: `${sc.w}>${cw} / ${sc.h}>${ch}`,
        spill: +spill.toFixed(2)
      });
    }
  });
  return out;
});
ok(
  overflow.length === 0,
  "所有岛内容不溢出裁切区",
  overflow.length
    ? overflow.map((o) => `${o.scene}/${o.state}:${o.spill ?? o.why}`).join(" ")
    : `${await page.evaluate(() => document.querySelectorAll(".isl").length)} 个岛全通过`
);

/* -------------------------------------------------- D 连续圆角是否真生效 */
const corner = await page.evaluate(async () => {
  const mk = (shape, top) => {
    const box = document.createElement("div");
    box.style.cssText = `position:fixed;left:0;top:${top}px;width:200px;height:120px;background:#000;z-index:99999`;
    const s = document.createElement("div");
    s.style.cssText = `position:absolute;left:0;top:0;width:200px;height:120px;
      border-radius:48px;corner-shape:${shape};background:#fff`;
    box.appendChild(s);
    document.body.appendChild(box);
  };
  mk("round", 0);
  mk("superellipse(4)", 130);
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  return true;
});
await page.waitForTimeout(120);
const shotCorner = (await page.screenshot({ type: "png" })).toString("base64");
const cornerStats = await page.evaluate(async (b64) => {
  const bin = atob(b64);
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
  const bmp = await createImageBitmap(new Blob([buf], { type: "image/png" }));
  const cv = new OffscreenCanvas(bmp.width, bmp.height);
  const cx = cv.getContext("2d");
  cx.drawImage(bmp, 0, 0);
  const scale = bmp.width / 1440;
  /* 对角点 (9,9)：圆弧之外、超椭圆之内 */
  const at = (x, y) => {
    const d = cx.getImageData(Math.round(x * scale), Math.round(y * scale), 1, 1).data;
    return d[0];
  };
  return {
    round: at(9, 9),
    cont: at(9, 130 + 9),
    roundInner: at(40, 40),
    contInner: at(40, 130 + 40)
  };
}, shotCorner);
ok(
  cornerStats.roundInner > 240 && cornerStats.contInner > 240,
  "圆角形状内部为填充色",
  `round ${cornerStats.roundInner} / cont ${cornerStats.contInner}`
);
ok(
  cornerStats.cont - cornerStats.round > 60,
  "superellipse 在肩部比圆弧更饱满（连续圆角真生效）",
  `对角点 round=${cornerStats.round} cont=${cornerStats.cont}`
);

/* ------------------------------------------- E 玻璃是否真的在采样背后内容 */
const glassStats = await page.evaluate(async () => {
  /* 造一个高饱和背景 + 两颗岛（玻璃 vs 曜黑），比对内部色度 */
  const wrap = document.createElement("div");
  wrap.style.cssText =
    "position:fixed;left:0;top:0;width:100%;height:100%;z-index:99998;background:linear-gradient(120deg,#2b6cff,#ff2f7a)";
  const mkIsl = (look, top) => {
    const el = document.createElement("div");
    el.className = "isl";
    el.setAttribute("data-look", look);
    el.setAttribute("data-state", "expanded");
    el.style.cssText += `position:absolute;left:20px;top:${top}px`;
    el.innerHTML =
      '<div class="isl__layer isl__refract"></div><div class="isl__layer isl__tint"></div>' +
      '<div class="isl__layer isl__lens"></div><div class="isl__layer isl__spec"></div>' +
      '<div class="isl__layer isl__grain"></div><div class="isl__stack"></div>';
    window.WinIslandPro.applySize(el, "expanded");
    wrap.appendChild(el);
    return el;
  };
  mkIsl("glass", 20);
  mkIsl("obsidian", 220);
  document.body.appendChild(wrap);
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  return true;
});
await page.waitForTimeout(320);
const shotGlass = (await page.screenshot({ type: "png" })).toString("base64");
const glassPix = await page.evaluate(async (b64) => {
  const bin = atob(b64);
  const buf = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
  const bmp = await createImageBitmap(new Blob([buf], { type: "image/png" }));
  const cv = new OffscreenCanvas(bmp.width, bmp.height);
  const cx = cv.getContext("2d");
  cx.drawImage(bmp, 0, 0);
  const scale = bmp.width / 1440;
  const patch = (x, y) => {
    const d = cx.getImageData(Math.round(x * scale), Math.round(y * scale), 1, 1).data;
    return [d[0], d[1], d[2]];
  };
  const chroma = (p) => Math.max(...p) - Math.min(...p);
  /* 取岛内右侧空白处，避开文字 */
  const g = patch(400, 60);
  const o = patch(400, 260);
  return { glass: g, obsidian: o, glassChroma: chroma(g), obsidianChroma: chroma(o) };
}, shotGlass);
ok(
  glassPix.glassChroma > 16,
  "玻璃在采样背后内容（内部带背景色度）",
  `chroma ${glassPix.glassChroma} rgb(${glassPix.glass.join(",")})`
);
ok(
  glassPix.glassChroma > glassPix.obsidianChroma + 10,
  "玻璃比曜黑更透背景色",
  `glass ${glassPix.glassChroma} vs obsidian ${glassPix.obsidianChroma}`
);

/* -------------------------------------------------- F/G 对比度（回到真实页） */
/* 之前的做法是把整个岛打成网格找最亮面，但封面本身就是亮的、文字并不压在封面上，
   那样量到的是封面而不是文字背景，不公平也不准确。
   改成按真实文字元素量：先把文字变透明拍一张「纯背景」，再逐元素取该矩形内的
   平均色作为背景，与元素自己的 color 算对比度。四套外观各测一遍。 */
await page.reload({ waitUntil: "load" });
await page.waitForTimeout(1700);

const TEXT_SEL =
  ".t1,.t2,.t3,.num,.times > span,.btn,.pct,.size,.name,.speed,.soon,.unit";
const MEASURE_LOOKS = ["glass", "obsidian", "graphite", "frost"];
const MEASURE_SCENES = ["media", "timer", "volume", "transfer", "meeting"];

async function measureOne(look, scene) {
  await page.evaluate(
    ({ lk, sc }) => {
      const el = document.getElementById("heroIsland");
      el.setAttribute("data-look", lk);
      el.setAttribute("data-scene", sc);
      window.WinIslandPro.render(el, { art: "aurora" });
      window.WinIslandPro.applySize(el, "expanded");
    },
    { lk: look, sc: scene }
  );
  /* 等错峰入场跑完，否则量到的是半透明的中间帧 */
  await page.waitForTimeout(680);

  const items = await page.evaluate((sel) => {
    const root = document.querySelector("#heroIsland .isl__view--expanded");
    if (!root) return [];
    const out = [];
    root.querySelectorAll(sel).forEach((n) => {
      if (!n.textContent.trim()) return;
      const r = n.getBoundingClientRect();
      if (r.width < 5 || r.height < 5) return;
      const cs = getComputedStyle(n);
      const m = cs.color.match(/[\d.]+/g);
      if (!m || (m[3] !== undefined && parseFloat(m[3]) < 0.05)) return;
      out.push({
        cls: [...n.classList].join(".").slice(0, 26) || "(无类名)",
        color: cs.color,
        x: r.left,
        y: r.top,
        w: r.width,
        h: r.height
      });
    });
    return out;
  }, TEXT_SEL);
  if (!items.length) return { worst: null, count: 0 };

  /* 文字变透明，只留背景 */
  await page.evaluate((sel) => {
    const st = document.createElement("style");
    st.id = "__probe_transparent";
    st.textContent = `#heroIsland .isl__view--expanded ${sel.replace(
      /,/g,
      ",#heroIsland .isl__view--expanded "
    )}{color:transparent!important;-webkit-text-fill-color:transparent!important;text-shadow:none!important}`;
    document.head.appendChild(st);
  }, TEXT_SEL);
  await page.waitForTimeout(90);

  const shot = (await page.screenshot({ type: "png" })).toString("base64");
  const sampled = await page.evaluate(
    async ({ b64, items }) => {
      const bin = atob(b64);
      const buf = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) buf[i] = bin.charCodeAt(i);
      const bmp = await createImageBitmap(new Blob([buf], { type: "image/png" }));
      const cv = new OffscreenCanvas(bmp.width, bmp.height);
      const cx = cv.getContext("2d");
      cx.drawImage(bmp, 0, 0);
      const scale = bmp.width / 1440;
      const toLin = (v) => {
        const t = v / 255;
        return t <= 0.04045 ? t / 12.92 : Math.pow((t + 0.055) / 1.055, 2.4);
      };
      return items.map((it) => {
        let acc = 0,
          n = 0;
        for (let ry = 0; ry < 2; ry++) {
          for (let rx = 0; rx < 3; rx++) {
            const x = it.x + (it.w * (rx + 1)) / 4;
            const y = it.y + (it.h * (ry + 1)) / 3;
            const d = cx.getImageData(
              Math.round(x * scale),
              Math.round(y * scale),
              1,
              1
            ).data;
            acc += 0.2126 * toLin(d[0]) + 0.7152 * toLin(d[1]) + 0.0722 * toLin(d[2]);
            n++;
          }
        }
        return { cls: it.cls, color: it.color, bg: acc / n };
      });
    },
    { b64: shot, items }
  );

  await page.evaluate(() => {
    const st = document.getElementById("__probe_transparent");
    if (st) st.remove();
  });

  const parse = (c) => {
    const m = c.match(/[\d.]+/g).map(Number);
    return m.length >= 3 ? [m[0], m[1], m[2]] : [255, 255, 255];
  };
  let worst = null;
  for (const s of sampled) {
    const inkL = lum(...parse(s.color));
    const a = inkL > s.bg ? inkL : s.bg;
    const b = inkL > s.bg ? s.bg : inkL;
    const c = (a + 0.05) / (b + 0.05);
    if (!worst || c < worst.c) worst = { ...s, c, scene };
  }
  return { worst, count: sampled.length };
}

const contrastRows = [];
const badLooks = [];
for (const look of MEASURE_LOOKS) {
  let w = null;
  let total = 0;
  for (const scene of MEASURE_SCENES) {
    const r = await measureOne(look, scene);
    total += r.count;
    if (r.worst && (!w || r.worst.c < w.c)) w = r.worst;
  }
  contrastRows.push(
    `${look} ${w.c.toFixed(2)}:1（最差 ${w.cls} @${w.scene}；本外观共测 ${total} 处文字）`
  );
  if (w.c < 4.5) badLooks.push(`${look}=${w.c.toFixed(2)} @${w.scene}/${w.cls} bg=${w.bg.toFixed(3)} ink=${w.color}`);
}
ok(
  badLooks.length === 0,
  "四套外观 × 五场景的最低文字对比度全部 ≥4.5:1",
  badLooks.length ? badLooks.join(" ") : contrastRows.join(" / ")
);

/* 强调色压白字：五个取材的 acc-on 全部要 ≥4.5 */
const accents = await page.evaluate(() => window.WinIslandPro.ART);
const badAcc = [];
const accRows = [];
for (const [k, a] of Object.entries(accents)) {
  const c = contrast(1.0, lum(...hex2rgb(a.on)));
  accRows.push(`${k} ${c.toFixed(2)}`);
  if (c < 4.5) badAcc.push(`${k}=${c.toFixed(2)}`);
}
ok(badAcc.length === 0, "五套取材的实心面压白字全部 ≥4.5:1", accRows.join(" / "));

/* ------------------------------------------- G 视口宽度下页面不横向溢出 */
const overflowRows = [];
const badWidths = [];
for (const w of [1440, 1280, 1024, 900]) {
  await page.setViewportSize({ width: w, height: 1000 });
  await page.waitForTimeout(240);
  const r = await page.evaluate(() => ({
    sw: document.documentElement.scrollWidth,
    cw: document.documentElement.clientWidth
  }));
  if (r.sw > r.cw + 1) badWidths.push(`${w}px 溢出到 ${r.sw}px`);
  overflowRows.push(`${w}:${r.sw}/${r.cw}`);
}
ok(
  badWidths.length === 0,
  "四种视口宽度下页面无横向溢出",
  badWidths.length ? badWidths.join(" ") : overflowRows.join(" ")
);

/* 报告 */
console.log("");
console.log("【A】脚本与几何");
console.log("【B】弹簧与连续圆角");
console.log("【C】内容溢出");
console.log("【D】圆角形状");
console.log("【E】玻璃折射");
console.log("【F】文本对比度");
console.log("【G】响应式");
for (const p of pass) console.log("  ok   " + p);
for (const f of fail) console.log("  FAIL " + f);
console.log("");
console.log(`通过 ${pass.length} 项，失败 ${fail.length} 项`);

await browser.close();
process.exit(fail.length ? 1 : 0);