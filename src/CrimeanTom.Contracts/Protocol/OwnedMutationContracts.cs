namespace CrimeanTom.Protocol;

public static class OwnedMutationContracts
{
    public const int MaximumScopedIdentifierLength = 128;
    public const string AssignmentPrefix = "assignment:";
    public const string GameEntityPrefix = "unit:crimean-tom:";

    public static bool IsValidScopedIdentifier(string? value, string prefix) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumScopedIdentifierLength
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.All(character => char.IsLetterOrDigit(character)
            || character is ':' or '-' or '_' or '.');

    public static bool IsValidIdempotencyKey(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumScopedIdentifierLength;

    public static string CanonicalEntityId(string entityId)
    {
        if (entityId.StartsWith(GameEntityPrefix, StringComparison.Ordinal))
        {
            return entityId[GameEntityPrefix.Length..];
        }

        return entityId;
    }

    public static string ResolveActivationIdempotencyKey(string migrationOrStageKey, string entityId)
    {
        if (string.IsNullOrWhiteSpace(migrationOrStageKey))
        {
            throw new InvalidDataException("A migration or staging idempotency key is required.");
        }

        var canonicalEntityId = CanonicalEntityId(entityId);
        if (string.IsNullOrWhiteSpace(canonicalEntityId))
        {
            throw new InvalidDataException("A canonical entity identifier is required.");
        }

        var activateSuffix = $"-{canonicalEntityId}-activate";
        var stageSuffix = $"-{canonicalEntityId}-stage";
        if (migrationOrStageKey.EndsWith(activateSuffix, StringComparison.Ordinal))
        {
            if (IsValidIdempotencyKey(migrationOrStageKey))
            {
                return migrationOrStageKey;
            }

            var legacyStageActivateSuffix = $"{stageSuffix}{activateSuffix}";
            if (migrationOrStageKey.EndsWith(legacyStageActivateSuffix, StringComparison.Ordinal))
            {
                var normalized = migrationOrStageKey[..^legacyStageActivateSuffix.Length] + activateSuffix;
                if (IsValidIdempotencyKey(normalized))
                {
                    return normalized;
                }
            }

            throw new InvalidDataException(
                $"The resolved activation idempotency key exceeds {MaximumScopedIdentifierLength} characters.");
        }

        if (migrationOrStageKey.EndsWith(stageSuffix, StringComparison.Ordinal))
        {
            var resolvedFromStage = migrationOrStageKey[..^stageSuffix.Length] + activateSuffix;
            if (!IsValidIdempotencyKey(resolvedFromStage))
            {
                throw new InvalidDataException(
                    $"The resolved activation idempotency key exceeds {MaximumScopedIdentifierLength} characters.");
            }

            return resolvedFromStage;
        }

        var resolved = $"{migrationOrStageKey}{activateSuffix}";
        if (!IsValidIdempotencyKey(resolved))
        {
            throw new InvalidDataException(
                $"The resolved activation idempotency key exceeds {MaximumScopedIdentifierLength} characters.");
        }

        return resolved;
    }

    public static void ValidateOwnedLifecycleIdentifiers(
        OwnedMutationContextPayload? context,
        string entityId)
    {
        if (context is null)
        {
            throw new InvalidDataException("The owned mutation context is required.");
        }

        if (!IsValidScopedIdentifier(context.AssignmentId, AssignmentPrefix)
            || !IsValidScopedIdentifier(entityId, GameEntityPrefix)
            || !IsValidIdempotencyKey(context.IdempotencyKey)
            || context.AuthorityEpoch < 1
            || context.ExpectedRevision < 0)
        {
            throw new InvalidDataException(
                "Owned entity, assignment, authority epoch, revision, and idempotency identifiers are required.");
        }
    }

    public static SetOwnedSurfaceSuspensionRequestPayload BuildSetOwnedSurfaceSuspensionRequest(
        OwnedMutationContextPayload context,
        string entityId,
        bool isSuspended) =>
        new()
        {
            Context = context,
            EntityId = entityId,
            IsSuspended = isSuspended,
        };
}
