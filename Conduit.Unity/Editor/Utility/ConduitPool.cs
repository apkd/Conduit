#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Pool;

namespace Conduit
{
    static class ConduitPool
    {
        const int MaximumPooledCollectionCapacity = 32 * 1024;

        internal struct PooledListHandle<T> : IDisposable
        {
            List<T>? list;

            internal PooledListHandle(List<T> list) => this.list = list;

            public void Dispose()
            {
                if (list == null)
                    return;

                var rented = list;
                if (rented.Capacity > MaximumPooledCollectionCapacity)
                {
                    rented.Clear();
                    rented.Capacity = 0;
                }

                list = null;
                ListPool<T>.Release(rented);
            }
        }

        internal struct PooledSetHandle<T> : IDisposable
        {
            HashSet<T>? set;

            internal PooledSetHandle(HashSet<T> set) => this.set = set;

            public void Dispose()
            {
                if (set == null)
                    return;

                var rented = set;
                if (rented.EnsureCapacity(0) > MaximumPooledCollectionCapacity)
                {
                    rented.Clear();
                    rented.TrimExcess();
                }

                set = null;
                CollectionPool<HashSet<T>, T>.Release(rented);
            }
        }

        internal struct PooledDictionaryHandle<TKey, TValue> : IDisposable
        {
            Dictionary<TKey, TValue>? dictionary;

            internal PooledDictionaryHandle(Dictionary<TKey, TValue> dictionary)
                => this.dictionary = dictionary;

            public void Dispose()
            {
                if (dictionary == null)
                    return;

                var rented = dictionary;
                if (rented.EnsureCapacity(0) > MaximumPooledCollectionCapacity)
                {
                    rented.Clear();
                    rented.TrimExcess();
                }

                dictionary = null;
                DictionaryPool<TKey, TValue>.Release(rented);
            }
        }

        /// <summary>Rents an empty pooled list.</summary>
        internal static PooledListHandle<T> GetPooledList<T>(out List<T> list)
        {
            _ = ListPool<T>.Get(out list);
            return new(list);
        }

        /// <summary>
        /// Rents an empty pooled hash set.
        /// </summary>
        internal static PooledSetHandle<T> GetPooledSet<T>(out HashSet<T> set)
        {
            _ = CollectionPool<HashSet<T>, T>.Get(out set);
            return new(set);
        }

        /// <summary>Rents an empty pooled dictionary.</summary>
        internal static PooledDictionaryHandle<TKey, TValue> GetPooledDictionary<TKey, TValue>(
            out Dictionary<TKey, TValue> dictionary)
        {
            _ = DictionaryPool<TKey, TValue>.Get(out dictionary);
            return new(dictionary);
        }

        /// <summary>
        /// Rents a pooled <see cref="StringBuilder"/> and clears its contents.
        /// </summary>
        internal static BridgeStringBuilderPool.StringBuilderHandle GetStringBuilder(out StringBuilder builder)
            => BridgeStringBuilderPool.Rent(out builder);

    }
}
