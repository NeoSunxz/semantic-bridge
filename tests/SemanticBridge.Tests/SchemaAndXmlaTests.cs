using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Tests;

public sealed class SchemaAndXmlaTests
{
    private static MetaRow Row(params (string, object?)[] values) => new(values.ToDictionary(v => v.Item1, v => v.Item2));

    private static readonly IReadOnlyList<MetaRow> Tables =
    [
        Row(("ID", 1L), ("Name", "Sales"), ("Description", "Fact table"), ("IsHidden", false)),
        Row(("ID", 2L), ("Name", "Product"), ("IsHidden", false)),
        Row(("ID", 3L), ("Name", "DateTableTemplate_x"), ("IsHidden", true), ("IsPrivate", true)),
    ];

    private static readonly IReadOnlyList<MetaRow> Columns =
    [
        Row(("ID", 10L), ("TableID", 1L), ("ExplicitName", "Amount"), ("ExplicitDataType", 10L), ("Type", 1L), ("IsHidden", false)),
        Row(("ID", 11L), ("TableID", 1L), ("ExplicitName", "ProductKey"), ("ExplicitDataType", 6L), ("Type", 1L)),
        Row(("ID", 12L), ("TableID", 1L), ("ExplicitName", "RowNumber-xyz"), ("Type", 3L)),
        Row(("ID", 20L), ("TableID", 2L), ("ExplicitName", "ProductKey"), ("ExplicitDataType", 6L), ("Type", 1L)),
    ];

    private static readonly IReadOnlyList<MetaRow> Measures =
    [
        Row(("ID", 30L), ("TableID", 1L), ("Name", "Total"), ("Expression", "SUM(Sales[Amount])"), ("DataType", 10L), ("Description", "Sum of sales")),
    ];

    private static readonly IReadOnlyList<MetaRow> Relationships =
    [
        Row(("ID", 40L), ("FromTableID", 1L), ("FromColumnID", 11L), ("FromCardinality", 2L), ("ToTableID", 2L), ("ToColumnID", 20L), ("ToCardinality", 1L), ("CrossFilteringBehavior", 1L), ("IsActive", true)),
    ];

    private static ModelSchema Build(string? filter = null) => SchemaBuilder.Build(Tables, Columns, Measures, Relationships, filter, 5, []);

    [Fact]
    public void BuildsTablesColumnsMeasuresAndRelationships()
    {
        var schema = Build();
        Assert.Equal(["Product", "Sales"], schema.Tables.Select(t => t.Name));
        var sales = schema.Tables.Single(t => t.Name == "Sales");
        Assert.Equal(["Amount", "ProductKey"], sales.Columns.Select(c => c.Name));
        Assert.Equal("Decimal", sales.Columns[0].DataType);
        var measure = Assert.Single(sales.Measures);
        Assert.Equal("SUM(Sales[Amount])", measure.Expression);
        var rel = Assert.Single(schema.Relationships);
        Assert.Equal(("Sales", "ProductKey", "many"), (rel.FromTable, rel.FromColumn, rel.FromCardinality));
        Assert.Equal(("Product", "ProductKey", "one", "single"), (rel.ToTable, rel.ToColumn, rel.ToCardinality, rel.CrossFilter));
    }

    [Fact]
    public void TableFilterKeepsOneTableAndItsRelationships()
    {
        var schema = Build("product");
        Assert.Equal("Product", Assert.Single(schema.Tables).Name);
        Assert.Single(schema.Relationships);
    }

    [Fact]
    public void UnknownTableFilterListsAvailableTables()
    {
        var ex = Assert.Throws<BridgeException>(() => Build("Nope"));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
        Assert.Contains("Sales", ex.Message);
    }

    [Fact]
    public void InfoResultColumnNamesAreNormalized()
    {
        var result = new QueryResult
        {
            Columns = [new ColumnInfo("[Name]", "String"), new ColumnInfo("Sales[Amount]", "Int64")],
            Rows = [["a", 1L]],
            Path = ResultPaths.Xmla,
        };
        var row = Assert.Single(MetaRow.From(result));
        Assert.Equal("a", row.Str("Name"));
        Assert.Equal(1, row.Int("Amount"));
    }

    [Fact]
    public void RowsWithoutIdOrWithDuplicateIdsDoNotBreakTheBuild()
    {
        IReadOnlyList<MetaRow> tables = [.. Tables, Row(("Name", "NoId")), Row(("ID", 1L), ("Name", "DuplicateOfSales"))];
        IReadOnlyList<MetaRow> columns = [.. Columns, Row(("ExplicitName", "Orphan")), Row(("ID", 11L), ("TableID", 1L), ("ExplicitName", "Dup"), ("Type", 1L))];
        var schema = SchemaBuilder.Build(tables, columns, Measures, Relationships, null, 5, []);
        Assert.Single(schema.Relationships);
        Assert.Equal("ProductKey", schema.Relationships[0].FromColumn);
    }

    [Fact]
    public void MetadataRowCapIsAppliedAndReported()
    {
        var warnings = new List<string>();
        var rows = Enumerable.Range(0, 100_001).Select(i => Row(("ID", (long)i))).ToList();
        var capped = AdomdXmlaClient.CapRows(rows, "TMSCHEMA_COLUMNS", warnings);
        Assert.Equal(100_000, capped.Count);
        Assert.Contains("TMSCHEMA_COLUMNS", Assert.Single(warnings));
        warnings.Clear();
        Assert.Equal(2, AdomdXmlaClient.CapRows(rows.Take(2).ToList(), "x", warnings).Count);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ConnectionStringQuotesValuesSoNamesCannotInjectProperties()
    {
        var config = TestData.Config();
        foreach (var dataset in new[] { "a;Roles=x", "a\"b;Roles=x", "plain" })
        {
            var cs = TokenAdomdConnectionFactory.BuildConnectionString(config, "Sales;Roles=y", dataset);
            var parsed = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };
            Assert.Equal(dataset, parsed["Initial Catalog"]);
            Assert.False(parsed.ContainsKey("Roles"));
            Assert.Equal("120", parsed["Connect Timeout"]);
            Assert.Equal(3, parsed.Count);
        }
        Assert.Contains("Initial Catalog=\"a;Roles=x\"", TokenAdomdConnectionFactory.BuildConnectionString(config, "Sales", "a;Roles=x"));
        Assert.Contains("Initial Catalog=\"a\"\"b\"", TokenAdomdConnectionFactory.BuildConnectionString(config, "Sales", "a\"b"));
    }

    [Fact]
    public void ConnectionStringRejectsControlCharacters()
    {
        var ex = Assert.Throws<BridgeException>(() => TokenAdomdConnectionFactory.BuildConnectionString(TestData.Config(), "Sales", "a\nb"));
        Assert.True(ex.IsUsageError);
    }
}
