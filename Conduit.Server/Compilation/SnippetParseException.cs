namespace Conduit;

sealed class SnippetParseException(int lineNumber, string message)
    : Exception($"execute_code({lineNumber}): {message}");
