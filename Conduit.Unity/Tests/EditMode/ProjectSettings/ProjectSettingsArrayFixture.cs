#nullable enable

using System;
using UnityEngine;

sealed class ProjectSettingsArrayFixture : ScriptableObject
{
    [SerializeField]
    int[] values = { 1 };

    [SerializeField]
    UnityEngine.Object?[] references = Array.Empty<UnityEngine.Object?>();
}
