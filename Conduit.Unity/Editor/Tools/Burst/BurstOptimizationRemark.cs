#nullable enable

namespace Conduit
{
    readonly struct BurstOptimizationRemark
    {
        internal readonly string Type;
        internal readonly string Message;
        internal readonly string Pass;
        internal readonly string Reason;
        internal readonly string Function;
        internal readonly string Source;

        internal BurstOptimizationRemark(
            string type,
            string message,
            string pass,
            string reason,
            string function,
            string source)
        {
            Type = type;
            Message = message;
            Pass = pass;
            Reason = reason;
            Function = function;
            Source = source;
        }
    }
}
