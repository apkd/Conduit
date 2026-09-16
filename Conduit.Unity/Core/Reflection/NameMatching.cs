#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    /// <summary>Applies the same name preference when reflection and Burst tools select a target.</summary>
    static class NameMatching
    {
        internal const int Exact = 0;
        internal const int None = int.MaxValue;

        /// <summary>Orders exact spelling, case-insensitive equality, prefixes, then substrings.</summary>
        internal static int Rank(string name, string query)
        {
            if (query.Length == 0)
                return Exact;
            int offset = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (offset < 0)
                return None;
            if (offset > 0)
                return 3;
            if (name.Length != query.Length)
                return 2;
            return string.Equals(name, query, StringComparison.Ordinal) ? Exact : 1;
        }

        // keep ties so callers can report ambiguity instead of choosing whichever member was scanned first.
        internal static void AddBest<T>(List<T> matches, T candidate, int rank, ref int bestRank)
        {
            if (rank == None || rank > bestRank)
                return;
            if (rank < bestRank)
            {
                matches.Clear();
                bestRank = rank;
            }
            matches.Add(candidate);
        }
    }
}
