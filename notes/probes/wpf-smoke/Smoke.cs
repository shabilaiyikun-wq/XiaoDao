using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

class SmokeApp : Application {
  [STAThread]
  static void Main() {
    var app = new SmokeApp();
    app.Startup += (s, e) => {
      var w = new Window();
      w.WindowStyle = WindowStyle.None;
      w.AllowsTransparency = true;
      w.Background = Brushes.Transparent;
      w.Topmost = true;
      w.ShowInTaskbar = false;
      w.Width = 400;
      w.Height = 120;
      w.Left = 200;
      w.Top = 120;
      var b = new Border();
      b.Background = new SolidColorBrush(Color.FromRgb(255, 40, 40));
      b.CornerRadius = new CornerRadius(30);
      w.Content = b;
      w.Show();
      var t = new DispatcherTimer();
      t.Interval = TimeSpan.FromSeconds(4);
      t.Tick += (a1, a2) => { t.Stop(); app.Shutdown(); };
      t.Start();
    };
    app.Run();
  }
}
