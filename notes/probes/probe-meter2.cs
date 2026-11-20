using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

/* 纯 COM 探针：只读端点电平，不带 WinRT。
   用法：
     probe-meter2 write <file.wav>   生成 440Hz 正弦（给另一个进程放）
     probe-meter2                    连读 15 次峰值
   放音用 PowerShell 的 SoundPlayer 起在别的进程里，避免 COM/WinRT 抢同一个套间。 */

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumeratorM
{
    int EnumAudioEndpoints(int f, int m, out IntPtr d);
    int GetDefaultAudioEndpoint(int f, int r, out IMMDeviceM e);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceM d);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceM
{
    int Activate(ref Guid iid, uint ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    int OpenPropertyStore(uint a, out IntPtr s);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out uint s);
}

[ComImport, Guid("C02216F6-CA67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterM
{
    int GetPeakValue(out float p);
    int GetMeteringChannelCount(out uint n);
}

class MeterProbe2
{
    static byte[] SineWav(int seconds, int rate, double freq)
    {
        int n = seconds * rate;
        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        int dataLen = n * 2;
        w.Write(new char[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataLen);
        w.Write(new char[] { 'W', 'A', 'V', 'E' });
        w.Write(new char[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write(new char[] { 'd', 'a', 't', 'a' });
        w.Write(dataLen);
        for (int i = 0; i < n; i++)
        {
            double v = Math.Sin(2 * Math.PI * freq * i / rate) * 0.55;
            w.Write((short)(v * 32767));
        }
        w.Flush();
        return ms.ToArray();
    }

    static void Main(string[] rawArgs)
    {
        if (rawArgs.Length >= 1 && rawArgs[0] == "write")
        {
            File.WriteAllBytes(rawArgs[1], SineWav(20, 44100, 440));
            Console.WriteLine("wrote " + rawArgs[1]);
            return;
        }

        try
        {
            Type t = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            IMMDeviceEnumeratorM en = (IMMDeviceEnumeratorM)Activator.CreateInstance(t);
            IMMDeviceM dev;
            int hr0 = en.GetDefaultAudioEndpoint(0, 1, out dev);
            Console.WriteLine("endpoint hr=0x" + hr0.ToString("X8"));

            Guid iid = typeof(IAudioMeterM).GUID;
            object o;
            int hr = dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            Console.WriteLine("Activate(meter) hr=0x" + hr.ToString("X8")
                              + " obj=" + (o == null ? "null" : o.GetType().Name));

            IAudioMeterM meter = o as IAudioMeterM;
            if (meter == null) { Console.WriteLine("no meter interface"); return; }

            uint chans;
            meter.GetMeteringChannelCount(out chans);
            Console.WriteLine("channels=" + chans);

            float max = 0;
            for (int i = 0; i < 15; i++)
            {
                float p;
                meter.GetPeakValue(out p);
                if (p > max) max = p;
                Console.WriteLine("  peak=" + Math.Round(p, 4)
                                  + "".PadRight((int)(p * 40), '#'));
                Thread.Sleep(110);
            }
            Console.WriteLine("max=" + Math.Round(max, 4)
                              + (max > 0.02 ? "  -> LIVE LEVEL WORKS" : "  -> engine silent"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}