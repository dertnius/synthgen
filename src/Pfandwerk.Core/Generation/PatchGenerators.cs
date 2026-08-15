using System.Globalization;
using Bogus;
using SynthGen.Core.Generation;

namespace Pfandwerk.Core.Generation;

public sealed class GeneratorException : Exception
{
    public GeneratorException(string message) : base(message) { }
}

/// <summary>
/// The only source of patched values (hard rule 1). Two maps, deliberately separate.
///
/// <para><b>Random</b> backs ephemeral and identity rules: <c>Func&lt;Faker, object&gt;</c>,
/// composing SynthGen's <see cref="FakerMap"/> and adding pfandwerk's domain entries.</para>
///
/// <para><b>Derived</b> backs derived rules: a pure function of the row's declared inputs.
/// The ephemeral signature is never widened to take the row — a generator able to read the
/// row would become order-dependent, and its output would stop being reproducible from
/// plan.json alone, which is the property freezing values at plan time exists to protect.</para>
/// </summary>
public static class PatchGenerators
{
    private static readonly string[] EnergyClasses = { "A+", "A", "B", "C", "D", "E", "F", "G" };

    private static readonly Dictionary<string, Func<Faker, object>> RandomMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["property.energyClass"] = f => f.PickRandom(EnergyClasses),
            ["property.yearBuilt"] = f => f.Random.Int(1950, 2020),
            ["property.securityId"] = f => NewSecurityId(f),
            ["security.securityId"] = f => NewSecurityId(f),
        };

    private static readonly Dictionary<string, Func<IReadOnlyDictionary<string, object?>, object>> DerivedMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["security.bathroomsFromRooms"] = inputs => BathroomsFromRooms(inputs),
        };

    /// <summary>Documented minimum per derived key, used by onMissingInput: floor.</summary>
    private static readonly Dictionary<string, object> DerivedFloors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["security.bathroomsFromRooms"] = 1,
        };

    public static bool IsDerived(string key) => DerivedMap.ContainsKey(key);

    /// <summary>Every usable `fix` key, so a rule author can check before writing YAML.</summary>
    public static IEnumerable<string> RandomKeys =>
        RandomMap.Keys.Concat(FakerMap.KnownMethods).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> DerivedKeys =>
        DerivedMap.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    public static object Random(string key, Faker faker)
    {
        if (RandomMap.TryGetValue(key, out var fn)) return fn(faker);
        // Fall through to SynthGen's curated Bogus methods so a rule can use name.firstName
        // and friends without pfandwerk re-declaring them.
        try { return FakerMap.Resolve(key)(faker); }
        catch (GenerationException) { throw Unknown(key); }
    }

    public static object Derived(string key, IReadOnlyDictionary<string, object?> inputs)
    {
        if (!DerivedMap.TryGetValue(key, out var fn)) throw Unknown(key);
        return fn(inputs);
    }

    public static object Floor(string key) =>
        DerivedFloors.TryGetValue(key, out var v) ? v
            : throw new GeneratorException($"Generator '{key}' declares no floor, so onMissingInput: floor cannot be used.");

    private static GeneratorException Unknown(string key)
    {
        var known = RandomMap.Keys.Concat(DerivedMap.Keys).Concat(FakerMap.KnownMethods)
                             .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        return new GeneratorException($"Unknown fix key '{key}'. Available: {string.Join(", ", known)}");
    }

    /// <summary>
    /// One bathroom minimum, one more per three rooms: max(1, ceil(Rooms / 3)).
    /// Mirrors the SQL in SEC-001's invariant exactly — if these two ever disagree, the
    /// invariant fails on rows this generator just wrote, which is the intended alarm.
    /// </summary>
    private static object BathroomsFromRooms(IReadOnlyDictionary<string, object?> inputs)
    {
        if (!inputs.TryGetValue("Rooms", out var raw))
            throw new GeneratorException("security.bathroomsFromRooms requires input 'Rooms'.");
        if (raw is null)
            throw new GeneratorException("security.bathroomsFromRooms: input 'Rooms' is NULL.");

        var rooms = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        return rooms < 1 ? 1 : (rooms + 2) / 3;
    }

    private static string NewSecurityId(Faker f) =>
        "DE" + f.Random.ReplaceNumbers("000") + f.Random.String2(6, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789");
}
