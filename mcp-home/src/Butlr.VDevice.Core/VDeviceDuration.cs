namespace Butlr.VDevice.Core;

public abstract record VDeviceDuration
{
    public sealed record Ttl(int Ms) : VDeviceDuration
    {
        public Ttl() : this(0) { }
    }
    public sealed record Persistent : VDeviceDuration;

    private VDeviceDuration() { }
}
