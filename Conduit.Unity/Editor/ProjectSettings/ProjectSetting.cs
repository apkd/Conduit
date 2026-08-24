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

    sealed class ProjectSetting
    {
        internal ProjectSetting(
            string key,
            Func<string> read,
            Action<string>? set,
            Action<string>? add,
            Action? remove)
        {
            Key = key;
            CompactKey = ProjectSettingKey.Compact(key);
            Tokens = ProjectSettingKey.Tokens(key);
            Read = read;
            SetValue = set;
            AddValue = add;
            RemoveElement = remove;
        }

        internal string Key { get; }
        internal string CompactKey { get; }
        internal string[] Tokens { get; }
        internal Func<string> Read { get; }
        internal Action<string>? SetValue { get; }
        internal Action<string>? AddValue { get; }
        internal Action? RemoveElement { get; }
        internal ProjectSettingOperations Operations
            => (SetValue == null ? ProjectSettingOperations.None : ProjectSettingOperations.Set)
               | (AddValue == null ? ProjectSettingOperations.None : ProjectSettingOperations.AddElement)
               | (RemoveElement == null ? ProjectSettingOperations.None : ProjectSettingOperations.RemoveElement);
    }

    [Flags]
    enum ProjectSettingOperations
    {
        None = 0,
        Set = 1 << 0,
        AddElement = 1 << 1,
        RemoveElement = 1 << 2,
    }
}
