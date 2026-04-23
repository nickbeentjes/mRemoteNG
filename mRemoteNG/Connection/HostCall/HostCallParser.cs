using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace mRemoteNG.Connection.HostCall
{
    /// <summary>
    /// Parses host-call tags embedded in text (e.g. Claude responses, session log output).
    /// Tag format: &lt;&lt;host-call&gt;&lt;action&gt;payload&gt;&gt;
    /// </summary>
    public static class HostCallParser
    {
        // Matches: <<host-call><action>payload>>
        private static readonly Regex TagPattern = new Regex(
            @"<<host-call><([\w-]+)>(.*?)>>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// Parses all host-call tags from the given text and returns them as a sequence.
        /// </summary>
        public static IEnumerable<HostCall> Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                yield break;

            foreach (Match match in TagPattern.Matches(text))
            {
                yield return new HostCall(
                    Action: match.Groups[1].Value,
                    Payload: match.Groups[2].Value);
            }
        }

        /// <summary>
        /// Removes all host-call tags from the text, returning clean display text.
        /// </summary>
        public static string Strip(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            return TagPattern.Replace(text, string.Empty);
        }
    }

    /// <summary>
    /// A parsed host-call instruction.
    /// </summary>
    public record HostCall(string Action, string Payload);
}
