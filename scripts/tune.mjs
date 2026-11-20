// 弹簧参数标定：用和 style-demo.html 完全一致的积分器与判据，
// 反解出让「展开落在 350–450ms、收起落在 250–300ms」的刚度/阻尼。
// 用法：node scripts/tune.mjs
const CASES = {
  // [起点, 终点]：媒体场景 紧凑→展开 的宽度与高度位移
  width: [226, 400],
  height: [36, 140]
};

// 与 demo 相同的半隐式欧拉 + 子步
// 时长判据：误差「最后一次」超出 1% 位移带的时刻（= 此后稳定在带内）。
// 用「首次进入」会得到非单调结果：轨迹高速掠过目标点时瞬时撞线，之后还会弹出去。
function simulate({ from, to, k, c, fps = 60, maxMs = 3000, pct = 0.01 }) {
  let v = from, vel = 0;
  const delta = Math.abs(to - from);
  const thr = Math.max(0.5, delta * pct);
  const dt = 1 / fps;
  let t = 0;
  let lastAbove = 0;
  while (t < maxMs) {
    const steps = Math.max(1, Math.ceil(dt / (1 / 240)));
    const h = dt / steps;
    for (let i = 0; i < steps; i++) {
      const a = -k * (v - to) - c * vel;
      vel += a * h;
      v += vel * h;
    }
    t += dt * 1000;
    if (Math.abs(v - to) > thr) lastAbove = t;
    if (Math.abs(v - to) < 0.4 && Math.abs(vel) < 12) return lastAbove;
  }
  return Infinity;
}

function overshoot({ from, to, k, c, fps = 60 }) {
  let v = from, vel = 0, peak = v;
  const dt = 1 / fps;
  for (let i = 0; i < fps * 2; i++) {
    const steps = 8, h = dt / steps;
    for (let j = 0; j < steps; j++) {
      const a = -k * (v - to) - c * vel;
      vel += a * h;
      v += vel * h;
    }
    if (to > from) peak = Math.max(peak, v);
    else peak = Math.min(peak, v);
  }
  return ((peak - to) / (to - from)) * 100; // 百分比
}

// demo 里的增益关系：高度轴略硬 k×1.06 / c×1.02；收起由 collapseK/collapseC 决定
function gains(collapseK, collapseC) {
  return {
    expand: { k: 1, c: 1 },
    collapse: { k: collapseK, c: collapseC },
    hExpand: { k: 1.06, c: 1.02 },
    hCollapse: { k: 1.06 * collapseK, c: 1.02 * collapseC }
  };
}

function evaluate(k, zeta, collapseK, collapseC) {
  const G = gains(collapseK, collapseC);
  const c = 2 * zeta * Math.sqrt(k);
  const expandW = simulate({
    from: CASES.width[0], to: CASES.width[1], k: k * G.expand.k, c: c * G.expand.c
  });
  const expandH = simulate({
    from: CASES.height[0], to: CASES.height[1], k: k * G.hExpand.k, c: c * G.hExpand.c
  });
  const collapseW = simulate({
    from: CASES.width[1], to: CASES.width[0], k: k * G.collapse.k, c: c * G.collapse.c
  });
  const collapseH = simulate({
    from: CASES.height[1], to: CASES.height[0], k: k * G.hCollapse.k, c: c * G.hCollapse.c
  });
  return {
    expand: Math.max(expandW, expandH),
    collapse: Math.max(collapseW, collapseH),
    over: overshoot({ from: CASES.width[0], to: CASES.width[1], k, c })
  };
}

const EXPAND = [375, 413];   // §8.2 要求 350–450，取安全中段
const COLLAPSE = [262, 288]; // §8.2 要求 250–300，取安全中段

let best = null;
for (let k = 60; k <= 460; k += 1) {
  for (let zeta = 0.62; zeta <= 1.0; zeta += 0.01) {
    for (let ck = 1.4; ck <= 4.2; ck += 0.05) {
      const cc = 1.0 + (ck - 1.0) * 0.55; // 阻尼跟着刚度一起抬，ζ 基本不变
      const r = evaluate(k, zeta, ck, cc);
      if (!isFinite(r.expand) || !isFinite(r.collapse)) continue;
      if (r.expand < EXPAND[0] || r.expand > EXPAND[1]) continue;
      if (r.collapse < COLLAPSE[0] || r.collapse > COLLAPSE[1]) continue;
      // 目标：回弹 1.5%~3.5%（§8.2「默认略带回弹」），并靠近「展开 400 / 收起 275」
      const bounce = Math.abs(r.over);
      const bouncePenalty = bounce < 1.5 ? (1.5 - bounce) * 60 : bounce > 3.5 ? (bounce - 3.5) * 60 : 0;
      const midPenalty = Math.abs(r.expand - 400) * 0.5 + Math.abs(r.collapse - 275) * 0.5;
      const score = bouncePenalty + midPenalty;
      if (!best || score < best.score) best = { k, zeta, ck, cc, ...r, score };
    }
  }
}

if (!best) {
  console.log("没找到同时满足两段的参数");
  process.exit(0);
}

const c = 2 * best.zeta * Math.sqrt(best.k);
console.log("最优参数：");
console.log(`  刚度 k = ${best.k.toFixed(0)}`);
console.log(`  阻尼比 ζ = ${best.zeta.toFixed(2)}  →  阻尼 c = ${c.toFixed(1)}`);
console.log(`  收起增益：刚度 ×${best.ck.toFixed(2)} / 阻尼 ×${best.cc.toFixed(2)}`);
console.log(`  展开 = ${Math.round(best.expand)}ms（要求 350–450）`);
console.log(`  收起 = ${Math.round(best.collapse)}ms（要求 250–300）`);
console.log(`  回弹 = ${best.over.toFixed(2)}%`);

console.log("\n候选（同一 ζ 与收起增益下微调刚度）：");
for (const k of [best.k - 20, best.k - 10, best.k, best.k + 10, best.k + 20]) {
  const r = evaluate(k, best.zeta, best.ck, best.cc);
  const c2 = 2 * best.zeta * Math.sqrt(k);
  console.log(
    `  k=${k} c=${c2.toFixed(1)} → 展开 ${Math.round(r.expand)}ms / 收起 ${Math.round(r.collapse)}ms / 回弹 ${r.over.toFixed(2)}%`
  );
}