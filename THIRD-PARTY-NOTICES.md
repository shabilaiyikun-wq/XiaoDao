# 第三方许可 / Third-party notices

本仓库有若干效果移植自两个上游项目，都是 MIT：

- **FluidKit**（Copyright (c) 2026 Yousef Hindy，https://github.com/runvendo/fluidkit ）
- **liquid-glass**（Copyright (c) 2025 Nikita Stadnik，https://github.com/nikdelvin/liquid-glass ）

许可原文只保留各一份：`desktop/LICENSES/fluidkit-MIT.txt`、
`desktop/LICENSES/liquid-glass-MIT.txt`（都含完整 MIT 文本）。
本文件是索引，按文件列出「借用了什么、借到什么程度、改了什么」，避免两处
版权文本各说一套。

## 按文件（本仓库 ← 上游）

| 本仓库文件 | 上游来源 | 借用程度 | 说明 |
| --- | --- | --- | --- |
| `desktop/Morph.cs` | `src/components/MorphSurface.tsx`、`docs/primitives/morph-surface.md` | **设计移植** | 见下节。 |
| `desktop/LiquidGeometry.cs` | `src/liquid/geometry.ts` | 算法移植 | 见 `desktop/LICENSES/fluidkit-MIT.txt`。 |
| `desktop/LiquidTension.cs` | `src/liquid/tension.ts` | 算法移植 | 同上。 |
| `desktop/Squish.cs` | `src/hooks/useSquish.ts` | 算法移植 | 按下时的方向性压扁。许可文本同上。 |
| `desktop/LiquidPair.cs` | `src/liquid/*` | 算法移植 | 同上。 |
| `desktop/Glass.cs` | `src/utils/liquidGlass.ts`（**liquid-glass，不是 FluidKit**） | 算法移植 | 边缘折射：位移图 / 位移语义 / 三通道色散。见 `desktop/LICENSES/liquid-glass-MIT.txt` 与下节。 |
| `desktop/verify/morph-tests.cs`、`morph-tests.cmd` | — | 本项目自写 | 验证上述移植的行为，不含上游代码。 |
| `desktop/verify/glass-bend.ps1`、`notes/probes/probe-glass-math.cs` | — | 本项目自写 | 承接 `Glass.cs` 的端到端与数学验收，不含上游代码。 |

本仓库**没有**引入上游的 React / motion 运行时。所有时间线推进都用既有的
`Squircle.cs` 里的 `Spring`（半隐式欧拉、1/240s 定步长子步）；上游
`useMotionSprings` 只作为参数与语义的参照。

## `desktop/Glass.cs` 的移植边界（liquid-glass）

上游把效果做成一条 SVG 滤镜链（`feImage` + `feDisplacementMap` +
三次 `feColorMatrix` 走 `screen` 合成），跑在 Astro/React 页面里。这三样在本项目
里都不存在：WPF 的 `AllowsTransparency` 分层窗口走软件光栅化，自定义
`ShaderEffect` 用不了，构建链里也没有 HLSL 编译器。所以**算法照搬、机器重写**：

1. **位移图。** 上游是中性 `#808080` 底 + `#000080` 填充，`X`/`Y` 两条线性渐变从
   `ceil(radius*15%)%` 走到 `floor(100-radius*15%)%`，再把内缩 `depth` 的圆角矩形
   按 `depth` 模糊。本项目改成解析式：用「到岛边的有号距离」
   （`GlassBend.EdgeDistance`，圆角段按 `Squircle.cs` 的超椭圆求），渐变断点取
   `ceil(r*0.15)/w`，两个通道值直接算出来，不去读纹理。
2. **位移语义。** `feDisplacementMap` 的定义是
   `P'(x,y) = P(x + scale*(Cx-0.5), y + scale*(Cy-0.5))`，逐字照搬，
   `scale = strength + chroma*k`。
3. **色散。** 上游跑三遍位移（`strength+2cab` / `+cab` / `strength`），每遍用
   `feColorMatrix` 抠出一条通道再 `screen` 合成。抠通道后 screen 合成等价于
   「每条通道按自己的偏移各采一点」，所以这里直接按三个偏移采 R/G/B 写进一张
   BGRA 纹理（`GlassBend.Render`）。
4. **参数。** 上游默认 `depth=10 / strength=100 / chromaticAberration=0`。本项目的
   取值按 452x206 DIP 岛体、26 DIP 带宽重定，放在 `desktop/Theme.cs` 的
   `Look.Bend / BendKick / BendSplit`，非 glass 外观一律为 0。

**没有**取的部分：SVG 滤镜的构建代码、React/Astro 组件、模糊与色调栈（本项目
本来就有）、任何高光/镜面层，以及绑在 web 布局上的尺寸换算。

## `desktop/Morph.cs` 的移植边界

取自 `MorphSurface.tsx` / `morph-surface.md` 的部分（设计，然后按本项目重写）：

- **锚定边缘的连续形变。** 上游 `anchor: "center" | "top"`：形变时锚定的一对边不动，
  另一对往外长。本项目原先是隐式锚（散在 `SyncPlacement` 里），现在收敛成
  `IslandFrame.Island()`（横向岛心、纵向岛顶）。
- **主体与附属元素分别配弹簧。** 上游 `bodySpring {240,24}` / `satelliteSpring {150,14}`：
  卫星更软、慢半拍。本项目主体沿用既有的 `250/24`（收起 `×1.75/×1.38`）**逐位未改**，
  只给内容面另配一套（进场 `150/26`、退场 `340/37`）。
- **内容只做位移 + 透明度。** 上游两脸常驻、只改 opacity/translateY，进场延时
  `.12s`、退场 `.12s` 且不停留；「faces never scale」。本项目用同一语义，
  参数重定为延时 `90ms`、进场 `+8px → 0`、退场 `→ +5px`。
- **retarget 语义。** 上游 `setTargets` 停掉现有动画并从当前值重新起算；
  本项目靠 `Spring.Aim()`（保留 `Value`/`Velocity`，只改 `From`/`Target`）达成同一件事。

**没有**取的部分：液态材质、张力桥与卫星水滴的几何、`clipContent` 的 DOM 量测
（WPF 这边裁剪由 `_host.Clip` 负责）、以及整个 motion/react 动画引擎。

对移植部分的改动（都已写进 `Morph.cs` 的文件头注释）：

1. 时间常数按本项目的形变时长重定（上游 CSS 毫秒曲线 → 弹簧刚度/阻尼）。
2. 可见度与位移共用同一套 `Spring`，并各自配了判定「到位」的阈值 ——
   `Spring.Settled` 的 `0.35 / 12` 是给像素量级定的，拿来判 0..1 的透明度会在 65%
   就判到位（本项目在消息小球那里踩过这个坑）。
3. 场上最多留 3 张脸（上游固定 2 张）。第三张只用于 A→B→C 极快连切时让 C 从 B
   手里接过去；超编时淘汰最淡的那张，而**不是**淘汰顺序最早的那张。
4. 退场中的那张脸 `IsHitTestVisible = false` —— 上游的 DOM 淡出元素不接收指针事件，
   WPF 里必须显式关掉，否则会点到一张看不见的卡片上的按钮。