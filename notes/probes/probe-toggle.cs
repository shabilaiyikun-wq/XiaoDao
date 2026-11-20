using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

/* 定向开关某个播放器的播放状态：只看某个 AppId，不动别的会话。
   用来把 QQ 音乐放起来，好复现「播放中会不会卡」。 */

class Toggle
{
    static T Wait<T>(IAsyncOperation<T> op)
    {
        if (op == null) return default(T);
        int w = 0;
        while (op.Status == AsyncStatus.Started && w < 3000) { Thread.Sleep(2); w += 2; }
        return op.Status == AsyncStatus.Completed ? op.GetResults() : default(T);
    }

    static void Main(string[] rawArgs)
    {
        string needle = rawArgs.Length > 0 ? rawArgs[0].ToLower() : "qqmusic";
        var mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        if (mgr == null) { Console.WriteLine("no manager"); return; }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = mgr.GetSessions();
        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            string app = "";
            try { app = s.SourceAppUserModelId; } catch { }
            if (app.ToLower().IndexOf(needle) < 0) continue;

            string before = "?", after = "?";
            try { before = s.GetPlaybackInfo().PlaybackStatus.ToString(); } catch { }
            bool ok = false;
            try { ok = Wait(s.TryTogglePlayPauseAsync()); } catch (Exception ex) { Console.WriteLine("toggle threw " + ex.GetType().Name); }
            Thread.Sleep(500);
            try { after = s.GetPlaybackInfo().PlaybackStatus.ToString(); } catch { }
            Console.WriteLine(app + "  toggle=" + ok + "  " + before + " -> " + after);
            return;
        }
        Console.WriteLine("no session matching '" + needle + "'");
    }
}