using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vhui.Core.Windows;

namespace Vhui.Core.Protocol;

/// <summary>
/// Сервер протокола 8: сведения об устройстве и необязательный WinUSB backend.
/// По умолчанию слушает loopback; захват включается явно режимом serve-usb.
/// </summary>
public static class VirtualHereServer
{
    public static Task RunAsync(UsbPhysicalDevice device, int port, TextWriter log, CancellationToken cancellationToken, bool shareUsb = false, IPAddress? bindAddress = null)
    {
        var configuration = new Vhui.Core.Configuration.ServerConfiguration();
        configuration.Set("TCPPort", port.ToString());
        configuration.Set("NetworkInterface", (bindAddress ?? IPAddress.Loopback).ToString());
        configuration.Set("AllowedDevices", $"{device.VendorId:x4}/{device.ProductId:x4}");
        configuration.Set("TraceMsgs", "1");
        configuration.Set("UseAVAHI", "0");
        var host = new UsbServerHost(new UsbServerOptions { Configuration = configuration }, log,
            () => OperatingSystem.IsWindows() ? WindowsUsbInventory.Enumerate(device.VendorId, device.ProductId) : new([device], []), shareUsb: shareUsb);
        return host.RunAsync(cancellationToken);
    }

    public static VirtualHereMessage BuildServerInformation(VirtualHereMessage request, UsbPhysicalDevice device, Guid connectionId, bool shareUsb = false)
    {
        byte[] payload = new byte[724];
        payload[0] = 8;
        WriteString(payload, 1, 128, shareUsb ? "Vhui C# USB server" : "Vhui C# (discovery only)");
        byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes($"Vhui:{device.HubPath}:{device.Port}"));
        WriteString(payload, 129, 37, new Guid(identity.AsSpan(0, 16)).ToString("D"));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(166), 1);
        connectionId.TryWriteBytes(payload.AsSpan(168, 16));
        // Версия нашего приложения 0.1.0. Версия протокола задаётся отдельно выше.
        payload[184] = 0; payload[185] = 1; payload[186] = 0;
        WriteString(payload, 188, 256, Environment.MachineName);
        return request with { Type = 1, Payload = payload };
    }

    public static VirtualHereMessage BuildDeviceInformation(VirtualHereMessage request, UsbPhysicalDevice device)
    {
        byte[] payload = new byte[571];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, device.VendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), device.ProductId);
        WriteString(payload, 4, 64, device.Manufacturer ?? "");
        WriteString(payload, 68, 64, device.Product ?? $"USB {device.VendorId:X4}:{device.ProductId:X4}");
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(132), (uint)device.Port);
        payload[136] = 1; // Device status: available. Bus speed belongs to capture reply.
        if (device.DeviceDescriptor.Length >= 18) { payload[221] = device.DeviceDescriptor[17]; }
        if (device.ConfigurationDescriptor.Length >= 9)
        {
            payload[222] = device.ConfigurationDescriptor[4];
            ReadOnlySpan<byte> descriptors = device.ConfigurationDescriptor;
            for (int offset = 0; offset + 2 <= descriptors.Length;)
            {
                int length = descriptors[offset];
                if (length < 2 || length > descriptors.Length - offset) { break; }
                if (descriptors[offset + 1] == 4 && length >= 9)
                {
                    descriptors.Slice(offset + 5, 3).CopyTo(payload.AsSpan(223));
                    break;
                }
                offset += length;
            }
        }
        WriteString(payload, 306, 128, device.SerialNumber ?? "");
        return request with { Type = 16, Payload = payload };
    }

    private static void WriteString(byte[] target, int offset, int capacity, string value)
    {
        // Encoder.Convert не обрывает многобайтовый UTF-8 символ на границе поля.
        Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), target.AsSpan(offset, capacity - 1), true, out _, out _, out _);
    }
}
