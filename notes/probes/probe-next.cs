using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

/* 对匹配到的会话执行一次「下一首」，用来验证切换曲目后封面有没有跟着换。 */

class Next
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

            bool ok = false;
            try { ok = Wait(s.TrySkipNextAsync()); }
            catch (Exception ex) { Console.WriteLine("skip threw " + ex.GetType().Name); }
            Console.WriteLine(app + "  skipNext=" + ok);
            return;
        }
        Console.WriteLine("no session matching '" + needle + "'");
    }
}