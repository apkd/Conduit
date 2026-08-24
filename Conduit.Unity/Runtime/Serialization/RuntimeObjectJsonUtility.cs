#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static partial class RuntimeObjectJsonUtility
    {
        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        // cache the ordered serialization view and edit lookup together so reflection runs once per runtime type.
        static readonly ConcurrentDictionary<Type, WritablePropertySet> writablePropertySets = new();

        internal static string ToJson(Object target)
            => target switch
            {
                GameObject gameObject => SerializeGameObject(gameObject),
                Transform transform => SerializeTransform(transform),
                MonoBehaviour or ScriptableObject => JsonUtility.ToJson(target, true),
                _ => SerializeWritableProperties(target),
            };

        internal static string FromJsonOverwrite(Object target, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("JSON payload was empty.");

            var body = UnwrapTarget(RuntimeJsonObject.Parse(json), target.GetType());
            var before = ToJson(target);
            using var pooledRequestedPaths = CollectionPool<HashSet<string>, string>.Get(
                out var requestedPaths
            );
            requestedPaths.Clear();
            switch (target)
            {
                case GameObject gameObject:
                    OverwriteGameObject(gameObject, body, requestedPaths);
                    break;
                case Transform transform:
                    OverwriteTransform(transform, body, requestedPaths);
                    break;
                case MonoBehaviour:
                case ScriptableObject:
                    AddRequestedPaths(body, string.Empty, requestedPaths);
                    JsonUtility.FromJsonOverwrite(body.Source, target);
                    break;
                default:
                    OverwriteWritableProperties(target, body, requestedPaths);
                    break;
            }

            return FormatChanges(before, ToJson(target), requestedPaths);
        }
    }
}
