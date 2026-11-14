using Newtonsoft.Json;
using SeaPower;

namespace Quickload;

internal class UnitLoadout
{
    [JsonProperty(Required = Required.Always)] public string Name = "";
    [JsonProperty(Required = Required.Always)] public Dictionary<string, int> Amounts = [];
    [JsonProperty(Required = Required.Always)] public Dictionary<string, int> Accountable = [];
    [JsonProperty(Required = Required.Always)] public Dictionary<string, List<(int Year, string WeaponRef)>> Alternatives = [];
    public int Year;
    public bool Civilian;
    public float Points;
    public float ReadyUp;
    public float CoolDown;
    public ObjectBase.LoadoutCapabilities Capabilities;

    public UnitLoadout() { }

    internal UnitLoadout(Loadout source)
    {
        Name = source.Name;
        Amounts = source.AmmunitionAmounts;
        Accountable = source._accountableAmmunition;
        Alternatives = source.DateBasedAlternatives;
        Year = source.ServiceEntryYear;
        Civilian = source._isCivilian;
        Points = source._ammoPoints;
        ReadyUp = source._readyUpDuration;
        CoolDown = source._coolDownTime;
        Capabilities = source._loadoutCapabilities;
    }

    internal Loadout Restore(IniHandler ini) => new(Name)
    {
        OwnerIni = ini,
        ServiceEntryYear = Year,
        _isCivilian = Civilian,
        _ammoPoints = Points,
        _readyUpDuration = ReadyUp,
        _coolDownTime = CoolDown,
        _loadoutCapabilities = Capabilities,
        AmmunitionAmounts = Amounts,
        _accountableAmmunition = Accountable,
        DateBasedAlternatives = Alternatives,
    };
}
