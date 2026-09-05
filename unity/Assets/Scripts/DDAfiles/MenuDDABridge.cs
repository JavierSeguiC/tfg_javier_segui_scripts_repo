using UnityEngine;

namespace DDA
{
    /// <summary>
    /// THE ONLY LINK BETWEEN THE MENUS AND THE DDA LAYER.
    ///
    /// The UI folder deliberately contains no reference to anything in namespace
    /// DDA, so the delete test still holds: remove the DDA folder and the menus,
    /// profiles, settings and diagnostics all still compile and run — you simply
    /// lose recording, controller selection and the debug overlays.
    ///
    /// This bridge does three jobs:
    ///
    ///  1. SESSION RECORDING — subscribes to the GameFlow events and drives
    ///     SessionRecorder. Play starts a recording stamped with the selected
    ///     profile; pause freezes it; exiting to the main menu saves or discards
    ///     it according to the user's answer to the keep-or-discard prompt.
    ///
    ///  2. CONTROLLER SELECTION — registers a settings entry that picks which
    ///     writer holds DifficultyAuthority. Registering it here (rather than in
    ///     GameSettingsBootstrap) means deleting the DDA folder removes the
    ///     setting cleanly instead of leaving a dead entry behind.
    ///
    ///  3. DEVELOPMENT MODE — shows/hides RuleBasedDDAController's IMGUI panel and
    ///     the difficulty preset buttons. The preset buttons are additionally only
    ///     visible when the preset switcher actually holds authority, per the
    ///     design: they are meaningless when a closed-loop controller is driving.
    ///
    /// SCENE SETUP: attach to any GameObject (DDA_System is the natural home) and
    /// assign the three optional references. Anything left null is skipped.
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after UIManager (-50): its settings exist by now
    public class MenuDDABridge : MonoBehaviour
    {
        public const string CONTROLLER_SETTING_ID = "dda.controller";

        [Header("References (leave null to skip)")]
        [Tooltip("Recorder driven by the menu flow. Auto-found if left empty.")]
        public SessionRecorder recorder;
        [Tooltip("Preset switcher whose Low/Mid/High buttons are shown in dev mode.")]
        public DifficultyPresetSwitcher presetSwitcher;
        [Tooltip("Rule-based controller whose IMGUI panel is toggled by dev mode.")]
        public RuleBasedDDAController ruleBased;
        [Tooltip("PI controller — selectable as the active difficulty writer.")]
        public PIDifficultyController piController;
        [Tooltip("PI tuning HUD (the difficulty/errors plot panel). Toggled by dev mode: " +
                 "both .enabled (stops Update/OnGUI outright) and .visible (so it doesn't " +
                 "come back hidden behind a stale F1 press) are set together.")]
        public PITuningHUD piTuningHud;

        // Order must match the option strings registered below.
        private enum ControllerChoice { Manual = 0, RuleBased = 1, PI = 2 }

        // True while we're pushing the authority's state INTO the setting, so the
        // setting's onApply doesn't turn around and re-claim (which would loop).
        private bool _syncingFromAuthority;

        // True from construction until the end of the first-frame sync. Controller
        // selection is an ACTION (an authority handover), not idempotent state to push —
        // so it must fire ONLY on a genuine user change in the menu, never as a side
        // effect of registration, disk load, or ApplyAll(), all of which call the
        // setting's onApply. Without this gate, registering the setting (with the stored
        // or default value) immediately claimed that controller before the single
        // startup decision below could run. This is the "manual keeps overriding" half of
        // the bug: a non-user apply kept re-claiming underneath the user's choice.
        private bool _suppressClaim = true;

        // The persisted controller choice, captured in Awake — BEFORE any controller's
        // Start() can self-claim and (via HandleAuthorityChanged) mutate the in-memory
        // setting underneath us. The one-frame startup decision reads this snapshot, so a
        // stray self-claim can never redirect which controller we settle on.
        private ControllerChoice _bootChoice;

        void Awake()
        {
            if (recorder == null)       recorder       = FindFirstObjectByType<SessionRecorder>();
            if (presetSwitcher == null) presetSwitcher = FindFirstObjectByType<DifficultyPresetSwitcher>();
            if (ruleBased == null)      ruleBased      = FindFirstObjectByType<RuleBasedDDAController>();
            if (piController == null)   piController   = FindFirstObjectByType<PIDifficultyController>();
            if (piTuningHud == null)    piTuningHud    = FindFirstObjectByType<PITuningHUD>();

            RegisterSettings();

            // Snapshot the persisted choice now (default PI). Registration above has
            // already pulled the stored value off disk, and no Start() has run yet, so
            // this is the clean user preference before any self-claim can perturb it.
            _bootChoice = (ControllerChoice)SettingsRegistry.GetInt(
                CONTROLLER_SETTING_ID, (int)ControllerChoice.PI);
        }

        void Start()
        {
            // THE ONE PLACE that decides the initial controller. Deferred a frame so all
            // components have registered with DifficultyAuthority first; then we claim the
            // controller named by the persisted setting (default PI) and nobody else.
            StartCoroutine(DecideInitialControllerNextFrame());
        }

        private System.Collections.IEnumerator DecideInitialControllerNextFrame()
        {
            yield return null;

            // Single source of truth: the persisted choice snapshotted in Awake (default
            // PI). Claim it directly — this deterministically overrides any controller
            // that self-claimed in its own Start(), collapsing the old three-way startup
            // race into one decision from one place. Not routed through the setting's
            // onApply (still suppressed), so there is no re-entrancy.
            ClaimForChoice(_bootChoice);

            // Mirror the actual resulting authority back into the dropdown (in case the saved
            // target was missing and we fell back), then open the gate: from here on, a
            // change to the setting is a real user action and IS allowed to claim.
            SyncSettingToAuthority(DifficultyAuthority.Current);
            RefreshDevVisibility();
            _suppressClaim = false;
        }

        void OnEnable()
        {
            GameFlow.OnGameStarted += HandleGameStarted;
            GameFlow.OnGamePaused  += HandleGamePaused;
            GameFlow.OnGameResumed += HandleGameResumed;
            GameFlow.OnGameEnded   += HandleGameEnded;

            SettingsRegistry.OnSettingChanged += HandleSettingChanged;
            DifficultyAuthority.OnAuthorityChanged += HandleAuthorityChanged;
        }

        void OnDisable()
        {
            GameFlow.OnGameStarted -= HandleGameStarted;
            GameFlow.OnGamePaused  -= HandleGamePaused;
            GameFlow.OnGameResumed -= HandleGameResumed;
            GameFlow.OnGameEnded   -= HandleGameEnded;

            SettingsRegistry.OnSettingChanged -= HandleSettingChanged;
            DifficultyAuthority.OnAuthorityChanged -= HandleAuthorityChanged;
        }

        // ================================================================
        // 1. Recording lifecycle
        // ================================================================

        private void HandleGameStarted()
        {
            ResetAllDifficultyControllers();

            if (recorder == null) return;

            var p = ProfileManager.Current;
            var user = p == null
                ? new SessionRecorder.SessionUserInfo()
                : new SessionRecorder.SessionUserInfo(p.id, p.name, p.age, p.physicalState, p.notes,
                                                       p.playingHand, p.dominantHand);

            recorder.StartRecording(user);
        }

        /// <summary>
        /// Every session starts fresh, for EVERY controller — not just whichever one
        /// currently holds authority. This matters because PI's error-rate estimator
        /// keeps listening to note outcomes in the background even while a different
        /// controller is driving (by design, so it's pre-converged if you switch to it
        /// mid-session). Without an unconditional reset here, switching to PI mid-session
        /// would carry over history from the PREVIOUS session, not just "since the switch"
        /// — which is the one case that should never leak.
        ///
        /// DifficultyAuthority.Claim() is also a no-op when a controller already holds
        /// authority, so re-claiming wouldn't reset anything anyway; each controller's
        /// own reset hook is called directly instead.
        /// </summary>
        private void ResetAllDifficultyControllers()
        {
            if (piController != null) piController.ResetLoops();
            if (ruleBased    != null) ruleBased.ResetToInitial();
            // Manual presets have nothing to reset — the operating point IS the fixed value.
        }

        private void HandleGamePaused()
        {
            if (recorder != null && recorder.IsRecording) recorder.PauseRecording();
        }

        private void HandleGameResumed()
        {
            if (recorder != null && recorder.IsPaused) recorder.ResumeRecording();
        }

        private void HandleGameEnded(bool keepRecording)
        {
            if (recorder == null || !recorder.IsRecording) return;

            if (keepRecording) recorder.StopAndSave();
            else               recorder.DiscardRecording();
        }

        // ================================================================
        // 2 & 3. Settings
        // ================================================================

        private void RegisterSettings()
        {
            SettingsRegistry.RegisterEnum(
                CONTROLLER_SETTING_ID, "Difficulty controller", "Difficulty",
                (int)ControllerChoice.PI,   // default on a fresh install
                new[] { "Manual presets", "Rule-based DDA", "PI controller" },
                v => ApplyControllerChoice((ControllerChoice)(int)v),
                "Which system controls difficulty. Only one may write at a time.");
        }

        private void HandleSettingChanged(string id)
        {
            // Dev mode lives game-side but its DDA-side effects belong here.
            if (id == GameSettingsBootstrap.DEV_MODE_ID || id == CONTROLLER_SETTING_ID)
                RefreshDevVisibility();
        }

        private void ApplyControllerChoice(ControllerChoice choice)
        {
            // Two cases where onApply fires but MUST NOT claim:
            //  • _suppressClaim: registration / disk load / ApplyAll during bootstrap.
            //    Authority is settled once, below, by DecideInitialControllerNextFrame.
            //  • _syncingFromAuthority: an external change (a HUD button) is being
            //    mirrored INTO the setting; claiming here would loop.
            if (_suppressClaim || _syncingFromAuthority) { RefreshDevVisibility(); return; }

            // A genuine user change in the settings menu. Claim() revokes every other
            // writer, so this is a clean handover — never two controllers on one frame.
            if (!ClaimForChoice(choice))
                Debug.LogWarning($"[MenuDDABridge] Controller '{choice}' has no reference " +
                                 "assigned/found — cannot switch to it.");

            RefreshDevVisibility();
        }

        /// <summary>
        /// Resolve a choice to its writer and claim it. Returns false (claiming nothing)
        /// if that writer has no reference, so callers can fall back. The one funnel
        /// through which this bridge grants authority — startup and user-change both.
        /// </summary>
        private bool ClaimForChoice(ControllerChoice choice)
        {
            IDifficultyWriter target = choice switch
            {
                ControllerChoice.RuleBased => ruleBased,
                ControllerChoice.PI        => piController,
                _                          => presetSwitcher,
            };
            if (target == null) return false;
            DifficultyAuthority.Claim(target);
            return true;
        }

        /// <summary>
        /// Push the ACTUAL current controller into the dropdown value, so the setting
        /// always shows reality — including changes made from the Inspector, the PI
        /// panel buttons, or the preset buttons. Guarded so it doesn't re-claim.
        /// </summary>
        private void HandleAuthorityChanged(IDifficultyWriter writer)
        {
            SyncSettingToAuthority(writer);
            RefreshDevVisibility();
        }

        private void SyncSettingToAuthority(IDifficultyWriter writer)
        {
            int choice = (int)ChoiceFor(writer);
            if (SettingsRegistry.GetInt(CONTROLLER_SETTING_ID) == choice) return;

            _syncingFromAuthority = true;
            SettingsRegistry.SetValue(CONTROLLER_SETTING_ID, choice, save: false);
            _syncingFromAuthority = false;
        }

        private ControllerChoice ChoiceFor(IDifficultyWriter writer)
        {
            if (writer != null && ReferenceEquals(writer, piController)) return ControllerChoice.PI;
            if (writer != null && ReferenceEquals(writer, ruleBased))    return ControllerChoice.RuleBased;
            return ControllerChoice.Manual;   // preset switcher, or nobody
        }

        /// <summary>
        /// Dev mode gates every debug overlay. The preset buttons additionally
        /// require the preset switcher to actually hold authority.
        /// </summary>
        private void RefreshDevVisibility()
        {
            bool dev = SettingsRegistry.GetBool(GameSettingsBootstrap.DEV_MODE_ID);

            // RuleBasedDDAController's panel is IMGUI drawn by the component itself,
            // so toggle its flag — disabling the GameObject would stop the controller too.
            if (ruleBased != null) ruleBased.drawOnScreenPanel = dev;

            // PI tuning HUD: .enabled stops Update/OnGUI outright; .visible is also set
            // so the panel doesn't come back hidden behind a stale F1 press from before
            // dev mode was last turned off.
            if (piTuningHud != null)
            {
                piTuningHud.enabled = dev;
                piTuningHud.visible = dev;
            }

            if (presetSwitcher != null)
            {
                bool presetActive = DifficultyAuthority.HasAuthority(presetSwitcher);
                bool show = dev && presetActive;

                SetActive(presetSwitcher.lowButton);
                SetActive(presetSwitcher.midButton);
                SetActive(presetSwitcher.highButton);

                void SetActive(UnityEngine.UI.Button b)
                {
                    if (b != null) b.gameObject.SetActive(show);
                }

                if (presetSwitcher.activeLabel != null)
                    presetSwitcher.activeLabel.gameObject.SetActive(show);
            }
        }
    }
}
