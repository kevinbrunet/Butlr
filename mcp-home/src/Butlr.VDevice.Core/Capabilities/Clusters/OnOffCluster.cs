namespace Butlr.VDevice.Core.Capabilities.Clusters;

// Matter spec §1.5 — OnOff Cluster (0x0006)
public static class OnOffCluster
{
    public static readonly ClusterId Id = ClusterId.OnOff;

    public static class Attributes
    {
        // bool — true=on, false=off
        public static readonly AttributeId OnOff = new(0x0000);
    }

    public static class Commands
    {
        public static readonly CommandId Off = new(0x00);
        public static readonly CommandId On = new(0x01);
        public static readonly CommandId Toggle = new(0x02);
    }
}
