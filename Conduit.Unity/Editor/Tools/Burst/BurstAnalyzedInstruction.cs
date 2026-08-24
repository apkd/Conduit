#nullable enable

namespace Conduit
{
    readonly struct BurstAnalyzedInstruction
    {
        internal readonly string Mnemonic;
        internal readonly string BranchTarget;
        internal readonly string Source;
        internal readonly BurstInstructionFacts Facts;
        internal readonly bool IsConditionalBranch;
        internal readonly bool IsUnconditionalBranch;

        internal BurstAnalyzedInstruction(
            string mnemonic,
            string branchTarget,
            string source,
            BurstInstructionFacts facts,
            bool isConditionalBranch,
            bool isUnconditionalBranch)
        {
            Mnemonic = mnemonic;
            BranchTarget = branchTarget;
            Source = source;
            Facts = facts;
            IsConditionalBranch = isConditionalBranch;
            IsUnconditionalBranch = isUnconditionalBranch;
        }
    }
}
