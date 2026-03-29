using System;
using System.Collections.Generic;
using System.IO;
using DedicatedServerMod.API;
using DedicatedServerMod.Server.Player;
using DedicatedServerMod.Shared.Networking;
using FishNet.Connection;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using UnityEngine;
using Player = ScheduleOne.PlayerScripts.Player;

[assembly: MelonInfo(typeof(S1DSMod.TextChat.S1DSTextChatServerMod), "S1DS-TextChat", "1.0.0", "ZackaryH8")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace S1DSMod.TextChat
{
    // ── Shared command strings ────────────────────────────────────────────────
    internal static class Cmds
    {
        /// <summary>Client → Server: JSON ChatSendPayload (text + scope).</summary>
        public const string Send = "textchat_send";
        /// <summary>Server → Client: JSON ChatRecvPayload (sender + text + scope).</summary>
        public const string Recv = "textchat_recv";
    }

    // ── Payload types ─────────────────────────────────────────────────────────
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

    // ── Server configuration ──────────────────────────────────────────────────
    [Serializable]
    internal sealed class ServerSettings
    {
        /// <summary>Radius (Unity units) within which Local messages are delivered.</summary>
        [JsonProperty("localChatRadius")] public float LocalChatRadius { get; set; } = 5f;
        /// <summary>Maximum number of characters in a single message (server-enforced).</summary>
        [JsonProperty("maxMessageLength")] public int MaxMessageLength { get; set; } = 200;
    }

    // ── Server mod ────────────────────────────────────────────────────────────
    public sealed class S1DSTextChatServerMod : ServerMelonModBase
    {
        private PlayerManager _playerManager;
        private ServerSettings _settings = new ServerSettings();
        private float _radiusSq = 5f * 2f;

        // clientId → NetworkConnection (populated on first message from each client)
        private readonly Dictionary<int, NetworkConnection> _connections
            = new Dictionary<int, NetworkConnection>();

        // ── Lifecycle ────────────────────────────────────────────────────────

        public override void OnServerInitialize()
        {
            LoadSettings();
            _playerManager = S1DS.Server.Players;
            CustomMessaging.ServerMessageReceived -= OnServerMessage;
            CustomMessaging.ServerMessageReceived += OnServerMessage;
            LoggerInstance.Msg("S1DS-TextChat server mod initialized.");
        }

        public override void OnServerShutdown()
        {
            CustomMessaging.ServerMessageReceived -= OnServerMessage;
            _connections.Clear();
        }

        // ── Message routing ───────────────────────────────────────────────────

        private void OnServerMessage(NetworkConnection conn, string cmd, string data)
        {
            // Track every connection we hear from so we can route local messages later
            _connections[conn.ClientId] = conn;

            switch (cmd)
            {
                case Cmds.Send:
                    try { HandleChat(conn, data); }
                    catch (Exception ex)
                    { LoggerInstance.Warning($"S1DS-TextChat: chat handler error: {ex.Message}"); }
                    break;
            }
        }

        private void HandleChat(NetworkConnection senderConn, string data)
        {
            var payload = JsonConvert.DeserializeObject<ChatSendPayload>(data);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Text)) return;

            // Resolve sender's display name and ConnectedPlayerInfo
            string senderName = "Unknown";
            ConnectedPlayerInfo senderInfo = null;
            foreach (ConnectedPlayerInfo info in _playerManager.GetConnectedPlayers())
            {
                if (info != null && info.ClientId == senderConn.ClientId)
                {
                    senderInfo = info;
                    senderName = info.DisplayName ?? "Unknown";
                    break;
                }
            }

            // Sanitise length server-side
            string text = payload.Text.Trim();
            if (text.Length > _settings.MaxMessageLength)
                text = text.Substring(0, _settings.MaxMessageLength);

            bool isLocal = payload.Scope == "local";
            var recv = new ChatRecvPayload
            {
                Sender = senderName,
                Text = text,
                Scope = isLocal ? "local" : "global"
            };
            string json = JsonConvert.SerializeObject(recv);

            // ── Global ───────────────────────────────────────────────────────
            if (!isLocal)
            {
                foreach (ConnectedPlayerInfo info in _playerManager.GetConnectedPlayers())
                {
                    if (!TryGetTargetConn(info, senderConn.ClientId, out var targetConn)) continue;
                    CustomMessaging.SendToClient(targetConn, Cmds.Recv, json);
                }
                LoggerInstance.Msg($"[TextChat][G] {senderName}: {text}");
                return;
            }

            // ── Local ────────────────────────────────────────────────────────
            Player senderPlayer = senderInfo?.PlayerInstance;
            if (senderPlayer == null)
            {
                LoggerInstance.Warning(
                    $"S1DS-TextChat: no PlayerInstance for {senderName} " +
                    $"(clientId={senderConn.ClientId}); dropping local message.");
                return;
            }

            Vector3 senderPos = senderPlayer.transform.position;

            foreach (ConnectedPlayerInfo info in _playerManager.GetConnectedPlayers())
            {
                if (!TryGetTargetConn(info, senderConn.ClientId, out NetworkConnection targetConn)) continue;

                Player targetPlayer = info.PlayerInstance;
                if (targetPlayer == null) continue;

                Vector3 targetPos = targetPlayer.transform.position;
                if ((senderPos - targetPos).sqrMagnitude <= _radiusSq)
                    CustomMessaging.SendToClient(targetConn, Cmds.Recv, json);
            }

            LoggerInstance.Msg($"[TextChat][L] {senderName}: {text}");
        }

        // ── Settings ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true (and the connection) for a player that should receive a routed message.
        /// Filters out null, loopback, disconnected, the sender, and untracked connections.
        /// </summary>
        private bool TryGetTargetConn(ConnectedPlayerInfo info, int senderClientId, out NetworkConnection conn)
        {
            conn = null;
            if (info == null || info.IsLoopbackConnection || !info.IsConnected) return false;
            if (info.ClientId == senderClientId) return false; // sender echoes locally
            return _connections.TryGetValue(info.ClientId, out conn);
        }

        private void LoadSettings()
        {
            string path = Path.Combine(MelonEnvironment.UserDataDirectory, "S1DS-TextChat.json");
            try
            {
                if (File.Exists(path))
                {
                    _settings = JsonConvert.DeserializeObject<ServerSettings>(
                        File.ReadAllText(path)) ?? new ServerSettings();
                    _radiusSq = _settings.LocalChatRadius * _settings.LocalChatRadius;
                }
                else
                {
                    _settings = new ServerSettings();
                    File.WriteAllText(path,
                        JsonConvert.SerializeObject(_settings, Formatting.Indented));
                    LoggerInstance.Msg($"S1DS-TextChat: created default settings at {path}");
                }
                _radiusSq = _settings.LocalChatRadius * _settings.LocalChatRadius;
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning(
                    $"S1DS-TextChat: failed to load settings, using defaults: {ex.Message}");
                _settings = new ServerSettings();
            }
        }
    }
}
