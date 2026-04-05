using System;
using System.Text;
using Newtonsoft.Json;

namespace S1DSMod.TextChat
{
    // Shared command strings used by client and server.
    internal static class Cmds
    {
        /// <summary>Client -> Server: JSON ChatSendPayload (text + scope).</summary>
        public const string Send = "textchat_send";
        /// <summary>Server -> Client: JSON ChatRecvPayload (sender + text + scope).</summary>
        public const string Recv = "textchat_recv";
    }

    [Serializable]
    internal sealed class ChatSendPayload
    {
        [JsonProperty("text")] public string Text { get; set; } = string.Empty;
        [JsonProperty("scope")] public string Scope { get; set; } = "global";
    }

    [Serializable]
    internal sealed class ChatRecvPayload
    {
        [JsonProperty("sender")] public string Sender { get; set; } = string.Empty;
        [JsonProperty("text")] public string Text { get; set; } = string.Empty;
        [JsonProperty("scope")] public string Scope { get; set; } = "global";
    }

    internal static class TextChatSanitizer
    {
        public static string Sanitize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(input.Length);
            bool lastWasSpace = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];

                if (c == '\r' || c == '\n' || c == '\t')
                {
                    c = ' ';
                }
                else if (char.IsControl(c))
                {
                    continue;
                }

                if (c == '<' || c == '>')
                {
                    c = ' ';
                }

                if (char.IsWhiteSpace(c))
                {
                    if (lastWasSpace) continue;
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }

            return sb.ToString().Trim();
        }
    }
}
