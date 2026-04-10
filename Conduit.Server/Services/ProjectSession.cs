namespace Conduit;

sealed class ProjectSession
{
    readonly Lock gate = new();
    ActiveCommandState? activeCommand;
    bool isReachable;
    int queuedCount;
    string currentStatus = ProjectStatus.Offline;

    public ProjectSession(string projectPath)
    {
        ProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        DisplayName = Path.GetFileName(ProjectPath);
        UnityVersion = string.Empty;
        LastSeenUtc = DateTimeOffset.MinValue;
    }

    public ProjectSession(RecentProjectRecord record)
    {
        ProjectPath = ProjectPathNormalizer.Normalize(record.ProjectPath);
        DisplayName = record.DisplayName;
        UnityVersion = record.UnityVersion;
        LastSeenUtc = record.LastSeenUtc;
    }

    public string ProjectPath { get; }

    public string DisplayName { get; private set; }

    public string UnityVersion { get; private set; }

    public DateTimeOffset LastSeenUtc { get; private set; }

    public bool UpdateMetadata(BridgeProjectHandshake handshake)
    {
        lock (gate)
        {
            var normalizedDisplayName = string.IsNullOrWhiteSpace(handshake.DisplayName)
                ? Path.GetFileName(ProjectPath)
                : handshake.DisplayName;

            var changed = DisplayName != normalizedDisplayName
                          || UnityVersion != handshake.UnityVersion
                          || LastSeenUtc != handshake.LastSeenUtc
                          || !isReachable;

            DisplayName = normalizedDisplayName;
            UnityVersion = handshake.UnityVersion;
            LastSeenUtc = handshake.LastSeenUtc;
            isReachable = true;
            UpdateStatusUnderLock();
            return changed;
        }
    }

    public void MarkReachable(bool reachable)
    {
        lock (gate)
        {
            isReachable = reachable;
            UpdateStatusUnderLock();
        }
    }

    public void IncrementQueuedCount()
    {
        lock (gate)
        {
            queuedCount++;
            UpdateStatusUnderLock();
        }
    }

    public void DecrementQueuedCount()
    {
        lock (gate)
        {
            if (queuedCount > 0)
                queuedCount--;

            UpdateStatusUnderLock();
        }
    }

    public ActiveCommandContext StartCommand(BridgeCommand command)
    {
        lock (gate)
        {
            if (activeCommand is not null)
                throw new InvalidOperationException($"Project '{ProjectPath}' already has an active command.");

            activeCommand = new(command);
            isReachable = true;
            UpdateStatusUnderLock();
            return new(activeCommand);
        }
    }

    public bool WasReachableRecently(DateTimeOffset now, TimeSpan maxAge)
    {
        lock (gate)
            return isReachable
                   && LastSeenUtc != DateTimeOffset.MinValue
                   && now - LastSeenUtc <= maxAge;
    }

    public void FinishCommand(string requestId, bool reachable)
    {
        lock (gate)
        {
            if (activeCommand?.RequestId == requestId)
                activeCommand = null;

            isReachable = reachable;
            UpdateStatusUnderLock();
        }
    }

    public bool CanExpire(DateTimeOffset cutoff)
    {
        lock (gate)
            return !isReachable && activeCommand is null && queuedCount == 0 && LastSeenUtc < cutoff;
    }

    public ProjectListItem ToListItem(DateTimeOffset now)
    {
        lock (gate)
        {
            return new()
            {
                ProjectPath = ProjectPath,
                DisplayName = DisplayName,
                UnityVersion = UnityVersion,
                LastSeenUtc = FormatRelativeLastSeen(LastSeenUtc, now),
                Status = currentStatus,
            };
        }
    }

    public RecentProjectRecord ToRecentProjectRecord()
    {
        lock (gate)
        {
            return new()
            {
                ProjectPath = ProjectPath,
                DisplayName = DisplayName,
                UnityVersion = UnityVersion,
                LastSeenUtc = LastSeenUtc,
            };
        }
    }

    static string FormatRelativeLastSeen(DateTimeOffset lastSeenUtc, DateTimeOffset now)
    {
        if (lastSeenUtc == DateTimeOffset.MinValue)
            return string.Empty;

        var elapsed = now - lastSeenUtc;
        if (elapsed <= TimeSpan.FromMinutes(2))
            return "just now";

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = Math.Max(1, (int)elapsed.TotalDays);
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    void UpdateStatusUnderLock()
    {
        currentStatus = activeCommand is not null || queuedCount > 0
            ? ProjectStatus.ConnectedBusy
            : isReachable
                ? ProjectStatus.ConnectedIdle
                : ProjectStatus.Offline;
    }
}

sealed class ActiveCommandState(BridgeCommand command)
{
    public BridgeCommand Command { get; } = command;

    public string RequestId { get; } = ConduitUtility.CreateRequestId();
}

sealed class ActiveCommandContext(ActiveCommandState activeCommand)
{
    public string RequestId => activeCommand.RequestId;
}
