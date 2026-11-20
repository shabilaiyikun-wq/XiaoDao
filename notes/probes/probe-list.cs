using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
class T {
  static T2 Wait<T2>(IAsyncOperation<T2> op) {
    if (op == null) return default(T2);
    int w = 0;
    while (op.Status == AsyncStatus.Started && w < 800) { Thread.Sleep(2); w += 2; }
    return op.Status == AsyncStatus.Completed ? op.GetResults() : default(T2);
  }
  static void Main() {
    var mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
    for (int round = 0; round < 5; round++) {
      Console.WriteLine("--- round " + round + " ---");
      IReadOnlyList<GlobalSystemMediaTransportControlsSession> all = mgr.GetSessions();
      for (int i = 0; i < all.Count; i++) {
        var s = all[i];
        string app = "?"; try { app = s.SourceAppUserModelId; } catch (Exception e) { app = "THREW " + e.GetType().Name; }
        string st = "?"; try { st = s.GetPlaybackInfo().PlaybackStatus.ToString(); } catch (Exception e) { st = "THREW " + e.GetType().Name; }
        Console.WriteLine("  [" + i + "] " + app + "  status=" + st);
      }
      Thread.Sleep(500);
    }
  }
}