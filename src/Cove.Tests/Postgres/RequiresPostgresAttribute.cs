using Xunit.v3;

namespace Cove.Tests.Postgres;

/// <summary>
/// Marks tests that need the PostgreSQL server <see cref="CovePostgresDatabase"/> connects to, so a run
/// without one can leave them out with <c>--filter "Requires!=PostgreSQL"</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresPostgresAttribute : Attribute, ITraitAttribute
{
    public const string TraitName = "Requires";
    public const string TraitValue = "PostgreSQL";

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new(TraitName, TraitValue)];
}
