namespace Butlr.VDevice.Core.Capabilities;

// https://csa-iot.org/developer-resource/specifications-download-request/ (Matter spec, cluster IDs)
public readonly record struct ClusterId(uint Value)
{
    // Clusters Matter standards utilisés dans Butlr
    public static readonly ClusterId OnOff = new(0x0006);
    public static readonly ClusterId LevelControl = new(0x0008);
    public static readonly ClusterId ColorControl = new(0x0300);
    public static readonly ClusterId Thermostat = new(0x0201);
    public static readonly ClusterId WindowCovering = new(0x0102);
    public static readonly ClusterId OccupancySensing = new(0x0406);
    public static readonly ClusterId BooleanState = new(0x0045);

    public override string ToString() => $"0x{Value:X4}";
}
