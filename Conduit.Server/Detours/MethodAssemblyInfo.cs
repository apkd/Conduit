namespace Conduit;

sealed class MethodAssemblyInfo(Guid moduleVersionId, string name, string path)
{
    internal Guid ModuleVersionId { get; } = moduleVersionId;
    internal string Name { get; } = name;
    internal string Path { get; } = path;
}
