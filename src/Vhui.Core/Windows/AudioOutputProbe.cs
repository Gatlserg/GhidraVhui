using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vhui.Core.Windows;

public sealed record AudioOutputResult(string Device, int Bytes, int Milliseconds, bool Completed);

/// <summary>Plays only zero PCM samples, to exercise USB audio OUT without an audible signal.</summary>
[SupportedOSPlatform("windows")]
public static class AudioOutputProbe
{
    public static AudioOutputResult Run(string name)
    {
        uint selected = uint.MaxValue; string deviceName = "";
        for (uint index = 0; index < waveOutGetNumDevs(); index++)
        {
            Check(waveOutGetDevCapsW((UIntPtr)index, out Capabilities caps, (uint)Marshal.SizeOf<Capabilities>()));
            if (caps.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) { selected = index; deviceName = caps.Name; break; }
        }
        if (selected == uint.MaxValue) { throw new IOException("Audio output device not found: " + name); }
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var format = new Format { Tag = 1, Channels = 2, Rate = 48000, BytesPerSecond = 192000, Alignment = 4, Bits = 16 };
        Check(waveOutOpen(out IntPtr device, selected, ref format, completed.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0x50000));
        const int length = 19200; // 100 ms of 48 kHz, stereo, signed 16-bit PCM, all silence.
        IntPtr data = Marshal.AllocHGlobal(length), header = Marshal.AllocHGlobal(Marshal.SizeOf<Header>());
        bool prepared = false;
        try
        {
            Marshal.Copy(new byte[length], 0, data, length);
            Marshal.StructureToPtr(new Header { Data = data, Length = length }, header, false);
            Check(waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<Header>())); prepared = true;
            completed.Reset(); Check(waveOutWrite(device, header, (uint)Marshal.SizeOf<Header>()));
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while ((Marshal.PtrToStructure<Header>(header).Flags & 1) == 0)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds > 8) { throw new IOException("Audio output timed out."); }
                completed.WaitOne(100);
            }
            return new(deviceName, length, 100, true);
        }
        finally
        {
            waveOutReset(device);
            bool released = !prepared || waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<Header>()) == 0;
            waveOutClose(device);
            if (released) { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); }
        }
    }
    private static void Check(uint code) { if (code != 0) { throw new IOException("Wave output error: " + code); } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Capabilities
    { public ushort Manufacturer, Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format
    { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Alignment, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Header
    { public IntPtr Data; public int Length; public uint Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetDevCapsW(UIntPtr id, out Capabilities caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, uint id, ref Format format, IntPtr callback, IntPtr user, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
}
