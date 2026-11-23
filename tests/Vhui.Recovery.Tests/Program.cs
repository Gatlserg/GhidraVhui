using System.Security.Cryptography;
using System.Text.Json;
using Vhui.Core.Unpacking;
using Vhui.Core.Configuration;
using Vhui.Core.Usb;
using System.Text;
using System.Buffers.Binary;
using Vhui.Core.Protocol;
using Vhui.Core.Windows;

if (args is ["--write-tls-fixture", var fixtureDirectory])
{
    TlsFixture.Write(fixtureDirectory);
    return 0;
}

if (args is ["--republish-fixture", var lifecycleDirectory])
{
    if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
    Directory.CreateDirectory(lifecycleDirectory);
    var configuration = new ServerConfiguration();
    configuration.Set("TCPPort", "17623"); configuration.Set("ScanInterval", "1"); configuration.Set("UseAVAHI", "0");
    configuration.Set("AllowedDevices", "145f/02e7");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
    var host = new UsbServerHost(new() { Configuration = configuration }, Console.Error,
        () => OperatingSystem.IsWindows() ? WindowsUsbInventory.Enumerate(0x145f, 0x02e7) : throw new PlatformNotSupportedException(),
        publish: _ => File.Exists(Path.Combine(lifecycleDirectory, "shared")));
    Task watcher = Task.Run(async () =>
    {
        try { while (!deadline.IsCancellationRequested && !File.Exists(Path.Combine(lifecycleDirectory, "stop"))) { await Task.Delay(100, deadline.Token); } }
        catch (OperationCanceledException) { }
        if (!deadline.IsCancellationRequested) { await deadline.CancelAsync(); }
    });
    try { await host.RunAsync(deadline.Token); }
    finally { await deadline.CancelAsync(); await watcher; }
    return 0;
}

int failures = 0;
var decoder = new RawLzmaDecoder();
var fixtures = JsonSerializer.Deserialize<List<Fixture>>(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "lzma-fixtures.json")))!;
foreach (Fixture fixture in fixtures)
{
    Check(fixture.Name, () =>
    {
        byte[] compressed = Convert.FromBase64String(fixture.CompressedBase64);
        var properties = new LzmaProperties(fixture.Lc, fixture.Lp, fixture.Pb);
        LzmaDecodeResult result = decoder.Decode(compressed, fixture.OutputLength + 1, properties);
        Require(result.EndMarkerSeen, "Expected end-of-stream marker.");
        Require(result.Bytes.Length == fixture.OutputLength, "Incorrect output length.");
        Require(Convert.ToHexString(SHA256.HashData(result.Bytes)) == fixture.Sha256, "Output differs from Python liblzma reference.");
        if (fixture.OutputLength > 0)
        {
            LzmaDecodeResult bounded = decoder.Decode(compressed, fixture.OutputLength, properties);
            Require(bounded.Bytes.SequenceEqual(result.Bytes), "Output-limit decoding differs from end-marker decoding.");
        }
    });
}

Check("UPX property layout", () =>
{
    LzmaProperties properties = LzmaProperties.FromUpxBytes(0x1a, 0x03);
    Require(properties.LiteralContextBits == 3 && properties.LiteralPositionBits == 0 && properties.PositionBits == 2, "Incorrect UPX properties.");
    Expect<ArgumentOutOfRangeException>(() => new LzmaProperties(9, 0, 2));
    Expect<ArgumentOutOfRangeException>(() => LzmaProperties.FromUpxBytes(7, 0));
});
Check("Truncated input, invalid distance and cancellation", () =>
{
    var properties = new LzmaProperties(3, 0, 2);
    Expect<EndOfStreamException>(() => decoder.Decode(new byte[4], 16, properties));
    Expect<InvalidDataException>(() => decoder.Decode(new byte[] { 255, 255, 255, 255, 255, 255 }, 16, properties));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Expect<OperationCanceledException>(() => decoder.Decode(new byte[8], 16, properties, cancellation.Token));
});
Check("Configuration keys, defaults and environment substitution", () =>
{
    var environment = new Dictionary<string, string> { ["ROOT"] = "C:\\vh", ["NESTED"] = "${ROOT}\\logs" };
    var warnings = new List<string>();
    var configuration = new ServerConfiguration(key => environment.GetValueOrDefault(key), warnings.Add);
    configuration.Set("ServerName", "first");
    configuration.Set("servername", "second");
    Require(configuration.GetString("SERVERNAME") == "second", "Configuration lookup must ignore key case.");
    Require(configuration.GetString("absent") is null, "Missing key should return null.");
    Require(configuration.GetString("absent", "default") == "default", "Default was lost.");
    configuration.Set("LogPath", "${NESTED}/${ROOT}/${MISSING}");
    Require(configuration.GetString("logpath") == "C:\\vh\\logs/C:\\vh/", "Incorrect nested or missing-variable substitution.");
    Require(warnings.Count == 1, "Missing environment variable should be reported.");
    Require(configuration.GetString("LogPath", expandEnvironment: false) == "${NESTED}/${ROOT}/${MISSING}", "Disabled substitution changed text.");
    configuration.Set("Incomplete", "${UNFINISHED");
    Require(configuration.GetString("Incomplete") == "${UNFINISHED", "Unclosed substitution should remain literal.");
});
Check("Configuration Windows strtol decimal-prefix compatibility", () =>
{
    var configuration = new ServerConfiguration();
    Require(configuration.GetInteger("TCPPort", 7575) == 7575, "Missing numeric default failed.");
    foreach (var (text, expected) in new (string, int)[] {
        ("  +7575tail", 7575), ("-42 rest", -42), ("no digits", 0), ("", 0),
        ("0x20", 0), ("2147483648", int.MaxValue), ("-2147483649", int.MinValue),
        ("-2147483648", int.MinValue), ("2147483647", int.MaxValue) })
    {
        configuration.Set("number", text);
        Require(configuration.GetInteger("number", 99) == expected, $"Incorrect strtol result for '{text}'.");
    }
});
Check("Environment cycles and unbounded expansion are rejected", () =>
{
    var cyclic = new ServerConfiguration(name => name == "A" ? "${B}" : "${A}");
    cyclic.Set("value", "${A}");
    Expect<InvalidDataException>(() => cyclic.GetString("value"));
    var growing = new ServerConfiguration(_ => "${A}x");
    growing.Set("value", "${A}");
    Expect<InvalidDataException>(() => growing.GetString("value"));
});
Check("USB string descriptor request matches native layout", () =>
{
    byte[] request = UsbStringDescriptorCodec.CreateRequest(7, 2, 0x409);
    Require(request.Length == 0x10b, "Incorrect IOCTL buffer length.");
    Require(request.AsSpan(0, 12).SequenceEqual(new byte[] { 7, 0, 0, 0, 0, 0, 2, 3, 9, 4, 255, 0 }), "Incorrect USB_DESCRIPTOR_REQUEST fields.");
    Require(request.AsSpan(12).IndexOfAnyExcept((byte)0) == -1, "Descriptor result area must be zeroed.");
});
Check("USB UTF-16 descriptor and malformed replies", () =>
{
    byte[] text = Encoding.Unicode.GetBytes("USB Тест");
    byte[] response = new byte[14 + text.Length];
    response[12] = (byte)(text.Length + 2);
    response[13] = 3;
    text.CopyTo(response, 14);
    UsbStringDescriptor descriptor = UsbStringDescriptorCodec.ParseResponse(response, response.Length, 2, 0x409);
    Require(descriptor.Index == 2 && descriptor.LanguageId == 0x409 && descriptor.DecodeText() == "USB Тест", "Incorrect descriptor result.");
    Expect<InvalidDataException>(() => UsbStringDescriptorCodec.ParseResponse(response, 1, 2, 0x409));
    Expect<InvalidDataException>(() => UsbStringDescriptorCodec.ParseResponse(response, response.Length + 1, 2, 0x409));
    response[13] = 2;
    Expect<InvalidDataException>(() => UsbStringDescriptorCodec.ParseResponse(response, response.Length, 2, 0x409));
    response[13] = 3;
    response[12]--;
    Expect<InvalidDataException>(() => UsbStringDescriptorCodec.ParseResponse(response, response.Length, 2, 0x409));
    Expect<InvalidDataException>(() => UsbStringDescriptorCodec.ParseResponse(response, response.Length - 1, 2, 0x409));
    Expect<InvalidOperationException>(() => new UsbStringDescriptor(0, 0, [4, 3, 9, 4]).DecodeText());
});
Check("LZ4 raw block: literals, overlapping matches and bounds", () =>
{
    Require(Encoding.ASCII.GetString(Lz4BlockDecoder.Decode([0x50, 104, 101, 108, 108, 111], 5)) == "hello", "Literal LZ4 failed.");
    byte[] repeated = [0x35, 97, 98, 99, 3, 0, 0x50, 120, 121, 122, 65, 66];
    Require(Encoding.ASCII.GetString(Lz4BlockDecoder.Decode(repeated, 17)) == "abcabcabcabcxyzAB", "Overlapping match failed.");
    byte[] extended = new byte[302];
    extended[0] = 0xf0; extended[1] = 255; extended[2] = 29;
    Array.Fill(extended, (byte)65, 3, 299);
    Require(Lz4BlockDecoder.Decode(extended, 299).All(value => value == 65), "Extended literal length failed.");
    Expect<InvalidDataException>(() => Lz4BlockDecoder.Decode([0x10, 65, 0, 0], 5));
    Expect<InvalidDataException>(() => Lz4BlockDecoder.Decode([0x10, 65, 2, 0], 5));
    Expect<InvalidDataException>(() => Lz4BlockDecoder.Decode([0x10, 65, 1], 5));
    Expect<InvalidDataException>(() => Lz4BlockDecoder.Decode([0xf0, 255], 5));
    Expect<InvalidDataException>(() => Lz4BlockDecoder.Decode(repeated, 16));
    Expect<InvalidDataException>(() => Lz4BlockDecoder.Decode(repeated, 18));
});
Check("VirtualHere transport: fragmented frames, compression and malformed lengths", () =>
{
    var original = new VirtualHereMessage(0x10, 7, 0x1122334455667788, 0xaabbccdd,
        0x8877665544332211, 23, [1, 2, 3]);
    using var wire = new MemoryStream();
    VirtualHereFrameCodec.WriteAsync(wire, original).GetAwaiter().GetResult();
    byte[] frame = wire.ToArray();
    Require(frame.AsSpan(0, 8).SequenceEqual(new byte[] { 0, 0, 0, 0, 36, 0, 0, 0 }), "Incorrect outer lengths.");
    Require(frame[8] == 16 && frame[13] == 0x88 && frame[20] == 0x11, "Incorrect packed header layout.");
    using var fragmented = new FragmentedReadStream(frame);
    VirtualHereMessage decoded = VirtualHereFrameCodec.ReadAsync(fragmented).GetAwaiter().GetResult()!;
    Require(decoded with { Payload = original.Payload } == original && decoded.Payload.SequenceEqual(original.Payload), "Frame changed header or payload.");
    Require(VirtualHereFrameCodec.ReadAsync(fragmented).GetAwaiter().GetResult() is null, "Clean EOF should return null.");

    // Independent LZ4 block consisting of one extended literal run containing a message.
    byte[] body = original.ToBytes();
    byte[] compressedFrame = new byte[8 + 2 + body.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(compressedFrame, (uint)(body.Length + 2));
    BinaryPrimitives.WriteUInt32LittleEndian(compressedFrame.AsSpan(4), (uint)body.Length);
    compressedFrame[8] = 0xf0; compressedFrame[9] = (byte)(body.Length - 15);
    body.CopyTo(compressedFrame, 10);
    using var compressed = new FragmentedReadStream(compressedFrame);
    Require(VirtualHereFrameCodec.ReadAsync(compressed).GetAwaiter().GetResult()!.ToBytes().SequenceEqual(body), "Compressed frame changed bytes.");
    Expect<EndOfStreamException>(() => VirtualHereFrameCodec.ReadAsync(new FragmentedReadStream(frame[..^1])).GetAwaiter().GetResult());
    Expect<EndOfStreamException>(() => VirtualHereFrameCodec.ReadAsync(new FragmentedReadStream(frame[..3])).GetAwaiter().GetResult());
    byte[] invalid = frame.ToArray(); invalid[4] = 0;
    Expect<InvalidDataException>(() => VirtualHereFrameCodec.ReadAsync(new MemoryStream(invalid)).GetAwaiter().GetResult());
    BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(4), uint.MaxValue);
    Expect<InvalidDataException>(() => VirtualHereFrameCodec.ReadAsync(new MemoryStream(invalid)).GetAwaiter().GetResult());
    body[29]++;
    Expect<InvalidDataException>(() => VirtualHereMessage.Parse(body));
});
Check("Native isochronous completion header and compact packet packing", () =>
{
    byte[] buffer = Encoding.ASCII.GetBytes("ab00cde0");
    byte[] packets = Convert.FromHexString("040000000200000000000000040000000300000000000000");
    byte[] expectedHeader = Convert.FromHexString("050000000000000000000000");
    VirtualHereMessage padded = UsbWireFormat.IsochronousReply(VirtualHereMessage.Create(0x24), buffer, packets, true, 0, 5);
    Require(padded.Type == 0x25 && padded.Payload.AsSpan(0, 12).SequenceEqual(expectedHeader), "Iso completion byte count/frame/error header is incorrect.");
    Require(padded.Payload.AsSpan(36).SequenceEqual(buffer), "Legacy isochronous data must retain requested packet spacing.");
    VirtualHereMessage compact = UsbWireFormat.IsochronousReply(VirtualHereMessage.Create(0x4d), buffer, packets, true, 0, 5);
    Require(compact.Type == 0x4c && Encoding.ASCII.GetString(compact.Payload.AsSpan(36)) == "abcde", "Compact isochronous data retains gaps or drops bytes.");
    VirtualHereMessage cancelled = UsbWireFormat.IsochronousReply(VirtualHereMessage.Create(0x4d), buffer, packets, true, -104, 0);
    Require(cancelled.Type == 0x25 && BinaryPrimitives.ReadInt32LittleEndian(cancelled.Payload) == -104, "Failed compact transfers must use the legacy error response.");
    Expect<InvalidDataException>(() => UsbWireFormat.IsochronousReply(VirtualHereMessage.Create(0x4d), buffer, packets, true, 0, 4));
    packets[4] = 5;
    Expect<InvalidDataException>(() => UsbWireFormat.IsochronousReply(VirtualHereMessage.Create(0x4d), buffer, packets, true, 0, 5));
});
Check("Native client/server capture and control completion byte-for-byte fixtures", () =>
{
    byte[] ReadFixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    string CString(byte[] bytes, int offset, int count) => Encoding.UTF8.GetString(bytes, offset, count).Split('\0')[0];
    VirtualHereMessage capture = VirtualHereMessage.Parse(ReadFixture("native-capture-reply.bin"));
    VirtualHereMessage captureRequest = VirtualHereMessage.Parse(ReadFixture("native-capture-request.bin"));
    VirtualHereMessage info = VirtualHereMessage.Parse(ReadFixture("native-device-info.bin"));
    byte[] descriptor = capture.Payload.AsSpan(5, 18).ToArray();
    int configurationLength = BinaryPrimitives.ReadUInt16LittleEndian(capture.Payload.AsSpan(25));
    var device = new UsbPhysicalDevice("native-fixture", 7, BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(8)),
        BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(10)), capture.Payload[4], false,
        CString(info.Payload, 4, 64), CString(capture.Payload, 0x10016, 128), CString(capture.Payload, 0x10096, 128),
        descriptor, capture.Payload.AsSpan(23, configurationLength).ToArray());
    string serial = CString(capture.Payload, 0x10116, 64);
    Require(VirtualHereServer.BuildDeviceInformation(captureRequest, device).Payload[136] == info.Payload[136],
        "Device status was confused with USB bus speed.");
    Require(UsbWireFormat.CaptureReply(captureRequest, device, serial, 1).ToBytes().SequenceEqual(capture.ToBytes()),
        "Managed capture reply differs from native server bytes.");

    VirtualHereMessage request = VirtualHereMessage.Parse(ReadFixture("native-control-request.bin"));
    VirtualHereMessage expected = VirtualHereMessage.Parse(ReadFixture("native-control-reply.bin"));
    var (setup, buffer, input) = UsbWireFormat.ParseControl(request);
    Require(input && setup[1] == 6 && buffer.Length == 64, "Native GET_DESCRIPTOR request decoded incorrectly.");
    descriptor.CopyTo(buffer, 0);
    VirtualHereMessage actual = UsbWireFormat.ControlReply(request, setup, buffer, input, 0, descriptor.Length);
    Require(actual.ToBytes().SequenceEqual(expected.ToBytes()), "Managed control completion differs from native server bytes.");
    Expect<InvalidDataException>(() => UsbWireFormat.ParseControl(request with { Payload = request.Payload[..15] }));
    byte[] invalidOut = request.Payload.ToArray(); invalidOut[8] = 0;
    Expect<InvalidDataException>(() => UsbWireFormat.ParseControl(request with { Payload = invalidOut }));
    Expect<InvalidDataException>(() => UsbWireFormat.ControlReply(request, setup, buffer, input, 0, 65));
});
foreach (var (name, check) in CompatibilityChecks.Cases()) { Check(name, () => check().GetAwaiter().GetResult()); }
if (args is ["--packed", var packedPath])
{
    Check("Original vhusbd UPX stream: independently verified 11 MB prefix", () =>
    {
        byte[] packed = File.ReadAllBytes(packedPath);
        Require(Convert.ToHexString(SHA256.HashData(packed)) == "6AE762D598800F0F11BA4F317F09697177A569BADA586CC518BAABFC4E043EEC", "Unexpected source EXE.");
        LzmaDecodeResult result = decoder.Decode(packed.AsMemory(0x21f, 0x3249e0), 0xaf0603,
            LzmaProperties.FromUpxBytes(packed[0x21d], packed[0x21e]));
        Require(result.Bytes.Length == 0xaf0603, "Incorrect decoded prefix length.");
        Require(Convert.ToHexString(SHA256.HashData(result.Bytes)) == "61945CC7B89351313683F74257EF9FC0AA0D49A0823E67E0F9901FEA9AE19067", "Payload differs from independent LZMA reference.");
        // The exported stub declares a limit two bytes larger. Do not silently
        // manufacture those bytes or read past the declared input boundary.
        Expect<EndOfStreamException>(() => decoder.Decode(packed.AsMemory(0x21f, 0x3249e0), 0xaf0605,
            LzmaProperties.FromUpxBytes(packed[0x21d], packed[0x21e])));
        Console.WriteLine($"     Decoded {result.Bytes.Length} bytes, consumed {result.InputBytesRead} input bytes.");
        PackedPayloadInfo profile = VhusbdImageProfile.Inspect(packed);
        Require(profile.StreamFileOffset == 0x21f && profile.Properties.PositionBits == 2, "PE RVA mapping failed.");
        Require(VhusbdImageProfile.ExtractVerifiedPayload(packed).Bytes.SequenceEqual(result.Bytes), "Profile extraction differs.");
        packed[0] ^= 1;
        Expect<InvalidDataException>(() => VhusbdImageProfile.Inspect(packed));
    });
}
Console.WriteLine(failures == 0 ? "All recovery checks passed." : $"Failures: {failures}");
return failures == 0 ? 0 : 1;

void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception}"); }
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
}

static void Expect<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

internal sealed record Fixture(string Name, int Lc, int Lp, int Pb, int OutputLength, string Sha256, string CompressedBase64);

internal sealed class FragmentedReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
}
