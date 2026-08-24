namespace Conduit;

enum MetadataUnsupportedReason : byte
{
    None,
    Constructor,
    Generic,
    PInvoke,
    Runtime,
    NoBody,
    VarArgs,
}
