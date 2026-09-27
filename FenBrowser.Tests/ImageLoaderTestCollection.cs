using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests;

/// <summary>
/// Single shared collection for every test class that calls
/// <see cref="ImageLoader.ClearCache"/>.
/// <para>
/// The state behind that call is process-wide: <c>ImageLoader</c> is a static
/// class holding <c>_memoryCache</c>, <c>_legacyCache</c>, <c>_lastLoadResults</c>,
/// <c>_failedDataUriCache</c>, <c>_lazyRegistry</c> and static hit/miss counters.
/// <c>ClearCache</c> empties all of them and schedules the live bitmaps for
/// disposal, so a concurrent clear can drop a cached bitmap between another
/// test's cache lookup and its measurement, and can bump that test's miss
/// counter mid-assertion.
/// </para>
/// <para>
/// The collection is declared non-parallelizable, so xUnit serializes it against
/// every other collection in the assembly. A class that calls
/// <c>ClearCache</c> must carry
/// <c>[Collection(ImageLoaderTestCollection.Name)]</c>; a class in any other
/// non-parallelizable collection (such as
/// <see cref="Logging.EngineLogTestCollection"/> or
/// <c>Synthetic CAPTCHA</c>) is already serialized and needs no attribute.
/// <see cref="ImageLoaderTestCollectionTests"/> enforces both halves of that
/// rule by reading the call sites out of the compiled IL rather than trusting
/// class names or source text.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageLoaderTestCollection
{
    public const string Name = "ImageLoader process-wide cache";
}

/// <summary>
/// Reads "which test types actually call this process-wide global" straight out
/// of the compiled assembly, and reports what collection each of them is
/// serialized in.
/// <para>
/// The enumeration is by IL call site, not by class name or source text, so a
/// caller cannot escape the check by being named something unexpected, and a
/// class that merely mentions <c>ImageLoader</c> cannot be dragged into the
/// collection by accident.
/// </para>
/// </summary>
internal static class GlobalSingletonCallSites
{
    private const byte CallOpCode = 0x28;
    private const byte CallVirtOpCode = 0x6F;

    public static Assembly TestAssembly => typeof(GlobalSingletonCallSites).Assembly;

    /// <summary>
    /// Every test type whose IL calls <paramref name="methodName"/> on
    /// <paramref name="declaringType"/>, paired with the collection names that
    /// type declares.
    /// <para>
    /// Compiler-generated nested types -- async state machines and lambda
    /// display classes -- are folded into the nearest hand-written class that
    /// owns them, because that is the type xUnit collects. Without that fold an
    /// <c>async Task</c> test body would report no call site at all and would
    /// sail straight past this check.
    /// </para>
    /// </summary>
    public static List<(Type Type, string[] Collections)> TypesCalling(
        Type declaringType,
        string methodName)
    {
        var owners = new List<Type>();
        foreach (var type in TestAssembly.GetTypes())
        {
            if (Calls(type, declaringType, methodName))
            {
                var owner = OwningTypeOf(type);
                if (!owners.Contains(owner))
                {
                    owners.Add(owner);
                }
            }
        }

        owners.Sort((left, right) => string.CompareOrdinal(left.FullName, right.FullName));
        return owners
            .Select(owner => (Type: owner, Collections: CollectionNamesOf(owner)))
            .ToList();
    }

    private static Type OwningTypeOf(Type type)
    {
        while (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || type.Name.IndexOf('<') >= 0)
        {
            var outer = type.DeclaringType;
            if (outer is null)
            {
                return type;
            }

            type = outer;
        }

        return type;
    }

    /// <summary>
    /// Maps every collection name defined in the assembly to whether that
    /// definition disables parallelization. A <c>[Collection("name")]</c> whose
    /// name has no definition at all is a parallelizable default collection,
    /// which is exactly the case that lets these races through.
    /// </summary>
    public static Dictionary<string, bool> CollectionDefinitions() =>
        TestAssembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributesData()
                .Where(data => data.AttributeType == typeof(CollectionDefinitionAttribute))
                .Select(data => (
                    Name: (string)data.ConstructorArguments[0].Value!,
                    NonParallel: data.NamedArguments.Any(argument =>
                        argument.MemberName == "DisableParallelization"
                        && argument.TypedValue.Value is true))))
            .ToDictionary(entry => entry.Name, entry => entry.NonParallel, StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="type"/> is a fixture of a collection that
    /// disables parallelization. A collection fixture runs outside any test
    /// class, so it carries no collection attribute of its own.
    /// </summary>
    public static bool IsFixtureOfNonParallelizableCollection(Type type)
    {
        foreach (var definition in TestAssembly.GetTypes())
        {
            var ownsFixture = definition.GetInterfaces().Any(contract =>
                contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(ICollectionFixture<>)
                && contract.GetGenericArguments()[0] == type);
            if (!ownsFixture)
            {
                continue;
            }

            foreach (var attribute in definition.GetCustomAttributesData()
                         .Where(data => data.AttributeType == typeof(CollectionDefinitionAttribute)))
            {
                var nonParallel = attribute.NamedArguments.Any(argument =>
                    argument.MemberName == "DisableParallelization"
                    && argument.TypedValue.Value is true);
                if (nonParallel)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when every collection the type declares is non-parallelizable and
    /// there is at least one, or when the type is a fixture of such a
    /// collection. Anything else runs concurrently with the rest of the
    /// assembly.
    /// </summary>
    public static bool IsSerializedAgainst(
        string[] collections,
        IReadOnlyDictionary<string, bool> definitions,
        Type type) =>
        collections.Length > 0
            ? collections.All(name => definitions.TryGetValue(name, out var nonParallel) && nonParallel)
            : IsFixtureOfNonParallelizableCollection(type);

    // CollectionAttribute stores its name in the constructor argument only, so
    // the metadata (not an instantiated attribute) is the readable source.
    private static string[] CollectionNamesOf(Type type) =>
        type.GetCustomAttributesData()
            .Where(data => data.AttributeType == typeof(CollectionAttribute))
            .Select(data => (string)data.ConstructorArguments[0].Value!)
            .ToArray();

    private static bool Calls(Type type, Type declaringType, string methodName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static;

        foreach (var method in type.GetMethods(flags).Cast<MethodBase>()
                     .Concat(type.GetConstructors(flags)))
        {
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch
            {
                continue;
            }

            if (il is null)
            {
                continue;
            }

            for (var index = 0; index + 4 < il.Length; index++)
            {
                if (il[index] != CallOpCode && il[index] != CallVirtOpCode)
                {
                    continue;
                }

                var token = BitConverter.ToInt32(il, index + 1);
                try
                {
                    var resolved = type.Module.ResolveMethod(
                        token,
                        type.GetGenericArguments(),
                        method is MethodInfo { IsGenericMethod: true } generic
                            ? generic.GetGenericArguments()
                            : null);
                    if (resolved is not null
                        && resolved.DeclaringType == declaringType
                        && resolved.Name == methodName)
                    {
                        return true;
                    }
                }
                catch
                {
                    // A token we cannot resolve is not a call we can attribute.
                }
            }
        }

        return false;
    }
}

/// <summary>
/// Guards the wiring itself, because the serialization is only as good as the
/// collection definitions: a dropped <c>DisableParallelization</c>, or a new
/// caller left in a parallelizable collection, silently re-opens the races this
/// collection exists to close.
/// </summary>
[Collection(ImageLoaderTestCollection.Name)]
public sealed class ImageLoaderTestCollectionTests
{
    [Fact]
    public void Collection_IsDeclaredNonParallelizableUnderAStableName()
    {
        var definition = typeof(ImageLoaderTestCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>()
            .Single();

        Assert.True(
            definition.DisableParallelization,
            "the ImageLoader cache collection must stay non-parallelizable or it can "
            + "race another collection's cache, counters, or in-flight bitmap disposal");
        Assert.Equal("ImageLoader process-wide cache", ImageLoaderTestCollection.Name);
    }

    [Fact]
    public void EveryClearCacheCaller_RunsInANonParallelizableCollection()
    {
        var definitions = GlobalSingletonCallSites.CollectionDefinitions();
        var callers = GlobalSingletonCallSites.TypesCalling(typeof(ImageLoader), "ClearCache");

        Assert.NotEmpty(callers);

        var parallel = callers
            .Where(caller => !GlobalSingletonCallSites.IsSerializedAgainst(
                caller.Collections, definitions, caller.Type))
            .Select(caller => caller.Type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            parallel.Length == 0,
            "every class that calls ImageLoader.ClearCache() must sit in a "
            + "collection declared with DisableParallelization, or be a fixture of "
            + "one; these run in parallelizable collections and wipe each other's "
            + "process-wide cache mid-test: " + string.Join("; ", parallel));
    }

    [Fact]
    public void ClearCacheCallersThatWereLeftUncollected_JoinTheSharedCollection()
    {
        // These four were the ones in the wild with no collection attribute at
        // all, which is what made the ImageLoader cache races observable. Named
        // explicitly so that moving one back out of the collection is a
        // deliberate, reviewed act rather than a silent edit.
        string[] expected =
        {
            "FenBrowser.Tests.Core.BrowserHostDiagnosticsTests",
            "FenBrowser.Tests.Core.ImageLoaderCssFunctionTests",
            "FenBrowser.Tests.Core.PaintTreePillRenderingContractTests",
            "FenBrowser.Tests.Layout.ReplacedElementSizingTests",
        };

        var callers = GlobalSingletonCallSites.TypesCalling(typeof(ImageLoader), "ClearCache")
            .ToDictionary(caller => caller.Type.FullName!, caller => caller.Collections);

        foreach (var name in expected)
        {
            Assert.True(callers.ContainsKey(name), $"expected an ImageLoader.ClearCache() call site in {name}");
            Assert.True(
                callers[name].Contains(ImageLoaderTestCollection.Name, StringComparer.Ordinal),
                $"{name} calls ImageLoader.ClearCache() but is not in "
                + $"'{ImageLoaderTestCollection.Name}'");
        }
    }
}
