#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Marks a static <c>void(ProjectSettingsRegistry)</c> provider method.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ConduitProjectSettingsProviderAttribute : Attribute { }
}
