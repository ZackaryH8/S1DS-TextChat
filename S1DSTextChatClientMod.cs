using System;
using System.Collections.Generic;
using System.IO;
using DedicatedServerMod.API;
using DedicatedServerMod.Shared.Networking;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
using UnityEngine;

[assembly: MelonInfo(typeof(S1DSMod.TextChat.S1DSTextChatClientMod), "S1DS-TextChat", "1.0.0", "ZackaryH8")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace S1DSMod.TextChat
{
    // ── Shared command strings ────────────────────────────────────────────────
    internal static class Cmds
    {
        public const string Send = "textchat_send";
        public const string Recv = "textchat_recv";
    }

    // ── Payload types (mirror of server-side) ─────────────────────────────────
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

    // ── Client configuration ──────────────────────────────────────────────────
    [Serializable]
    internal sealed class ClientSettings
    {
        [JsonProperty("localChatKey")] public string LocalChatKey { get; set; } = "U";
        [JsonProperty("globalChatKey")] public string GlobalChatKey { get; set; } = "Y";
    }

    internal sealed class ChatEntry
    {
        public string Display { get; set; }
        public bool IsLocal { get; set; }
    }

    internal enum InputMode { None, Global, Local }

    // ── Client mod ────────────────────────────────────────────────────────────
    public sealed class S1DSTextChatClientMod : ClientMelonModBase
    {
        // ── Layout constants ─────────────────────────────────────────────────
        private const int MAX_MESSAGES = 50;
        private const float PANEL_W = 360f;
        private const float MSG_AREA_H = 160f;
        private const float INPUT_H = 24f;
        private const float PAD = 6f;
        private const float FADE_DELAY = 10f;   // seconds idle before fade begins
        private const float FADE_DURATION = 1.5f;  // seconds to fully fade out

        // ── Colours ──────────────────────────────────────────────────────────
        private static readonly Color C_BG = new Color(0.07f, 0.08f, 0.11f, 0.35f);  // low-opacity bg
        private static readonly Color C_LABEL_G = new Color(1.00f, 0.82f, 0.20f, 1.00f);  // gold  (input bar)
        private static readonly Color C_LABEL_L = new Color(0.30f, 0.90f, 0.45f, 1.00f);  // green (input bar)
        private static readonly Color C_INPUT_BG = new Color(0.10f, 0.11f, 0.15f, 1.00f);
        private static readonly Color C_FIELD_BG = new Color(0.15f, 0.16f, 0.21f, 1.00f);
        private static readonly Color C_HINT = new Color(0.50f, 0.52f, 0.58f, 0.75f);

        // ── Runtime state ────────────────────────────────────────────────────
        private bool _ready;
        private InputMode _inputMode = InputMode.None;
        private string _draft = string.Empty;
        private bool _wantFocus;
        private bool _suppressChar;
        private Vector2 _scrollPos;
        private bool _scrollToBottom;
        private float _fadeTimer;
        private float _fadeAlpha = 1f;

        private ClientSettings _settings = new ClientSettings();
        private readonly List<ChatEntry> _messages = new List<ChatEntry>();

        // ── IMGUI styles (lazy-built once inside a GUI context) ───────────────
        private GUIStyle _sPanel;
        private GUIStyle _sMsgGlobal;
        private GUIStyle _sMsgLocal;
        private GUIStyle _sInputBg;
        private GUIStyle _sField;
        private GUIStyle _sLabel;
        private GUIStyle _sHint;
        private bool _stylesReady;

        // ── Lifecycle ────────────────────────────────────────────────────────

        public override void OnInitializeMelon()
        {
            LoadSettings();
        }

        public override void OnClientInitialize()
        {
            CustomMessaging.ClientMessageReceived -= OnClientMessage;
            CustomMessaging.ClientMessageReceived += OnClientMessage;
        }

        public override void OnClientShutdown()
        {
            CustomMessaging.ClientMessageReceived -= OnClientMessage;
            GameInput.DeregisterExitListener(OnChatExit);
        }

        public override void OnClientPlayerReady()
        {
            // Register AFTER scene load — exitListeners is cleared by onPreSceneChange,
            // so OnClientInitialize is too early.
            GameInput.DeregisterExitListener(OnChatExit);  // guard against duplicates
            GameInput.RegisterExitListener(OnChatExit, priority: 10);
            _ready = true;
            _inputMode = InputMode.None;
            _draft = string.Empty;
        }

        public override void OnDisconnectedFromServer()
        {
            SetChatFocus(false);
            GameInput.DeregisterExitListener(OnChatExit);
            _ready = false;
            _inputMode = InputMode.None;
            _draft = string.Empty;
        }

        public override void OnUpdate()
        {
            if (_inputMode != InputMode.None)
            {
                _fadeTimer = 0f;
                _fadeAlpha = 1f;
                return;
            }
            if (_fadeAlpha <= 0f) return;
            _fadeTimer += Time.deltaTime;
            if (_fadeTimer >= FADE_DELAY)
            {
                float t = (_fadeTimer - FADE_DELAY) / FADE_DURATION;
                _fadeAlpha = Mathf.Clamp01(1f - t);
            }
        }

        // ── IMGUI ────────────────────────────────────────────────────────────

        public override void OnGUI()
        {
            // Only draw when connected (or if there are leftover messages to read)
            if (!_ready && _messages.Count == 0) return;

            EnsureStyles();

            // ── Key event handling ────────────────────────────────────────────
            // Check BEFORE drawing the text field so our handler wins over IMGUI.
            var e = Event.current;

            // Eat the character event that IMGUI fires after the hotkey KeyDown we already consumed.
            if (_suppressChar && (e.type == EventType.KeyDown || e.type == EventType.KeyUp) && e.character != '\0')
            {
                _suppressChar = false;
                e.Use();
                return;
            }

            if (e.type == EventType.KeyDown)
            {
                bool isReturn = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;

                if (_inputMode == InputMode.None && _ready)
                {
                    // Open local chat with configured key (default T)
                    if (MatchesKey(e, _settings.LocalChatKey))
                    {
                        _inputMode = InputMode.Local;
                        _draft = string.Empty;
                        _wantFocus = true;
                        _suppressChar = true;
                        SetChatFocus(true);
                        e.Use();
                    }
                    // Open global chat with configured key (default G)
                    else if (MatchesKey(e, _settings.GlobalChatKey))
                    {
                        _inputMode = InputMode.Global;
                        _draft = string.Empty;
                        _wantFocus = true;
                        _suppressChar = true;
                        SetChatFocus(true);
                        e.Use();
                    }
                }
                else if (_inputMode != InputMode.None)
                {
                    if (isReturn)
                    {
                        SubmitMessage();
                        e.Use();
                    }
                }
            }

            // ── Panel dimensions ─────────────────────────────────────────────
            float inputRowH = _inputMode != InputMode.None ? (INPUT_H + PAD) : 0f;
            float totalH = PAD + MSG_AREA_H + inputRowH + PAD;
            float panelX = 10f;
            float panelY = 80f;

            var panelRect = new Rect(panelX, panelY, PANEL_W, totalH);

            // ── Fade guard ───────────────────────────────────────────────────
            if (_fadeAlpha <= 0f && _inputMode == InputMode.None) return;
            var savedColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _fadeAlpha);

            // ── Background ───────────────────────────────────────────────────
            GUI.Box(panelRect, GUIContent.none, _sPanel);

            // ── Content area ─────────────────────────────────────────────────
            GUILayout.BeginArea(panelRect);
            {
                GUILayout.Space(PAD);

                // Auto-scroll: keep scrolled to bottom while new messages arrive;
                // reset flag once the repaint has committed the position.
                if (_scrollToBottom)
                {
                    _scrollPos.y = float.MaxValue;
                    if (e.type == EventType.Repaint)
                        _scrollToBottom = false;
                }

                _scrollPos = GUILayout.BeginScrollView(
                    _scrollPos, false, false,
                    GUIStyle.none, GUI.skin.verticalScrollbar,
                    GUILayout.Width(PANEL_W - PAD * 2f),
                    GUILayout.Height(MSG_AREA_H));
                {
                    foreach (var msg in _messages)
                        GUILayout.Label(msg.Display, msg.IsLocal ? _sMsgLocal : _sMsgGlobal);
                }
                GUILayout.EndScrollView();

                // ── Input bar ─────────────────────────────────────────────────
                if (_inputMode != InputMode.None)
                {
                    GUILayout.Space(PAD);

                    GUILayout.BeginHorizontal(_sInputBg, GUILayout.Height(INPUT_H));
                    {
                        bool isGlobal = (_inputMode == InputMode.Global);

                        // Mode label (gold = Global, green = Local)
                        var prev = GUI.color;
                        GUI.color = isGlobal ? C_LABEL_G : C_LABEL_L;
                        GUILayout.Label(isGlobal ? " [Global] " : " [Local]  ",
                                        _sLabel, GUILayout.ExpandWidth(false));
                        GUI.color = prev;

                        // Text field — focus as soon as the control exists
                        if (_wantFocus)
                            GUI.FocusControl("ChatDraft");
                        GUI.SetNextControlName("ChatDraft");
                        _draft = GUILayout.TextField(_draft, 200, _sField);
                        if (_wantFocus && GUI.GetNameOfFocusedControl() == "ChatDraft")
                            _wantFocus = false;
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.Space(PAD);
            }
            GUILayout.EndArea();

            // ── Hint line below panel ─────────────────────────────────────────
            if (_ready && _inputMode == InputMode.None)
            {
                string hint = $"{_settings.LocalChatKey} = Local chat  |  {_settings.GlobalChatKey} = Global chat";
                GUI.Label(
                    new Rect(panelX, panelY + totalH + 2f, PANEL_W, 14f),
                    hint,
                    _sHint);
            }

            GUI.color = savedColor;
        }

        // ── Network ──────────────────────────────────────────────────────────

        private void OnClientMessage(string cmd, string data)
        {
            if (cmd != Cmds.Recv) return;
            try
            {
                var p = JsonConvert.DeserializeObject<ChatRecvPayload>(data);
                if (p == null) return;

                bool isLocal = p.Scope == "local";
                string display = BuildDisplay(p.Sender, p.Text, isLocal);
                AddMessage(new ChatEntry { Display = display, IsLocal = isLocal });
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"S1DS-TextChat: failed to parse message: {ex.Message}");
            }
        }

        private void AddMessage(ChatEntry entry)
        {
            _messages.Add(entry);
            while (_messages.Count > MAX_MESSAGES)
                _messages.RemoveAt(0);
            _scrollToBottom = true;
            _fadeTimer = 0f;
            _fadeAlpha = 1f;
        }

        private void SubmitMessage()
        {
            string text = _draft?.Trim() ?? string.Empty;
            InputMode mode = _inputMode;

            _draft = string.Empty;
            _inputMode = InputMode.None;
            SetChatFocus(false);
            if (string.IsNullOrEmpty(text)) return;

            bool isLocal = mode == InputMode.Local;
            var payload = new ChatSendPayload
            {
                Text = text,
                Scope = isLocal ? "local" : "global"
            };
            CustomMessaging.SendToServer(Cmds.Send, JsonConvert.SerializeObject(payload));

            // Show own message immediately without waiting for server echo
            string display = BuildDisplay("You", text, isLocal);
            AddMessage(new ChatEntry { Display = display, IsLocal = isLocal });
        }

        // ── Message formatting ────────────────────────────────────────────────

        // Mirrors the canonical pattern from StorageMenu, PickpocketScreen, etc.
        // GameInput.Exit() calls this before PauseMenu.LateUpdate() checks TogglePauseInputUsed,
        // so marking action.Used = true here is what actually blocks the pause menu.
        private void OnChatExit(ExitAction action)
        {
            if (!action.Used && _inputMode != InputMode.None && action.exitType == ExitType.Escape)
            {
                action.Used = true;
                _inputMode = InputMode.None;
                _draft = string.Empty;
                SetChatFocus(false);
            }
        }

        private static string BuildDisplay(string sender, string text, bool isLocal)
        {
            bool isServer = string.Equals(sender, "Server", StringComparison.OrdinalIgnoreCase);
            if (isServer)
                return $"[<color=#f5a121>Server</color>] {text}";
            if (isLocal)
                return $"<b>{sender}</b>: {text}";
            return $"[<color=#59b5ff>Global</color>] <b>{sender}</b>: {text}";
        }

        // ── Settings ─────────────────────────────────────────────────────────

        /// <summary>Returns true if the event's key name matches the configured key string (case-insensitive).</summary>
        private static bool MatchesKey(Event e, string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return false;
            return string.Equals(e.keyCode.ToString(), keyName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Acquires or releases game input focus for the chat panel.
        /// Mirrors PlayerUtilities.OpenMenu/CloseMenu exactly.
        /// </summary>
        private static void SetChatFocus(bool focused)
        {
            try
            {
                GameInput.IsTyping = focused;

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                var move = PlayerSingleton<PlayerMovement>.Instance;
                var inv = PlayerSingleton<PlayerInventory>.Instance;

                if (focused)
                {
                    cam?.FreeMouse();
                    cam?.SetCanLook(false);
                    cam?.AddActiveUIElement("textchat");  // disables PunchController
                    if (move != null) move.CanMove = false;
                    inv?.SetInventoryEnabled(false);
                    if (Singleton<HUD>.InstanceExists)
                        Singleton<HUD>.Instance.SetCrosshairVisible(false);
                }
                else
                {
                    cam?.LockMouse();
                    cam?.SetCanLook(true);
                    cam?.RemoveActiveUIElement("textchat");
                    if (move != null) move.CanMove = true;
                    inv?.SetInventoryEnabled(true);
                    if (Singleton<HUD>.InstanceExists)
                        Singleton<HUD>.Instance.SetCrosshairVisible(true);
                }
            }
            catch { /* non-critical; player may not be loaded yet */ }
        }

        private void LoadSettings()
        {
            string path = Path.Combine(MelonEnvironment.UserDataDirectory, "S1DS-TextChat.json");
            try
            {
                if (File.Exists(path))
                {
                    _settings = JsonConvert.DeserializeObject<ClientSettings>(
                        File.ReadAllText(path)) ?? new ClientSettings();
                }
                else
                {
                    _settings = new ClientSettings();
                    File.WriteAllText(path,
                        JsonConvert.SerializeObject(_settings, Formatting.Indented));
                    LoggerInstance.Msg($"S1DS-TextChat: created default settings at {path}");
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning(
                    $"S1DS-TextChat: failed to load settings, using defaults: {ex.Message}");
                _settings = new ClientSettings();
            }
        }

        // ── IMGUI style helpers ───────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _sPanel = new GUIStyle(GUI.skin.box);
            _sPanel.normal.background = MakeTex(C_BG);
            _sPanel.border = new RectOffset(0, 0, 0, 0);

            _sMsgGlobal = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                fontSize = 12,
                richText = true
            };
            _sMsgGlobal.normal.textColor = Color.white;

            // Both scopes share the same base style; colour comes from rich-text tags in the display string.
            _sMsgLocal = new GUIStyle(_sMsgGlobal);

            _sInputBg = new GUIStyle(GUI.skin.box);
            _sInputBg.normal.background = MakeTex(C_INPUT_BG);
            _sInputBg.border = new RectOffset(0, 0, 0, 0);

            _sField = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
            _sField.normal.textColor = Color.white;
            _sField.focused.textColor = Color.white;
            _sField.normal.background = MakeTex(C_FIELD_BG);
            _sField.focused.background = MakeTex(new Color(0.19f, 0.21f, 0.28f, 1f));

            _sLabel = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };

            _sHint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft
            };
            _sHint.normal.textColor = C_HINT;
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(2, 2);
            tex.SetPixels(new[] { color, color, color, color });
            tex.Apply();
            return tex;
        }
    }
}
