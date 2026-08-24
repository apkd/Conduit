#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static partial class RuntimeObjectJsonUtility
    {
        static string SerializeGameObject(GameObject gameObject)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                gameObject.name.Length + gameObject.tag.Length + 128
            );
            builder.Append("{\n  \"name\": ");
            AppendQuoted(builder, gameObject.name);
            builder.Append(",\n  \"activeSelf\": ")
                .Append(gameObject.activeSelf ? "true" : "false")
                .Append(",\n  \"layer\": ")
                .Append(gameObject.layer.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"tag\": ");
            AppendQuoted(builder, gameObject.tag);
            return builder
                .Append(",\n  \"hideFlags\": ")
                .Append(((int)gameObject.hideFlags).ToString(CultureInfo.InvariantCulture))
                .Append("\n}")
                .ToString();
        }

        static string SerializeTransform(Transform transform)
        {
            var localPosition = JsonUtility.ToJson(transform.localPosition, true);
            var localRotation = JsonUtility.ToJson(transform.localRotation, true);
            var localScale = JsonUtility.ToJson(transform.localScale, true);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                localPosition.Length + localRotation.Length + localScale.Length + 96
            );
            builder.Append("{\n  \"localPosition\": ");
            AppendIndented(builder, localPosition, 2);
            builder.Append(",\n  \"localRotation\": ");
            AppendIndented(builder, localRotation, 2);
            builder.Append(",\n  \"localScale\": ");
            AppendIndented(builder, localScale, 2);
            return builder.Append("\n}").ToString();
        }

        static string SerializeWritableProperties(Object target)
        {
            var properties = GetWritablePropertySet(target.GetType()).Ordered;
            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                4 + properties.Length * 32
            );
            var count = 0;
            foreach (var property in properties)
            {
                object? value;
                try
                {
                    value = property.GetValue(target);
                }
                catch
                {
                    continue;
                }

                if (!TrySerializeValue(value, property.PropertyType, out var json))
                    continue;

                builder.Append(count++ == 0 ? "{\n  " : ",\n  ");
                AppendQuoted(builder, property.Name);
                builder.Append(": ");
                AppendIndented(builder, json, 2);
            }

            return count == 0 ? "{}" : builder.Append("\n}").ToString();
        }

        static void OverwriteGameObject(
            GameObject target,
            RuntimeJsonObject json,
            HashSet<string> requestedPaths)
        {
            var name = target.name;
            var activeSelf = target.activeSelf;
            var layer = target.layer;
            var tag = target.tag;
            var hideFlags = target.hideFlags;
            var setName = false;
            var setActiveSelf = false;
            var setLayer = false;
            var setTag = false;
            var setHideFlags = false;
            foreach (var member in json.Members)
            {
                switch (member.Name)
                {
                    case "name":
                    case "m_Name":
                        name = ParseString(member);
                        setName = true;
                        requestedPaths.Add("name");
                        break;
                    case "activeSelf":
                    case "m_IsActive":
                        activeSelf = ParseBoolean(member);
                        setActiveSelf = true;
                        requestedPaths.Add("activeSelf");
                        break;
                    case "layer":
                    case "m_Layer":
                        layer = ParseInt32(member);
                        if (layer is < 0 or > 31)
                            throw new InvalidOperationException("GameObject layer must be between 0 and 31.");
                        setLayer = true;
                        requestedPaths.Add("layer");
                        break;
                    case "tag":
                    case "m_TagString":
                        tag = ParseString(member);
                        target.CompareTag(tag); // validates the tag without mutating the object
                        setTag = true;
                        requestedPaths.Add("tag");
                        break;
                    case "hideFlags":
                    case "m_ObjectHideFlags":
                        hideFlags = (HideFlags)ParseInt32(member);
                        setHideFlags = true;
                        requestedPaths.Add("hideFlags");
                        break;
                    default:
                        throw UnknownProperty(target, member.Name);
                }
            }

            if (setName)
                target.name = name;
            if (setActiveSelf)
                target.SetActive(activeSelf);
            if (setLayer)
                target.layer = layer;
            if (setTag)
                target.tag = tag;
            if (setHideFlags)
                target.hideFlags = hideFlags;
        }

        static void OverwriteTransform(
            Transform target,
            RuntimeJsonObject json,
            HashSet<string> requestedPaths)
        {
            var localPosition = target.localPosition;
            var localRotation = target.localRotation;
            var localScale = target.localScale;
            var setLocalPosition = false;
            var setLocalRotation = false;
            var setLocalScale = false;
            foreach (var member in json.Members)
            {
                switch (member.Name)
                {
                    case "localPosition":
                    case "m_LocalPosition":
                        localPosition = ParseStruct(member, localPosition);
                        setLocalPosition = true;
                        AddRequestedPaths(member, "localPosition", requestedPaths);
                        break;
                    case "localRotation":
                    case "m_LocalRotation":
                        localRotation = ParseStruct(member, localRotation);
                        setLocalRotation = true;
                        AddRequestedPaths(member, "localRotation", requestedPaths);
                        break;
                    case "localScale":
                    case "m_LocalScale":
                        localScale = ParseStruct(member, localScale);
                        setLocalScale = true;
                        AddRequestedPaths(member, "localScale", requestedPaths);
                        break;
                    default:
                        throw UnknownProperty(target, member.Name);
                }
            }

            if (setLocalPosition)
                target.localPosition = localPosition;
            if (setLocalRotation)
                target.localRotation = localRotation;
            if (setLocalScale)
                target.localScale = localScale;
        }

        static void OverwriteWritableProperties(
            Object target,
            RuntimeJsonObject json,
            HashSet<string> requestedPaths)
        {
            var properties = GetWritablePropertySet(target.GetType()).ByName;
            using var pooledEdits = ListPool<(
                PropertyInfo Property,
                object? Before,
                object? After
            )>.Get(out var edits);
            edits.Clear();
            if (edits.Capacity < json.Members.Count)
                edits.Capacity = json.Members.Count;
            foreach (var member in json.Members)
            {
                if (!properties.TryGetValue(member.Name, out var property))
                    throw UnknownProperty(target, member.Name);

                try
                {
                    var before = property.GetValue(target);
                    edits.Add((property, before, ParseValue(member, property.PropertyType, before)));
                    AddRequestedPaths(member, property.Name, requestedPaths);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Could not overwrite runtime property '{property.Name}' on '{target.GetType().Name}'.",
                        exception is TargetInvocationException { InnerException: { } inner }
                            ? inner
                            : exception
                    );
                }
            }

            var applied = 0;
            try
            {
                foreach (var edit in edits)
                {
                    edit.Property.SetValue(target, edit.After);
                    applied++;
                }
            }
            catch (Exception exception)
            {
                for (var index = applied - 1; index >= 0; index--)
                    edits[index].Property.SetValue(target, edits[index].Before);

                var property = edits[Math.Min(applied, edits.Count - 1)].Property;
                throw new InvalidOperationException(
                    $"Could not overwrite runtime property '{property.Name}' on '{target.GetType().Name}'.",
                    exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception
                );
            }
        }
    }
}
