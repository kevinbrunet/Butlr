namespace Butlr.VDevice.Core.Capabilities.Clusters;

// Matter spec §1.6 — Level Control Cluster (0x0008)
public static class LevelControlCluster
{
    public static readonly ClusterId Id = ClusterId.LevelControl;

    public const byte MinLevel = 0;
    public const byte MaxLevel = 254;

    public static class Attributes
    {
        // uint8 [0, 254]
        public static readonly AttributeId CurrentLevel = new(0x0000);
    }

    public static class Commands
    {
        public static readonly CommandId MoveToLevel = new(0x00);
        public static readonly CommandId MoveToLevelWithOnOff = new(0x04);
    }
}
