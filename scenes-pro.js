/* =============================================================================
   WinIsland · 场景渲染器（无约束版）
   -----------------------------------------------------------------------------
   与 winisland-scenes.js 的区别：
     · 尺寸放大一档，展开态给内容留呼吸
     · 封面改成生成式（三层径向光斑 + 斜向反光 + 内侧高光），不再是纯色块
     · 展开态版面按「主栏 + 次栏」重排，留出按钮和波形的位置
     · 强调色分成 acc（装饰面）与 acc-on（实心压字面），保证白字可读
   经典脚本，file:// 直接可跑。
   ============================================================================= */
(function (global) {
  "use strict";

  /* ------------------------------------------------------------------ 尺寸 */
  var SIZES = {
    idle: { idle: [132, 30] },
    media: { idle: [132, 30], compact: [248, 38], expanded: [416, 156] },
    timer: { idle: [132, 30], compact: [188, 38], expanded: [416, 176] },
    volume: { idle: [132, 30], compact: [252, 38], expanded: [396, 136] },
    transfer: { idle: [132, 30], compact: [268, 38], expanded: [436, 196] },
    meeting: { idle: [132, 30], compact: [256, 38], expanded: [416, 164] }
  };

  /* 展开态圆角上限 30，其余为半高。
     写成「只依赖高度」的单调函数，而不是按状态二分——否则展开→收起时状态先切到
     compact，半径会从 30 直接跳回 h/2（156/2=78），中途出现一次反向跳变。
     min(30, h/2) 在 h≤60 时恒等于 h/2，在展开高度上恒等于 30，两个端点都对得上。 */
  function radiusFor(state, h) {
    return Math.min(30, h / 2);
  }

  /* ------------------------------------------------------------- 封面主色
     c1/c2/c3 = 封面渐变三色；acc = 装饰面；acc-on = 实心压白字的面（已按 ≥4.5:1 选深） */
  var ART = {
    aurora: { c1: "#9a8cf5", c2: "#5a4ac8", c3: "#3ec8e8", acc: "#b4a8ff", on: "#5342c2" },
    ember: { c1: "#ffa87a", c2: "#d8492c", c3: "#ffd166", acc: "#ffb08a", on: "#a9340d" },
    mint: { c1: "#6ee7b7", c2: "#0f9b96", c3: "#bef264", acc: "#5eead4", on: "#0a6157" },
    rose: { c1: "#fb9fc8", c2: "#a03389", c3: "#ffc86b", acc: "#f9b0d6", on: "#8f1d5c" },
    steel: { c1: "#9dc0f7", c2: "#3f6fd8", c3: "#a5e9ff", acc: "#9dc9fd", on: "#1b49b8" }
  };

  /* ------------------------------------------------------------------ 图标 */
  var ICON = {
    play: '<path d="M7.4 5.32c0-.7.76-1.12 1.35-.75l7.28 4.68a.89.89 0 0 1 0 1.5l-7.28 4.68c-.6.38-1.35-.05-1.35-.75V5.32Z"/>',
    pause:
      '<rect x="6.4" y="4.9" width="2.7" height="10.2" rx="1.35"/><rect x="10.9" y="4.9" width="2.7" height="10.2" rx="1.35"/>',
    prev:
      '<rect x="4.9" y="4.9" width="2.1" height="10.2" rx="1.05"/><path d="M15.1 5.9v8.2c0 .79-.87 1.25-1.51.81L7.4 10.6a.9.9 0 0 1 0-1.2l6.19-4.31c.64-.44 1.51.02 1.51.81Z"/>',
    next:
      '<rect x="13" y="4.9" width="2.1" height="10.2" rx="1.05"/><path d="M4.9 5.9v8.2c0 .79.87 1.25 1.51.81L12.6 10.6a.9.9 0 0 0 0-1.2L6.41 5.09c-.64-.44-1.51.02-1.51.81Z"/>',
    speaker:
      '<path d="M9.55 4.62c0-.81-.95-1.25-1.56-.74L4.93 6.9H3.45c-.57 0-1.03.46-1.03 1.03v4.14c0 .57.46 1.03 1.03 1.03h1.48l3.06 3.02c.61.51 1.56.07 1.56-.74V4.62Z"/><path d="M12.62 7.09a.92.92 0 0 1 1.3.05 4.5 4.5 0 0 1 0 5.72.92.92 0 1 1-1.35-1.25 2.66 2.66 0 0 0 0-3.22.92.92 0 0 1 .05-1.3Z"/><path d="M15.12 4.86a.92.92 0 0 1 1.3.06 8.2 8.2 0 0 1 0 10.16.92.92 0 1 1-1.36-1.25 6.36 6.36 0 0 0 0-7.66.92.92 0 0 1 .06-1.31Z"/>',
    calendar:
      '<path d="M6.1 2.5c.52 0 .94.42.94.94v.66h5.92v-.66a.94.94 0 1 1 1.88 0v.67a2.5 2.5 0 0 1 2.2 2.48v8.16a2.5 2.5 0 0 1-2.5 2.5H5.46a2.5 2.5 0 0 1-2.5-2.5V6.59c0-1.2.85-2.2 1.98-2.44v-.71c0-.52.42-.94.94-.94Zm9.24 5.62H4.66v6.63c0 .44.36.8.8.8h9.08c.44 0 .8-.36.8-.8V8.12Z"/>',
    clock:
      '<path d="M10 2.6a7.4 7.4 0 1 1 0 14.8 7.4 7.4 0 0 1 0-14.8Zm0 1.88a5.52 5.52 0 1 0 0 11.04 5.52 5.52 0 0 0 0-11.04Zm.92 2.2v3.2l2.3 1.36a.94.94 0 1 1-.96 1.62l-2.7-1.6a.94.94 0 0 1-.47-.81V6.68a.94.94 0 1 1 1.83 0Z"/>',
    doc: '<path d="M5.9 2.4h4.97c.25 0 .49.1.66.28l4.2 4.2c.18.17.28.41.28.66v8.06a1.8 1.8 0 0 1-1.8 1.8H5.9a1.8 1.8 0 0 1-1.8-1.8V4.2a1.8 1.8 0 0 1 1.8-1.8Zm4.6 1.9v3.1h3.06L10.5 4.3Z"/>',
    reset:
      '<path d="M10 3.1c1.83 0 3.49.71 4.73 1.87l.9-.9c.5-.5 1.37-.15 1.37.56v3.4c0 .44-.35.79-.79.79h-3.4c-.71 0-1.06-.87-.56-1.37l.99-.99A5 5 0 1 0 15 10c0 .6.49 1.09 1.09 1.09S17.18 10.6 17.18 10A7.18 7.18 0 1 0 10 17.18c.6 0 1.09.49 1.09 1.09s-.49 1.09-1.09 1.09A9.36 9.36 0 1 1 10 3.1Z"/>',
    video:
      '<path d="M3.4 6.1c0-1.1.9-2 2-2h6.2c1.1 0 2 .9 2 2v.55l2.35-1.3c.68-.37 1.5.12 1.5.9v7.5c0 .78-.82 1.27-1.5.9L13.6 13.4v.5c0 1.1-.9 2-2 2H5.4c-1.1 0-2-.9-2-2V6.1Z"/>',
    bell: '<path d="M10 2.4c3.03 0 5.5 2.46 5.5 5.5v2.5l1.16 2.47a.9.9 0 0 1-.81 1.3H4.15a.9.9 0 0 1-.81-1.3L4.5 10.4V7.9c0-3.04 2.46-5.5 5.5-5.5Zm0 15.2a2.3 2.3 0 0 1-2.2-1.66h4.4A2.3 2.3 0 0 1 10 17.6Z"/>',
    pin: '<path d="M10 2.2c3.2 0 5.8 2.6 5.8 5.8 0 4-4.4 8.1-5.27 8.85a.8.8 0 0 1-1.06 0C8.6 16.1 4.2 12 4.2 8A5.8 5.8 0 0 1 10 2.2Zm0 3.4a2.4 2.4 0 1 0 0 4.8 2.4 2.4 0 0 0 0-4.8Z"/>'
  };

  function svg(name, cls) {
    return (
      '<span class="ico ' + (cls || "ico--md") + '">' +
      '<svg viewBox="0 0 20 20" width="100%" height="100%" fill="currentColor" aria-hidden="true">' +
      (ICON[name] || "") + "</svg></span>"
    );
  }
  function tile(name, cls) {
    return (
      '<span class="tile ' + (cls || "") + '">' +
      '<svg viewBox="0 0 20 20" width="100%" height="100%" fill="currentColor" aria-hidden="true">' +
      (ICON[name] || "") + "</svg></span>"
    );
  }
  function cov(size, a) {
    return (
      '<span class="cov cov--' + size + '" style="--c1:' + a.c1 + ";--c2:" + a.c2 +
      ";--c3:" + a.c3 + '"></span>'
    );
  }
  function wave(paused) {
    return (
      '<span class="wave" data-paused="' + (paused ? "true" : "false") +
      '" aria-hidden="true"><i></i><i></i><i></i><i></i><i></i></span>'
    );
  }
  function bar(pct, thin) {
    return (
      '<div class="bar' + (thin ? " bar--thin" : "") + '" style="--p:' + pct + '%">' +
      '<span class="bar__fill"></span>' +
      (thin ? "" : '<span class="bar__head"></span>') +
      "</div>"
    );
  }
  function seg(n, on) {
    var out = '<div class="seg" aria-hidden="true">';
    for (var i = 0; i < n; i++) out += "<i" + (i < on ? ' data-on="1"' : "") + "></i>";
    return out + "</div>";
  }
  function btn(icon, cls) {
    return '<button class="btn ' + (cls || "") + '" type="button">' + svg(icon, "ico--sm") + "</button>";
  }

  /* 环形进度：viewBox 108，描边 7 */
  function ring(pct) {
    var r = 48;
    var c = 2 * Math.PI * r;
    var off = c * (1 - pct);
    return (
      '<svg class="ring" width="108" height="108" viewBox="0 0 108 108" aria-hidden="true">' +
      '<circle class="ring__track" cx="54" cy="54" r="' + r + '" stroke-width="7" fill="none"/>' +
      '<circle class="ring__arc" cx="54" cy="54" r="' + r + '" stroke-width="7" fill="none" ' +
      'stroke-dasharray="' + c.toFixed(2) + '" stroke-dashoffset="' + off.toFixed(2) + '" ' +
      'transform="rotate(-90 54 54)"/></svg>'
    );
  }

  /* --------------------------------------------------------------- 各场景 */
  var SCENES = {
    /* 媒体：左封面 + 右「标题 / 进度 / 控制」三行 */
    media: function (a, paused) {
      return {
        idle: '<span class="breathe"></span>',
        compact:
          cov("sm", a) +
          '<span class="txt tight"><span class="t1">夜航西飞</span><span class="t2">The Velvet Hours</span></span>' +
          wave(paused),
        expanded:
          '<div class="sc-media">' +
          cov("lg", a) +
          '<div class="sc-media__col stag">' +
          '<span class="txt"><span class="t1">夜航西飞</span>' +
          '<span class="t2">The Velvet Hours · Late Lines</span></span>' +
          bar(62) +
          '<span class="times"><span>2:14</span><span>3:36</span></span>' +
          '<span class="sc-media__ctrl">' +
          btn("prev", "btn--icon") +
          btn("play", "btn--round btn--primary") +
          btn("next", "btn--icon") +
          '<span class="grow"></span>' +
          wave(paused) +
          "</span>" +
          "</div></div>"
      };
    },

    /* 计时器：左环 + 右「标签 / 大数字 / 三键」 */
    timer: function (a) {
      return {
        idle: '<span class="breathe"></span>',
        compact:
          '<span class="tile tile--sm">' +
          '<svg viewBox="0 0 20 20" width="100%" height="100%" fill="currentColor" aria-hidden="true">' +
          ICON.clock + "</svg></span>" +
          '<span class="txt tight"><span class="t1">18:42</span><span class="t2">专注 · 番茄钟</span></span>' +
          '<span class="t3">62%</span>',
        expanded:
          '<div class="sc-timer">' +
          ring(0.62) +
          '<div class="sc-timer__col stag">' +
          '<span class="t3">专注 · 番茄钟</span>' +
          '<span class="num num--mega">18:42</span>' +
          '<span class="sc-timer__btns">' +
          btn("pause", "btn--icon") +
          '<button class="btn" type="button">重置</button>' +
          '<button class="btn" type="button">+1 分</button>' +
          "</span>" +
          "</div></div>"
      };
    },

    /* 音量：设备行 + 24 段条 */
    volume: function (a, paused, on) {
      var lvl = on == null ? 15 : on;
      return {
        idle: '<span class="breathe"></span>',
        compact:
          svg("speaker", "ico--sm") +
          '<span class="txt tight"><span class="t1">扬声器</span><span class="t2">Realtek · 立体声</span></span>' +
          '<span class="t3" style="font-size:var(--fs-body);color:var(--ink-1)">62</span>',
        expanded:
          '<div class="sc-vol stag">' +
          '<span class="sc-vol__head">' +
          tile("speaker") +
          '<span class="txt tight"><span class="t1">扬声器（Realtek）</span>' +
          '<span class="t2">已连接 · 立体声</span></span>' +
          '<span class="sc-vol__val"><span class="num">62</span><span class="unit">%</span></span>' +
          "</span>" +
          seg(24, lvl) +
          "</div>"
      };
    },

    /* 传输：汇总 + 三行清单 */
    transfer: function (a) {
      var rows = [
        { n: "季度复盘-2026Q3.pdf", p: 78, s: "18.2 / 23.4 MB" },
        { n: "品牌视觉规范_v4.zip", p: 41, s: "96 / 234 MB" },
        { n: "会议录音_0914.m4a", p: 12, s: "2.4 / 20 MB" }
      ];
      var body = rows
        .map(function (r) {
          return (
            '<span class="sc-trans__row">' +
            tile("doc", "tile--sm") +
            '<span class="name">' + r.n + "</span>" +
            bar(r.p, true) +
            '<span class="pct">' + r.p + "%</span>" +
            '<span class="size">' + r.s + "</span>" +
            "</span>"
          );
        })
        .join("");
      return {
        idle: '<span class="breathe"></span>',
        compact:
          tile("doc", "tile--sm") +
          '<span class="txt tight"><span class="t1">正在传输 3 个文件</span>' +
          '<span class="t2">季度复盘-2026Q3.pdf</span></span>' +
          '<span class="t3">78%</span>',
        expanded:
          '<div class="sc-trans stag">' +
          '<span class="sc-trans__head"><span class="t2">3 个文件 · 总计 277 MB</span>' +
          '<span class="speed">12.4 MB/s</span></span>' +
          body +
          "</div>"
      };
    },

    /* 会议：左日历磁贴 + 右「标题 / 时间地点 / 两个键」 */
    meeting: function (a) {
      return {
        idle: '<span class="breathe"></span>',
        compact:
          tile("calendar", "tile--sm") +
          '<span class="txt tight"><span class="t1">周会 · 视觉评审</span>' +
          '<span class="t2">14:00 · 3F 山海</span></span>' +
          '<span class="t3">5 分钟</span>',
        expanded:
          '<div class="sc-meet">' +
          tile("calendar", "tile--lg") +
          '<div class="sc-meet__col stag">' +
          '<span class="txt"><span class="t1">周会 · 视觉设计评审</span></span>' +
          '<span class="sc-meet__meta">' + svg("clock", "ico--xs") +
          "<span>14:00 – 14:45</span>" + svg("pin", "ico--xs") +
          "<span>3F 山海</span></span>" +
          '<span class="sc-meet__btns">' +
          '<button class="btn" type="button">' + svg("bell", "ico--sm") + "稍后提醒</button>" +
          '<button class="btn btn--primary" type="button">' + svg("video", "ico--sm") + "加入会议</button>" +
          "</span>" +
          "</div>" +
          '<span class="soon">5 分钟后</span>' +
          "</div>"
      };
    },

    /* 无活动：只有一处呼吸点 */
    idle: function () {
      return { idle: '<span class="breathe"></span>' };
    }
  };

  /* --------------------------------------------------------------- 落位 */
  function sizeFor(scene, state) {
    var t = SIZES[scene] || SIZES.idle;
    return t[state] || t.idle;
  }

  /* --------------------------------------------------------------- 外壳
     岛的七层质感 + 三套视图。render() 只负责往已有视图里填内容，
     外壳必须由这里建，否则岛是空的。 */
  var SHELL =
    '<div class="isl__layer isl__refract"></div>' +
    '<div class="isl__layer isl__tint"></div>' +
    '<div class="isl__layer isl__lens"></div>' +
    '<div class="isl__layer isl__spec"></div>' +
    '<div class="isl__layer isl__edge"></div>' +
    '<div class="isl__layer isl__glow"></div>' +
    '<div class="isl__layer isl__grain"></div>' +
    '<div class="isl__stack">' +
    '<div class="isl__view isl__view--idle"></div>' +
    '<div class="isl__view isl__view--compact"></div>' +
    '<div class="isl__view isl__view--expanded"></div>' +
    "</div>";

  function ensureShell(el) {
    if (!el.querySelector(".isl__stack")) el.innerHTML = SHELL;
    return el;
  }

  function render(el, opts) {
    opts = opts || {};
    var scene = el.getAttribute("data-scene") || "media";
    var art = ART[opts.art] || ART.aurora;
    var views = (SCENES[scene] || SCENES.media)(art, !!opts.paused, opts.level);

    ["idle", "compact", "expanded"].forEach(function (s) {
      var host = el.querySelector(".isl__view--" + s);
      if (!host) return;
      /* 没有对应模板就清空，避免残留上一个场景的 DOM */
      host.innerHTML = views[s] == null ? "" : views[s];
    });

    el.style.setProperty("--acc", art.acc);
    el.style.setProperty("--acc-on", art.on);
  }

  /* 直接落位（画廊卡片用这种，不走弹簧） */
  function applySize(el, state) {
    var scene = el.getAttribute("data-scene") || "media";
    var pair = sizeFor(scene, state);
    el.style.setProperty("--w", pair[0] + "px");
    el.style.setProperty("--h", pair[1] + "px");
    el.style.setProperty("--r", radiusFor(state, pair[1]) + "px");
    return pair;
  }

  /* 一次装好 */
  function mount(el, opts) {
    opts = opts || {};
    var scene = opts.scene || "media";
    var state = opts.state || "compact";
    ensureShell(el);
    el.setAttribute("data-scene", scene);
    el.setAttribute("data-state", state);
    el.setAttribute("data-look", opts.look || "obsidian");
    el.setAttribute("data-mode", opts.mode || "inline");
    render(el, opts);
    return applySize(el, state);
  }

  global.WinIslandPro = {
    SIZES: SIZES,
    ART: ART,
    radiusFor: radiusFor,
    sizeFor: sizeFor,
    ensureShell: ensureShell,
    render: render,
    applySize: applySize,
    mount: mount,
    svg: svg,
    ICON: ICON
  };
})(window);