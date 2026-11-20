/* =============================================================================
   WinIsland · 场景内容渲染器 (v0.1)
   -----------------------------------------------------------------------------
   只负责「往三套视图里塞内容」，不碰几何动画。
   经典脚本（非 ES module），保证 file:// 直接双击可跑。

   用法：
     WinIsland.render(islandEl);          // 按 data-scene 填充内容
     WinIsland.applySize(islandEl);       // 按 data-scene + data-state 落位
   ============================================================================= */
(function (global) {
  "use strict";

  /* ---------------------------------------------------------------- 几何表
     §5.1 的三种形态尺寸；每类活动允许内容自适应（文档写「约 …，内容自适应」） */
  var SIZES = {
    idle: { idle: [120, 28] },
    media: { idle: [120, 28], compact: [226, 36], expanded: [400, 140] },
    timer: { idle: [120, 28], compact: [176, 36], expanded: [400, 160] },
    volume: { idle: [120, 28], compact: [236, 36], expanded: [380, 124] },
    transfer: { idle: [120, 28], compact: [240, 36], expanded: [420, 176] },
    meeting: { idle: [120, 28], compact: [244, 36], expanded: [400, 148] }
  };

  /* 封面占位色 → 封面主色（§6.2 封面主色提取，用于展开态背景光晕）
     accent    = 装饰面（进度/光环）
     accentOn  = 实心面（磁贴/按钮），压白字需 ≥4.5:1 */
  var ART_ACCENT = {
    aurora: { accent: "#a99bff", on: "#6d5bd0" },
    ember: { accent: "#ff8a63", on: "#c2410c" },
    mint: { accent: "#4fd1c5", on: "#0f766e" }
  };

  /* ------------------------------------------------------------------ 图标
     Fluent 风格：20×20 画布，圆角端点，实心块面 */
  var ICON = {
    play: '<path d="M7.4 5.32c0-.7.76-1.12 1.35-.75l7.28 4.68a.89.89 0 0 1 0 1.5l-7.28 4.68c-.6.38-1.35-.05-1.35-.75V5.32Z"/>',
    pause:
      '<rect x="6.4" y="4.9" width="2.7" height="10.2" rx="1.35"/><rect x="10.9" y="4.9" width="2.7" height="10.2" rx="1.35"/>',
    prev: '<rect x="4.9" y="4.9" width="2.1" height="10.2" rx="1.05"/><path d="M15.1 5.9v8.2c0 .79-.87 1.25-1.51.81L7.4 10.6a.9.9 0 0 1 0-1.2l6.19-4.31c.64-.44 1.51.02 1.51.81Z"/>',
    next: '<rect x="13" y="4.9" width="2.1" height="10.2" rx="1.05"/><path d="M4.9 5.9v8.2c0 .79.87 1.25 1.51.81L12.6 10.6a.9.9 0 0 0 0-1.2L6.41 5.09c-.64-.44-1.51.02-1.51.81Z"/>',
    speaker:
      '<path d="M9.55 4.62c0-.81-.95-1.25-1.56-.74L4.93 6.9H3.45c-.57 0-1.03.46-1.03 1.03v4.14c0 .57.46 1.03 1.03 1.03h1.48l3.06 3.02c.61.51 1.56.07 1.56-.74V4.62Z"/><path d="M12.62 7.09a.92.92 0 0 1 1.3.05 4.5 4.5 0 0 1 0 5.72.92.92 0 1 1-1.35-1.25 2.66 2.66 0 0 0 0-3.22.92.92 0 0 1 .05-1.3Z"/><path d="M15.12 4.86a.92.92 0 0 1 1.3.06 8.2 8.2 0 0 1 0 10.16.92.92 0 1 1-1.36-1.25 6.36 6.36 0 0 0 0-7.66.92.92 0 0 1 .06-1.31Z"/>',
    calendar:
      '<path d="M6.1 2.5c.52 0 .94.42.94.94v.66h5.92v-.66a.94.94 0 1 1 1.88 0v.67a2.5 2.5 0 0 1 2.2 2.48v8.16a2.5 2.5 0 0 1-2.5 2.5H5.46a2.5 2.5 0 0 1-2.5-2.5V6.59c0-1.2.85-2.2 1.98-2.44v-.71c0-.52.42-.94.94-.94Zm9.24 5.62H4.66v6.63c0 .44.36.8.8.8h9.08c.44 0 .8-.36.8-.8V8.12Z"/>',
    doc: '<path d="M5.9 2.4h4.97c.25 0 .49.1.66.28l4.2 4.2c.18.17.28.41.28.66v8.06a1.8 1.8 0 0 1-1.8 1.8H5.9a1.8 1.8 0 0 1-1.8-1.8V4.2a1.8 1.8 0 0 1 1.8-1.8Zm4.6 1.9v3.1h3.06L10.5 4.3Z"/>',
    reset:
      '<path d="M10 3.1c1.83 0 3.49.71 4.73 1.87l.9-.9c.5-.5 1.37-.15 1.37.56v3.4c0 .44-.35.79-.79.79h-3.4c-.71 0-1.06-.87-.56-1.37l.99-.99A5 5 0 1 0 15 10c0 .6.49 1.09 1.09 1.09.6 0 1.09-.49 1.09-1.09A7.18 7.18 0 1 0 10 17.18c.6 0 1.09.49 1.09 1.09S10.6 19.36 10 19.36A9.36 9.36 0 1 1 10 3.1Z"/>',
    plus: '<path d="M10.94 3.8a.94.94 0 1 0-1.88 0v5.26H3.8a.94.94 0 1 0 0 1.88h5.26v5.26a.94.94 0 1 0 1.88 0v-5.26h5.26a.94.94 0 1 0 0-1.88h-5.26V3.8Z"/>',
    check:
      '<path d="M8.6 14.4 4.8 10.6a1 1 0 1 1 1.42-1.42l2.38 2.4 5.18-5.2a1 1 0 1 1 1.42 1.42l-5.9 5.9a1 1 0 0 1-1.41 0Z"/>',
    video:
      '<path d="M3.4 6.1c0-1.1.9-2 2-2h6.2c1.1 0 2 .9 2 2v.55l2.35-1.3c.68-.37 1.5.12 1.5.9v7.5c0 .78-.82 1.27-1.5.9L13.6 13.4v.5c0 1.1-.9 2-2 2H5.4c-1.1 0-2-.9-2-2V6.1Z"/>',
    bell: '<path d="M10 2.4c3.03 0 5.5 2.46 5.5 5.5v2.5l1.16 2.47a.9.9 0 0 1-.81 1.3H4.15a.9.9 0 0 1-.81-1.3L4.5 10.4V7.9c0-3.04 2.46-5.5 5.5-5.5Zm0 15.2a2.3 2.3 0 0 1-2.2-1.66h4.4A2.3 2.3 0 0 1 10 17.6Z"/>',
    waveform:
      '<rect x="2.6" y="7.4" width="1.9" height="5.2" rx=".95"/><rect x="6.1" y="4.9" width="1.9" height="10.2" rx=".95"/><rect x="9.6" y="2.8" width="1.9" height="14.4" rx=".95"/><rect x="13.1" y="5.9" width="1.9" height="8.2" rx=".95"/><rect x="16.6" y="8.2" width="1.9" height="3.6" rx=".95"/>'
  };

  function ico(name, cls) {
    return (
      '<span class="ico ' +
      (cls || "ico--md") +
      '"><svg viewBox="0 0 20 20" width="100%" height="100%" fill="currentColor" aria-hidden="true">' +
      ICON[name] +
      "</svg></span>"
    );
  }

  function tile(name) {
    return (
      '<span class="tile"><svg viewBox="0 0 20 20" width="14" height="14" fill="currentColor" aria-hidden="true">' +
      ICON[name] +
      "</svg></span>"
    );
  }

  function wave(paused) {
    return (
      '<span class="wave" data-paused="' +
      (paused ? "true" : "false") +
      '" aria-hidden="true"><i></i><i></i><i></i><i></i></span>'
    );
  }

  /* -------------------------------------------------------- 环形进度工具
     r / stroke 一律用 viewBox 单位（画布 20×20），CSS 侧再整体缩放：
       ring--sm 20px  → 1 VB = 1 px   （r 8.25 / stroke 2.5 → 视觉 2.5px）
       ring--lg 96px  → 1 VB = 4.8 px （r 8.9  / stroke 1.25 → 视觉 6px）   */
  function ring(size, r, stroke, pct) {
    var c = 2 * Math.PI * r;
    var off = c * (1 - pct);
    return (
      '<span class="ring ring--' +
      size +
      '"><svg viewBox="0 0 20 20" width="100%" height="100%" aria-hidden="true">' +
      '<circle class="ring__bg" cx="10" cy="10" r="' +
      r +
      '" stroke-width="' +
      stroke +
      '"/>' +
      '<circle class="ring__fg" cx="10" cy="10" r="' +
      r +
      '" stroke-width="' +
      stroke +
      '" stroke-dasharray="' +
      c.toFixed(2) +
      '" stroke-dashoffset="' +
      off.toFixed(2) +
      '"/></svg>' +
      (size === "lg" ? '<span class="ring__center">专注</span>' : "") +
      "</span>"
    );
  }

  /* ------------------------------------------------------------ 各场景模板 */
  var TEMPLATES = {
    /* 空闲：极小胶囊（§5.1 约 120×28） —— 只有一处呼吸点，不承载信息 */
    idle: function () {
      return {
        idle:
          '<span class="dot"></span>' +
          '<span class="t3" style="margin-left:2px">空闲</span>'
      };
    },

    /* 媒体（§6.2）：紧凑左封面右波形，展开完整控制 */
    media: function (opt) {
      var art = opt.art || "aurora";
      var paused = !!opt.paused;
      return {
        compact:
          '<span class="cover cover--sm" data-art="' +
          art +
          '"></span>' +
          '<span class="t1 ellipsis grow">Clair de Lune</span>' +
          wave(paused),
        /* 展开态：左封面、右控制栏（400×140 里塞得下的唯一排法） */
        expanded:
          '<span class="cover cover--lg" data-art="' +
          art +
          '"></span>' +
          '<div class="stack">' +
          '<div class="meta grow" style="padding-top:0">' +
          '<span class="t-title ellipsis">Clair de Lune</span>' +
          '<span class="t2 ellipsis">Debussy · Suite bergamasque</span>' +
          "</div>" +
          '<div class="progress">' +
          '<div class="track"><div class="track__fill" style="width:37%"></div>' +
          '<div class="track__knob" style="left:37%"></div></div>' +
          '<div class="times num"><span>1:12</span><span>-2:02</span></div>' +
          "</div>" +
          '<div class="controls">' +
          '<button class="ctl" type="button" aria-label="上一首">' +
          ico("prev", "ico--sm") +
          "</button>" +
          '<button class="ctl ctl--primary" type="button" aria-label="播放">' +
          ico(paused ? "play" : "pause", "ico--sm") +
          "</button>" +
          '<button class="ctl" type="button" aria-label="下一首">' +
          ico("next", "ico--sm") +
          "</button>" +
          "</div>" +
          "</div>"
      };
    },

    /* 计时器 / 番茄钟（§6.4）：紧凑环形进度 + 剩余时间 */
    timer: function () {
      return {
        compact:
          ring("sm", 8.25, 2.5, 0.62) + '<span class="t1 num grow">24:59</span>',
        expanded:
          ring("lg", 8.9, 1.25, 0.62) +
          '<div class="stack">' +
          '<span class="num num--lg">24:59</span>' +
          '<span class="t2">番茄钟 · 专注中</span>' +
          '<div class="controls" style="margin-top:2px">' +
          '<button class="ctl ctl--primary" type="button" aria-label="暂停">' +
          ico("pause", "ico--sm") +
          "</button>" +
          '<button class="ctl" type="button" aria-label="重置">' +
          ico("reset", "ico--sm") +
          "</button>" +
          '<button class="ctl" type="button" aria-label="加一分钟">' +
          ico("plus", "ico--sm") +
          "</button>" +
          "</div>" +
          "</div>"
      };
    },

    /* 音量 OSD（§6.3 P0 瞬时）：一条横向音量条 */
    volume: function () {
      return {
        compact:
          ico("speaker", "ico--sm") +
          '<span class="track grow" style="height:4px">' +
          '<span class="track__fill" style="width:62%;display:block;height:100%"></span>' +
          "</span>" +
          '<span class="t1 num" style="font-size:12px">62</span>',
        expanded:
          '<div class="stack">' +
          '<div class="row">' +
          tile("speaker") +
          '<div class="meta grow" style="padding-top:0">' +
          '<span class="t-title ellipsis">Realtek Audio</span>' +
          '<span class="t2">扬声器 · 已连接</span>' +
          "</div>" +
          '<span class="t1 num">62</span>' +
          "</div>" +
          '<div class="progress" style="margin-top:2px">' +
          '<div class="track"><div class="track__fill" style="width:62%"></div>' +
          '<div class="track__knob" style="left:62%"></div></div>' +
          "</div>" +
          "</div>"
      };
    },

    /* 传输进度（§6.6）：紧凑一条迷你条，展开列多文件 */
    transfer: function () {
      function row(name, pct, size) {
        return (
          '<div class="xfer">' +
          tile("doc") +
          '<div class="xfer__meta">' +
          '<div class="xfer__line">' +
          '<span class="t2 ellipsis" style="color:var(--is-text)">' +
          name +
          "</span>" +
          '<span class="t3 num">' +
          pct +
          "%</span>" +
          "</div>" +
          '<div class="track" style="height:3px;background:var(--is-track)">' +
          '<div class="track__fill" style="width:' +
          pct +
          '%"></div>' +
          "</div>" +
          "</div>" +
          '<span class="t3 num" style="width:34px;text-align:right">' +
          size +
          "</span>" +
          "</div>"
        );
      }
      return {
        compact:
          tile("doc") +
          '<span class="t1 ellipsis grow">设计终稿.fig</span>' +
          '<span class="minibar"><i style="width:48%"></i></span>' +
          '<span class="t1 num" style="font-size:12px">48%</span>',
        expanded:
          '<div class="stack">' +
          '<div class="xfer__line">' +
          '<span class="t-title">3 个文件正在传输</span>' +
          '<span class="t3 num">剩余约 1 分 20 秒</span>' +
          "</div>" +
          '<div class="stack" style="gap:9px;margin-top:2px">' +
          row("设计终稿.fig", 48, "42 MB") +
          row("宣传片_4K.mp4", 76, "860 MB") +
          row("季度汇报.pptx", 21, "18 MB") +
          "</div>" +
          "</div>"
      };
    },

    /* 会议提醒（§6.7 P1）：展开态带「加入会议」 */
    meeting: function () {
      return {
        compact:
          tile("calendar") +
          '<span class="t1 ellipsis grow">10:30 站会</span>' +
          '<span class="t3">5 分钟后</span>',
        expanded:
          '<div class="stack" style="gap:10px">' +
          '<div class="row">' +
          tile("calendar") +
          '<div class="meta grow" style="padding-top:0">' +
          '<span class="t-title ellipsis">每日站会</span>' +
          '<span class="t2 ellipsis">10:30 – 10:45 · 会议室 A</span>' +
          "</div>" +
          "</div>" +
          '<div class="row row--end">' +
          '<button class="pill" type="button">稍后</button>' +
          '<button class="pill pill--accent" type="button">' +
          ico("video", "ico--sm") +
          "<span>加入会议</span></button>" +
          "</div>" +
          "</div>"
      };
    }
  };

  /* 默认选项（真实产品里由活动源提供） */
  var DEFAULT_OPT = { art: "aurora", paused: false };

  /**
   * 按 island 的 data-scene 填充三套视图。
   * @param {HTMLElement} el
   * @param {object} [opt] 场景参数（art / paused …）
   */
  function render(el, opt) {
    var scene = el.getAttribute("data-scene") || "media";
    var options = Object.assign({}, DEFAULT_OPT, opt || {});
    var make = TEMPLATES[scene] || TEMPLATES.idle;
    var views = make(options);

    ["idle", "compact", "expanded"].forEach(function (state) {
      var host = el.querySelector(".island__view--" + state);
      if (!host) return;
      var html = views[state];
      /* 没有对应模板（如「无活动」没有展开态）就清空，避免残留上一次场景的 DOM */
      host.innerHTML = typeof html === "string" ? html : "";
    });

    /* 媒体场景用封面主色做强调色与光晕（§6.2 封面主色提取） */
    if (scene === "media") {
      var pair = ART_ACCENT[options.art] || ART_ACCENT.aurora;
      el.style.setProperty("--is-accent", pair.accent);
      el.style.setProperty("--is-accent-on", pair.on);
    } else {
      el.style.removeProperty("--is-accent");
      el.style.removeProperty("--is-accent-on");
    }
    return el;
  }

  /**
   * 按 data-scene + data-state 把尺寸写到元素上（无动画的静态落位）。
   * 交互演示里由弹簧驱动，不用这个；静态样式板用它。
   */
  function applySize(el) {
    var scene = el.getAttribute("data-scene") || "media";
    var state = el.getAttribute("data-state") || "idle";
    var table = SIZES[scene] || SIZES.idle;
    var pair = table[state] || table.idle;
    var w = pair[0];
    var h = pair[1];
    el.style.width = w + "px";
    el.style.height = h + "px";
    el.style.borderRadius = (state === "expanded" ? Math.min(28, h / 2) : h / 2) + "px";
    return { w: w, h: h };
  }

  global.WinIsland = {
    SIZES: SIZES,
    ICON: ICON,
    ico: ico,
    render: render,
    applySize: applySize,
    /** 建一个空的岛骨架 DOM */
    create: function (scene, state, look) {
      var el = document.createElement("div");
      el.className = "island";
      el.setAttribute("data-scene", scene || "media");
      el.setAttribute("data-state", state || "compact");
      el.setAttribute("data-look", look || "obsidian");
      el.innerHTML =
        '<div class="island__acrylic"></div>' +
        '<div class="island__tint"></div>' +
        '<div class="island__grain"></div>' +
        '<div class="island__sheen"></div>' +
        '<div class="island__glow"></div>' +
        '<div class="island__stack">' +
        '<div class="island__view island__view--idle"></div>' +
        '<div class="island__view island__view--compact"></div>' +
        '<div class="island__view island__view--expanded"></div>' +
        "</div>" +
        '<div class="island__hairline"></div>';
      return el;
    }
  };
})(window);