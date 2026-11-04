using SemanticBridge.Core;
using SemanticBridge.Core.Config;
using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;
using SemanticBridge.Core.Xmla;

namespace SemanticBridge.Tests;

public sealed class BridgeServiceTests
{
    private static BridgeService Service(FakeXmla xmla, FakeHandler handler, QueryVia via = QueryVia.Auto, int ceiling = 100_000) =>
        new(TestData.Config(via, ceiling: ceiling), TestData.Rest(handler, TestData.Config(via, ceiling: ceiling)), xmla);

    private static Task<QueryResult> Query(BridgeService s, int? maxRows = null, QueryVia? via = null, string dax = "EVALUATE x") =>
        s.QueryAsync("sales", "model", dax, maxRows, via, CancellationToken.None);

    [Fact]
    public async Task AutoUsesXmlaWhenItWorks()
    {
        var xmla = new FakeXmla { OnQuery = () => TestData.Xmla() };
        var result = await Query(Service(xmla, TestData.PowerBi()));
        Assert.Equal(ResultPaths.Xmla, result.Path);
        Assert.Equal("Sales", xmla.LastWorkspace);
    }

    private static readonly string NoCapacity = $$"""{"value":[{"id":"{{TestData.WorkspaceId}}","name":"Sales","isOnDedicatedCapacity":false}]}""";
    private static readonly string UnknownCapacity = $$"""{"value":[{"id":"{{TestData.WorkspaceId}}","name":"Sales"}]}""";

    private static bool RanExecuteQueries(FakeHandler handler) =>
        handler.Requests.Any(r => r.Url.EndsWith("/executeQueries", StringComparison.Ordinal));

    [Fact]
    public async Task AutoOnWorkspaceWithoutCapacityUsesRestWithoutTryingXmlaAndSaysWhy()
    {
        var xmla = new FakeXmla { OnQuery = () => throw new InvalidOperationException("must not be called") };
        var handler = TestData.PowerBi(workspaces: NoCapacity);
        var result = await Query(Service(xmla, handler));
        Assert.Equal(ResultPaths.Rest, result.Path);
        Assert.Equal(0, xmla.QueryCalls);
        Assert.Contains(result.Warnings, w => w.Contains("not on dedicated capacity"));
        Assert.True(RanExecuteQueries(handler));
    }

    [Theory]
    [InlineData(ErrorKind.Dax)]
    [InlineData(ErrorKind.Xmla)]
    [InlineData(ErrorKind.Auth)]
    public async Task AutoOnCapacityWorkspaceSurfacesXmlaErrorsWithoutFallback(ErrorKind kind)
    {
        var xmla = new FakeXmla { OnQuery = () => throw new BridgeException(kind, "boom") };
        var handler = TestData.PowerBi();
        var ex = await Assert.ThrowsAsync<BridgeException>(() => Query(Service(xmla, handler)));
        Assert.Equal(kind, ex.Kind);
        Assert.False(RanExecuteQueries(handler));
    }

    [Fact]
    public async Task AutoOnCapacityWorkspaceDoesNotFallBackOnTimeoutOrGenericErrors()
    {
        var handler = TestData.PowerBi();
        var timeout = new FakeXmla { OnQuery = () => throw new BridgeException(ErrorKind.Xmla, "XMLA connection timed out after 120 s.") };
        await Assert.ThrowsAsync<BridgeException>(() => Query(Service(timeout, handler)));
        var generic = new FakeXmla { OnQuery = () => throw new TimeoutException("The connection either timed out or was lost.") };
        await Assert.ThrowsAsync<TimeoutException>(() => Query(Service(generic, handler)));
        Assert.False(RanExecuteQueries(handler));
    }

    [Fact]
    public async Task AutoWithUnknownCapacityUsesXmla()
    {
        var xmla = new FakeXmla { OnQuery = () => TestData.Xmla() };
        var result = await Query(Service(xmla, TestData.PowerBi(workspaces: UnknownCapacity)));
        Assert.Equal(ResultPaths.Xmla, result.Path);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ExplicitXmlaNeverFallsBackEvenWithoutCapacity()
    {
        var xmla = new FakeXmla { OnQuery = () => throw new BridgeException(ErrorKind.Xmla, "no endpoint") };
        var handler = TestData.PowerBi(workspaces: NoCapacity);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => Query(Service(xmla, handler), via: QueryVia.Xmla));
        Assert.Equal(ErrorKind.Xmla, ex.Kind);
        Assert.Equal(1, xmla.QueryCalls);
        Assert.False(RanExecuteQueries(handler));
    }

    [Fact]
    public async Task ExplicitRestSkipsXmla()
    {
        var xmla = new FakeXmla { OnQuery = () => throw new InvalidOperationException("must not be called") };
        var result = await Query(Service(xmla, TestData.PowerBi()), via: QueryVia.Rest);
        Assert.Equal(ResultPaths.Rest, result.Path);
        Assert.Equal(0, xmla.QueryCalls);
    }

    [Fact]
    public async Task ConfigDefaultViaIsHonoured()
    {
        var xmla = new FakeXmla { OnQuery = () => throw new InvalidOperationException("must not be called") };
        var result = await Query(Service(xmla, TestData.PowerBi(), QueryVia.Rest));
        Assert.Equal(ResultPaths.Rest, result.Path);
    }

    private static ModelSchema EmptySchema() => new() { Tables = [], Relationships = [] };

    [Fact]
    public async Task SchemaWithExplicitViaRestFailsClearlyBeforeAnyNetworkCall()
    {
        var handler = TestData.PowerBi();
        var service = Service(new FakeXmla(), handler);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => service.GetSchemaAsync("sales", "model", null, QueryVia.Rest, CancellationToken.None));
        Assert.True(ex.IsUsageError);
        Assert.Contains("per the Power BI REST docs (Datasets - Execute Queries)", ex.Message);
        Assert.Contains("INFO functions are not supported", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SchemaWithConfigDefaultRestStillUsesXmla()
    {
        var xmla = new FakeXmla { OnSchema = EmptySchema };
        var schema = await Service(xmla, TestData.PowerBi(), QueryVia.Rest).GetSchemaAsync("sales", "model", null, null, CancellationToken.None);
        Assert.Equal(ResultPaths.Xmla, schema.Path);
    }

    [Fact]
    public async Task SchemaOnWorkspaceWithoutCapacityGivesTheRestUnsupportedError()
    {
        var xmla = new FakeXmla { OnSchema = () => throw new InvalidOperationException("must not be called") };
        var service = Service(xmla, TestData.PowerBi(workspaces: NoCapacity));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => service.GetSchemaAsync("sales", "model", null, null, CancellationToken.None));
        Assert.True(ex.IsUsageError);
        Assert.Contains("not on a dedicated capacity", ex.Message);
        Assert.Contains("Datasets - Execute Queries", ex.Message);
    }

    [Fact]
    public async Task MaxRowsAboveCeilingIsCappedAndReported()
    {
        var xmla = new FakeXmla { OnQuery = () => TestData.Xmla() };
        var result = await Query(Service(xmla, TestData.PowerBi(), ceiling: 500), maxRows: 9000);
        Assert.Equal(500, xmla.LastMaxRows);
        Assert.Contains(result.Warnings, w => w.Contains("9000") && w.Contains("500"));
    }

    [Fact]
    public async Task MaxRowsWithinCeilingIsPassedThroughAndDefaultsApply()
    {
        var xmla = new FakeXmla { OnQuery = () => TestData.Xmla() };
        var service = Service(xmla, TestData.PowerBi());
        var result = await Query(service, maxRows: 7);
        Assert.Equal(7, xmla.LastMaxRows);
        Assert.Empty(result.Warnings);
        await Query(service);
        Assert.Equal(1000, xmla.LastMaxRows);
        await Assert.ThrowsAsync<BridgeException>(() => Query(service, maxRows: 0));
    }

    [Fact]
    public async Task NonDaxIsRejectedBeforeAnyCall()
    {
        var handler = TestData.PowerBi();
        var ex = await Assert.ThrowsAsync<BridgeException>(() => Query(Service(new FakeXmla(), handler), dax: "SELECT 1"));
        Assert.True(ex.IsUsageError);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AmbiguousWorkspaceNamesAreReportedAndDuplicatesUseTheGuidForm()
    {
        var two = $$"""{"value":[{"id":"{{TestData.WorkspaceId}}","name":"Sales"},{"id":"33333333-3333-3333-3333-333333333333","name":"sales"}]}""";
        var service = Service(new FakeXmla { OnQuery = () => TestData.Xmla() }, TestData.PowerBi(workspaces: two));
        var ex = await Assert.ThrowsAsync<BridgeException>(() => service.ResolveWorkspaceAsync("SALES", CancellationToken.None));
        Assert.Equal(ErrorKind.Ambiguous, ex.Kind);
        var byId = await service.ResolveWorkspaceAsync(TestData.WorkspaceId, CancellationToken.None);
        Assert.Equal($"Sales - {TestData.WorkspaceId}", byId.XmlaName);
    }

    [Fact]
    public async Task DefaultWorkspaceIsUsedAndMissingOneIsAUsageError()
    {
        var handler = TestData.PowerBi();
        var withDefault = new BridgeService(TestData.Config(defaultWorkspace: "Sales"), TestData.Rest(handler), new FakeXmla());
        Assert.Equal("Sales", (await withDefault.ResolveWorkspaceAsync(null, CancellationToken.None)).Workspace.Name);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => Service(new FakeXmla(), handler).ResolveWorkspaceAsync(null, CancellationToken.None));
        Assert.True(ex.IsUsageError);
    }
}
