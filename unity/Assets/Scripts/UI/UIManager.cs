using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// THE ONLY SCENE OBJECT THE MENU SYSTEM NEEDS.
///
/// Drop this on one empty GameObject and press Play. It creates the Canvas, the
/// EventSystem, and every screen from code — nothing is assembled by hand.
///
/// RESPONSIBILITIES
///   - own the flow state machine (MainMenu / Playing / Paused)
///   - own Time.timeScale (nothing else may write it)
///   - route screen transitions
///   - raise GameFlow events so the DDA bridge can drive the recorder
///
/// WHY TIMESCALE IS THE PAUSE MECHANISM: NoteSpawner's beat clock, NoteMover and
/// NoteHitDetector all advance on Time.deltaTime, so timeScale = 0 freezes the
/// entire game with no stop/start API on any of them. The main menu is simply the
/// same frozen state entered at boot.
/// </summary>
[DefaultExecutionOrder(-50)]
public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }

    [Header("Canvas")]
    [Tooltip("Reference resolution the code-built UI is laid out against.")]
    public Vector2 referenceResolution = new Vector2(1920f, 1080f);
    [Tooltip("Sort order for the menu canvas. Raise it if another canvas covers the menus.")]
    public int canvasSortOrder = 100;

    [Header("Development mode targets")]
    [Tooltip("GameObjects shown ONLY when the 'Development mode' setting is on — " +
             "e.g. the PITuningHUD object. RuleBasedDDAController's IMGUI panel and " +
             "the difficulty preset buttons are handled automatically by MenuDDABridge.")]
    public List<GameObject> devModeObjects = new List<GameObject>();

    [Header("Gameplay objects hidden outside a session")]
    [Tooltip("Optional. Objects deactivated while a menu is up and reactivated on " +
             "Play — e.g. the score text. Leave empty if nothing needs hiding.")]
    public List<GameObject> gameplayOnlyObjects = new List<GameObject>();

    // ---------------- screens ----------------
    private MainMenuScreen    _mainMenu;
    private SettingsScreen    _settings;
    private DiagnosticsScreen _diagnostics;
    private DeviceScreen      _device;
    private DeviceLostScreen  _deviceLost;
    private PauseScreen       _pause;
    private GameHUDScreen     _hud;
    private ConfirmDialog     _confirm;

    private RectTransform _canvasRoot;

    /// <summary>Which screen Settings should return to when closed.</summary>
    private GameFlowState _settingsReturnState = GameFlowState.MainMenu;

    /// <summary>
    /// Watchdog state for PauseForDeviceLoss(). Tracked here rather than in
    /// SerialForceSource because whether a disconnect matters is a FLOW question
    /// (are we mid-session right now?), not a device question — keeps the device
    /// adapter ignorant of the UI layer, matching every other seam in this project.
    /// </summary>
    private bool _deviceWasConnected;

    // ================================================================
    // Lifecycle
    // ================================================================

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        // 1. Settings definitions must exist before values are loaded from disk.
        GameSettingsBootstrap.devModeTargets = devModeObjects;
        GameSettingsBootstrap.RegisterAll();

        // 2. Profiles (the test profile is created here on first run).
        ProfileManager.EnsureLoaded();

        // 3. Build the UI.
        BuildCanvas();
        BuildScreens();
    }

    void Start()
    {
        // Deferred to Start so late registrants (MenuDDABridge registers its
        // settings in Awake) are all present before stored values are applied.
        SettingsRegistry.Load();

        ShowMainMenu();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Time.timeScale = 1f;          // never leave the editor stuck at 0
    }

    void Update()
    {
        // Device-loss watchdog. Only meaningful mid-session: a drop while sitting in
        // a menu is just "not connected yet" and handled by the Device screen, not
        // an emergency pause. Runs every frame regardless of keyboard presence, ABOVE
        // the Esc handling below, since it must fire even if Keyboard.current is null.
        if (GameFlow.State == GameFlowState.Playing)
        {
            bool connectedNow = InputManagerScript.DeviceConnected;
            if (_deviceWasConnected && !connectedNow) PauseForDeviceLoss();
            _deviceWasConnected = connectedNow;
        }

        // Esc toggles pause. Uses the New Input System directly, matching
        // InputManagerScript's existing style rather than introducing an action asset.
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.escapeKey.wasPressedThisFrame)
        {
            if (_confirm.IsVisible) return;                       // modal owns Esc
            if (_deviceLost.IsVisible) return;                    // only a reconnect clears this

            if (GameFlow.State == GameFlowState.Playing)      PauseGame();
            else if (GameFlow.State == GameFlowState.Paused)
            {
                // Esc backs out of Settings first, then unpauses.
                if (_settings.IsVisible) CloseSettings();
                else                     ResumeGame();
            }
        }
    }

    // ================================================================
    // Construction
    // ================================================================

    private void BuildCanvas()
    {
        var go = new GameObject("MenuCanvas", typeof(RectTransform), typeof(Canvas),
                                typeof(CanvasScaler), typeof(GraphicRaycaster));
        go.transform.SetParent(transform, false);

        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = canvasSortOrder;

        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = referenceResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        _canvasRoot = (RectTransform)go.transform;

        // An EventSystem is required for any UI input; create one if the scene lacks it.
        if (EventSystem.current == null)
        {
            var es = new GameObject("EventSystem", typeof(EventSystem),
                                    typeof(InputSystemUIInputModule));
            es.transform.SetParent(transform, false);
        }
    }

    private void BuildScreens()
    {
        _hud         = new GameHUDScreen();
        _mainMenu    = new MainMenuScreen();
        _settings    = new SettingsScreen();
        _diagnostics = new DiagnosticsScreen();
        _device      = new DeviceScreen();
        _deviceLost  = new DeviceLostScreen();
        _pause       = new PauseScreen();
        _confirm     = new ConfirmDialog();

        // HUD first so menus always draw over it.
        _hud.Init(this, _canvasRoot);
        _mainMenu.Init(this, _canvasRoot);
        _settings.Init(this, _canvasRoot);
        _diagnostics.Init(this, _canvasRoot);
        _device.Init(this, _canvasRoot);
        _deviceLost.Init(this, _canvasRoot);
        _pause.Init(this, _canvasRoot);
        _confirm.Init(this, _canvasRoot);
    }

    // ================================================================
    // Flow transitions
    // ================================================================

    public void ShowMainMenu()
    {
        HideAll();
        Time.timeScale = 0f;
        SetGameplayObjectsActive(false);
        _mainMenu.Show();
        GameFlow.RaiseStateChanged(GameFlowState.MainMenu);
    }

    /// <summary>Play pressed: clear the board, unfreeze, and start a recording.</summary>
    public void StartGame()
    {
        HideAll();
        ClearNotesInFlight();

        SetGameplayObjectsActive(true);
        _hud.Show();
        Time.timeScale = 1f;

        // Arm the watchdog against whatever the connection state ACTUALLY is right
        // now, not a stale value from browsing menus — otherwise a device that was
        // connected while sitting in Settings but got unplugged before Play was
        // pressed would trigger an immediate false "disconnect" on frame one.
        _deviceWasConnected = InputManagerScript.DeviceConnected;

        GameFlow.RaiseStateChanged(GameFlowState.Playing);
        GameFlow.RaiseGameStarted();       // MenuDDABridge starts the SessionRecorder here
    }

    public void PauseGame()
    {
        if (GameFlow.State != GameFlowState.Playing) return;

        Time.timeScale = 0f;
        _pause.Show();

        GameFlow.RaiseStateChanged(GameFlowState.Paused);
        GameFlow.RaiseGamePaused();        // recorder pauses its clock + sample streams
    }

    public void ResumeGame()
    {
        if (GameFlow.State != GameFlowState.Paused) return;

        _settings.Hide();
        _pause.Hide();
        Time.timeScale = 1f;

        GameFlow.RaiseStateChanged(GameFlowState.Playing);
        GameFlow.RaiseGameResumed();
    }

    /// <summary>
    /// Emergency pause triggered by the Update() watchdog when the device drops mid-
    /// session. Reuses the SAME GameFlow pause/resume events as the normal Esc pause
    /// (RaiseGamePaused/RaiseGameResumed) so the recorder's clock+sample handling
    /// doesn't need a third code path — from SessionRecorder's point of view this
    /// looks identical to any other pause. Only the SCREEN shown differs, and only
    /// that screen can clear it (see the Esc guard in Update()).
    /// </summary>
    public void PauseForDeviceLoss()
    {
        if (GameFlow.State != GameFlowState.Playing) return;

        Time.timeScale = 0f;
        _deviceLost.Show();

        GameFlow.RaiseStateChanged(GameFlowState.Paused);
        GameFlow.RaiseGamePaused();
    }

    /// <summary>Called by DeviceLostScreen itself the instant the device is available again.</summary>
    public void ResumeFromDeviceLoss()
    {
        if (GameFlow.State != GameFlowState.Paused) return;

        _deviceLost.Hide();
        Time.timeScale = 1f;

        GameFlow.RaiseStateChanged(GameFlowState.Playing);
        GameFlow.RaiseGameResumed();

        // The device just proved itself alive again; re-arm the watchdog so a SECOND
        // drop later in the same session pauses again instead of being missed.
        _deviceWasConnected = true;
    }

    /// <summary>
    /// Exit from the pause menu. Asks the keep-or-discard question first, since
    /// that decision is what determines whether the session hits disk.
    /// </summary>
    public void RequestExitToMainMenu()
    {
        Confirm(
            "Keep this recording?",
            $"Save the session for '{ProfileManager.Describe(ProfileManager.Current)}'?\n\n" +
            "Choosing 'Discard' writes no files at all.",
            "Keep", "Discard",
            keep =>
            {
                GameFlow.RaiseGameEnded(keep);   // bridge saves or discards
                ShowMainMenu();
            });
    }

    // ================================================================
    // Sub-screens
    // ================================================================

    public void ShowSettings()
    {
        _settingsReturnState = GameFlow.State;   // remember where we came from
        _mainMenu.Hide();
        _settings.Show();
    }

    public void CloseSettings()
    {
        _settings.Hide();
        if (_settingsReturnState == GameFlowState.Paused) _pause.Show();
        else                                              _mainMenu.Show();
    }

    public void ShowDiagnostics()
    {
        _mainMenu.Hide();
        _diagnostics.Show();
    }

    /// <summary>Device connection + calibration screen. Main menu only.</summary>
    public void ShowDevice()
    {
        _mainMenu.Hide();
        _device.Show();
    }

    public void CloseDevice()
    {
        _device.Hide();
        _mainMenu.Show();
    }

    /// <summary>Open the shared yes/no modal.</summary>
    public void Confirm(string title, string body, string yes, string no, Action<bool> callback)
        => _confirm.Ask(title, body, yes, no, callback);

    // ================================================================
    // Helpers
    // ================================================================

    private void HideAll()
    {
        _hud.Hide();
        _mainMenu.Hide();
        _settings.Hide();
        _diagnostics.Hide();
        _device.Hide();
        _deviceLost.Hide();
        _pause.Hide();
        _confirm.Hide();
    }

    private void SetGameplayObjectsActive(bool active)
    {
        foreach (var go in gameplayOnlyObjects)
            if (go != null) go.SetActive(active);
    }

    /// <summary>
    /// Destroy notes left on screen from the previous session so a new one always
    /// starts on a clean board. Every note (real and ghost) carries a NoteMover,
    /// which makes it the reliable handle for finding them.
    /// </summary>
    private void ClearNotesInFlight()
    {
        var movers = FindObjectsByType<NoteMover>(FindObjectsSortMode.None);
        foreach (var m in movers)
            if (m != null) Destroy(m.gameObject);
    }
}
