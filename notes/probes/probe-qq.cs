using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

/* 逐调用计时。目的不是「能不能读到」，而是「哪一步会卡、卡多久」。
   和主程序的区别：这里不带 [STAThread]、不跑 UI 线程，所以如果这里毫秒级返回、
   而岛里卡死，那问题就锁定在「在 STA/UI 线程上阻塞等待 WinRT 异步」
   —— 完成回调要回到被阻塞的那个线程，于是永远等不到。 */

class QQProbe
{
    static Stopwatch _sw = Stopwatch.StartNew();

    static void Mark(string what)
    {
        Console.WriteLine("  [" + _sw.ElapsedMilliseconds.ToString().PadLeft(5) + "ms] " + what);
    }

    static T Wait<T>(string label, IAsyncOperation<T> op, int capMs)
    {
        if (op == null) { Mark(label + " op=null"); return default(T); }
        long t0 = _sw.ElapsedMilliseconds;
        int spins = 0;
        while (op.Status == AsyncStatus.Started && _sw.ElapsedMilliseconds - t0 < capMs)
        {
            Thread.Sleep(2);
            spins++;
        }
        AsyncStatus st = op.Status;
        long dt = _sw.ElapsedMilliseconds - t0;
        if (st == AsyncStatus.Completed)
        {
            T r = op.GetResults();
            Mark(label + " OK " + dt + "ms spins=" + spins);
            return r;
        }
        Mark(label + " !! status=" + st + " after " + dt + "ms spins=" + spins);
        return default(T);
    }

    static void Main()
    {
        Console.WriteLine("apartment=" + Thread.CurrentThread.GetApartmentState()
                          + "  ui=" + (SynchronizationContext.Current == null ? "none" : "present"));

        GlobalSystemMediaTransportControlsSessionManager mgr =
            Wait("RequestAsync(manager)", GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 4000);
        if (mgr == null) { Console.WriteLine("no manager"); return; }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = null;
        long t0 = _sw.ElapsedMilliseconds;
        try { all = mgr.GetSessions(); }
        catch (Exception ex) { Mark("GetSessions THREW " + ex.GetType().Name + ": " + ex.Message); return; }
        Mark("GetSessions -> " + (all == null ? -1 : all.Count) + " in " + (_sw.ElapsedMilliseconds - t0) + "ms");
        if (all == null) return;

        for (int i = 0; i < all.Count; i++)
        {
            GlobalSystemMediaTransportControlsSession s = all[i];
            Console.WriteLine("--- session " + i + " ---");
            try { Mark("SourceAppUserModelId = " + s.SourceAppUserModelId); }
            catch (Exception ex) { Mark("AppId THREW " + ex.GetType().Name); }

            try
            {
                long a = _sw.ElapsedMilliseconds;
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = s.GetPlaybackInfo();
                Mark("GetPlaybackInfo OK " + (_sw.ElapsedMilliseconds - a) + "ms status="
                     + (info == null ? "null" : info.PlaybackStatus.ToString())
                     + " canSeek=" + (info != null && info.Controls != null
                         ? info.Controls.IsPlaybackPositionEnabled.ToString() : "?"));
            }
            catch (Exception ex) { Mark("GetPlaybackInfo THREW " + ex.GetType().Name + ": " + ex.Message); }

            try
            {
                long a = _sw.ElapsedMilliseconds;
                GlobalSystemMediaTransportControlsSessionTimelineProperties tl = s.GetTimelineProperties();
                Mark("GetTimelineProperties OK " + (_sw.ElapsedMilliseconds - a) + "ms pos="
                     + (tl == null ? "null" : tl.Position.TotalSeconds + "/" + tl.EndTime.TotalSeconds));
            }
            catch (Exception ex) { Mark("GetTimeline THREW " + ex.GetType().Name); }

            GlobalSystemMediaTransportControlsSessionMediaProperties p =
                Wait("TryGetMediaPropertiesAsync", s.TryGetMediaPropertiesAsync(), 4000);
            if (p != null)
            {
                Mark("  title='" + p.Title + "' artist='" + p.Artist + "' album='" + p.AlbumTitle + "'");
                if (p.Thumbnail == null) Mark("  thumbnail=none");
                else
                {
                    IRandomAccessStreamWithContentType rs =
                        Wait("  Thumbnail.OpenReadAsync", p.Thumbnail.OpenReadAsync(), 4000);
                    if (rs != null)
                    {
                        Mark("  stream size=" + rs.Size);
                        uint size = (uint)rs.Size;
                        if (size > 0 && size < 8 * 1024 * 1024)
                        {
                            DataReader dr = new DataReader(rs);
                            Wait("  DataReader.LoadAsync", dr.LoadAsync(size), 4000);
                            byte[] buf = new byte[size];
                            try { dr.ReadBytes(buf); Mark("  read " + buf.Length + "B ok"); }
                            catch (Exception ex) { Mark("  ReadBytes THREW " + ex.GetType().Name); }
                            dr.Dispose();
                        }
                    }
                }
            }
        }
        Console.WriteLine("PROBE DONE total=" + _sw.ElapsedMilliseconds + "ms");
    }
}