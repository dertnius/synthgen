using System.Text;
using SynthGen.Core.Model;

namespace SynthGen.Sqlite;

/// <summary>
/// Materializes a parsed MSSQL table as a SQLite table so rules can be smoke-tested
/// fully locally. The table keeps its [schema].[name] spelling: the connection factory
/// attaches a companion database under the schema's name, so MSSQL-style queries from
/// the rules file run verbatim. Computed and rowversion columns are omitted (SQLite has
/// no equivalent and the generator never inserts them).
/// </summary>
public static class SqliteSchemaBuilder
{
    public static string BuildCreateTable(TableDefinition table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE IF NOT EXISTS [{table.Schema}].[{table.Name}] (");

        var columns = table.Columns.Where(c => !c.IsDbGenerated).ToList();
        var lines = new List<string>();

        bool integerPkInline = false;
        foreach (var col in columns)
        {
            var line = $"    [{col.Name}] {Affinity(col)}";

            // An identity int PK maps to INTEGER PRIMARY KEY (rowid alias): omitted on
            // insert, auto-assigned by SQLite — same observable behavior as IDENTITY.
            if (col.IsPrimaryKey && table.PrimaryKeyColumns.Count == 1 && Affinity(col) == "INTEGER")
            {
                line += " PRIMARY KEY";
                integerPkInline = true;
            }
            else if (!col.IsNullable)
            {
                line += " NOT NULL";
            }

            if (col.HasUniqueConstraint)
                line += " UNIQUE";

            lines.Add(line);
        }

        if (!integerPkInline && table.PrimaryKeyColumns.Count > 0)
        {
            var pkCols = string.Join(", ", table.PrimaryKeyColumns.Select(c => $"[{c}]"));
            lines.Add($"    PRIMARY KEY ({pkCols})");
        }

        sb.AppendLine(string.Join(",\n", lines));
        sb.Append(')');
        return sb.ToString();
    }

    private static string Affinity(ColumnDefinition col) => col.SqlType switch
    {
        "bit" or "tinyint" or "smallint" or "int" or "bigint" => "INTEGER",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "REAL",
        "binary" or "varbinary" or "image" => "BLOB",
        _ => "TEXT",
    };
}
