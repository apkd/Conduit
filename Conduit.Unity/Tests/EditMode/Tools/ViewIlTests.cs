#nullable enable

using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Conduit;
using NUnit.Framework;

public sealed class ViewIlTests
{
    [Test]
    public void TypeOutlineContainsDeclarationsAndLayoutWithoutBodies()
    {
        var outline = Inspect(nameof(ViewIlFixture));
        Assert.That(outline, Does.Contain(nameof(ViewIlFixture.Choose)));
        Assert.That(outline, Does.Contain(nameof(ViewIlFixture.Value)));
        Assert.That(outline, Does.Contain(nameof(ViewIlFixture.Changed)));
        Assert.That(outline, Does.Not.Contain("stack="));
        Assert.That(outline, Does.Not.Contain("get_Value("));
        var layout = Inspect(nameof(ViewIlLayoutFixture));
        Assert.That(layout, Does.Contain("explicit"));
        Assert.That(layout, Does.Contain("offset="));
    }

    [TestCase("ViewIlFixture.InspectBranch")]
    [TestCase("ViewIlFixture::InspectBranch")]
    [TestCase("ViewIlFixture::inspectbra")]
    [TestCase("ViewIlFixture::InspectBranch( int )")]
    public void TargetFormsResolveTheSameBody(string target)
    {
        var output = Inspect(target);
        Assert.That(output, Is.EqualTo(Inspect(ReflectionMemberFormatter.Selector(Method(nameof(ViewIlFixture.InspectBranch))))));
        Assert.That(output, Does.Contain("ret"));
    }

    [Test]
    public void OverloadsReturnSelectorsThatEachResolveOneBody()
    {
        var result = ViewIlTool.Execute(nameof(ViewIlFixture) + "::Choose");
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
        Assert.That(result.diagnostic, Does.Not.Contain("stack="));
        foreach (var selector in result.diagnostic!.Split('\n').Skip(1))
            Assert.That(Inspect(selector), Does.Contain("stack="));
    }

    [Test]
    public void PropertiesAndEventsOfferAccessorsAndFieldsPointToInitializers()
    {
        foreach (var member in new[] { nameof(ViewIlFixture.Value), nameof(ViewIlFixture.Changed) })
        {
            var output = Inspect(nameof(ViewIlFixture) + "::" + member);
            foreach (var selector in output.Split('\n').Skip(1))
                Assert.That(Inspect(selector), Does.Contain("stack="));
        }
        Assert.That(Inspect(nameof(ViewIlFixture) + "::count"), Does.Contain("::.ctor"));
        Assert.That(Inspect(nameof(ViewIlFixture) + "::.ctor"), Does.Contain("stfld"));
    }

    [Test]
    public void InspectionDoesNotRunStaticConstructorsGettersOrMethods()
    {
        foreach (var suffix in new[] { "", "::.ctor", "::.cctor", "::get_Value", "::Fail" })
            Assert.That(Inspect(nameof(ViewIlNeverRunFixture) + suffix), Is.Not.Empty);
    }

    [Test]
    public void GenericTokensResolveInTheirOwningContext()
    {
        var output = Inspect("ViewIlGenericFixture<T>::Map<U>(U)");
        Assert.That(output, Does.Contain("Identity<U>(U)"));
        Assert.That(output, Does.Contain("ViewIlGenericFixture<T>.value"));
        Assert.That(output, Does.Not.Contain("unresolved("));
    }

    [Test]
    public void CollidingShortNamesStayDistinctInSignaturesAndOperands()
    {
        var output = Inspect(nameof(ViewIlFixture) + "::Collisions");
        Assert.That(output, Does.Contain("ViewIlFirst.Value"));
        Assert.That(output, Does.Contain("ViewIlSecond.Value"));
        var result = ViewIlTool.Execute(nameof(ViewIlFixture) + "::CollisionOverload");
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.AmbiguousTarget));
        foreach (var selector in result.diagnostic!.Split('\n').Skip(1))
            Assert.That(Inspect(selector), Does.Contain("ret"));
    }

    [Test]
    public void ManagedBodyIncludesFieldsStringsLocalsAndExceptionRegions()
    {
        var output = Inspect(nameof(ViewIlFixture) + "::Protected");
        foreach (var part in new[] { "try ", "filter ", "finally ", "locals", "stfld", "ldstr", "\\n", "call" })
            Assert.That(output, Does.Contain(part));
        Assert.That(output, Does.Not.Contain("Version="));
    }

    [Test]
    public void NativeAndAbstractMethodsHaveExplicitNoBodyResults()
    {
        Assert.That(Inspect(nameof(ViewIlFixture) + "::Native"), Does.Contain("no IL"));
        Assert.That(Inspect(nameof(ViewIlFixture) + "::Native"), Does.Contain("extern"));
        Assert.That(Inspect(nameof(ViewIlAbstractFixture) + "::Run"), Does.Contain("abstract"));
    }

    [Test]
    public void FunctionPointerCallDisplaysStandaloneSignature()
    {
        var output = Inspect(nameof(ViewIlFixture) + "::Indirect");
        Assert.That(output, Does.Contain("calli"));
        Assert.That(output, Does.Contain("sig("));
    }

    [Test]
    public void ReaderPreservesShortBranchesAndResolvesTheirDestinations()
    {
        foreach (var bytes in new[]
        {
            new byte[] { 0x2b, 1, 0, 0x2a },
            new byte[] { 0x38, 1, 0, 0, 0, 0, 0x2a },
        })
        {
            var instructions = MethodIL.Read(bytes);
            Assert.That(instructions[0].OpCode.Value, Is.EqualTo((short)bytes[0]));
            Assert.That(instructions[0].Operand, Is.EqualTo(instructions.Last().Offset));
        }
        var switched = MethodIL.Read(new byte[] { 0x45, 2, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0x2a });
        Assert.That((int[])switched[0].Operand!, Is.EqualTo(switched.Skip(1).Select(instruction => instruction.Offset)));
    }

    [Test]
    public void ReaderRetainsWideVariableIndicesSignedConstantsAndUnclonableInstructions()
    {
        var variable = MethodIL.Read(new byte[] { 0xfe, 0x09, 0xff, 0xff, 0x2a });
        Assert.That(variable[0].Operand, Is.EqualTo(ushort.MaxValue));
        var constant = MethodIL.Read(new byte[] { 0x1f, 0xff, 0x2a });
        Assert.That(Convert.ToInt32(constant[0].Operand), Is.LessThan(0));
        Assert.That(MethodIL.Read(new byte[] { 0x27, 0, 0, 0, 0 })[0].OpCode, Is.EqualTo(OpCodes.Jmp));
    }

    [TestCase(new byte[] { 0xfe })]
    [TestCase(new byte[] { 0x20, 0 })]
    [TestCase(new byte[] { 0x2b, 0xff })]
    [TestCase(new byte[] { 0x45, 0xff, 0xff, 0xff, 0x7f })]
    public void MalformedBodiesReportTheFailingOffset(byte[] bytes)
        => Assert.That(Assert.Throws<NotSupportedException>(() => MethodIL.Read(bytes))!.Message, Does.Contain("IL_"));

    [Test]
    public void UnavailableMetadataDoesNotDiscardTheInstruction()
    {
        var instruction = new MethodIL.Instruction(0, OpCodes.Call, int.MaxValue);
        var operand = IlFormatter.Resolve(new MethodIL.Resolver(Method(nameof(ViewIlFixture.InspectBranch))), instruction);
        Assert.That(operand, Is.TypeOf<IlFormatter.UnresolvedToken>());
        Assert.That(((IlFormatter.UnresolvedToken)operand!).Value, Is.EqualTo(instruction.Operand));
    }

    static MethodInfo Method(string name) => typeof(ViewIlFixture).GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    static string Inspect(string target)
    {
        var result = ViewIlTool.Execute(target);
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success), result.diagnostic);
        return result.return_value!;
    }
}

sealed class ViewIlFixture
{
    int count = 1;
    public int Value { get => count; set => count = value; }
    public event Action? Changed;
    public int Choose(int value) => value;
    public string Choose(string value) => value;
    public int InspectBranch(int value) => value < 0 ? count : value;
    public object Collisions(ViewIlFirst.Value first, ViewIlSecond.Value second) => first ?? (object)second;
    public object CollisionOverload(ViewIlFirst.Value value) => value;
    public object CollisionOverload(ViewIlSecond.Value value) => value;
    public int Protected(int value)
    {
        try
        {
            if (value < 0)
                throw new ArgumentException("line\nbreak");
            return value + count;
        }
        catch (ArgumentException exception) when (exception.Message.Length > value)
        {
            return count;
        }
        finally
        {
            count++;
            Changed?.Invoke();
        }
    }
    [DllImport("view_il_never_loaded")]
    public static extern int Native();
    public static unsafe int Indirect(delegate*<int, int> operation, int value) => operation(value);
}

sealed class ViewIlGenericFixture<T>
{
    T value = default!;
    public U Map<U>(U input) { GC.KeepAlive(value); return Identity(input); }
    static U Identity<U>(U input) => input;
}

sealed class ViewIlNeverRunFixture
{
    static ViewIlNeverRunFixture() => throw new InvalidOperationException();
    public static int Value => throw new InvalidOperationException();
    public static int Fail() => throw new InvalidOperationException();
}

[StructLayout(LayoutKind.Explicit)]
struct ViewIlLayoutFixture
{
    [FieldOffset(0)] public int First;
    [FieldOffset(4)] public int Second;
}

abstract class ViewIlAbstractFixture { public abstract void Run(); }
namespace ViewIlFirst { sealed class Value { } }
namespace ViewIlSecond { sealed class Value { } }
