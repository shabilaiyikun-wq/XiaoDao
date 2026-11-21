# 第三方许可 / Third-party notices

本仓库有若干效果移植自三个上游项目，都是 MIT：

- **FluidKit**（Copyright (c) 2026 Yousef Hindy，https://github.com/runvendo/fluidkit ）
- **liquid-glass**（Copyright (c) 2025 Nikita Stadnik，https://github.com/nikdelvin/liquid-glass ）
- **Bencho**（Copyright (c) 2026 Lorenzo Cabra，https://github.com/cabralorenzo/bencho ）

许可原文只保留各一份：`desktop/LICENSES/fluidkit-MIT.txt`、
`desktop/LICENSES/liquid-glass-MIT.txt`、`desktop/LICENSES/bencho-MIT.txt`
（都含完整 MIT 文本）。
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
| `desktop/Tower.cs` | `blocks/todo-tower/Tower.tsx`（内联 `lab/rigid.ts`） | 算法移植 | 二维刚体求解器 + 「压不碎东西的手」。见 `desktop/LICENSES/bencho-MIT.txt` 与 `## desktop/Tower.cs` 一节。 |
| `desktop/TodoTower.cs` | `blocks/todo-tower/Tower.tsx`、`Tower.css` | 设计移植 | 岛内视图（纸片、勾选抽出、拖拽、静止即停帧）。同上。 |
| `desktop/verify/tower-tests.cs`、`tower-tests.cmd` | — | 本项目自写 | 验证上述移植的行为，不含上游代码。 |
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

## `desktop/Tower.cs` 的移植边界（Bencho / Todo Tower）

上游的 Todo Tower 是一个 React 块：`Tower.tsx` 把 `lab/rigid.ts`（二维刚体）
和 `lab/spring` 的注释一起内联进组件，卡片由 React 渲染、位置每帧写成
`transform: translate(...) rotate(...)`。本仓库要的是同一个交互（待办叠成塔、
勾掉一张它就被抽走、上面的在重力下落下来、能用手拿起来扔），而项目是 WPF/C#，
不引入 React，也没有 WebView。

**算法照搬、机器重写**，全部落在 `desktop/Tower.cs`（纯 C#，不引用 WPF，
所以能被 `desktop/verify/tower-tests.cmd` 单文件编译成无窗口测试）：

1. **碰撞。** 分离轴（先 A 的坐标系、再 B 的），挑最深的轴，把对方的棱裁到
   这条轴的侧棱之间，得到 1~2 个接触点；棱编号组成 4×4bit 的特征键，用来在
   相邻两步之间认「同一个接触」（warm starting 的前提）。
2. **求解。** 顺序冲量，12 次迭代，摩擦按 `sqrt(μa·μb)` 合成、以 `μ·pn` 截断；
   位置修正按深度成比例、**封顶**（`PUSH`）。封顶是必须的：手把一张纸片按进
   塔里时重叠能有整张卡那么深，不封顶就是以「被弹射」的形式解开重叠。
3. **静止接触不算撞击。** 接近速度低于 `RVEL` 时不算回弹，否则一摞接触会
   互相「回弹」成一个嗡嗡抖的塔。`Restitution = 0`：砖头落地就是落地。
4. **手（`Steer`）。** 两道限制都保留：手是让物体**加速**朝目标速度走（封顶
   `accel`），不是直接重写速度；而且不会继续往「已经顶住的接触」里推 ——
   对手里的物体每一条比阈值更深的接触，把手速度沿该法线朝对方体内的分量减掉。
   另外保留「骑手（`Riders`）只拿得到 μ·g·GRIP 的侧向速度、向上不封顶、
   向下只有重力」这条，甩一下才能真的把上面的纸片甩掉。
5. **静止即停帧。** 上游是 `requestAnimationFrame`，全部物体慢下来 60 步后
   自己停；本项目挂在 `CompositionTarget.Rendering` 上，同样在静止后摘掉回调
   （岛不该因为一座站着不动的塔一直重绘）。

**没有**取的部分：React 组件、`framer-motion`、`lucide-react`、CSS 过渡
（滑出动画按同一条 220ms 曲线在本项目里用逐帧插值写）、合成音效
（`sounds/`；岛没有音频通路，也不该为一块待办开一条）。

按本项目做的改动（都已写进 `Tower.cs` / `TodoTower.cs` 的文件头注释）：

1. **坐标系。** 物理保持「y 向上、角度逆时针」（和上游一致），转换只发生在
   绘制层（`TodoTower.Place`：`Top = roomH - y - hh`、`Angle = -a`），
   物理不必知道 WPF 的 y 是往下的。
2. **尺寸缩放 `K = 卡片高 / 46`。** 上游是在 46 高的卡片、320 高的房间里调的。
   岛上的房间只有 150 高、卡片 28 高，直接搬 2550px/s² 会快得像掉落动画。
   加速度与速度阈值、接触容差一并乘 K，只缩放「带时间的量」和长度，
   无量纲的（摩擦、迭代次数、GRIP/CARRY）不动。
3. **没有翻倒的余量。** 上游的卡片宽 206、房间 300，左右各留 47px，所以
   「大半悬空就翻下去」经常发生。岛上的面板只有 468 宽，纸片几乎占满房间
   （每边 12px）。被拖出来、被甩到墙上、被砸到的纸片照样会转、会翻，但
   「站在塔上翻下去」需要横向半个卡片的余量，这里没有。这是取舍：优先保住
   「叠得住、勾掉就掉、拖得动」，没有为了翻倒把待办正文挤成一条。
4. **`Slide` 换成 `Scroll`。** 上游用 `slip` 旋钮同时决定摩擦和初始歪斜；
   岛上没有旋钮，取默认值 45（μ≈0.56），歪斜用上游那张写死的 `LEAN` 表。
5. **两种视图共用一座塔。** 上游只有「未完成」一摞。岛上原来那一页把已完成
   的也列出来、点一下能改回来，还有「清空已完成」。为了不砍掉这些，保留了一个
   「已完成 N / 返回待办」的切换：同一套物理、同一套 `Todos.Toggle`，只是取
   哪几条待办做纸片不同。
6. **卡片不会丢。** 上游有一层「非有限值就放回房间里」的兜底；这里除了保留
   那一层，还加了两道：每帧把所有纸片的位置夹回房间内；指针按键的真实状态
   每帧用 `Native.LeftButtonDown()` 复核，松手/丢捕获/窗口失活都会结束「手持」
   状态，纸片不会永久停在手里。