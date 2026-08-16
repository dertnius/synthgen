using System.Text.RegularExpressions;
using Bogus;

namespace SynthGen.Core.Rules;

/// <summary>YAML shape of one rules/datasets/&lt;name&gt;.yaml file.</summary>
public sealed class DatasetFile
{
    /// <summary>Column names; every row carries one value per column, in this order.</summary>
    public List<string> Columns { get; set; } = new();

    /// <summary>Rows of correlated values, e.g. a currency code and its name.</summary>
    public List<List<string>> Rows { get; set; } = new();

    /// <summary>Optional relative pick weights, one per row.</summary>
    public List<double>? Weights { get; set; }

    /// <summary>
    /// Optional inference hook: dataset column -> database-column-name globs (e.g.
    /// "*currency*"). A DB column matching a glob is generated from this dataset.
    /// </summary>
    public Dictionary<string, List<string>>? Match { get; set; }
}

/// <summary>
/// One reviewed vocabulary: named, validated rows of correlated columns. The same file
/// serves bulk generation (the "dataset" strategy) and the patch pipeline
/// ("dataset.&lt;name&gt;" fix keys), so one review covers both consumers.
/// </summary>
public sealed class Dataset
{
    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
    public IReadOnlyList<double>? Weights { get; }
    public IReadOnlyDictionary<string, List<string>> Match { get; }

    public Dataset(string name, DatasetFile file)
    {
        if (name.Contains('.'))
            throw new RulesLoadException(
                $"Dataset '{name}': names may not contain '.' — it separates the parts of a fix key.");
        Name = name;

        if (file.Columns.Count == 0)
            throw new RulesLoadException($"Dataset '{name}': 'columns' is required.");
        if (file.Columns.Any(string.IsNullOrWhiteSpace))
            throw new RulesLoadException($"Dataset '{name}': column names must not be blank.");
        if (file.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Columns.Count)
            throw new RulesLoadException($"Dataset '{name}': column names must be distinct.");
        if (file.Rows.Count == 0)
            throw new RulesLoadException($"Dataset '{name}': 'rows' is required.");

        var bad = file.Rows.FindIndex(r => r is null || r.Count != file.Columns.Count);
        if (bad >= 0)
            throw new RulesLoadException(
                $"Dataset '{name}': row {bad + 1} has {file.Rows[bad]?.Count ?? 0} values " +
                $"but {file.Columns.Count} columns are declared.");

        if (file.Weights is not null)
        {
            if (file.Weights.Count != file.Rows.Count)
                throw new RulesLoadException(
                    $"Dataset '{name}': {file.Weights.Count} weights for {file.Rows.Count} rows.");
            if (file.Weights.Any(w => w <= 0))
                throw new RulesLoadException($"Dataset '{name}': weights must be positive.");
        }

        foreach (var key in (file.Match ?? new()).Keys)
        {
            if (!file.Columns.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new RulesLoadException(
                    $"Dataset '{name}': 'match' names column '{key}', which is not declared in 'columns'.");
        }

        Columns = file.Columns;
        Rows = file.Rows;
        Weights = file.Weights;
        Match = file.Match ?? new Dictionary<string, List<string>>();
    }

    public int ColumnIndex(string column) =>
        Columns.ToList().FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>A weighted (or uniform) random row index from the run's seeded stream.</summary>
    public int PickRow(Faker faker)
    {
        if (Weights is null) return faker.Random.Int(0, Rows.Count - 1);
        var total = Weights.Sum();
        var indices = Enumerable.Range(0, Rows.Count).ToArray();
        var normalized = Weights.Select(w => (float)(w / total)).ToArray();
        return faker.Random.WeightedRandom(indices, normalized);
    }

    /// <summary>
    /// The single row whose values equal every given input, or -1. Rule validation
    /// guarantees the input columns key the rows uniquely, so first match is the match.
    /// </summary>
    public int FindRow(IReadOnlyDictionary<string, string> inputs)
    {
        var lookups = inputs.Select(kv => (Index: ColumnIndex(kv.Key), kv.Value)).ToList();
        for (int i = 0; i < Rows.Count; i++)
        {
            if (lookups.All(l => string.Equals(Rows[i][l.Index], l.Value, StringComparison.OrdinalIgnoreCase)))
                return i;
        }
        return -1;
    }

    /// <summary>Whether the value tuples over these columns are distinct across all rows.</summary>
    public bool ColumnsKeyRowsUniquely(IEnumerable<string> columns)
    {
        var indices = columns.Select(ColumnIndex).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Rows.All(r => seen.Add(string.Join("", indices.Select(i => r[i]))));
    }

    /// <summary>The dataset column whose 'match' globs cover this DB column name, or null.</summary>
    public string? MatchColumn(string dbColumnName)
    {
        var normalized = dbColumnName.ToLowerInvariant().Replace("_", "");
        foreach (var (column, globs) in Match.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (globs.Any(g => GlobMatches(g, normalized))) return column;
        }
        return null;
    }

    private static bool GlobMatches(string glob, string text) =>
        Regex.IsMatch(text,
            "^" + Regex.Escape(glob.ToLowerInvariant()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
}

/// <summary>
/// Every dataset next to a rules file: &lt;rules-dir&gt;/datasets/*.yaml, named by file
/// name. Loading validates each file the way rule files are validated — a broken
/// vocabulary fails at load, not when a row first happens to need it.
/// </summary>
public sealed class DatasetStore
{
    public const string KeyPrefix = "dataset.";

    public static DatasetStore Empty { get; } = new(new Dictionary<string, Dataset>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, Dataset> _byName;

    private DatasetStore(Dictionary<string, Dataset> byName) => _byName = byName;

    public static DatasetStore LoadDirectory(string? directory)
    {
        if (directory is null || !Directory.Exists(directory)) return Empty;

        var byName = new Dictionary<string, Dataset>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.yaml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var file = RulesLoader.Deserialize<DatasetFile>(RulesLoader.ReadFile(path));
            byName[name] = new Dataset(name, file);
        }
        return new DatasetStore(byName);
    }

    /// <summary>The store for a rules file: its sibling datasets/ directory, or empty.</summary>
    public static DatasetStore ForRulesFile(string rulesPath) =>
        LoadDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulesPath)) ?? ".", "datasets"));

    public IEnumerable<Dataset> All => _byName.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> Names => All.Select(d => d.Name);

    public bool TryGet(string name, out Dataset dataset) => _byName.TryGetValue(name, out dataset!);

    public static bool IsDatasetKey(string fix) =>
        fix.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves "dataset.&lt;name&gt;" (column taken from the rule's patched column) or
    /// "dataset.&lt;name&gt;.&lt;column&gt;" to a dataset and the column it emits.
    /// </summary>
    public (Dataset Dataset, int ColumnIndex) ResolveFixKey(string fix, string targetColumn)
    {
        var rest = fix[KeyPrefix.Length..];
        var dot = rest.IndexOf('.');
        var (name, column) = dot < 0 ? (rest, targetColumn) : (rest[..dot], rest[(dot + 1)..]);

        if (!TryGet(name, out var dataset))
            throw new RulesLoadException(
                $"Unknown dataset '{name}' in fix key '{fix}'. " +
                (Names.Any() ? $"Available: {string.Join(", ", Names)}."
                             : "No datasets/ directory exists next to the rules file."));

        var index = dataset.ColumnIndex(column);
        if (index < 0)
            throw new RulesLoadException(
                $"Fix key '{fix}' needs column '{column}', but dataset '{name}' has: " +
                $"{string.Join(", ", dataset.Columns)}.");
        return (dataset, index);
    }
}
