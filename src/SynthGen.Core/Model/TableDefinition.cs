namespace SynthGen.Core.Model;

/// <summary>Parsed representation of a single CREATE TABLE statement.</summary>
public sealed class TableDefinition
{
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = "";
    public List<ColumnDefinition> Columns { get; set; } = new();
    public List<string> PrimaryKeyColumns { get; set; } = new();
    public List<UniqueConstraintDefinition> UniqueConstraints { get; set; } = new();
    public List<ForeignKeyDefinition> ForeignKeys { get; set; } = new();
    public List<CheckConstraintDefinition> CheckConstraints { get; set; } = new();

    public string QualifiedName => $"[{Schema}].[{Name}]";

    public ColumnDefinition? FindColumn(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class ColumnDefinition
{
    public string Name { get; set; } = "";
    /// <summary>Lower-case base type name, e.g. "nvarchar", "int", "decimal".</summary>
    public string SqlType { get; set; } = "";
    /// <summary>Length for (n)char/(n)varchar/binary/varbinary; -1 means MAX.</summary>
    public int? Length { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool IsNullable { get; set; } = true;
    public bool IsIdentity { get; set; }
    public long IdentitySeed { get; set; } = 1;
    public long IdentityIncrement { get; set; } = 1;
    public bool IsComputed { get; set; }
    public bool IsRowVersion { get; set; }
    public string? DefaultExpression { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool HasUniqueConstraint { get; set; }

    /// <summary>Columns the generator can never write to (DB owns the value).</summary>
    public bool IsDbGenerated => IsComputed || IsRowVersion;
}

public sealed class UniqueConstraintDefinition
{
    public string? Name { get; set; }
    public List<string> Columns { get; set; } = new();
}

public sealed class ForeignKeyDefinition
{
    public string? Name { get; set; }
    public List<string> Columns { get; set; } = new();
    public string ReferencedTable { get; set; } = "";
    public List<string> ReferencedColumns { get; set; } = new();
}

public sealed class CheckConstraintDefinition
{
    public string? Name { get; set; }
    public string Expression { get; set; } = "";
}
