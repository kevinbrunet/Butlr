namespace Butlr.VDevice.Core.Capabilities.Clusters;

// Matter spec §2.7 — Occupancy Sensing Cluster (0x0406)
public static class OccupancySensingCluster
{
    public static readonly ClusterId Id = ClusterId.OccupancySensing;

    public static class Attributes
    {
        // bitmap8 — bit 0 = occupancy detected
        public static readonly AttributeId Occupancy = new(0x0000);
    }
}
