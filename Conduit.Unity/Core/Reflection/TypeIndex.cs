#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;

namespace Conduit
{
    sealed class TypeIndex : IReadOnlyList<Type>
    {
        readonly Type[] types;
        internal readonly TypeSearchInfo[] SearchInfos;

        internal TypeIndex(Type[] types, TypeSearchInfo[] searchInfos)
        {
            this.types = types;
            SearchInfos = searchInfos;
        }

        public int Count => types.Length;
        public Type this[int index] => types[index];
        public IEnumerator<Type> GetEnumerator() => ((IEnumerable<Type>)types).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => types.GetEnumerator();
    }

}
