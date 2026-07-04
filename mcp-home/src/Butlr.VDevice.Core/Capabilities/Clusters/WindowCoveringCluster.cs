namespace Butlr.VDevice.Core.Capabilities.Clusters;

// Matter spec §5.3 — Window Covering Cluster (0x0102)
public static class WindowCoveringCluster
{
    public static readonly ClusterId Id = ClusterId.WindowCovering;

    public static class Attributes
    {
        // uint8 [0, 100], pourcentage ouverture
        public static readonly AttributeId CurrentPositionLiftPercentage = new(0x0008);
        public static readonly AttributeId CurrentPositionTiltPercentage = new(0x0009);
    }

    public static class Commands
    {
        public static readonly CommandId UpOrOpen = new(0x00);
        public static readonly CommandId DownOrClose = new(0x01);
        public static readonly CommandId Stop = new(0x02);
        public static readonly CommandId GoToLiftPercentage = new(0x05);
    }
}
