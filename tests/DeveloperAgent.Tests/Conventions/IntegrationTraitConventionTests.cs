using System.Reflection;
using FluentAssertions;

namespace DeveloperAgent.Tests.Conventions;

/// <summary>
/// Convention test that enforces the integration-test trait rule mechanically:
/// every test class whose namespace ends with ".Integration" OR whose name ends with
/// "IntegrationTests" must carry <c>[Trait("Category", "Integration")]</c> at the class level.
///
/// This is a <b>unit</b> test — it carries no Category trait and runs in the default-fast filter.
/// </summary>
public sealed class IntegrationTraitConventionTests
{
    [Fact]
    public void Every_class_in_an_Integration_folder_or_named_with_Integration_suffix_carries_the_Integration_trait()
    {
        var assembly = typeof(IntegrationTraitConventionTests).Assembly;

        // Find all test classes in the integration-test convention scope.
        // Exclude compiler-generated types: state machine types (<SomeMethod>d__N), closure
        // classes (<>c, <>c__DisplayClassN), lambda implementations, and any type that is
        // itself nested inside a compiler-generated type. We detect these by:
        //   1. The type itself carries [CompilerGenerated].
        //   2. The type's Name starts with '<' (C# compiler convention for all generated types).
        //   3. Any ancestor (via DeclaringType chain) starts with '<'.
        var candidates = assembly.GetTypes()
            .Where(t =>
                t.IsClass &&
                !t.IsAbstract &&
                !IsCompilerGenerated(t) &&
                (t.Namespace?.EndsWith(".Integration", StringComparison.Ordinal) == true ||
                 t.Name.EndsWith("IntegrationTests", StringComparison.Ordinal)))
            .ToList();

        // For each candidate, assert it has [Trait("Category", "Integration")] at the class level.
        // We use GetCustomAttributesData() to read constructor arguments without relying on
        // strongly typed property access (which can differ across xUnit major versions).
        var violators = candidates
            .Where(t => !HasIntegrationTrait(t))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        violators.Should().BeEmpty(
            because: "every integration test class must be decorated with " +
                     "[Trait(\"Category\", \"Integration\")] so the default-fast filter excludes it. " +
                     "Add the trait to: {0}", string.Join(", ", violators));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the type is compiler-generated.
    /// Detects state machines, closures, display classes, and any type
    /// nested inside such a generated type, by checking both
    /// <see cref="System.Runtime.CompilerServices.CompilerGeneratedAttribute"/> and
    /// the C# naming convention (generated type names start with <c>&lt;</c>).
    /// </summary>
    private static bool IsCompilerGenerated(Type type)
    {
        var t = type;
        while (t is not null)
        {
            if (t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
                return true;
            if (t.Name.StartsWith('<'))
                return true;
            t = t.DeclaringType;
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the type carries
    /// <c>[Trait("Category", "Integration")]</c> as a class-level attribute.
    /// </summary>
    private static bool HasIntegrationTrait(Type type)
    {
        // Use GetCustomAttributesData so we can read constructor arguments regardless of
        // whether TraitAttribute exposes Name/Value as public properties in this xUnit build.
        foreach (var attrData in type.GetCustomAttributesData())
        {
            if (!string.Equals(
                    attrData.AttributeType.Name, "TraitAttribute",
                    StringComparison.Ordinal))
                continue;

            var args = attrData.ConstructorArguments;
            if (args.Count >= 2 &&
                string.Equals(args[0].Value as string, "Category", StringComparison.Ordinal) &&
                string.Equals(args[1].Value as string, "Integration", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
