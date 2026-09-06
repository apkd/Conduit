#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Conduit
{
    // command captures share exact deduplication; test ownership is maintained by the Editor adapter
    sealed class CapturedLogEntries
    {
        static readonly Stack<CapturedLogEntries> pool = new();
        readonly Dictionary<Signature, int> indexes = new();
        internal List<Entry> Entries { get; } = new();

        internal static CapturedLogEntries Rent()
        {
            lock (pool)
                return pool.TryPop(out var entries) ? entries : new();
        }

        internal void Release()
        {
            Clear();
            lock (pool)
                pool.Push(this);
        }

        internal int Add(string message, string stackTrace, LogType type)
        {
            if (message.Length == 0 && stackTrace.Length == 0)
                return -1;

            var signature = new Signature(message, stackTrace, type);
            if (indexes.TryGetValue(signature, out var index))
                Entries[index].RepeatCount++;
            else
            {
                index = Entries.Count;
                indexes.Add(signature, index);
                Entries.Add(new(message, stackTrace, type));
            }
            return index;
        }

        internal void Clear()
        {
            indexes.Clear();
            Entries.Clear();
        }

        internal sealed class Entry
        {
            internal Entry(string message, string stackTrace, LogType logType)
            {
                Message = message;
                StackTrace = stackTrace;
                LogType = logType;
            }

            internal string Message { get; }
            internal string StackTrace { get; }
            internal LogType LogType { get; }
            internal int RepeatCount { get; set; } = 1;
        }

        readonly struct Signature : IEquatable<Signature>
        {
            readonly string message;
            readonly string stackTrace;
            readonly LogType type;
            readonly int hashCode;

            internal Signature(string message, string stackTrace, LogType type)
            {
                this.message = message;
                this.stackTrace = stackTrace;
                this.type = type;
                unchecked
                {
                    hashCode = ((StringComparer.Ordinal.GetHashCode(message) * 397)
                                ^ StringComparer.Ordinal.GetHashCode(stackTrace)) * 397 ^ (int)type;
                }
            }

            public bool Equals(Signature other)
                => type == other.type && message == other.message && stackTrace == other.stackTrace;

            public override bool Equals(object? value) => value is Signature other && Equals(other);
            public override int GetHashCode() => hashCode;
        }
    }
}
