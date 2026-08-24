#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed partial class ConduitMcpToolsTests
{
    [Test]
    public void ReflectCommand_Parses()
    {
        var command = ConduitToolRunner.ParseIncomingCommand(BridgeCommandTypes.Reflect);

        Assert.That(command, Is.EqualTo(BridgeCommandKind.Reflect));
    }

    [Test]
    public void ReflectTypes_SearchesByTypeNameAndKind()
    {
        var result = ReflectionTool.Reflect(new[] { "classes", "ConduitReflectDerivedFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("class ConduitReflectDerivedFixture"));
        Assert.That(result.return_value, Does.Contain("Base: ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Contain("Interfaces: ConduitReflectInterfaceFixture"));
        Assert.That(result.return_value, Does.Not.Contain("Types:"));
    }

    [Test]
    public void ReflectModes_AreCaseInsensitive()
    {
        var result = ReflectionTool.Reflect(new[] { "CLASSES", "ConduitReflectDerivedFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("class ConduitReflectDerivedFixture"));
    }

    [Test]
    public void ReflectTypes_FiltersStructsAndEnums()
    {
        var structResult = ReflectionTool.Reflect(new[] { "structs", "ConduitReflectStructFixture", string.Empty });
        var refStructResult = ReflectionTool.Reflect(new[] { "structs", "ConduitReflectRefStructFixture", string.Empty });
        var enumResult = ReflectionTool.Reflect(new[] { "enums", "ConduitReflectEnumFixture", string.Empty });

        Assert.That(structResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(structResult.return_value, Does.Contain("struct ConduitReflectStructFixture"));
        Assert.That(
            refStructResult.return_value,
            Does.Contain("readonly ref struct ConduitReflectRefStructFixture")
        );
        Assert.That(enumResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(enumResult.return_value, Does.Contain("enum ConduitReflectEnumFixture"));
    }

    [Test]
    public void ReflectTypes_FiltersInterfacesAndDelegates()
    {
        var interfaceResult = ReflectionTool.Reflect(new[] { "interfaces", "ConduitReflectInterfaceFixture", string.Empty });
        var delegateResult = ReflectionTool.Reflect(new[] { "delegates", "ConduitReflectDelegateFixture", string.Empty });

        Assert.That(interfaceResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(interfaceResult.return_value, Does.Contain("interface ConduitReflectInterfaceFixture"));
        Assert.That(delegateResult.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(delegateResult.return_value, Does.Contain("delegate ConduitReflectDelegateFixture"));
    }

    [Test]
    public void ReflectTypes_SearchesByDirectMemberName()
    {
        var result = ReflectionTool.Reflect(new[] { "types", string.Empty, "ReflectBaseOnlyMethod" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Not.Contain("ConduitReflectDerivedFixture"));
    }

    [Test]
    public void ReflectMembers_TargetTypeIncludesDeclaredAndInheritedMembers()
    {
        var result = ReflectionTool.Reflect(new[] { "members", "ConduitReflectDerivedFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("Declared on ConduitReflectDerivedFixture"));
        Assert.That(result.return_value, Does.Contain("int derivedPrivateField"));
        Assert.That(result.return_value, Does.Not.Contain("private int derivedPrivateField"));
        Assert.That(result.return_value, Does.Contain("public string DerivedProperty { get; private set; }"));
        Assert.That(result.return_value, Does.Contain("public T GenericMethod<T>(ref int value, out string text, params T[] items)"));
        Assert.That(result.return_value, Does.Contain("Inherited from ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Contain("protected string ReflectBaseOnlyMethod()"));
        Assert.That(result.return_value, Does.Contain("Interface ConduitReflectInterfaceFixture"));
        Assert.That(result.return_value, Does.Not.Contain("System.Object"));
    }

    [Test]
    public void ReflectMethods_PreservesAdvancedSignaturesAndMarksOnlyUnsupportedMethods()
    {
        var result = ReflectionTool.Reflect(new[] { "methods", "ConduitReflectSignatureFixture", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("static ref int RefReturn()"));
        Assert.That(result.return_value, Does.Contain("static ref readonly int RefReadonlyReturn()"));
        Assert.That(result.return_value, Does.Contain("Span<int> values"));
        Assert.That(result.return_value, Does.Contain("int* pointer"));
        Assert.That(result.return_value, Does.Contain("delegate*<int, int> managed"));
        Assert.That(result.return_value, Does.Contain("delegate* unmanaged[Cdecl]<int, int> native"));
        Assert.That(result.return_value, Does.Contain("in int input"));
        Assert.That(result.return_value, Does.Contain("ref int reference"));
        Assert.That(result.return_value, Does.Contain("out int output"));
        Assert.That(
            result.return_value,
            Does.Contain(
                "static ConduitReflectSignatureFixture.@class @event("
                + "ConduitReflectSignatureFixture.@class @this)"
            )
        );
        Assert.That(result.return_value, Does.Contain("Generic<T>() // detour-incompatible"));
        Assert.That(result.return_value, Does.Contain("Native() // detour-incompatible"));
        Assert.That(result.return_value, Does.Not.Contain("private static"));
        const string supportedSignature = "static unsafe Span<int> SpanAndPointers(Span<int> values, int* pointer, delegate*<int, int> managed, delegate* unmanaged[Cdecl]<int, int> native)";
        Assert.That(result.return_value, Does.Contain(supportedSignature));
        Assert.That(result.return_value, Does.Not.Contain(supportedSignature + " // detour-incompatible"));
        Assert.That(
            result.return_value,
            Does.Not.Contain(
                "@event(ConduitReflectSignatureFixture.@class @this) // detour-incompatible"
            )
        );
    }

    [Test]
    public void ReflectFieldsAndPropertiesPreserveUnsafeAndRefReturnDeclarations()
    {
        var fields = ReflectionTool.Reflect(
            new[] { "fields", "ConduitReflectSignatureFixture", "operation" }
        );
        var properties = ReflectionTool.Reflect(
            new[] { "properties", "ConduitReflectSignatureFixture", "Property" }
        );

        Assert.That(fields.return_value, Does.Contain("static unsafe delegate*<int, int> operation"));
        Assert.That(properties.return_value, Does.Contain("static ref int RefProperty { get; }"));
        Assert.That(
            properties.return_value,
            Does.Contain("static ref readonly int RefReadonlyProperty { get; }")
        );
        Assert.That(
            properties.return_value,
            Does.Contain("static unsafe int* PointerProperty { get; }")
        );
    }

    [Test]
    public void ReflectInterfaceMethods_OmitImplicitAccessAndAbstractModifiers()
    {
        var result = ReflectionTool.Reflect(new[] { "methods", "ConduitReflectInterfaceFixture", string.Empty });

        Assert.That(result.return_value, Does.Contain("void ReflectInterfaceMethod() // detour-incompatible"));
        Assert.That(result.return_value, Does.Not.Contain("public abstract void ReflectInterfaceMethod"));
    }

    [Test]
    public void ReflectConstructorsOmitImplicitPrivateAndMarkThemIncompatible()
    {
        var result = ReflectionTool.Reflect(
            new[] { "constructors", "ConduitReflectDerivedFixture", string.Empty }
        );

        Assert.That(
            result.return_value,
            Does.Contain("public ConduitReflectDerivedFixture() // detour-incompatible")
        );
        Assert.That(
            result.return_value,
            Does.Contain("static ConduitReflectDerivedFixture() // detour-incompatible")
        );
        Assert.That(result.return_value, Does.Not.Contain("private static"));
    }

    [Test]
    public void ReflectMembers_WideSearchUsesDirectContainingTypeOnly()
    {
        var result = ReflectionTool.Reflect(new[] { "methods", string.Empty, "ReflectBaseOnlyMethod" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("Containing Type: ConduitReflectBaseFixture"));
        Assert.That(result.return_value, Does.Not.Contain("Containing Type: ConduitReflectDerivedFixture"));
    }

    [Test]
    public void ReflectMembers_AmbiguousTypeReturnsCandidates()
    {
        var result = ReflectionTool.Reflect(new[] { "members", "ConduitReflectAmbiguous", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
        Assert.That(result.diagnostic, Does.Contain("Multiple types match"));
        Assert.That(result.diagnostic, Does.Contain("ConduitReflectAmbiguousAlpha"));
        Assert.That(result.diagnostic, Does.Contain("ConduitReflectAmbiguousBeta"));
    }

    [Test]
    public void ReflectMembers_WideSearchTruncatesAtTwoHundredRows()
    {
        var result = ReflectionTool.Reflect(new[] { "members", string.Empty, "ToString" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Does.Contain("showing 200"));
        Assert.That(result.return_value, Does.Contain("Truncated:"));
    }

    [Test]
    public void ReflectMembers_WideSearchRanksExactMatchesBeforeSubstringMatches()
    {
        var result = ReflectionTool.Reflect(new[] { "methods", string.Empty, "ReflectRank" });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        var text = result.return_value!;
        var exactIndex = text.IndexOf("public void ReflectRank()", StringComparison.Ordinal);
        var looseIndex = text.IndexOf("public void PrefixReflectRankSuffix()", StringComparison.Ordinal);

        Assert.That(exactIndex, Is.GreaterThanOrEqualTo(0), result.return_value);
        Assert.That(looseIndex, Is.GreaterThanOrEqualTo(0), result.return_value);
        Assert.That(exactIndex, Is.LessThan(looseIndex), result.return_value);
    }

    [Test]
    public void ReflectMembers_NonTruncatedSearchOmitsHeaderAndNoMatchesAreExplicit()
    {
        var matched = ReflectionTool.Reflect(new[] { "methods", string.Empty, "ReflectBaseOnlyMethod" });
        var noMatch = ReflectionTool.Reflect(new[] { "methods", string.Empty, "DefinitelyNotAConduitReflectMember" });

        Assert.That(matched.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(matched.return_value, Does.Not.Contain("Members:"));
        Assert.That(noMatch.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(noMatch.return_value, Is.EqualTo("No members matched."));
    }

    [Test]
    public void ReflectTypes_NoMatchesAreExplicit()
    {
        var result = ReflectionTool.Reflect(new[] { "types", "DefinitelyNotAConduitReflectType", string.Empty });

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(result.return_value, Is.EqualTo("No types matched."));
    }

    [Test]
    public void ConduitReflect_TypeHelpersReturnTypedResults()
    {
        var type = ConduitReflect.Type("ConduitReflectDerivedFixture");
        var interfaces = ConduitReflect.Interfaces("ConduitReflectInterfaceFixture");

        Assert.That(type, Is.EqualTo(typeof(ConduitReflectDerivedFixture)));
        Assert.That(interfaces, Is.EqualTo(new[] { typeof(ConduitReflectInterfaceFixture) }));
    }

    [Test]
    public void ConduitReflect_TypeSearchCanUseMemberQuery()
    {
        var types = ConduitReflect.Types(member: "ReflectBaseOnlyMethod");

        Assert.That(types, Has.Member(typeof(ConduitReflectBaseFixture)));
        Assert.That(types, Has.No.Member(typeof(ConduitReflectDerivedFixture)));
    }

    [Test]
    public void ConduitReflect_MemberHelpersReturnTypedResults()
    {
        var baseMethod = ConduitReflect.Method(type: "ConduitReflectDerivedFixture", member: "ReflectBaseOnlyMethod");
        var constructors = ConduitReflect.Constructors("ConduitReflectDerivedFixture");

        Assert.That(baseMethod.DeclaringType, Is.EqualTo(typeof(ConduitReflectBaseFixture)));
        Assert.That(constructors, Has.Length.GreaterThanOrEqualTo(1));
        Assert.That(constructors, Has.Some.Property(nameof(ConstructorInfo.DeclaringType)).EqualTo(typeof(ConduitReflectDerivedFixture)));
    }

    [Test]
    public void ConduitReflect_FindHandlesCardinalityAndCompatibility()
    {
        var manyMethods = ConduitReflect.FindMany<MethodInfo>("members", type: "ConduitReflectDerivedFixture", member: "Reflect");
        var ambiguous = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Type("ConduitReflectAmbiguous"));
        var missing = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Type("DefinitelyNotAConduitReflectType"));
        var invalidType = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Find<MethodInfo>("fields", type: "ConduitReflectDerivedFixture"));
        var empty = Assert.Throws<InvalidOperationException>(() => ConduitReflect.Types());

        Assert.That(manyMethods, Is.Not.Empty);
        Assert.That(ambiguous!.Message, Does.Contain("Multiple reflected results match"));
        Assert.That(ambiguous.Message, Does.Contain("ConduitReflectAmbiguousAlpha"));
        Assert.That(missing!.Message, Does.Contain("No reflected result matched"));
        Assert.That(invalidType!.Message, Does.Contain("cannot return MethodInfo"));
        Assert.That(empty!.Message, Does.Contain("reflect type modes require"));
    }
}
