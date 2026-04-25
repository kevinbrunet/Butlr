namespace Butlr.VDevice.Core.Capabilities;

public readonly record struct AttributeId(uint Value)
{
    public override string ToString() => $"0x{Value:X4}";
}
