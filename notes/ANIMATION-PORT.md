# 桌宠（Pet）代码接入说明 · ANIMATION-PORT

> 面向后续 agent 和接手的人。这一阶段只做「代码 + 构建」接入：
> 主程序启动逻辑、岛菜单都没动。桌面端的 `--pet` / `--pet-demo` / `--pet-selftest`
> 三个开关依旧是 `desktop\Program.cs` 里原来那套，一点没改。

## 1. 单一源码树（最重要的一条）

桌宠的实现只有一份，在 `pet-demo\`。`desktop\build.cmd` **直接从 `..\pet-demo\` 编译**这七个文件：

```
AnimationCore.cs   PetEase / AnimationMath / PetRandom / PetSpring / PetKey / PetTrack / PetImpulse
PetClips.cs        PetEyePose / PetFaces / PetFaceKey / PetClip / PetClips（自编剧目）
ReferenceClips.cs  28 段参考动画的 JSON 加载、分组与查表
PetShape.cs        形状 profile、球面投影、胶囊眼
PetAnim.cs         PetExpression / PetFrame / PetInput / PetAnimationRuntime / PetAnim
Pet3D.cs           3D 球体、眼睛 mesh、正交相机场景
Pet.cs             PetWindow（窗口、命中、拖动、自检）
```

绝对不要复制到 `desktop\` 再改。`desktop\Pet.cs`、`desktop\PetAnim.cs`、`desktop\PetShape.cs`
是**旧实现，已经移出编译列表**，文件头加了「不参与编译」的说明，只为查历史差异留着。
把任意一份加回编译列表 = 同一个 `WinIsland` 命名空间里出现两个 `PetWindow` /
`PetAnim` / `PetShape`，直接编译失败。

### 为什么排除 pet-demo 的 Program.cs 和 DemoSupport.cs

- `Program.cs`：主项目已经有 `desktop\Program.cs` 的 `static class Program` 和 `IslandApp`，
  再来一份就是重复定义；而且它带 `System.Windows.Forms` 托盘菜单，主项目不需要。
- `DemoSupport.cs`：里面重新声明了 `Native`、`Ui`、`AppDir` 三个类型，和
  `desktop\Native.cs`、`desktop\Ui.cs`、`desktop\Store.cs` 里的同名类型直接冲突。
  它只是「让 demo 能独立跑」的垫片。

反过来，独立 demo（`pet-demo\build.cmd`）必须带上这两个文件，所以它和主项目是
**不同的编译组合、同一批动画源码**。

## 2. 构建接线

| | `desktop\build.cmd` | `pet-demo\build.cmd` | `pet-demo\test.cmd` |
|---|---|---|---|
| 输出 | `WinIsland.exe`（winexe） | `PetDemo.exe`（winexe） | `AnimationTests.exe`（exe，跑完即退） |
| 动画源码 | `..\pet-demo\` 七个文件 | 同七个 + `DemoSupport.cs` + `Program.cs` | 七个（不含 `Pet.cs`）+ `AnimationTests.cs` |
| 嵌入资源 | `..\pet-demo\grok-clips.json` → **`WinIsland.grok-clips.json`** | 同名同法 | 同名同法 |
| `System.Web.Extensions` | 需要（`JavaScriptSerializer`） | 需要 | 需要 |
| `/codepage:65001` | 有 | 有 | 有 |

三个脚本里的资源名必须完全一致：`ReferenceClips.Load()` 是按
`WinIsland.grok-clips.json` 这个名字从清单里取的，改一处就得三处一起改，否则
启动直接抛 `InvalidDataException("Embedded reference clips are missing.")`。

`/codepage:65001` 不是可选项：动画源码是 UTF-8 无 BOM，不带这个参数时
csc 按系统 ANSI 代码页读，`Title = "桌宠"` 这类字符串会变成乱码（能编过，但显示是错的）。

`desktop\build.cmd` 在编译前会检查 `..\pet-demo\` 下七个 `.cs` 和 `grok-clips.json`
是否存在，缺了就直接报 `[x] missing ...`，而不是让 csc 抛一串 CS2001。

## 3. 主项目接口适配（逐项核对结果）

新版 `Pet.cs` 只依赖三个外部类型。核对后只有一处是主项目缺的，已补：

| pet-demo 用法 | 主项目对应 | 状态 |
|---|---|---|
| `Native.GetExStyle` / `SetExStyle` | `desktop\Native.cs` | 原有，签名一致 |
| `Native.WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW` / `WS_EX_TRANSPARENT` | 同上 | 原有 |
| `Native.LeftButtonDown()` | 同上 | 原有 |
| `Native.TryGetCursor(out Point)` | 同上 | 原有 |
| `Native.Capture(x,y,w,h)` | 同上 | 原有 |
| `Native.ScreenWidth` / `ScreenHeight` | 同上 | 原有 |
| `Native.KeepTopmost(hwnd)` | **本次新增** | `SetWindowPos(HWND_TOPMOST, NOMOVE\|NOSIZE\|NOACTIVATE)`，只给自检垫探针窗时用 |
| `AppDir.ReadLines(name)` → `string[]` | `desktop\Store.cs` | 原有；失败返回空数组而不是 null，`Pet.cs` 两个都判 |
| `AppDir.WriteLines(name, List<string>)` | 同上 | 原有，签名一致 |
| `Ui.Br(Color)` | `desktop\Ui.cs` | 原有；新版 `Pet.cs` 已经不用它（3D 材质自己建刷子） |

一个副作用值得记：两边 `AppDir` 根目录不同（主项目 `%APPDATA%\WinIsland`，
demo `%APPDATA%\XiaodaoPetDemo`），但文件名都叫 `pet.txt`。所以桌宠位置在旧实现和
新实现之间**是无缝迁移的**，`--pet` 打开还停在原来的位置。

## 4. 对外接口（给后续 agent 用）

### PetWindow（`Pet.cs`）

```csharp
PetWindow(string logPath, bool demo)   // logPath 为空 = 不打日志；demo = 每 2.6s 自戳/自转
void Poke()                            // 戳一下：压扁回弹 + Surprised
void Spin()                            // 转一圈：yaw 走满 2π + Happy，背面不画眼睛
void SetState(string state)            // "idle" / "happy" / "thinking" / "sleepy" / "curious" / "morph"
void PlayClip(string clipName)         // 直接播某段参考动画，名字见 ReferenceClips.Groups
string RunSelfTest()                   // 自检，返回 JSON 文本，同时写 exe 目录 pet-selftest.json
long Frames { get; }                   // 已经渲染过多少帧（验证用）
bool Ignoring { get; }                 // 当前是不是穿透态
```

窗口自身契约（和岛一致，别改）：透明 + 无边框 + 不进任务栏 + `WS_EX_NOACTIVATE`；
整窗默认穿透，鼠标落进球体才关穿透；命中靠 10ms 轮询 `GetCursorPos`（穿透窗收不到
鼠标消息）；拖动阈值 3px；位置存 `pet.txt`。

关窗清理在 `OnClosed`：停掉 `_hit` / `_demo` 两个 `DispatcherTimer` 并置空、
`StopAnimating()` 摘掉 `CompositionTarget.Rendering`、停表、解绑
`Loaded` / `SourceInitialized` / `Closed`。`Rendering` 是静态事件，漏一次 `-=`
就是永久泄漏，下次开窗两路动画叠加。

### PetAnimationRuntime（`PetAnim.cs`）

```csharp
new PetAnimationRuntime(uint seed)
PetFrame Frame { get; }
string State { get; }
void SetState(string s)      // 换片段池，走 0.35s 桥接，不跳变
void PlayClip(string name)   // 未知名字抛 ArgumentException
void Poke(); void Spin()
void Advance(double seconds) // 内部固定 1/120 步长，单次最多吃 0.1s
```

`Advance` 是「喂时间」而不是「算第几帧」：渲染掉帧只少画几帧，动画内容不变。
`PetAnim.Sample(PetInput)` 是纯函数版本（自己内部建 runtime 重放到目标时刻），
自检用它验可重放性。

### 数据与查表

```csharp
PetExpression  Neutral, Happy, Surprised, Sleepy, Focused, Sad, Angry, Curious, Wink
PetFrame       GazeX GazeY Yaw Pitch Roll Lid ShapeIndex MorphAmount SquashX SquashY
               Zoom OffsetX OffsetY Expression LeftEye RightEye HasEyePose HasMorph State
PetEyePose     Width Height Tilt Bend OffsetY（+ Blend(a,b,t)）
PetClips.Idle / PetClips.MorphDemo / PetClips.ForState(string)
ReferenceClips.All            // 28 段，按加载顺序
ReferenceClips.Idle           // Idle_A / Idle_B / Idle_D
ReferenceClips.Find(name)     // 找不到返回 null
ReferenceClips.Groups         // 9 组，菜单照这个顺序建，正好覆盖 28 段
ReferenceClips.DefaultZoom    // 0.6，Clip 里的 scale 轨道要除以它
Pet3D.Scene(PetFrame)         // 整个 Model3DGroup，窗口每帧只换这一层
Pet3D.GazeShift(v)            // clamp(v, -1, 1) * 0.62，视线到球面偏移的增益
Pet3D.SphereMesh / EyeMesh    // 自检会数顶点，签名别动
PetShape.Body / Face / EyePair / Profile / PickShape / Names / Samples
```

`ReferenceClips.Groups` 的九组 = Blocked / Bodymoves / Done / Idle / Logo /
Marketing / Rotation / Streaming / Working；`test.cmd` 会断言
「各组条目数之和 == 28 且每段中点都能出可渲染的帧」，加片子要同步更新这个数字。

### 状态名 / Clip 名

- 自编 idle 剧目：`idle-rest` `idle-glance` `idle-happy` `idle-look-up` `idle-sleepy` `idle-wink` `idle-morph`
- 交互态：`poked` `spin`
- `SetState` 的键：`idle`（默认池）、`happy`、`thinking`、`sleepy`、`curious`、`morph`
- `PlayClip` 之后 `State` 就等于 clip 名本身（例如 `Bodymoves_A`），便于断言

## 5. 必须保持的规则（改动画前先读这段）

1. **颜色 `#5a4ac8`、纯色无高光。** `Pet3D.BodyMaterial()` 用
   `DiffuseMaterial(SolidColorBrush(0x5A,0x4A,0xC8))` + `AmbientLight(White)`：
   全屏均匀上色，没有方向光就没有高光。自检里 `flatPurplePixels > purplePixels*0.95`
   就是钉这条的，加高光会立刻打红。
2. **眼睛是放大的胶囊，画在球面上。** `Pet3D.EyeMesh` 造的是圆角矩形
   （`Min(width,height)/2` 当圆角半径，`RectangleGeometry` 语义），所有顶点投影到
   半径 `1.015` 的球面上、距球心 `MaxEyeRadius=0.97` 以内；`AddSurfacePoint` 负责
   把出界的点夹回来。自检断言 `eyeProjectedWidth 0.2000 / eyeProjectedHeight 0.2500`、
   `eyeSurface`、`gazeSurface`、`capsuleEyes`。
3. **眼睛不是挖孔。** 旧实现靠 `Geometry.Combine(Exclude)`，新版是独立实色层
   （`0xF4F3FF`）。桌宠背后是桌面壁纸，挖孔会让眼睛跟壁纸同色等于消失。
4. **自主张望。** 视线完全由 clip 的 gaze 轨道驱动，**不看鼠标**：
   `gazeIgnoresPointer` 断言「同样的 Time、不同的指针坐标 → 同一帧」，
   `wanderingRangeVisible` 断言 60 秒内 gazeX 的摆幅 > 1.1。别把指针接回来。
5. **morph 藏在眨眼后面。** 形状过渡窗口里 `Lid` 被强制抬到 ≥0.94，
   `morphBehindBlink` 钉这条；`idleShapeReturnsToBall` 要求非 morph 片段里
   `MorphAmount ≈ 0`（大部分时间就是个球）。
6. **呼吸幅度 0.0055。** `SquashX/Y` 反相 ±0.0055，`breathRange` 断言约 0.0064。
7. **固定步长 1/120、单次最多 0.1s、60fps 渲染上限、几何按量化 key 重建**
   （`Key(PetFrame)` 的步长是「看不出差别的最大步长」）。这三条一起保证了
   「帧率高低不影响动画内容」，`AnimationTests` 里有专门的 delta 分组对比。

## 6. 验证

```cmd
cd E:\xiaodao\pet-demo  && build.cmd        :: [ok] PetDemo.exe built
cd E:\xiaodao\pet-demo  && test.cmd         :: 13 项 PASS，退出码 0
cd E:\xiaodao\pet-demo  && PetDemo.exe --selftest   :: 写 pet-demo\pet-selftest.json，exit 0
cd E:\xiaodao\desktop   && build.cmd        :: [ok] WinIsland.exe built
cd E:\xiaodao\desktop   && WinIsland.exe --pet-selftest   :: 写 desktop\pet-selftest.json，exit 0
```

`--pet-selftest` 的判定是 `pet-selftest.json` 里 `"ok": true`（`Program.cs` 用
`report.IndexOf("\"ok\": true")` 判断，再设 `Environment.ExitCode`）。
这台机器（DPI 1.5）上应当看到：`bodyRatio 0.330`、`purplePixels 36045`、
`basePurplePixels 35361`、`flatPurplePixels 35361`、`eyePixels 1075`、
`idleStateCount 7`、`idleExpressionCount 6`。数值和 pet-demo 那次已验证的运行完全一致，
只有 `frames` / `elapsed` 会因机器负载浮动。

关窗清理另有探针，不在 build 里，手动编手动跑：

```cmd
cd E:\xiaodao\pet-demo
csc /target:exe /platform:x64 /out:..\notes\probes\probe-petclose.exe /codepage:65001 ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationFramework.dll ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\PresentationCore.dll ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Xaml.dll ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll ^
    /r:%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.dll ^
    /resource:grok-clips.json,WinIsland.grok-clips.json ^
    DemoSupport.cs AnimationCore.cs PetClips.cs ReferenceClips.cs PetShape.cs ^
    PetAnim.cs Pet3D.cs Pet.cs ..\notes\probes\probe-petclose.cs
..\notes\probes\probe-petclose.exe
```

期望 `ALL CLEAN`。它验的是：开窗后有帧、有动画；关窗后 `_hit`/`_demo` 置空、
`_animating` 归 false、秒表停；再开一关计数依旧为 0；最后强制 GC，
关掉的窗口必须能被回收（`Rendering` 上留了委托的话，静态事件就是 GC 根，收不掉）。

## 7. 已知坑

- **exe 被占用 → CS0016。** 桌宠/岛正在跑的时候 `csc` 写不进去，报
  `CS0016 无法写入输出文件…正由另一个进程使用`。先 `taskkill /im WinIsland.exe /f`。
- **自检会垫一块品红探针窗**并抓屏统计像素。跑 `--pet-selftest` 时别再开别的
  topmost 窗口压在同位置，否则紫色像素计数会被污染。`KeepTopmost` 只是把桌宠
  压回最上层，不解决重叠的第三方窗口。
- **`pet-selftest.json` / `pet-selftest.png` 写在 exe 同目录**，不是 `%APPDATA%`。
  所以 desktop 和 pet-demo 各有一份，互不覆盖。
- **`grok-clips.json` 是 119KB 的真数据**，`ReferenceClips.Load()` 会断言恰好 28 段，
  段数变了要先改这个断言再改 `Groups`。
- **`PetShape.PickShape/Hash` 新版不再被 idle 用到**（形状改由 `idle-morph` 片段的
  Shape=1 + morph 轨道驱动），但自检和旧断言还在引用 `PetShape.Body` 之类的几何函数，
  别删。

## 8. 第二阶段：桌宠与岛同进程共存（已完成，本节取代原来的「没做的事」）

原来的结论是「岛菜单没有桌宠入口、主程序启动逻辑未改、桌宠只能靠 `--pet` 起」。
第二阶段把这条打通了，下面是现在的真实行为。

### 8.1 进程形态

| 启动方式 | 建岛 | 建桌宠 | 用途 |
|---|---|---|---|
| 无参数（正常启动） | ✅ | ✅ 默认开（按 `ui.txt` 的 `pet=`） | 日常使用 |
| `--no-pet` | ✅ | ❌（本次，不写偏好） | 验收 / 只想要岛 |
| `--selftest`、`--wda-probe`、`--live-probe`、`--freeze-shot`、`--heartbeat`、`--todo-open`、`--notify*`、`--exclude-capture` | ✅ | ❌ 不自动弹 | 诊断 / 抓帧 |
| `--pet` / `--pet-demo` | ❌ | ✅ | 桌宠独立诊断（行为未变） |
| `--pet-selftest` | ❌ | ✅ | 桌宠自检（行为未变） |

「诊断模式不弹桌宠」是在 `Program.cs` 里算一个 `diagnostic` 再传给岛，
**不是**在岛里散落一堆 if，也**不写** `ui.txt`：诊断跑一百次也不会改掉用户偏好。

正常启动**只读**偏好、不写。第一次启动（`ui.txt` 里没有 `pet=` 这一行）走默认值
「开」，文件里依旧没有这一行 —— 「默认」和「用户的选择」因此不会混成一件事。
真正写盘只发生在菜单「启用桌宠」和「点桌宠动作顺手把它开起来」这两处。

### 8.2 桌面窗口

> **已被第 10 节取代。** 集成版不再有独立桌宠窗口：桌宠是岛可视树里的一个节点，
> 遮挡由绘制顺序保证，不靠两个 `Topmost` 争 z-order。本节保留下来是因为
> `desktop\PetController.cs` 还在，`--pet*` 那几条诊断路径仍在用独立窗口。

- 两个窗口**同一个进程**：岛（无标题，`HwndWrapper[WinIsland.exe;…]`）与桌宠
  （`Title="桌宠"`，220×220 DIP）。
- 桌宠**不设 Owner**。设了之后它会被岛的关闭/最小化带着走，而且 WPF 会做
  owned-window 的 z-order 联动 —— 那和「桌宠在桌面上自己待着」是两件事。
- 位置分开存：岛 `%APPDATA%\WinIsland\window.txt`，桌宠 `…\pet.txt`，互不覆盖。

### 8.3 代码入口

> **集成版的路由见第 10 节。** 下表中 `PetController.cs` 一行已不再参与
> 主路径：桌面端菜单动作现在直接调 `PetIsland.cs` 里的 `PetPoke` /
> `PetSpin` / `PetSetState` / `PetPlayClip` / `PetRandomIdle`，
> 它们驱的是岛自己那个 `PetStage`。`PetController` 只留给 `--pet*` 诊断。

| 位置 | 职责 |
|---|---|
| `desktop\PetController.cs`（新） | 单例闸门 + 全部桌宠动作 + `Release()` 统一释放 |
| `desktop\Program.cs` | `diagnostic` / `petAuto` 判定，`--no-pet`，`PetController.Init(log)` |
| `desktop\IslandWindow.cs` | `OnLoaded` 按偏好启用；菜单动作六件；`BeginShutdown` + 外部关闭兜底释放 |
| `desktop\Menu.cs` | `Menu.Build(..., petOn, petState)`，桌宠子菜单 |
| `desktop\Store.cs` | `Prefs.Pet`（默认 true），`ui.txt` 的 `pet=` |
| `pet-demo\Pet.cs` | 只读 `State` 属性（菜单勾选靠它，不另记一份「上次点了什么」） |

菜单结构：`桌宠 ▸ 启用桌宠 / 戳击 / 转圈 / 表情模式(6) / 全部动画(9 组 28 段) / 恢复随机待机`。
28 段直接从 `ReferenceClips.Groups` 生成，不在菜单里另抄一份名单。

### 8.4 释放

> **集成版的释放见 10.7。** 集成版没有第二个窗口，`PetController` 那条
> `Application.Exit` 兜底对它不适用；真正管事的是 `ReleasePet()` +
> `StopTimersForShutdown()`。

`PetController` 在第一次 `Enable()` 时往 `Application.Exit` 挂一次钩子，
覆盖岛那六条 `Shutdown` 路径（菜单退出、自检结束、抓帧结束、WDA 探针、采样探针、
以及窗口被外部关掉）。`BeginShutdown()` 也显式 `Release()` 一次 —— 重复调用幂等。

另外补了一条原来没有的缝：`ShutdownMode` 是 `OnExplicitShutdown`，所以外部
`WM_CLOSE`（任务栏「结束任务」）只会关掉岛窗口、进程留着重桌宠。现在岛的
`Closed` 事件会收掉桌宠并退出（用 `_closing` 旗避免和 `BeginShutdown` 重入）。

## 9. 这一阶段没做的事

- 没有引入 WebView / Node / 子进程；全部是 C# 5 + .NET Framework 4.8 + WPF，
  `csc.exe` 一步编完。
- 桌宠的「启用」开关只在右键菜单里，没有托盘图标，也没有快捷键。
- 28 段动画没有预览缩略图，只有名字。

## 10. 第三阶段：并进岛的那一层合成（已完成；**第 8.2 节「两个窗口」已被本节取代**）

这一阶段把桌宠从「另一个置顶窗口」改成「岛自己可视树里的一个节点」。

### 10.1 为什么必须改

第 8 阶段的两个窗口都是 `Topmost`。`Topmost` 在 Win32 里不是一个全局序，
而是「一大票置顶窗口内部的先后」，这个先后由**最后一次 `SetWindowPos`、
激活、以及系统自己的排队**共同决定。两件事因此无法保证：

1. 岛体**遮挡**球这件事。需求是「出场时球先在岛体后方，被岛挡住，
   移出边界之后才显露」。两个独立窗口做不到「挡住」——后置顶的那个必然
   盖住另一个整体，包括本该被遮住的部分。
2. 顺序稳定。用户点了别的窗口、切了桌面、系统重排 z-order 之后，
   岛的球跑到岛前面是随机的。

所以集成版只有**一个**窗口、一层合成：遮挡由 WPF 的绘制顺序保证，
不依赖任何 `Topmost` 的胜负。

### 10.2 一套合成、两个 z 槽

`_root`（岛窗口的根 Canvas）里现在插了两个槽位：

```
_root
 ├─ _shadowLayer
 ├─ _petUnder      ← 岛体**之前**：这里画出来的东西会被岛体挡住（真遮挡）
 ├─ _ballDock
 ├─ _host         ← 岛体本体（含 Squircle 圆角裁剪）
 │   ├─ _refract / _wash / _tint / _spec / _glow / _grain / _edge
 │   ├─ _petDock   ← 岛体**之内**：继承岛的圆角裁剪，于是「藏在岛里」是免费的
 │   └─ _content
 └─ …
```

- **第一层（idle）**把球放在 `_petDock`。它是 `_host` 的子节点，而 `_host`
  有 `Clip = Squircle.Build(...)`，所以球超出岛体轮廓的部分**自动被裁掉**：
  「藏在岛内」不需要额外代码，也不需要额外的不透明度技巧。
- **第二、三层**把球放到 `_petUnder`。它在 `_host` 之前，于是岛体
  在它上面绘制 → 球被岛真的挡住。显露时机由 `UpdatePetUnderClip` 控制：
  把窗口矩形减去「膨胀过的岛体轮廓」得到裁剪域，球只在这块区域里可见。
  轮廓几何按量化后的尺寸缓存，避免每帧重建。
- 只有一个 `Shell`（`PetStage` 的 `Viewport3D`），换层时**改父节点**，
  绝不复制。于是「不能同时出现两个球」「不能留残影」是由结构保证的，
  不是靠时序小心维持的。

### 10.3 一套动画、一个更新循环

`PetStage`（`pet-demo\PetStage.cs`）把「3D 场景 + `PetAnimationRuntime` +
相机/位移/去重」收成一个可复用的部件。岛持有**一个** `PetStage`，
由岛本来就有的 `OnFrame`（`CompositionTarget.Rendering`）推进：

```
IslandWindow.OnFrame → AdvancePet(now) → _petStage.Advance(dt) → PlacePet()
```

没有为「岛内那颗」和「出场那颗」各起一套动画、各挂一个 Rendering 回调 ——
那正是需求里明确要避免的东西。`AdvancePet` 里有 60fps 闸门，
`PetAnimationRuntime` 自己是 1/120 定步长，所以帧率变化不影响动作速度。

### 10.4 尺寸：原球多大就多大

第一层的球径取岛第一层原球的**实测可见直径**：

```
Layout.IdleOrbSize(w, h) = min(w, h) * IdleOrbRatio   // IdleOrbRatio = 0.56
`52 × 30` → `30 × 0.56 = 16.8 DIP`
```

依据是原球在 `52×30` 里实际占多大（不是面板尺寸，也不是猜测值）。
被隐藏的原球是 `Ui.Orb`——`Rebuild()` 里当 `petDocked` 为真时**根本不建**
这个节点，所以「隐藏原球」不是设透明度，而是它压根不存在。

出场尺寸是它的两倍（`PetGrow = 2.0`，33.6 DIP）。`PetStage` 是按
**相机宽度**改大小的（`_camera.Width = 2*_shellSize/_diameter`），
不是靠 `RenderTransform` 缩放 —— 后者会把 3D 内容拉伸成软的，
16.8 DIP 那种尺寸下糊得没法看。

### 10.5 过渡：换层点选在「画面不变」的那一刻

时间线 `t ∈ [0, 0.44]`：

| 区间 | 槽位 | 直径 | 不透明度 | 位置 |
|---|---|---|---|---|
| `[0, 0.10]` | `_petDock` | 16.8 恒定 | `1 → 0` | 岛心 |
| `[0.10, 0.44]` | `_petUnder` | `16.8 → 33.6` | 1 | 岛心 → **左侧**停靠点 |

球**从岛体左边缘移出去**，停在岛左边 `PetGap` 处（实测间隙 7.6~8.0 DIP）。
因为球先整颗在岛体轮廓里、往左移动，出轮廓的那一刻它才「显露」，
这就是「先躲在岛后、移出边缘再出现」的实际形态。

回程沿同一条曲线倒放。换层发生在 `t = 0.10`，此时：
岛内那颗已经淡到 0，岛下那颗**整颗埋在岛体轮廓里**（球心还在岛心，
球径 16.8 而岛体 52×30）——两边都不可见，所以换层那一刻画面上什么都不会变。
探针把这一条量成了断言：`slotSwitches == 1` 且 `switchOffsetDip < 0.6`。

> 这里曾经写过一个 bug：岛内那颗在淡出的同时缩到 78%（`d1*(1-0.22*fade)`），
> 而岛下那颗一接手就是整尺寸 16.8，直径于是在换层那一帧硬跳 3.7 DIP。
> 因为那一刻两边都不可见，肉眼看不出来 —— 是逐帧轨迹里的 `diaStep` 把它抓出来的。
> 现在只淡出不缩，直径全程连续。

### 10.6 窗口宽度：预留加在**左侧**，且只在实际开着时才加

停靠点在岛体**左侧**（球从左边出来）`PetGap(8) + d2(33.6)` 处，所以窗口要预留
`PetReserve = 8 + 16.8*2 + 6 = 47.6 DIP`。这块预留**只在桌宠真的开着时才加**：

```csharp
// 尺寸
double nw = def.ExpW + MX*2 + (_notify ? NotifyReserve : 0) + (_petOn ? PetReserve : 0);
// 位置（SyncPlacement）：窗口向左长 PetReserve，_ix 同步右移同样的量
double lead = MX + (_petOn ? PetReserve : 0);
double wl = ClampD(ClampD(_anchorX - def.ExpW/2, vl, vr - def.ExpW) - lead, vl, vr - Width);
```

两处都要改，只改尺寸是不够的：`_ix` 的定义是「岛体左边 − 窗口左边」，
窗口只加宽不加 `lead` 的话，多出来的 47.6 DIP 全长在右边，球在左侧依旧没地方站
（展开态左侧原本只有 `MX = 24`，而球要 41.6）。加了 `lead` 之后：

| 状态 | `_ix`（改前 → 改后） | 左侧可用 | 右侧可用 |
|---|---|---|---|
| idle 52 宽 | 224 → 271.6 | 271.6 | 275.6 |
| compact 112 宽 | 194 → 241.6 | 241.6 | 193.6 |
| expanded 452 宽 | 24 → 71.6 | **71.6** ✔ | 23.6 |

改前展开态左侧只有 24，程序只能把球挤到右边去 —— 所以「从左边出来」这件事
不是改一个字号就能做到的，**必须同时把预留挪到左边**。

恒留会让窗口比原来宽 47.6 DIP，岛在窗口里的偏移 `_ix` 跟着变，
既有验收脚本按「窗口 = `ExpW + 48`」量出来的像素位置就整体偏 23.8 DIP。
桌宠关着（也就是所有既有验收脚本的默认形态）时必须逐像素不变。
开关桌宠走 `ApplyPetReserve()`：改尺寸 → `SyncWindowSize()` →
`ForceClickThroughStyle()`（改尺寸会重建合成属性，穿透有可能被清掉）。

停靠侧的选择（`PlacePet`）：**优先左边**；左侧放不下（岛贴屏幕左缘、
窗口左边距被压没了）才退到右边，避免球被窗口边缘切掉；两侧都不够时取宽的一侧
（窗口预留本应保证走不到这一步）。

### 10.7 释放

集成版**没有第二个窗口**了，所以「关岛就没有球在跑」完全取决于 `ReleasePet()`：

- `Shell` 从可视树摘下（`UnmountPet`）
- `_petStage = null`（runtime 不再被推进）
- `PetWantsFrames` 归假 → `OnFrame` 的续跑条件不再成立，
  `StopAnimating()` 会把 `CompositionTarget.Rendering` 摘掉
- `StopTimersForShutdown()` 收三个 `DispatcherTimer`

`BeginShutdown()` 与 `OnIslandClosed` 都调 `ReleasePet()`，重复调用幂等。
漏掉 `Rendering` 那一环的后果很具体：它是**静态事件**，会把窗口当 GC 根钉住，
进程退不干净。

### 10.8 验收

| 工具 | 管什么 |
|---|---|
| `WinIsland.exe --pet-probe` | 三层 + 往返 + 过渡轨迹 + 菜单动作链路，写 `pet-island-probe.{json,txt}` 与 5 张 PNG |
| `desktop\verify\pet-island.ps1` | **从进程外面**数顶层窗口（必须恰好 1 个、且不存在标题为「桌宠」的窗口），再校验上面的 JSON |

为什么窗口数要从外面数：代码里写「不再 `new PetWindow`」不算证明，
只有 `EnumWindows` 数一遍才能确定「只有一个窗口」真的成立。
注意用 `EnumWindows` 而不是 `Get-Process.MainWindowHandle` —— 后者只给
「一个主窗口」，恰好会把第二个置顶窗口藏起来，而那正是要否掉的东西。

过渡的平滑度**不能**靠抓像素量：软件渲染一张全尺寸窗口（`_refract` 上挂着
模糊）要 80~300ms，实测一拍就差 0.35s，连 0.44s 的行程都解析不出来。
所以改成记录动画自己的输出（`_petTrace`：时间线/直径/球心/不透明度/槽位），
一次行程就有二十多帧真值；像素只用来确认「画面上确实只有一个球」。

## 11. 第四阶段：文件拖拽注视（已完成）

### 11.1 参考实现与许可

跟随算法移植自 Bencho 的 Eye tracker（MIT）：

- 上游：`https://github.com/cabralorenzo/bencho` → `blocks/eye-tracker/Watcher.tsx`
- 许可：MIT，`Copyright (c) 2026 Lorenzo Cabra`
- 完整许可文本与出处写在 `pet-demo/PetGaze.cs` 文件头尾，随源码一起走

**移植了什么**：方向归一化、`tanh` 距离饱和、双轴弹簧跟随、双眼共用一组
yaw/pitch 在球面上转。

**没移植什么**：参考实现里那套 SVG 上的「simulate foreshortening」缩放。
这边是真 3D——眼睛是球面补丁，透视缩窄和互相遮挡由几何和 z-buffer 负责。
两套一起用会重复变形，所以只保留真实几何那一路。

### 11.2 数学（`pet-demo/PetGaze.cs`）

```
far  = min(cap, tanh(d / reach) * (follow/100) * 1.5)
reach= max(60, min(w,h) * 0.28)          % 参考实现里是像素；这里换算成球半径倍数
k    = 0.06                              % 弹簧刚度（参考实现）
d    = 0.34                              % 阻尼系数（参考实现）
yaw  = asin(clamp(t.x, -0.92, 0.92))
pitch= -asin(clamp(t.y, -0.92, 0.92))
```

参考实现的 `k/d` 是 60Hz 尺度下的经验值，这里的动画是固定 120Hz
（`PetAnimationRuntime.Step = 1/120`），所以换算成：

```
ω = sqrt(0.06) * 60 = 14.697 rad/s
ζ = 0.34 / (2 * sqrt(0.06)) = 0.694
```

`reach` 用「球半径倍数」而不是绝对像素：岛内 16.8dip 的球和岛旁 33.6dip 的球
在同样的**相对**距离上有同样的偏转，这样换层、长两倍都不用改参数。

### 11.3 眼几何（`pet-demo/Pet3D.cs`）

眼睛不再「平移两个圆心」，而是：

1. 静止点在半径 `EyeSurfaceRadius = 1.015` 的球上
2. 按 `yaw/pitch` 旋转该点得到锚点
3. 在锚点处取切平面基（`right = cross(worldUp, n)`、`up = cross(n, right)`）
4. 补丁的每个顶点投影回球面并重新归一化到 `1.015`

于是靠近轮廓的眼睛自然被压窄（实测 **53.4%**），高度不变，且始终贴着球面。
`EyeFrame` 用世界朝上而不是旋转后的静止基，与「贴花」相比有约 2° 滚转差，
换来的是一条几何通路——这个取舍写在文件里。

`FitGaze` 用 24 步二分把注视量压到「双眼都还在 `MaxEyeRadius = 0.97` 圆盘内、
且 `z >= EyeFrontZ = 0.12`」的范围，所以极端角度不会穿到轮廓外。

### 11.4 岛侧接线（`desktop/PetIsland.cs`）

触发条件是**层状态**，不是拖放事件：

- `PetLookTick()` 每帧跑（`AdvancePet` 里排在 `PlacePet()` 之后），
  `want = _state == "expanded"` —— 第三层（岛完全展开，球在岛旁、直径 33.6）
  开着就一直跟；第一层（`idle`，球停在岛内 16.8）和第二层（`compact`）不跟
- 进层：`_petStage.Hold(true)`；退层：`Hold(false)` + `LookAway()`。
  随机张望和随机片段自己回来，不需要另外的定时器
- 坐标：`GetCursorPos` 给物理像素，`screen.X / _dpi - Left` 换成根坐标 DIP ——
  和 `OnHitTick` 算 `localX/localY` 是同一条换算，所以「点在哪儿」和
  「画在哪儿」不会各说一套。球心/球径由 `PlacePet` 每帧写进
  `_petCx/_petCy/_petDia`，并岛换层、长两倍、DPI 缩放全部自动跟上
- 半径取 `_petDia / 2`，`reach` 随球一起缩放
- 日志按偏移量限流（变化 >2 才记一行），第三层一开着就是每秒 60 次调用

**为什么不用 `DragEnter` / `DragOver` 驱动**：那是**事件**，频率跟鼠标一样抖，
而且鼠标不动时就停在最后一次事件的坐标上。「第三层开着就一直盯着鼠标」是
**状态** —— 用状态驱动，鼠标停住时眼睛也停在同一方向，且不要求用户按住左键。

> 追加需求（本次）：这条**取代**了早先「只在 OLE 确认过 `DataFormats.FileDrop`
> 时才跟」的约束。第三层里普通鼠标移动、以及拖文本，都会带动眼睛；拖文件时
> 岛会自动进第三层（`OnDragEnterFiles` 里 `SetState("expanded")`），所以原来
> 「拖文件跟眼睛」的行为仍然在，只是触发条件换成了层。

### 11.5 保持态：第三层期间不做随机动作（`pet-demo/PetAnim.cs`）

`PetAnim` 加了一个 `hold` 位，落地方式是「把底稿换成静止中立帧 + 停掉片段调度」：

- `HoldPose`：`SquashX=SquashY=Zoom=1`、`Yaw/Pitch/Roll/Offset=0`、
  `MorphAmount=0`、`Expression=Neutral`、`State="hold"`
- `BaseFrame()`：`hold ? HoldPose : clip.Sample(clipTime)`
- `Tick()`：`else if(!hold)` 才推 `clipTime` 和 `NextClip()` —— 随机池真正停摆，
  不是「抽到也不显示」
- 进出都从当前画面接（`bridgeFrom = current, bridgeTime = 0`，走同一条 `Blend`），
  所以换姿态是滑的；退出时顺手 `NextClip()`，接上新的随机片段
- `Hold()` 幂等，重复调用不会重起桥接

**为什么不用「冻结当前片段进度」**：片段可能正好停在 squash 或翻滚的中段，
冻在那儿看起来是坏了，不是站着。中立帧是明确姿态，读起来是刻意的。

### 11.6 验收

| 项 | 结论 | 证据 |
|---|---|---|
| 第三层跟鼠标 | 通过 | 真机日志：`state compact -> expanded` → `pet look on (layer3 hold)` |
| 退出第三层恢复默认 | 通过 | `state expanded -> compact` → `pet look off, random pool resumes`，此后零条 `pet look aim` |
| 方向正确 | 通过 | 光标在球心右上 → `0.884,0.467`；右下 → `0.996,-0.088`；**左侧** → `-0.185,0.983` |
| 保持期间无随机动作 | 通过 | 连拍 5 帧球 bbox 恒为 `46,55-94,104`，面积 1817–1832（±0.8%） |
| 对照：不保持时在动 | 通过 | 同一台机、同一尺寸：bbox `308,27-357,75`→`313,28-361,76`，面积 1766–1828（±3.5%） |
| 保持期间眼睛仍在动 | 通过 | 同 5 帧眼质心 norm x `0.24→0.10→0.56→0.52`，y `-0.46→0.51→0.03` |
| 快速跨越平滑 | 通过 | 弹簧单帧 ≤5.8%（限值 7%），过冲 <8%，横穿 26 帧 |
| 坐标跨层/DPI 正确 | 通过 | 物理光标 (1517,744) → 根 (424,419)，日志 `aim 431.9,**419.0**`（y 逐位吻合，x 差 7.9 = 采样时刻差） |
| 极限角度不穿模 | 通过 | 10 个表情 × 4 个对角，顶点全在 0.97 圆盘内且 `z >= 0.12` |
| 保持态可复用 | 通过 | `hold parks the body…` / `…stops the random clip pool` / `…still lets the eyes follow` / `leaving hold resumes…` / `…bridges back`（单帧 0.0029）/ `holding twice is idempotent` |

**没有验证到的**（如实记录）：

1. 这台机上有个**残留探针进程**（`IslandDragProbe` pid 9032，早先 `cancel`
   模式的崩溃实例，`taskkill` 报 Access denied）仍在偶尔注入鼠标移动，
   所以「我设的光标点」有时会被它挪走。上面的注视数值全部来自岛**自己**读到的
   光标，成对校验过（物理 → 根坐标换算与 `hover` 日志逐位一致），不受影响；
   但「精确停在某个点」的实验做不干净。
2. 第一层（`idle`，球停在岛内 16.8）时的跟手没有单独取像素证据 —— 门控是
   `_state == "expanded"`，`idle` 明确不跟，只由代码路径保证，没有实拍对照。
3. 早先那轮合成拖拽在两种构建上都没走到 `Drop`（`DROP items=0`、
   `transfer.txt` 不变），「文件真的被收下」仍靠**未改动**的既有路径，
   本次没有再测（触发源已从拖放换成层状态，这一项与本功能无关）。
4. 岛在「拖出窗口后松手」时 `_dragIn` 会卡住（`drag leave = 0`，岛不回 idle）。
   既有问题，改它等于改文件接收状态机，本次不动。
