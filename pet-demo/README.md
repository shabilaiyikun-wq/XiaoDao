# 桌宠 Demo

这是一个独立运行、独立构建的 Windows 桌面宠物演示程序，不会启动灵动岛。除原有随机待机外，托盘菜单“参考动画”中提供 GrokBot 参考项目的 28 段关键帧片段，可按类别逐个播放。片段的身体轮廓仍使用本 Demo 的紫球；参考配方里的 clover/cloud 身体网格与完整状态机未移植。来源和移植边界见 [ANIMATION-PORT.md](ANIMATION-PORT.md)。

```bat
build.cmd
run.cmd
```

桌宠会自主四处张望，可以拖动，点击可戳它。托盘菜单提供“戳一下”“转一圈”、开心/思考/困倦/好奇表情和“退出桌宠”；运行 `run.cmd --demo` 可自动轮流演示转圈和戳击。

运行内置动画与像素自检：

```bat
PetDemo.exe --selftest
```

运行动画和网格回归测试：

```bat
test.cmd
```

位置保存在 `%APPDATA%\XiaodaoPetDemo`。
