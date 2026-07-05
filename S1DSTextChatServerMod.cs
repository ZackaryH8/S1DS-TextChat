using System;
using System.Collections.Generic;
using System.IO;
using DedicatedServerMod.API;
using DedicatedServerMod.API.Metadata;
using DedicatedServerMod.Server.Commands.Output;
using DedicatedServerMod.Server.Core;
using DedicatedServerMod.Server.Player;
using DedicatedServerMod.Shared.Networking;
using DedicatedServerMod.Shared.Permissions;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using UnityEngine;
#if IL2CPP
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Player = Il2CppScheduleOne.PlayerScripts.Player;
#else
using FishNet;
using FishNet.Connection;
using Player = ScheduleOne.PlayerScripts.Player;
#endif

[assembly: MelonInfo(typeof(S1DSMod.TextChat.S1DSTextChatServerMod), "S1DS-TextChat", "1.1.0", "ZackaryH8")]
[assembly: S1DSClientCompanion(
    modId: "zackaryh8.textchat",
    displayName: "Text Chat",
    Required = true,
    MinVersion = "1.1.0")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace S1DSMod.TextChat
{
    // ── Server mod ────────────────────────────────────────────────────────────
    public sealed class S1DSTextChatServerMod : ServerMelonModBase
    {
        private PlayerManager _playerManager;
        private ServerSettings _settings = new ServerSettings();
        private float _radiusSq = 5f * 5f;

        private readonly Dictionary<int, RateLimitState> _rateLimits
            = new Dictionary<int, RateLimitState>();
        private readonly Dictionary<string, DateTime> _mutedUntilByPlayerId
            = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static class TextChatPermissionNodes
        {
            public const string Moderate = "textchat.moderate";
            public const string RateLimitBypass = "textchat.ratelimit.bypass";
        }

        // ── Lifecycle ────────────────────────────────────────────────────────

        public override void OnServerInitialize()
        {
            LoadSettings();
            _playerManager = S1DS.Server.Players;
            RegisterPermissionNodes();
            CustomMessaging.ServerMessageReceived -= OnServerMessage;
            CustomMessaging.ServerMessageReceived += OnServerMessage;
            LoggerInstance.Msg("S1DS-TextChat server mod initialized.");
        }

        private void RegisterPermissionNodes()
        {
            var permissions = S1DS.Server.Permissions;
            if (permissions == null) return;

            permissions.RegisterPermissionDefinitions("zackaryh8.textchat", new[]
            {
                new PermissionDefinition
                {
                    Node = TextChatPermissionNodes.Moderate,
                    Category = "TextChat",
                    Description = "Mute and unmute chat participants via /mute and /unmute.",
                    SuggestedGroups =
                    {
                        PermissionBuiltIns.Groups.Moderator,
                        PermissionBuiltIns.Groups.Administrator,
                        PermissionBuiltIns.Groups.Operator,
                    },
                },
                new PermissionDefinition
                {
                    Node = TextChatPermissionNodes.RateLimitBypass,
                    Category = "TextChat",
                    Description = "Bypass chat and command rate limits.",
                    SuggestedGroups =
                    {
                        PermissionBuiltIns.Groups.Moderator,
                        PermissionBuiltIns.Groups.Administrator,
                        PermissionBuiltIns.Groups.Operator,
                    },
                },
            });
        }

        public override void OnServerShutdown()
        {
            CustomMessaging.ServerMessageReceived -= OnServerMessage;
            _rateLimits.Clear();
            _mutedUntilByPlayerId.Clear();
        }

        // ── Message routing ───────────────────────────────────────────────────

        private void OnServerMessage(NetworkConnection conn, string cmd, string data)
        {
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
                    senderName = TextChatSanitizer.Sanitize(info.DisplayName ?? "Unknown");
                    break;
                }
            }

            // Sanitize and validate message text server-side.
            string text = TextChatSanitizer.Sanitize(payload.Text);
            if (string.IsNullOrWhiteSpace(text)) return;

            if (senderInfo != null && IsMuted(senderInfo, out DateTime mutedUntilUtc))
            {
                TimeSpan remaining = mutedUntilUtc - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                SendServerLine(
                    senderConn,
                    $"You are muted for another {FormatDuration(remaining)}.",
                    isError: true);
                return;
            }

            int maxLen = Math.Max(1, _settings.MaxMessageLength);
            if (text.Length > maxLen)
            {
                SendServerLine(senderConn, $"Message too long ({text.Length}/{maxLen}).");
                return;
            }

            bool isCommand = text.StartsWith("/", StringComparison.Ordinal);
            if (TryConsumeRateLimit(senderConn.ClientId, senderInfo, isCommand, out string limitReason))
            {
                SendServerLine(senderConn, limitReason);
                return;
            }

            // Slash-prefixed input is treated as a server command and is not broadcast as chat.
            if (isCommand)
            {
                HandleChatCommand(senderConn, senderInfo, text);
                return;
            }

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

        private void HandleChatCommand(NetworkConnection senderConn, ConnectedPlayerInfo senderInfo, string text)
        {
            string commandLine = text.Substring(1).Trim();
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                SendServerLine(senderConn, "Usage: /<command> [args]", isError: true);
                return;
            }

            if (ServerBootstrap.Commands == null)
            {
                SendServerLine(senderConn, "Command system is not available.", isError: true);
                return;
            }

            if (TryHandleBuiltInModerationCommand(senderConn, senderInfo, commandLine))
            {
                return;
            }

            ChatCommandOutput output = new ChatCommandOutput(this, senderConn);
            ServerBootstrap.Commands.ExecuteConsoleLine(commandLine, output, senderInfo);
        }

        private bool TryHandleBuiltInModerationCommand(
            NetworkConnection senderConn,
            ConnectedPlayerInfo senderInfo,
            string commandLine)
        {
            string[] parts = commandLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            string command = parts[0];
            if (string.Equals(command, "mute", StringComparison.OrdinalIgnoreCase))
            {
                if (!CanModerateMutes(senderInfo))
                {
                    SendServerLine(senderConn, "You do not have permission to use /mute.", isError: true);
                    return true;
                }

                if (parts.Length < 3)
                {
                    SendServerLine(senderConn, "Usage: /mute <player> <duration>", isError: true);
                    SendServerLine(senderConn, "Examples: /mute Zack 1h, /mute 7656119... 30m");
                    return true;
                }

                string durationToken = parts[parts.Length - 1];
                string targetQuery = string.Join(" ", parts, 1, parts.Length - 2).Trim();
                if (!TryParseDuration(durationToken, out TimeSpan duration))
                {
                    SendServerLine(senderConn, "Invalid duration. Use values like 30s, 10m, 1h, 2d.", isError: true);
                    return true;
                }

                ConnectedPlayerInfo target = FindConnectedPlayer(targetQuery);
                if (target == null)
                {
                    SendServerLine(senderConn, $"Player '{targetQuery}' not found (or ambiguous).", isError: true);
                    return true;
                }

                string targetId = (target.TrustedUniqueId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(targetId))
                {
                    SendServerLine(senderConn, "Target has no stable player ID; cannot mute.", isError: true);
                    return true;
                }

                DateTime until = DateTime.UtcNow.Add(duration);
                _mutedUntilByPlayerId[targetId] = until;
                SendServerLine(senderConn,
                    $"Muted {TextChatSanitizer.Sanitize(target.DisplayName ?? "Unknown")} for {FormatDuration(duration)}.");
                return true;
            }

            if (string.Equals(command, "unmute", StringComparison.OrdinalIgnoreCase))
            {
                if (!CanModerateMutes(senderInfo))
                {
                    SendServerLine(senderConn, "You do not have permission to use /unmute.", isError: true);
                    return true;
                }

                if (parts.Length < 2)
                {
                    SendServerLine(senderConn, "Usage: /unmute <player>", isError: true);
                    return true;
                }

                string targetQuery = string.Join(" ", parts, 1, parts.Length - 1).Trim();
                ConnectedPlayerInfo target = FindConnectedPlayer(targetQuery);
                if (target == null)
                {
                    SendServerLine(senderConn, $"Player '{targetQuery}' not found (or ambiguous).", isError: true);
                    return true;
                }

                string targetId = (target.TrustedUniqueId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(targetId) || !_mutedUntilByPlayerId.Remove(targetId))
                {
                    SendServerLine(senderConn,
                        $"{TextChatSanitizer.Sanitize(target.DisplayName ?? "Unknown")} is not currently muted.");
                    return true;
                }

                SendServerLine(senderConn,
                    $"Unmuted {TextChatSanitizer.Sanitize(target.DisplayName ?? "Unknown")}.");
                return true;
            }

            return false;
        }

        private bool CanModerateMutes(ConnectedPlayerInfo info)
        {
            return HasTextChatPermission(info, TextChatPermissionNodes.Moderate);
        }

        private ConnectedPlayerInfo FindConnectedPlayer(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;

            string normalized = query.Trim();
            ConnectedPlayerInfo partial = null;

            foreach (ConnectedPlayerInfo info in _playerManager.GetConnectedPlayers())
            {
                if (info == null) continue;

                string id = (info.TrustedUniqueId ?? string.Empty).Trim();
                string name = TextChatSanitizer.Sanitize(info.DisplayName ?? string.Empty);

                if (string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return info;
                }

                if (name.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (partial != null)
                    {
                        return null; // ambiguous partial match
                    }
                    partial = info;
                }

                if (id.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (partial != null)
                    {
                        return null; // ambiguous partial match
                    }
                    partial = info;
                }
            }

            return partial;
        }

        private bool IsMuted(ConnectedPlayerInfo info, out DateTime mutedUntilUtc)
        {
            mutedUntilUtc = DateTime.MinValue;
            if (info == null) return false;

            string playerId = (info.TrustedUniqueId ?? string.Empty).Trim();
            if (playerId.Length == 0) return false;

            if (!_mutedUntilByPlayerId.TryGetValue(playerId, out mutedUntilUtc))
            {
                return false;
            }

            if (DateTime.UtcNow >= mutedUntilUtc)
            {
                _mutedUntilByPlayerId.Remove(playerId);
                mutedUntilUtc = DateTime.MinValue;
                return false;
            }

            return true;
        }

        private static bool TryParseDuration(string token, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(token)) return false;

            string value = token.Trim().ToLowerInvariant();
            if (value.EndsWith("hours", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 5) + "h";
            else if (value.EndsWith("hour", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 4) + "h";
            else if (value.EndsWith("hrs", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 3) + "h";
            else if (value.EndsWith("hr", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 2) + "h";
            else if (value.EndsWith("minutes", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 7) + "m";
            else if (value.EndsWith("minute", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 6) + "m";
            else if (value.EndsWith("mins", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 4) + "m";
            else if (value.EndsWith("min", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 3) + "m";
            else if (value.EndsWith("seconds", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 7) + "s";
            else if (value.EndsWith("second", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 6) + "s";
            else if (value.EndsWith("secs", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 4) + "s";
            else if (value.EndsWith("sec", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 3) + "s";

            if (value.Length < 2) return false;

            char suffix = value[value.Length - 1];
            string numericPart = value.Substring(0, value.Length - 1);
            if (!double.TryParse(numericPart, out double amount)) return false;
            if (amount <= 0) return false;

            switch (suffix)
            {
                case 's':
                    duration = TimeSpan.FromSeconds(amount);
                    break;
                case 'm':
                    duration = TimeSpan.FromMinutes(amount);
                    break;
                case 'h':
                    duration = TimeSpan.FromHours(amount);
                    break;
                case 'd':
                    duration = TimeSpan.FromDays(amount);
                    break;
                default:
                    return false;
            }

            // Guard against extreme values / accidental huge mutes.
            if (duration > TimeSpan.FromDays(3650)) return false;
            return duration > TimeSpan.Zero;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays >= 1d) return $"{Math.Ceiling(duration.TotalDays):0}d";
            if (duration.TotalHours >= 1d) return $"{Math.Ceiling(duration.TotalHours):0}h";
            if (duration.TotalMinutes >= 1d) return $"{Math.Ceiling(duration.TotalMinutes):0}m";
            return $"{Math.Ceiling(duration.TotalSeconds):0}s";
        }

        private void SendServerLine(NetworkConnection targetConn, string line, bool isError = false)
        {
            if (targetConn == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            ChatRecvPayload recv = new ChatRecvPayload
            {
                Sender = "Server",
                Text = TextChatSanitizer.Sanitize(line),
                Scope = "global"
            };

            CustomMessaging.SendToClient(targetConn, Cmds.Recv, JsonConvert.SerializeObject(recv));
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

            var clientManager = InstanceFinder.ClientManager;
            if (clientManager?.Clients == null) return false;
            return clientManager.Clients.TryGetValue(info.ClientId, out conn);
        }

        private bool TryConsumeRateLimit(int clientId, ConnectedPlayerInfo senderInfo, bool isCommand, out string reason)
        {
            reason = string.Empty;

            if (_settings.BypassRateLimitForPrivileged && IsPrivileged(senderInfo))
            {
                return false;
            }

            float windowSec = Mathf.Max(0.25f, _settings.RateLimitWindowSeconds);
            int chatMax = Mathf.Max(1, _settings.MaxChatMessagesPerWindow);
            int cmdMax = Mathf.Max(1, _settings.MaxCommandsPerWindow);
            float now = Time.realtimeSinceStartup;

            if (!_rateLimits.TryGetValue(clientId, out RateLimitState state))
            {
                state = new RateLimitState
                {
                    WindowStart = now,
                    ChatCount = 0,
                    CommandCount = 0
                };
                _rateLimits[clientId] = state;
            }

            if (now - state.WindowStart >= windowSec)
            {
                state.WindowStart = now;
                state.ChatCount = 0;
                state.CommandCount = 0;
            }

            if (isCommand)
            {
                if (state.CommandCount >= cmdMax)
                {
                    reason = $"Command rate limit exceeded. Wait {windowSec:0.#}s.";
                    return true;
                }
                state.CommandCount++;
                return false;
            }

            if (state.ChatCount >= chatMax)
            {
                reason = $"Chat rate limit exceeded. Wait {windowSec:0.#}s.";
                return true;
            }
            state.ChatCount++;
            return false;
        }

        private bool IsPrivileged(ConnectedPlayerInfo info)
        {
            return HasTextChatPermission(info, TextChatPermissionNodes.RateLimitBypass);
        }

        /// <summary>
        /// Grants the given TextChat node, or falls back to elevated built-in
        /// groups (moderator/administrator/operator) and the settings exempt list.
        /// </summary>
        private bool HasTextChatPermission(ConnectedPlayerInfo info, string node)
        {
            if (info == null)
            {
                return false;
            }

            string subjectId = (info.TrustedUniqueId ?? string.Empty).Trim();
            var permissions = S1DS.Server.Permissions;
            if (permissions != null && subjectId.Length > 0)
            {
                if (permissions.HasPermission(subjectId, node))
                {
                    return true;
                }

                foreach (string group in permissions.GetEffectiveGroups(subjectId))
                {
                    if (string.Equals(group, PermissionBuiltIns.Groups.Moderator, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(group, PermissionBuiltIns.Groups.Administrator, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(group, PermissionBuiltIns.Groups.Operator, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return IsIdExempt(info.TrustedUniqueId);
        }

        private bool IsIdExempt(string trustedUniqueId)
        {
            if (_settings.RateLimitExemptPlayerIds == null ||
                _settings.RateLimitExemptPlayerIds.Count == 0)
            {
                return false;
            }

            string normalized = (trustedUniqueId ?? string.Empty).Trim();
            for (int i = 0; i < _settings.RateLimitExemptPlayerIds.Count; i++)
            {
                string allowed = (_settings.RateLimitExemptPlayerIds[i] ?? string.Empty).Trim();
                if (allowed.Length == 0) continue;
                if (string.Equals(normalized, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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
                _radiusSq = _settings.LocalChatRadius * _settings.LocalChatRadius;
            }
        }

        private sealed class ChatCommandOutput : ICommandOutput
        {
            private readonly S1DSTextChatServerMod _owner;
            private readonly NetworkConnection _targetConn;

            public ChatCommandOutput(S1DSTextChatServerMod owner, NetworkConnection targetConn)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _targetConn = targetConn;
            }

            public void WriteInfo(string message)
            {
                _owner.SendServerLine(_targetConn, message);
            }

            public void WriteWarning(string message)
            {
                _owner.SendServerLine(_targetConn, message);
            }

            public void WriteError(string message)
            {
                _owner.SendServerLine(_targetConn, message, isError: true);
            }
        }
    }
}
