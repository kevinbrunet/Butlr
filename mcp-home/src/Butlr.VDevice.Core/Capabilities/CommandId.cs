namespace Butlr.VDevice.Core.Capabilities;

public readonly record struct CommandId(uint Value)
{
    public override string ToString() => $"0x{Value:X4}";
}
