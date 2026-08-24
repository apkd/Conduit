#nullable enable

namespace Conduit
{
    readonly struct ScoredTarget
    {
        internal readonly int Index;
        internal readonly int Score;

        internal ScoredTarget(int index, int score)
        {
            Index = index;
            Score = score;
        }
    }
}
