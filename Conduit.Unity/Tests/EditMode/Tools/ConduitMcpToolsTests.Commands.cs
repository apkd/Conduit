#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed partial class ConduitMcpToolsTests
{
    [Test]
    public void SharedStatusDurationUsesTwoSignificantUnits()
    {
        Assert.That(
            BridgeStatusUtility.FormatDuration(new TimeSpan(1, 2, 3, 4)),
            Is.EqualTo("1 day 2 hours")
        );
        Assert.That(
            BridgeStatusUtility.FormatDuration(TimeSpan.FromSeconds(61)),
            Is.EqualTo("1 minute 1 second")
        );
    }
}
