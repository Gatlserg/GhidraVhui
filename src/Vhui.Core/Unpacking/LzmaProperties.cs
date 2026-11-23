namespace Vhui.Core.Unpacking;

/// <summary>Имена свойств LZMA вместо байтов DAT_1407ce01d / DAT_1407ce01e.</summary>
public sealed record LzmaProperties
{
    public int LiteralContextBits { get; }
    public int LiteralPositionBits { get; }
    public int PositionBits { get; }

    public LzmaProperties(int literalContextBits, int literalPositionBits, int positionBits)
    {
        if (literalContextBits is < 0 or > 8 || literalPositionBits is < 0 or > 4 || positionBits is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(literalContextBits), "Недопустимые свойства LZMA.");
        }
        LiteralContextBits = literalContextBits;
        LiteralPositionBits = literalPositionBits;
        PositionBits = positionBits;
    }

    public static LzmaProperties FromUpxBytes(byte positionAndWorkspace, byte literalContextAndPosition)
        => new(literalContextAndPosition & 15, literalContextAndPosition >> 4, positionAndWorkspace & 7);
}
