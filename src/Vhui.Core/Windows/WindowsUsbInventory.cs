using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Vhui.Core.Usb;
using Microsoft.Win32;
using System.Text;

namespace Vhui.Core.Windows;

public sealed record UsbInventoryError(string HubPath, int Port, int ErrorCode, string Message);
public sealed record UsbPhysicalDevice(string HubPath, int Port, ushort VendorId, ushort ProductId,
    byte Speed, bool IsHub, string? Manufacturer, string? Product, string? SerialNumber,
    byte[] DeviceDescriptor, byte[] ConfigurationDescriptor);
public sealed record UsbInventoryResult(IReadOnlyList<UsbPhysicalDevice> Devices, IReadOnlyList<UsbInventoryError> Errors);

/// <summary>
/// Чтение USB-хабов через SetupAPI и информационные IOCTL. Устройства не захватывает,
/// драйверы не устанавливает. Основа: EnumerateWindowsUsbDevices и ReadUsb*FromHub.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsUsbInventory
{
    private static readonly Guid HubInterfaceClass = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const uint GetNodeInformation = 0x220408;
    private const uint GetConnectionInformationEx = 0x220448;
    private static readonly UsbInventoryCache Cache = new();

    public static bool IsVirtualHereHub(string path)
    {
        int marker = path.LastIndexOf("#{", StringComparison.Ordinal);
        if (marker <= 4) { return false; }
        string id = path[4..marker].Replace('#', '\\');
        uint node = 0;
        if (Native.CM_Locate_DevNodeW(ref node, id, 0) != 0) { return false; }
        for (int depth = 0; depth < 32; depth++)
        {
            var name = new StringBuilder(512);
            if (Native.CM_Get_Device_IDW(node, name, name.Capacity, 0) != 0) { break; }
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + name);
            if (key?.GetValue("Service") is string service && service.StartsWith("vhusb", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (Native.CM_Get_Parent(out uint parent, node, 0) != 0) { break; }
            node = parent;
        }
        return false;
    }
    public static UsbInventoryResult Enumerate(ushort? vendorId = null, ushort? productId = null)
        => WinUsbDeviceLease.ReadStableInventory(() => Cache.Merge(EnumerateCore(vendorId, productId)));

    private static UsbInventoryResult EnumerateCore(ushort? vendorId, ushort? productId)
    {
        var devices = new List<UsbPhysicalDevice>();
        var errors = new List<UsbInventoryError>();
        foreach (string path in GetInterfacePaths(HubInterfaceClass))
        {
            // Avoid recursively exporting devices created by a loopback VirtualHere client.
            if (IsVirtualHereHub(path)) { continue; }
            using SafeFileHandle hub = OpenHub(path);
            if (hub.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                errors.Add(new(path, 0, code, new Win32Exception(code).Message));
                continue;
            }
            var information = new byte[1024];
            if (!Query(hub, GetNodeInformation, information, out _))
            {
                int code = Marshal.GetLastWin32Error();
                errors.Add(new(path, 0, code, new Win32Exception(code).Message));
                continue;
            }
            int portCount = information[6];
            for (int port = 1; port <= portCount; port++)
            {
                var connection = new byte[4096];
                BinaryPrimitives.WriteUInt32LittleEndian(connection, (uint)port);
                if (!Query(hub, GetConnectionInformationEx, connection, out int returned))
                {
                    int code = Marshal.GetLastWin32Error();
                    errors.Add(new(path, port, code, new Win32Exception(code).Message));
                    continue;
                }
                if (returned < 35)
                {
                    errors.Add(new(path, port, 13, "Truncated USB connection information."));
                    continue;
                }
                if (BinaryPrimitives.ReadUInt32LittleEndian(connection.AsSpan(31)) != 1)
                {
                    continue;
                }
                ushort vid = BinaryPrimitives.ReadUInt16LittleEndian(connection.AsSpan(12));
                ushort pid = BinaryPrimitives.ReadUInt16LittleEndian(connection.AsSpan(14));
                if ((vendorId.HasValue && vendorId != vid) || (productId.HasValue && productId != pid)) { continue; }

                byte[] descriptor = connection.AsSpan(4, 18).ToArray();
                // Continue checking physical presence, but never send descriptor requests
                // through the hub while a remote client owns the WinUSB function.
                UsbPhysicalDevice? captured = WinUsbDeviceLease.CapturedDevice(path, port);
                if (captured is not null && captured.VendorId == vid && captured.ProductId == pid)
                {
                    devices.Add(captured with { Speed = connection[23], DeviceDescriptor = descriptor });
                    continue;
                }
                byte[] configuration = [];
                string? manufacturer = null, product = null, serial = null;
                try
                {
                    configuration = ReadDescriptor(hub, (byte)port, 2, 0, 0, 9);
                    if (configuration.Length >= 4)
                    {
                        int totalLength = BinaryPrimitives.ReadUInt16LittleEndian(configuration.AsSpan(2));
                        if (totalLength is >= 9 and <= 65535)
                        {
                            configuration = ReadDescriptor(hub, (byte)port, 2, 0, 0, totalLength);
                        }
                    }
                    ushort language = GetLanguage(hub, (byte)port);
                    manufacturer = ReadText(hub, (byte)port, descriptor[14], language);
                    product = ReadText(hub, (byte)port, descriptor[15], language);
                    serial = ReadText(hub, (byte)port, descriptor[16], language);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
                {
                    errors.Add(new(path, port, exception is Win32Exception win32 ? win32.NativeErrorCode : 13, exception.Message));
                }
                devices.Add(new(path, port, vid, pid, connection[23], connection[24] != 0,
                    manufacturer, product, serial, descriptor, configuration));
            }
        }
        return new(devices, errors);
    }

    private static string? ReadText(SafeFileHandle hub, byte port, byte index, ushort language)
    {
        if (index == 0) { return null; }
        byte[] request = UsbStringDescriptorCodec.CreateRequest(port, index, language);
        if (!Query(hub, UsbStringDescriptorCodec.GetDescriptorIoctl, request, out int returned))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return UsbStringDescriptorCodec.ParseResponse(request, returned, index, language).DecodeText();
    }

    private static ushort GetLanguage(SafeFileHandle hub, byte port)
    {
        byte[] languages = ReadDescriptor(hub, port, 3, 0, 0, 255);
        for (int index = 2; index + 1 < languages.Length; index += 2)
        {
            ushort language = BinaryPrimitives.ReadUInt16LittleEndian(languages.AsSpan(index));
            if (language == 0x409) { return language; }
        }
        return languages.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(languages.AsSpan(2)) : (ushort)0x409;
    }

    private static byte[] ReadDescriptor(SafeFileHandle hub, byte port, byte type, byte index, ushort language, int length)
    {
        byte[] request = new byte[12 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(request, port);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)((type << 8) | index));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(8), language);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(10), (ushort)length);
        if (!Query(hub, UsbStringDescriptorCodec.GetDescriptorIoctl, request, out int returned))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (returned < 12 || returned > request.Length) { throw new InvalidDataException("Неверная длина USB-дескриптора."); }
        return request.AsSpan(12, returned - 12).ToArray();
    }

    private static SafeFileHandle OpenHub(string path)
    {
        SafeFileHandle handle = Native.CreateFileW(path, 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid && Marshal.GetLastWin32Error() == 5)
        {
            handle.Dispose();
            // Информационные IOCTL имеют FILE_ANY_ACCESS; для них достаточно
            // разрешённого системой открытия без GENERIC_READ/GENERIC_WRITE.
            handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }
        return handle;
    }

    private static bool Query(SafeFileHandle hub, uint code, byte[] buffer, out int returned)
        => Native.DeviceIoControl(hub, code, buffer, buffer.Length, buffer, buffer.Length, out returned, IntPtr.Zero);

    internal static string ReadDriverKey(UsbPhysicalDevice device)
    {
        using SafeFileHandle hub = OpenHub(device.HubPath);
        if (hub.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        byte[] buffer = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)device.Port);
        if (!Query(hub, 0x220420, buffer, out int returned)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (returned < 10) { throw new InvalidDataException("USB driver key response is truncated."); }
        return System.Text.Encoding.Unicode.GetString(buffer, 8, returned - 8).TrimEnd('\0');
    }

    internal static IReadOnlyList<string> GetInterfacePaths(Guid classGuid)
    {
        IntPtr set = Native.SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var paths = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref classGuid, index, ref data))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) { break; }
                    throw new Win32Exception(error);
                }
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required < 6 || required > 65536) { throw new InvalidDataException("Некорректная длина пути USB-хаба."); }
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    paths.Add(Marshal.PtrToStringUni(IntPtr.Add(detail, 4))!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    private static class Native
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Locate_DevNodeW(ref uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] internal static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid classGuid, uint index, ref DeviceInterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, [In, Out] byte[] input, int inputSize,
            [In, Out] byte[] output, int outputSize, out int returned, IntPtr overlapped);
    }
}
