using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace S1DSMod.TextChat
{
    [Serializable]
    internal sealed class ServerSettings
    {
        /// <summary>Radius (Unity units) within which Local messages are delivered.</summary>
        [JsonProperty("localChatRadius")] public float LocalChatRadius { get; set; } = 5f;
        /// <summary>Maximum number of characters in a single message (server-enforced).</summary>
        [JsonProperty("maxMessageLength")] public int MaxMessageLength { get; set; } = 200;
        /// <summary>Rate-limit window size in seconds for chat/command submissions.</summary>
        [JsonProperty("rateLimitWindowSeconds")] public float RateLimitWindowSeconds { get; set; } = 5f;
        /// <summary>Max non-command chat messages allowed per window per player.</summary>
        [JsonProperty("maxChatMessagesPerWindow")] public int MaxChatMessagesPerWindow { get; set; } = 6;
        /// <summary>Max slash commands allowed per window per player.</summary>
        [JsonProperty("maxCommandsPerWindow")] public int MaxCommandsPerWindow { get; set; } = 4;
        /// <summary>Bypass rate limits for staff-like users when true.</summary>
        [JsonProperty("bypassRateLimitForPrivileged")] public bool BypassRateLimitForPrivileged { get; set; } = false;
        /// <summary>Optional TrustedUniqueId/SteamID allowlist for rate-limit bypass (case-insensitive).</summary>
        [JsonProperty("rateLimitExemptPlayerIds")] public List<string> RateLimitExemptPlayerIds { get; set; } = new List<string>();
    }

    internal sealed class RateLimitState
    {
        public float WindowStart { get; set; }
        public int ChatCount { get; set; }
        public int CommandCount { get; set; }
    }
}
