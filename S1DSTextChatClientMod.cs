using System;
using System.Collections.Generic;
using System.IO;
using DedicatedServerMod.API;
using DedicatedServerMod.API.Metadata;
using DedicatedServerMod.Shared.Networking;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.UI;
#else
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.UI;
#endif
using UnityEngine;

[assembly: MelonInfo(typeof(S1DSMod.TextChat.S1DSTextChatClientMod), "S1DS-TextChat", "1.1.0", "ZackaryH8")]
[assembly: S1DSClientModIdentity("zackaryh8.textchat", "1.1.0")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace S1DSMod.TextChat
{
    // ── Client mod ────────────────────────────────────────────────────────────
    public sealed class S1DSTextChatClientMod : ClientMelonModBase
    {
        // ── Layout constants ─────────────────────────────────────────────────
        private const int MAX_MESSAGES = 50;
        private const float PANEL_W = 450f;
        private const float MSG_AREA_H = 200f;
        private const float INPUT_H = 24f;
        private const float PAD = 6f;
        private const float FADE_DELAY = 10f;   // seconds idle before fade begins
        private const float FADE_DURATION = 1.5f;  // seconds to fully fade out
        private const int MAX_INPUT_HISTORY = 100;

        // ── Colours ──────────────────────────────────────────────────────────
        private static readonly Color C_BG = new Color(0.07f, 0.08f, 0.11f, 0.35f);  // low-opacity bg
        private static readonly Color C_LABEL_G = new Color(1.00f, 0.82f, 0.20f, 1.00f);  // gold  (input bar)
        private static readonly Color C_LABEL_L = new Color(0.30f, 0.90f, 0.45f, 1.00f);  // green (input bar)
        private static readonly Color C_INPUT_BG = new Color(0.10f, 0.11f, 0.15f, 1.00f);
        private static readonly Color C_INPUT_BG_FOCUSED = new Color(0.13f, 0.15f, 0.20f, 1.00f);
        private static readonly Color C_FIELD_BG = new Color(0.15f, 0.16f, 0.21f, 1.00f);
        private static readonly Color C_FIELD_BG_FOCUSED = new Color(0.19f, 0.21f, 0.28f, 1f);
        private static readonly Color C_SCROLL_TRACK = new Color(0.11f, 0.12f, 0.17f, 0.95f);
        private static readonly Color C_SCROLL_THUMB = new Color(0.36f, 0.66f, 0.92f, 0.90f);
        private static readonly Color C_SCROLL_THUMB_HOVER = new Color(0.47f, 0.75f, 0.98f, 0.95f);
        private static readonly Color C_SCROLL_THUMB_ACTIVE = new Color(0.30f, 0.58f, 0.87f, 1.00f);
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
        private int _historyIndex = -1;
        private string _historyScratch = string.Empty;

        private ClientSettings _settings = new ClientSettings();
        private readonly List<ChatEntry> _messages = new List<ChatEntry>();
        private readonly List<string> _inputHistory = new List<string>();

        // ── IMGUI styles (lazy-built once inside a GUI context) ───────────────
        private GUIStyle _sPanel;
        private GUIStyle _sMsgGlobal;
        private GUIStyle _sMsgLocal;
        private GUIStyle _sInputBg;
        private GUIStyle _sInputBgFocused;
        private GUIStyle _sField;
        private GUIStyle _sLabel;
        private GUIStyle _sHint;
        private GUIStyle _sVScrollbar;
        private GUIStyle _sVScrollbarThumb;
        private GUIStyle _sVScrollbarBtn;
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
            DeregisterChatExitListener();
        }

        public override void OnClientPlayerReady()
        {
            // Register AFTER scene load — exitListeners is cleared by onPreSceneChange,
            // so OnClientInitialize is too early.
            DeregisterChatExitListener();  // guard against duplicates
            RegisterChatExitListener();
            _ready = true;
            _inputMode = InputMode.None;
            _draft = string.Empty;
        }

        public override void OnDisconnectedFromServer()
        {
            SetChatFocus(false);
            DeregisterChatExitListener();
            _ready = false;
            _inputMode = InputMode.None;
            _draft = string.Empty;
        }

#if IL2CPP
        // Il2Cpp-generated APIs take a generated delegate type, and register/deregister
        // must see the same instance — cache the converted delegate.
        private GameInput.ExitDelegate _chatExitListener;

        private GameInput.ExitDelegate ChatExitListener =>
            _chatExitListener ??= DelegateSupport.ConvertDelegate<GameInput.ExitDelegate>(
                new Action<ExitAction>(OnChatExit));

        private void RegisterChatExitListener() => GameInput.RegisterExitListener(ChatExitListener, 10);

        private void DeregisterChatExitListener() => GameInput.DeregisterExitListener(ChatExitListener);
#else
        private void RegisterChatExitListener() => GameInput.RegisterExitListener(OnChatExit, priority: 10);

        private void DeregisterChatExitListener() => GameInput.DeregisterExitListener(OnChatExit);
#endif

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
                    // Open local chat with configured key (default U)
                    if (MatchesKey(e, _settings.LocalChatKey))
                    {
                        _inputMode = InputMode.Local;
                        _draft = string.Empty;
                        ResetHistoryNavigation();
                        _wantFocus = true;
                        _suppressChar = true;
                        SetChatFocus(true);
                        e.Use();
                    }
                    // Open global chat with configured key (default Y)
                    else if (MatchesKey(e, _settings.GlobalChatKey))
                    {
                        _inputMode = InputMode.Global;
                        _draft = string.Empty;
                        ResetHistoryNavigation();
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
                    else if (e.keyCode == KeyCode.UpArrow)
                    {
                        RecallOlderInput();
                        e.Use();
                    }
                    else if (e.keyCode == KeyCode.DownArrow)
                    {
                        RecallNewerInput();
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

                var prevVScrollbar = GUI.skin.verticalScrollbar;
                var prevVScrollbarThumb = GUI.skin.verticalScrollbarThumb;
                var prevVScrollbarUpButton = GUI.skin.verticalScrollbarUpButton;
                var prevVScrollbarDownButton = GUI.skin.verticalScrollbarDownButton;
                GUI.skin.verticalScrollbar = _sVScrollbar;
                GUI.skin.verticalScrollbarThumb = _sVScrollbarThumb;
                GUI.skin.verticalScrollbarUpButton = _sVScrollbarBtn;
                GUI.skin.verticalScrollbarDownButton = _sVScrollbarBtn;

                _scrollPos = GUILayout.BeginScrollView(
                    _scrollPos, false, false,
                    GUIStyle.none, _sVScrollbar,
                    GUILayout.Width(PANEL_W - PAD * 2f),
                    GUILayout.Height(MSG_AREA_H));
                {
                    foreach (var msg in _messages)
                        GUILayout.Label(msg.Display, msg.IsLocal ? _sMsgLocal : _sMsgGlobal);
                }
                GUILayout.EndScrollView();

                GUI.skin.verticalScrollbar = prevVScrollbar;
                GUI.skin.verticalScrollbarThumb = prevVScrollbarThumb;
                GUI.skin.verticalScrollbarUpButton = prevVScrollbarUpButton;
                GUI.skin.verticalScrollbarDownButton = prevVScrollbarDownButton;

                // ── Input bar ─────────────────────────────────────────────────
                if (_inputMode != InputMode.None)
                {
                    GUILayout.Space(PAD);

                    bool isDraftFocused = GUI.GetNameOfFocusedControl() == "ChatDraft";
                    GUIStyle inputStyle = isDraftFocused ? _sInputBgFocused : _sInputBg;

                    GUILayout.BeginHorizontal(inputStyle, GUILayout.Height(INPUT_H));
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
            string text = TextChatSanitizer.Sanitize(_draft);
            if (string.IsNullOrEmpty(text))
            {
                CancelChatInput();
                return;
            }

            int maxLen = Math.Max(1, _settings.MaxMessageLength);
            if (text.Length > maxLen)
            {
                AddMessage(new ChatEntry
                {
                    Display = $"[Server] Message too long ({text.Length}/{maxLen}).",
                    IsLocal = false
                });
                _wantFocus = true;
                return;
            }

            InputMode mode = _inputMode;

            _draft = string.Empty;
            _inputMode = InputMode.None;
            ResetHistoryNavigation();
            SetChatFocus(false);

            AddInputHistory(text);

            bool isLocal = mode == InputMode.Local;
            var payload = new ChatSendPayload
            {
                Text = text,
                Scope = isLocal ? "local" : "global"
            };
            CustomMessaging.SendToServer(Cmds.Send, JsonConvert.SerializeObject(payload));

            if (text.StartsWith("/", StringComparison.Ordinal))
                return;

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
                CancelChatInput();
            }
        }

        private void CancelChatInput()
        {
            _inputMode = InputMode.None;
            _draft = string.Empty;
            ResetHistoryNavigation();
            SetChatFocus(false);
        }

        private void AddInputHistory(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _inputHistory.Add(text);
            if (_inputHistory.Count > MAX_INPUT_HISTORY)
                _inputHistory.RemoveAt(0);
        }

        private void ResetHistoryNavigation()
        {
            _historyIndex = -1;
            _historyScratch = string.Empty;
        }

        private void RecallOlderInput()
        {
            if (_inputHistory.Count == 0) return;

            if (_historyIndex < 0)
            {
                _historyScratch = _draft;
                _historyIndex = _inputHistory.Count - 1;
            }
            else if (_historyIndex > 0)
            {
                _historyIndex--;
            }

            _draft = _inputHistory[_historyIndex];
            _wantFocus = true;
        }

        private void RecallNewerInput()
        {
            if (_historyIndex < 0 || _inputHistory.Count == 0) return;

            if (_historyIndex < _inputHistory.Count - 1)
            {
                _historyIndex++;
                _draft = _inputHistory[_historyIndex];
            }
            else
            {
                _historyIndex = -1;
                _draft = _historyScratch;
                _historyScratch = string.Empty;
            }

            _wantFocus = true;
        }

        private static string BuildDisplay(string sender, string text, bool isLocal)
        {
            string safeSender = TextChatSanitizer.Sanitize(sender);
            string safeText = TextChatSanitizer.Sanitize(text);

            bool isServer = string.Equals(safeSender, "Server", StringComparison.OrdinalIgnoreCase);
            if (isServer)
                return $"[Server] {safeText}";
            if (isLocal)
                return $"{safeSender}: {safeText}";
            return $"[Global] {safeSender}: {safeText}";
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
                richText = false
            };
            _sMsgGlobal.normal.textColor = Color.white;

            // Both scopes share the same base style; colour comes from rich-text tags in the display string.
            _sMsgLocal = new GUIStyle(_sMsgGlobal);

            _sInputBg = new GUIStyle(GUI.skin.box);
            _sInputBg.normal.background = MakeTex(C_INPUT_BG);
            _sInputBg.border = new RectOffset(0, 0, 0, 0);

            _sInputBgFocused = new GUIStyle(_sInputBg);
            _sInputBgFocused.normal.background = MakeTex(C_INPUT_BG_FOCUSED);

            _sField = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
            _sField.normal.textColor = Color.white;
            _sField.focused.textColor = Color.white;
            _sField.normal.background = MakeTex(C_FIELD_BG);
            _sField.focused.background = MakeTex(C_FIELD_BG_FOCUSED);
            _sField.padding = new RectOffset(8, 8, 4, 4);

            _sVScrollbar = new GUIStyle(GUI.skin.verticalScrollbar);
            _sVScrollbar.normal.background = MakeTex(C_SCROLL_TRACK);
            _sVScrollbar.hover.background = _sVScrollbar.normal.background;
            _sVScrollbar.active.background = _sVScrollbar.normal.background;
            _sVScrollbar.fixedWidth = 10f;

            _sVScrollbarThumb = new GUIStyle(GUI.skin.verticalScrollbarThumb);
            _sVScrollbarThumb.normal.background = MakeTex(C_SCROLL_THUMB);
            _sVScrollbarThumb.hover.background = MakeTex(C_SCROLL_THUMB_HOVER);
            _sVScrollbarThumb.active.background = MakeTex(C_SCROLL_THUMB_ACTIVE);
            _sVScrollbarThumb.fixedWidth = 10f;

            _sVScrollbarBtn = new GUIStyle(GUI.skin.verticalScrollbarUpButton);
            _sVScrollbarBtn.normal.background = MakeTex(new Color(0f, 0f, 0f, 0f));
            _sVScrollbarBtn.hover.background = _sVScrollbarBtn.normal.background;
            _sVScrollbarBtn.active.background = _sVScrollbarBtn.normal.background;
            _sVScrollbarBtn.fixedHeight = 0f;

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
