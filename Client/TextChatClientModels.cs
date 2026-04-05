using System;
using Newtonsoft.Json;

namespace S1DSMod.TextChat
{
    [Serializable]
    internal sealed class ClientSettings
    {
        [JsonProperty("localChatKey")] public string LocalChatKey { get; set; } = "U";
        [JsonProperty("globalChatKey")] public string GlobalChatKey { get; set; } = "Y";
        [JsonProperty("maxMessageLength")] public int MaxMessageLength { get; set; } = 200;
    }

    internal sealed class ChatEntry
    {
        public string Display { get; set; }
        public bool IsLocal { get; set; }
    }

    internal enum InputMode { None, Global, Local }
}
