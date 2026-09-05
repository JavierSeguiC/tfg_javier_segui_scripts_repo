using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace DDA
{
    /// <summary>
    /// One-click difficulty preset switcher for sys-id and manual testing.
    ///
    /// Holds three fully-configurable presets (Low / Mid / High). Each preset
    /// maps to the six control-vector entries u = [v, f_s, τ_1, τ_2, τ_3, τ_4].
    /// Pressing a button (or calling ApplyPreset() from code) writes the values
    /// directly to GameDifficulty.Instance.
    ///
    /// AUTHORITY (added Aug 2026)
    ///   This is one of THREE possible writers of GameDifficulty, alongside
    ///   PIDifficultyController and RuleBasedDDAController. It implements
    ///   IDifficultyWriter: pressing a preset button CLAIMS authority, which
    ///   automatically silences whichever closed-loop controller was running.
    ///   That is the intended debug behaviour — "freeze the plant at a known
    ///   operating point" — and it makes the takeover explicit and logged instead
    ///   of two scripts quietly overwriting each other every frame.
    ///
    ///   The start-up default (apply Mid) is DEFERRED by one frame and only fires
    ///   if nobody else has claimed authority. This avoids a script-execution-order
    ///   race where the preset switcher would stomp a controller that legitimately
    ///   claimed control in its own Start().
    ///
    /// INSPECTOR SETUP
    ///   1. Attach this script to any scene GameObject (e.g. "GameSystem").
    ///   2. Assign the three Button references (LowButton, MidButton, HighButton).
    ///   3. Edit the three DifficultyPreset blocks in the inspector to set the
    ///      exact v / spawnInterval / requiredForce values for your operating points.
    ///
    /// Designed operating points (June 2026 sys-id campaign):
    ///   Low  — v=200,  spawnInterval=2.0 s
    ///   Mid  — v=300,  spawnInterval=1.2 s   ← nominal operating point u*
    ///   High — v=550,  spawnInterval=0.6 s
    /// </summary>
    public class DifficultyPresetSwitcher : MonoBehaviour, IDifficultyWriter
    {
        // ----------------------------------------------------------------
        // Preset data
        // ----------------------------------------------------------------

        [System.Serializable]
        public class DifficultyPreset
        {
            [Tooltip("Display name shown on the active-preset label.")]
            public string label = "Mid";

            [Header("Reflexes")]
            [Tooltip("Note speed in world units/second. u[0] = v.")]
            public float noteSpeed = 300f;
            [Tooltip("Seconds between spawns (beat period). u[1] = 1/f_s.")]
            public float spawnInterval = 1.2f;

            [Header("Force thresholds τ_ℓ — one per lane")]
            [Tooltip("Required force per lane, normalised [0,1]. u[2..5] = τ_1..τ_4.")]
            public float[] requiredForce = new float[] { 0.4f, 0.4f, 0.4f, 0.4f };
        }

        [Header("Presets")]
        public DifficultyPreset lowPreset  = new DifficultyPreset
        {
            label = "Low", noteSpeed = 200f, spawnInterval = 2.0f,
            requiredForce = new float[] { 0.25f, 0.25f, 0.25f, 0.25f }
        };
        public DifficultyPreset midPreset  = new DifficultyPreset
        {
            label = "Mid", noteSpeed = 300f, spawnInterval = 1.2f,
            requiredForce = new float[] { 0.40f, 0.40f, 0.40f, 0.40f }
        };
        public DifficultyPreset highPreset = new DifficultyPreset
        {
            label = "High", noteSpeed = 550f, spawnInterval = 0.6f,
            requiredForce = new float[] { 0.60f, 0.60f, 0.60f, 0.60f }
        };

        [Header("Start-up behaviour")]
        [Tooltip("Apply the Mid preset on start, but ONLY if no controller has claimed " +
                 "difficulty authority first. Turn OFF when a closed-loop controller " +
                 "should always own the session.")]
        public bool applyMidOnStartIfUnclaimed = true;

        // ----------------------------------------------------------------
        // UI references
        // ----------------------------------------------------------------

        [Header("UI Buttons (assign in inspector)")]
        public Button lowButton;
        public Button midButton;
        public Button highButton;

        [Header("Optional — active preset label")]
        [Tooltip("TMP text that shows which preset is active. Leave unassigned to skip.")]
        public TMP_Text activeLabel;

        // ----------------------------------------------------------------
        // Visual feedback colours
        // ----------------------------------------------------------------

        [Header("Button colours")]
        public Color activeColour   = new Color(0.25f, 0.75f, 0.35f); // green
        public Color inactiveColour = new Color(0.85f, 0.85f, 0.85f); // light grey

        // ----------------------------------------------------------------
        // Internal
        // ----------------------------------------------------------------

        private enum Preset { Low, Mid, High }
        private Preset _current = Preset.Mid;
        private bool _hasAppliedOnce;

        // ---------------- IDifficultyWriter ----------------
        public string AuthorityName => "Difficulty preset switcher (manual)";

        /// <summary>
        /// Always NaN. A preset's noteSpeed doesn't map cleanly back onto the shared
        /// step-count d (that would require assuming a specific speedStep, which this
        /// debug tool has no reason to track precisely) — and the switcher is a manual
        /// debug aid, not something worth plotting on the difficulty axis. SessionRecorder
        /// and PITuningHUD both already treat NaN d as "no reading" rather than 0.
        /// </summary>
        public float Difficulty => float.NaN;

        public void OnAuthorityGranted()
        {
            // Selecting manual mode must define an operating point immediately, even
            // before any button press — otherwise "Manual" would hold authority while
            // leaving difficulty at whatever the previous writer left. Default to Mid on
            // the first grant, then re-assert the current preset on any later grant so the
            // game state matches the highlighted button the moment control is handed over.
            if (!_hasAppliedOnce) { _hasAppliedOnce = true; _current = Preset.Mid; }
            WriteToGameDifficulty(PresetData(_current));
            RefreshButtonVisuals(PresetData(_current).label);
        }

        public void OnAuthorityRevoked()
        {
            // Nothing to stop: this writer only acts on an explicit button press,
            // and every write is gated on HasAuthority. Just refresh the label.
            RefreshButtonVisuals(_hasAppliedOnce ? PresetData(_current).label : "—");
        }

        // ----------------------------------------------------------------
        // Lifecycle
        // ----------------------------------------------------------------

        void Awake()
        {
            if (lowButton  != null) lowButton .onClick.AddListener(() => ApplyPreset(Preset.Low));
            if (midButton  != null) midButton .onClick.AddListener(() => ApplyPreset(Preset.Mid));
            if (highButton != null) highButton.onClick.AddListener(() => ApplyPreset(Preset.High));

            DifficultyAuthority.Register(this);
        }

        void OnDestroy() => DifficultyAuthority.Unregister(this);

        void Start()
        {
            // When the menu bridge is present it is the SINGLE place that decides the
            // initial controller (from the persisted setting), so this switcher must not
            // also grab authority at startup — that self-claim is exactly the "manual
            // keeps overriding" behaviour we're removing. Only auto-apply in a bridge-less
            // scene (a controller-only test scene, or the delete-test with the menu layer
            // stripped), where nobody else will.
            if (applyMidOnStartIfUnclaimed &&
                FindFirstObjectByType<MenuDDABridge>() == null)
                StartCoroutine(ApplyDefaultIfUnclaimed());
        }

        IEnumerator ApplyDefaultIfUnclaimed()
        {
            yield return null;   // let all Start() methods run

            if (DifficultyAuthority.Current == null)
            {
                ApplyPreset(Preset.Mid);
            }
            else
            {
                _hasAppliedOnce = true;
                _current = Preset.Mid;
                RefreshButtonVisuals(midPreset.label);
                Debug.Log($"[DifficultyPresetSwitcher] Start-up preset skipped — " +
                          $"'{DifficultyAuthority.CurrentName}' already holds authority.");
            }
        }

        // ----------------------------------------------------------------
        // Public API — callable from code (e.g. sys-id driver)
        // ----------------------------------------------------------------

        public void ApplyLow()  => ApplyPreset(Preset.Low);
        public void ApplyMid()  => ApplyPreset(Preset.Mid);
        public void ApplyHigh() => ApplyPreset(Preset.High);

        // ----------------------------------------------------------------
        // Core
        // ----------------------------------------------------------------

        private DifficultyPreset PresetData(Preset p) => p switch
        {
            Preset.Low  => lowPreset,
            Preset.High => highPreset,
            _           => midPreset,
        };

        private void ApplyPreset(Preset p)
        {
            _current        = p;
            _hasAppliedOnce = true;

            // Manual override: taking a preset means taking the plant off any
            // closed-loop controller. Claim first, then write.
            DifficultyAuthority.Claim(this);

            DifficultyPreset data = PresetData(p);

            WriteToGameDifficulty(data);
            RefreshButtonVisuals(data.label);

            Debug.Log($"[DifficultyPresetSwitcher] Applied preset '{data.label}': " +
                      $"v={data.noteSpeed}, spawnInterval={data.spawnInterval}, " +
                      $"τ=[{string.Join(", ", data.requiredForce)}]");
        }

        private void WriteToGameDifficulty(DifficultyPreset data)
        {
            // Authority gate — never write unless this component currently owns difficulty.
            if (!DifficultyAuthority.HasAuthority(this)) return;

            var d = GameDifficulty.Instance;
            if (d == null)
            {
                Debug.LogWarning("[DifficultyPresetSwitcher] GameDifficulty.Instance is null — " +
                                 "preset written but no game object to receive it.");
                return;
            }

            d.noteSpeed     = data.noteSpeed;
            d.spawnInterval = data.spawnInterval;

            // Write per-lane force thresholds, guarding against a mismatched
            // preset array (e.g. inspector was edited to the wrong length).
            if (data.requiredForce != null)
            {
                for (int i = 0; i < d.requiredForce.Length; i++)
                {
                    d.requiredForce[i] = (i < data.requiredForce.Length)
                        ? data.requiredForce[i]
                        : data.requiredForce[data.requiredForce.Length - 1]; // repeat last
                }
            }
        }

        private void RefreshButtonVisuals(string presetLabel)
        {
            bool owns = DifficultyAuthority.HasAuthority(this);

            SetButtonColour(lowButton,  owns && lowPreset.label  == presetLabel);
            SetButtonColour(midButton,  owns && midPreset.label  == presetLabel);
            SetButtonColour(highButton, owns && highPreset.label == presetLabel);

            if (activeLabel != null)
            {
                activeLabel.text = owns
                    ? $"Difficulty: <b>{presetLabel}</b>"
                    : $"Difficulty: <i>{DifficultyAuthority.CurrentName}</i>";
            }
        }

        private void SetButtonColour(Button btn, bool isActive)
        {
            if (btn == null) return;
            var colours      = btn.colors;
            colours.normalColor      = isActive ? activeColour : inactiveColour;
            colours.highlightedColor = isActive ? activeColour : new Color(
                inactiveColour.r - 0.1f, inactiveColour.g - 0.1f, inactiveColour.b - 0.1f);
            btn.colors = colours;
        }
    }
}
