using SynthGen.Core.Model;

namespace SynthGen.Core.Rules;

/// <summary>
/// Derives a sensible default <see cref="ColumnRule"/> from the DDL alone. Used by
/// "init" to scaffold the rules file and at generation time for columns without a rule.
/// </summary>
public static class ColumnInference
{
    public static ColumnRule Infer(ColumnDefinition col, TableDefinition table,
                                   DatasetStore? datasets = null)
    {
        // The database owns these values.
        if (col.IsIdentity || col.IsDbGenerated)
            return new ColumnRule { Strategy = "skip" };

        // A declared DEFAULT is usually the intended test value; let the DB apply it.
        if (col.DefaultExpression is not null && col.IsNullable == false && !col.IsPrimaryKey)
            return new ColumnRule { Strategy = "dbDefault" };

        // Single-column FK: pull real values from the referenced table.
        var fk = table.ForeignKeys.FirstOrDefault(f =>
            f.Columns.Count == 1 &&
            string.Equals(f.Columns[0], col.Name, StringComparison.OrdinalIgnoreCase));
        if (fk is not null)
        {
            var refCol = fk.ReferencedColumns.Count == 1 ? fk.ReferencedColumns[0] : col.Name;
            return WithNullRate(col, new ColumnRule
            {
                Strategy = "query",
                Query = $"SELECT [{refCol}] FROM {fk.ReferencedTable}",
            });
        }

        // Non-identity primary keys must be generated and unique.
        if (col.IsPrimaryKey && table.PrimaryKeyColumns.Count == 1)
        {
            return col.SqlType switch
            {
                "uniqueidentifier" => new ColumnRule { Strategy = "guid" },
                "tinyint" or "smallint" or "int" or "bigint" =>
                    new ColumnRule { Strategy = "sequence", Start = 1, Step = 1 },
                // A "ID-{row}" template would truncate into duplicates on short columns;
                // random strings at the full declared length stay unique.
                _ when col.Length is > 0 and < 8 =>
                    new ColumnRule { Strategy = "string", Length = col.Length, Unique = true },
                _ => new ColumnRule { Strategy = "template", Template = "ID-{row}", Unique = true },
            };
        }

        var rule = InferByDataset(col, datasets) ?? InferByNameOrType(col);
        if (col.HasUniqueConstraint)
            rule.Unique = true;
        return WithNullRate(col, rule);
    }

    /// <summary>
    /// The dataset name-match hook (D-A2): a dataset whose 'match' globs cover this
    /// column's name supplies its values. Reviewed vocabularies beat generic fakers, so
    /// this runs before the faker-by-name heuristics.
    /// </summary>
    private static ColumnRule? InferByDataset(ColumnDefinition col, DatasetStore? datasets)
    {
        if (datasets is null) return null;
        foreach (var dataset in datasets.All)
        {
            if (dataset.MatchColumn(col.Name) is { } datasetColumn)
                return new ColumnRule
                {
                    Strategy = "dataset",
                    Dataset = dataset.Name,
                    DatasetColumn = datasetColumn,
                };
        }
        return null;
    }

    private static ColumnRule WithNullRate(ColumnDefinition col, ColumnRule rule)
    {
        if (col.IsNullable && rule.Strategy is not "skip" and not "dbDefault")
            rule.NullRate = 0.1;
        return rule;
    }

    private static ColumnRule InferByNameOrType(ColumnDefinition col)
    {
        if (IsTextType(col.SqlType))
        {
            var byName = InferFakerByName(col.Name);
            if (byName is not null)
                return new ColumnRule { Strategy = "faker", Method = byName };
        }

        return col.SqlType switch
        {
            "bit" => new ColumnRule { Strategy = "bool", TrueRate = 0.5 },
            "tinyint" => new ColumnRule { Strategy = "int", Min = "0", Max = "255" },
            "smallint" => new ColumnRule { Strategy = "int", Min = "0", Max = "32767" },
            "int" => new ColumnRule { Strategy = "int", Min = "1", Max = "100000" },
            "bigint" => new ColumnRule { Strategy = "int", Min = "1", Max = "10000000" },
            "decimal" or "numeric" => DecimalRule(col),
            "money" or "smallmoney" => new ColumnRule { Strategy = "decimal", Min = "0", Max = "10000" },
            "float" or "real" => new ColumnRule { Strategy = "decimal", Min = "0", Max = "1000" },
            "date" => new ColumnRule { Strategy = "date", Min = "2020-01-01", Max = "2025-12-31" },
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" =>
                new ColumnRule { Strategy = "datetime", Min = "2020-01-01", Max = "2025-12-31" },
            "time" => new ColumnRule { Strategy = "time" },
            "uniqueidentifier" => new ColumnRule { Strategy = "guid" },
            "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" =>
                new ColumnRule { Strategy = "string", Length = CapLength(col.Length, 20) },
            "binary" or "varbinary" or "image" =>
                new ColumnRule { Strategy = "bytes", Length = CapLength(col.Length, 16) },
            "xml" => new ColumnRule { Strategy = "constant", Value = "<data />" },
            _ => new ColumnRule { Strategy = "string", Length = 10 },
        };
    }

    private static ColumnRule DecimalRule(ColumnDefinition col)
    {
        int precision = col.Precision ?? 18;
        int scale = col.Scale ?? 0;
        // Keep the magnitude comfortably inside precision - scale integral digits.
        int intDigits = Math.Max(1, Math.Min(precision - scale, 6));
        decimal max = (decimal)Math.Pow(10, intDigits) - 1;
        return new ColumnRule { Strategy = "decimal", Min = "0", Max = max.ToString() };
    }

    private static int CapLength(int? declared, int cap)
    {
        if (declared is null or -1) return cap;
        return Math.Min(declared.Value, cap);
    }

    private static bool IsTextType(string sqlType) =>
        sqlType is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext";

    private static string? InferFakerByName(string columnName)
    {
        var n = columnName.ToLowerInvariant().Replace("_", "");
        return n switch
        {
            _ when n.Contains("email") => "internet.email",
            _ when n.Contains("firstname") || n.Contains("givenname") => "name.firstName",
            _ when n.Contains("lastname") || n.Contains("surname") => "name.lastName",
            _ when n.Contains("fullname") || n == "name" || n.Contains("contactname") => "name.fullName",
            _ when n.Contains("username") || n.Contains("login") => "internet.userName",
            _ when n.Contains("phone") || n.Contains("mobile") || n.Contains("fax") => "phone.phoneNumber",
            _ when n.Contains("city") => "address.city",
            _ when n.Contains("country") => "address.country",
            _ when n.Contains("state") || n.Contains("province") => "address.state",
            _ when n.Contains("zip") || n.Contains("postalcode") || n.Contains("postcode") => "address.zipCode",
            _ when n.Contains("street") || n.Contains("address") => "address.streetAddress",
            _ when n.Contains("company") || n.Contains("organization") || n.Contains("employer") => "company.companyName",
            _ when n.Contains("job") && n.Contains("title") => "name.jobTitle",
            _ when n.Contains("url") || n.Contains("website") || n.Contains("homepage") => "internet.url",
            _ when n.Contains("description") || n.Contains("comment") || n.Contains("notes") || n.Contains("summary")
                => "lorem.sentence",
            _ when n.Contains("product") => "commerce.productName",
            _ when n.Contains("department") => "commerce.department",
            _ when n.Contains("color") || n.Contains("colour") => "commerce.color",
            _ when n.Contains("iban") => "finance.iban",
            _ when n.Contains("currency") => "finance.currencyCode",
            _ => null,
        };
    }
}
