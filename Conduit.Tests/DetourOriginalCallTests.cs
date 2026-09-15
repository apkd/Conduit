namespace Conduit;

public sealed class DetourOriginalCallTests
{
    [Test]
    [Arguments("// @base(arg0)\nreturn arg0;", false)]
    [Arguments("return \"@base(arg0)\";", false)]
    [Arguments("return arg0.@base;", false)]
    [Arguments("return @base(arg0);", true)]
    [Arguments("return $\"{@base(arg0)}\";", true)]
    [Arguments("var f = @base; return f(arg0);", true)]
    [Arguments("int F(int x) => @base(x); return F(arg0);", true)]
    public async Task OnlyOriginalReferencesRequestCloning(string source, bool expected)
        => await Assert.That(DetourOriginalCall.Analyze(ConduitCodeParser.Parse(source))).IsEqualTo(expected);

    [Test]
    [Arguments("static int value = @base(1); return value;")]
    [Arguments("static Func<int,int> value = @base; return value(arg0);")]
    [Arguments("int @base = 1; return @base;")]
    [Arguments("int F(int @base) => @base; return F(arg0);")]
    public async Task OriginalCannotBeShadowedOrUsedBeforeBinding(string source)
        => await Assert.That(() => DetourOriginalCall.Analyze(ConduitCodeParser.Parse(source)))
            .Throws<SnippetParseException>();
}
