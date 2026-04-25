namespace Butlr.VDevice.Core;

public readonly record struct VDeviceId(string Value)
{
    public static VDeviceId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}
