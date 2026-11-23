namespace Vhui.Core.Unpacking;

public sealed record LzmaDecodeResult(byte[] Bytes, int InputBytesRead, bool EndMarkerSeen);

/// <summary>
/// Управляемая реконструкция LZMA-части entry из предоставленного C-экспорта.
/// Работает с массивами байтов; не загружает DLL и не исполняет машинный код.
/// </summary>
public sealed class RawLzmaDecoder
{
    // Смещения моделей из исходного entry, в элементах ushort, а не в байтах.
    private const int IsMatch = 0;
    private const int IsRepeatedMatch = 192;         // C: +0x180
    private const int IsRepeatDistance0 = 204;       // C: +0x198
    private const int IsRepeatDistance1 = 216;       // C: +0x1b0
    private const int IsRepeatDistance2 = 228;       // C: +0x1c8
    private const int IsShortRepeat = 240;           // C: +0x1e0
    private const int PositionSlot = 432;            // C: +0x360
    private const int SpecialDistance = 688;
    private const int DistanceAlignment = 802;       // C: +0x644
    private const int MatchLength = 818;             // C: +0x664
    private const int RepeatLength = 1332;           // C: +0xa68
    private const int Literal = 1846;                // C: +0xe6c

    public LzmaDecodeResult Decode(ReadOnlyMemory<byte> input, int outputLimit,
        LzmaProperties properties, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentOutOfRangeException.ThrowIfNegative(outputLimit);
        cancellationToken.ThrowIfCancellationRequested();
        var probabilities = new ushort[Literal + (768 << (properties.LiteralContextBits + properties.LiteralPositionBits))];
        Array.Fill(probabilities, (ushort)1024);
        var decoder = new RangeDecoder(input, probabilities);
        byte[] output = new byte[outputLimit];
        int position = 0;
        int state = 0;
        uint repeatDistance0 = 1, repeatDistance1 = 1, repeatDistance2 = 1, repeatDistance3 = 1;
        int positionMask = (1 << properties.PositionBits) - 1;
        int literalPositionMask = (1 << properties.LiteralPositionBits) - 1;

        while (position < outputLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int positionState = position & positionMask;
            if (decoder.DecodeBit(IsMatch + state * 16 + positionState) == 0)
            {
                byte previousByte = position == 0 ? (byte)0 : output[position - 1];
                int context = ((position & literalPositionMask) << properties.LiteralContextBits)
                    + (previousByte >> (8 - properties.LiteralContextBits));
                int literalModel = Literal + context * 768;
                int literalSymbol = 1;
                if (state >= 7)
                {
                    CheckDistance(repeatDistance0, position);
                    int matchedByte = output[position - (int)repeatDistance0];
                    do
                    {
                        int matchedBit = (matchedByte >> 7) & 1;
                        matchedByte <<= 1;
                        int bit = decoder.DecodeBit(literalModel + ((1 + matchedBit) << 8) + literalSymbol);
                        literalSymbol = (literalSymbol << 1) | bit;
                        if (matchedBit != bit)
                        {
                            break;
                        }
                    } while (literalSymbol < 256);
                }
                while (literalSymbol < 256)
                {
                    literalSymbol = (literalSymbol << 1) | decoder.DecodeBit(literalModel + literalSymbol);
                }
                output[position++] = (byte)literalSymbol;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            int length;
            if (decoder.DecodeBit(IsRepeatedMatch + state) == 1)
            {
                if (decoder.DecodeBit(IsRepeatDistance0 + state) == 0)
                {
                    if (decoder.DecodeBit(IsShortRepeat + state * 16 + positionState) == 0)
                    {
                        CheckDistance(repeatDistance0, position);
                        output[position] = output[position - (int)repeatDistance0];
                        position++;
                        state = state < 7 ? 9 : 11;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (decoder.DecodeBit(IsRepeatDistance1 + state) == 0)
                    {
                        distance = repeatDistance1;
                    }
                    else
                    {
                        if (decoder.DecodeBit(IsRepeatDistance2 + state) == 0)
                        {
                            distance = repeatDistance2;
                        }
                        else
                        {
                            distance = repeatDistance3;
                            repeatDistance3 = repeatDistance2;
                        }
                        repeatDistance2 = repeatDistance1;
                    }
                    repeatDistance1 = repeatDistance0;
                    repeatDistance0 = distance;
                }
                length = decoder.DecodeLength(RepeatLength, positionState) + 2;
                state = state < 7 ? 8 : 11;
            }
            else
            {
                repeatDistance3 = repeatDistance2;
                repeatDistance2 = repeatDistance1;
                repeatDistance1 = repeatDistance0;
                int lengthSymbol = decoder.DecodeLength(MatchLength, positionState);
                length = lengthSymbol + 2;
                state = state < 7 ? 7 : 10;
                int distanceSlot = decoder.DecodeTree(PositionSlot + Math.Min(lengthSymbol, 3) * 64, 6);
                uint zeroBasedDistance;
                if (distanceSlot < 4)
                {
                    zeroBasedDistance = (uint)distanceSlot;
                }
                else
                {
                    int distanceBits = (distanceSlot >> 1) - 1;
                    zeroBasedDistance = (uint)(2 | (distanceSlot & 1)) << distanceBits;
                    if (distanceSlot < 14)
                    {
                        zeroBasedDistance += decoder.DecodeReverseTree(
                            SpecialDistance + (int)zeroBasedDistance - distanceSlot - 1, distanceBits);
                    }
                    else
                    {
                        zeroBasedDistance += decoder.DecodeDirectBits(distanceBits - 4) << 4;
                        zeroBasedDistance += decoder.DecodeReverseTree(DistanceAlignment, 4);
                    }
                }
                repeatDistance0 = unchecked(zeroBasedDistance + 1);
                if (repeatDistance0 == 0)
                {
                    return new LzmaDecodeResult(output[..position], decoder.InputBytesRead, true);
                }
            }
            CheckDistance(repeatDistance0, position);
            // Перекрывающаяся копия: каждый следующий байт может ссылаться на
            // только что записанный, поэтому Array.Copy здесь использовать нельзя.
            int copyLength = Math.Min(length, outputLimit - position);
            for (int index = 0; index < copyLength; index++)
            {
                output[position] = output[position - (int)repeatDistance0];
                position++;
            }
        }
        return new LzmaDecodeResult(output, decoder.InputBytesRead, false);
    }

    private static void CheckDistance(uint distance, int position)
    {
        if (distance == 0 || distance > (uint)position)
        {
            throw new InvalidDataException("LZMA ссылается на ещё не декодированные байты.");
        }
    }

    private sealed class RangeDecoder
    {
        private const uint NormalizeThreshold = 1 << 24;
        private readonly ReadOnlyMemory<byte> _input;
        private readonly ushort[] _probabilities;
        private uint _range = uint.MaxValue;
        private uint _code;
        public int InputBytesRead { get; private set; }

        public RangeDecoder(ReadOnlyMemory<byte> input, ushort[] probabilities)
        {
            _input = input;
            _probabilities = probabilities;
            for (int index = 0; index < 5; index++)
            {
                _code = (_code << 8) | ReadByte();
            }
        }

        public int DecodeBit(int modelIndex)
        {
            Normalize();
            uint probability = _probabilities[modelIndex];
            uint bound = (_range >> 11) * probability;
            if (_code < bound)
            {
                _range = bound;
                _probabilities[modelIndex] = (ushort)(probability + ((2048 - probability) >> 5));
                return 0;
            }
            _range -= bound;
            _code -= bound;
            _probabilities[modelIndex] = (ushort)(probability - (probability >> 5));
            return 1;
        }

        public int DecodeTree(int modelBase, int bitCount)
        {
            int node = 1;
            for (int index = 0; index < bitCount; index++)
            {
                node = (node << 1) | DecodeBit(modelBase + node);
            }
            return node - (1 << bitCount);
        }

        public uint DecodeReverseTree(int modelBase, int bitCount)
        {
            int node = 1;
            uint result = 0;
            for (int index = 0; index < bitCount; index++)
            {
                int bit = DecodeBit(modelBase + node);
                node = (node << 1) | bit;
                result |= (uint)bit << index;
            }
            return result;
        }

        public uint DecodeDirectBits(int bitCount)
        {
            uint result = 0;
            for (int index = 0; index < bitCount; index++)
            {
                Normalize();
                _range >>= 1;
                result <<= 1;
                if (_code >= _range)
                {
                    _code -= _range;
                    result |= 1;
                }
            }
            return result;
        }

        public int DecodeLength(int modelBase, int positionState)
        {
            if (DecodeBit(modelBase) == 0)
            {
                return DecodeTree(modelBase + 2 + positionState * 8, 3);
            }
            if (DecodeBit(modelBase + 1) == 0)
            {
                return 8 + DecodeTree(modelBase + 130 + positionState * 8, 3);
            }
            return 16 + DecodeTree(modelBase + 258, 8);
        }

        private void Normalize()
        {
            if (_range < NormalizeThreshold)
            {
                _range <<= 8;
                _code = (_code << 8) | ReadByte();
            }
        }

        private byte ReadByte()
        {
            if (InputBytesRead == _input.Length)
            {
                throw new EndOfStreamException("Сжатый поток LZMA оборван.");
            }
            return _input.Span[InputBytesRead++];
        }
    }
}
