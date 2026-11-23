using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Vhui.Core.Unpacking;

public sealed record PackedPayloadInfo(int StreamFileOffset, int StreamLength,
    int DeclaredOutputLimit, int VerifiedOutputLength, LzmaProperties Properties);

/// <summary>
/// Профиль именно предоставленного vhusbdwinw64.exe, восстановленный из C и GZF.
/// Адреса относятся к этой сборке; для других версий они не предполагаются верными.
/// </summary>
public static class VhusbdImageProfile
{
    public const string PackedSha256 = "6AE762D598800F0F11BA4F317F09697177A569BADA586CC518BAABFC4E043EEC";
    public const string VerifiedPayloadSha256 = "61945CC7B89351313683F74257EF9FC0AA0D49A0823E67E0F9901FEA9AE19067";
    public const int CompressedStreamRva = 0x7ce01f;
    public const int CompressedStreamLength = 0x3249e0;
    public const int DeclaredOutputLimit = 0xaf0605;
    public const int VerifiedOutputLength = 0xaf0603;

    public static PackedPayloadInfo Inspect(byte[] executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        if (Convert.ToHexString(SHA256.HashData(executable)) != PackedSha256)
        {
            throw new InvalidDataException("Этот профиль относится к другой сборке EXE: SHA-256 не совпадает.");
        }
        using var stream = new MemoryStream(executable, writable: false);
        using var pe = new PEReader(stream);
        PEHeader header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("Отсутствует заголовок PE.");
        if (header.Magic != PEMagic.PE32Plus || header.ImageBase != 0x140000000 || header.AddressOfEntryPoint != 0xaf2a10)
        {
            throw new InvalidDataException("Заголовок PE не соответствует разобранному C-экспорту.");
        }
        foreach (SectionHeader section in pe.PEHeaders.SectionHeaders)
        {
            int relativeOffset = CompressedStreamRva - section.VirtualAddress;
            if (relativeOffset < 2 || relativeOffset > section.SizeOfRawData - CompressedStreamLength)
            {
                continue;
            }
            int fileOffset = checked(section.PointerToRawData + relativeOffset);
            if (fileOffset < 2 || fileOffset > executable.Length - CompressedStreamLength)
            {
                throw new InvalidDataException("Сжатые данные выходят за границы файла.");
            }
            return new PackedPayloadInfo(fileOffset, CompressedStreamLength, DeclaredOutputLimit,
                VerifiedOutputLength, LzmaProperties.FromUpxBytes(executable[fileOffset - 2], executable[fileOffset - 1]));
        }
        throw new InvalidDataException("Секция с потоком LZMA отсутствует.");
    }

    public static LzmaDecodeResult ExtractVerifiedPayload(byte[] executable,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackedPayloadInfo info = Inspect(executable);
        // Независимый liblzma-декодер подтверждает этот префикс. Последние два
        // байта заявленного лимита нельзя получить из объявленного сжатого
        // диапазона: не дописываем их и не выдаём результат за готовый PE.
        LzmaDecodeResult result = new RawLzmaDecoder().Decode(
            executable.AsMemory(info.StreamFileOffset, info.StreamLength),
            info.VerifiedOutputLength, info.Properties, cancellationToken);
        if (Convert.ToHexString(SHA256.HashData(result.Bytes)) != VerifiedPayloadSha256)
        {
            throw new InvalidDataException("SHA-256 распакованного префикса не совпадает с эталоном.");
        }
        return result;
    }
}
