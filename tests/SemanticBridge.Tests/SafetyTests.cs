using SemanticBridge.Core.Results;
using SemanticBridge.Core.Safety;

namespace SemanticBridge.Tests;

public sealed class SecretScrubberTests
{
    [Fact]
    public void RemovesRegisteredSecretsAndTokens()
    {
        var s = new SecretScrubber();
        s.Register("my-client-secret-xyz");
        s.Register("access-token-abcdef");
        var text = s.Scrub("failed with my-client-secret-xyz and access-token-abcdef end");
        Assert.DoesNotContain("my-client-secret-xyz", text);
        Assert.DoesNotContain("access-token-abcdef", text);
    }

    [Fact]
    public void PaddedSecretRegisteredRawAndTrimmedScrubsTheTrimmedFormToo()
    {
        const string padded = "  http-token-0123456789  ";
        var s = new SecretScrubber();
        s.Register(padded);
        s.Register(padded.Trim());
        Assert.DoesNotContain("http-token-0123456789", s.Scrub("rejected http-token-0123456789 here"));
    }

    [Theory]
    [InlineData("Data Source=powerbi://x;User ID=app:a@b;Password=hunter2;Initial Catalog=d", "hunter2")]
    [InlineData("conn pwd=abc123; rest", "abc123")]
    [InlineData("Authorization: Bearer abc.DEF-123_xyz", "abc.DEF-123_xyz")]
    [InlineData("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl end", "eyJhbGci")]
    [InlineData("grant client_secret=topsecret&client_id=1", "topsecret")]
    [InlineData("{\"access_token\":\"abcdefgh\"}", "abcdefgh")]
    public void RemovesWellKnownShapes(string input, string mustNotRemain)
    {
        Assert.DoesNotContain(mustNotRemain, new SecretScrubber().Scrub(input));
    }

    [Fact]
    public void RemovesEscapedVariantsOfRegisteredSecrets()
    {
        const string secret = "p@ss/w+rd \"q\"&x=y~z";
        var s = new SecretScrubber();
        s.Register(secret);
        var variants = new[]
        {
            System.Text.Json.JsonSerializer.Serialize(secret)[1..^1],
            Uri.EscapeDataString(secret),
            System.Net.WebUtility.UrlEncode(secret),
            $"client_secret={System.Net.WebUtility.UrlEncode(secret)}&grant_type=client_credentials",
        };
        foreach (var variant in variants)
        {
            var text = s.Scrub("failed: " + variant + " end");
            Assert.DoesNotContain("p%40ss", text);
            Assert.DoesNotContain("p@ss", text);
            Assert.DoesNotContain("w%2Brd", text);
            Assert.DoesNotContain("w+rd", text);
            Assert.DoesNotContain("w\\u002Brd", text);
        }
    }

    [Fact]
    public void FullConnectionStringIsRemoved()
    {
        var text = new SecretScrubber().Scrub("error in Data Source=powerbi://api.powerbi.com/v1.0/myorg/ws;Initial Catalog=ds;Provider=MSOLAP; bye");
        Assert.DoesNotContain("powerbi://", text);
        Assert.Contains("[connection string removed]", text);
    }

    [Fact]
    public void LeavesOrdinaryTextAlone()
    {
        Assert.Equal("Query failed: column 'Sales[Amount]' not found", new SecretScrubber().Scrub("Query failed: column 'Sales[Amount]' not found"));
    }
}

public sealed class RowCapTests
{
    [Fact]
    public void StopsAtMaxPlusOneAndFlagsTruncation()
    {
        var cap = new RowCap(3);
        var read = 0;
        foreach (var i in Enumerable.Range(0, 100))
        {
            read++;
            if (!cap.Add([i])) break;
        }
        Assert.Equal(3, cap.Rows.Count);
        Assert.Equal(4, read);
        Assert.True(cap.Truncated);
        Assert.Contains("max_rows (3)", cap.Reason);
    }

    [Fact]
    public void ExactlyMaxRowsIsNotTruncated()
    {
        var cap = new RowCap(2);
        Assert.True(cap.Add([1]));
        Assert.True(cap.Add([2]));
        Assert.False(cap.Truncated);
        Assert.Null(cap.Reason);
    }

    [Fact]
    public void RejectedRowIsNeverBuilt()
    {
        var cap = new RowCap(1);
        cap.Add([1]);
        var built = false;
        Assert.False(cap.Add(() => { built = true; return [2]; }));
        Assert.False(built);
    }
}

public sealed class ValueSanitizerTests
{
    [Fact]
    public void NonFiniteBecomesNullAndIsCounted()
    {
        var s = new ValueSanitizer();
        Assert.Null(s.Sanitize(double.NaN, "[m]"));
        Assert.Null(s.Sanitize(double.PositiveInfinity, "[m]"));
        Assert.Equal(1.5, s.Sanitize(1.5, "[m]"));
        Assert.Null(s.Sanitize(DBNull.Value, "[m]"));
        Assert.Single(s.Warnings);
        Assert.Contains("2 NaN/Infinity", s.Warnings[0]);
        Assert.Contains("[m]", s.Warnings[0]);
    }

    [Fact]
    public void DateTimeKeepsFullPrecisionAndInventsNoTimezone()
    {
        var s = new ValueSanitizer();
        var unspecified = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Unspecified).AddTicks(1234567);
        Assert.Equal("2024-05-06T07:08:09.1234567", s.Sanitize(unspecified, "[d]"));
        var utc = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        Assert.Equal("2024-05-06T07:08:09.0000000Z", s.Sanitize(utc, "[d]"));
    }
}

public sealed class AccessTokenInfoTests
{
    [Fact]
    public void ToStringMasksTheToken()
    {
        var info = new SemanticBridge.Core.Auth.AccessTokenInfo("tok-very-secret-123", DateTimeOffset.UtcNow);
        Assert.DoesNotContain("tok-very-secret-123", info.ToString());
        Assert.DoesNotContain("tok-very-secret-123", $"{info}");
        Assert.Contains("***", info.ToString());
    }
}

public sealed class DaxGuardTests
{
    [Theory]
    [InlineData("EVALUATE ROW(\"x\", 1)")]
    [InlineData("  \n\t evaluate Sales")]
    [InlineData("DEFINE MEASURE T[M] = 1 EVALUATE ROW(\"a\", [M])")]
    [InlineData("// note\nEVALUATE Sales")]
    [InlineData("-- note\r\n/* block\ncomment */ EVALUATE Sales")]
    public void AcceptsQueries(string dax) => DaxGuard.Validate(dax);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SELECT * FROM $SYSTEM.TMSCHEMA_TABLES")]
    [InlineData("<Execute xmlns=\"urn:schemas-microsoft-com:xml-analysis\"/>")]
    [InlineData("EVALUATED Sales")]
    [InlineData("// EVALUATE hidden in a comment")]
    [InlineData("/* unterminated EVALUATE")]
    [InlineData("REFRESH TABLE x")]
    public void RejectsEverythingElse(string dax)
    {
        var ex = Assert.Throws<BridgeException>(() => DaxGuard.Validate(dax));
        Assert.True(ex.IsUsageError);
    }
}

public sealed class WriteGateTests
{
    [Fact]
    public void ClosedByDefaultAndOpenOnlyWhenAllowed()
    {
        var closed = new WriteGate(false);
        Assert.False(closed.IsOpen);
        Assert.Equal(ErrorKind.WriteDisabled, Assert.Throws<BridgeException>(closed.EnsureOpen).Kind);
        var open = new WriteGate(true);
        Assert.True(open.IsOpen);
        open.EnsureOpen();
    }

    [Fact]
    public void HttpNeedsTheSecondOptIn()
    {
        Assert.False(new WriteGate(true, overHttp: true, allowHttpWrite: false).IsOpen);
        Assert.False(new WriteGate(false, overHttp: true, allowHttpWrite: true).IsOpen);
        Assert.True(new WriteGate(true, overHttp: true, allowHttpWrite: true).IsOpen);
        Assert.Contains("--allow-http-write", new WriteGate(true, true, false).ClosedReason);
    }
}
