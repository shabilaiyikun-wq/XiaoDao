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

- 两个窗口**同一个进程**：岛（无标题，`HwndWrapper[WinIsland.exe;…]`）与桌宠
  （`Title="桌宠"`，220×220 DIP）。
- 桌宠**不设 Owner**。设了之后它会被岛的关闭/最小化带着走，而且 WPF 会做
  owned-window 的 z-order 联动 —— 那和「桌宠在桌面上自己待着」是两件事。
- 位置分开存：岛 `%APPDATA%\WinIsland\window.txt`，桌宠 `…\pet.txt`，互不覆盖。

### 8.3 代码入口

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