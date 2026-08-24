namespace Conduit;

sealed partial class MethodCatalog
{
    internal MethodResolution Resolve(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return MethodResolution.Failed("`methodName` must identify a method.");

        if (resolutionCache.TryGetValue(selector, out var cached))
            return cached;

        var resolved = ResolveUncached(selector);
        return resolved.Target == null
            ? resolved
            : resolutionCache.GetOrAdd(selector, resolved);
    }

    MethodResolution ResolveUncached(string selector)
    {
        if (!TryGetMethodNameRange(selector, out var methodNameStart, out var methodNameLength))
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");
        var lookup = methodBuckets.GetAlternateLookup<ReadOnlySpan<char>>();
        var methodName = selector.AsSpan(methodNameStart, methodNameLength);
        if (!lookup.TryGetValue(methodName, out var bucket)
            && (methodName[0] != '@' || !lookup.TryGetValue(methodName[1..], out bucket)))
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");

        List<MethodTarget>? matches = null;
        List<MethodTarget>? supported = null;
        var end = bucket.Offset + bucket.Count;
        for (var index = bucket.Offset; index < end; index++)
        {
            var method = indexedMethods[index];
            if (!method.Matches(selector))
                continue;

            (matches ??= []).Add(method);
            if (method.UnsupportedReason is null)
                (supported ??= []).Add(method);
        }

        if (matches is not { Count: > 0 })
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");

        if (supported is { Count: 1 })
            return MethodResolution.Succeeded(supported[0]);

        if (supported is not { Count: > 0 })
        {
            var reasons = matches
                .Select(method => $"{method.CanonicalSelector}: {method.UnsupportedReason}")
                .Distinct(StringComparer.Ordinal);
            return MethodResolution.Failed(string.Join("\n", reasons));
        }

        return MethodResolution.Ambiguous(
            "Method selector is ambiguous. Use one of:\n" + string.Join("\n", supported.Select(static method => method.CanonicalSelector))
        );

        static bool TryGetMethodNameRange(
            string selector,
            out int start,
            out int length)
        {
            var parameterStart = selector.IndexOf('(');
            var end = parameterStart < 0 ? selector.Length : parameterStart;
            if (end == 0)
            {
                start = 0;
                length = 0;
                return false;
            }

            var separator = selector.LastIndexOf('.', end - 1);
            if (separator < 0 || separator + 1 == end)
            {
                start = 0;
                length = 0;
                return false;
            }

            start = separator + 1;
            length = end - start;
            return true;
        }
    }
}

