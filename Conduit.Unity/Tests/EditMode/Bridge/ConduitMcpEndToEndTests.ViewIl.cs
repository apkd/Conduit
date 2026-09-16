#nullable enable

#if UNITY_EDITOR
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;

public sealed partial class ConduitMcpEndToEndTests
{
    [Test]
    public async Task ViewIl_InspectsTypesAndSelectsOneOverloadThroughMcp()
    {
        var outline = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", nameof(ViewIlFixture))));
        AssertSuccessful(outline, nameof(ViewIlFixture.Choose));
        Assert.That(outline.Text, Does.Not.Contain("stack="));

        var ambiguous = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", nameof(ViewIlFixture) + ".Choose")));
        Assert.That(ambiguous.Text, Does.Contain("Choose(int)"));
        Assert.That(ambiguous.Text, Does.Not.Contain("stack="));

        var body = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", nameof(ViewIlFixture) + "::Choose(int)")));
        AssertSuccessful(body, "stack=", "ret");

        var exact = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", nameof(ConduitMatchFixture) + ".Update")));
        AssertSuccessful(exact, "Update()", "stack=");
        Assert.That(exact.Text, Does.Not.Contain(nameof(ConduitMatchFixture.UpdateCache)));

        var reflected = await client.CallToolAsync(BridgeCommandTypes.ExecuteCode,
            Args(("projectPath", projectPath),
                ("snippet", "return Reflect.Method(\"ConduitMatchFixture\", \"Update\").Name;")));
        AssertSuccessful(reflected, nameof(ConduitMatchFixture.Update));
        Assert.That(reflected.Text, Does.Not.Contain(nameof(ConduitMatchFixture.UpdateCache)));

        var alias = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", "int::GetHashCode() -> int")));
        AssertSuccessful(alias, "GetHashCode", "stack=", "ret");

        const string conversion = "UnityEngine.Vector4::op_Implicit(UnityEngine.Vector4)";
        var conversions = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", conversion)));
        Assert.That(conversions.Text, Does.Contain("-> UnityEngine.Vector2"));
        Assert.That(conversions.Text, Does.Contain("-> UnityEngine.Vector3"));
        Assert.That(conversions.Text, Does.Not.Contain("stack="));
        foreach (var returnType in new[] { "Vector2", "Vector3" })
        {
            var selected = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
                Args(("projectPath", projectPath), ("target", conversion + " -> UnityEngine." + returnType)));
            AssertSuccessful(selected, returnType + " op_Implicit", "stack=", "ret");
        }

        var collection = typeof(System.Collections.ObjectModel.KeyedCollection<,>);
        var getter = ReflectionMemberFormatter.Selector(collection.GetProperty("Count")!.GetMethod!);
        var property = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", ReflectionMemberFormatter.Selector(collection) + "::Count")));
        AssertSuccessful(property, getter);
        var inherited = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
            Args(("projectPath", projectPath), ("target", getter)));
        AssertSuccessful(inherited, "stack=", "ret");

        var owner = ReflectionMemberFormatter.Selector(typeof(System.Collections.ObjectModel.Collection<>));
        foreach (var name in new[] { "Items", "items" })
        {
            var member = await client.CallToolAsync(BridgeCommandTypes.ViewIl,
                Args(("projectPath", projectPath), ("target", owner + "::" + name)));
            AssertSuccessful(member, name);
            Assert.That(member.Text, Does.Not.Contain("Multiple matches"));
        }
    }
}
#endif
