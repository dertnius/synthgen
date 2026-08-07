using SynthGen.Core.Ddl;

namespace SynthGen.Tests;

public class DdlParserTests
{
    private const string CustomersDdl = """
        CREATE TABLE [dbo].[Customers]
        (
            [CustomerId]    INT             IDENTITY(1,1) NOT NULL,
            [CustomerCode]  VARCHAR(20)     NOT NULL,
            [Email]         NVARCHAR(120)   NOT NULL,
            [BirthDate]     DATE            NULL,
            [CreditLimit]   DECIMAL(12,2)   NOT NULL,
            [IsActive]      BIT             NOT NULL CONSTRAINT DF_IsActive DEFAULT (1),
            [CountryCode]   CHAR(2)         NOT NULL,
            [CreatedAt]     DATETIME2(3)    NOT NULL DEFAULT (SYSUTCDATETIME()),
            [Notes]         NVARCHAR(MAX)   NULL,
            [FullName]      AS ([CustomerCode] + 'x'),
            [RowVer]        ROWVERSION      NOT NULL,
            CONSTRAINT [PK_Customers] PRIMARY KEY ([CustomerId]),
            CONSTRAINT [UQ_Email] UNIQUE ([Email]),
            CONSTRAINT [FK_Country] FOREIGN KEY ([CountryCode]) REFERENCES [dbo].[Countries] ([CountryCode]),
            CONSTRAINT [CK_Credit] CHECK ([CreditLimit] >= 0)
        );
        """;

    [Fact]
    public void Parses_columns_with_types_and_modifiers()
    {
        var table = DdlParser.ParseSingle(CustomersDdl);

        Assert.Equal("dbo", table.Schema);
        Assert.Equal("Customers", table.Name);

        var id = table.FindColumn("CustomerId")!;
        Assert.True(id.IsIdentity);
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.IsNullable);
        Assert.Equal("int", id.SqlType);

        var code = table.FindColumn("CustomerCode")!;
        Assert.Equal("varchar", code.SqlType);
        Assert.Equal(20, code.Length);

        var credit = table.FindColumn("CreditLimit")!;
        Assert.Equal(12, credit.Precision);
        Assert.Equal(2, credit.Scale);

        var notes = table.FindColumn("Notes")!;
        Assert.Equal(-1, notes.Length);
        Assert.True(notes.IsNullable);

        Assert.True(table.FindColumn("FullName")!.IsComputed);
        Assert.True(table.FindColumn("RowVer")!.IsRowVersion);
        Assert.NotNull(table.FindColumn("IsActive")!.DefaultExpression);
        Assert.NotNull(table.FindColumn("CreatedAt")!.DefaultExpression);
    }

    [Fact]
    public void Parses_table_constraints()
    {
        var table = DdlParser.ParseSingle(CustomersDdl);

        Assert.Equal(new[] { "CustomerId" }, table.PrimaryKeyColumns);
        Assert.True(table.FindColumn("Email")!.HasUniqueConstraint);

        var fk = Assert.Single(table.ForeignKeys);
        Assert.Equal(new[] { "CountryCode" }, fk.Columns);
        Assert.Equal("[dbo].[Countries]", fk.ReferencedTable);
        Assert.Equal(new[] { "CountryCode" }, fk.ReferencedColumns);

        var check = Assert.Single(table.CheckConstraints);
        Assert.Equal("CK_Credit", check.Name);
        Assert.Contains("CreditLimit", check.Expression);
    }

    [Fact]
    public void Multi_table_script_requires_selector()
    {
        var ddl = "CREATE TABLE A (Id INT NOT NULL); CREATE TABLE B (Id INT NOT NULL);";
        Assert.Equal(2, DdlParser.ParseScript(ddl).Count);
        Assert.Throws<DdlParseException>(() => DdlParser.ParseSingle(ddl));
        Assert.Equal("B", DdlParser.ParseSingle(ddl, "B").Name);
        Assert.Equal("A", DdlParser.ParseSingle(ddl, "dbo.A").Name);
    }

    [Fact]
    public void Invalid_sql_reports_position()
    {
        var ex = Assert.Throws<DdlParseException>(() => DdlParser.ParseScript("CREATE TABLE ("));
        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public void Script_without_create_table_throws()
    {
        Assert.Throws<DdlParseException>(() => DdlParser.ParseScript("SELECT 1;"));
    }

    [Fact]
    public void Inline_pk_marks_column()
    {
        var table = DdlParser.ParseSingle(
            "CREATE TABLE T (Code CHAR(2) NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL)");
        Assert.True(table.FindColumn("Code")!.IsPrimaryKey);
        Assert.Equal(new[] { "Code" }, table.PrimaryKeyColumns);
    }
}
