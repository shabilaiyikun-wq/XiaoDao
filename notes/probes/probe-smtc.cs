using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

class Probe
{
    /* 不用 AsTask(): System.Runtime.WindowsRuntime 的 AsTask 是针对合并版 Windows.winmd
       编译的，而本机只有 System32\WinMetadata 里的拆分版, 引用对不上。
       直接轮询 IAsyncOperation.Status 再 GetResults(), 完全走 CLR 内建的 WinRT 支持。 */
    static T Wait<T>(IAsyncOperation<T> op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(8);
        if (op.Status == AsyncStatus.Error)
            throw new Exception("async failed: " + op.ErrorCode.Message);
        return op.GetResults();
    }

    static void Main()
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionManager mgr =
                Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = mgr.GetSessions();
            Console.WriteLine("sessions=" + all.Count);

            for (int i = 0; i < all.Count; i++)
            {
                GlobalSystemMediaTransportControlsSession s = all[i];
                Console.WriteLine("[" + i + "] app=" + s.SourceAppUserModelId);
                Console.WriteLine("    status=" + s.GetPlaybackInfo().PlaybackStatus);

                try
                {
                    GlobalSystemMediaTransportControlsSessionMediaProperties p =
                        Wait(s.TryGetMediaPropertiesAsync());
                    if (p != null)
                        Console.WriteLine("    title=" + p.Title + " | artist=" + p.Artist
                                          + " | album=" + p.AlbumTitle);
                }
                catch (Exception e2) { Console.WriteLine("    props ERR " + e2.GetType().Name + ": " + e2.Message); }

                try
                {
                    GlobalSystemMediaTransportControlsSessionTimelineProperties t = s.GetTimelineProperties();
                    Console.WriteLine("    pos=" + Math.Round(t.Position.TotalSeconds, 1)
                                      + "/" + Math.Round(t.EndTime.TotalSeconds, 1));
                }
                catch (Exception e3) { Console.WriteLine("    timeline ERR " + e3.GetType().Name); }

                try
                {
                    Console.WriteLine("    canNext=" + s.GetPlaybackInfo().Controls.IsNextEnabled
                                      + " canSeek=" + s.GetPlaybackInfo().Controls.IsPlaybackPositionEnabled);
                }
                catch (Exception e4) { Console.WriteLine("    controls ERR " + e4.GetType().Name); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR " + ex.GetType().FullName + ": " + ex.Message);
            Exception inner = ex.InnerException;
            int guard = 0;
            while (inner != null && guard++ < 4)
            {
                Console.WriteLine("  inner " + inner.GetType().FullName + ": " + inner.Message);
                inner = inner.InnerException;
            }
        }
    }
}