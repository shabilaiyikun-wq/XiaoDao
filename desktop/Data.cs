using System;

namespace WinIsland
{
    /* 界面上要显示的一切，都在这一份快照里。
       每个字段都自带「拿不到时的体面说法」，UI 层不需要判断 null。 */
    class Snap
    {
        /* --- 媒体 --- */
        public NowPlaying Media = new NowPlaying();

        /* --- 倒计时 --- */
        public double TimerLeft = 25 * 60;
        public double TimerTotal = 25 * 60;
        public bool TimerRunning;

        /* ---------------------------------------------------------------- 天气 */

        /* 字段名用 Wx 而不是 Weather：Weather 是个静态类，同名字段会在 Snap
           的作用域里把它遮住 —— 以后谁在这儿写一行 Weather.Describe() 就会
           莫名其妙编不过，而且报错信息还指不到根上。 */
        public WeatherNow Wx = new WeatherNow();

        public bool WxHas { get { return Wx.Has; } }

        /// <summary>大字号那个温度。没数据就是 --°，不假装 0°。</summary>
        public string WxTempText { get { return Wx.Has ? Wx.TempText : "--°"; } }

        public string WxDesc
        {
            get
            {
                if (Wx.Has) return Wx.Describe;
                return Wx.Error.Length > 0 ? Wx.Error : "还没取到天气";
            }
        }

        /// <summary>城市名。定位中 / 没数据时给一句站得住的话，不留空。</summary>
        public string WxPlace
        {
            get
            {
                if (Wx.Has) return Wx.Place.Length > 0 ? Wx.Place : "当地";
                return "定位中";
            }
        }

        /// <summary>城市下面那行小字：省·国，或者「自动定位」/「指定」。 </summary>
        public string WxWhere
        {
            get
            {
                if (!Wx.Has) return "右键菜单里可以指定城市";
                string tag = Wx.Auto ? "自动定位" : "指定城市";
                if (Wx.Region.Length > 0) return Wx.Region + " · " + tag;
                return tag;
            }
        }

        /// <summary>数据新鲜度：卡片右下角那句。</summary>
        public string WxAge
        {
            get
            {
                if (!Wx.Has) return "等待首次更新";
                string s = Weather.AgeText(Wx.At);
                if (Wx.Stale) s = "离线 · " + s;
                return s;
            }
        }

        /* ---------------------------------------------------------------- 媒体 */
        public bool MediaHas { get { return Media.Has; } }
        public bool Playing { get { return Media.Playing; } }

        public string MediaTitle
        {
            get
            {
                if (!Media.Has) return "没有正在播放的媒体";
                return Media.Title.Length > 0 ? Media.Title : "未知曲目";
            }
        }

        public string MediaSub
        {
            get
            {
                if (!Media.Has) return "打开任意播放器就会出现在这里";
                string s = Media.Artist;
                if (Media.Album.Length > 0)
                    s = s.Length > 0 ? s + " · " + Media.Album : Media.Album;
                if (s.Length == 0) s = Media.PrettyAppName;
                if (!Media.Playing && Media.Status == "Paused") s = "已暂停 · " + s;
                return s;
            }
        }

        public double MediaPct
        {
            get
            {
                if (!Media.Has || Media.Duration <= 0) return 0;
                double p = Media.Position / Media.Duration;
                return p < 0 ? 0 : (p > 1 ? 1 : p);
            }
        }

        public string MediaPos { get { return Media.Has ? Fmt(Media.Position) : "0:00"; } }
        public string MediaDur { get { return Media.Has ? Fmt(Media.Duration) : "0:00"; } }


        /* ---------------------------------------------------------------- 倒计时 */
        public string TimerText { get { return Fmt(TimerLeft); } }
        public double TimerPct
        {
            get
            {
                if (TimerTotal <= 0) return 0;
                double p = TimerLeft / TimerTotal;
                return p < 0 ? 0 : (p > 1 ? 1 : p);
            }
        }
        public string TimerPctText
        {
            get { return Math.Round(TimerPct * 100) + "%"; }
        }

        /* 112 -> "1:52"；3725 -> "1:02:05" */
        public static string Fmt(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Round(seconds);
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            if (h > 0) return h + ":" + (m < 10 ? "0" : "") + m + ":" + (s < 10 ? "0" : "") + s;
            return m + ":" + (s < 10 ? "0" : "") + s;
        }
    }

    /* 卡片里能被点的动作。由 IslandWindow 提供实现，Layout 只负责接线。 */
    class CardActions
    {
        public Action PlayPause;
        public Action Next;
        public Action Prev;
        public Action TimerToggle;
        public Action TimerReset;
        public Action TimerPlus;

        /* --- 剪贴板 / 待办 --- */
        public Action<int> CopyClip;         // 复制历史里的第 i 条
        public Action ClearClips;
        public Action<int> ToggleTodo;       // 勾选/取消第 i 条
        public Action AddTodo;               // 打开输入窗，直接在岛上写一条
        public Action ClearDoneTodos;

        /* --- 中转站 --- */
        public Action ClearTransfer;         // 清空全部
        public Action ClearTransferMissing;  // 只清已丢失的
        public Action<string> TransferOpen;  // 用默认程序打开
        public Action<string> TransferRemove;// 移除一项
        public Action<string> TransferDragOut; // 从这一项开始原生拖放
        public Action<string> TransferMenu;  // 行级右键菜单（资源管理器/复制路径/移除）

        /* 底部指示点：直接跳到某个场景 */
        public Action<string> Scene;

        /* --- 天气 --- */
        public Action RefreshWeather;   // 立刻重抓一次（尊重最短间隔）
        public Action PickCity;         // 打开输入窗填城市
    }
}
