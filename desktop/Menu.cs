using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinIsland
{
    /* ============================================================================
     * 右键菜单。
     *
     * 卡片里那个右上角的关闭按钮去掉了 —— 18px 的圆钮压在 30 圆角的边上，
     * 既挡视线又不属于这张卡片的信息层级。所有操作收进右键菜单：
     * 场景 / 外观 / 取材 / 形态 四个子菜单 + 复位位置 + 退出。
     *
     * 菜单是独立的 ContextMenu（自己的 HWND），所以能画到岛外面去；
     * 面板用不透明色而不是玻璃 —— 菜单是要读的，不是要看透的。
     * ========================================================================= */

    class MenuActions
    {
        public Action<string> Scene;
        public Action<string> Look;
        public Action<string> Art;
        public Action<string> State;
        public Action ResetPosition;
        public Action Shot;
        public Action ToggleCapture;
        public Action ToggleTransferOut;
        public Action AddFromClipboard;
        public Action Quit;

        /* 桌宠。启用/关闭是一个动作而不是两个 —— 菜单项自己知道当前态，
           点下去就是取反，和「允许截图与录屏捕捉」一致。 */
        public Action TogglePet;
        public Action PetPoke;
        public Action PetSpin;
        public Action PetRandomIdle;
        public Action<string> PetState;
        public Action<string> PetClip;

        /* 中转站行级菜单专用 */
        public Action Reveal;
        public Action CopyPath;
        public Action RemoveRow;
    }

    static class Menu
    {
        const double ItemH = 29;
        const double Radius = 9;

        /* ---------------------------------------------------------------- 配色 */

        public static bool Dark(Look look)
        {
            Color c = look.TintMid;
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128.0;
        }

        /// 不透明面板色：在 look 的中/底色调上再压一格，保证压任何壁纸都能读。
        public static Color Panel(Look look)
        {
            Color c = Ui.Mix(look.TintMid, look.TintBottom, 0.45);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        public static Color Line(Look look)
        {
            bool dark = Dark(look);
            Color c = Ui.Mix(Panel(look), dark ? Colors.White : Colors.Black, dark ? 0.15 : 0.11);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        public static Color Hover(Look look)
        {
            bool dark = Dark(look);
            Color c = Ui.Mix(Panel(look), dark ? Colors.White : Colors.Black, dark ? 0.13 : 0.07);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        /* ---------------------------------------------------------------- 模板 */

        /// 一行菜单项：左边选中点、中间标题、右边子菜单箭头。
        /// 用 DockPanel 而不是 Grid —— 在 FrameworkElementFactory 里加列定义不可靠。
        ///
        /// 根是 Grid 而不是 Border：Grid 要装两样东西 —— 这行标题，以及子菜单的 Popup。
        /// Border 只允许一个子元素，装不下。
        static ControlTemplate ItemTemplate(Color hover, Color fg, Color dim, Color accent,
                                            Color panelBg, Color panelLine)
        {
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.MarginProperty, new Thickness(5, 1, 5, 1));
            bd.SetValue(Border.PaddingProperty, new Thickness(8, 0, 8, 0));
            bd.SetValue(FrameworkElement.MinHeightProperty, ItemH);
            bd.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory dock = new FrameworkElementFactory(typeof(DockPanel));
            dock.SetValue(DockPanel.LastChildFillProperty, true);

            FrameworkElementFactory dotCell = new FrameworkElementFactory(typeof(Border));
            dotCell.SetValue(DockPanel.DockProperty, Dock.Left);
            dotCell.SetValue(FrameworkElement.WidthProperty, 15.0);

            FrameworkElementFactory dot = new FrameworkElementFactory(typeof(Ellipse));
            dot.Name = "Dot";
            dot.SetValue(FrameworkElement.WidthProperty, 5.0);
            dot.SetValue(FrameworkElement.HeightProperty, 5.0);
            dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetValue(Shape.FillProperty, new SolidColorBrush(accent));
            dot.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
            dotCell.AppendChild(dot);

            FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(Path));
            arrow.Name = "Arrow";
            arrow.SetValue(DockPanel.DockProperty, Dock.Right);
            arrow.SetValue(Shape.FillProperty, new SolidColorBrush(dim));
            arrow.SetValue(Path.DataProperty, Geometry.Parse("M0,0 L3.4,4 L0,8 Z"));
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 1, 0));
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(TextElement.ForegroundProperty, new SolidColorBrush(fg));

            dock.AppendChild(dotCell);
            dock.AppendChild(arrow);
            dock.AppendChild(content);
            bd.AppendChild(dock);

            /* ---- 子菜单弹层 ----
               这块是「子菜单能展开」的全部前提：MenuItem 的模板必须自带 Popup，
               把 IsOpen 绑到 IsSubmenuOpen，Popup 里放一个 IsItemsHost 的容器
               让 WPF 把子项塞进去。
               之前模板只有一个 Border，Popup 没了 —— 箭头画得出来，点上去没反应。 */
            FrameworkElementFactory popBg = new FrameworkElementFactory(typeof(Border));
            popBg.Name = "Pop";
            popBg.SetValue(Border.BackgroundProperty, new SolidColorBrush(panelBg));
            popBg.SetValue(Border.BorderBrushProperty, new SolidColorBrush(panelLine));
            popBg.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popBg.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radius));
            popBg.SetValue(Border.PaddingProperty, new Thickness(0, 5, 0, 5));
            popBg.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
            popBg.SetValue(FrameworkElement.MinWidthProperty, 132.0);

            FrameworkElementFactory popHost = new FrameworkElementFactory(typeof(StackPanel));
            popHost.SetValue(StackPanel.IsItemsHostProperty, true);
            popBg.AppendChild(popHost);

            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetValue(Popup.HorizontalOffsetProperty, 1.0);
            /* 模板绑定 IsSubmenuOpen —— 少了这根线，Popup 永远不会打开 */
            popup.SetBinding(Popup.IsOpenProperty,
                new Binding("IsSubmenuOpen")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });
            popup.AppendChild(popBg);

            FrameworkElementFactory rootGrid = new FrameworkElementFactory(typeof(Grid));
            rootGrid.AppendChild(bd);
            rootGrid.AppendChild(popup);

            ControlTemplate t = new ControlTemplate(typeof(MenuItem));
            t.VisualTree = rootGrid;

            Trigger hi = new Trigger();
            hi.Property = MenuItem.IsHighlightedProperty;
            hi.Value = true;
            hi.Setters.Add(new Setter(Border.BackgroundProperty,
                                      new SolidColorBrush(hover), "Bd"));
            t.Triggers.Add(hi);

            Trigger on = new Trigger();
            on.Property = MenuItem.IsCheckedProperty;
            on.Value = true;
            on.Setters.Add(new Setter(UIElement.VisibilityProperty,
                                      Visibility.Visible, "Dot"));
            t.Triggers.Add(on);

            /* Tag="sub" 标记带子菜单的那几行，箭头才显示 */
            Trigger sub = new Trigger();
            sub.Property = FrameworkElement.TagProperty;
            sub.Value = "sub";
            sub.Setters.Add(new Setter(UIElement.VisibilityProperty,
                                       Visibility.Visible, "Arrow"));
            t.Triggers.Add(sub);

            Trigger off = new Trigger();
            off.Property = UIElement.IsEnabledProperty;
            off.Value = false;
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "Bd"));
            t.Triggers.Add(off);

            return t;
        }

        static ControlTemplate PanelTemplate(Color bg, Color line)
        {
            FrameworkElementFactory bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new SolidColorBrush(bg));
            bd.SetValue(Border.BorderBrushProperty, new SolidColorBrush(line));
            bd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(Radius));
            bd.SetValue(Border.PaddingProperty, new Thickness(0, 5, 0, 5));
            bd.SetValue(UIElement.ClipToBoundsProperty, true);

            FrameworkElementFactory sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.IsItemsHostProperty, true);
            bd.AppendChild(sp);

            ControlTemplate t = new ControlTemplate(typeof(ContextMenu));
            t.VisualTree = bd;
            return t;
        }

        /* ---------------------------------------------------------------- 条目 */

        static MenuItem Sub(string header, ControlTemplate item)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            mi.Template = item;
            mi.Tag = "sub";
            return mi;
        }

        static MenuItem Leaf(string header, ControlTemplate item, bool on, Action run)
        {
            MenuItem mi = new MenuItem();
            mi.Header = header;
            mi.Template = item;
            mi.IsChecked = on;
            if (run != null) mi.Click += delegate { run(); };
            return mi;
        }

        static MenuItem Rule(Color line)
        {
            MenuItem mi = new MenuItem();
            mi.IsEnabled = false;
            mi.Height = 9;
            FrameworkElementFactory b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.HeightProperty, 1.0);
            b.SetValue(Border.MarginProperty, new Thickness(13, 4, 13, 4));
            b.SetValue(Border.BackgroundProperty, new SolidColorBrush(line));
            ControlTemplate t = new ControlTemplate(typeof(MenuItem));
            t.VisualTree = b;
            mi.Template = t;
            return mi;
        }

        /* ---------------------------------------------------------------- 组装 */

        public static ContextMenu Build(Look look, Art art, MenuActions act,
                                        string scene, string lookKey, string artKey, string state,
                                        bool capture, bool transferOut,
                                        bool petOn, string petState)
        {
            Color bg = Panel(look);
            Color line = Line(look);
            ControlTemplate item = ItemTemplate(Hover(look), look.Ink1, look.Ink3,
                                                Ui.AccInk(look, art), bg, line);

            ContextMenu m = new ContextMenu();
            m.OverridesDefaultStyle = true;
            m.Template = PanelTemplate(bg, line);
            m.Background = new SolidColorBrush(bg);
            m.BorderBrush = new SolidColorBrush(line);
            m.Foreground = new SolidColorBrush(look.Ink1);
            m.FontFamily = new FontFamily(Ui.FontUi);
            m.FontSize = 12.5;
            m.HasDropShadow = false;
            m.Placement = PlacementMode.MousePoint;

            MenuItem scenes = Sub("场景", item);
            scenes.Items.Add(Leaf("媒体", item, scene == "media",
                delegate { act.Scene("media"); }));
            scenes.Items.Add(Leaf("倒计时", item, scene == "timer",
                delegate { act.Scene("timer"); }));
            scenes.Items.Add(Leaf("剪贴板", item, scene == "clip",
                delegate { act.Scene("clip"); }));
            scenes.Items.Add(Leaf("待办", item, scene == "todo",
                delegate { act.Scene("todo"); }));
            scenes.Items.Add(Leaf("中转站", item, scene == "transfer",
                delegate { act.Scene("transfer"); }));

            MenuItem looks = Sub("外观", item);
            looks.Items.Add(Leaf("Glass", item, lookKey == "glass",
                delegate { act.Look("glass"); }));
            looks.Items.Add(Leaf("曜黑 Obsidian", item, lookKey == "obsidian",
                delegate { act.Look("obsidian"); }));
            looks.Items.Add(Leaf("石墨 Graphite", item, lookKey == "graphite",
                delegate { act.Look("graphite"); }));
            looks.Items.Add(Leaf("霜白 Frost", item, lookKey == "frost",
                delegate { act.Look("frost"); }));

            MenuItem arts = Sub("取材", item);
            arts.Items.Add(Leaf("Aurora", item, artKey == "aurora",
                delegate { act.Art("aurora"); }));
            arts.Items.Add(Leaf("Ember", item, artKey == "ember",
                delegate { act.Art("ember"); }));
            arts.Items.Add(Leaf("Mint", item, artKey == "mint",
                delegate { act.Art("mint"); }));
            arts.Items.Add(Leaf("Rose", item, artKey == "rose",
                delegate { act.Art("rose"); }));
            arts.Items.Add(Leaf("Steel", item, artKey == "steel",
                delegate { act.Art("steel"); }));

            MenuItem forms = Sub("形态", item);
            forms.Items.Add(Leaf("展开", item, state == "expanded",
                delegate { act.State("expanded"); }));
            forms.Items.Add(Leaf("紧凑", item, state == "compact",
                delegate { act.State("compact"); }));
            forms.Items.Add(Leaf("休眠", item, state == "idle",
                delegate { act.State("idle"); }));

            m.Items.Add(scenes);
            m.Items.Add(looks);
            m.Items.Add(arts);
            m.Items.Add(forms);
            m.Items.Add(PetMenu(item, act, petOn, petState, line));
            m.Items.Add(Rule(line));
            /* 热键被别的程序占用时（RegisterHotKey 会失败），这里仍然能截图 */
            m.Items.Add(Leaf("截图  Alt+A", item, false, act.Shot));
            /* 勾选态 = 能被截图 / 录屏拍到。默认勾上。 */
            m.Items.Add(Leaf("允许截图与录屏捕捉", item, capture, act.ToggleCapture));
            m.Items.Add(Leaf("拖出后从暂存移除", item, transferOut, act.ToggleTransferOut));
            /* 兜底入口：拖放万一在某台机器上不灵（穿透窗口 + OLE 这条路
               依赖 OLE 的命中判定），在资源管理器里 Ctrl+C 复制文件也进得来。 */
            m.Items.Add(Leaf("把剪贴板里的文件暂存", item, false, act.AddFromClipboard));
            m.Items.Add(Leaf("复位位置", item, false, act.ResetPosition));
            m.Items.Add(Leaf("退出 WinIsland", item, false, act.Quit));

            return m;
        }

        /* 桌宠子菜单。

           为什么整个塞进岛现有的右键菜单、而不是像 demo 那样做一个 WinForms 托盘：
           桌宠和岛现在是同一个进程、同一套视觉。再起一个托盘图标意味着第二次
           发明「怎么控制桌宠」，还得处理两个进程之间的状态同步。岛已经有右键菜单了，
           就地加一个子菜单是最小面、也最不容易漂移的做法。

           结构：
             启用桌宠              ← 勾选态 = 现在有没有桌宠
             戳击 / 转圈
             ──
             表情模式 ▸ idle/happy/thinking/sleepy/curious/morph
             全部动画 ▸ 9 组 ▸ 28 段参考动画
             ──
             恢复随机待机

           28 段动画直接从 ReferenceClips.Groups 生成，不在菜单里再抄一份名单 ——
           抄一份的下场是加片子忘了改菜单，然后菜单里少一段没人发现。 */
        static MenuItem PetMenu(ControlTemplate item, MenuActions act, bool on, string petState,
                                Color line)
        {
            MenuItem pet = Sub("桌宠", item);
            pet.Items.Add(Leaf("启用桌宠", item, on, act.TogglePet));
            pet.Items.Add(Leaf("戳击", item, false, act.PetPoke));
            pet.Items.Add(Leaf("转圈", item, false, act.PetSpin));
            pet.Items.Add(Rule(line));

            MenuItem moods = Sub("表情模式", item);
            string[] keys = new string[] { "idle", "happy", "thinking", "sleepy", "curious", "morph" };
            string[] names = new string[] { "待机 idle", "开心 happy", "思考 thinking",
                                            "困 sleepy", "好奇 curious", "变形 morph" };
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                moods.Items.Add(Leaf(names[i], item, petState == key,
                    delegate { if (act.PetState != null) act.PetState(key); }));
            }
            pet.Items.Add(moods);

            /* 全部 28 段：按 ReferenceClips.Groups 的九组展开。
               资源缺失时（ReferenceClips 静态构造抛异常）不能让整个右键菜单都打不开，
               所以这里兜一层，退化成一行禁用的说明项。 */
            MenuItem all = Sub("全部动画", item);
            try
            {
                PetClipGroup[] groups = ReferenceClips.Groups;
                int total = 0;
                for (int g = 0; g < groups.Length; g++)
                {
                    PetClipGroup group = groups[g];
                    MenuItem gm = Sub(group.Name + " (" + group.Clips.Length + ")", item);
                    for (int c = 0; c < group.Clips.Length; c++)
                    {
                        string clip = group.Clips[c];
                        total += 1;
                        /* 片段名形如 Blocked_A / Working_F，里面的下划线在 WPF 里是
                           「访问键」标记：Header 写 "Blocked_A" 会显示成 "BlockedA"，
                           并且 A 变成 Alt 访问键。菜单是给人看的，名字要按原样显示，
                           所以把下划线转义成两个（AccessText 的规则）。 */
                        gm.Items.Add(Leaf(clip.Replace("_", "__"), item, petState == clip,
                            delegate { if (act.PetClip != null) act.PetClip(clip); }));
                    }
                    all.Items.Add(gm);
                }
                all.Header = "全部动画 (" + total + ")";
            }
            catch (Exception ex)
            {
                MenuItem bad = new MenuItem();
                bad.Header = "动画清单加载失败";
                bad.Template = item;
                bad.IsEnabled = false;
                bad.ToolTip = ex.Message;
                all.Items.Add(bad);
            }
            pet.Items.Add(all);

            pet.Items.Add(Rule(line));
            pet.Items.Add(Leaf("恢复随机待机", item, petState == "idle", act.PetRandomIdle));
            return pet;
        }

        /* 中转站单行的右键菜单：在资源管理器中显示 / 复制路径 / 移除。

           复用 Build 里那一整套模板，不然会冒出一个系统默认样式的白菜单，
           在深色玻璃卡片上格外扎眼。 */
        public static ContextMenu BuildRow(Look look, Art art, MenuActions act)
        {
            Color bg = Panel(look);
            Color line = Line(look);
            ControlTemplate item = ItemTemplate(Hover(look), look.Ink1, look.Ink3,
                                                Ui.AccInk(look, art), bg, line);

            ContextMenu m = new ContextMenu();
            m.OverridesDefaultStyle = true;
            m.Template = PanelTemplate(bg, line);
            m.Background = new SolidColorBrush(bg);
            m.BorderBrush = new SolidColorBrush(line);
            m.Foreground = new SolidColorBrush(look.Ink1);
            m.FontFamily = new FontFamily(Ui.FontUi);
            m.FontSize = 12.5;
            m.HasDropShadow = false;
            m.Placement = PlacementMode.MousePoint;

            m.Items.Add(Leaf("在资源管理器中显示", item, false, act.Reveal));
            m.Items.Add(Leaf("复制路径", item, false, act.CopyPath));
            m.Items.Add(Rule(line));
            m.Items.Add(Leaf("从暂存中移除", item, false, act.RemoveRow));

            return m;
        }
    }
}
