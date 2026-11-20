using System;
using System.Runtime.InteropServices;

/* 音频端点音量: 手写 CoreAudio COM 互操作, 不引入 NAudio 之类的包。
   方法顺序必须与 Windows SDK 的 vtable 完全一致, 否则调用会串位。 */

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    int GetDevice(string id, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(uint access, out IntPtr store);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out uint state);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float level, ref Guid ctx);
    int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint index, float level, ref Guid ctx);
    int SetChannelVolumeLevelScalar(uint index, float level, ref Guid ctx);
    int GetChannelVolumeLevel(uint index, out float level);
    int GetChannelVolumeLevelScalar(uint index, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    int GetVolumeStepInfo(out uint step, out uint stepCount);
    int VolumeStepUp(ref Guid ctx);
    int VolumeStepDown(ref Guid ctx);
    int QueryHardwareSupport(out uint mask);
    int GetVolumeRange(out float min, out float max, out float inc);
}

[ComImport, Guid("C02216F6-CA67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    int GetPeakValue(out float peak);
}

class AudioProbe
{
    const int eRender = 0;
    const int eMultimedia = 1;

    static IAudioEndpointVolume Endpoint()
    {
        Type t = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)Activator.CreateInstance(t);
        IMMDevice dev;
        int hr = en.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev);
        if (hr != 0) throw new Exception("GetDefaultAudioEndpoint hr=0x" + hr.ToString("X8"));

        Guid iid = typeof(IAudioEndpointVolume).GUID;
        object o;
        hr = dev.Activate(ref iid, 23 /*ALL*/, IntPtr.Zero, out o);
        if (hr != 0) throw new Exception("Activate hr=0x" + hr.ToString("X8"));
        return (IAudioEndpointVolume)o;
    }

    static IAudioMeterInformation Meter()
    {
        Type t = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)Activator.CreateInstance(t);
        IMMDevice dev;
        en.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev);
        Guid iid = typeof(IAudioMeterInformation).GUID;
        object o;
        int hr = dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        if (hr != 0) throw new Exception("meter Activate hr=0x" + hr.ToString("X8"));
        return (IAudioMeterInformation)o;
    }

    static void Main()
    {
        try
        {
            IAudioEndpointVolume vol = Endpoint();
            Guid ctx = Guid.Empty;

            uint chans; vol.GetChannelCount(out chans);
            float cur; vol.GetMasterVolumeLevelScalar(out cur);
            bool mute; vol.GetMute(out mute);
            float lo, hi, step; vol.GetVolumeRange(out lo, out hi, out step);
            uint st, stc; vol.GetVolumeStepInfo(out st, out stc);
            Console.WriteLine("channels=" + chans);
            Console.WriteLine("volume=" + Math.Round(cur * 100, 1) + "%  mute=" + mute);
            Console.WriteLine("range=" + lo + ".." + hi + " step=" + step
                              + "  steps=" + st + "/" + stc);

            // 写-读-还原
            float probe = 0.42f;
            int hr = vol.SetMasterVolumeLevelScalar(probe, ref ctx);
            float back; vol.GetMasterVolumeLevelScalar(out back);
            Console.WriteLine("set 42% -> hr=0x" + hr.ToString("X8") + " readback="
                              + Math.Round(back * 100, 1) + "%");

            vol.SetMute(true, ref ctx);
            bool m2; vol.GetMute(out m2);
            Console.WriteLine("mute on -> readback=" + m2);
            vol.SetMute(false, ref ctx);

            vol.SetMasterVolumeLevelScalar(cur, ref ctx);
            vol.SetMute(mute, ref ctx);
            float restored; vol.GetMasterVolumeLevelScalar(out restored);
            Console.WriteLine("restored=" + Math.Round(restored * 100, 1) + "%");

            IAudioMeterInformation meter = Meter();
            float peak;
            meter.GetPeakValue(out peak);
            Console.WriteLine("peak=" + Math.Round(peak, 4));
            Console.WriteLine("AUDIO OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERR " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}