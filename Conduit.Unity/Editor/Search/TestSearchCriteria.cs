#nullable enable

using System;

namespace Conduit
{
    readonly struct TestSearchCriteria
    {
        readonly string[] nameTokens;

        internal TestSearchCriteria(TestSearchMode mode, string[] nameTokens)
        {
            Mode = mode;
            this.nameTokens = nameTokens;
        }

        internal TestSearchMode Mode { get; }

        internal bool MatchesMode(TestSearchMode candidateMode)
            => Mode == TestSearchMode.Any || Mode == candidateMode;

        internal bool MatchesName(string displayName)
        {
            foreach (var nameToken in nameTokens)
                if (displayName.IndexOf(nameToken, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

            return true;
        }
    }
}
