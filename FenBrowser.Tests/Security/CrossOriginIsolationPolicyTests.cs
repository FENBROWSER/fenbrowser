using FenBrowser.Core.Security;
using Xunit;

namespace FenBrowser.Tests.Security;

public sealed class CrossOriginIsolationPolicyTests
{
    [Theory]
    [InlineData("same-origin", "require-corp", true)]
    [InlineData("same-origin", "credentialless", true)]
    [InlineData("same-origin-plus-coep", "require-corp", true)]
    [InlineData("same-origin", "unsafe-none", false)]
    [InlineData("unsafe-none", "require-corp", false)]
    [InlineData("same-origin-allow-popups", "require-corp", false)]
    [InlineData(null, null, false)]
    [InlineData("same-origin", null, false)]
    public void IsCrossOriginIsolated_HeaderCombinations(string? coop, string? coep, bool expected)
    {
        var policy = new CrossOriginIsolationPolicy();
        policy.ParseCoopHeader(coop);
        policy.ParseCoepHeader(coep);

        Assert.Equal(expected, policy.IsCrossOriginIsolated);
    }

    [Theory]
    [InlineData("same-origin", true)]
    [InlineData("same-site", true)]
    [InlineData("cross-origin", true)]
    [InlineData("", true)]
    [InlineData("same-origin", true)]
    public void IsResponseEmbeddable_SameOrigin_AlwaysAllowed(string? corp, bool ignored)
    {
        _ = ignored;
        var policy = new CrossOriginIsolationPolicy();
        policy.ParseCoopHeader("same-origin");
        policy.ParseCoepHeader("require-corp");

        Assert.True(policy.IsResponseEmbeddable(requestIsSameOrigin: true, corp, requestIsNoCors: true));
    }

    [Theory]
    [InlineData("cross-origin", true)]
    [InlineData("same-origin", false)]
    [InlineData("same-site", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsResponseEmbeddable_CrossOriginNoCors_RequiresCorpCrossOrigin(string? corp, bool expected)
    {
        var policy = new CrossOriginIsolationPolicy();
        policy.ParseCoopHeader("same-origin");
        policy.ParseCoepHeader("require-corp");

        Assert.Equal(expected, policy.IsResponseEmbeddable(requestIsSameOrigin: false, corp, requestIsNoCors: true));
    }

    [Theory]
    [InlineData("cross-origin", true)]
    [InlineData("same-origin", true)]
    [InlineData(null, true)]
    public void IsResponseEmbeddable_CorsMode_AlwaysAllowed(string? corp, bool expected)
    {
        var policy = new CrossOriginIsolationPolicy();
        policy.ParseCoopHeader("same-origin");
        policy.ParseCoepHeader("require-corp");

        Assert.Equal(expected, policy.IsResponseEmbeddable(requestIsSameOrigin: false, corp, requestIsNoCors: false));
    }

    [Theory]
    [InlineData("require-corp", true)]
    [InlineData("credentialless", true)]
    [InlineData("unsafe-none", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void RequiresCorp_OnlyWhenCoepActive(string? coep, bool expected)
    {
        var policy = new CrossOriginIsolationPolicy();
        policy.ParseCoepHeader(coep);

        Assert.Equal(expected, policy.RequiresCorp);
    }

    [Theory]
    [InlineData("same-origin", CrossOriginIsolationPolicy.CorpValue.SameOrigin)]
    [InlineData("same-site", CrossOriginIsolationPolicy.CorpValue.SameSite)]
    [InlineData("cross-origin", CrossOriginIsolationPolicy.CorpValue.CrossOrigin)]
    [InlineData("SAME-ORIGIN", CrossOriginIsolationPolicy.CorpValue.SameOrigin)]
    [InlineData(null, CrossOriginIsolationPolicy.CorpValue.None)]
    [InlineData("bogus-value", CrossOriginIsolationPolicy.CorpValue.None)]
    public void ParseCorpHeader_VariousValues(string? header, CrossOriginIsolationPolicy.CorpValue expected)
    {
        Assert.Equal(expected, CrossOriginIsolationPolicy.ParseCorpHeader(header));
    }
}