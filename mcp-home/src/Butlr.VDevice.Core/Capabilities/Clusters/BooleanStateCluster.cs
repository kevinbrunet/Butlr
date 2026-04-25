namespace Butlr.VDevice.Core.Capabilities.Clusters;

// Matter spec §1.7 — Boolean State Cluster (0x0045)
public static class BooleanStateCluster
{
    public static readonly ClusterId Id = ClusterId.BooleanState;

    public static class Attributes
    {
        // bool — true = contact fermé (fenêtre/porte fermée selon convention Z2M)
        public static readonly AttributeId StateValue = new(0x0000);
    }
}
