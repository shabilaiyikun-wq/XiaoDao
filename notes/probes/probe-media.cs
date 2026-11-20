using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

/* 自造一个 SMTC 会话（MediaPlayer + DisplayUpdater），再用 Media.Read() 同一套接口
   把它读回来 —— 这样媒体链路在没有别的播放器时也能验证。 */

class MediaProbe
{
    static T Wait<T>(IAsyncOperation<T> op)
    {
        if (op == null) return default(T);
        int guard = 0;
        while (op.Status == AsyncStatus.Started && guard++ < 4000) Thread.Sleep(2);
        if (op.Status == AsyncStatus.Completed) return op.GetResults();
        return default(T);
    }

    static byte[] SilentWav(int seconds, int rate)
    {
        int samples = seconds * rate;
        int dataLen = samples * 2;
        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        w.Write(new char[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new char[] { 'W', 'A', 'V', 'E' });
        w.Write(new char[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(rate);
        w.Write(rate * 2);          // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits
        w.Write(new char[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);
        w.Write(new byte[dataLen]);
        w.Flush();
        return ms.ToArray();
    }

    static void Main()
    {
        MediaPlayer mp = null;
        try
        {
            byte[] wav = SilentWav(6, 8000);
            InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
            DataWriter dw = new DataWriter(stream);
            dw.WriteBytes(wav);
            Wait(dw.StoreAsync());
            dw.DetachStream();
            stream.Seek(0);

            mp = new MediaPlayer();
            mp.Source = MediaSource.CreateFromStream(stream, "audio/wav");

            SystemMediaTransportControls smtc = mp.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;

            mp.Play();
            Console.WriteLine("player started, waiting for the session to register...");
            /* 关键：属性必须在开始播放之后再写。MediaPlayer 播放时会用媒体源自带的
               元数据覆盖 DisplayUpdater，先写就被冲掉（上一版 title 读回来是空的）。 */
            Thread.Sleep(500);
            smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = "\u591c\u822a\u897f\u98de";
            smtc.DisplayUpdater.MusicProperties.Artist = "\u9648\u5f37";
            smtc.DisplayUpdater.MusicProperties.AlbumTitle = "\u8bd5\u9a8c\u789f";
            smtc.DisplayUpdater.Update();
            Console.WriteLine("metadata written after play");

            GlobalSystemMediaTransportControlsSessionManager mgr = null;
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = null;
            for (int i = 0; i < 25; i++)
            {
                Thread.Sleep(200);
                mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                if (mgr == null) continue;
                sessions = mgr.GetSessions();
                if (sessions != null && sessions.Count > 0) break;
            }

            Console.WriteLine("sessions=" + (sessions == null ? -1 : sessions.Count));
            if (sessions != null)
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    GlobalSystemMediaTransportControlsSession s = sessions[i];
                    Console.WriteLine("[" + i + "] app=" + s.SourceAppUserModelId
                                      + " status=" + s.GetPlaybackInfo().PlaybackStatus);
                    GlobalSystemMediaTransportControlsSessionMediaProperties p =
                        Wait(s.TryGetMediaPropertiesAsync());
                    if (p != null)
                        Console.WriteLine("    title=" + p.Title + " | artist=" + p.Artist
                                          + " | album=" + p.AlbumTitle
                                          + " | thumb=" + (p.Thumbnail == null ? "none" : "yes"));
                    GlobalSystemMediaTransportControlsSessionTimelineProperties tl = s.GetTimelineProperties();
                    Console.WriteLine("    pos=" + Math.Round(tl.Position.TotalSeconds, 2)
                                      + "/" + Math.Round(tl.EndTime.TotalSeconds, 2));
                }

                if (sessions.Count > 0)
                {
                    GlobalSystemMediaTransportControlsSession s = sessions[0];
                    Console.WriteLine("--- control round-trip ---");
                    Console.WriteLine("toggle  -> " + Wait(s.TryTogglePlayPauseAsync()));
                    Thread.Sleep(350);
                    Console.WriteLine("after   -> " + s.GetPlaybackInfo().PlaybackStatus);
                    Console.WriteLine("toggle  -> " + Wait(s.TryTogglePlayPauseAsync()));
                    Thread.Sleep(350);
                    Console.WriteLine("after   -> " + s.GetPlaybackInfo().PlaybackStatus);
                    Console.WriteLine("seek2s  -> " + Wait(s.TryChangePlaybackPositionAsync(20000000)));
                    Thread.Sleep(300);
                    Console.WriteLine("pos now -> "
                        + Math.Round(s.GetTimelineProperties().Position.TotalSeconds, 2));
                }
            }
            Console.WriteLine("MEDIA PROBE OK");
            /* MEDIA_HOLD=1 时保持会话存活, 方便别的进程观察它 */
            if (Environment.GetEnvironmentVariable("MEDIA_HOLD") == "1")
            {
                Console.WriteLine("holding session for 25s...");
                Thread.Sleep(25000);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR " + ex.GetType().FullName + ": " + ex.Message);
            Exception inner = ex.InnerException;
            int g = 0;
            while (inner != null && g++ < 4)
            {
                Console.WriteLine("  inner " + inner.GetType().FullName + ": " + inner.Message);
                inner = inner.InnerException;
            }
        }
        finally
        {
            try { if (mp != null) { mp.Pause(); mp.Dispose(); } } catch { }
        }
    }
}