using System.Runtime.CompilerServices;
using System.Text;
using System.Globalization;
using SeaPower;

namespace UNREP;

internal class SupplyPolicy
{
    private static readonly ConditionalWeakTable<Vessel, SupplyPolicy> Policies = new();
    private readonly Dictionary<string, double> minimums = [];
    internal bool Unrep { get; private set; }
    internal bool Vertrep { get; private set; }
    internal static SupplyPolicy For(Vessel ship) => Policies.GetValue(ship, _ => new());
    internal double Minimum(string key) => minimums.GetValueOrDefault(key);
    internal void SetMinimum(string key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1e9) return;
        minimums[key] = value;
    }
    internal void Enable(Vessel ship, bool airlift, bool enabled)
    {
        if (airlift) Vertrep = enabled; else Unrep = enabled;
        if (enabled) return;
        if (airlift) VertrepOperation.For(ship).Cancel();
        else SupplyTransfers.Jobs.Where(job => job.Source == ship).ToList().ForEach(job => job.Cancel());
    }
    internal static double Available(Vessel ship, SupplyStock stock)
    {
        double total = SupplyInventory.Read(ship).Where(item => item.Key == stock.Key && item.Transferable).Sum(item => item.Current);
        return Math.Min(stock.Withdrawable, Math.Max(0, total - For(ship).Minimum(stock.Key)));
    }
    // INI keys cannot contain the '=' padding used by standard Base64.
    private static string Encode(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=');
    private static string Decode(string key) => Encoding.UTF8.GetString(
        Convert.FromBase64String(key.PadRight((key.Length + 3) / 4 * 4, '=')));
    internal void Save(IniHandler ini, string section)
    {
        ini.writeValue(section, "UNREP_Enabled", Unrep);
        ini.writeValue(section, "VERTREP_Enabled", Vertrep);
        ini.writeValue(section, "UNREP_MinimumKeys", string.Join("|", minimums.Keys.Select(Encode)));
        foreach (var pair in minimums) ini.writeValue(section, "UNREP_Min_" + Encode(pair.Key), pair.Value.ToString("R", CultureInfo.InvariantCulture));
    }
    internal void Load(IniHandler ini, string section)
    {
        Unrep = ini.readValue(section, "UNREP_Enabled", false);
        Vertrep = ini.readValue(section, "VERTREP_Enabled", false);
        minimums.Clear();
        foreach (string encoded in ini.readValue(section, "UNREP_MinimumKeys", "").Split('|').Where(key => key.Length > 0))
        {
            try
            {
                if (double.TryParse(ini.readValue(section, "UNREP_Min_" + encoded, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    SetMinimum(Decode(encoded), value);
            }
            catch (FormatException) { /* Ignore malformed saved keys. */ }
        }
    }
}
