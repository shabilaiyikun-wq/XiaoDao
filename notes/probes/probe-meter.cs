using System;
using System.Runtime.InteropServices;
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int f, int m, out IntPtr d);
    int GetDefaultAudioEndpoint(int f, int r, out IMMDevice e);
    int GetDevice(string id, out IMMDevice d);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice {
    int Activate(ref Guid iid, uint ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    int OpenPropertyStore(uint a, out IntPtr s);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out uint s);
}
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IntPtr n);
    int UnregisterControlChangeNotify(IntPtr n);
    int GetChannelCount(out uint c);
    int SetMasterVolumeLevel(float l, ref Guid g);
    int SetMasterVolumeLevelScalar(float l, ref Guid g);
    int GetMasterVolumeLevel(out float l);
    int GetMasterVolumeLevelScalar(out float l);
    int SetChannelVolumeLevel(uint i, float l, ref Guid g);
    int SetChannelVolumeLevelScalar(uint i, float l, ref Guid g);
    int GetChannelVolumeLevel(uint i, out float l);
    int GetChannelVolumeLevelScalar(uint i, out float l);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid g);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m);
    int GetVolumeStepInfo(out uint s, out uint c);
    int VolumeStepUp(ref Guid g);
    int VolumeStepDown(ref Guid g);
    int QueryHardwareSupport(out uint m);
    int GetVolumeRange(out float a, out float b, out float c);
}
[ComImport, Guid("C02216F6-CA67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation { int GetPeakValue(out float p); }

class T {
    static void Main() {
        try {
            Type t = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            IMMDeviceEnumerator en = (IMMDeviceEnumerator)Activator.CreateInstance(t);
            IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object o; int hr = dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            Console.WriteLine("activate hr=0x" + hr.ToString("X8") + " type=" + o.GetType().Name);
            IAudioEndpointVolume vol = (IAudioEndpointVolume)o;
            float cur; vol.GetMasterVolumeLevelScalar(out cur);
            Console.WriteLine("volume=" + Math.Round(cur*100,1) + "%");
            IAudioMeterInformation meter = o as IAudioMeterInformation;
            Console.WriteLine("as meter = " + (meter == null ? "null" : "ok"));
            if (meter != null) {
                float p; meter.GetPeakValue(out p);
                Console.WriteLine("peak=" + Math.Round(p,4));
                float p2; System.Threading.Thread.Sleep(120); meter.GetPeakValue(out p2);
                Console.WriteLine("peak(after 120ms)=" + Math.Round(p2,4));
            }
            Console.WriteLine("METER OK");
        } catch (Exception ex) { Console.WriteLine("ERR " + ex.GetType().Name + ": " + ex.Message); }
    }
}