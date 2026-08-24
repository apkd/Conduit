#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Conduit
{
    static partial class ProfilerTool
    {
        static bool TryBuildBrowseHierarchy(
            int frameIndex,
            string threadSelector,
            string sort,
            out HierarchyRow root,
            out float frameTimeMs,
            out int aggregateWorkerCount,
            out string threadSummary,
            out string diagnostic
        )
        {
            if (string.Equals(threadSelector, "all_workers", StringComparison.OrdinalIgnoreCase))
                return TryBuildWorkerHierarchy(frameIndex, sort, out root, out frameTimeMs, out aggregateWorkerCount, out threadSummary, out diagnostic);

            root = null!;
            frameTimeMs = 0;
            aggregateWorkerCount = 0;
            threadSummary = string.Empty;
            if (!TryResolveThread(frameIndex, threadSelector, out var thread, out diagnostic))
                return false;

            using var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex,
                thread.Index,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnDontSort,
                false
            );

            if (!hierarchy.valid)
            {
                diagnostic = $"No hierarchy data is available for frame {frameIndex} thread {thread.Index}.";
                return false;
            }

            root = BuildHierarchy(hierarchy, sort);
            frameTimeMs = hierarchy.frameTimeMs;
            diagnostic = string.Empty;
            return true;
        }

        static bool TryBuildWorkerHierarchy(
            int frameIndex,
            string sort,
            out HierarchyRow root,
            out float frameTimeMs,
            out int aggregateWorkerCount,
            out string threadSummary,
            out string diagnostic
        )
        {
            root = CreateWorkerAggregateRoot();
            frameTimeMs = 0;
            aggregateWorkerCount = 0;
            threadSummary = string.Empty;
            using var pooledLabels = ConduitPool.GetPooledList<string>(out var labels);
            for (var threadIndex = 0; ; ++threadIndex)
            {
                ProfilerThreadInfo thread;
                using (var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                {
                    if (!raw.valid)
                        break;

                    thread = new(
                        threadIndex,
                        raw.threadName ?? $"Thread {threadIndex.ToString(CultureInfo.InvariantCulture)}",
                        raw.threadGroupName ?? string.Empty
                    );
                }

                if (!ProfilerThreadLabels.TryParseJobWorkerIndex(thread, out var workerIndex))
                    continue;

                using var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(
                    frameIndex,
                    thread.Index,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnDontSort,
                    false
                );

                if (!hierarchy.valid)
                    continue;

                if (labels.Count == 0)
                    frameTimeMs = hierarchy.frameTimeMs;

                MergeWorkerHierarchy(root, BuildHierarchy(hierarchy, sort));
                labels.Add(ProfilerThreadLabels.FormatWorkerLabel(workerIndex));
            }

            if (labels.Count == 0)
            {
                diagnostic = $"No Job Worker hierarchy data is available for frame {frameIndex}.";
                return false;
            }

            aggregateWorkerCount = labels.Count;
            threadSummary =
                $"Threads: {ProfilerThreadLabels.FormatThreadLabels(labels)}\n" +
                $"Aggregation: {labels.Count.ToString(CultureInfo.InvariantCulture)} Job Worker threads; total/self/GC/calls are summed, mean/min/max use workers containing each path.";
            diagnostic = string.Empty;
            return true;
        }

        static HierarchyRow CreateWorkerAggregateRoot() =>
            new()
            {
                Name = "Job Workers",
                IdentityHash = StableHash("Job Workers[1]"),
            };

        static void MergeWorkerHierarchy(HierarchyRow aggregate, HierarchyRow workerRoot)
        {
            AddWorkerMetrics(aggregate, workerRoot);

            // worker-specific roots are discarded so equivalent job paths merge
            foreach (var child in workerRoot.Children)
                MergeHierarchyRow(aggregate, child);
        }

        static void MergeHierarchyRow(HierarchyRow parent, HierarchyRow source)
        {
            // all worker trees share this index so merging stays linear in the number of samples.
            var childrenByName = parent.MergedChildren ??= new(StringComparer.Ordinal);
            if (!childrenByName.TryGetValue(source.Name, out var target))
            {
                target = new()
                {
                    Parent = parent,
                    Name = source.Name,
                    Depth = parent.Depth + 1,
                    IdentityHash = AppendIdentitySegment(
                        parent.IdentityHash,
                        NormalizeIdentitySegment(source.Name),
                        1,
                        includeSeparator: true
                    ),
                };
                parent.Children.Add(target);
                childrenByName.Add(source.Name, target);
            }

            AddWorkerMetrics(target, source);
            foreach (var child in source.Children)
                MergeHierarchyRow(target, child);
        }

        static void AddWorkerMetrics(HierarchyRow target, HierarchyRow source)
        {
            target.MinTotalMs = target.ContributingWorkerCount == 0
                ? source.TotalMs
                : Math.Min(target.MinTotalMs, source.TotalMs);
            target.MaxTotalMs = Math.Max(target.MaxTotalMs, source.TotalMs);
            target.ContributingWorkerCount++;
            target.TotalMs += source.TotalMs;
            target.SelfMs += source.SelfMs;
            target.GcBytes += source.GcBytes;
            target.Calls += source.Calls;
        }

        static HierarchyRow BuildHierarchy(HierarchyFrameDataView hierarchy, string sort)
            => BuildHierarchyRow(
                hierarchy,
                hierarchy.GetRootItemID(),
                parent: null,
                depth: 0,
                identityHash: 0,
                GetRowComparison(sort),
                knownName: null
            );

        static HierarchyRow BuildHierarchyRow(
            HierarchyFrameDataView hierarchy,
            int itemId,
            HierarchyRow? parent,
            int depth,
            uint identityHash,
            Comparison<HierarchyRow> rowComparison,
            string? knownName
        )
        {
            var name = knownName ?? hierarchy.GetItemName(itemId) ?? "<unnamed>";
            if (parent == null)
                identityHash = AppendIdentitySegment(
                    2166136261,
                    NormalizeIdentitySegment(name),
                    1,
                    includeSeparator: false
                );

            var row = new HierarchyRow
            {
                ItemId = itemId,
                Parent = parent,
                Name = name,
                Depth = depth,
                IdentityHash = identityHash,
                TotalMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnTotalTime),
                SelfMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnSelfTime),
                GcBytes = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnGcMemory),
                Calls = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnCalls),
            };

            using var pooledChildren = ConduitPool.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            if (children.Count > 0)
            {
                using var pooledOccurrences = ConduitPool.GetPooledDictionary<string, int>(out var occurrences);
                foreach (var childId in children)
                {
                    var childName = hierarchy.GetItemName(childId) ?? "<unnamed>";
                    var segment = NormalizeIdentitySegment(childName);
                    occurrences.TryGetValue(segment, out var occurrence);
                    occurrences[segment] = ++occurrence;

                    row.Children.Add(
                        BuildHierarchyRow(
                            hierarchy,
                            childId,
                            row,
                            depth + 1,
                            AppendIdentitySegment(
                                row.IdentityHash,
                                segment,
                                occurrence,
                                includeSeparator: true
                            ),
                            rowComparison,
                            childName
                        )
                    );
                }
            }

            row.Children.Sort(rowComparison);
            return row;
        }

        static void AssignPublicIds(IReadOnlyList<HierarchyRow> rows)
        {
            using var pooledUsed = ConduitPool.GetPooledDictionary<string, int>(out var used);
            foreach (var row in rows)
            {
                var baseId = StableId(row.IdentityHash);
                used.TryGetValue(baseId, out var count);
                used[baseId] = ++count;
                row.PublicId = count == 1 ? baseId : $"{baseId}-{count.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        static HierarchyRow? ResolveRoot(
            HierarchyRow root,
            IReadOnlyList<HierarchyRow> rows,
            string selector,
            List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return root;

            foreach (var row in rows)
                if (string.Equals(row.PublicId, selector, StringComparison.Ordinal))
                    return row;

            if (selector.IndexOf('/') >= 0)
                foreach (var row in rows)
                    if (string.Equals(BuildDisplayPath(row), selector, StringComparison.OrdinalIgnoreCase))
                        return row;

            using var pooledExactMatches = ConduitPool.GetPooledList<HierarchyRow>(out var exactMatches);
            foreach (var row in rows)
                if (string.Equals(row.Name, selector, StringComparison.Ordinal))
                    exactMatches.Add(row);

            if (exactMatches.Count == 0)
                foreach (var row in rows)
                    if (string.Equals(row.Name, selector, StringComparison.OrdinalIgnoreCase))
                        exactMatches.Add(row);

            if (exactMatches.Count == 0)
                return null;

            exactMatches.Sort((left, right) => right.TotalMs.CompareTo(left.TotalMs));
            if (exactMatches.Count > 1)
                warnings.Add("root_matched_multiple_items_used_highest_total_ms");

            return exactMatches[0];
        }

        static void SelectVisibleRows(
            HierarchyRow root,
            int depth,
            string sort,
            bool onlyNonTrivial,
            float frameTimeMs,
            HashSet<HierarchyRow> visibleRows)
        {
            Mark(root, 0);

            bool Mark(HierarchyRow row, int relativeDepth)
            {
                if (relativeDepth >= depth)
                    return false;

                var selfVisible = ReferenceEquals(row, root) || !onlyNonTrivial || IsNonTrivial(row, sort, frameTimeMs);
                var childVisible = false;
                foreach (var child in row.Children)
                    childVisible |= Mark(child, relativeDepth + 1);

                if (!selfVisible && !childVisible)
                    return false;

                visibleRows.Add(row);
                return true;
            }
        }

        static bool IsNonTrivial(HierarchyRow row, string sort, float frameTimeMs)
        {
            var thresholdMs = Math.Max(0.001, frameTimeMs * 0.01);
            return sort switch
            {
                "self_ms"  => row.SelfMs >= thresholdMs,
                "gc_bytes" => row.GcBytes > 0,
                "calls"    => row.Calls > 1 || row.TotalMs >= thresholdMs,
                _          => row.TotalMs >= thresholdMs,
            };
        }

    }
}
