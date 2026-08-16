using Microsoft.SqlServer.TransactSql.ScriptDom;
using TableModel = SynthGen.Core.Model.TableDefinition;
using ColumnModel = SynthGen.Core.Model.ColumnDefinition;

namespace SynthGen.Core.Ddl;

public sealed class DdlParseException : Exception
{
    public DdlParseException(string message) : base(message) { }
}

/// <summary>
/// Parses MSSQL CREATE TABLE DDL into <see cref="TableDefinition"/> using the official
/// T-SQL parser. TSql150Parser targets the SQL Server 2019 language surface.
/// </summary>
public static class DdlParser
{
    /// <summary>Parses all CREATE TABLE statements found in the script.</summary>
    public static List<TableModel> ParseScript(string ddl)
    {
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(ddl);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0)
        {
            var details = string.Join("; ", errors.Select(e => $"line {e.Line}, col {e.Column}: {e.Message}"));
            throw new DdlParseException($"DDL is not valid T-SQL (SQL Server 2019 grammar): {details}");
        }

        var visitor = new CreateTableVisitor(ddl);
        fragment.Accept(visitor);

        if (visitor.Tables.Count == 0)
            throw new DdlParseException("No CREATE TABLE statement found in the DDL script.");

        return visitor.Tables;
    }

    /// <summary>
    /// Parses the script and returns a single table. When the script defines several
    /// tables, <paramref name="tableName"/> selects one ("Name" or "Schema.Name").
    /// </summary>
    public static TableModel ParseSingle(string ddl, string? tableName = null)
    {
        var tables = ParseScript(ddl);
        if (tableName is null)
        {
            if (tables.Count == 1) return tables[0];
            var names = string.Join(", ", tables.Select(t => $"{t.Schema}.{t.Name}"));
            throw new DdlParseException(
                $"The script defines {tables.Count} tables ({names}). Use --table to pick one.");
        }

        var parts = tableName.Replace("[", "").Replace("]", "").Split('.');
        var (schema, name) = parts.Length == 2 ? (parts[0], parts[1]) : ((string?)null, parts[0]);

        var match = tables.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (schema is null || string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            var names = string.Join(", ", tables.Select(t => $"{t.Schema}.{t.Name}"));
            throw new DdlParseException($"Table '{tableName}' not found in script. Available: {names}");
        }
        return match;
    }

    private sealed class CreateTableVisitor : TSqlFragmentVisitor
    {
        private readonly string _source;
        public List<TableModel> Tables { get; } = new();

        public CreateTableVisitor(string source) => _source = source;

        public override void ExplicitVisit(CreateTableStatement node)
        {
            var table = new TableModel
            {
                Schema = node.SchemaObjectName.SchemaIdentifier?.Value ?? "dbo",
                Name = node.SchemaObjectName.BaseIdentifier.Value,
            };

            foreach (var col in node.Definition.ColumnDefinitions)
                table.Columns.Add(ReadColumn(col, table));

            foreach (var constraint in node.Definition.TableConstraints)
                ReadTableConstraint(constraint, table);

            // PK columns are implicitly NOT NULL.
            foreach (var pkCol in table.PrimaryKeyColumns)
            {
                var c = table.FindColumn(pkCol);
                if (c is not null)
                {
                    c.IsNullable = false;
                    c.IsPrimaryKey = true;
                }
            }

            Tables.Add(table);
        }

        private ColumnModel ReadColumn(ColumnDefinition col, TableModel table)
        {
            var def = new ColumnModel { Name = col.ColumnIdentifier.Value };

            if (col.ComputedColumnExpression is not null)
                def.IsComputed = true;

            if (col.DataType is SqlDataTypeReference sqlType)
            {
                def.SqlType = sqlType.SqlDataTypeOption.ToString().ToLowerInvariant();
                if (sqlType.SqlDataTypeOption is SqlDataTypeOption.Rowversion or SqlDataTypeOption.Timestamp)
                    def.IsRowVersion = true;

                ReadTypeParameters(sqlType, def);
            }
            else if (col.DataType is not null)
            {
                // User-defined or XML/CLR types: keep the raw name; generation requires an explicit rule.
                def.SqlType = GetText(col.DataType).ToLowerInvariant();
            }

            if (col.IdentityOptions is not null)
            {
                def.IsIdentity = true;
                if (col.IdentityOptions.IdentitySeed is IntegerLiteral seed)
                    def.IdentitySeed = long.Parse(seed.Value);
                if (col.IdentityOptions.IdentityIncrement is IntegerLiteral inc)
                    def.IdentityIncrement = long.Parse(inc.Value);
            }

            foreach (var constraint in col.Constraints)
            {
                switch (constraint)
                {
                    case NullableConstraintDefinition nullable:
                        def.IsNullable = nullable.Nullable;
                        break;
                    case UniqueConstraintDefinition unique when unique.IsPrimaryKey:
                        table.PrimaryKeyColumns.Add(def.Name);
                        break;
                    case UniqueConstraintDefinition:
                        def.HasUniqueConstraint = true;
                        break;
                    case CheckConstraintDefinition check:
                        table.CheckConstraints.Add(new Model.CheckConstraintDefinition
                        {
                            Name = check.ConstraintIdentifier?.Value,
                            Expression = GetText(check.CheckCondition),
                        });
                        break;
                    case ForeignKeyConstraintDefinition fk:
                        table.ForeignKeys.Add(new Model.ForeignKeyDefinition
                        {
                            Name = fk.ConstraintIdentifier?.Value,
                            Columns = { def.Name },
                            ReferencedTable = FormatName(fk.ReferenceTableName),
                            ReferencedColumns = fk.ReferencedTableColumns.Select(c => c.Value).ToList(),
                        });
                        break;
                }
            }

            if (col.DefaultConstraint is not null)
                def.DefaultExpression = GetText(col.DefaultConstraint.Expression);

            return def;
        }

        private void ReadTableConstraint(ConstraintDefinition constraint, TableModel table)
        {
            switch (constraint)
            {
                case UniqueConstraintDefinition unique when unique.IsPrimaryKey:
                    table.PrimaryKeyColumns.AddRange(
                        unique.Columns.Select(c => c.Column.MultiPartIdentifier.Identifiers[^1].Value));
                    break;
                case UniqueConstraintDefinition unique:
                    // Multi-column uniques are not jointly enforced (documented limit);
                    // only a single-column constraint marks its column unique.
                    if (unique.Columns.Count == 1)
                    {
                        var c = table.FindColumn(
                            unique.Columns[0].Column.MultiPartIdentifier.Identifiers[^1].Value);
                        if (c is not null) c.HasUniqueConstraint = true;
                    }
                    break;
                case ForeignKeyConstraintDefinition fk:
                    table.ForeignKeys.Add(new Model.ForeignKeyDefinition
                    {
                        Name = fk.ConstraintIdentifier?.Value,
                        Columns = fk.Columns.Select(c => c.Value).ToList(),
                        ReferencedTable = FormatName(fk.ReferenceTableName),
                        ReferencedColumns = fk.ReferencedTableColumns.Select(c => c.Value).ToList(),
                    });
                    break;
                case CheckConstraintDefinition check:
                    table.CheckConstraints.Add(new Model.CheckConstraintDefinition
                    {
                        Name = check.ConstraintIdentifier?.Value,
                        Expression = GetText(check.CheckCondition),
                    });
                    break;
            }
        }

        private static void ReadTypeParameters(SqlDataTypeReference sqlType, ColumnModel def)
        {
            var parameters = sqlType.Parameters;
            if (parameters.Count == 0) return;

            bool isMax = parameters[0] is MaxLiteral;
            switch (sqlType.SqlDataTypeOption)
            {
                case SqlDataTypeOption.Char or SqlDataTypeOption.VarChar
                    or SqlDataTypeOption.NChar or SqlDataTypeOption.NVarChar
                    or SqlDataTypeOption.Binary or SqlDataTypeOption.VarBinary:
                    def.Length = isMax ? -1 : int.Parse(parameters[0].Value);
                    break;
                case SqlDataTypeOption.Decimal or SqlDataTypeOption.Numeric:
                    def.Precision = int.Parse(parameters[0].Value);
                    def.Scale = parameters.Count > 1 ? int.Parse(parameters[1].Value) : 0;
                    break;
                case SqlDataTypeOption.DateTime2 or SqlDataTypeOption.DateTimeOffset or SqlDataTypeOption.Time:
                    def.Scale = int.Parse(parameters[0].Value);
                    break;
                case SqlDataTypeOption.Float:
                    def.Precision = int.Parse(parameters[0].Value);
                    break;
            }
        }

        private static string FormatName(SchemaObjectName name)
        {
            var schema = name.SchemaIdentifier?.Value;
            var baseName = name.BaseIdentifier.Value;
            return schema is null ? $"[dbo].[{baseName}]" : $"[{schema}].[{baseName}]";
        }

        private string GetText(TSqlFragment fragment)
        {
            var tokens = fragment.ScriptTokenStream
                .Skip(fragment.FirstTokenIndex)
                .Take(fragment.LastTokenIndex - fragment.FirstTokenIndex + 1)
                .Select(t => t.Text);
            return string.Join("", tokens).Trim();
        }
    }
}
