namespace CrimeanTom.Operations;

internal static class CampaignValidation
{
    public static void EnsureUniqueIds(IEnumerable<string> ids, string parameterName)
    {
        HashSet<string> uniqueIds = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id, parameterName);
            if (!uniqueIds.Add(id))
            {
                throw new ArgumentException($"Duplicate unit id '{id}'.", parameterName);
            }
        }
    }

    public static void ValidateUnit(CampaignUnit unit, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit.Id, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit.Name, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit.SeaPowerType, parameterName);
        EnsurePercentage(unit.Condition, nameof(unit.Condition));
        EnsurePercentage(unit.Fuel, nameof(unit.Fuel));
        EnsureNonNegative(unit.PrimaryAmmunition, nameof(unit.PrimaryAmmunition));

        if (unit.Destroyed && unit.Condition != 0)
        {
            throw new ArgumentException("A destroyed unit must have zero condition.", parameterName);
        }
    }

    public static void ValidateAfterAction(UnitAfterAction unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit.UnitId);
        EnsurePercentage(unit.Condition, nameof(unit.Condition));
        EnsurePercentage(unit.Fuel, nameof(unit.Fuel));
        EnsureNonNegative(unit.PrimaryAmmunition, nameof(unit.PrimaryAmmunition));

        if (unit.Destroyed && unit.Condition != 0)
        {
            throw new ArgumentException("A destroyed unit must have zero condition.", nameof(unit));
        }
    }

    private static void EnsurePercentage(int value, string parameterName)
    {
        if (value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be between 0 and 100.");
        }
    }

    private static void EnsureNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative.");
        }
    }
}
