using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

/* 对比「暂停后恢复」的三条路：TryPlayAsync / TryTogglePlayPauseAsync，以及控制位说什么。
   播放器的状态机在被打断后会僵住，得知道到底是哪条调用失效。 */

class Control
{
    static T Wait<T>(IAsyncOperation<T> op)
    {
        if (op == null) return default(T);
        int w = 0;
        while (op.Status == AsyncStatus.Started && w < 3000) { Thread.Sleep(2); w += 2; }
        return op.Status == AsyncStatus.Completed ? op.GetResults() : default(T);
    }

    static string St(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus.ToString(); }
        catch (Exception ex) { return "THREW " + ex.GetType().Name; }
    }

    static void Flags(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var i = s.GetPlaybackInfo();
            var c = i.Controls;
            if (c == null) { Console.WriteLine("    ctl=null"); return; }
            Console.WriteLine("    ctl toggle=" + c.IsPlayPauseToggleEnabled
                              + " play=" + c.IsPlayEnabled
                              + " pause=" + c.IsPausedEnabled()
                              + " next=" + c.IsNextEnabled
                              + " seek=" + c.IsPlaybackPositionEnabled);
        }
        catch (Exception ex) { Console.WriteLine("    ctl THREW " + ex.GetType().Name); }
    }

    static void Main(string[] rawArgs)
    {
        string needle = rawArgs.Length > 0 ? rawArgs[0].ToLower() : "qqmusic";
        var mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        if (mgr == null) { Console.WriteLine("no manager"); return; }

        GlobalSystemMediaTransportControlsSession s = null;
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = mgr.GetSessions();
        for (int i = 0; i < all.Count; i++)
        {
            string app = "";
            try { app = all[i].SourceAppUserModelId; } catch { }
            Console.WriteLine("  session[" + i + "] " + app + " " + St(all[i]));
            if (app.ToLower().IndexOf(needle) >= 0) s = all[i];
        }
        if (s == null) { Console.WriteLine("no session '" + needle + "'"); return; }

        Console.WriteLine("=== 目标 session ===");
        Flags(s);
        Console.WriteLine("  status=" + St(s));

        Console.WriteLine("--- 1) TryPauseAsync ---");
        Console.WriteLine("  ok=" + Wait(s.TryPauseAsync()));
        Thread.Sleep(700);
        Console.WriteLine("  status=" + St(s));

        Console.WriteLine("--- 2) TryPlayAsync (暂停后恢复) ---");
        Console.WriteLine("  ok=" + Wait(s.TryPlayAsync()));
        Thread.Sleep(700);
        Console.WriteLine("  status=" + St(s));

        Console.WriteLine("--- 3) 再暂停, 然后 TryTogglePlayPauseAsync ---");
        Console.WriteLine("  pause ok=" + Wait(s.TryPauseAsync()));
        Thread.Sleep(700);
        Console.WriteLine("  status=" + St(s));
        Console.WriteLine("  toggle ok=" + Wait(s.TryTogglePlayPauseAsync()));
        Thread.Sleep(700);
        Console.WriteLine("  status=" + St(s));

        Console.WriteLine("=== 结束状态 ===");
        Flags(s);
        Console.WriteLine("  status=" + St(s));
    }
}

static class Ext
{
    /* 属性名在不同 SDK 版本里叫法不一样, 这里统一走反射兜底 */
    public static bool IsPausedEnabled(this GlobalSystemMediaTransportControlsSessionPlaybackControls c)
    {
        try
        {
            var p = c.GetType().GetProperty("IsPauseEnabled");
            if (p == null) return false;
            return (bool)p.GetValue(c, null);
        }
        catch { return false; }
    }
}