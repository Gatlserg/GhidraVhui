using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Vhui.Core.Windows;

public sealed record CameraFrameResult(string Device, string SymbolicLink, uint Width, uint Height, Guid Subtype, int Bytes, string Sha256, bool Jpeg,
    int Frames = 1, long TotalBytes = 0, bool Audio = false, double Seconds = 0);

/// <summary>Reads one native camera sample through Media Foundation for interoperability tests.</summary>
[SupportedOSPlatform("windows")]
public static class CameraFrameProbe
{
    private static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static readonly Guid VideoCapture = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    private static readonly Guid SymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    private static readonly Guid MediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Mjpeg = new("47504a4d-0000-0010-8000-00aa00389b71");
    private const uint VideoStream = 0xfffffffc;

    public static CameraFrameResult Read(string name, int frames = 1, bool audio = false, int durationSeconds = 0)
    {
        if (frames is < 1 or > 36000) { throw new ArgumentOutOfRangeException(nameof(frames)); }
        if (durationSeconds is < 0 or > 600) { throw new ArgumentOutOfRangeException(nameof(durationSeconds)); }
        uint stream = audio ? 0xfffffffd : VideoStream;
        int com = Api.CoInitializeEx(IntPtr.Zero, 0);
        if (com < 0) { Marshal.ThrowExceptionForHR(com); }
        Check(Api.MFStartup(0x20070, 0));
        IntPtr attributes = IntPtr.Zero, activations = IntPtr.Zero, activation = IntPtr.Zero;
        IntPtr source = IntPtr.Zero, reader = IntPtr.Zero, mediaType = IntPtr.Zero;
        try
        {
            Check(Api.MFCreateAttributes(out attributes, 1));
            Guid key = SourceType, value = audio ? new("14dd9a1c-7cff-41be-b1b9-ba1ac6ecb571") : VideoCapture;
            Check(Method<SetGuid>(attributes, 24)(attributes, ref key, ref value));
            Check(Api.MFEnumDeviceSources(attributes, out activations, out uint count));
            string deviceName = "", link = "";
            for (int index = 0; index < count; index++)
            {
                IntPtr candidate = Marshal.ReadIntPtr(activations, index * IntPtr.Size);
                string candidateName = GetString(candidate, FriendlyName);
                if (activation == IntPtr.Zero && candidateName.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    activation = candidate; deviceName = candidateName;
                    link = GetString(candidate, audio ? new("30da9258-feb9-47a7-a453-763a7a8e1c5f") : SymbolicLink);
                }
                else { Marshal.Release(candidate); }
            }
            if (activation == IntPtr.Zero) { throw new IOException($"Camera '{name}' was not found by Media Foundation."); }
            Guid sourceId = MediaSource;
            Check(Method<Activate>(activation, 33)(activation, ref sourceId, out source));
            Check(Api.MFCreateSourceReaderFromMediaSource(source, IntPtr.Zero, out reader));
            uint width = 0, height = 0;
            Guid subtype = Guid.Empty;
            for (uint index = 0; index < 512; index++)
            {
                int hr = Method<GetNativeType>(reader, 5)(reader, stream, index, out IntPtr candidate);
                if (hr < 0) { break; }
                Guid sizeKey = FrameSize, subtypeKey = Subtype;
                Method<GetUInt64>(candidate, 8)(candidate, ref sizeKey, out ulong size);
                Method<GetGuid>(candidate, 10)(candidate, ref subtypeKey, out Guid type);
                bool preferred = audio || (uint)(size >> 32) == 640 && (uint)size == 480 && type == Mjpeg;
                if (mediaType == IntPtr.Zero || preferred)
                {
                    if (mediaType != IntPtr.Zero) { Marshal.Release(mediaType); }
                    mediaType = candidate; width = (uint)(size >> 32); height = (uint)size; subtype = type;
                }
                else { Marshal.Release(candidate); }
                if (preferred) { break; }
            }
            if (mediaType == IntPtr.Zero) { throw new IOException("Camera did not publish a video media type."); }
            Check(Method<SetSelection>(reader, 4)(reader, 0xfffffffe, 0));
            Check(Method<SetSelection>(reader, 4)(reader, stream, 1));
            Check(Method<SetType>(reader, 7)(reader, stream, IntPtr.Zero, mediaType));
            int received = 0; long totalBytes = 0;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int attempt = 0; attempt < frames + 60; attempt++)
            {
                Check(Method<ReadSample>(reader, 9)(reader, stream, 0, out _, out _, out _, out IntPtr sample));
                if (sample == IntPtr.Zero) { continue; }
                try
                {
                    Check(Method<GetBuffer>(sample, 41)(sample, out IntPtr buffer));
                    try
                    {
                        Check(Method<LockBuffer>(buffer, 3)(buffer, out IntPtr pointer, out _, out uint current));
                        byte[] bytes;
                        try
                        {
                            if (current == 0 || current > 32 * 1024 * 1024) { throw new InvalidDataException("Invalid camera sample size."); }
                            bytes = new byte[current]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
                        }
                        finally { Check(Method<NoArguments>(buffer, 4)(buffer)); }
                        totalBytes += bytes.Length;
                        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                        if (++received == frames || durationSeconds > 0 && seconds >= durationSeconds)
                        {
                            return new(deviceName, link, width, height, subtype, bytes.Length,
                                Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length > 2 && bytes[0] == 255 && bytes[1] == 216, received, totalBytes, audio, seconds);
                        }
                    }
                    finally { Marshal.Release(buffer); }
                }
                finally { Marshal.Release(sample); }
            }
            throw new IOException("Camera returned no video samples.");
        }
        finally
        {
            if (reader != IntPtr.Zero) { Marshal.Release(reader); }
            if (mediaType != IntPtr.Zero) { Marshal.Release(mediaType); }
            if (source != IntPtr.Zero) { Marshal.Release(source); }
            if (activation != IntPtr.Zero) { Method<NoArguments>(activation, 34)(activation); Marshal.Release(activation); }
            if (activations != IntPtr.Zero) { Marshal.FreeCoTaskMem(activations); }
            if (attributes != IntPtr.Zero) { Marshal.Release(attributes); }
            Api.MFShutdown(); Api.CoUninitialize();
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static string GetString(IntPtr instance, Guid key)
    {
        Check(Method<GetAllocatedString>(instance, 13)(instance, ref key, out IntPtr text, out uint length));
        try { return Marshal.PtrToStringUni(text, checked((int)length)) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    private static void Check(int result) { if (result < 0) { Marshal.ThrowExceptionForHR(result); } }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuid(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuid(IntPtr self, ref Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt64(IntPtr self, ref Guid key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAllocatedString(IntPtr self, ref Guid key, out IntPtr value, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Activate(IntPtr self, ref Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetNativeType(IntPtr self, uint stream, uint index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetSelection(IntPtr self, uint stream, int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetType(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSample(IntPtr self, uint stream, uint flags, out uint actualStream, out uint streamFlags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBuffer(IntPtr self, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(IntPtr self, out IntPtr pointer, out uint maximum, out uint current);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArguments(IntPtr self);
    private static class Api
    {
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("mfplat.dll")] internal static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll")] internal static extern int MFShutdown();
        [DllImport("mfplat.dll")] internal static extern int MFCreateAttributes(out IntPtr attributes, uint capacity);
        [DllImport("mf.dll")] internal static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr activations, out uint count);
        [DllImport("mfreadwrite.dll")] internal static extern int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, out IntPtr reader);
    }
}
