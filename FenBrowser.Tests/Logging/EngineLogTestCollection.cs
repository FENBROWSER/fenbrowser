using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Logging;

/// <summary>
/// Single shared collection for every test class that calls
/// <see cref="EngineLog.Configure"/>.
/// <para>
/// <c>EngineLog</c> is a static singleton, so reconfiguring it re-points the
/// process-wide sinks. A class that configures it from a parallelizable
/// collection can disable or replace another test's sink mid-test, which is how
/// a trace file that should hold exactly one line picks up every other test's
/// log writes instead.
/// </para>
/// <para>
/// The collection is declared non-parallelizable, so xUnit serializes it against
/// every other collection in the assembly. A class that calls
/// <c>Configure</c> must carry <c>[Collection(EngineLogTestCollection.Name)]</c>;
/// <see cref="EngineLogTestCollectionTests"/> enforces that by reading the call
/// sites out of the compiled IL rather than trusting class names or source text.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EngineLogTestCollection
{
    public const string Name = "EngineLog";
}

/// <summary>
/// Guards the wiring, for the same reason
/// <see cref="ImageLoaderTestCollectionTests"/> does: a dropped
/// <c>DisableParallelization</c> or a new uncollected caller silently re-opens
/// the races this collection exists to close.
/// </summary>
[Collection(EngineLogTestCollection.Name)]
public sealed class EngineLogTestCollectionTests
{
    [Fact]
    public void Collection_IsDeclaredNonParallelizableUnderAStableName()
    {
        var definition = typeof(EngineLogTestCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>()
            .Single();

        Assert.True(
            definition.DisableParallelization,
            "the EngineLog collection must stay non-parallelizable or it can race "
            + "another collection's sink configuration and trace-file contents");
        Assert.Equal("EngineLog", EngineLogTestCollection.Name);
    }

    [Fact]
    public void EveryConfigureCaller_RunsInANonParallelizableCollection()
    {
        var definitions = GlobalSingletonCallSites.CollectionDefinitions();
        var callers = GlobalSingletonCallSites.TypesCalling(typeof(EngineLog), "Configure");

        Assert.NotEmpty(callers);

        var parallel = callers
            .Where(caller => !GlobalSingletonCallSites.IsSerializedAgainst(
                caller.Collections, definitions, caller.Type))
            .Select(caller => caller.Type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            parallel.Length == 0,
            "every class that calls EngineLog.Configure() must sit in a collection "
            + "declared with DisableParallelization, or be a fixture of one; these "
            + "re-point the process-wide log sinks while another collection is "
            + "asserting on them: " + string.Join("; ", parallel));
    }

    [Fact]
    public void TheCorsSenderWasTheLastUncollectedConfigureCaller()
    {
        // Named explicitly so that moving it back out of the collection is a
        // deliberate, reviewed act rather than a silent edit.
        const string name = "FenBrowser.Tests.Core.ResourceManagerCorsSendAsyncTests";

        var callers = GlobalSingletonCallSites.TypesCalling(typeof(EngineLog), "Configure")
            .ToDictionary(caller => caller.Type.FullName!, caller => caller.Collections);

        Assert.True(callers.ContainsKey(name), $"expected an EngineLog.Configure() call site in {name}");
        Assert.True(
            callers[name].Contains(EngineLogTestCollection.Name, StringComparer.Ordinal),
            $"{name} calls EngineLog.Configure() but is not in '{EngineLogTestCollection.Name}'");
    }
}
