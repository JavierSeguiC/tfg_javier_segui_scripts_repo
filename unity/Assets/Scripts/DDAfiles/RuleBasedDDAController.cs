using System.Collections.Generic;
using UnityEngine;

namespace DDA
{
    /// <summary>
    /// Rule-based dynamic difficulty adjustment — prototype controller / A-B baseline.
    ///
    /// ── ONE SCALAR, NOT TWO AXES (Aug 2026 rework) ──────────────────────────
    /// Previously this tracked noteSpeed and spawnInterval as two independently
    /// stepped and independently clamped state variables. It now tracks a SINGLE
    /// difficulty level d, measured in SUCCESSES: a cleared obstacle adds
    /// successStep (1.0), a failed one subtracts failMultiplier · successStep
    /// (3.0 — a fail undoes exactly three successes). Note speed and spawn interval
    /// are DERIVED from d through DifficultyMapping, never stored.
    ///
    /// Why that matters: with two separate clamps, the note-speed axis and the
    /// spawn-interval axis could hit their limits at different times and the tuned
    /// ratio between them would silently break. With one scalar and a derived
    /// mapping, the ratio is exact by construction, and the "interval saturates,
    /// speed keeps going" behaviour at both ends falls out of a single clamp
    /// inside the mapping. At the defaults:
    ///   d ≤ 12  → interval pinned at 2.00 s, only speed moves (easy end)
    ///   d = 60  → the tuned anchor: 300 wu/s at 1.20 s
    ///   d ≥ 123 → interval pinned at 0.15 s at 615 wu/s, only speed moves (hard end)
    /// so 63 clean successes from the anchor reach 615 wu/s / 0.15 s; ten more give
    /// 665 wu/s at the same 0.15 s; and a fail up there costs speed only.
    ///
    /// The force loop τ_ℓ is unchanged — still its own per-lane stepped axis,
    /// still optional (turn OFF for keyboard play).
    ///
    /// AUTHORITY
    ///   One of THREE possible writers of GameDifficulty, alongside
    ///   PIDifficultyController and DifficultyPresetSwitcher. Implements
    ///   IDifficultyWriter and must hold DifficultyAuthority before it writes.
    ///   Enabling the mode claims authority (silencing the others); being revoked
    ///   switches the mode off, so two controllers can never drive the plant at once.
    ///
    /// CHORDS ARE ONE OBSTACLE
    ///   A chord is scored as a whole, not per note: difficulty moves ONE step
    ///   regardless of how many notes the chord holds. If every member is hit it is a
    ///   single UP step; if ANY member is missed it is a single DOWN step (no partial
    ///   credit). Chord membership is read from the note's NoteInfo (chordId /
    ///   chordSize / IsChord), baked by NoteSpawner — the controller groups the
    ///   per-note conclusive events by chordId and acts once the whole chord has
    ///   resolved. This needs the live note GameObject, which only Stream 2 carries;
    ///   on Stream 1 (no noteObj) each note is scored individually.
    ///
    /// Optional chain escalation makes streak clears raise difficulty more (a cleared
    /// chord counts as one streak clear). Any fail breaks the streak.
    ///
    /// AUDIO is not driven here. NoteAudioFeedback listens to the game note events
    /// directly and decides what to play.
    /// </summary>
    public class RuleBasedDDAController : MonoBehaviour, IDifficultyWriter
    {
        public enum OutcomeSource
        {
            InstantStream2,   // GameEvents.OnNoteStateUpdate — ~one-frame latency; carries noteObj (chord grouping)
            AccurateStream1   // DDAEventBus.OnNoteOutcome — full classification but deferred; per-note only
        }

        [Header("Mode")]
        [Tooltip("Start with the rule active? Can also be toggled live with the on-screen button. " +
                 "Enabling it CLAIMS difficulty authority and silences the PI controller " +
                 "and the preset switcher.")]
        public bool startEnabled = false;

        [Tooltip("Stream 2 is near-instant AND carries the live note GameObject, which is needed to " +
                 "group a chord's notes by chordId and score the chord as one obstacle. Stream 1 is " +
                 "the 'pure' DDA observer but lags by the grace + fusion windows and has no noteObj, " +
                 "so on Stream 1 each note is scored individually. Set before pressing Play.")]
        public OutcomeSource outcomeSource = OutcomeSource.InstantStream2;

        // ------------------------------------------------------------------
        [Header("── Difficulty mapping (d → noteSpeed, spawnInterval) ──")]
        [Tooltip("This controller's OWN copy of the mapping. The PI controller has a " +
                 "separate one — normally set them the same, but they can be tuned " +
                 "independently on purpose.")]
        public DifficultyMapping mapping = new DifficultyMapping();

        [Header("── Rule steps (in difficulty units) ──")]
        [Tooltip("Difficulty level the controller seizes when the mode is enabled. " +
                 "60 = the tuned anchor (300 wu/s at 1.2 s) with the default mapping.")]
        public float initialDifficulty = 60f;

        [Tooltip("Difficulty ADDED per cleared note/chord. This is the definition of the " +
                 "difficulty unit itself, so leave at 1.0 unless you want to rescale the " +
                 "whole axis. At the default mapping 1 unit = +5 wu/s and −0.01667 s.")]
        public float successStep = 1f;

        [Tooltip("A fail moves this many TIMES the success step, in the opposite direction. " +
                 "3.0 means one fail undoes exactly three successes (−15 wu/s, +0.05 s).")]
        public float failMultiplier = 3f;

        [Header("Force thresholds τ_ℓ  (0..1)  — turn OFF for keyboard play")]
        [Tooltip("When OFF, the controller never touches GameDifficulty.requiredForce (leaves the " +
                 "preset default), so keyboard sessions aren't polluted by a force loop that can't " +
                 "be satisfied. Can also be toggled live with the on-screen button.")]
        public bool adjustForceThresholds = false;
        [Tooltip("τ each of the 4 lanes seizes when the force loop is (re)initialised.")]
        public float initialTau = 0.4f;
        [Tooltip("τ ADDED per cleared note/chord (more force required = harder).")]
        public float tauHitStep = 0.02f;
        [Tooltip("τ REMOVED per failed note/chord (less force required = easier).")]
        public float tauFailStep = 0.04f;
        [Tooltip("Lowest τ the controller will drop to.")]
        public float minTau = 0f;
        [Tooltip("Highest τ the controller will climb to.")]
        public float maxTau = 1f;

        [Header("Chain escalation (optional)")]
        [Tooltip("If on, streak clears raise difficulty more per clear. Any fail resets " +
                 "the streak. The multiplier scales the success step on every enabled axis.")]
        public bool escalateChains = false;

        [Tooltip("Extra fraction added to the step per extra chained clear. 0.5 = +50% per consecutive.\n" +
                 "mult = 1 + chainBonus * chainIndex.")]
        public float chainBonus = 0.5f;

        [Tooltip("Cap on the step multiplier so long chains don't explode the difficulty.")]
        public float maxStepMultiplier = 5f;

        [Header("UI")]
        [Tooltip("Draw the built-in IMGUI panel. Turn OFF when using PITuningHUD so the " +
                 "two overlays don't fight for the same corner of the screen.")]
        public bool drawOnScreenPanel = true;

        // ---------------- runtime ----------------
        float curDifficulty;                                   // THE state variable
        readonly float[] curTau = new float[4];
        int chainLen;   // consecutive clears since the last fail
        bool modeOn;
        readonly HashSet<int> _concluded = new HashSet<int>();          // dedup conclusive events per note
        readonly Dictionary<int, ChordTally> _chords = new Dictionary<int, ChordTally>(); // chordId -> progress

        class ChordTally { public int resolved; public int size; public bool anyFailed; }

        // ---------------- public read-outs ----------------
        public float Difficulty        => curDifficulty;
        public float NoteSpeed         => mapping.NoteSpeed(curDifficulty);
        public float SpawnInterval     => mapping.SpawnInterval(curDifficulty);
        // f_s = 1 / spawnInterval. Same definition as PIDifficultyController.SpawnFrequency,
        // added here for read-out parity so both controllers expose the identical
        // (d, v, f_s) surface for the HUD / logging / direct comparison.
        public float SpawnFrequency    => SpawnInterval > 1e-4f ? 1f / SpawnInterval : 0f;
        public bool  IntervalSaturated => mapping.IntervalSaturated(curDifficulty);

        // ---------------- IDifficultyWriter ----------------
        public string AuthorityName => "Rule-based DDA (prototype)";

        public void OnAuthorityGranted()
        {
            modeOn   = true;
            chainLen = 0;
            Apply();
        }

        /// <summary>
        /// Re-seed d (and τ, if the force loop is on) back to their inspector initial
        /// values without leaving authority. DifficultyAuthority.Claim() is a no-op
        /// when this controller already holds authority, so a caller that wants a
        /// clean start on every session (e.g. the menu's Play button) must call this
        /// explicitly rather than re-claiming. Safe to call whether or not this
        /// controller currently holds authority; it only WRITES GameDifficulty if it does.
        /// </summary>
        public void ResetToInitial()
        {
            InitState();
            chainLen = 0;
            _concluded.Clear();
            _chords.Clear();
            if (DifficultyAuthority.HasAuthority(this)) Apply();
        }

        public void OnAuthorityRevoked()
        {
            // Go quiet. Do NOT try to reclaim — that would ping-pong with whoever took over.
            modeOn = false;
        }

        void Awake()
        {
            InitState();
            modeOn = startEnabled;
            DifficultyAuthority.Register(this);
        }

        void OnEnable()
        {
            if (outcomeSource == OutcomeSource.InstantStream2)
                GameEvents.OnNoteStateUpdate += OnNoteState;
            else
                DDAEventBus.OnNoteOutcome += OnNoteOutcome;
        }

        void OnDisable()
        {
            GameEvents.OnNoteStateUpdate -= OnNoteState;   // inactive one is a harmless no-op
            DDAEventBus.OnNoteOutcome -= OnNoteOutcome;
        }

        void OnDestroy() => DifficultyAuthority.Unregister(this);

        void Start()
        {
            // Seize control immediately if we start enabled. Claim() revokes the others.
            if (modeOn) DifficultyAuthority.Claim(this);
        }

        // Seed the runtime state from the inspector initial values (clamped).
        void InitState()
        {
            curDifficulty = mapping.ClampDifficulty(initialDifficulty);
            for (int l = 0; l < curTau.Length; l++)
                curTau[l] = Mathf.Clamp(initialTau, minTau, maxTau);
        }

        void OnValidate() => mapping.Validate();

        // ---------------- Stream 2 (instant): conclusive outcomes, grouped into chords ----------------
        void OnNoteState(NoteStateEvent ev)
        {
            if (!modeOn || !DifficultyAuthority.HasAuthority(this)) return;

            // in-progress frames carry no outcome for the rule (audio handles holds elsewhere)
            if (!ev.succeeded && !ev.failed) return;

            // conclusive frame — one per note
            if (!_concluded.Add(ev.noteId)) return;

            NoteInfo info = ev.noteObj != null ? ev.noteObj.GetComponent<NoteInfo>() : null;

            // Standalone note (or no info): score immediately.
            if (info == null || !info.IsChord)
            {
                if (ev.succeeded) RegisterHit(); else RegisterFail();
                return;
            }

            // Chord member: accumulate, act once the whole chord has resolved.
            if (!_chords.TryGetValue(info.chordId, out var tally))
            {
                tally = new ChordTally { size = Mathf.Max(1, info.chordSize) };
                _chords[info.chordId] = tally;
            }
            tally.resolved++;
            if (ev.failed) tally.anyFailed = true;

            if (tally.resolved >= tally.size)
            {
                _chords.Remove(info.chordId);
                if (tally.anyFailed) RegisterFail();   // any missed note -> whole chord fails
                else                 RegisterHit();    // every note hit   -> whole chord clears
            }
        }

        // ---------------- Stream 1 (accurate, deferred): per-note only (no noteObj to group) ----------------
        void OnNoteOutcome(NoteOutcomeEvent ev)
        {
            if (!modeOn || !DifficultyAuthority.HasAuthority(this)) return;
            if (!_concluded.Add(ev.noteId)) return;

            if (ev.outcome == NoteOutcome.Hit) RegisterHit();
            else RegisterFail();
        }

        // ---------------- rule ----------------
        void RegisterHit()
        {
            int idx = chainLen; // 0-based position of this clear in the streak
            chainLen++;

            float mult = escalateChains
                ? Mathf.Min(1f + chainBonus * idx, maxStepMultiplier)
                : 1f;

            // ONE scalar moves; v and T are derived from it in Apply().
            curDifficulty = mapping.ClampDifficulty(curDifficulty + successStep * mult);

            if (adjustForceThresholds)
                for (int l = 0; l < curTau.Length; l++)
                    curTau[l] = Mathf.Clamp(curTau[l] + tauHitStep * mult, minTau, maxTau);

            Apply();
        }

        void RegisterFail()
        {
            chainLen = 0; // any fail -> break the streak

            curDifficulty = mapping.ClampDifficulty(curDifficulty - successStep * failMultiplier);

            if (adjustForceThresholds)
                for (int l = 0; l < curTau.Length; l++)
                    curTau[l] = Mathf.Clamp(curTau[l] - tauFailStep, minTau, maxTau);

            Apply();
        }

        void Apply()
        {
            // Authority gate: never write unless this controller currently owns difficulty.
            if (!DifficultyAuthority.HasAuthority(this)) return;

            var gd = GameDifficulty.Instance;
            if (gd == null) return;

            // Derived, never stored — the tuned ratio and the "interval saturates,
            // speed keeps moving" behaviour both live inside the mapping.
            gd.noteSpeed     = mapping.NoteSpeed(curDifficulty);
            gd.spawnInterval = mapping.SpawnInterval(curDifficulty);

            // Only touch the force array when the force loop is enabled (keyboard-safe).
            if (adjustForceThresholds && gd.requiredForce != null)
            {
                int n = Mathf.Min(curTau.Length, gd.requiredForce.Length);
                for (int l = 0; l < n; l++) gd.requiredForce[l] = curTau[l];
            }

            // Cosmetic only (background/UI hooks): d normalised across its clamp range.
            gd.generalDifficulty = mapping.Normalised(curDifficulty);
        }

        // ---------------- on-screen UI (IMGUI = zero scene wiring) ----------------
        void OnGUI()
        {
            if (!drawOnScreenPanel) return;

            const int w = 340, pad = 10;
            // Offset to the right so it does not overlap PITuningHUD's panel.
            GUILayout.BeginArea(new Rect(Screen.width - w - pad, pad, w, 245), GUI.skin.box);

            GUILayout.Label("<b>Rule-based DDA (prototype)</b>", RichLabel());
            GUILayout.Label($"Authority: {DifficultyAuthority.CurrentName}", RichLabel());

            bool owns = DifficultyAuthority.HasAuthority(this);
            if (GUILayout.Button(owns ? "Mode: ON  (tap to release)"
                                      : "Mode: OFF (tap to take over)"))
            {
                if (owns) DifficultyAuthority.Release(this);
                else      DifficultyAuthority.Claim(this);
            }

            if (GUILayout.Button(adjustForceThresholds ? "Force τ loop: ON  (tap for keyboard)"
                                                       : "Force τ loop: OFF (keyboard mode)"))
            {
                adjustForceThresholds = !adjustForceThresholds;
                if (modeOn) Apply();   // push/withhold τ immediately
            }

            string pinned = IntervalSaturated
                ? "  <color=#ffcc55>[T pinned — speed only]</color>" : "";
            GUILayout.Label($"d = <b>{curDifficulty:0.0}</b>   →   " +
                            $"{NoteSpeed:0} wu/s   {SpawnInterval:0.000}s{pinned}", RichLabel());

            if (adjustForceThresholds)
                GUILayout.Label($"τ  [{curTau[0]:0.00} {curTau[1]:0.00} {curTau[2]:0.00} {curTau[3]:0.00}]");
            else
                GUILayout.Label("τ  (loop off — keyboard mode)");

            GUILayout.Label($"<size=10>{mapping.Describe()}</size>", RichLabel());

            if (escalateChains)
                GUILayout.Label($"Chain: {chainLen}");

            GUILayout.EndArea();
        }

        static GUIStyle _rich;
        static GUIStyle RichLabel()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true };
            return _rich;
        }
    }
}
