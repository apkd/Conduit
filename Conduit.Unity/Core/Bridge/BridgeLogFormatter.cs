#nullable enable

using System.Text;

namespace Conduit
{
    static class BridgeLogFormatter
    {
        internal static void Append(
            StringBuilder builder,
            string message,
            string? stackTrace,
            int repeatCount = 1)
        {
            AppendQuotedLines(builder, message);
            if (!string.IsNullOrEmpty(stackTrace))
            {
                AppendSeparator(builder);
                builder.Append(stackTrace);
            }

            if (repeatCount <= 1)
                return;

            AppendSeparator(builder);
            builder.Append("*log repeated ")
                .Append(repeatCount)
                .Append(" times*");
        }

        static void AppendQuotedLines(StringBuilder builder, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            builder.Append("> ");
            for (var index = 0; index < message.Length; index++)
            {
                var character = message[index];
                if (character == '\r')
                    continue;

                builder.Append(character);
                if (character == '\n' && index + 1 < message.Length)
                    builder.Append("> ");
            }
        }

        static void AppendSeparator(StringBuilder builder)
        {
            if (builder.Length > 0)
                builder.Append("\n\n");
        }
    }
}
