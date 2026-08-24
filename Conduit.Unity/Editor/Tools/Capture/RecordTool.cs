#nullable enable

using System;
using System.Threading.Tasks;
using UnityEditor;

namespace Conduit
{
    /// <summary>Coordinates recording sessions across asynchronous tool calls and domain reloads.</summary>
    static class RecordTool
    {
        internal const string ReloadCompletionStateKey = "Conduit.Record.ReloadCompletion";

        static RecordingSession? active;
        static string? completedUncollected;

        internal static async Task<string> ExecuteAsync(string? target, string[] args)
        {
            RestoreReloadCompletion();
            if (active != null)
                return await active.WaitAsync();

            var settings = RecordSettings.Parse(target, args);
            var previousCompletion = completedUncollected;
            RecordingSession? session = null;
            try
            {
                session = await RecordingSession.CreateAsync(settings, OnCompleted);
                active = session;
                session.Start();
            }
            catch (Exception exception)
            {
                active = null;
                session?.Dispose();
                if (previousCompletion != null)
                {
                    completedUncollected = null;
                    SessionState.EraseString(ReloadCompletionStateKey);
                    throw new InvalidOperationException(
                        $"{previousCompletion}\n\nA new recording did not start: {exception.Message}",
                        exception
                    );
                }

                throw;
            }

            completedUncollected = null;
            SessionState.EraseString(ReloadCompletionStateKey);
            var startedSession = session
                                 ?? throw new InvalidOperationException("Recording initialization did not complete.");
            return previousCompletion == null
                ? startedSession.StartedMessage
                : $"{previousCompletion}\n\n{startedSession.StartedMessage}";
        }

        internal static bool CancelWait() => active?.CancelWait() == true;

        internal static string? BuildStatusLine()
        {
            RestoreReloadCompletion();
            return active?.BuildStatusLine();
        }

        static void OnCompleted(RecordingSession session, RecordCompletion completion)
        {
            if (ReferenceEquals(active, session))
                active = null;

            if (!session.ResultClaimed)
            {
                completedUncollected = completion.Message;
                SessionState.SetString(ReloadCompletionStateKey, completion.Message);
            }
        }

        static void RestoreReloadCompletion()
        {
            if (completedUncollected != null)
                return;

            var restored = SessionState.GetString(ReloadCompletionStateKey, string.Empty);
            if (restored.Length == 0)
                return;

            completedUncollected = restored;
        }

        internal sealed class WaitCancelledException : OperationCanceledException { }

    }
}
