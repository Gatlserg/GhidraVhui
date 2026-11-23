using System.Buffers.Binary;
using System.Text;

namespace Vhui.Core.Usb;

/// <summary>Результат ReadUsbStringDescriptorFromHub (0x140733680).</summary>
public sealed record UsbStringDescriptor(byte Index, ushort LanguageId, byte[] DescriptorBytes)
{
    public string DecodeText()
    {
        if (Index == 0)
        {
            throw new InvalidOperationException("Дескриптор 0 содержит идентификаторы языков, а не текст.");
        }
        return Encoding.Unicode.GetString(DescriptorBytes.AsSpan(2));
    }
}

/// <summary>
/// Формирование запроса и проверка ответа IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION.
/// Вызов драйвера сюда не входит; код не переключает и не захватывает устройства.
/// </summary>
public static class UsbStringDescriptorCodec
{
    public const uint GetDescriptorIoctl = 0x220410;
    private const int RequestHeaderLength = 12;
    private const int MaximumDescriptorLength = 255;

    public static byte[] CreateRequest(byte portIndex, byte descriptorIndex, ushort languageId)
    {
        var request = new byte[RequestHeaderLength + MaximumDescriptorLength];
        BinaryPrimitives.WriteUInt32LittleEndian(request, portIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)(0x300 | descriptorIndex));
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(8), languageId);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(10), MaximumDescriptorLength);
        return request;
    }

    public static UsbStringDescriptor ParseResponse(ReadOnlySpan<byte> response, int bytesReturned,
        byte descriptorIndex, ushort languageId)
    {
        if (bytesReturned < RequestHeaderLength + 2 || bytesReturned > response.Length)
        {
            throw new InvalidDataException("Ответ USB-хаба слишком короткий или выходит за границы буфера.");
        }
        ReadOnlySpan<byte> descriptor = response.Slice(RequestHeaderLength, bytesReturned - RequestHeaderLength);
        if (descriptor[1] != 3) { throw new InvalidDataException("Получен не строковый USB-дескриптор."); }
        if (descriptor[0] != descriptor.Length) { throw new InvalidDataException("Длина USB-дескриптора не совпадает с ответом."); }
        if ((descriptor.Length & 1) != 0) { throw new InvalidDataException("Длина строкового USB-дескриптора должна быть чётной."); }
        return new UsbStringDescriptor(descriptorIndex, languageId, descriptor.ToArray());
    }
}
