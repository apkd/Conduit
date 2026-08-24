#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace Conduit
{
    static class MaterialShaderPropertyCatalog
    {
        internal static void GetPropertyTypes(
            Material target,
            Dictionary<string, ShaderPropertyType> propertyTypes)
        {
            if (target.shader == null)
                throw new InvalidOperationException("Material overwrite could not resolve a shader for the target material.");

            var shader = target.shader;
            var propertyCount = shader.GetPropertyCount();
            for (var index = 0; index < propertyCount; index++)
                propertyTypes[shader.GetPropertyName(index)] = shader.GetPropertyType(index);
        }

        internal static void ValidatePropertyType(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            string propertyName,
            string label,
            ShaderPropertyType supportedType,
            ShaderPropertyType? alternateSupportedType = null)
        {
            if (!shaderPropertyTypes.TryGetValue(propertyName, out var propertyType))
                throw new InvalidOperationException($"Material overwrite does not support {label} property '{propertyName}'.");

            if (propertyType == supportedType || propertyType == alternateSupportedType)
                return;

            throw new InvalidOperationException($"Material overwrite does not support {label} property '{propertyName}'.");
        }
    }
}
