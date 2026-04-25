namespace Butlr.VDevice.Core.Capabilities.Clusters;

// Matter spec §4.3 — Thermostat Cluster (0x0201)
// Valeurs en centidegrés Celsius (ex. 2100 = 21.00 °C)
public static class ThermostatCluster
{
    public static readonly ClusterId Id = ClusterId.Thermostat;

    // ~ Plages standard Matter — à confirmer sur le device réel
    public const short MinHeatSetpoint = 700;   // 7 °C
    public const short MaxHeatSetpoint = 3000;  // 30 °C

    public static class Attributes
    {
        // int16, centidegrés Celsius
        public static readonly AttributeId LocalTemperature = new(0x0000);
        public static readonly AttributeId OccupiedHeatingSetpoint = new(0x0012);
        public static readonly AttributeId OccupiedCoolingSetpoint = new(0x0011);
        public static readonly AttributeId SystemMode = new(0x001C);
    }
}
