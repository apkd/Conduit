#nullable enable

namespace Conduit
{
    enum EditorIdleOwnership { User, Agent }
    enum EditorIdleAvailability { Busy, Idle }

    // input ownership survives reloads; elapsed idle time starts fresh after a reload
    sealed class EditorIdleCloseState
    {
        EditorIdleOwnership ownership;
        double lastActivity;

        internal EditorIdleCloseState(EditorIdleOwnership ownership, double now)
        {
            this.ownership = ownership;
            lastActivity = now;
        }

        internal EditorIdleOwnership Ownership => ownership;

        internal void RecordUserInteraction() => ownership = EditorIdleOwnership.User;

        internal void RecordAgentInteraction(double now)
        {
            ownership = EditorIdleOwnership.Agent;
            Postpone(now);
        }

        internal void Postpone(double now) => lastActivity = now;

        internal bool ShouldClose(double now, double minutes, EditorIdleAvailability availability)
        {
            if (availability == EditorIdleAvailability.Busy || ownership == EditorIdleOwnership.User)
            {
                Postpone(now);
                return false;
            }

            return minutes > 0d && now - lastActivity >= minutes * 60d;
        }
    }
}
