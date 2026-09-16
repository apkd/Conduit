#nullable enable

using System;
using System.Linq;
using System.Reflection;
using Conduit;
using NUnit.Framework;
using UnityEngine;

public sealed class ReflectionMatchingTests
{
    [TestCase("Update")]
    [TestCase("uPdAtE")]
    public void SingularLookupsPreferExactNamesAndPluralSearchesKeepPartialMatches(string query)
    {
        var type = typeof(ConduitMatchFixture);
        var expected = type.GetMethod(nameof(ConduitMatchFixture.Update));
        Assert.That(ConduitReflect.Method(type.Name, query), Is.EqualTo(expected));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "." + query), Is.EqualTo(new[] { expected }));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + query), Is.EqualTo(new[] { expected }));

        var all = ConduitReflect.Methods(type.Name, query);
        Assert.That(all.Select(method => method.Name), Is.EquivalentTo(new[]
        {
            nameof(ConduitMatchFixture.Update), nameof(ConduitMatchFixture.UpdateCache), nameof(ConduitMatchFixture.FixedUpdate),
        }));
        var report = ReflectionTool.Reflect(new[] { "methods", type.Name, query });
        Assert.That(report.outcome, Is.EqualTo(ToolOutcome.Success));
        foreach (var method in all)
            Assert.That(report.return_value, Does.Contain(method.Name));
    }

    [Test]
    public void PrefixWinsOverSubstringAndEqualPrefixesStayAmbiguous()
    {
        var type = typeof(ConduitMatchFixture);
        var expected = type.GetMethod(nameof(ConduitMatchFixture.RefreshCache));
        Assert.That(ConduitReflect.Method(type.Name, "Refresh"), Is.EqualTo(expected));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::Refresh"), Is.EqualTo(new[] { expected }));
        Assert.Throws<InvalidOperationException>(() => ConduitReflect.Method(type.Name, "Updat"));
        Assert.That(ViewIlTool.Execute(type.Name + "::Updat").outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
    }

    [Test]
    public void OverloadsStayAmbiguousAndReportedSelectorsResolveEachBody()
    {
        var type = typeof(ConduitMatchFixture);
        var error = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Method(type.Name, "Choose"))!;
        var result = ViewIlTool.Execute(type.Name + "::Choose");
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
        Assert.That(result.diagnostic, Does.Not.Contain(nameof(ConduitMatchFixture.ChooseCached)));
        foreach (var method in type.GetMethods().Where(method => method.Name == "Choose"))
        {
            var selector = ReflectionMemberFormatter.Selector(method);
            Assert.That(result.diagnostic, Does.Contain(selector));
            Assert.That(error.Message, Does.Contain(ReflectionMemberFormatter.MemberName(method, TypeNameFormat.Qualified)));
            Assert.That(ReflectionQueryEngine.ResolveTarget(selector), Is.EqualTo(new[] { method }));
            Assert.That(ConduitReflect.Method(type.Name, ReflectionMemberFormatter.MemberName(method, TypeNameFormat.Short)), Is.EqualTo(method));
            Assert.That(ViewIlTool.Execute(selector).outcome, Is.EqualTo(ToolOutcome.Success));
        }
    }

    [Test]
    public void WideLookupUsesTheSameExactNamesAndSignatures()
    {
        var type = typeof(ConduitMatchFixture);
        var method = type.GetMethod(nameof(ConduitMatchFixture.ConduitWideMemberProbe));
        foreach (var query in new[] { "ConduitWideMemberProbe", "ConduitWideMemberProbe( int )" })
        {
            Assert.That(ConduitReflect.Method(member: query), Is.EqualTo(method));
            Assert.That(ReflectionQueryEngine.ResolveTarget(query), Is.EqualTo(new[] { method }));
            var report = ReflectionTool.Reflect(new[] { "methods", string.Empty, query });
            Assert.That(report.outcome, Is.EqualTo(ToolOutcome.Success));
            Assert.That(report.return_value, Does.Contain(method!.Name));
        }
    }

    [TestCase("ctor", false)]
    [TestCase(".ctor", false)]
    [TestCase("cctor", true)]
    [TestCase(".cctor", true)]
    public void ConstructorAliasesDistinguishInstanceAndStaticConstructors(string query, bool isStatic)
    {
        var type = typeof(ConduitMatchFixture);
        var expected = isStatic ? type.TypeInitializer : type.GetConstructor(Type.EmptyTypes);
        Assert.That(ConduitReflect.Constructor(type.Name, query), Is.EqualTo(expected));
        Assert.That(ConduitReflect.Constructor(type.Name, query + "()"), Is.EqualTo(expected));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + query), Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void TypeLookupSharesExactPreferenceAndAcceptsAssemblyQualifiedSelectors()
    {
        var type = typeof(ConduitMatchFixture);
        Assert.That(ConduitReflect.Type(type.Name), Is.EqualTo(type));
        Assert.That(ConduitReflect.ResolveType(type.Name), Is.EqualTo(type));
        Assert.That(ConduitReflect.Types(type.Name), Has.Member(typeof(ConduitMatchFixtureExtra)));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name), Is.EqualTo(new[] { type }));
        Assert.That(ReflectionQueryEngine.ResolveTarget(ReflectionMemberFormatter.Selector(type, SelectorFormat.AssemblyQualified)), Is.EqualTo(new[] { type }));
    }

    [Test]
    public void LimitingCandidateDisplayDoesNotMakeAnAmbiguousTypeUnique()
    {
        var types = new[] { typeof(ConduitMatchFixture), typeof(ConduitMatchFixtureExtra) };
        var match = ReflectionQueryEngine.MatchSingleType(types, "ConduitMatch", maxCandidates: 1);
        Assert.That(match.Kind, Is.EqualTo(TypeMatchKind.Ambiguous));
        Assert.That(match.CandidateCount, Is.EqualTo(types.Length));
        Assert.That(match.Candidates, Has.Count.EqualTo(1));
        Assert.That(match.Type, Is.Null);
    }

    [TestCase(typeof(ConduitMatchingFirst.LookupFixture<>), typeof(ConduitMatchingSecond.LookupFixture<>))]
    [TestCase(typeof(ConduitMatchingFirst.LookupFixture<>.Nested), typeof(ConduitMatchingSecond.LookupFixture<>.Nested))]
    public void GenericAndNestedDisplayNamesPreserveAmbiguityUntilQualified(Type first, Type second)
    {
        string name = ReflectionTypeFormatter.DisplayTypeName(first, includeNamespace: false);
        Assert.Throws<InvalidOperationException>(() => ConduitReflect.Type(name));
        Assert.That(ReflectionQueryEngine.ResolveTarget(name), Is.EquivalentTo(new[] { first, second }));
        foreach (var type in new[] { first, second })
        {
            string selector = ReflectionMemberFormatter.Selector(type);
            Assert.That(ConduitReflect.Type(selector), Is.EqualTo(type));
            Assert.That(ReflectionQueryEngine.ResolveTarget(selector), Is.EqualTo(new[] { type }));
        }
    }

    [Test]
    public void ConversionOverloadsRemainDistinctAndEverySelectorResolvesOneBody()
    {
        var type = typeof(Vector4);
        var methods = type.GetMethods().Where(method => method.Name == "op_Implicit"
            && method.GetParameters()[0].ParameterType == type).ToArray();
        Assert.That(methods.Length, Is.GreaterThan(1));
        var target = "UnityEngine.Vector4::op_Implicit(UnityEngine.Vector4)";
        Assert.That(ReflectionQueryEngine.ResolveTarget(target), Is.EquivalentTo(methods));
        var ambiguous = ViewIlTool.Execute(target);
        Assert.That(ambiguous.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));

        var selectors = methods.Select(method => ReflectionMemberFormatter.Selector(method)).ToArray();
        Assert.That(selectors.Distinct().Count(), Is.EqualTo(methods.Length));
        foreach (var method in methods)
        {
            var selector = ReflectionMemberFormatter.Selector(method);
            Assert.That(ambiguous.diagnostic, Does.Contain(selector));
            Assert.That(ReflectionQueryEngine.ResolveTarget(selector), Is.EqualTo(new[] { method }));
            Assert.That(ViewIlTool.Execute(selector).outcome, Is.EqualTo(ToolOutcome.Success));
            var signature = selector.Substring(selector.IndexOf("::", StringComparison.Ordinal) + 2);
            Assert.That(ConduitReflect.Method(type.FullName, signature), Is.EqualTo(method));
        }
    }

    [TestCase(typeof(void))]
    [TestCase(typeof(bool))]
    [TestCase(typeof(byte))]
    [TestCase(typeof(sbyte))]
    [TestCase(typeof(char))]
    [TestCase(typeof(decimal))]
    [TestCase(typeof(double))]
    [TestCase(typeof(float))]
    [TestCase(typeof(int))]
    [TestCase(typeof(uint))]
    [TestCase(typeof(long))]
    [TestCase(typeof(ulong))]
    [TestCase(typeof(object))]
    [TestCase(typeof(short))]
    [TestCase(typeof(ushort))]
    [TestCase(typeof(string))]
    [TestCase(typeof(IntPtr))]
    [TestCase(typeof(UIntPtr))]
    public void BuiltInTypeNamesAndTheirMemberSelectorsRoundTrip(Type type)
    {
        var alias = ReflectionTypeFormatter.FormatType(type);
        Assert.That(ConduitReflect.Type(alias), Is.EqualTo(type));
        Assert.That(ConduitReflect.Type(alias.ToUpperInvariant()), Is.EqualTo(type));
        Assert.That(ReflectionQueryEngine.ResolveTarget(alias), Is.EqualTo(new[] { type }));
        Assert.That(ConduitReflect.Types(alias), Has.Member(type));

        var declaringType = type.GetMethod(nameof(GetHashCode), Type.EmptyTypes)!.DeclaringType!;
        var method = declaringType.GetMethod(nameof(GetHashCode), Type.EmptyTypes)!;
        Assert.That(ReflectionQueryEngine.ResolveTarget(alias + "::GetHashCode()"), Is.EqualTo(new[] { method }));
        Assert.That(ReflectionQueryEngine.ResolveTarget(ReflectionMemberFormatter.Selector(method)), Is.EqualTo(new[] { method }));
    }

    [TestCase("Choose( int )")]
    [TestCase(" Choose ( int ) -> int ")]
    [TestCase("CHOOSE(\nint\t)\n->\tint")]
    public void SignaturesIgnoreWhitespaceAndAcceptAReturnType(string query)
    {
        var type = typeof(ConduitMatchFixture);
        var method = type.GetMethod(nameof(ConduitMatchFixture.Choose), new[] { typeof(int) });
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + query), Is.EqualTo(new[] { method }));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "." + query), Is.EqualTo(new[] { method }));
        Assert.That(ConduitReflect.Method(type.Name, query), Is.EqualTo(method));
        Assert.That(ConduitReflect.Method(member: "ConduitWideMemberProbe( int ) -> int"),
            Is.EqualTo(type.GetMethod(nameof(ConduitMatchFixture.ConduitWideMemberProbe))));
    }

    [TestCase("Choose(int) -> float")]
    [TestCase("Choose(int) ->")]
    [TestCase("Choose(int")]
    [TestCase("Choose->(int)")]
    public void InvalidOrNonmatchingSignaturesDoNotSelectAMethod(string query)
    {
        Assert.That(ReflectionQueryEngine.ResolveTarget(nameof(ConduitMatchFixture) + "::" + query), Is.Empty);
        Assert.That(ViewIlTool.Execute(nameof(ConduitMatchFixture) + "::" + query).outcome, Is.EqualTo(ToolOutcome.Exception));
    }

    [TestCase("RefReturn")]
    [TestCase("RefReadonlyReturn")]
    public void RefReturnSelectorsPreserveTheReturnModifier(string name)
    {
        var type = typeof(ConduitReflectSignatureFixture);
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.That(ReflectionQueryEngine.ResolveTarget(ReflectionMemberFormatter.Selector(method)), Is.EqualTo(new[] { method }));
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + name + "() -> int"), Is.Empty);
    }

    [TestCase("Inspect")]
    [TestCase("Echo")]
    [TestCase("Value")]
    [TestCase("Changed")]
    public void OverridesResolveToTheMostDerivedDeclaration(string name)
    {
        var type = typeof(ConduitOverrideMatchFixture);
        var expected = type.GetMember(name, BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance);
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + name), Is.EqualTo(expected));
    }

    [Test]
    public void AnAccessorAndItsPropertyRemainSeparateCandidates()
    {
        var type = typeof(ConduitOverrideMatchFixture);
        var property = type.GetProperty(nameof(ConduitOverrideMatchFixture.Value))!;
        var matches = ReflectionQueryEngine.ResolveTarget(type.Name + "::alue");
        Assert.That(matches, Has.Member(property));
        Assert.That(matches, Has.Member(property.GetMethod));
    }

    [TestCase("Hidden")]
    [TestCase("GenericHidden")]
    [TestCase("CompositeHidden")]
    public void HiddenMethodsResolveToTheClosestDeclaration(string name)
    {
        var type = typeof(ConduitOverrideMatchFixture);
        var expected = new[]
        {
            type.GetMethod(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public),
            typeof(ConduitOverrideBaseFixture).GetMethod(name),
        };
        Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + name), Is.EqualTo(new[] { expected[0] }));
        foreach (var method in expected)
            Assert.That(ReflectionQueryEngine.ResolveTarget(ReflectionMemberFormatter.Selector(method!)), Is.EqualTo(new[] { method }));
    }

    [Test]
    public void InheritedMethodsWithDifferentReturnTypesRemainSelectable()
    {
        var type = typeof(ConduitOverrideMatchFixture);
        var methods = ReflectionQueryEngine.ResolveTarget(type.Name + "::ReturnOverload");
        Assert.That(methods, Has.Length.EqualTo(2));
        foreach (var method in methods)
            Assert.That(ReflectionQueryEngine.ResolveTarget(ReflectionMemberFormatter.Selector(method)), Is.EqualTo(new[] { method }));
    }

    [Test]
    public void ExplicitInterfaceAccessorsCanBeFoundBySignature()
    {
        var type = typeof(ConduitExplicitMatchFixture);
        var method = type.GetInterfaceMap(typeof(IConduitExplicitMatchFixture)).TargetMethods.Single();
        var signature = ReflectionMemberFormatter.MemberName(method, TypeNameFormat.Qualified);
        Assert.That(ConduitReflect.Method(type.FullName, signature), Is.EqualTo(method));
        Assert.That(ConduitReflect.Method(member: signature), Is.EqualTo(method));
        Assert.That(ReflectionQueryEngine.ResolveTarget(ReflectionMemberFormatter.Selector(method)), Is.EqualTo(new[] { method }));
    }

    [Test]
    public void BothLookupsFindInheritedMembers()
    {
        var method = ConduitReflect.Method(nameof(ConduitReflectDerivedFixture), "ReflectBaseOnlyMethod");
        Assert.That(method.DeclaringType, Is.EqualTo(typeof(ConduitReflectBaseFixture)));
        Assert.That(ReflectionQueryEngine.ResolveTarget(nameof(ConduitReflectDerivedFixture) + "::ReflectBaseOnlyMethod"),
            Is.EqualTo(new[] { method }));
    }

    [TestCase(typeof(System.Collections.ObjectModel.KeyedCollection<,>))]
    [TestCase(typeof(ConduitGenericSelectorFixture))]
    public void InheritedGenericSelectorsResolveTheirMetadataDefinitions(Type type)
    {
        var owner = type.BaseType!;
        var definition = owner.GetGenericTypeDefinition();
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static;
        foreach (var member in owner.GetMembers(flags).Where(member => member is not Type))
        {
            var expected = definition.GetMembers(flags).Single(candidate => candidate.MetadataToken == member.MetadataToken);
            foreach (var format in new[] { SelectorFormat.Qualified, SelectorFormat.AssemblyQualified })
            {
                var selector = ReflectionMemberFormatter.Selector(member, format);
                Assert.That(ReflectionQueryEngine.ResolveTarget(selector), Is.EqualTo(new[] { expected }), selector);
                Assert.That(ViewIlTool.Execute(selector).outcome, Is.EqualTo(ToolOutcome.Success), selector);
            }
        }
    }

    [Test]
    public void InheritedGenericPropertyOffersAWorkingGetterSelector()
    {
        var type = typeof(System.Collections.ObjectModel.KeyedCollection<,>);
        var result = ViewIlTool.Execute(ReflectionMemberFormatter.Selector(type) + "::Count");
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success), result.diagnostic);
        foreach (var selector in result.return_value!.Split('\n').Skip(1))
        {
            var body = ViewIlTool.Execute(selector);
            Assert.That(body.outcome, Is.EqualTo(ToolOutcome.Success), body.diagnostic);
            Assert.That(body.return_value, Does.Contain("stack="));
        }
    }

    [Test]
    public void CaseOnlyFieldAndPropertyNamesCanBeSelectedFromAmbiguityResults()
    {
        var type = typeof(System.Collections.ObjectModel.Collection<>);
        var result = ViewIlTool.Execute(ReflectionMemberFormatter.Selector(type) + "::ITEMS");
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
        var matches = result.diagnostic!.Split('\n').Skip(1)
            .Select(selector => ReflectionQueryEngine.ResolveTarget(selector)).ToArray();
        Assert.That(matches.All(match => match.Length == 1), Is.True);
        Assert.That(matches.SelectMany(match => match).Select(member => member.MemberType),
            Is.EquivalentTo(new[] { MemberTypes.Field, MemberTypes.Property }));
    }

    [TestCase("Run")]
    [TestCase("run")]
    public void ExactCaseSelectsMethodsWithAndWithoutSignatures(string name)
    {
        var type = typeof(ConduitCaseSelectorFixture);
        var method = type.GetMethod(name)!;
        foreach (var query in new[] { name, name + "()", name + "() -> void" })
        {
            Assert.That(ConduitReflect.Method(type.Name, query), Is.EqualTo(method));
            Assert.That(ConduitReflect.Methods(type.Name, query), Has.Length.EqualTo(2));
            Assert.That(ReflectionQueryEngine.ResolveTarget(type.Name + "::" + query), Is.EqualTo(new[] { method }));
        }
        Assert.Throws<InvalidOperationException>(() => ConduitReflect.Method(type.Name, "RUN()"));
    }
}

sealed class ConduitGenericSelectorFixture : System.Collections.ObjectModel.Collection<int> { }

sealed class ConduitCaseSelectorFixture
{
    public void Run() { }
    public void run() { }
}

namespace ConduitMatchingFirst
{
    sealed class LookupFixture<T> { public sealed class Nested { } }
}

namespace ConduitMatchingSecond
{
    sealed class LookupFixture<T> { public sealed class Nested { } }
}

sealed class ConduitMatchFixture
{
    static ConduitMatchFixture() { }
    public ConduitMatchFixture() { }
    public void Update() { }
    public void UpdateCache() { }
    public void FixedUpdate() { }
    public void RefreshCache() { }
    public void DoRefresh() { }
    public int Choose(int value) => value;
    public string Choose(string value) => value;
    public int ChooseCached(int value) => value;
    public int ConduitWideMemberProbe(int value) => value;
    public int ConduitWideMemberProbeCached(int value) => value;
}

sealed class ConduitMatchFixtureExtra { }

class ConduitOverrideBaseFixture
{
    public virtual int Inspect(int value) => value;
    public virtual T Echo<T>(T value) => value;
    public virtual int Value => 1;
    public virtual event Action Changed { add { } remove { } }
    public int Hidden(int value) => value;
    public T GenericHidden<T>(T value) => value;
    public System.Collections.Generic.List<T[]> CompositeHidden<T>(System.Collections.Generic.List<T[]> values) => values;
    public int ReturnOverload(int value) => value;
}

sealed class ConduitOverrideMatchFixture : ConduitOverrideBaseFixture
{
    public override int Inspect(int value) => value + 1;
    public override U Echo<U>(U value) => value;
    public override int Value => 2;
    public override event Action Changed { add { } remove { } }
    public new int Hidden(int value) => value + 1;
    public new U GenericHidden<U>(U value) => value;
    public new System.Collections.Generic.List<U[]> CompositeHidden<U>(System.Collections.Generic.List<U[]> values) => values;
    public new string ReturnOverload(int value) => value.ToString();
}

interface IConduitExplicitMatchFixture
{
    int Value { get; }
}

sealed class ConduitExplicitMatchFixture : IConduitExplicitMatchFixture
{
    int IConduitExplicitMatchFixture.Value => 1;
}
